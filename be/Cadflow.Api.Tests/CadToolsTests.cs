using System.Text.Json;
using Cadflow.Api.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadflow.Api.Tests;

public sealed class CadToolsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly JobStore _store;
    private readonly CadTools _tools;

    public CadToolsTests()
    {
        _store = _db.NewStore();
        var configs = new ConfigStore(_db.DataDir);
        var jobs = new JobService(_store, new ScriptStore(_db.DataDir), configs, NullLogger<JobService>.Instance);
        _tools = new CadTools(_store, configs, jobs, new DraftStore());
    }

    public void Dispose() => _db.Dispose();

    private static readonly Dictionary<string, double> Turbine8 = new() { ["GT_CanCount"] = 8, ["GT_ExhLength"] = 1500 };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ListProducts_has_the_six_built_in_products()
    {
        var codes = Parse(_tools.ListProducts()).EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Contains("box", codes);
        Assert.Contains("gas-turbine", codes);
        Assert.Contains("circuit-breaker", codes);
        Assert.Contains("disconnector", codes);
        Assert.Equal(6, codes.Count);
    }

    [Fact]
    public void GetProductSchema_unknown_product_returns_an_error()
    {
        Assert.True(Parse(_tools.GetProductSchema("nope")).TryGetProperty("error", out _));
    }

    [Fact]
    public void ValidateJob_reports_cross_rule_violations_without_creating_anything()
    {
        var r = Parse(_tools.ValidateJob("gas-turbine", new() { ["GT_CanCount"] = 6 }));
        Assert.False(r.GetProperty("valid").GetBoolean());
        Assert.Contains("multiple of 4", r.GetProperty("issues").ToString());
        Assert.Empty(_store.List());
    }

    [Fact]
    public void ValidateJob_accepts_good_values_and_flags_unknown_parameters()
    {
        var r = Parse(_tools.ValidateJob("gas-turbine", new(Turbine8) { ["NOT_A_PARAM"] = 1 }));
        Assert.True(r.GetProperty("valid").GetBoolean());
        Assert.Equal("NOT_A_PARAM", r.GetProperty("unknownParameters")[0].GetString());
        Assert.Equal(1500, r.GetProperty("values").GetProperty("GT_ExhLength").GetDouble());
    }

    [Fact]
    public void ProposeJob_saves_a_draft_but_does_not_enqueue()
    {
        var (json, draft) = _tools.ProposeJob("gas-turbine", Turbine8, null, null, null, "tester");
        Assert.NotNull(draft);
        Assert.True(Parse(json).GetProperty("drafted").GetBoolean());
        Assert.Empty(_store.List());
    }

    [Fact]
    public void ProposeJob_with_invalid_values_makes_no_draft()
    {
        var (json, draft) = _tools.ProposeJob("gas-turbine", new() { ["GT_CanCount"] = 6 }, null, null, null, "tester");
        Assert.Null(draft);
        Assert.False(Parse(json).GetProperty("drafted").GetBoolean());
    }

    [Fact]
    public void Confirm_creates_the_job_once_and_only_once()
    {
        var (_, draft) = _tools.ProposeJob("gas-turbine", Turbine8, null, null, null, "tester");

        var first = _tools.Confirm(draft!.Id, "alice");
        Assert.True(first.Ok);
        var job = Assert.Single(_store.List());
        Assert.Equal("alice", job.RequestedBy);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(8, job.Inputs["GT_CanCount"]);

        var second = _tools.Confirm(draft.Id, "alice");
        Assert.False(second.Ok);
        Assert.Equal(404, second.Status);
        Assert.Single(_store.List());
    }

    [Fact]
    public void Confirm_unknown_draft_is_404()
    {
        Assert.Equal(404, _tools.Confirm("missing", null).Status);
    }

    [Fact]
    public void CreateNow_validates_again_on_the_server()
    {
        var bad = _tools.CreateNow("gas-turbine", new() { ["GT_CanCount"] = 6 }, null, null, null, "tester");
        Assert.False(bad.Ok);
        Assert.Empty(_store.List());

        var good = _tools.CreateNow("gas-turbine", Turbine8, null, null, null, "tester");
        Assert.True(good.Ok);
        Assert.Equal("gas-turbine", Assert.Single(_store.List()).ProductCode);
    }

    [Fact]
    public void GetJob_ListJobs_and_CancelJob_work_through_the_tools()
    {
        var id = _tools.CreateNow("box", null, null, null, null, "tester").Job!.Id;

        Assert.Equal("queued", Parse(_tools.GetJob(id)).GetProperty("status").GetString());
        Assert.True(Parse(_tools.GetJob("nope")).TryGetProperty("error", out _));
        Assert.Single(Parse(_tools.ListJobs(status: "queued")).EnumerateArray());
        Assert.Empty(Parse(_tools.ListJobs(status: "failed")).EnumerateArray());
        Assert.True(Parse(_tools.ListJobs(status: "bogus")).TryGetProperty("error", out _));

        Assert.True(Parse(_tools.CancelJob(id)).GetProperty("cancelled").GetBoolean());
        Assert.True(Parse(_tools.CancelJob(id)).TryGetProperty("error", out _));
        Assert.Equal("cancelled", Parse(_tools.GetJob(id)).GetProperty("status").GetString());
    }
}
