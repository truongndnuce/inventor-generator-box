using Microsoft.AspNetCore.Http.Features;

namespace Cadflow.Api.Vault;

public sealed record CreateFolderRequest(string? ParentId, string? Name, string? CreatedBy);
public sealed record RenameFolderRequest(string? Name);
public sealed record StateRequest(string? State, string? ModifiedBy);
public sealed record FromJobRequest(string? RequestedBy);

public static class VaultEndpoints
{
    private const long MaxUploadBytes = 1L << 29;   // 512 MB

    /// <summary>The document vault API (<c>/api/vault</c>); see docs/superpowers/specs/2026-11-10-plm-vault-design.md.</summary>
    public static void MapVault(this WebApplication app)
    {
        var vault = app.Services.GetRequiredService<VaultStore>();
        var files = app.Services.GetRequiredService<VaultFiles>();
        var jobs = app.Services.GetRequiredService<JobStore>();
        var g = app.MapGroup("/api/vault").AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (VaultException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        g.MapGet("/tree", () => new { folders = vault.Folders(), states = VaultStore.States });

        // Contents of one folder; the root (no id) lists the top-level folders.
        g.MapGet("/folders", () => new
        {
            folder = (VaultFolder?)null, breadcrumb = new List<VaultFolder>(),
            folders = vault.Folders().Where(f => f.ParentId == null), items = new List<VaultItem>(),
        });

        g.MapGet("/folders/{id}", (string id) =>
        {
            if (vault.Folder(id) is not { } folder) return Results.NotFound(new { error = "Folder not found." });
            return Results.Ok(new
            {
                folder, breadcrumb = vault.Breadcrumb(id),
                folders = vault.Folders().Where(f => f.ParentId == id), items = vault.Items(id),
            });
        });

        g.MapPost("/folders", (CreateFolderRequest req) =>
            Results.Created("/api/vault/folders/", vault.CreateFolder(req.ParentId, req.Name ?? "", req.CreatedBy)));

        g.MapPatch("/folders/{id}", (string id, RenameFolderRequest req) => Results.Ok(vault.RenameFolder(id, req.Name ?? "")));

        g.MapDelete("/folders/{id}", (string id) =>
        {
            files.Delete(vault.DeleteFolder(id));
            return Results.NoContent();
        });

        // Upload: the request body is the file. A name that already exists in the folder becomes a new version of that item.
        g.MapPost("/folders/{id}/items", async (string id, string name, string? by, string? comment, HttpContext ctx) =>
        {
            if (!VaultStore.IsValidName(name)) return Results.BadRequest(new { error = "File name is empty, too long or contains / \\ : * ? \" < > |." });
            var temp = await SaveBody(ctx, app);
            try
            {
                var type = VaultFiles.ContentTypeFor(name);
                var r = vault.Put(id, name, by, comment, type, null, (itemId, v) => files.PlaceFile(temp, itemId, v, name, type));
                return Results.Json(r.Item, statusCode: r.Created ? 201 : 200);
            }
            finally { TryDelete(temp); }
        });

        g.MapGet("/items/{id}", (string id) =>
            vault.Item(id) is { } item ? Results.Ok(new { item, versions = vault.Versions(id), breadcrumb = vault.Breadcrumb(item.FolderId) }) : Results.NotFound(new { error = "Item not found." }));

        g.MapPost("/items/{id}/versions", async (string id, string? by, string? comment, HttpContext ctx) =>
        {
            var item = vault.Item(id);
            if (item == null) return Results.NotFound(new { error = "Item not found." });
            var temp = await SaveBody(ctx, app);
            try
            {
                var type = VaultFiles.ContentTypeFor(item.Name);
                var r = vault.AddVersion(id, by, comment, type, (itemId, v) => files.PlaceFile(temp, itemId, v, item.Name, type));
                return Results.Ok(r.Item);
            }
            finally { TryDelete(temp); }
        });

        g.MapGet("/items/{id}/versions/{n:int}/file", (string id, int n, bool? download) =>
            vault.Version(id, n) is { } x ? files.Serve(x.Item, x.Version, download == true) : Results.NotFound());

        g.MapPatch("/items/{id}/state", (string id, StateRequest req) => Results.Ok(vault.SetState(id, req.State ?? "", req.ModifiedBy)));

        g.MapDelete("/items/{id}", (string id) =>
        {
            files.Delete(vault.DeleteItem(id));
            return Results.NoContent();
        });

        g.MapGet("/search", (string? q, string? kind, string? state) => vault.Search(q, kind, state));

        // Save the results of a finished job: folder "<product>-<job id>" with image / drawing / model sub-folders, files copied in.
        g.MapPost("/from-job/{jobId}", (string jobId, FromJobRequest? req) =>
        {
            var job = jobs.Get(jobId);
            if (job == null) return Results.NotFound(new { error = "Job not found." });
            if (job.Status != JobStatus.Succeeded || job.Kind == JobKinds.Preview)
                return Results.Conflict(new { error = "Only a finished full build (generate or export) can be saved to the vault." });
            if (job.ArtifactsPurgedAt != null)
                return Results.Json(new { error = "The result files of this job were deleted by retention. Rebuild the job first." }, statusCode: 410);

            var (folder, sub) = vault.EnsureJobFolder(job.Id, $"{job.ProductCode}-{job.Id}", req?.RequestedBy ?? job.RequestedBy);
            int added = 0, versions = 0, unchanged = 0;
            var skipped = new List<string>();
            foreach (var a in job.Artifacts)
            {
                var kind = VaultStore.KindOf(a.Name);
                if (!sub.TryGetValue(kind, out var target)) { skipped.Add(a.Name); continue; }
                var r = vault.Put(target.Id, a.Name, req?.RequestedBy ?? job.RequestedBy, $"From job {job.Id}", VaultFiles.ContentTypeFor(a.Name), job.Id,
                    (itemId, v) => files.CopyFromJob(job, a, itemId, v));
                if (r.Unchanged) unchanged++; else if (r.Created) added++; else versions++;
            }
            return Results.Ok(new { folder, added, newVersions = versions, unchanged, skipped });
        });
    }

    /// <summary>Copies the request body to a temp file (so R2 can be given a file of known length).</summary>
    private static async Task<string> SaveBody(HttpContext ctx, WebApplication app)
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxUploadBytes;
        var dir = Path.Combine(Path.GetFullPath(app.Configuration["DataDir"] ?? Path.Combine(app.Environment.ContentRootPath, "data")), "vault-tmp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        try
        {
            await using var fs = File.Create(path);
            await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);
        }
        catch { TryDelete(path); throw; }
        if (new FileInfo(path).Length == 0) { TryDelete(path); throw new VaultException(400, "The file is empty."); }
        return path;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* ignore */ } }
}
