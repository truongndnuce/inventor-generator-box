using Microsoft.Extensions.Logging.Abstractions;

namespace Cadflow.Api.Tests;

/// <summary>Job kinds, rebuild, queue depth and queue position (the server side of the SDS-style queues).</summary>
public sealed class QueueApiTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly JobStore _store;
    private readonly JobService _jobs;

    public QueueApiTests()
    {
        _store = _db.NewStore();
        _jobs = new JobService(_store, new ScriptStore(_db.DataDir), new ConfigStore(_db.DataDir), NullLogger<JobService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private Job Order(string product = "box", string? kind = null)
    {
        Thread.Sleep(15);
        var r = _jobs.Create(new CreateJobRequest(product, null, "tester", Kind: kind));
        Assert.True(r.Ok, r.Error?.ToString());
        return r.Job!;
    }

    [Fact]
    public void Default_order_is_a_generate_job_in_the_build_queue()
    {
        var j = Order();
        Assert.Equal((JobKinds.Generate, JobQueues.Build, 20), (j.Kind, j.Queue, j.Priority));
    }

    [Fact]
    public void Preview_order_goes_to_the_filter_queue_with_priority_zero()
    {
        var j = Order(kind: "Preview");                       // case does not matter
        Assert.Equal((JobKinds.Preview, JobQueues.Filter, 0), (j.Kind, j.Queue, j.Priority));
        Assert.Equal((JobKinds.Preview, JobQueues.Filter, 0), (_store.Get(j.Id)!.Kind, _store.Get(j.Id)!.Queue, _store.Get(j.Id)!.Priority));
    }

    [Theory]
    [InlineData("export")]       // only Rebuild creates exports
    [InlineData("nonsense")]
    public void A_user_cannot_order_an_unknown_or_export_kind(string kind)
    {
        var r = _jobs.Create(new CreateJobRequest("box", null, "tester", Kind: kind));
        Assert.False(r.Ok);
        Assert.Equal(400, r.Status);
        Assert.Empty(_store.List());
    }

    [Fact]
    public void A_preview_is_handed_out_before_older_generate_jobs_to_a_worker_with_no_queue_order()
    {
        var gen = Order();
        var prev = Order(kind: "preview");
        Assert.Equal(prev.Id, _store.Claim("w")!.Id);
        Assert.Equal(gen.Id, _store.Claim("w")!.Id);
    }

    // ---------------------------------------------------------------- rebuild

    private Job Finished(string product = "transformer")
    {
        var job = Order(product);
        var item = _store.Claim("w1")!;
        Assert.Equal(job.Id, item.Id);
        Assert.True(_store.Complete(job.Id, "w1", new Dictionary<string, double>()));
        return _store.Get(job.Id)!;
    }

    [Fact]
    public void Rebuild_queues_an_export_job_with_the_same_script_and_values()
    {
        var src = Finished();
        Assert.NotNull(src.ScriptSha256);

        var r = _jobs.Rebuild(src.Id, "someone");
        Assert.True(r.Ok, r.Error?.ToString());
        var job = _store.Get(r.Job!.Id)!;
        Assert.Equal((JobKinds.Export, JobQueues.Build, 20, src.Id), (job.Kind, job.Queue, job.Priority, job.SourceJobId));
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(src.ScriptSha256, job.ScriptSha256);
        Assert.Equal(src.ScriptName, job.ScriptName);
        Assert.Equal(src.Inputs, job.Inputs);
        Assert.Equal("someone", job.RequestedBy);

        Assert.Equal(JobKinds.Export, _store.Claim("w2")!.Kind);   // the worker is told it is an export
    }

    [Fact]
    public void Rebuild_without_a_name_keeps_the_original_requester()
    {
        var src = Finished();
        Assert.Equal("tester", _store.Get(_jobs.Rebuild(src.Id, null).Job!.Id)!.RequestedBy);
    }

    [Fact]
    public void Rebuild_refuses_unknown_waiting_and_running_jobs()
    {
        Assert.Equal(404, _jobs.Rebuild("AJ-nope", null).Status);

        var queued = Order();
        Assert.Equal(409, _jobs.Rebuild(queued.Id, null).Status);

        _store.Claim("w1");
        Assert.Equal(409, _jobs.Rebuild(queued.Id, null).Status);   // now running
    }

    [Fact]
    public void Rebuild_refuses_when_the_stored_script_is_gone()
    {
        var src = Finished();
        File.Delete(Directory.GetFiles(Path.Combine(_db.DataDir, "scripts")).Single(f => f.Contains(src.ScriptSha256!)));
        Assert.Equal(410, _jobs.Rebuild(src.Id, null).Status);
    }

    // ---------------------------------------------------------------- queue depth and position

    [Fact]
    public void QueueStats_lists_both_queues_with_depth_wait_and_workers()
    {
        var empty = _store.QueueStats();
        Assert.Equal([JobQueues.Build, JobQueues.Filter], empty.Select(q => q.Queue));
        Assert.All(empty, q => Assert.Equal((0, 0, (double?)null, 0), (q.Queued, q.Running, q.OldestWaitSeconds, q.OnlineWorkers)));

        Order(); Order(); Order(kind: "preview");
        _store.Claim("builder", queues: [JobQueues.Build]);
        _store.Heartbeat(new WorkerHello("builder", null, null, true));
        _store.Claim("anyone");                                   // takes the preview, no queue order = takes any queue
        _store.Heartbeat(new WorkerHello("anyone", null, null, true));

        var stats = _store.QueueStats().ToDictionary(q => q.Queue);
        Assert.Equal((1, 1), (stats[JobQueues.Build].Queued, stats[JobQueues.Build].Running));
        Assert.Equal((0, 1), (stats[JobQueues.Filter].Queued, stats[JobQueues.Filter].Running));
        Assert.NotNull(stats[JobQueues.Build].OldestWaitSeconds);
        Assert.Null(stats[JobQueues.Filter].OldestWaitSeconds);
        Assert.Equal(2, stats[JobQueues.Build].OnlineWorkers);    // builder + anyone
        Assert.Equal(1, stats[JobQueues.Filter].OnlineWorkers);   // only anyone
    }

    [Fact]
    public void QueuePosition_counts_the_jobs_ahead_in_the_same_queue_only()
    {
        var a = Order();
        var b = Order();
        var prev = Order(kind: "preview");
        var c = Order();

        Assert.Equal(0, _store.QueuePosition(a.Id));
        Assert.Equal(1, _store.QueuePosition(b.Id));
        Assert.Equal(2, _store.QueuePosition(c.Id));
        Assert.Equal(0, _store.QueuePosition(prev.Id));           // other queue: not behind the build jobs

        _store.Claim("w", queues: [JobQueues.Build]);             // takes a
        Assert.Null(_store.QueuePosition(a.Id));                  // no longer queued
        Assert.Equal(0, _store.QueuePosition(b.Id));
    }
}
