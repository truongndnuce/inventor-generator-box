using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

/// <summary>ADD_COMPONENT (TARGET ASSEMBLY): parser, checker, interpreter and the simulation host. No Inventor needed.</summary>
public class GeaAssemblyTests
{
    private const string Header = "GEA_VERSION 1\nPRODUCT_NAME \"Yard\"\nPARAM_PREFIX YD_\nTARGET ASSEMBLY\n";

    private static string Script(string build, string form = "") =>
        Header + "BEGIN_FORM\n" + form + "END_FORM\nBEGIN_BUILD\n" + build + "END_BUILD\n";

    private static (GeaScript Script, GeaSimulationHost Host, GeaRunResult Run) Run(string build, string form = "")
    {
        var s = GeaScript.Compile(Script(build, form));
        var host = new GeaSimulationHost();
        var run = s.RunBuild(s.EvaluateFormNumbers(null), host);
        return (s, host, run);
    }

    [Fact]
    public void A_component_is_placed_with_position_and_rotation_and_traced()
    {
        var (s, host, run) = Run(
            "DECLARE_REFERENCE tr\n" +
            "ADD_COMPONENT tr FROM \"transformer\" AT 1000 mm, 2 m, 0 ROT 0, 0, 90 deg\n" +
            "SET_REF_NAME tr \"Transformer_A\"\n");
        Assert.False(s.HasErrors, string.Join("\n", s.Diagnostics));
        Assert.True(run.Ok, run.Error?.Message);
        var obj = Assert.Single(host.Objects);
        Assert.Equal("Transformer_A", obj.Name);
        Assert.Equal("component:transformer", obj.Builder);
        Assert.Equal(1000, obj.Inputs["X"].Num);
        Assert.Equal(2000, obj.Inputs["Y"].Num);
        Assert.Equal(90, obj.Inputs["Rz"].Num);
        Assert.Contains(host.Trace, t => t.StartsWith("ADD_COMPONENT \"transformer\" AT 1000"));
    }

    [Fact]
    public void Position_and_rotation_are_optional()
    {
        var (s, host, run) = Run("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"post\"\n");
        Assert.False(s.HasErrors, string.Join("\n", s.Diagnostics));
        Assert.True(run.Ok, run.Error?.Message);
        Assert.Equal(0, Assert.Single(host.Objects).Inputs["X"].Num);
    }

    [Fact]
    public void A_loop_places_several_components_with_numbered_names_and_computed_positions()
    {
        var (s, host, run) = Run(
            "DECLARE_REFERENCE c\n" +
            "FOR i FROM 0 TO 2\n" +
            "  ADD_COMPONENT c FROM \"post\" AT i * 500 mm, 0, 0\n" +
            "  SET_REF_NAME c \"Post*\"\n" +
            "END_FOR\n");
        Assert.False(s.HasErrors, string.Join("\n", s.Diagnostics));
        Assert.True(run.Ok, run.Error?.Message);
        Assert.Equal(new[] { "Post_1", "Post_2", "Post_3" }, host.Objects.Select(o => o.Name));
        Assert.Equal(new[] { 0.0, 500, 1000 }, host.Objects.Select(o => o.Inputs["X"].Num));
    }

    [Fact]
    public void A_form_parameter_can_drive_the_position()
    {
        var (s, host, run) = Run(
            "DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"post\" AT YD_Gap, 0, 0\n",
            "USER_INPUT_PARAM YD_Gap LENGTH 750 mm\n");
        Assert.False(s.HasErrors, string.Join("\n", s.Diagnostics));
        Assert.True(run.Ok, run.Error?.Message);
        Assert.Equal(750, Assert.Single(host.Objects).Inputs["X"].Num);
    }

    [Fact]
    public void The_example_of_the_language_spec_compiles_and_runs()
    {
        const string example =
            "GEA_VERSION 1\nPRODUCT_NAME \"Yard\"\nPARAM_PREFIX YD_\nTARGET ASSEMBLY\n" +
            "BEGIN_FORM\n  USER_INPUT_PARAM YD_Gap LENGTH 500 mm MIN 100 mm\nEND_FORM\n" +
            "BEGIN_BUILD\n  DECLARE_REFERENCE c\n  FOR i FROM 0 TO 2\n" +
            "    ADD_COMPONENT c FROM \"post\" AT i * YD_Gap, 0, 0 ROT 0, 0, 0\n    SET_REF_NAME c \"Post*\"\n  END_FOR\nEND_BUILD\n";
        var s = GeaScript.Compile(example);
        Assert.False(s.HasErrors, string.Join("\n", s.Diagnostics));
        var host = new GeaSimulationHost();
        Assert.True(s.RunBuild(s.EvaluateFormNumbers(null), host).Ok);
        Assert.Equal(new[] { 0.0, 500, 1000 }, host.Objects.Select(o => o.Inputs["X"].Num));
    }

    [Fact]
    public void ADD_COMPONENT_in_a_part_file_is_an_error()
    {
        var text = Script("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"post\"\n").Replace("TARGET ASSEMBLY", "TARGET PART");
        var s = GeaScript.Compile(text);
        Assert.True(s.HasErrors);
        Assert.Contains(s.Diagnostics, d => d.Code == "GEA2020" && d.Message.Contains("ADD_COMPONENT"));
    }

    [Theory]
    [InlineData("DECLARE_REFERENCE c\nADD_COMPONENT c FROM 5\n", "GEA2002")]                          // part name must be a string
    [InlineData("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"p\" AT 5, 0, 0\n", "GEA2002")]          // 5 has no unit
    [InlineData("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"p\" ROT 10 mm, 0, 0\n", "GEA2002")]     // a length where an angle is needed
    [InlineData("ADD_COMPONENT nope FROM \"p\"\n", "GEA2001")]                                       // not declared
    [InlineData("DECLARE_VARIABLE INTEGER n\nADD_COMPONENT n FROM \"p\"\n", "GEA2002")]              // not a reference
    public void Wrong_arguments_are_reported_by_the_checker(string build, string code)
    {
        var s = GeaScript.Compile(Script(build));
        Assert.Contains(s.Diagnostics, d => d.Code == code && d.Severity == GeaSeverity.Error);
    }

    [Theory]
    [InlineData("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"p\" AT 1 mm, 2 mm\n")]               // only two values
    [InlineData("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"p\" AT 0, 0, 0 AT 0, 0, 0\n")]         // AT twice
    [InlineData("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"p\" BELOW 0, 0, 0\n")]                  // unknown option
    [InlineData("DECLARE_REFERENCE c\nADD_COMPONENT c \"p\"\n")]                                     // FROM is missing
    public void Syntax_errors_are_reported_by_the_parser(string build)
    {
        var s = GeaScript.Compile(Script(build));
        Assert.Contains(s.Diagnostics, d => d.Code == "GEA1003" && d.Severity == GeaSeverity.Error);
    }

    [Fact]
    public void An_empty_part_name_fails_at_run_time()
    {
        var (_, _, run) = Run("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"\"\n");
        Assert.False(run.Ok);
        Assert.Equal("GEA4012", run.Error!.Code);
    }

    [Fact]
    public void A_host_that_builds_a_part_refuses_ADD_COMPONENT_with_GEA4020()
    {
        var s = GeaScript.Compile(Script("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"p\"\n"));
        var run = s.RunBuild(s.EvaluateFormNumbers(null), new PartOnlyHost());
        Assert.False(run.Ok);
        Assert.Equal("GEA4020", run.Error!.Code);
    }

    [Fact]
    public void An_assembly_script_is_accepted_for_the_web_and_has_no_warnings()
    {
        var s = GeaScript.Compile(Script("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"box\"\n"));
        Assert.False(s.HasErrors, string.Join("\n", s.Diagnostics));
        Assert.Empty(s.Diagnostics);
        Assert.Null(GeaForms.Reject(s));
    }

    [Fact]
    public void The_server_imports_an_assembly_script_and_the_job_keeps_it_for_the_worker()
    {
        using var db = new TestDb();
        var scripts = new ScriptStore(db.DataDir);
        var jobs = new JobService(db.NewStore(), scripts, new ConfigStore(db.DataDir), Microsoft.Extensions.Logging.Abstractions.NullLogger<JobService>.Instance);

        var r = jobs.Import(Script("DECLARE_REFERENCE c\nADD_COMPONENT c FROM \"box\" AT YD_Gap, 0, 0\n", "USER_INPUT_PARAM YD_Gap LENGTH 500 mm\n"));
        Assert.Equal(200, r.Status);
        var scriptId = System.Text.Json.JsonSerializer.SerializeToElement(r.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            .GetProperty("scriptId").GetString()!;
        Assert.Contains("TARGET ASSEMBLY", scripts.Get(scriptId)!);
    }

    /// <summary>Implements the interface without AddComponent, like the Inventor Host that builds a part.</summary>
    private sealed class PartOnlyHost : IGeaHost
    {
        public void SyncParameters(IReadOnlyList<KeyValuePair<string, GeaValue>> values) { }
        public void SetParam(string name, GeaValue value) { }
        public void SetParamExpr(string name, string expression) { }
        public GeaValue GetParam(string name, GeaType type) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, GeaRef> CreateFeature(BuilderManifest builder, GeaRef? on, IReadOnlyDictionary<string, GeaValue> inputs) => throw new NotSupportedException();
        public void SetRefName(GeaRef target, string name) { }
        public string GetRefName(GeaRef target) => "";
        public void SetRefAttr(GeaRef target, string key, GeaValue value) { }
        public GeaValue? GetRefAttr(GeaRef target, string key) => null;
        public void SetAppearance(GeaRef target, string appearance) { }
        public void Regen() { }
        public void Progress(GeaProgressKind kind, string? text, double? percent) { }
        public void Print(string message, int line) { }
    }
}
