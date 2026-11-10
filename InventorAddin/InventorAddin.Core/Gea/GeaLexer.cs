using System.Collections.Generic;
using System.Text;

namespace InventorAddin.Gea;

public enum TokKind { Word, Number, String, Symbol }

public readonly record struct GeaToken(TokKind Kind, string Text, int Line, int Col)
{
    public bool Is(string text) => (Kind == TokKind.Word || Kind == TokKind.Symbol) && Text == text;

    public override string ToString() => Kind == TokKind.String ? "\"" + Text + "\"" : Text;
}

/// <summary>One logical statement line (lines ending in <c>\</c> were joined, comments removed).</summary>
public sealed record GeaLine(int Line, IReadOnlyList<GeaToken> Tokens);

/// <summary>
/// Line-based lexer (section 2 of the spec): <c>!</c> and <c>/* */</c> comments, line joining with <c>\</c>,
/// strings with escapes, numbers, identifiers, operators. Records the line and column (1-based) of each token.
/// </summary>
public static class GeaLexer
{
    public static List<GeaLine> Tokenize(string text, List<GeaDiagnostic> diagnostics)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        var physical = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var result = new List<GeaLine>();
        bool inBlockComment = false;
        List<GeaToken>? pending = null; // the logical line being joined
        int pendingLine = 0;

        for (int li = 0; li < physical.Length; li++)
        {
            int lineNo = li + 1;
            var tokens = new List<GeaToken>();
            bool continued = LexLine(physical[li], lineNo, tokens, ref inBlockComment, diagnostics);

            if (pending != null)
            {
                pending.AddRange(tokens);
                tokens = pending;
            }
            else pendingLine = lineNo;

            if (continued)
            {
                pending = tokens;
                continue;
            }
            pending = null;
            if (tokens.Count > 0) result.Add(new GeaLine(pendingLine, tokens));
        }
        if (pending is { Count: > 0 }) result.Add(new GeaLine(pendingLine, pending));
        if (inBlockComment)
            diagnostics.Add(new GeaDiagnostic("GEA1004", GeaSeverity.Error, physical.Length, 1, "The /* comment is not closed by */."));
        return result;
    }

    /// <summary>Splits one physical line. Returns true if the line ends in <c>\</c> (joined with the next line).</summary>
    private static bool LexLine(string s, int line, List<GeaToken> tokens, ref bool inBlockComment, List<GeaDiagnostic> diags)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (inBlockComment)
            {
                int end = s.IndexOf("*/", i, System.StringComparison.Ordinal);
                if (end < 0) return false;
                inBlockComment = false;
                i = end + 2;
                continue;
            }

            char c = s[i];
            int col = i + 1;
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '!') return false; // a comment runs to the end of the line
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                inBlockComment = true;
                i += 2;
                continue;
            }
            if (c == '\\')
            {
                // Line joining: valid only when nothing but whitespace or a comment follows
                var rest = s.Substring(i + 1).TrimStart();
                if (rest.Length == 0 || rest[0] == '!') return true;
                diags.Add(new GeaDiagnostic("GEA1001", GeaSeverity.Error, line, col, "The character '\\' is only for joining lines at the end of a line."));
                i++;
                continue;
            }

            if (c == '"')
            {
                var sb = new StringBuilder();
                int j = i + 1;
                bool closed = false;
                while (j < s.Length)
                {
                    char ch = s[j];
                    if (ch == '"') { closed = true; j++; break; }
                    if (ch == '\\' && j + 1 < s.Length)
                    {
                        char e = s[j + 1];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', '"' => '"', '\\' => '\\', _ => e });
                        j += 2;
                        continue;
                    }
                    sb.Append(ch);
                    j++;
                }
                if (!closed)
                    diags.Add(new GeaDiagnostic("GEA1004", GeaSeverity.Error, line, col, "The string is not closed by a \" character."));
                tokens.Add(new GeaToken(TokKind.String, sb.ToString(), line, col));
                i = j;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j < s.Length && s[j] == '.' && j + 1 < s.Length && char.IsDigit(s[j + 1]))
                {
                    j++;
                    while (j < s.Length && char.IsDigit(s[j])) j++;
                }
                // Exponent: 1e-3
                if (j < s.Length && (s[j] == 'e' || s[j] == 'E'))
                {
                    int k = j + 1;
                    if (k < s.Length && (s[k] == '+' || s[k] == '-')) k++;
                    if (k < s.Length && char.IsDigit(s[k]))
                    {
                        while (k < s.Length && char.IsDigit(s[k])) k++;
                        j = k;
                    }
                }
                tokens.Add(new GeaToken(TokKind.Number, s.Substring(i, j - i), line, col));
                i = j; // a unit written right after the number (100mm) is split into a separate Word on the next round
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                tokens.Add(new GeaToken(TokKind.Word, s.Substring(i, j - i), line, col));
                i = j;
                continue;
            }

            string? sym = null;
            if (i + 1 < s.Length)
            {
                var two = s.Substring(i, 2);
                if (two is "==" or "<>" or "<=" or ">=") sym = two;
                else if (two is "++" or "--")
                {
                    // i++ / i-- is a statement of its own: nothing follows it (except a comment)
                    var rest = s.Substring(i + 2).TrimStart();
                    if (rest.Length == 0 || rest[0] == '!') sym = two;
                }
            }
            if (sym == null && "+-*/%<>=(),.:".IndexOf(c) >= 0) sym = c.ToString();
            if (sym == null)
            {
                diags.Add(new GeaDiagnostic("GEA1001", GeaSeverity.Error, line, col, $"Invalid character: '{c}'."));
                i++;
                continue;
            }
            tokens.Add(new GeaToken(TokKind.Symbol, sym, line, col));
            i += sym.Length;
        }
        return false;
    }
}
