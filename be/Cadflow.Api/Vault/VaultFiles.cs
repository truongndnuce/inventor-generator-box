using Microsoft.AspNetCore.Http;

namespace Cadflow.Api.Vault;

/// <summary>The bytes of the vault: Cloudflare R2 when configured (key <c>vault/{item}/v{n}/{name}</c>), otherwise <c>data/vault/{item}/v{n}/{name}</c> on the server disk.</summary>
public sealed class VaultFiles(string dataDir, R2Store r2, JobStore jobs)
{
    private string DiskPath(string itemId, int version, string name) => Path.Combine(dataDir, "vault", itemId, "v" + version, name);

    /// <summary>Stores a local temp file as (item, version). Moves it on disk, uploads it to R2. Returns the R2 key (null = disk) and the size.</summary>
    public (string? R2Key, long Size) PlaceFile(string tempPath, string itemId, int version, string name, string contentType)
    {
        var size = new FileInfo(tempPath).Length;
        if (r2.IsConfigured)
        {
            var key = R2Store.KeyForVault(itemId, version, name);
            r2.PutFileAsync(key, tempPath, contentType).GetAwaiter().GetResult();
            File.Delete(tempPath);
            return (key, size);
        }
        var dest = DiskPath(itemId, version, name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(tempPath, dest, overwrite: true);
        return (null, size);
    }

    /// <summary>Copies a job result into the vault (never a pointer back to <c>jobs/...</c>, which retention deletes).</summary>
    public (string? R2Key, long Size) CopyFromJob(Job job, ArtifactInfo artifact, string itemId, int version)
    {
        var diskSource = Path.Combine(jobs.ArtifactDir(job.Id), artifact.Name);
        if (artifact.Key != null)
        {
            if (!r2.IsConfigured) throw new VaultException(503, "This result is stored in R2 but R2 is not configured on the server.");
            var key = R2Store.KeyForVault(itemId, version, artifact.Name);
            r2.CopyAsync(artifact.Key, key).GetAwaiter().GetResult();
            return (key, artifact.Size);
        }
        if (!File.Exists(diskSource)) throw new VaultException(410, $"The file '{artifact.Name}' of job {job.Id} is gone (deleted by retention).");
        var size = new FileInfo(diskSource).Length;
        if (r2.IsConfigured)
        {
            var key = R2Store.KeyForVault(itemId, version, artifact.Name);
            r2.PutFileAsync(key, diskSource, ContentTypeFor(artifact.Name)).GetAwaiter().GetResult();
            return (key, size);
        }
        var dest = DiskPath(itemId, version, artifact.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(diskSource, dest, overwrite: true);
        return (null, size);
    }

    /// <summary>The HTTP answer for downloading a version: a redirect to a short-lived R2 URL, or the file from disk.</summary>
    public IResult Serve(VaultItem item, VaultVersion v, bool download)
    {
        if (v.R2Key != null && r2.IsConfigured)
            return Results.Redirect(r2.PresignGet(v.R2Key, item.Name, v.ContentType, download, DateTime.UtcNow));
        var path = DiskPath(item.Id, v.Version, item.Name);
        return File.Exists(path) ? Results.File(path, v.ContentType, download ? item.Name : null) : Results.NotFound();
    }

    public void Delete(IEnumerable<VaultFileRef> refs)
    {
        foreach (var f in refs)
        {
            try
            {
                if (f.R2Key != null && r2.IsConfigured) r2.DeleteAsync(f.R2Key).GetAwaiter().GetResult();
                var dir = Path.Combine(dataDir, "vault", f.ItemId);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { /* the DB rows are already gone; a leftover object is harmless */ }
        }
    }

    public static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".txt" or ".gea" => "text/plain; charset=utf-8",
        ".zip" => "application/zip",
        ".dwg" => "image/vnd.dwg",
        _ => "application/octet-stream",
    };
}
