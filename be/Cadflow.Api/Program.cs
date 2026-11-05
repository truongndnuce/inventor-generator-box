using System.Text.RegularExpressions;
using Cadflow.Api;
using InventorAddin.Models;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.GetFullPath(builder.Configuration["DataDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"));
var workerKey = builder.Configuration["Worker:Key"] ?? "dev-worker-key";
var frontendDir = Path.GetFullPath(builder.Configuration["Frontend:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "fe"));

builder.Services.AddSingleton(new JobStore(dataDir));
builder.Services.AddHostedService<StaleJobReaper>();
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
var safeName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._\- ]{0,119}$", RegexOptions.Compiled);

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

// ================================================================= đơn hàng / job

app.MapPost("/api/jobs", (CreateJobRequest req) =>
{
    var product = Products.Find(string.IsNullOrWhiteSpace(req.ProductCode) ? "box" : req.ProductCode);
    if (product == null) return Results.NotFound(new { error = $"Unknown product '{req.ProductCode}'." });

    // Server luôn kiểm tra lại lần cuối (là bản chuẩn), không tin form ở trình duyệt.
    var values = product.Complete(req.Inputs);
    var issues = product.Validate(values);
    if (issues.Count > 0)
        return Results.BadRequest(new { error = "The order violates product rules.", issues });

    var requestedBy = string.IsNullOrWhiteSpace(req.RequestedBy) ? "anonymous" : req.RequestedBy.Trim();
    var job = store.Create(product.Code, values, requestedBy.Length > 80 ? requestedBy[..80] : requestedBy);
    return Results.Created($"/api/jobs/{job.Id}", job);
});

app.MapGet("/api/jobs", () => store.List());

app.MapGet("/api/jobs/{id}", (string id) => store.Get(id) is { } j ? Results.Ok(j) : Results.NotFound());

app.MapPost("/api/jobs/{id}/cancel", (string id) =>
    store.Cancel(id) ? Results.NoContent() : Results.Conflict(new { error = "The job is not found or already finished." }));

app.MapGet("/api/jobs/{id}/artifacts/{name}", (string id, string name) =>
{
    var job = store.Get(id);
    if (job == null || !job.Artifacts.Any(a => a.Name == name) || !safeName.IsMatch(name)) return Results.NotFound();
    var path = Path.Combine(store.ArtifactDir(id), name);
    if (!File.Exists(path)) return Results.NotFound();
    // Trả inline để trình duyệt hiển thị ảnh/PDF trong trang; nút tải ở front-end dùng thuộc tính download.
    return Results.File(path, ContentTypeFor(name));
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
    return store.Claim(req.Worker!) is { } item ? Results.Ok(item) : Results.NoContent();
});

worker.MapPost("/jobs/{id}/progress", (string id, string worker, ProgressRequest req) =>
    store.Progress(id, worker, req.Step, req.Message) ? Results.NoContent() : Results.Conflict(new { error = "The job is not running on this worker." }));

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
    _ => "application/octet-stream", // .stp/.step/.ipt/.idw...
};
