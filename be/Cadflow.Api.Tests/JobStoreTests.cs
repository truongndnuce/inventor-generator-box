using System.Text.Json;

namespace Cadflow.Api.Tests;

public class JobStoreTests
{
    private static Job NewJob(JobStore s, string product = "box", string by = "tester") =>
        s.Create(product, new Dictionary<string, double> { ["BX_Width"] = 60 }, by);

    [Fact]
    public void Create_is_persisted_and_survives_a_new_store()
    {
        using var db = new TestDb();
        var id = NewJob(db.NewStore()).Id;

        var job = db.NewStore().Get(id)!; // a new store = like a backend restart
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal("tester", job.RequestedBy);
        Assert.Equal(60, job.Inputs["BX_Width"]);
        Assert.Single(job.Events);
    }

    [Fact]
    public void Get_unknown_returns_null_and_List_is_newest_first()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        Assert.Null(s.Get("nope"));
        var a = NewJob(s);
        Thread.Sleep(20);
        var b = NewJob(s);
        Assert.Equal([b.Id, a.Id], s.List().Select(j => j.Id));
    }

    [Fact]
    public void Claim_takes_oldest_queued_and_respects_product_filter()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var turbine = NewJob(s, "gas-turbine");
        Thread.Sleep(20);
        var box = NewJob(s, "box");

        var item = s.Claim("w1", ["box"])!;
        Assert.Equal(box.Id, item.Id);
        Assert.Null(s.Claim("w2", ["box"]));                 // no box job left
        Assert.Equal(turbine.Id, s.Claim("w2")!.Id);         // no filter: takes the remaining job

        var claimed = s.Get(box.Id)!;
        Assert.Equal(JobStatus.Running, claimed.Status);
        Assert.Equal("w1", claimed.Worker);
        Assert.Equal(1, claimed.Attempts);
        Assert.NotNull(claimed.StartedAt);
    }

    [Fact]
    public void Concurrent_claims_never_hand_out_the_same_job()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        for (var i = 0; i < 8; i++) NewJob(s);

        var claimed = Enumerable.Range(0, 24).AsParallel().WithDegreeOfParallelism(12)
            .Select(i => s.Claim("w" + i, ["box"])).Where(c => c != null).Select(c => c!.Id).ToList();

        Assert.Equal(8, claimed.Count);
        Assert.Equal(8, claimed.Distinct().Count());
    }

    [Fact]
    public void Only_the_owning_worker_can_progress_complete_or_fail()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var id = NewJob(s).Id;
        s.Claim("w1");

        Assert.False(s.Progress(id, "w2", "x", null));
        Assert.False(s.Complete(id, "w2", null));
        Assert.False(s.Fail(id, "w2", "x", false));

        Assert.True(s.Progress(id, "W1", "Building", "half"));   // worker name is case-insensitive
        Assert.True(s.Complete(id, "w1", new() { ["volume"] = 12.5 }));
        Assert.False(s.Progress(id, "w1", "late", null));          // a finished job can no longer be updated

        var job = s.Get(id)!;
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal("Done", job.Step);
        Assert.Equal(12.5, job.Summary["volume"]);
        Assert.NotNull(job.FinishedAt);
        Assert.Contains(job.Events, e => e.Message == "half");
        Assert.Contains(job.Events, e => e.Message == "Build finished.");
    }

    [Fact]
    public void Retryable_failure_requeues_until_max_attempts_then_fails()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var id = NewJob(s).Id;

        for (var attempt = 1; attempt < JobStore.MaxAttempts; attempt++)
        {
            Assert.NotNull(s.Claim("w1"));
            Assert.True(s.Fail(id, "w1", "boom", retryable: true));
            Assert.Equal(JobStatus.Queued, s.Get(id)!.Status);
        }
        Assert.NotNull(s.Claim("w1"));
        Assert.True(s.Fail(id, "w1", "boom", retryable: true));

        var job = s.Get(id)!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("boom", job.Error);
        Assert.Equal(JobStore.MaxAttempts, job.Attempts);
    }

    [Fact]
    public void Non_retryable_failure_fails_immediately()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var id = NewJob(s).Id;
        s.Claim("w1");
        Assert.True(s.Fail(id, "w1", "bad input", retryable: false));
        Assert.Equal(JobStatus.Failed, s.Get(id)!.Status);
    }

    [Fact]
    public void Cancel_works_on_unfinished_jobs_only()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var queued = NewJob(s).Id;
        Assert.True(s.Cancel(queued));
        Assert.Equal(JobStatus.Cancelled, s.Get(queued)!.Status);
        Assert.False(s.Cancel(queued));            // already cancelled
        Assert.False(s.Cancel("nope"));
        Assert.Null(s.Claim("w1"));                // a cancelled job is not handed out

        var done = NewJob(s).Id;
        s.Claim("w1");
        s.Complete(done, "w1", null);
        Assert.False(s.Cancel(done));
    }

    [Fact]
    public void ReapStale_requeues_then_fails_after_max_attempts()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var id = NewJob(s).Id;

        s.Claim("w1");
        Assert.Equal(0, s.ReapStale());            // the heartbeat is still fresh
        db.Sql($"UPDATE jobs SET last_heartbeat = now() - interval '10 minutes' WHERE id = '{id}'");
        Assert.Equal(1, s.ReapStale());
        Assert.Equal(JobStatus.Queued, s.Get(id)!.Status);

        for (var i = 1; i < JobStore.MaxAttempts; i++)
        {
            s.Claim("w1");
            db.Sql($"UPDATE jobs SET last_heartbeat = now() - interval '10 minutes' WHERE id = '{id}'");
            s.ReapStale();
        }
        var job = s.Get(id)!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("too many times", job.Error);
    }

    [Fact]
    public void Heartbeat_keeps_running_jobs_alive_and_tracks_workers()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var id = NewJob(s).Id;
        s.Claim("w1");
        db.Sql($"UPDATE jobs SET last_heartbeat = now() - interval '10 minutes' WHERE id = '{id}'");

        s.Heartbeat(new WorkerHello("w1", "2026", "1.0", true));
        Assert.Equal(0, s.ReapStale());
        var w = Assert.Single(s.Workers());
        Assert.True(w.Online);
        Assert.True(w.Busy);
        Assert.True(s.AnyWorkerOnline());
    }

    [Fact]
    public void Artifacts_are_upserted_and_purged_by_retention()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var id = NewJob(s).Id;
        s.Claim("w1");

        Assert.True(s.AddArtifact(id, "w1", new ArtifactInfo { Name = "a.ipt", Kind = "model", Size = 10 }));
        Assert.True(s.AddArtifact(id, "w1", new ArtifactInfo { Name = "a.ipt", Kind = "model", Size = 99, Key = "jobs/x/a.ipt" }));
        Assert.False(s.AddArtifact(id, "w2", new ArtifactInfo { Name = "b.ipt", Kind = "model", Size = 1 }));
        s.Complete(id, "w1", null);

        var art = Assert.Single(s.Get(id)!.Artifacts);
        Assert.Equal(99, art.Size);
        Assert.Equal("jobs/x/a.ipt", art.Key);

        Assert.Empty(s.ExpiredWithArtifacts(DateTimeOffset.UtcNow.AddDays(-1)));   // not past the deadline yet
        var expired = Assert.Single(s.ExpiredWithArtifacts(DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(id, expired.Id);

        s.MarkArtifactsPurged(id, 30);
        var job = s.Get(id)!;
        Assert.Empty(job.Artifacts);
        Assert.NotNull(job.ArtifactsPurgedAt);
        Assert.Empty(s.ExpiredWithArtifacts(DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public void Legacy_jobs_json_is_imported_once_and_renamed()
    {
        using var db = new TestDb();
        var legacy = new Job
        {
            Id = "AJ-LEGACY-0001", ProductCode = "box", RequestedBy = "old", Status = JobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), FinishedAt = DateTimeOffset.UtcNow.AddDays(-2),
            Inputs = { ["BX_Width"] = 50 }, Summary = { ["volume"] = 3 },
            Artifacts = { new ArtifactInfo { Name = "m.ipt", Kind = "model", Size = 5 } },
            Events = { new JobEvent { Time = DateTimeOffset.UtcNow.AddDays(-2), Message = "Build finished." } },
        };
        var json = JsonSerializer.Serialize(new[] { legacy }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        File.WriteAllText(Path.Combine(db.DataDir, "jobs.json"), json);

        var s = db.NewStore();
        var job = s.Get("AJ-LEGACY-0001")!;
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(50, job.Inputs["BX_Width"]);
        Assert.Single(job.Artifacts);
        Assert.Single(job.Events);
        Assert.False(File.Exists(Path.Combine(db.DataDir, "jobs.json")));
        Assert.True(File.Exists(Path.Combine(db.DataDir, "jobs.json.migrated")));

        Assert.Single(db.NewStore().List());       // a restart does not import twice
    }
}
