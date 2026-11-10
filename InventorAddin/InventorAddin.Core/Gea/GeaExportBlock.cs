using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace InventorAddin.Gea;

/// <summary>
/// Reads the <c>!@</c> value block that the web (CAD Automation) writes at the end of a .gea file on Export: lines <c>!@VALUE name number</c> (number in the internal unit, LENGTH = mm)
/// and <c>!@TEXT name "JSON string"</c>. For the interpreter these are only comments. The format matches Cadflow.Api\GeaExport.cs.
/// </summary>
public static class GeaExportBlock
{
    private const string Tag = "!@";

    public sealed record Block(IReadOnlyDictionary<string, double> Values, IReadOnlyDictionary<string, string> Texts);

    /// <summary>Returns null if the file has no block (a plain .gea file).</summary>
    public static Block? Parse(string text)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        bool found = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(Tag, StringComparison.Ordinal)) continue;
            var parts = line[Tag.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            switch (parts[0])
            {
                case "CADFLOW": found = true; break;
                case "VALUE" when parts.Length >= 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d):
                    values[parts[1]] = d;
                    break;
                case "TEXT" when parts.Length >= 3:
                {
                    var afterName = line[(Tag.Length + "TEXT".Length)..].TrimStart()[parts[1].Length..].Trim();
                    try { if (JsonSerializer.Deserialize<string>(afterName) is { } s) texts[parts[1]] = s; }
                    catch (JsonException) { /* broken line: keep the default value */ }
                    break;
                }
            }
        }
        return found || values.Count > 0 || texts.Count > 0 ? new Block(values, texts) : null;
    }
}
