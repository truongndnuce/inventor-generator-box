using System.Text;
using System.Text.RegularExpressions;

namespace Cadflow.Api;

/// <summary>What <see cref="DesignBlock.ReadBack"/> found in a .gea file.</summary>
/// <param name="Model">The design stored in the file, or null when the file has no usable Designer block (then <see cref="Reason"/> says why).</param>
/// <param name="BodyChanged">True when the script text no longer matches the one the Designer generated (it was edited by hand after the Designer wrote it).
/// The model is still returned: the caller decides whether to open the model (dropping the hand edits) or to open the file as text.</param>
public sealed record DesignReadBack(DesignModel? Model, bool BodyChanged, string? Reason)
{
    public bool HasModel => Model != null;
}

/// <summary>
/// The <c>!@DESIGNER</c> block: the design model stored at the end of a generated .gea file, as comment lines, so the Designer can open the file again.
/// Ordinary .gea tools ignore it (language spec section 21), like the <c>!@</c> value block that CADFlow adds when it exports a job (section 19).
/// <code>
/// ! ---------------------------------------------------------------------------
/// ! CADFlow Designer: the model below lets the Designer open this file again.
/// ! Do not edit the lines starting with !@ by hand; edit the file in the Designer.
/// !@DESIGNER 1
/// !@BODY_SHA256 &lt;sha-256 of the script above the block, normalized&gt;
/// !@MODEL &lt;the model as JSON, UTF-8, base64url without padding, on one line&gt;
/// </code>
/// Only lines that start in column 0 count, and generated build text is always indented, so text typed in the Build tab cannot pose as the block.
/// If a file somehow has several blocks, the last one is used.
/// </summary>
public static partial class DesignBlock
{
    public const int Version = 1;

    private const string Dashes = "! ---------------------------------------------------------------------------";
    private const string Header = "! CADFlow Designer: the model below lets the Designer open this file again.";
    private const string Warning = "! Do not edit the lines starting with !@ by hand; edit the file in the Designer.";
    private const string TagDesigner = "!@DESIGNER";
    private const string TagSha = "!@BODY_SHA256";
    private const string TagModel = "!@MODEL";
    private const string ExportHeader = "! CADFlow export:";       // GeaExport.BlockHeader

    /// <summary>Base64url of the largest model (<see cref="DesignValidator.MaxModelBytes"/>) plus a little room.</summary>
    private static readonly int MaxPayloadChars = (DesignValidator.MaxModelBytes * 4 / 3) + 8;

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex Base64UrlChars();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();

    // ------------------------------------------------------------------ write

    /// <summary>The generated <paramref name="body"/> followed by the block that holds <paramref name="model"/>. A block already in <paramref name="body"/> is replaced.</summary>
    public static string Append(string body, DesignModel model)
    {
        var clean = Strip(body);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(DesignJson.Serialize(model)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var sb = new StringBuilder(clean);
        sb.Append('\n');
        sb.Append(Dashes).Append('\n');
        sb.Append(Header).Append('\n');
        sb.Append(Warning).Append('\n');
        sb.Append(TagDesigner).Append(' ').Append(Version).Append('\n');
        sb.Append(TagSha).Append(' ').Append(Sha(clean)).Append('\n');
        sb.Append(TagModel).Append(' ').Append(payload).Append('\n');
        return sb.ToString();
    }

    /// <summary>The script without the Designer block (and without anything after it, such as a CADFlow export block): LF line endings, exactly one newline at the end.</summary>
    public static string Strip(string text)
    {
        var lines = Lines(text);
        var marker = FindMarker(lines);
        return Normalize(string.Join("\n", marker < 0 ? lines : lines.Take(BlockStart(lines, marker))));
    }

    // ------------------------------------------------------------------ read

    public static DesignReadBack ReadBack(string? text)
    {
        var lines = Lines(text ?? "");
        int marker = FindMarker(lines);
        if (marker < 0)
            return new(null, false, "This file was not made with the Designer (it has no Designer block), so it can only be edited as text.");

        var version = lines[marker].Length > TagDesigner.Length ? lines[marker][TagDesigner.Length..].Trim() : "";
        if (version != Version.ToString())
            return new(null, false, $"The Designer block has version \"{version}\"; this Designer reads version {Version}.");

        // the !@ lines that follow the marker directly
        string? sha = null, payload = null;
        int next = marker + 1;
        for (; next < lines.Count && lines[next].StartsWith("!@", StringComparison.Ordinal); next++)
        {
            var (name, value) = Split(lines[next]);
            if (name == TagSha) sha ??= value;
            else if (name == TagModel) payload ??= value;
        }

        if (string.IsNullOrEmpty(payload))
            return new(null, false, "The Designer block has no model line.");
        if (payload.Length > MaxPayloadChars)
            return new(null, false, $"The model in the Designer block is larger than {DesignValidator.MaxModelBytes / 1024} KB.");

        var model = Decode(payload, out var reason);
        if (model is null) return new(null, false, reason);

        return new(model, BodyChanged(lines, marker, next, sha), null);
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>True when the text above the block no longer has the hash recorded in it, or when something other than blank lines (and a CADFlow export block) follows the block.</summary>
    private static bool BodyChanged(List<string> lines, int marker, int afterBlock, string? recordedSha)
    {
        if (recordedSha is null || !Sha256Hex().IsMatch(recordedSha)) return true;

        var body = Normalize(string.Join("\n", lines.Take(BlockStart(lines, marker))));
        if (!string.Equals(Sha(body), recordedSha, StringComparison.OrdinalIgnoreCase)) return true;

        // what follows the block: blank lines are fine; a CADFlow export block is fine (an import drops it); anything else is a statement added by hand
        int end = lines.Count;
        int export = lines.FindIndex(afterBlock, l => l.StartsWith(ExportHeader, StringComparison.Ordinal));
        if (export >= 0) end = export > afterBlock && lines[export - 1].StartsWith("! ----", StringComparison.Ordinal) ? export - 1 : export;
        for (int i = afterBlock; i < end; i++)
            if (lines[i].Trim().Length > 0) return true;
        return false;
    }

    private static DesignModel? Decode(string payload, out string? reason)
    {
        reason = null;
        if (!Base64UrlChars().IsMatch(payload))
        {
            reason = "The model in the Designer block is damaged (it is not base64).";
            return null;
        }

        byte[] bytes;
        try
        {
            var b64 = payload.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            bytes = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            reason = "The model in the Designer block is damaged (it is not base64).";
            return null;
        }

        if (bytes.Length > DesignValidator.MaxModelBytes)
        {
            reason = $"The model in the Designer block is larger than {DesignValidator.MaxModelBytes / 1024} KB.";
            return null;
        }

        string json;
        try { json = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            reason = "The model in the Designer block is damaged (it is not UTF-8 text).";
            return null;
        }

        var model = DesignJson.Deserialize(json);
        if (model is null) reason = "The model in the Designer block is not a design.";
        return model;
    }

    /// <summary>Index of the last line (starting in column 0) that is the <c>!@DESIGNER</c> marker, or -1.</summary>
    private static int FindMarker(List<string> lines) =>
        lines.FindLastIndex(l => l == TagDesigner || l.StartsWith(TagDesigner + " ", StringComparison.Ordinal));

    /// <summary>First line of the block: the dashed line above the header comment, or the marker itself if the comment lines were removed.</summary>
    private static int BlockStart(List<string> lines, int marker)
    {
        int h = lines.FindLastIndex(marker, l => l.StartsWith("! CADFlow Designer:", StringComparison.Ordinal));
        if (h < 0) return marker;
        for (int i = h; i < marker; i++)
            if (!lines[i].StartsWith('!')) return marker;          // something else sits between the header and the marker: not our header
        return h > 0 && lines[h - 1].StartsWith("! ----", StringComparison.Ordinal) ? h - 1 : h;
    }

    private static (string Name, string Value) Split(string line)
    {
        int sp = line.IndexOf(' ');
        return sp < 0 ? (line.TrimEnd(), "") : (line[..sp], line[(sp + 1)..].Trim());
    }

    private static List<string> Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    /// <summary>LF line endings, trailing spaces and blank lines at the end removed, exactly one newline at the end (the same normalization as the export hash in language spec section 19).</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() + "\n";

    private static string Sha(string normalizedBody) => ScriptStore.Sha256(normalizedBody);
}
