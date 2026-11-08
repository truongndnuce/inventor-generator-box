using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cadflow.Api;

/// <summary>
/// Kho script .gea theo nội dung: <c>data\scripts\&lt;sha256&gt;.gea</c>. Job chỉ giữ mã băm, nên nhiều job dùng chung một bản
/// mà jobs.json không phình, và vẫn export đúng bản đã chạy dù script gốc đổi sau đó.
/// </summary>
public sealed class ScriptStore
{
    private readonly string _dir;

    public ScriptStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "scripts");
        Directory.CreateDirectory(_dir);
    }

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public string Save(string text)
    {
        var sha = Sha256(text);
        var path = PathFor(sha);
        if (!File.Exists(path)) File.WriteAllText(path, text, new UTF8Encoding(false));
        return sha;
    }

    public string? Get(string? sha) =>
        sha is { Length: 64 } && sha.All(Uri.IsHexDigit) && File.Exists(PathFor(sha)) ? File.ReadAllText(PathFor(sha), Encoding.UTF8) : null;

    private string PathFor(string sha) => Path.Combine(_dir, sha + ".gea");

    /// <summary>Script đi kèm server (thư mục Scripts cạnh file chạy), hoặc null nếu thiếu.</summary>
    public static string? ReadBundled(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Scripts", Path.GetFileName(file));
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }
}

/// <summary>
/// Định dạng file .gea khi export/import hai chiều (xem sa-docs\gea-language-spec.md, mục "Giá trị đi kèm của CADFlow").
/// File vẫn là .gea hợp lệ: phần thêm vào chỉ là dòng chú thích bắt đầu bằng <c>!@</c>, nên trình thông dịch và add-in bỏ qua.
/// <code>
/// !@CADFLOW 1
/// !@JOB AJ-20261108-111053-402e
/// !@PRODUCT transformer 1
/// !@SCRIPT_SHA256 &lt;sha của script không có khối này&gt;
/// !@VALUE TR_TankLength 2000
/// </code>
/// Giá trị theo đơn vị nội bộ của form: LENGTH tính bằng mm, ANGLE bằng độ, số lượng là số nguyên.
/// </summary>
public static class GeaExport
{
    public const string Tag = "!@";

    // Không escape ký tự Unicode (tiếng Việt giữ nguyên dạng đọc được); vẫn escape nháy kép, xuống dòng và ký tự điều khiển.
    private static readonly JsonSerializerOptions TextJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record Block(string? Job, string? Product, int? ProductVersion, string? ScriptSha256,
        IReadOnlyDictionary<string, double> Values, IReadOnlyDictionary<string, string> Texts);

    /// <summary>Script gốc + khối giá trị của job. Khối cũ (nếu script từng được export/import) bị thay bằng khối mới.</summary>
    public static string Build(Job job, string scriptText)
    {
        var script = StripBlock(scriptText); // LF, cuối file có đúng một dấu xuống dòng
        var nl = scriptText.Contains("\r\n") ? "\r\n" : "\n";
        var sb = new StringBuilder(script.TrimEnd().Replace("\n", nl));
        sb.Append(nl).Append(nl);
        sb.Append("! ---------------------------------------------------------------------------").Append(nl);
        sb.Append(BlockHeader).Append(" job ").Append(job.Id).Append(", product ").Append(job.ProductCode).Append(nl);
        sb.Append("! Ordinary .gea tools ignore the lines below; CADFlow reads them back on import.").Append(nl);
        sb.Append(Tag).Append("CADFLOW 1").Append(nl);
        sb.Append(Tag).Append("JOB ").Append(job.Id).Append(nl);
        sb.Append(Tag).Append("PRODUCT ").Append(job.ProductCode).Append(' ').Append(job.ProductVersion.ToString(CultureInfo.InvariantCulture)).Append(nl);
        sb.Append(Tag).Append("SCRIPT_SHA256 ").Append(ScriptStore.Sha256(script)).Append(nl);
        foreach (var (name, value) in job.Inputs.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(Tag).Append("VALUE ").Append(name).Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(nl);
        // Giá trị chữ (ô text/radio) viết dạng chuỗi JSON để giữ được khoảng trắng, dấu nháy và ký tự Unicode trên một dòng.
        foreach (var (name, text) in job.TextInputs.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(Tag).Append("TEXT ").Append(name).Append(' ').Append(JsonSerializer.Serialize(text, TextJson)).Append(nl);
        return sb.ToString();
    }

    private const string BlockHeader = "! CADFlow export:";

    /// <summary>
    /// Script gốc, không có khối export: xuống dòng LF, đúng một dấu xuống dòng ở cuối. Khối export luôn nằm cuối file
    /// (từ dòng gạch ngang trước <c>! CADFlow export:</c> đến hết), và mọi dòng <c>!@</c> lẻ cũng bị bỏ.
    /// </summary>
    public static string StripBlock(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int cut = lines.FindIndex(l => l.StartsWith(BlockHeader, StringComparison.Ordinal));
        if (cut >= 0)
        {
            if (cut > 0 && lines[cut - 1].StartsWith("! ----", StringComparison.Ordinal)) cut--;
            lines.RemoveRange(cut, lines.Count - cut);
        }
        lines.RemoveAll(l => l.TrimStart().StartsWith(Tag, StringComparison.Ordinal));
        return string.Join("\n", lines).TrimEnd() + "\n";
    }

    /// <summary>Đọc khối <c>!@</c> từ file .gea; trả null nếu file không có khối (file .gea thường, chưa từng qua CADFlow).</summary>
    public static Block? ParseBlock(string text)
    {
        string? job = null, product = null, sha = null;
        int? version = null;
        bool found = false;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(Tag, StringComparison.Ordinal)) continue;
            var parts = line[Tag.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            switch (parts[0])
            {
                case "TEXT" when parts.Length >= 3:
                {
                    // Giá trị là phần còn lại của dòng sau tên thông số: một chuỗi JSON (có thể chứa khoảng trắng).
                    var afterName = line[(Tag.Length + "TEXT".Length)..].TrimStart()[parts[1].Length..].Trim();
                    try { if (JsonSerializer.Deserialize<string>(afterName) is { } s) texts[parts[1]] = s; }
                    catch (JsonException) { /* dòng hỏng: bỏ qua, import sẽ dùng giá trị mặc định */ }
                    break;
                }
                case "CADFLOW": found = true; break;
                case "JOB" when parts.Length >= 2: job = parts[1]; break;
                case "PRODUCT" when parts.Length >= 2:
                    product = parts[1];
                    if (parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) version = v;
                    break;
                case "SCRIPT_SHA256" when parts.Length >= 2: sha = parts[1]; break;
                case "VALUE" when parts.Length >= 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d):
                    values[parts[1]] = d;
                    break;
            }
        }
        return found ? new Block(job, product, version, sha, values, texts) : null;
    }
}
