using System.Text.Json;
using System.Text.RegularExpressions;
using Cadflow.Api;
using InventorAddin.Gea;
using InventorAddin.Models;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.GetFullPath(builder.Configuration["DataDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"));
var workerKey = builder.Configuration["Worker:Key"] ?? "dev-worker-key";
var frontendDir = Path.GetFullPath(builder.Configuration["Frontend:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "fe"));

builder.Services.AddSingleton(new JobStore(dataDir));
builder.Services.AddSingleton(new ScriptStore(dataDir));
builder.Services.AddSingleton(new R2Store(builder.Configuration.GetSection("R2").Get<R2Options>() ?? new R2Options()));
builder.Services.AddHostedService<StaleJobReaper>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
// Cho phép front-end chạy riêng (dev server) gọi API; bản chạy chung một origin thì không cần.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

if (Directory.Exists(frontendDir))
{
    var files = new PhysicalFileProvider(frontendDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}
else
{
    app.Logger.LogWarning("Frontend directory not found: {Dir}. Only the API is served.", frontendDir);
}

var store = app.Services.GetRequiredService<JobStore>();
var scripts = app.Services.GetRequiredService<ScriptStore>();
var r2 = app.Services.GetRequiredService<R2Store>();
app.Logger.LogInformation("Artifact storage: {Mode}", r2.IsConfigured ? "Cloudflare R2" : "local disk (R2 not configured)");
var safeName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._\- ]{0,119}$", RegexOptions.Compiled);
const string ImportedProduct = "imported"; // mã sản phẩm của job đặt từ script .gea do người dùng import

// ================================================================= sản phẩm

app.MapGet("/api/products", () => Products.All.Select(p => new { code = p.Code, name = p.Name, description = p.Description, version = p.Version }));

app.MapGet("/api/products/{code}", (string code) =>
    Products.Find(code) is { } p ? Results.Ok(p.Schema()) : Results.NotFound(new { error = $"Unknown product '{code}'." }));

app.MapPost("/api/products/{code}/validate", (string code, ValidateRequest req) =>
{
    if (Products.Find(code) is not { } product) return Results.NotFound(new { error = $"Unknown product '{code}'." });
    var values = product.Complete(req.Values);
    var issues = product.Validate(values);
    return Results.Ok(new { valid = issues.Count == 0, values, issues });
});

// ================================================================= script .gea (import)
// Import: nhận nội dung file .gea (có thể kèm khối giá trị !@ do CADFlow export), biên dịch, lưu theo mã băm và trả form.
// Form của script có thể ẩn/hiện ô theo giá trị, nên giao diện gọi lại /form mỗi khi giá trị đổi.

app.MapPost("/api/gea/import", (GeaTextRequest req) =>
{
    var text = req.Text ?? "";
    if (text.Trim().Length == 0) return Results.BadRequest(new { error = "The file is empty." });
    if (System.Text.Encoding.UTF8.GetByteCount(text) > GeaForms.MaxScriptBytes)
        return Results.BadRequest(new { error = $"The file is larger than {GeaForms.MaxScriptBytes / 1024} KB." });

    var block = GeaExport.ParseBlock(text);
    var clean = GeaExport.StripBlock(text); // lưu bản không có khối giá trị: cùng script thì cùng mã băm
    var script = GeaScript.Compile(clean);
    if (GeaForms.Reject(script) is { } why)
        return Results.BadRequest(new { error = why, diagnostics = GeaForms.Diagnostics(script) });

    var names = GeaForms.FieldNames(script).ToHashSet(StringComparer.Ordinal);
    var values = block?.Values.Where(kv => names.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, double>();
    var texts = block?.Texts.Where(kv => names.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, string>();
    var ignored = (block?.Values.Keys.Concat(block.Texts.Keys) ?? Enumerable.Empty<string>()).Where(k => !names.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    var warnings = new List<string>();
    if (block?.ScriptSha256 is { } exportedSha && exportedSha != ScriptStore.Sha256(clean))
        warnings.Add("The script was modified after this file was exported; the values may no longer fit it.");
    if (ignored.Length > 0)
        warnings.Add($"{ignored.Length} value(s) in the file do not match any parameter of the script and were ignored: {string.Join(", ", ignored.Take(5))}{(ignored.Length > 5 ? ", ..." : "")}.");

    var id = scripts.Save(clean);
    return Results.Ok(new
    {
        scriptId = id,
        name = script.Program.ProductName,
        hasValues = block != null,
        values,
        texts,
        ignored,
        warnings,
        diagnostics = GeaForms.Diagnostics(script),
        form = GeaForms.Describe(GeaForms.Evaluate(script, values, texts)),
    });
});

app.MapPost("/api/gea/{id}/form", (string id, GeaFormRequest req) =>
{
    if (scripts.Get(id) is not { } text) return Results.NotFound(new { error = "Unknown script. Import the .gea file again." });
    var script = GeaScript.Compile(text);
    if (GeaForms.Reject(script) is { } why) return Results.BadRequest(new { error = why });
    return Results.Ok(new { scriptId = id, name = script.Program.ProductName, form = GeaForms.Describe(GeaForms.Evaluate(script, req.Values, req.Texts)) });
});

// ================================================================= đơn hàng / job

app.MapPost("/api/jobs", (CreateJobRequest req) =>
{
    var requestedByName = string.IsNullOrWhiteSpace(req.RequestedBy) ? "anonymous" : req.RequestedBy.Trim();
    if (requestedByName.Length > 80) requestedByName = requestedByName[..80];

    // Đặt hàng từ script .gea đã import: server biên dịch lại và tính FORM lần cuối (không tin trình duyệt).
    if (!string.IsNullOrWhiteSpace(req.ScriptId))
    {
        if (scripts.Get(req.ScriptId) is not { } scriptSource)
            return Results.NotFound(new { error = "Unknown script. Import the .gea file again." });
        var script = GeaScript.Compile(scriptSource);
        if (GeaForms.Reject(script) is { } why)
            return Results.BadRequest(new { error = why, diagnostics = GeaForms.Diagnostics(script, errorsOnly: true) });
        var form = GeaForms.Evaluate(script, req.Inputs, req.TextInputs);
        if (!form.Ok)
            return Results.BadRequest(new { error = "The order violates the script's rules.", issues = form.Errors.Select(e => new { message = e.Message, @params = e.Params }) });

        var name = string.IsNullOrWhiteSpace(script.Program.ProductName) ? "imported.gea" : script.Program.ProductName;
        var (numbers, texts) = GeaForms.Complete(form);
        var scriptJob = store.Create(ImportedProduct, numbers, requestedByName, name, req.ScriptId, "imported", texts);
        return Results.Created($"/api/jobs/{scriptJob.Id}", scriptJob);
    }

    var product = Products.Find(string.IsNullOrWhiteSpace(req.ProductCode) ? "box" : req.ProductCode);
    if (product == null) return Results.NotFound(new { error = $"Unknown product '{req.ProductCode}'." });

    // Server luôn kiểm tra lại lần cuối (là bản chuẩn), không tin form ở trình duyệt.
    var values = product.Complete(req.Inputs);
    var issues = product.Validate(values);
    if (issues.Count > 0)
        return Results.BadRequest(new { error = "The order violates product rules.", issues });

    // Ghi lại đúng bản script .gea dùng cho job này (để export sau này khớp với những gì đã chạy).
    string? scriptSha = null;
    if (product.ScriptFile != null)
    {
        if (ScriptStore.ReadBundled(product.ScriptFile) is { } scriptText) scriptSha = scripts.Save(scriptText);
        else app.Logger.LogWarning("Bundled script {File} for product {Product} was not found; the job will have no .gea export.", product.ScriptFile, product.Code);
    }

    var job = store.Create(product.Code, values, requestedByName,
        scriptSha == null ? null : product.ScriptFile, scriptSha, scriptSha == null ? null : "bundled");
    return Results.Created($"/api/jobs/{job.Id}", job);
});

// Danh sách có lọc: ?status=queued,running &product=box &requestedBy=ten &from=2026-11-01 &to=2026-11-08 &limit=100
// Không có tham số thì trả toàn bộ (tương thích bản cũ). Mới nhất trước.
app.MapGet("/api/jobs", (string? status, string? product, string? requestedBy, DateTimeOffset? from, DateTimeOffset? to, int? limit) =>
{
    IEnumerable<Job> jobs = store.List();

    if (!string.IsNullOrWhiteSpace(status))
    {
        var wanted = new HashSet<JobStatus>();
        foreach (var s in status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<JobStatus>(s, ignoreCase: true, out var parsed))
                return Results.BadRequest(new { error = $"Unknown status '{s}'." });
            wanted.Add(parsed);
        }
        jobs = jobs.Where(j => wanted.Contains(j.Status));
    }
    if (!string.IsNullOrWhiteSpace(product))
        jobs = jobs.Where(j => string.Equals(j.ProductCode, product.Trim(), StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(requestedBy))
        jobs = jobs.Where(j => string.Equals(j.RequestedBy, requestedBy.Trim(), StringComparison.OrdinalIgnoreCase));
    if (from is { } f) jobs = jobs.Where(j => j.CreatedAt >= f);
    if (to is { } t) jobs = jobs.Where(j => j.CreatedAt <= t);
    if (limit is > 0) jobs = jobs.Take(limit.Value);

    return Results.Ok(jobs.ToList());
});

// Thống kê cho Dashboard: đếm theo trạng thái, tỉ lệ thành công, thời gian build, và theo ngày (giờ máy chủ).
// ?days=14 (1-90) &product= &requestedBy=. Thống kê tính trên job tạo trong khoảng `days` ngày gần nhất.
app.MapGet("/api/stats", (int? days, string? product, string? requestedBy) =>
{
    int span = Math.Clamp(days ?? 14, 1, 90);
    var today = DateTimeOffset.Now.Date;
    var firstDay = today.AddDays(-(span - 1));

    IEnumerable<Job> jobs = store.List().Where(j => j.CreatedAt.ToLocalTime().Date >= firstDay);
    if (!string.IsNullOrWhiteSpace(product))
        jobs = jobs.Where(j => string.Equals(j.ProductCode, product.Trim(), StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(requestedBy))
        jobs = jobs.Where(j => string.Equals(j.RequestedBy, requestedBy.Trim(), StringComparison.OrdinalIgnoreCase));
    var list = jobs.ToList();

    // Thời gian build = từ lúc worker nhận đến lúc xong, chỉ tính job thành công.
    static double? BuildSeconds(Job j) =>
        j.Status == JobStatus.Succeeded && j.StartedAt is { } s && j.FinishedAt is { } f && f >= s ? (f - s).TotalSeconds : null;
    static double? Avg(IEnumerable<double?> xs) { var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : Math.Round(v.Average(), 1); }

    var byStatus = Enum.GetValues<JobStatus>().ToDictionary(
        s => JsonNamingPolicy.CamelCase.ConvertName(s.ToString()), s => list.Count(j => j.Status == s));
    int ok = byStatus["succeeded"], bad = byStatus["failed"];

    var byDay = Enumerable.Range(0, span).Select(i =>
    {
        var day = firstDay.AddDays(i);
        var dayJobs = list.Where(j => j.CreatedAt.ToLocalTime().Date == day).ToList();
        return new
        {
            date = day.ToString("yyyy-MM-dd"),
            total = dayJobs.Count,
            succeeded = dayJobs.Count(j => j.Status == JobStatus.Succeeded),
            failed = dayJobs.Count(j => j.Status == JobStatus.Failed),
            avgBuildSeconds = Avg(dayJobs.Select(BuildSeconds)),
        };
    }).ToList();

    return Results.Ok(new
    {
        days = span,
        total = list.Count,
        byStatus,
        successRate = ok + bad == 0 ? (double?)null : Math.Round(100.0 * ok / (ok + bad), 1),
        avgBuildSeconds = Avg(list.Select(BuildSeconds)),
        byDay,
    });
});

app.MapGet("/api/jobs/{id}",(string id) => store.Get(id) is { } j ? Results.Ok(j) : Results.NotFound());

app.MapPost("/api/jobs/{id}/cancel", (string id) =>
    store.Cancel(id) ? Results.NoContent() : Results.Conflict(new { error = "The job is not found or already finished." }));

app.MapGet("/api/jobs/{id}/artifacts/{name}", (string id, string name, bool? download) =>
{
    var job = store.Get(id);
    if (job == null || !job.Artifacts.Any(a => a.Name == name) || !safeName.IsMatch(name)) return Results.NotFound();
    // Job chưa xong thì chưa cho xem/tải kết quả (artifact được đăng ký dần trong lúc job còn 'running').
    if (job.Status != JobStatus.Succeeded)
        return Results.Conflict(new { error = "Results are available only after the job has succeeded." });
    // File ở R2: chuyển hướng sang URL ký sẵn, hạn ngắn (bucket private, khóa R2 không rời server).
    if (job.Artifacts.First(a => a.Name == name).Key is { } key && r2.IsConfigured)
        return Results.Redirect(r2.PresignGet(key, name, ContentTypeFor(name), download == true, DateTime.UtcNow));
    var path = Path.Combine(store.ArtifactDir(id), name);
    if (!File.Exists(path)) return Results.NotFound();
    // Trả inline để trình duyệt hiển thị ảnh/PDF trong trang; nút tải ở front-end dùng thuộc tính download.
    return Results.File(path, ContentTypeFor(name));
});

// Export .gea: script đã dùng cho job + giá trị đầu vào (khối chú thích !@, xem GeaExport). Là đơn hàng chứ không phải kết quả,
// nên xuất được ở mọi trạng thái.
app.MapGet("/api/jobs/{id}/export.gea", (string id) =>
{
    var job = store.Get(id);
    if (job == null) return Results.NotFound();
    if (scripts.Get(job.ScriptSha256) is not { } text)
        return Results.NotFound(new { error = "This job has no .gea script (its product is not built from a script)." });
    var bytes = new System.Text.UTF8Encoding(false).GetBytes(GeaExport.Build(job, text));
    return Results.File(bytes, "text/plain; charset=utf-8", fileDownloadName: job.Id + ".gea");
});

app.MapGet("/api/workers", () => store.Workers());

// ================================================================= worker (Inventor)
// Worker chủ động hỏi server; server không gọi worker. Xác thực bằng khóa dùng chung (mỗi máy một khóa khi triển khai thật).

var worker = app.MapGroup("/api/worker").AddEndpointFilter(async (ctx, next) =>
{
    if (ctx.HttpContext.Request.Headers["X-Worker-Key"] != workerKey)
        return Results.Unauthorized();
    return await next(ctx);
});

worker.MapPost("/heartbeat", (WorkerHello hello) =>
{
    if (string.IsNullOrWhiteSpace(hello.Worker)) return Results.BadRequest(new { error = "worker is required." });
    store.Heartbeat(hello);
    return Results.NoContent();
});

worker.MapPost("/claim", (ClaimRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Worker)) return Results.BadRequest(new { error = "worker is required." });
    if (store.Claim(req.Worker!) is not { } item) return Results.NoContent();
    // Kèm đúng bản script đã ghi cho job (bản đi kèm sản phẩm hoặc bản người dùng import).
    var scriptJob = store.Get(item.Id);
    return Results.Ok(scriptJob?.ScriptSha256 != null && scripts.Get(scriptJob.ScriptSha256) is { } text
        ? item with { ScriptText = text, ScriptName = scriptJob.ScriptName }
        : item);
});

worker.MapPost("/jobs/{id}/progress", (string id, string worker, ProgressRequest req) =>
    store.Progress(id, worker, req.Step, req.Message) ? Results.NoContent() : Results.Conflict(new { error = "The job is not running on this worker." }));

// Bước 1 của upload qua R2: worker xin URL PUT. Chưa cấu hình R2 thì mode = "direct" (worker dùng upload thân file như cũ).
worker.MapPost("/jobs/{id}/artifacts/presign", (string id, string worker, string name) =>
{
    if (!safeName.IsMatch(name)) return Results.BadRequest(new { error = "Invalid file name." });
    var job = store.Get(id);
    if (job is not { Status: JobStatus.Running } || !string.Equals(job.Worker, worker, StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new { error = "The job is not running on this worker." });
    if (!r2.IsConfigured) return Results.Ok(new { mode = "direct" });
    var key = R2Store.KeyFor(id, name);
    return Results.Ok(new { mode = "r2", url = r2.PresignPut(key, DateTime.UtcNow), key });
});

// Bước 2: worker báo đã PUT xong; server kiểm tra object có thật trên R2 rồi mới ghi vào job.
worker.MapPost("/jobs/{id}/artifacts/commit", async (string id, string worker, string name, string? kind) =>
{
    if (!safeName.IsMatch(name)) return Results.BadRequest(new { error = "Invalid file name." });
    var job = store.Get(id);
    if (job is not { Status: JobStatus.Running } || !string.Equals(job.Worker, worker, StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new { error = "The job is not running on this worker." });
    if (!r2.IsConfigured) return Results.BadRequest(new { error = "R2 is not configured." });
    var key = R2Store.KeyFor(id, name);
    if (await r2.SizeAsync(key) is not { } size)
        return Results.BadRequest(new { error = "The file was not found in R2; upload it first." });
    store.AddArtifact(id, worker, new ArtifactInfo { Name = name, Kind = kind ?? Path.GetExtension(name).TrimStart('.').ToLowerInvariant(), Size = size, Key = key });
    return Results.NoContent();
});

worker.MapPost("/jobs/{id}/artifacts", async (string id, string worker, string name, string? kind, HttpRequest request) =>
{
    if (!safeName.IsMatch(name)) return Results.BadRequest(new { error = "Invalid file name." });
    var job = store.Get(id);
    if (job is not { Status: JobStatus.Running } || !string.Equals(job.Worker, worker, StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new { error = "The job is not running on this worker." });

    var dir = store.ArtifactDir(id);
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, name);
    await using (var fs = File.Create(path))
        await request.Body.CopyToAsync(fs);

    var size = new FileInfo(path).Length;
    store.AddArtifact(id, worker, new ArtifactInfo { Name = name, Kind = kind ?? Path.GetExtension(name).TrimStart('.').ToLowerInvariant(), Size = size });
    return Results.NoContent();
});

worker.MapPost("/jobs/{id}/complete", (string id, string worker, CompleteRequest req) =>
    store.Complete(id, worker, req.Summary) ? Results.NoContent() : Results.Conflict(new { error = "The job is not running on this worker." }));

worker.MapPost("/jobs/{id}/fail", (string id, string worker, FailRequest req) =>
    store.Fail(id, worker, string.IsNullOrWhiteSpace(req.Error) ? "Unknown error." : req.Error!, req.Retryable)
        ? Results.NoContent()
        : Results.Conflict(new { error = "The job is not running on this worker." }));

app.Run();

// ================================================================= tiện ích

static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".pdf" => "application/pdf",
    ".json" => "application/json",
    ".csv" => "text/csv",
    ".zip" => "application/zip",
    ".dwg" => "image/vnd.dwg",
    ".gea" => "text/plain; charset=utf-8",
    _ => "application/octet-stream", // .stp/.step/.ipt/.idw...
};
