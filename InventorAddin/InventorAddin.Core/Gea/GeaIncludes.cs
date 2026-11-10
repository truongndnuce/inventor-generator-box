using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InventorAddin.Gea;

/// <summary>Which file a line of the expanded text came from (file name, original line number, 1-based).</summary>
public sealed record GeaSourceLine(string File, int Line);

/// <summary>
/// Result of expanding <c>INCLUDE lib:</c>. <see cref="Lines"/> has exactly one element for each line of <see cref="Text"/>.
/// <see cref="Diagnostics"/> already use the coordinates of the original file (no need to map them again).
/// </summary>
public sealed record GeaExpansion(string Text, IReadOnlyList<GeaSourceLine> Lines, IReadOnlyList<string> LibPrefixes, IReadOnlyList<GeaDiagnostic> Diagnostics);

/// <summary>
/// Preprocessing step for <c>INCLUDE lib:&lt;name&gt;</c> (see docs/superpowers/specs/2026-11-10-gea-include-design.md): replaces the
/// INCLUDE line inside <c>BEGIN_FORM</c> or <c>BEGIN_BUILD</c> with the body of the library block of the same kind, before the lexer runs.
/// The library shares variables with the parent script. It works line by line only (it does not understand multi-line <c>/* */</c> block comments).
/// </summary>
public static class GeaIncludes
{
    public const int MaxDepth = 8;

    private static readonly Regex IncludeLine = new(@"^\s*INCLUDE\s+lib:(?<name>[^!]*?)\s*(!.*)?$", RegexOptions.Compiled);
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex PrefixLine = new(@"^(?<lead>\s*PARAM_PREFIX\s+)(?<list>[A-Za-z_][A-Za-z0-9_]*(?:\s+[A-Za-z_][A-Za-z0-9_]*)*)(?<tail>\s*(!.*)?)$", RegexOptions.Compiled);

    private enum Block { None, Form, Build }

    public static GeaExpansion Expand(string text, Func<string, string?>? resolveLib, string fileName = "script")
    {
        var run = new Run(resolveLib);
        var src = Split(text ?? "");
        if (!HasInclude(src))
        {
            // No INCLUDE: return the text unchanged (line break characters included), only build the mapping table.
            for (var i = 0; i < src.Count; i++) run.Lines.Add(new GeaSourceLine(fileName, i + 1));
            return new GeaExpansion(text ?? "", run.Lines, run.Prefixes, run.Diags);
        }
        run.Walk(src, fileName, Block.None, new List<string>(), 0);
        run.DeclareLibPrefixes(fileName);
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
        private readonly HashSet<string> _included = new(StringComparer.Ordinal); // "<name>|<block kind>"

        private string _top = "";

        // An error in the original file keeps its sentence; an error in a library gets the file name so you know where to fix it.
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
                    // Only the original file (and the already trimmed library body) has BEGIN/END; track the current block.
                    var open = OpenBlock(line);
                    if (open != Block.None) block = open;
                    else if (block != Block.None && IsCloser(line, block)) { Emit(line, file, i + 1); block = Block.None; continue; }
                }

                var m = IncludeLine.Match(line);
                if (!m.Success) { Emit(line, file, i + 1); continue; }
                Include(m.Groups["name"].Value, file, i + 1, block, stack, depth);
            }
        }

        /// <summary>
        /// The expanded copy must be valid on its own when compiled again (the job stores this copy): add the prefixes of the libraries to the
        /// PARAM_PREFIX line of the original file (<c>PARAM_PREFIX GT_ TR_</c>). If the original has no PARAM_PREFIX, nothing is needed.
        /// </summary>
        public void DeclareLibPrefixes(string topFile)
        {
            if (Prefixes.Count == 0) return;
            for (var i = 0; i < Out.Count; i++)
            {
                if (Lines[i].File != topFile) continue;
                var m = PrefixLine.Match(Out[i]);
                if (!m.Success) continue;
                var have = new List<string>(m.Groups["list"].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                foreach (var p in Prefixes) if (!have.Contains(p)) have.Add(p);
                Out[i] = m.Groups["lead"].Value + string.Join(" ", have) + m.Groups["tail"].Value;
                return;
            }
        }

        private void Emit(string line, string file, int n)
        {
            Out.Add(line);
            Lines.Add(new GeaSourceLine(file, n));
        }

        private void Include(string name, string file, int line, Block block, List<string> stack, int depth)
        {
            if (block == Block.None) { Error("GEA1010", file, line, "INCLUDE must be inside BEGIN_FORM or BEGIN_BUILD."); return; }
            if (!NamePattern.IsMatch(name)) { Error("GEA1011", file, line, $"Library name '{name}' is not valid (letters, digits, _ and - only, at most 64 characters)."); return; }
            if (depth + 1 > MaxDepth) { Error("GEA1013", file, line, $"INCLUDE is nested more than {MaxDepth} levels deep."); return; }
            if (stack.Contains(name)) { Error("GEA1014", file, line, $"Circular INCLUDE: {string.Join(" -> ", stack)} -> {name}."); return; }
            var key = name + "|" + block;
            if (!_included.Add(key)) { Error("GEA1015", file, line, $"Library '{name}' was already INCLUDEd in this block."); return; }
            var lib = resolve?.Invoke(name);
            if (lib == null) { Error("GEA1012", file, line, resolve == null ? $"There is no library loader to INCLUDE '{name}'." : $"Library '{name}' was not found."); return; }

            var libLines = Split(lib);
            foreach (var l in libLines)
            {
                var pm = PrefixLine.Match(l);
                if (!pm.Success) continue;
                foreach (var p in pm.Groups["list"].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!Prefixes.Contains(p)) Prefixes.Add(p);
            }

            // Cut the body of the block of the same kind, keeping the original line numbers of the library.
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
            // The body is already inside the block `block` of the parent, so nested INCLUDEs use the same kind of block.
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
