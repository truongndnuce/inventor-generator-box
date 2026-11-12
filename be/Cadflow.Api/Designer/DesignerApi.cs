using System.Text;
using System.Text.Json;

namespace Cadflow.Api;

public sealed record DesignGenerateRequest(DesignModel? Model);
public sealed record DesignParseRequest(string? Text);
public sealed record DesignDryRunRequest(DesignModel? Model, Dictionary<string, double>? Values, Dictionary<string, string>? Texts);

/// <summary>
/// The logic behind the Designer endpoints (<c>/api/gea/designer/*</c>), kept apart from the HTTP wiring so it can be tested.
/// Problems with the model are data in a 200 answer (the page shows them next to the elements); 400 is only for a body that is not JSON of the expected shape, 413 for a body that is too large.
/// </summary>
public static class DesignerApi
{
    /// <summary>The model cap (256 KB) plus raw build text and values.</summary>
    public const int MaxRequestBytes = 512 * 1024;

    public sealed record Reply(int Status, object Body);

    public static Reply Builders() => new(200, BuilderCatalogDto.List());

    /// <summary>Reads a request body up to <see cref="MaxRequestBytes"/>. Returns null (and the reply to send) when it is too large.</summary>
    public static async Task<(string? Body, Reply? Error)> ReadBody(Stream body, long? contentLength)
    {
        if (contentLength > MaxRequestBytes) return (null, TooLarge());
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxRequestBytes) return (null, TooLarge());
        }
        return (Encoding.UTF8.GetString(ms.ToArray()), null);
    }

    private static Reply TooLarge() => new(413, new { error = $"The request is larger than {MaxRequestBytes / 1024} KB." });

    private static (T? Value, Reply? Error) Read<T>(string body) where T : class
    {
        try
        {
            var v = JsonSerializer.Deserialize<T>(body, DesignJson.Options);
            return v is null ? (null, new Reply(400, new { error = "The request body is empty." })) : (v, null);
        }
        catch (JsonException ex)
        {
            return (null, new Reply(400, new { error = "The request body is not valid: " + ex.Message }));
        }
    }

    public static Reply Generate(string body)
    {
        var (req, err) = Read<DesignGenerateRequest>(body);
        if (err != null) return err;

        var check = DesignDiagnostics.Check(req!.Model);
        var issues = check.Issues.ToList();
        string? gea = null;
        if (check.Output != null)
        {
            gea = DesignBlock.Append(check.Output.Text, req.Model!);
            if (Encoding.UTF8.GetByteCount(gea) > GeaForms.MaxScriptBytes)
            {
                issues.Add(new DesignIssue("", $"The generated file is larger than {GeaForms.MaxScriptBytes / 1024} KB (the limit for a .gea file); shorten the design.", false));
                gea = null;
            }
        }
        return new(200, new { gea, issues, diagnostics = check.Diagnostics, lineMap = check.Output?.LineMap });
    }

    public static Reply Parse(string body)
    {
        var (req, err) = Read<DesignParseRequest>(body);
        if (err != null) return err;
        var text = req!.Text ?? "";
        if (Encoding.UTF8.GetByteCount(text) > GeaForms.MaxScriptBytes)
            return new(413, new { error = $"The file is larger than {GeaForms.MaxScriptBytes / 1024} KB." });

        var r = DesignBlock.ReadBack(text);
        return new(200, new { hasModel = r.HasModel, model = r.Model, bodyChanged = r.BodyChanged, reason = r.Reason });
    }

    public static Reply DryRun(string body)
    {
        var (req, err) = Read<DesignDryRunRequest>(body);
        if (err != null) return err;
        return new(200, DesignRunner.Run(req!.Model, req.Values, req.Texts));
    }
}
