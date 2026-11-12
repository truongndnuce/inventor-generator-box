using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadflow.Api.Tests;

/// <summary>The server expands INCLUDE lib: when a script is imported and when a job is created, and stores/sends the expanded copy to the worker.</summary>
public sealed class ServerIncludeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly JobStore _store;
    private readonly ScriptStore _scripts;
    private readonly JobService _jobs;

    public ServerIncludeTests()
    {
        _store = _db.NewStore();
        _scripts = new ScriptStore(_db.DataDir);
        _jobs = new JobService(_store, _scripts, new ConfigStore(_db.DataDir), NullLogger<JobService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private const string Reactor = """
        GEA_VERSION 1
        PRODUCT_NAME "Reactor"
        PARAM_PREFIX GT_
        TARGET PART
        BEGIN_FORM
          INCLUDE lib:tank-base-cover
          USER_INPUT_PARAM GT_Pipes INTEGER 2 MIN 0 MAX 10 LABEL "Pipes"
        END_FORM
        BEGIN_BUILD
          INCLUDE lib:tank-base-cover
        END_BUILD
        """;

    // A library comment may mention the word INCLUDE; what must hold is that no INCLUDE lib: statement line remains.
    private static void AssertNoIncludeDirective(string text) =>
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"^\s*INCLUDE\s+lib:", System.Text.RegularExpressions.RegexOptions.Multiline), text);

    private static JsonElement Json(object body) => JsonSerializer.SerializeToElement(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Import_expands_the_library_and_the_form_has_its_parameters()
    {
        var r = _jobs.Import(Reactor);

        Assert.Equal(200, r.Status);
        var body = Json(r.Body);
        var form = body.GetProperty("form").ToString();
        Assert.Contains("TR_TankLength", form);     // parameter from the library
        Assert.Contains("GT_Pipes", form);          // parameter of the parent script

        var stored = _scripts.Get(body.GetProperty("scriptId").GetString())!;
        AssertNoIncludeDirective(stored);
        Assert.Contains("SET_REF_NAME tbcBody \"TR_Tank\"", stored);
    }

    [Fact]
    public void Imported_script_with_include_can_become_a_job_and_the_worker_gets_expanded_text()
    {
        var scriptId = Json(_jobs.Import(Reactor).Body).GetProperty("scriptId").GetString()!;

        var created = _jobs.Create(new CreateJobRequest(null, new() { ["TR_TankLength"] = 2500 }, "tester", ScriptId: scriptId));

        Assert.True(created.Ok, created.Error?.ToString());
        var job = _store.Get(created.Job!.Id)!;
        Assert.Equal(2500, job.Inputs["TR_TankLength"]);
        AssertNoIncludeDirective(_scripts.Get(job.ScriptSha256)!);
    }

    [Fact]
    public void Built_in_transformer_job_stores_the_expanded_script()
    {
        var created = _jobs.Create(new CreateJobRequest("transformer", null, "tester"));

        Assert.True(created.Ok, created.Error?.ToString());
        var text = _scripts.Get(_store.Get(created.Job!.Id)!.ScriptSha256)!;
        AssertNoIncludeDirective(text);
        Assert.Contains("USER_INPUT_PARAM TR_TankLength", text);
        Assert.Contains("CREATE_FEATURE builder:Bushing", text);   // the parent script part is still there
    }

    [Fact]
    public void Import_of_an_unknown_library_is_a_400_with_diagnostics()
    {
        var r = _jobs.Import(Reactor.Replace("lib:tank-base-cover", "lib:no-such-lib"));
        Assert.Equal(400, r.Status);
        Assert.Contains("GEA1012", Json(r.Body).GetProperty("diagnostics").ToString());
    }

    [Theory]
    [InlineData("../appsettings")]
    [InlineData("..")]
    [InlineData("a/b")]
    public void Import_cannot_read_files_outside_the_library_folder(string name)
    {
        var r = _jobs.Import(Reactor.Replace("lib:tank-base-cover", "lib:" + name));
        Assert.Equal(400, r.Status);
        Assert.Contains("GEA1011", Json(r.Body).GetProperty("diagnostics").ToString());
    }

    [Fact]
    public void GeaLibraries_resolves_only_valid_names_that_exist()
    {
        Assert.NotNull(GeaLibraries.Resolve("tank-base-cover"));
        Assert.Null(GeaLibraries.Resolve("missing"));
        Assert.Null(GeaLibraries.Resolve("../Scripts/transformer"));
    }

    [Fact]
    public void Import_without_include_is_stored_unchanged()
    {
        var plain = "GEA_VERSION 1\nPRODUCT_NAME \"P\"\nPARAM_PREFIX GT_\nTARGET PART\nBEGIN_FORM\n  USER_INPUT_PARAM GT_A INTEGER 1\nEND_FORM\nBEGIN_BUILD\nEND_BUILD\n";
        var r = _jobs.Import(plain);
        Assert.Equal(200, r.Status);
        Assert.Equal(plain, _scripts.Get(Json(r.Body).GetProperty("scriptId").GetString()));
    }
}
