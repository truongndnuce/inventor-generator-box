using Cadflow.Api.Vault;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadflow.Api.Tests;

public sealed class VaultTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly VaultStore _vault;
    private readonly JobStore _jobs;
    private readonly JobService _service;
    private readonly VaultFiles _files;

    public VaultTests()
    {
        _vault = new VaultStore(_db.ConnectionString);
        _jobs = _db.NewStore();
        _service = new JobService(_jobs, new ScriptStore(_db.DataDir), new ConfigStore(_db.DataDir), NullLogger<JobService>.Instance);
        _files = new VaultFiles(_db.DataDir, new R2Store(new R2Options()), _jobs);
    }

    public void Dispose() => _db.Dispose();

    private PutResult Add(string folderId, string name, string content = "data", string? job = null, string by = "tester")
    {
        var temp = Path.Combine(_db.DataDir, Guid.NewGuid().ToString("N"));
        File.WriteAllText(temp, content);
        return _vault.Put(folderId, name, by, null, "application/octet-stream", job, (id, v) => _files.PlaceFile(temp, id, v, name, "application/octet-stream"));
    }

    // ---------------------------------------------------------------- folders

    [Fact]
    public void Folder_names_are_unique_per_parent_ignoring_case_and_nest()
    {
        var a = _vault.CreateFolder(null, "Project A", "me");
        var sub = _vault.CreateFolder(a.Id, "docs", "me");
        _vault.CreateFolder(null, "docs", "me");                                    // same name, other parent: fine
        Assert.Equal(409, Assert.Throws<VaultException>(() => _vault.CreateFolder(a.Id, "DOCS", "me")).Status);
        Assert.Equal(["Project A", "docs"], _vault.Breadcrumb(sub.Id).Select(f => f.Name));
        Assert.Equal(404, Assert.Throws<VaultException>(() => _vault.CreateFolder("nope", "x", "me")).Status);
    }

    [Theory]
    [InlineData("")] [InlineData("a/b")] [InlineData("..")] [InlineData("a:b")] [InlineData(" pad")]
    public void Unsafe_names_are_refused(string name)
    {
        Assert.Equal(400, Assert.Throws<VaultException>(() => _vault.CreateFolder(null, name, "me")).Status);
    }

    [Fact]
    public void Job_folder_has_image_drawing_model_bom_and_is_reused()
    {
        var (job, sub) = _vault.EnsureJobFolder("AJ-1", "box-AJ-1", "me");
        Assert.Equal(["image", "drawing", "model", "bom"], VaultStore.JobSubfolders);
        Assert.Equal(VaultStore.JobSubfolders.OrderBy(x => x), sub.Keys.OrderBy(x => x));
        Assert.All(sub.Values, f => Assert.Equal(job.Id, f.ParentId));
        var (job2, sub2) = _vault.EnsureJobFolder("AJ-1", "box-AJ-1", "me");
        Assert.Equal(job.Id, job2.Id);
        Assert.Equal(sub["model"].Id, sub2["model"].Id);
    }

    [Fact]
    public void Fixed_subfolders_cannot_be_renamed_or_deleted()
    {
        var (_, sub) = _vault.EnsureJobFolder("AJ-1", "box-AJ-1", "me");
        Assert.Equal(409, Assert.Throws<VaultException>(() => _vault.RenameFolder(sub["image"].Id, "pics")).Status);
        Assert.Equal(409, Assert.Throws<VaultException>(() => _vault.DeleteFolder(sub["image"].Id)).Status);
    }

    // ---------------------------------------------------------------- items and versions

    [Fact]
    public void New_file_is_item_version_1_in_work_with_a_number()
    {
        var f = _vault.CreateFolder(null, "p", "me");
        var r = Add(f.Id, "part.ipt");
        Assert.True(r.Created);
        Assert.Equal((1, VaultStore.InWork, "model"), (r.Item.Version, r.Item.State, r.Item.Kind));
        Assert.StartsWith("MDL-", r.Item.Number);
        Assert.Equal("MDL-", Add(f.Id, "other.stp").Item.Number[..4]);
        Assert.NotEqual(r.Item.Number, Add(f.Id, "third.ipt").Item.Number);
        Assert.StartsWith("IMG-", Add(f.Id, "a.png").Item.Number);
        Assert.StartsWith("DRW-", Add(f.Id, "a.pdf").Item.Number);
    }

    [Fact]
    public void Same_name_in_a_folder_becomes_a_new_version_and_resets_the_state()
    {
        var f = _vault.CreateFolder(null, "p", "me");
        var v1 = Add(f.Id, "part.ipt", "one").Item;
        _vault.SetState(v1.Id, VaultStore.UnderReview, "me");
        _vault.SetState(v1.Id, VaultStore.Approved, "me");

        var r = Add(f.Id, "PART.ipt", "two-two", by: "other");
        Assert.False(r.Created);
        Assert.Equal((v1.Id, 2, VaultStore.InWork, "other"), (r.Item.Id, r.Item.Version, r.Item.State, r.Item.ModifiedBy));
        Assert.Equal(7, r.Item.Size);
        Assert.Equal([2, 1], _vault.Versions(v1.Id).Select(v => v.Version));
        Assert.Single(_vault.Items(f.Id));
        Assert.Equal(3, _vault.AddVersion(v1.Id, "me", "fix", "application/octet-stream", (id, v) => { var t = Path.Combine(_db.DataDir, "t"); File.WriteAllText(t, "x"); return _files.PlaceFile(t, id, v, "part.ipt", "application/octet-stream"); }).Item.Version);
    }

    [Fact]
    public void State_follows_the_allowed_transitions()
    {
        var f = _vault.CreateFolder(null, "p", "me");
        var id = Add(f.Id, "a.ipt").Item.Id;
        Assert.Equal(409, Assert.Throws<VaultException>(() => _vault.SetState(id, VaultStore.Released, "me")).Status);   // must be approved first
        Assert.Equal(400, Assert.Throws<VaultException>(() => _vault.SetState(id, "Whatever", "me")).Status);
        _vault.SetState(id, VaultStore.UnderReview, "me");
        _vault.SetState(id, VaultStore.InWork, "me");                       // sent back
        _vault.SetState(id, VaultStore.UnderReview, "me");
        _vault.SetState(id, VaultStore.Approved, "me");
        Assert.Equal(VaultStore.Released, _vault.SetState(id, VaultStore.Released, "me").State);
        Assert.Equal(409, Assert.Throws<VaultException>(() => _vault.SetState(id, VaultStore.InWork, "me")).Status);   // released is final
        Assert.Equal(404, Assert.Throws<VaultException>(() => _vault.SetState("nope", VaultStore.InWork, "me")).Status);
    }

    [Fact]
    public void Delete_folder_removes_items_versions_and_files()
    {
        var p = _vault.CreateFolder(null, "p", "me");
        var c = _vault.CreateFolder(p.Id, "c", "me");
        var item = Add(c.Id, "a.ipt").Item;
        Add(c.Id, "a.ipt", "v2");
        var dir = Path.Combine(_db.DataDir, "vault", item.Id);
        Assert.True(Directory.Exists(dir));

        var refs = _vault.DeleteFolder(p.Id);
        Assert.Equal(2, refs.Count);
        _files.Delete(refs);
        Assert.False(Directory.Exists(dir));
        Assert.Null(_vault.Item(item.Id));
        Assert.Empty(_vault.Folders());
    }

    [Fact]
    public void Search_finds_by_name_or_number_and_filters_by_kind_and_state()
    {
        var f = _vault.CreateFolder(null, "p", "me");
        var a = Add(f.Id, "transformer-tank.ipt").Item;
        Add(f.Id, "transformer-front.png");
        Add(f.Id, "gasket.ipt");
        Assert.Equal(2, _vault.Search("transformer", null, null).Count);
        Assert.Single(_vault.Search("transformer", "image", null));
        Assert.Single(_vault.Search(a.Number, null, null));
        Assert.Empty(_vault.Search("transformer", null, VaultStore.Approved));
        Assert.Equal(3, _vault.Search(null, null, null).Count);
        Assert.Empty(_vault.Search("100%", null, null));           // % is literal, not a wildcard
    }

    // ---------------------------------------------------------------- from a job

    private Job FinishedJob()
    {
        var job = _service.Create(new CreateJobRequest("transformer", null, "tester")).Job!;
        Assert.Equal(job.Id, _jobs.Claim("w")!.Id);
        foreach (var name in new[] { job.Id + ".ipt", job.Id + ".stp", job.Id + ".png", job.Id + "_top.png", job.Id + ".pdf", job.Id + ".dwg", "notes.bin" })
        {
            Directory.CreateDirectory(_jobs.ArtifactDir(job.Id));
            File.WriteAllText(Path.Combine(_jobs.ArtifactDir(job.Id), name), "content of " + name);
            Assert.True(_jobs.AddArtifact(job.Id, "w", new ArtifactInfo { Name = name, Kind = Path.GetExtension(name).TrimStart('.'), Size = 5 }));
        }
        Assert.True(_jobs.Complete(job.Id, "w", new Dictionary<string, double>()));
        return _jobs.Get(job.Id)!;
    }

    [Fact]
    public void Saving_a_job_files_images_drawings_and_models_into_its_folder_and_copies_the_bytes()
    {
        var job = FinishedJob();
        var (folder, sub) = _vault.EnsureJobFolder(job.Id, $"{job.ProductCode}-{job.Id}", "me");
        foreach (var a in job.Artifacts)
            if (sub.TryGetValue(VaultStore.KindOf(a.Name), out var target))
                Add(target.Id, a.Name, "copy", job.Id);

        Assert.Equal(2, _vault.Items(sub["image"].Id).Count);        // iso + top section
        Assert.Equal(2, _vault.Items(sub["drawing"].Id).Count);      // pdf + dwg
        Assert.Equal(2, _vault.Items(sub["model"].Id).Count);        // ipt + stp
        Assert.Equal($"transformer-{job.Id}", folder.Name);
    }

    [Theory]
    [InlineData("AJ-1_bom.csv", "bom")] [InlineData("AJ-1_BOM.CSV", "bom")] [InlineData("data.csv", "other")]
    [InlineData("a.ipt", "model")] [InlineData("a.stp", "model")] [InlineData("a.pdf", "drawing")] [InlineData("a_front.png", "image")]
    public void KindOf_recognises_the_job_bom_but_not_any_csv(string name, string kind) => Assert.Equal(kind, VaultStore.KindOf(name));

    // ---------------------------------------------------------------- rename

    private string DiskFile(VaultItem item, int version) => Path.Combine(_db.DataDir, "vault", item.Id, "v" + version, item.Name);

    [Fact]
    public void Renaming_a_file_moves_every_version_and_keeps_number_state_and_version()
    {
        var f = _vault.CreateFolder(null, "p", "me");
        var item = Add(f.Id, "old.ipt", "v1").Item;
        Add(f.Id, "old.ipt", "v2");
        var before = _vault.Item(item.Id)!;

        var renamed = _vault.RenameItem(item.Id, "new.ipt", "bob", (from, to) => _files.RenameOnDisk(item.Id, from, to));

        Assert.Equal("new.ipt", renamed.Name);
        Assert.Equal((before.Number, before.Version, before.State, before.Kind), (renamed.Number, renamed.Version, renamed.State, renamed.Kind));
        Assert.Equal("bob", renamed.ModifiedBy);
        Assert.Equal("v1", File.ReadAllText(DiskFile(renamed, 1)));
        Assert.Equal("v2", File.ReadAllText(DiskFile(renamed, 2)));
        Assert.False(File.Exists(Path.Combine(_db.DataDir, "vault", item.Id, "v1", "old.ipt")));
        Add(f.Id, "new.ipt", "v3");                                                   // a new check-in still lands on the same item
        Assert.Equal(3, _vault.Item(item.Id)!.Version);
    }

    [Fact]
    public void Renaming_a_file_is_refused_for_a_taken_name_a_new_extension_or_an_unsafe_name_and_changes_nothing()
    {
        var f = _vault.CreateFolder(null, "p", "me");
        var a = Add(f.Id, "a.ipt", "A").Item;
        Add(f.Id, "b.ipt", "B");
        void Move(string from, string to) => _files.RenameOnDisk(a.Id, from, to);

        Assert.Equal(409, Assert.Throws<VaultException>(() => _vault.RenameItem(a.Id, "B.IPT", "me", Move)).Status);   // taken, ignoring case
        Assert.Equal(400, Assert.Throws<VaultException>(() => _vault.RenameItem(a.Id, "a.stp", "me", Move)).Status);   // extension kept
        Assert.Equal(400, Assert.Throws<VaultException>(() => _vault.RenameItem(a.Id, "x/y.ipt", "me", Move)).Status);
        Assert.Equal(404, Assert.Throws<VaultException>(() => _vault.RenameItem("nope", "z.ipt", "me", Move)).Status);
        Assert.Equal("a.ipt", _vault.Item(a.Id)!.Name);
        Assert.Equal("A", File.ReadAllText(DiskFile(a, 1)));
    }

    [Fact]
    public void A_failed_file_move_rolls_the_new_name_back()
    {
        var f = _vault.CreateFolder(null, "p", "me");
        var a = Add(f.Id, "a.ipt", "A").Item;
        Assert.Throws<IOException>(() => _vault.RenameItem(a.Id, "c.ipt", "me", (_, _) => throw new IOException("disk")));
        Assert.Equal("a.ipt", _vault.Item(a.Id)!.Name);
    }

    [Fact]
    public void Renaming_a_folder_keeps_its_content_and_fixed_job_folders_stay_fixed()
    {
        var f = _vault.CreateFolder(null, "old", "me");
        var item = Add(f.Id, "x.ipt").Item;
        Assert.Equal("new", _vault.RenameFolder(f.Id, "new").Name);
        Assert.Equal(item.Id, Assert.Single(_vault.Items(f.Id)).Id);
        var (_, sub) = _vault.EnsureJobFolder("AJ-9", "box-AJ-9", "me");
        Assert.Equal(409, Assert.Throws<VaultException>(() => _vault.RenameFolder(sub["bom"].Id, "x")).Status);
    }

    [Fact]
    public void JobSaver_puts_model_drawing_image_and_bom_files_in_their_folders_and_is_idempotent()
    {
        var job = FinishedJob();
        var bom = job.Id + "_bom.csv";
        File.WriteAllText(Path.Combine(_jobs.ArtifactDir(job.Id), bom), "part,qty\nbox,1\n");
        // The job in the DB must list the BOM too: re-read after registering it (the job is already complete, so add it straight to the object).
        job.Artifacts.Add(new ArtifactInfo { Name = bom, Kind = "bom", Size = 5 });

        var saver = new VaultJobSaver(_vault, _files);
        var first = saver.Save(job, "me");
        Assert.Equal(7, first.Added);                          // ipt, stp, 2 png, pdf, dwg, bom
        Assert.Equal(["notes.bin"], first.Skipped);            // unknown extension: no sub-folder for it
        var (_, sub) = _vault.EnsureJobFolder(job.Id, $"{job.ProductCode}-{job.Id}", "me");
        Assert.Equal(bom, Assert.Single(_vault.Items(sub["bom"].Id)).Name);
        Assert.StartsWith("BOM-", _vault.Items(sub["bom"].Id)[0].Number);

        var second = saver.Save(job, "me");
        Assert.Equal((0, 0, 7), (second.Added, second.NewVersions, second.Unchanged));
    }

    [Fact]
    public void CopyFromJob_copies_so_retention_of_the_job_cannot_remove_the_vault_file()
    {
        var job = FinishedJob();
        var art = job.Artifacts.First(a => a.Name.EndsWith(".ipt"));
        var f = _vault.CreateFolder(null, "p", "me");
        var r = _vault.Put(f.Id, art.Name, "me", null, "application/octet-stream", job.Id, (id, v) => _files.CopyFromJob(job, art, id, v));

        Directory.Delete(_jobs.ArtifactDir(job.Id), true);            // what retention does
        var (item, ver) = _vault.Version(r.Item.Id, 1)!.Value;
        Assert.Equal("content of " + art.Name, File.ReadAllText(Path.Combine(_db.DataDir, "vault", item.Id, "v1", item.Name)));
        Assert.Null(ver.R2Key);
    }

    [Fact]
    public void Saving_the_same_job_twice_is_a_no_op_but_a_different_job_adds_a_version()
    {
        var f = _vault.CreateFolder(null, "p", "me");
        Assert.True(Add(f.Id, "x.ipt", "a", job: "AJ-1").Created);
        var again = Add(f.Id, "x.ipt", "a", job: "AJ-1");
        Assert.True(again.Unchanged);
        Assert.Equal(1, again.Item.Version);
        Assert.Equal(2, Add(f.Id, "x.ipt", "b", job: "AJ-2").Item.Version);
    }

    [Fact]
    public void Missing_job_file_is_reported_as_gone()
    {
        var job = FinishedJob();
        var art = job.Artifacts.First();
        File.Delete(Path.Combine(_jobs.ArtifactDir(job.Id), art.Name));
        var f = _vault.CreateFolder(null, "p", "me");
        Assert.Equal(410, Assert.Throws<VaultException>(() => _vault.Put(f.Id, art.Name, "me", null, "x", job.Id, (id, v) => _files.CopyFromJob(job, art, id, v))).Status);
        Assert.Empty(_vault.Items(f.Id));                              // the transaction rolled back, no half item
    }

    [Fact]
    public void Existing_database_start_is_idempotent()
    {
        _ = new VaultStore(_db.ConnectionString);
        _ = new VaultStore(_db.ConnectionString);
    }
}
