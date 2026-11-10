using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InventorAddin.Gea;

/// <summary>Dòng của văn bản đã mở rộng đến từ file nào (tên file, số dòng gốc, tính từ 1).</summary>
public sealed record GeaSourceLine(string File, int Line);

/// <summary>
/// Kết quả mở rộng <c>INCLUDE lib:</c>. <see cref="Lines"/> có đúng một phần tử cho mỗi dòng của <see cref="Text"/>.
/// <see cref="Diagnostics"/> đã dùng tọa độ của file gốc (không cần ánh xạ lại).
/// </summary>
public sealed record GeaExpansion(string Text, IReadOnlyList<GeaSourceLine> Lines, IReadOnlyList<string> LibPrefixes, IReadOnlyList<GeaDiagnostic> Diagnostics);

/// <summary>
/// Bước tiền xử lý cho <c>INCLUDE lib:&lt;tên&gt;</c> (xem docs/superpowers/specs/2026-11-10-gea-include-design.md): thay dòng
/// INCLUDE bên trong <c>BEGIN_FORM</c> hoặc <c>BEGIN_BUILD</c> bằng thân khối cùng loại của thư viện, trước khi lexer chạy.
/// Thư viện dùng chung biến với script cha. Chỉ xử lý theo dòng (không hiểu chú thích khối <c>/* */</c> trải nhiều dòng).
/// </summary>
public static class GeaIncludes
{
    public const int MaxDepth = 8;

    private static readonly Regex IncludeLine = new(@"^\s*INCLUDE\s+lib:(?<name>[^!]*?)\s*(!.*)?$", RegexOptions.Compiled);
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex PrefixLine = new(@"^\s*PARAM_PREFIX\s+(?<p>[A-Za-z_][A-Za-z0-9_]*)\s*(!.*)?$", RegexOptions.Compiled);

    private enum Block { None, Form, Build }

    public static GeaExpansion Expand(string text, Func<string, string?>? resolveLib, string fileName = "script")
    {
        var run = new Run(resolveLib);
        var src = Split(text ?? "");
        if (!HasInclude(src))
        {
            // Không có INCLUDE: trả nguyên văn (kể cả ký tự xuống dòng), chỉ dựng bảng ánh xạ.
            for (var i = 0; i < src.Count; i++) run.Lines.Add(new GeaSourceLine(fileName, i + 1));
            return new GeaExpansion(text ?? "", run.Lines, run.Prefixes, run.Diags);
        }
        run.Walk(src, fileName, Block.None, new List<string>(), 0);
        return new GeaExpansion(string.Join("\n", run.Out), run.Lines, run.Prefixes, run.Diags);
    }

    private static bool HasInclude(List<string> lines)
    {
        foreach (var l in lines)
            if (IncludeLine.IsMatch(l)) return true;
        return false;
    }

    private static List<string> Split(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        return new List<string>(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
    }

    private static Block OpenBlock(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith("BEGIN_FORM", StringComparison.Ordinal) && IsWordEnd(t, 10)) return Block.Form;
        if (t.StartsWith("BEGIN_BUILD", StringComparison.Ordinal) && IsWordEnd(t, 11)) return Block.Build;
        return Block.None;
    }

    private static bool IsCloser(string line, Block kind)
    {
        var t = line.TrimStart();
        var word = kind == Block.Form ? "END_FORM" : "END_BUILD";
        return t.StartsWith(word, StringComparison.Ordinal) && IsWordEnd(t, word.Length);
    }

    private static bool IsWordEnd(string t, int i) => i >= t.Length || !(char.IsLetterOrDigit(t[i]) || t[i] == '_');

    private sealed class Run(Func<string, string?>? resolve)
    {
        public readonly List<string> Out = new();
        public readonly List<GeaSourceLine> Lines = new();
        public readonly List<string> Prefixes = new();
        public readonly List<GeaDiagnostic> Diags = new();
        private readonly HashSet<string> _included = new(StringComparer.Ordinal); // "<tên>|<loại khối>"

        private string _top = "";

        // Lỗi ở file gốc giữ nguyên câu; lỗi trong thư viện thêm tên file để biết sửa ở đâu.
        private void Error(string code, string file, int line, string message) =>
            Diags.Add(new GeaDiagnostic(code, GeaSeverity.Error, line, 1, file == _top ? message : $"{file}: {message}"));

        public void Walk(List<string> src, string file, Block block, List<string> stack, int depth)
        {
            if (depth == 0) _top = file;
            for (var i = 0; i < src.Count; i++)
            {
                var line = src[i];
                if (depth == 0)
                {
                    // Chỉ file gốc (và thân thư viện đã được cắt sẵn) mới có BEGIN/END; theo dõi khối hiện tại.
                    var open = OpenBlock(line);
                    if (open != Block.None) block = open;
                    else if (block != Block.None && IsCloser(line, block)) { Emit(line, file, i + 1); block = Block.None; continue; }
                }

                var m = IncludeLine.Match(line);
                if (!m.Success) { Emit(line, file, i + 1); continue; }
                Include(m.Groups["name"].Value, file, i + 1, block, stack, depth);
            }
        }

        private void Emit(string line, string file, int n)
        {
            Out.Add(line);
            Lines.Add(new GeaSourceLine(file, n));
        }

        private void Include(string name, string file, int line, Block block, List<string> stack, int depth)
        {
            if (block == Block.None) { Error("GEA1010", file, line, "INCLUDE phải nằm trong BEGIN_FORM hoặc BEGIN_BUILD."); return; }
            if (!NamePattern.IsMatch(name)) { Error("GEA1011", file, line, $"Tên thư viện '{name}' không hợp lệ (chỉ chữ, số, _ và -, tối đa 64 ký tự)."); return; }
            if (depth + 1 > MaxDepth) { Error("GEA1013", file, line, $"INCLUDE lồng quá {MaxDepth} tầng."); return; }
            if (stack.Contains(name)) { Error("GEA1014", file, line, $"INCLUDE vòng lặp: {string.Join(" -> ", stack)} -> {name}."); return; }
            var key = name + "|" + block;
            if (!_included.Add(key)) { Error("GEA1015", file, line, $"Thư viện '{name}' đã được INCLUDE trong khối này."); return; }
            var lib = resolve?.Invoke(name);
            if (lib == null) { Error("GEA1012", file, line, resolve == null ? $"Không có nơi nạp thư viện để INCLUDE '{name}'." : $"Không tìm thấy thư viện '{name}'."); return; }

            var libLines = Split(lib);
            foreach (var l in libLines)
            {
                var pm = PrefixLine.Match(l);
                if (pm.Success && !Prefixes.Contains(pm.Groups["p"].Value)) Prefixes.Add(pm.Groups["p"].Value);
            }

            // Cắt thân khối cùng loại, giữ số dòng gốc của thư viện.
            var body = new List<string>();
            var numbers = new List<int>();
            var inside = false;
            for (var i = 0; i < libLines.Count; i++)
            {
                if (!inside) { if (OpenBlock(libLines[i]) == block) inside = true; continue; }
                if (IsCloser(libLines[i], block)) break;
                body.Add(libLines[i]);
                numbers.Add(i + 1);
            }

            var libFile = name + ".gea";
            stack.Add(name);
            // Thân đã nằm trong khối `block` của cha nên INCLUDE lồng nhau dùng cùng loại khối.
            for (var i = 0; i < body.Count; i++)
            {
                var m = IncludeLine.Match(body[i]);
                if (m.Success) Include(m.Groups["name"].Value, libFile, numbers[i], block, stack, depth + 1);
                else Emit(body[i], libFile, numbers[i]);
            }
            stack.RemoveAt(stack.Count - 1);
        }
    }
}
