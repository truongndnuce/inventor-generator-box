using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace InventorAddin.Gea;

/// <summary>
/// Đọc khối giá trị <c>!@</c> mà web (CAD Automation) ghi cuối file .gea khi Export: dòng <c>!@VALUE tên số</c> (số theo đơn vị nội bộ, LENGTH = mm)
/// và <c>!@TEXT tên "chuỗi JSON"</c>. Với trình thông dịch đó chỉ là chú thích. Định dạng khớp Cadflow.Api\GeaExport.cs.
/// </summary>
public static class GeaExportBlock
{
    private const string Tag = "!@";

    public sealed record Block(IReadOnlyDictionary<string, double> Values, IReadOnlyDictionary<string, string> Texts);

    /// <summary>Trả null nếu file không có khối (file .gea thường).</summary>
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
                    catch (JsonException) { /* dòng hỏng: giữ giá trị mặc định */ }
                    break;
                }
            }
        }
        return found || values.Count > 0 || texts.Count > 0 ? new Block(values, texts) : null;
    }
}
