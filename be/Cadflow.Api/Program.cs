using System.Text.Json;
using System.Text.RegularExpressions;
using Cadflow.Api;
using Cadflow.Api.Chat;
using Cadflow.Api.Mcp;
using System.Threading.RateLimiting;
using InventorAddin.Gea;
using InventorAddin.Models;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// .env next to the server (ignored by git): KEY__SUB=value -> configuration KEY:SUB. Real environment variables still win.
var envFile = Path.Combine(builder.Environment.ContentRootPath, ".env");
if (File.Exists(envFile))
{
    var fromEnvFile = File.ReadAllLines(envFile)
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
        .Select(l => (Key: l[..l.IndexOf('=')].Trim().Replace("__", ":"), Value: l[(l.IndexOf('=') + 1)..].Trim()))
        .Where(kv => kv.Key.Length > 0 && kv.Value.Length > 0 && Environment.GetEnvironmentVariable(kv.Key.Replace(":", "__")) == null)
        .ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
    builder.Configuration.AddInMemoryCollection(fromEnvFile);
}

var dataDir = Path.GetFullPath(builder.Configuration["DataDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"));
var workerKey = builder.Configuration["Worker:Key"] ?? "dev-worker-key";
var frontendDir = Path.GetFullPath(builder.Configuration["Frontend:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "fe", "dist")); // React front-end: run `npm run build` in fe/

var dbConn = builder.Configuration.GetConnectionString("Cadflow")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Cadflow (PostgreSQL). Set it in appsettings.json or .env (ConnectionStrings__Cadflow).");
builder.Services.AddSingleton(new JobStore(dataDir, dbConn));
builder.Services.AddSingleton(new Cadflow.Api.Chat.ChatStore(dbConn));
builder.Services.AddSingleton(new ScriptStore(dataDir));
builder.Services.AddSingleton(new ConfigStore(dataDir));
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<DraftStore>();
builder.Services.AddSingleton<ComponentCatalog>();
builder.Services.AddSingleton<CadTools>();
// MCP server (/mcp): the same CadTools tool set as chat.
builder.Services.AddMcpServer().WithHttpTransport().WithTools<CadTools>();
// Rate limit for chat (it costs LLM money): per IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("chat", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddSingleton(new R2Store(builder.Configuration.GetSection("R2").Get<R2Options>() ?? new R2Options()));
builder.Services.AddHostedService<StaleJobReaper>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
// Lets the front-end run separately (dev server) and call the API; the single-origin deployment does not need it.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseRateLimiter();

// /mcp: key in the X-Api-Key header (Mcp:Key). If no key is configured, only calls from the server machine itself (loopback) are allowed.
var mcpKey = app.Configuration["Mcp:Key"];
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/mcp"))
    {
        var ok = string.IsNullOrEmpty(mcpKey)
            ? System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? System.Net.IPAddress.None)
            : ctx.Request.Headers["X-Api-Key"] == mcpKey;
        if (!ok) { ctx.Response.StatusCode = 401; return; }
    }
    await next();
});

if (Directory.Exists(frontendDir))
{
    var files = new PhysicalFileProvider(frontendDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}
else
{
    app.Logger.LogWarning("Frontend build not found: {Dir}. Run 'npm install' and 'npm run build' in fe/ (or use 'npm run dev'). Only the API is served.", frontendDir);
}

var store = app.Services.GetRequiredService<JobStore>();
var scripts = app.Services.GetRequiredService<ScriptStore>();
var configs = app.Services.GetRequiredService<ConfigStore>();
var r2 = app.Services.GetRequiredService<R2Store>();
app.Logger.LogInformation("Artifact storage: {Mode}", r2.IsConfigured ? "Cloudflare R2" : "local disk (R2 not configured)");
var safeName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._\- ]{0,119}$", RegexOptions.Compiled);

// ================================================================= products

app.MapGet("/api/products", () => Products.All.Select(p => new { code = p.Code, name = p.Name, description = p.Description, version = p.Version }));

// `defaults` = values of the default Configuration (or ?config=<id>); `factory` = the factory defaults in code.
app.MapGet("/api/products/{code}", (string code, string? config) =>
{
    if (Products.Find(code) is not { } p) return Results.NotFound(new { error = $"Unknown product '{code}'." });
    var cfg = string.IsNullOrWhiteSpace(config) ? null : configs.Get(p, config);
    if (!string.IsNullOrWhiteSpace(config) && cfg == null) return Results.NotFound(new { error = "Unknown configuration." });
    return Results.Ok(p.Schema(configs.Effective(p, null, cfg)));
});

app.MapPost("/api/products/{code}/validate", (string code, ValidateRequest req) =>
{
    if (Products.Find(code) is not { } product) return Results.NotFound(new { error = $"Unknown product '{code}'." });
    var values = configs.Effective(product, req.Values, null);
    var issues = product.Validate(values);
    return Results.Ok(new { valid = issues.Count == 0, values, issues });
});

// ================================================================= Configuration per product (Config tab)
// Each product has several Configurations (name, status, Operation, CAD design condition, component values).
// An order picks one Configuration (configId) or uses the default one.

app.MapGet("/api/config-schema", () => new { statuses = ConfigSchema.Statuses, groups = ConfigSchema.Groups });

app.MapGet("/api/products/{code}/configs", (string code) =>
    Products.Find(code) is { } p ? Results.Ok(configs.List(p).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) : Results.NotFound(new { error = $"Unknown product '{code}'." }));

IResult ConfigResult((ProductConfig? Config, string? Error, bool NotFound) r, bool created = false) =>
    r.NotFound ? Results.NotFound(new { error = r.Error })
    : r.Error != null ? Results.BadRequest(new { error = r.Error })
    : created ? Results.Created($"/api/configs/{r.Config!.Id}", r.Config) : Results.Ok(r.Config);

app.MapPost("/api/products/{code}/configs", (string code, ConfigRequest req) =>
    Products.Find(code) is { } p ? ConfigResult(configs.Save(p, null, req), created: true) : Results.NotFound(new { error = $"Unknown product '{code}'." }));

app.MapPut("/api/products/{code}/configs/{id}", (string code, string id, ConfigRequest req) =>
    Products.Find(code) is { } p ? ConfigResult(configs.Save(p, id, req)) : Results.NotFound(new { error = $"Unknown product '{code}'." }));

app.MapDelete("/api/products/{code}/configs/{id}", (string code, string id) =>
    Products.Find(code) is not { } p ? Results.NotFound(new { error = $"Unknown product '{code}'." })
    : configs.Delete(p, id) ? Results.NoContent() : Results.NotFound(new { error = "Unknown configuration." }));
// ================================================================= script .gea (import)
// Import: takes the content of a .gea file (it may carry a !@ value block exported by CADFlow), compiles it, stores it by hash and returns the form.
// The form of a script can show/hide fields depending on the values, so the UI calls /form again whenever a value changes.

app.MapPost("/api/gea/import", (GeaTextRequest req, JobService jobs) =>
{
    var r = jobs.Import(req.Text);
    return Results.Json(r.Body, statusCode: r.Status);
});

app.MapPost("/api/gea/{id}/form", (string id, GeaFormRequest req, JobService jobs) =>
{
    var r = jobs.ScriptForm(id, req.Values, req.Texts);
    return Results.Json(r.Body, statusCode: r.Status);
});

// ================================================================= orders / jobs

app.MapPost("/api/jobs", (CreateJobRequest req, JobService jobs) =>
{
    var r = jobs.Create(req);
    return r.Ok ? Results.Created($"/api/jobs/{r.Job!.Id}", r.Job) : Results.Json(r.Error, statusCode: r.Status);
});

// Filtered list: ?status=queued,running &product=box &requestedBy=name &from=2026-11-01 &to=2026-11-08 &limit=100
// Without parameters it returns everything (compatible with the old version). Newest first.
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

// Statistics for the Dashboard: counts by status, success rate, build time, and per day (server time).
// ?days=14 (1-90) &product= &requestedBy=. Statistics are computed over jobs created within the last `days` days.
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

    // Build time = from when the worker took the job until it finished, counting successful jobs only.
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
    // A job that is not finished cannot show/download its results yet (artifacts are registered gradually while the job is still 'running').
    if (job.Status != JobStatus.Succeeded)
        return Results.Conflict(new { error = "Results are available only after the job has succeeded." });
    // File on R2: redirect to a short-lived presigned URL (private bucket, the R2 key never leaves the server).
    if (job.Artifacts.First(a => a.Name == name).Key is { } key && r2.IsConfigured)
        return Results.Redirect(r2.PresignGet(key, name, ContentTypeFor(name), download == true, DateTime.UtcNow));
    var path = Path.Combine(store.ArtifactDir(id), name);
    if (!File.Exists(path)) return Results.NotFound();
    // Returned inline so the browser shows images/PDFs in the page; the download button in the front-end uses the download attribute.
    return Results.File(path, ContentTypeFor(name));
});

// Export .gea: the script used for the job + the input values (the !@ comment block, see GeaExport). It is an order, not a result,
// so it can be exported in every status.
app.MapGet("/api/jobs/{id}/export.gea", (string id) =>
{
    var job = store.Get(id);
    if (job == null) return Results.NotFound();
    if (scripts.Get(job.ScriptSha256) is not { } text)
        return Results.NotFound(new { error = "This job has no .gea script (its product is not built from a script)." });
    var bytes = new System.Text.UTF8Encoding(false).GetBytes(GeaExport.Build(job, text));
    return Results.File(bytes, "text/plain; charset=utf-8", fileDownloadName: job.Id + ".gea");
});

// Component catalog (INCLUDE lib: libraries): view, download the original file, download a starter script. Read-only, see docs/superpowers/specs/2026-11-10-component-catalog-design.md.
app.MapGet("/api/components", (ComponentCatalog catalog) => catalog.List());

app.MapGet("/api/components/{name}/gea", (string name, ComponentCatalog catalog) =>
    catalog.Raw(name) is { } text
        ? Results.File(new System.Text.UTF8Encoding(false).GetBytes(text), "text/plain; charset=utf-8", fileDownloadName: name + ".gea")
        : Results.NotFound(new { error = $"Unknown component '{name}'." }));

app.MapGet("/api/components/{name}/starter", (string name, string? productName, string? prefix, ComponentCatalog catalog) =>
{
    var r = catalog.Starter(name, productName, prefix);
    return r.Text != null
        ? Results.File(new System.Text.UTF8Encoding(false).GetBytes(r.Text), "text/plain; charset=utf-8", fileDownloadName: name + "-starter.gea")
        : Results.Json(new { error = r.Error }, statusCode: r.Status);
});

app.MapGet("/api/workers", () => store.Workers());

// ================================================================= worker (Inventor)
// The worker asks the server; the server never calls the worker. Authenticated with a shared key (one key per machine in a real deployment).

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
    if (store.Claim(req.Worker!, req.Products) is not { } item) return Results.NoContent();
    // Includes exactly the script recorded for the job (the one that ships with the product or the one the user imported).
    var scriptJob = store.Get(item.Id);
    return Results.Ok(scriptJob?.ScriptSha256 != null && scripts.Get(scriptJob.ScriptSha256) is { } text
        ? item with { ScriptText = text, ScriptName = scriptJob.ScriptName }
        : item);
});

worker.MapPost("/jobs/{id}/progress", (string id, string worker, ProgressRequest req) =>
    store.Progress(id, worker, req.Step, req.Message) ? Results.NoContent() : Results.Conflict(new { error = "The job is not running on this worker." }));

// Step 1 of the upload through R2: the worker asks for a PUT URL. If R2 is not configured, mode = "direct" (the worker uploads the file body as before).
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

// Step 2: the worker reports the PUT is done; the server checks the object really exists on R2 before recording it on the job.
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

app.MapMcp("/mcp");
app.MapChat(app.Configuration["Anthropic:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
    app.Configuration["Anthropic:Model"] ?? "claude-sonnet-5-5", app.Configuration["Anthropic:BaseUrl"], app.Configuration.GetValue("Chat:AutoCreate", true));

app.Run();

// ================================================================= utilities

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

