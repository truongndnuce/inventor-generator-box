using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

public class DesignRunnerTests
{
    private static string Bin(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    private static DesignModel Fixture(string name) =>
        DesignJson.Deserialize(File.ReadAllText(Bin("Fixtures", "designer", name + ".design.json")))!;

    private static string BoxBuild()
    {
        var lines = File.ReadAllText(Bin("Scripts", "box.gea")).Replace("\r\n", "\n").Split('\n');
        int a = Array.FindIndex(lines, l => l.Trim() == "BEGIN_BUILD");
        int b = Array.FindIndex(lines, l => l.Trim() == "END_BUILD");
        return string.Join("\n", lines[(a + 1)..b]);
    }

    private static DesignModel Box()
    {
        var m = Fixture("box-form");
        m.Steps.Add(new DesignStep { Kind = "raw", Text = BoxBuild() });
        return m;
    }

    private static string Show(DesignRunResult r) =>
        $"stage={r.Stage} valid={r.Valid} errors=[{string.Join(" | ", r.Errors.Select(e => $"{e.Code}@{e.Path}:{e.Line} {e.Message}"))}]";

    // ------------------------------------------------------------------ the normal path

    [Fact]
    public void Default_values_run_the_build_and_trace_the_builder_calls()
    {
        var r = DesignRunner.Run(Box());
        Assert.True(r.Valid, Show(r));
        Assert.Equal("build", r.Stage);
        Assert.Empty(r.Errors);
        Assert.NotNull(r.Form);
        Assert.Contains(r.Trace, t => t.Contains("Tank"));
        Assert.Contains(r.Trace, t => t.Contains("Cover"));
    }

    [Fact]
    public void Values_are_taken_in_the_display_unit_and_reach_the_build()
    {
        var r = DesignRunner.Run(Box(), new Dictionary<string, double> { ["BX_Length"] = 150 });
        Assert.True(r.Valid, Show(r));
        var trace = string.Join("\n", r.Trace);
        Assert.Contains("150", trace);
    }

    [Fact]
    public void A_design_without_build_steps_is_valid_and_has_a_trace_of_only_the_sync()
    {
        var r = DesignRunner.Run(Fixture("box-form"));
        Assert.True(r.Valid, Show(r));
        Assert.DoesNotContain(r.Trace, t => t.StartsWith("CREATE"));
    }

    [Fact]
    public void The_form_is_described_like_an_imported_script()
    {
        var r = DesignRunner.Run(Box());
        var json = System.Text.Json.JsonSerializer.Serialize(r.Form, DesignJson.Options);
        Assert.Contains("\"BX_Wall\"", json);
        Assert.Contains("\"fields\"", json);
    }

    // ------------------------------------------------------------------ rules

    [Fact]
    public void A_broken_rule_makes_the_run_invalid_and_names_the_rule()
    {
        var r = DesignRunner.Run(Box(), new Dictionary<string, double> { ["BX_Height"] = 20, ["BX_Bottom"] = 10, ["BX_Wall"] = 10 });
        Assert.False(r.Valid);
        Assert.Equal("form", r.Stage);
        Assert.Empty(r.Trace);
        var e = Assert.Single(r.Errors, x => x.Path == "rules[1]");
        Assert.Equal("Wall thickness must be less than (height - bottom thickness).", e.Message);
    }

    [Fact]
    public void A_field_out_of_range_is_reported_on_its_parameter()
    {
        var r = DesignRunner.Run(Box(), new Dictionary<string, double> { ["BX_Length"] = 5000 });
        Assert.False(r.Valid);
        Assert.Contains(r.Errors, e => e.Path == "groups[0].params[0]");
    }

    [Fact]
    public void A_warning_rule_keeps_the_run_valid_and_is_listed()
    {
        var m = Box();
        m.Rules.Add(new DesignRule { Expr = "BX_Length < 90 mm", Message = "Long boxes are slow to print.", Warn = true });
        var r = DesignRunner.Run(m);   // default length 100 breaks the warning rule
        Assert.True(r.Valid, Show(r));
        var w = Assert.Single(r.Warnings, x => x.Message == "Long boxes are slow to print.");
        Assert.Equal("rules[7]", w.Path);
    }

    [Fact]
    public void A_hidden_field_does_not_need_a_value_and_uses_its_default()
    {
        var m = Fixture("all-types");
        var r = DesignRunner.Run(m);
        Assert.True(r.Valid, Show(r));
    }

    // ------------------------------------------------------------------ errors in the design

    [Fact]
    public void A_design_the_validator_refuses_does_not_run()
    {
        var m = Box();
        m.Groups[0].Params[0].Name = "Length";
        var r = DesignRunner.Run(m);
        Assert.False(r.Valid);
        Assert.Equal("design", r.Stage);
        Assert.Null(r.Form);
        Assert.Contains(r.Errors, e => e.Path == "groups[0].params[0].name");
    }

    [Fact]
    public void A_null_model_is_an_error_not_an_exception()
    {
        var r = DesignRunner.Run(null);
        Assert.False(r.Valid);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void A_compile_error_in_the_build_text_stops_before_the_run_with_the_typed_line()
    {
        var m = Box();
        m.Steps.Clear();
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "REGEN_MDL\nTHIS_IS_NOT_A_STATEMENT 1" });
        var r = DesignRunner.Run(m);
        Assert.False(r.Valid);
        Assert.Equal("compile", r.Stage);
        var e = Assert.Single(r.Errors, x => x.Path == "steps[0]");
        Assert.Equal(2, e.Line);
    }

    // ------------------------------------------------------------------ errors while building

    [Fact]
    public void FAIL_in_the_build_is_returned_with_its_message_and_line()
    {
        var m = Box();
        m.Steps.Clear();
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "REGEN_MDL\nFAIL \"stopped on purpose\"" });
        var r = DesignRunner.Run(m);
        Assert.False(r.Valid);
        Assert.Equal("build", r.Stage);
        var e = Assert.Single(r.Errors);
        Assert.Contains("stopped on purpose", e.Message);
        Assert.Equal("steps[0]", e.Path);
        Assert.Equal(2, e.Line);
    }

    [Fact]
    public void A_runaway_loop_stops_at_the_step_limit_and_is_an_error()
    {
        var m = Box();
        m.Steps.Clear();
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "WHILE TRUE\n  REGEN_MDL\nEND_WHILE" });
        var r = DesignRunner.Run(m);
        Assert.False(r.Valid);
        Assert.Contains(r.Errors, e => e.Code == "GEA5001");
    }

    // ------------------------------------------------------------------ time limit

    private sealed class SlowHost(int ms) : IGeaHost
    {
        private readonly GeaSimulationHost _inner = new();
        public void SyncParameters(IReadOnlyList<KeyValuePair<string, GeaValue>> v) { Thread.Sleep(ms); _inner.SyncParameters(v); }
        public void SetParam(string n, GeaValue v) => _inner.SetParam(n, v);
        public void SetParamExpr(string n, string e) => _inner.SetParamExpr(n, e);
        public GeaValue GetParam(string n, GeaType t) => _inner.GetParam(n, t);
        public IReadOnlyDictionary<string, GeaRef> CreateFeature(BuilderManifest b, GeaRef? on, IReadOnlyDictionary<string, GeaValue> i) => _inner.CreateFeature(b, on, i);
        public void SetRefName(GeaRef t, string n) => _inner.SetRefName(t, n);
        public string GetRefName(GeaRef t) => _inner.GetRefName(t);
        public void SetRefAttr(GeaRef t, string k, GeaValue v) => _inner.SetRefAttr(t, k, v);
        public GeaValue? GetRefAttr(GeaRef t, string k) => _inner.GetRefAttr(t, k);
        public void SetAppearance(GeaRef t, string a) => _inner.SetAppearance(t, a);
        public void Regen() => _inner.Regen();
        public void Progress(GeaProgressKind k, string? t, double? p) => _inner.Progress(k, t, p);
        public void Print(string m, int l) => _inner.Print(m, l);
    }

    [Fact]
    public void A_run_that_takes_too_long_returns_a_timeout_message()
    {
        var r = DesignRunner.Run(Box(), null, null, TimeSpan.FromMilliseconds(50), () => new SlowHost(1000));
        Assert.False(r.Valid);
        Assert.Equal("timeout", r.Stage);
        Assert.Equal("Dry run timed out", Assert.Single(r.Errors).Message);
    }

    [Fact]
    public void A_host_that_throws_is_reported_not_thrown()
    {
        var m = Box();
        m.Steps.Clear();
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "SET_REF_NAME NOPE \"x\"" });
        var r = DesignRunner.Run(m);
        Assert.False(r.Valid);
        Assert.NotEmpty(r.Errors);
    }
}
