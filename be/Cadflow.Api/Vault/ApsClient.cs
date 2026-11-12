using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cadflow.Api.Vault;

public sealed record ApsStatus(bool Configured, string Status, string? Progress, string? Urn, string? Token, int ExpiresIn, string? Message);

/// <summary>
/// Autodesk Platform Services (Forge) client: uploads a vault file to an OSS bucket, asks Model Derivative to translate it to SVF2
/// and reports the state, plus a read-only viewer token. Stateless: the object key is derived from (item, version), so the
/// translation state is simply what the Model Derivative manifest says. Configure with Aps:ClientId / Aps:ClientSecret.
/// </summary>
public sealed class ApsClient(IConfiguration cfg) : IDisposable
{
    private const string Base = "https://developer.api.autodesk.com";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTime Expires)> _tokens = [];
    private bool _bucketReady;

    private string? ClientId => cfg["Aps:ClientId"];
    private string? ClientSecret => cfg["Aps:ClientSecret"];
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>Bucket keys are global and lower-case; derive one from the client id so it is stable and unique to this app.</summary>
    private string Bucket => cfg["Aps:Bucket"] is { Length: > 0 } b ? b : "smartvault-" + ClientId!.ToLowerInvariant()[..Math.Min(16, ClientId.Length)];

    public static string ObjectKey(string itemId, int version, string name) => $"{itemId}_v{version}{Path.GetExtension(name).ToLowerInvariant()}";

    public static string UrnOf(string bucket, string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"urn:adsk.objects:os.object:{bucket}/{key}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<(string Token, int ExpiresIn)> TokenAsync(string scope, CancellationToken ct)
    {
        if (_tokens.TryGetValue(scope, out var t) && t.Expires > DateTime.UtcNow.AddMinutes(2))
            return (t.Token, (int)(t.Expires - DateTime.UtcNow).TotalSeconds);
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/authentication/v2/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = scope });
        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new VaultException(502, $"Autodesk sign-in failed ({(int)res.StatusCode}): {Short(body)}");
        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        var exp = doc.RootElement.GetProperty("expires_in").GetInt32();
        _tokens[scope] = (token, DateTime.UtcNow.AddSeconds(exp));
        return (token, exp);
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] : s;

    private async Task<HttpResponseMessage> CallAsync(HttpMethod m, string path, string scope, object? json, CancellationToken ct, params (string, string)[] headers)
    {
        var (token, _) = await TokenAsync(scope, ct);
        var req = new HttpRequestMessage(m, Base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        if (json != null) req.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        return await _http.SendAsync(req, ct);
    }

    private const string DataScope = "data:read data:write data:create bucket:create bucket:read";
    private const string ViewScope = "viewables:read";

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady) return;
        using var get = await CallAsync(HttpMethod.Get, $"/oss/v2/buckets/{Bucket}/details", DataScope, null, ct);
        if (get.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            using var create = await CallAsync(HttpMethod.Post, "/oss/v2/buckets", DataScope, new { bucketKey = Bucket, policyKey = "persistent" }, ct);
            if (!create.IsSuccessStatusCode && (int)create.StatusCode != 409)
                throw new VaultException(502, $"Could not create the Autodesk bucket ({(int)create.StatusCode}): {Short(await create.Content.ReadAsStringAsync(ct))}");
        }
        else if (!get.IsSuccessStatusCode)
            throw new VaultException(502, $"Autodesk bucket check failed ({(int)get.StatusCode}): {Short(await get.Content.ReadAsStringAsync(ct))}");
        _bucketReady = true;
    }

    /// <summary>The current state of the translation of this file (never starts one).</summary>
    public async Task<ApsStatus> StatusAsync(string itemId, int version, string name, CancellationToken ct = default)
    {
        if (!IsConfigured) return new ApsStatus(false, "notConfigured", null, null, null, 0, "Autodesk Platform Services is not configured (Aps:ClientId / Aps:ClientSecret).");
        await EnsureBucketAsync(ct);
        var urn = UrnOf(Bucket, ObjectKey(itemId, version, name));
        using var res = await CallAsync(HttpMethod.Get, $"/modelderivative/v2/designdata/{urn}/manifest", DataScope, null, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return new ApsStatus(true, "notStarted", null, urn, null, 0, null);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new VaultException(502, $"Autodesk manifest failed ({(int)res.StatusCode}): {Short(body)}");
        using var doc = JsonDocument.Parse(body);
        var status = doc.RootElement.GetProperty("status").GetString() ?? "pending";
        var progress = doc.RootElement.TryGetProperty("progress", out var p) ? p.GetString() : null;
        string? message = null;
        if (status is "failed" or "timeout")
            message = FirstMessage(doc.RootElement) ?? "Autodesk could not translate this file.";
        string? token = null; var exp = 0;
        if (status == "success") (token, exp) = await TokenAsync(ViewScope, ct);
        return new ApsStatus(true, status, progress, urn, token, exp, message);
    }

    private static string? FirstMessage(JsonElement root)
    {
        try
        {
            foreach (var d in root.GetProperty("derivatives").EnumerateArray())
                if (d.TryGetProperty("messages", out var msgs))
                    foreach (var m in msgs.EnumerateArray())
                        if (m.TryGetProperty("message", out var s) && s.GetString() is { Length: > 0 } text) return text;
        }
        catch { /* no messages */ }
        return null;
    }

    /// <summary>Uploads the local file (signed S3 upload) and starts the translation to SVF2. A no-op when a translation already exists.</summary>
    public async Task<ApsStatus> TranslateAsync(string itemId, int version, string name, string localFile, CancellationToken ct = default)
    {
        var current = await StatusAsync(itemId, version, name, ct);
        if (!current.Configured || current.Status != "notStarted") return current;

        await _lock.WaitAsync(ct);
        try
        {
            var key = ObjectKey(itemId, version, name);
            var path = $"/oss/v2/buckets/{Bucket}/objects/{Uri.EscapeDataString(key)}/signeds3upload";
            using var sign = await CallAsync(HttpMethod.Get, path + "?minutesExpiration=20", DataScope, null, ct);
            var signBody = await sign.Content.ReadAsStringAsync(ct);
            if (!sign.IsSuccessStatusCode) throw new VaultException(502, $"Autodesk upload could not start ({(int)sign.StatusCode}): {Short(signBody)}");
            using var sd = JsonDocument.Parse(signBody);
            var uploadKey = sd.RootElement.GetProperty("uploadKey").GetString()!;
            var url = sd.RootElement.GetProperty("urls")[0].GetString()!;

            await using (var fs = File.OpenRead(localFile))
            {
                using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StreamContent(fs) };
                using var up = await _http.SendAsync(put, ct);
                if (!up.IsSuccessStatusCode) throw new VaultException(502, $"Autodesk upload failed ({(int)up.StatusCode}).");
            }

            using var done = await CallAsync(HttpMethod.Post, path, DataScope, new { uploadKey }, ct);
            if (!done.IsSuccessStatusCode) throw new VaultException(502, $"Autodesk upload could not be completed ({(int)done.StatusCode}): {Short(await done.Content.ReadAsStringAsync(ct))}");

            var job = new { input = new { urn = current.Urn }, output = new { formats = new[] { new { type = "svf2", views = new[] { "2d", "3d" } } } } };
            using var tr = await CallAsync(HttpMethod.Post, "/modelderivative/v2/designdata/job", DataScope, job, ct, ("x-ads-force", "false"));
            if (!tr.IsSuccessStatusCode) throw new VaultException(502, $"Autodesk translation could not start ({(int)tr.StatusCode}): {Short(await tr.Content.ReadAsStringAsync(ct))}");
        }
        finally { _lock.Release(); }
        return current with { Status = "pending", Progress = "0% complete" };
    }

    public void Dispose() { _http.Dispose(); _lock.Dispose(); }
}
