namespace Cadflow.Api.Vault;

public sealed record JobSaveResult(VaultFolder Folder, int Added, int NewVersions, int Unchanged, List<string> Skipped);

/// <summary>
/// Copies the result files of a finished job into its vault folder (<c>&lt;product&gt;-&lt;job id&gt;</c> with image / drawing / model / bom sub-folders).
/// Used by the "Save to Smart Vault" button and, automatically, when a worker completes a job. Saving the same job twice adds nothing.
/// </summary>
public sealed class VaultJobSaver(VaultStore vault, VaultFiles files)
{
    public JobSaveResult Save(Job job, string? by)
    {
        var who = by ?? job.RequestedBy;
        var (folder, sub) = vault.EnsureJobFolder(job.Id, $"{job.ProductCode}-{job.Id}", who);
        int added = 0, versions = 0, unchanged = 0;
        var skipped = new List<string>();
        foreach (var a in job.Artifacts)
        {
            var kind = VaultStore.KindOf(a.Name);
            if (!sub.TryGetValue(kind, out var target)) { skipped.Add(a.Name); continue; }
            var r = vault.Put(target.Id, a.Name, who, $"From job {job.Id}", VaultFiles.ContentTypeFor(a.Name), job.Id,
                (itemId, v) => files.CopyFromJob(job, a, itemId, v));
            if (r.Unchanged) unchanged++; else if (r.Created) added++; else versions++;
        }
        return new JobSaveResult(folder, added, versions, unchanged, skipped);
    }
}
