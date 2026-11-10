using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InventorAddin.Web;

/// <summary>A job the server hands down to the worker: only what is needed to build.</summary>
public sealed record WorkItem(string Id, string ProductCode, int ProductVersion, Dictionary<string, double> Inputs, string? ScriptText = null, string? ScriptName = null, Dictionary<string, string>? TextInputs = null);

/// <summary>Calls the worker API of the web server (see be/Cadflow.Api). The worker only connects outward, it opens no port.</summary>
public sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;
    // A separate HttpClient for R2: do not carry X-Worker-Key to another server.
    private readonly HttpClient _r2Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _worker;
    private readonly string[]? _products;

    /// <param name="products">Product codes this worker accepts; null = accepts every product.</param>
    public ServerClient(WorkerSettings settings, string[]? products = null)
    {
        _worker = settings.WorkerName;
        _products = products;
        _http = new HttpClient { BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Add("X-Worker-Key", settings.WorkerKey);
    }

    public async Task HeartbeatAsync(string? inventorVersion, string addinVersion, bool busy, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/worker/heartbeat",
            new { worker = _worker, inventorVersion, addinVersion, busy }, ct);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Takes the next job, null if the queue is empty.</summary>
    public async Task<WorkItem?> ClaimAsync(CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/worker/claim", new { worker = _worker, products = _products }, ct);
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<WorkItem>(cancellationToken: ct);
    }

    public Task<bool> ProgressAsync(string jobId, string step, string? message, CancellationToken ct) =>
        PostAsync($"api/worker/jobs/{jobId}/progress?worker={Uri.EscapeDataString(_worker)}", new { step, message }, ct);

    public async Task UploadAsync(string jobId, string filePath, string kind, CancellationToken ct)
    {
        var name = Path.GetFileName(filePath);

        // If the server has R2: upload straight to R2 with a presigned URL, then report the commit. An old server (404) or R2 not configured ("direct"): upload through the server.
        using (var pre = await _http.PostAsync(
            $"api/worker/jobs/{jobId}/artifacts/presign?worker={Uri.EscapeDataString(_worker)}&name={Uri.EscapeDataString(name)}", null, ct))
        {
            if (pre.StatusCode != HttpStatusCode.NotFound)
            {
                pre.EnsureSuccessStatusCode();
                using var doc = System.Text.Json.JsonDocument.Parse(await pre.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.GetProperty("mode").GetString() == "r2")
                {
                    var url = doc.RootElement.GetProperty("url").GetString()!;
                    await using var src = File.OpenRead(filePath);
                    using var put = new StreamContent(src);
                    // No header besides the file body: the URL signature does not bind Content-Type.
                    using var putRes = await _r2Http.PutAsync(url, put, ct);
                    putRes.EnsureSuccessStatusCode();
                    using var commit = await _http.PostAsync(
                        $"api/worker/jobs/{jobId}/artifacts/commit?worker={Uri.EscapeDataString(_worker)}&name={Uri.EscapeDataString(name)}&kind={Uri.EscapeDataString(kind)}", null, ct);
                    commit.EnsureSuccessStatusCode();
                    return;
                }
            }
        }

        await using var fs = File.OpenRead(filePath);
        using var content = new StreamContent(fs);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await _http.PostAsync(
            $"api/worker/jobs/{jobId}/artifacts?worker={Uri.EscapeDataString(_worker)}&name={Uri.EscapeDataString(name)}&kind={Uri.EscapeDataString(kind)}", content, ct);
        res.EnsureSuccessStatusCode();
    }

    public Task<bool> CompleteAsync(string jobId, Dictionary<string, double> summary, CancellationToken ct) =>
        PostAsync($"api/worker/jobs/{jobId}/complete?worker={Uri.EscapeDataString(_worker)}", new { summary }, ct);

    public Task<bool> FailAsync(string jobId, string error, bool retryable, CancellationToken ct) =>
        PostAsync($"api/worker/jobs/{jobId}/fail?worker={Uri.EscapeDataString(_worker)}", new { error, retryable }, ct);

    /// <summary>True if the server accepted it (2xx). 409 means the job no longer runs on this worker (cancelled or returned to the queue).</summary>
    private async Task<bool> PostAsync(string url, object body, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(url, body, ct);
        if (res.StatusCode == HttpStatusCode.Conflict) return false;
        res.EnsureSuccessStatusCode();
        return true;
    }

    public void Dispose() { _http.Dispose(); _r2Http.Dispose(); }
}
