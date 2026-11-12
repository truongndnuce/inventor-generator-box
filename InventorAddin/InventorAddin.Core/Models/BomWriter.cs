using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace InventorAddin.Models;

/// <summary>What one BOM line says about a part. Plain data (no Inventor types) so it can be tested without Inventor.</summary>
public sealed record BomLine(
    string PartNumber,
    string Description,
    string FileName,
    string Material,
    int Quantity,
    double? MassKg,
    double? VolumeCm3,
    double? SizeXmm,
    double? SizeYmm,
    double? SizeZmm);

/// <summary>
/// Writes the BOM of a job as CSV. Depends only on System, so the server tests compile it too (see Cadflow.Api.csproj).
/// Comma separated, quotes doubled, numbers with the invariant culture, a leading UTF-8 byte order mark added by the caller's encoding.
/// </summary>
public static class BomWriter
{
    private static readonly string[] FixedColumns =
        { "Item", "PartNumber", "Description", "FileName", "Material", "Quantity", "MassKg", "VolumeCm3", "SizeX_mm", "SizeY_mm", "SizeZ_mm" };

    /// <summary>The CSV text: a header, one row per line, then the job inputs as extra columns (same on every row).</summary>
    public static string ToCsv(IReadOnlyList<BomLine> lines, IReadOnlyDictionary<string, string> inputs)
    {
        var inputNames = inputs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        Row(sb, FixedColumns.Concat(inputNames));
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var cells = new List<string>
            {
                (i + 1).ToString(CultureInfo.InvariantCulture),
                l.PartNumber, l.Description, l.FileName, l.Material,
                l.Quantity.ToString(CultureInfo.InvariantCulture),
                Num(l.MassKg, 4), Num(l.VolumeCm3, 3), Num(l.SizeXmm, 2), Num(l.SizeYmm, 2), Num(l.SizeZmm, 2),
            };
            cells.AddRange(inputNames.Select(n => inputs[n]));
            Row(sb, cells);
        }
        return sb.ToString();
    }

    /// <summary>A number as text with the invariant culture (a comma decimal separator would break the CSV); empty when unknown.</summary>
    public static string Num(double? v, int digits) =>
        v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ? "" : Math.Round(v.Value, digits).ToString("0.#########", CultureInfo.InvariantCulture);

    private static void Row(StringBuilder sb, IEnumerable<string> cells)
    {
        sb.Append(string.Join(",", cells.Select(Escape))).Append("\r\n");
    }

    /// <summary>Quotes a cell that has a comma, quote or line break. A cell that starts with = + @ gets a leading apostrophe so Excel does not run it as a formula.</summary>
    public static string Escape(string? s)
    {
        s ??= "";
        if (s.Length > 0 && "=+@".IndexOf(s[0]) >= 0) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
