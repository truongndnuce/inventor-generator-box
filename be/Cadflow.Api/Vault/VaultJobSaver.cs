namespace Cadflow.Api.Vault;

public sealed record JobSaveResult(VaultFolder Folder, int Added, int NewVersions, int Unchanged, List<string> Skipped, List<VaultItem>? Models = null);

/// <summary>
/// Copies the result files of a finished job into its vault folder (<c>&lt;product&gt;-&lt;job id&gt;</c> with image / drawing / model / bom sub-folders).
/// Used by the "Save to Smart Vault" button and, automatically, when a worker completes a job. Saving the same job twice adds nothing.
/// </summary>
public sealed class VaultJobSaver(VaultStore vault, VaultFiles files, ApsClient? aps = null)
{
    /// <summary>
    /// Sends the models that <see cref="Save"/> just added (or gave a new version) to Autodesk to be prepared for the 3D viewer,
    /// so they open instantly later. Does nothing when Autodesk is not configured. One failing file does not stop the others.
    /// </summary>
    public async Task PrepareViewsAsync(JobSaveResult saved, ILogger log, CancellationToken ct = default)
    {
        if (aps is not { IsConfigured: true } || saved.Models is not { Count: > 0 }) return;
        foreach (var item in saved.Models)
        {
            try
            {
                var v = vault.Version(item.Id, item.Version);
                if (v is null) continue;
                var (path, temp) = await files.LocalCopyAsync(item, v.Value.Version, ct);
                try { await aps.TranslateAsync(item.Id, item.Version, item.Name, path, ct); }
                finally { if (temp) try { File.Delete(path); } catch { } }
                log.LogInformation("Sent {Name} v{Version} to Autodesk for 3D viewing.", item.Name, item.Version);
            }
            catch (Exception ex) { log.LogWarning(ex, "Could not prepare the 3D view of {Name}.", item.Name); }
        }
    }

    public JobSaveResult Save(Job job, string? by)
    {
        var who = by ?? job.RequestedBy;
        var (folder, sub) = vault.EnsureJobFolder(job.Id, $"{job.ProductCode}-{job.Id}", who);
        int added = 0, versions = 0, unchanged = 0;
        var skipped = new List<string>();
        var models = new List<VaultItem>();
        foreach (var a in job.Artifacts)
        {
            var kind = VaultStore.KindOf(a.Name);
            if (!sub.TryGetValue(kind, out var target)) { skipped.Add(a.Name); continue; }
            var r = vault.Put(target.Id, a.Name, who, $"From job {job.Id}", VaultFiles.ContentTypeFor(a.Name), job.Id,
                (itemId, v) => files.CopyFromJob(job, a, itemId, v));
            if (r.Unchanged) unchanged++; else if (r.Created) added++; else versions++;
            if (!r.Unchanged && VaultStore.IsForgeViewable(a.Name)) models.Add(r.Item);
        }
        return new JobSaveResult(folder, added, versions, unchanged, skipped, models);
    }
}
