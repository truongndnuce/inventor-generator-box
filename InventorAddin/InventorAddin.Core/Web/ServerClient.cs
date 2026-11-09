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

/// <summary>Job server giao xuống worker: chỉ những gì cần để dựng.</summary>
public sealed record WorkItem(string Id, string ProductCode, int ProductVersion, Dictionary<string, double> Inputs, string? ScriptText = null, string? ScriptName = null, Dictionary<string, string>? TextInputs = null);

/// <summary>Gọi API worker của web server (xem be/Cadflow.Api). Worker chỉ kết nối đi ra, không mở cổng.</summary>
public sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;
    // HttpClient riêng cho R2: không mang X-Worker-Key sang máy chủ khác.
    private readonly HttpClient _r2Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _worker;
    private readonly string[]? _products;

    /// <param name="products">Mã sản phẩm worker này nhận; null = nhận mọi sản phẩm.</param>
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

    /// <summary>Nhận job tiếp theo, null nếu hàng đợi trống.</summary>
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

        // Server có R2: tải thẳng lên R2 bằng URL ký sẵn rồi báo commit. Server cũ (404) hoặc chưa cấu hình R2 ("direct"): upload qua server.
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
                    // Không có header nào ngoài thân file: chữ ký URL không ràng buộc Content-Type.
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

    /// <summary>True nếu server nhận (2xx). 409 nghĩa là job không còn chạy trên worker này (bị hủy hoặc trả về hàng đợi).</summary>
    private async Task<bool> PostAsync(string url, object body, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(url, body, ct);
        if (res.StatusCode == HttpStatusCode.Conflict) return false;
        res.EnsureSuccessStatusCode();
        return true;
    }

    public void Dispose() { _http.Dispose(); _r2Http.Dispose(); }
}
