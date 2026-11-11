using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cadflow.Api;

/// <summary>
/// Content-addressed store of .gea scripts: <c>data\scripts\&lt;sha256&gt;.gea</c>. A job keeps only the hash, so many jobs share one copy
/// without growing jobs.json, and the exact script that ran can still be exported even if the original script changes later.
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

    /// <summary>Script that ships with the server (Scripts folder next to the executable), or null if missing.</summary>
    public static string? ReadBundled(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Scripts", Path.GetFileName(file));
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }
}

/// <summary>
/// Format of the .gea file for two-way export/import (see sa-docs\gea-language-spec.md, section "Values that travel with CADFlow").
/// The file is still valid .gea: the added part is only comment lines starting with <c>!@</c>, so the interpreter and the add-in ignore it.
/// <code>
/// !@CADFLOW 1
/// !@JOB AJ-20261108-111053-402e
/// !@PRODUCT transformer 1
/// !@SCRIPT_SHA256 &lt;sha of the script without this block&gt;
/// !@VALUE TR_TankLength 2000
/// </code>
/// Values use the internal unit of the form: LENGTH in mm, ANGLE in degrees, counts as integers.
/// </summary>
public static class GeaExport
{
    public const string Tag = "!@";

    // Unicode characters are not escaped (non-ASCII text stays readable); double quotes, line breaks and control characters are still escaped.
    private static readonly JsonSerializerOptions TextJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record Block(string? Job, string? Product, int? ProductVersion, string? ScriptSha256,
        IReadOnlyDictionary<string, double> Values, IReadOnlyDictionary<string, string> Texts);

    /// <summary>Original script + the value block of the job. An old block (if the script was exported/imported before) is replaced by the new one.</summary>
    public static string Build(Job job, string scriptText)
    {
        var script = StripBlock(scriptText); // LF line endings, exactly one newline at the end of the file
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
        // Text values (text/radio fields) are written as JSON strings so spaces, quotes and Unicode characters stay on one line.
        foreach (var (name, text) in job.TextInputs.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(Tag).Append("TEXT ").Append(name).Append(' ').Append(JsonSerializer.Serialize(text, TextJson)).Append(nl);
        return sb.ToString();
    }

    private const string BlockHeader = "! CADFlow export:";

    /// <summary>
    /// Original script without the export block: LF line endings, exactly one newline at the end. The export block is always at the end of the file
    /// (from the dashed line before <c>! CADFlow export:</c> to the end), and any stray <c>!@</c> lines are removed as well (except the three lines of the Designer block, which start in column 0).
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
        // The Designer keeps its model in lines of the same kind (!@DESIGNER, !@BODY_SHA256, !@MODEL, in column 0); they are part of the script, not stray lines.
        lines.RemoveAll(l => l.TrimStart().StartsWith(Tag, StringComparison.Ordinal) && !IsDesignerLine(l));
        return string.Join("\n", lines).TrimEnd() + "\n";
    }

    private static bool IsDesignerLine(string line) =>
        line.StartsWith("!@DESIGNER", StringComparison.Ordinal) || line.StartsWith("!@BODY_SHA256", StringComparison.Ordinal) || line.StartsWith("!@MODEL", StringComparison.Ordinal);

    /// <summary>Reads the <c>!@</c> block from a .gea file; returns null if the file has no block (a plain .gea file that never went through CADFlow).</summary>
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
                    // The value is the rest of the line after the parameter name: a JSON string (it may contain spaces).
                    var afterName = line[(Tag.Length + "TEXT".Length)..].TrimStart()[parts[1].Length..].Trim();
                    try { if (JsonSerializer.Deserialize<string>(afterName) is { } s) texts[parts[1]] = s; }
                    catch (JsonException) { /* broken line: skip it, import will use the default value */ }
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
