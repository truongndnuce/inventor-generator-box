using Microsoft.Extensions.Configuration;

namespace Cadflow.Api.Tests;

public class QueueTests
{
    private static Job NewJob(JobStore s, string kind = JobKinds.Generate, int? priority = null, string product = "box", string? queue = null)
    {
        var (q, p) = new QueuePolicy().Resolve(kind);
        Thread.Sleep(15); // distinct created_at so FIFO order is deterministic
        return s.Create(product, new Dictionary<string, double> { ["BX_Width"] = 60 }, "tester",
            kind: kind, queue: queue ?? q, priority: priority ?? p);
    }

    [Fact]
    public void Lower_priority_number_goes_first_even_when_created_later()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var slow = NewJob(s, priority: 50);
        var urgent = NewJob(s, priority: 5);
        var normal = NewJob(s, priority: 20);

        Assert.Equal(urgent.Id, s.Claim("w")!.Id);
        Assert.Equal(normal.Id, s.Claim("w")!.Id);
        Assert.Equal(slow.Id, s.Claim("w")!.Id);
    }

    [Fact]
    public void Same_priority_is_first_in_first_out()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var a = NewJob(s);
        var b = NewJob(s);
        Assert.Equal(a.Id, s.Claim("w")!.Id);
        Assert.Equal(b.Id, s.Claim("w")!.Id);
    }

    [Fact]
    public void Worker_takes_queues_in_its_own_order()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var gen = NewJob(s, JobKinds.Generate);
        var prev = NewJob(s, JobKinds.Preview);

        // A "build first" node picks the build job although the preview is more urgent; a "filter first" node does the opposite.
        Assert.Equal(gen.Id, s.Claim("node1", queues: [JobQueues.Build, JobQueues.Filter])!.Id);
        Assert.Equal(prev.Id, s.Claim("node1", queues: [JobQueues.Build, JobQueues.Filter])!.Id);

        var gen2 = NewJob(s, JobKinds.Generate);
        var prev2 = NewJob(s, JobKinds.Preview);
        Assert.Equal(prev2.Id, s.Claim("node3", queues: [JobQueues.Filter, JobQueues.Build])!.Id);
        Assert.Equal(gen2.Id, s.Claim("node3", queues: [JobQueues.Filter, JobQueues.Build])!.Id);
    }

    [Fact]
    public void Worker_moves_on_to_the_next_queue_when_its_first_is_empty()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var gen = NewJob(s, JobKinds.Generate);
        Assert.Equal(gen.Id, s.Claim("w", queues: [JobQueues.Filter, JobQueues.Build])!.Id);
    }

    [Fact]
    public void Worker_never_takes_a_queue_it_did_not_list()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        NewJob(s, JobKinds.Generate);
        Assert.Null(s.Claim("previewer", queues: [JobQueues.Filter]));
        Assert.Equal(JobStatus.Queued, s.List().Single().Status);
    }

    [Fact]
    public void No_queues_means_any_queue_sorted_by_priority()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var gen = NewJob(s, JobKinds.Generate);
        var prev = NewJob(s, JobKinds.Preview);
        Assert.Equal(prev.Id, s.Claim("old-addin")!.Id);   // preview has priority 0
        Assert.Equal(gen.Id, s.Claim("old-addin", queues: [])!.Id);
    }

    [Fact]
    public void Product_filter_and_queue_order_work_together()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var turbinePreview = NewJob(s, JobKinds.Preview, product: "gas-turbine");
        var boxGen = NewJob(s, JobKinds.Generate, product: "box");

        var item = s.Claim("box-addin", ["box"], [JobQueues.Filter, JobQueues.Build])!;
        Assert.Equal(boxGen.Id, item.Id);                  // the preview is a turbine job, not ours
        Assert.Equal(JobKinds.Generate, item.Kind);
        Assert.Equal(turbinePreview.Id, s.Claim("turbine-addin", ["gas-turbine"], [JobQueues.Filter])!.Id);
    }

    [Fact]
    public void Claim_hands_the_kind_to_the_worker()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        NewJob(s, JobKinds.Preview);
        Assert.Equal(JobKinds.Preview, s.Claim("w")!.Kind);
    }

    [Fact]
    public void Concurrent_claims_across_queues_never_hand_out_the_same_job()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var ids = Enumerable.Range(0, 12).Select(i => NewJob(s, i % 2 == 0 ? JobKinds.Generate : JobKinds.Preview).Id).ToHashSet();

        var taken = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 24, i =>
        {
            var order = i % 2 == 0 ? new[] { JobQueues.Build, JobQueues.Filter } : new[] { JobQueues.Filter, JobQueues.Build };
            if (s.Claim("w" + (i % 4), queues: order) is { } item) taken.Add(item.Id);
        });

        Assert.Equal(ids.Count, taken.Count);
        Assert.Equal(ids, taken.ToHashSet());
    }

    [Fact]
    public void A_retried_job_keeps_its_kind_queue_and_priority()
    {
        using var db = new TestDb();
        var s = db.NewStore();
        var urgent = NewJob(s, JobKinds.Preview, priority: 3);
        NewJob(s, JobKinds.Preview, priority: 3);          // created later, same priority

        var item = s.Claim("w1", queues: [JobQueues.Filter])!;
        Assert.Equal(urgent.Id, item.Id);
        Assert.True(s.Fail(item.Id, "w1", "COM error", retryable: true));

        var back = s.Get(urgent.Id)!;
        Assert.Equal((JobStatus.Queued, JobKinds.Preview, JobQueues.Filter, 3), (back.Status, back.Kind, back.Queue, back.Priority));
        Assert.Equal(urgent.Id, s.Claim("w2", queues: [JobQueues.Filter])!.Id); // still first: oldest in its priority
    }

    [Fact]
    public void Existing_database_is_migrated_and_old_jobs_become_generate_build_20()
    {
        using var db = new TestDb();
        // The jobs table as it was before queues existed.
        db.Sql("""
            CREATE TABLE jobs (
                id text PRIMARY KEY, product_code text NOT NULL, product_version int NOT NULL DEFAULT 1, status text NOT NULL,
                step text, error text, requested_by text NOT NULL DEFAULT '', created_at timestamptz NOT NULL, started_at timestamptz,
                finished_at timestamptz, last_heartbeat timestamptz, worker text, attempts int NOT NULL DEFAULT 0,
                inputs jsonb NOT NULL DEFAULT '{}', text_inputs jsonb NOT NULL DEFAULT '{}', summary jsonb NOT NULL DEFAULT '{}',
                script_name text, script_sha256 text, script_source text, artifacts_purged_at timestamptz);
            CREATE INDEX ix_jobs_status_created ON jobs (status, created_at);
            INSERT INTO jobs (id, product_code, status, created_at) VALUES ('AJ-old', 'box', 'queued', now());
            """);

        var s = db.NewStore();                                  // migrates
        var old = s.Get("AJ-old")!;
        Assert.Equal((JobKinds.Generate, JobQueues.Build, 20, (string?)null), (old.Kind, old.Queue, old.Priority, old.SourceJobId));
        Assert.Equal("AJ-old", s.Claim("w", queues: [JobQueues.Build])!.Id);

        db.NewStore();                                          // a second start must not fail on the existing columns
    }

    // ---------------------------------------------------------------- QueuePolicy

    [Fact]
    public void Policy_defaults_follow_SDS_preview_is_urgent_in_the_filter_queue()
    {
        var p = new QueuePolicy();
        Assert.Equal((JobQueues.Filter, 0), p.Resolve(JobKinds.Preview));
        Assert.Equal((JobQueues.Build, 20), p.Resolve(JobKinds.Generate));
        Assert.Equal((JobQueues.Build, 20), p.Resolve(JobKinds.Export));
        Assert.Equal((JobQueues.Build, 20), p.Resolve("nonsense"));
        Assert.Equal((JobQueues.Build, 20), p.Resolve(null));
    }

    [Fact]
    public void Policy_can_be_overridden_by_configuration_and_is_clamped()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queue:Kinds:export:Priority"] = "90",
            ["Queue:Kinds:preview:Priority"] = "-5",
            ["Queue:Kinds:generate:Queue"] = "nonexistent",   // ignored, the default stays
            ["Queue:Kinds:generate:Priority"] = "500",
        }).Build();
        var p = new QueuePolicy(config);
        Assert.Equal((JobQueues.Build, 90), p.Resolve(JobKinds.Export));
        Assert.Equal((JobQueues.Filter, 0), p.Resolve(JobKinds.Preview));
        Assert.Equal((JobQueues.Build, 100), p.Resolve(JobKinds.Generate));
    }
}
