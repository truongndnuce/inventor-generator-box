using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

public class DesignDiagnosticsTests
{
    private static DesignModel Fixture(string name) =>
        DesignJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designer", name + ".design.json")))!;

    private static DesignModel Fan() => Fixture("all-types");

    private static string Describe(DesignCheckResult r) =>
        string.Join(" | ", r.Diagnostics.Select(d => $"{d.Severity} {d.Code} @{d.Path ?? "-"}:{d.Line?.ToString() ?? "-"} {d.Message}"));

    private static DesignDiagnostic Single(DesignCheckResult r, string path)
    {
        var hits = r.Diagnostics.Where(d => d.Path == path && d.Severity == "error").ToList();
        Assert.True(hits.Count > 0, $"No error at '{path}'. Got: {Describe(r)}");
        return hits[0];
    }

    // ------------------------------------------------------------------ errors found by the real compiler

    [Fact]
    public void A_clean_design_has_no_diagnostics()
    {
        var r = DesignDiagnostics.Check(Fan());
        Assert.NotNull(r.Output);
        Assert.Empty(r.Diagnostics);
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void Unknown_name_in_a_rule_is_reported_on_that_rule()
    {
        var m = Fan();
        m.Rules.Add(new DesignRule { Expr = "FN_DoesNotExist > 1 mm", Message = "never" });
        var r = DesignDiagnostics.Check(m);

        var d = Single(r, "rules[2]");
        Assert.StartsWith("GEA", d.Code);
        Assert.Null(d.Line);          // only raw build text has a line number of its own
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void Type_mismatch_in_a_rule_is_reported_on_that_rule()
    {
        var m = Fan();
        m.Rules[0] = new DesignRule { Expr = "FN_Diameter > FN_Blades", Message = "length compared with a count" };   // LENGTH vs INTEGER
        var r = DesignDiagnostics.Check(m);
        Single(r, "rules[0]");
    }

    [Fact]
    public void Wrong_type_of_variable_expression_is_reported_on_the_variable_expression()
    {
        var m = Fan();
        m.Vars[0] = new DesignVar { Name = "radius", Type = "LENGTH", Expr = "5" };   // a plain number is not a LENGTH
        var r = DesignDiagnostics.Check(m);
        Single(r, "vars[0].expr");
    }

    [Fact]
    public void Bad_visibility_condition_is_reported_on_the_parameter_condition()
    {
        var m = Fan();
        m.Groups[1].Params[4].VisibleWhen = "FN_Diameter";      // a LENGTH is not a condition
        var r = DesignDiagnostics.Check(m);
        Single(r, "groups[1].params[4].visibleWhen");
    }

    [Fact]
    public void Syntax_error_in_raw_build_text_gets_the_line_the_author_typed()
    {
        var m = Fan();
        m.Steps.Clear();
        // typed lines: 1 and 2 blank, 3 valid, 4 bad, 5 valid. The generator drops the two leading blank lines, the reported line must still be 4.
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "\n\nREGEN_MDL\nTHIS_IS_NOT_A_STATEMENT 1\nREGEN_MDL\n" });
        var r = DesignDiagnostics.Check(m);

        var d = Single(r, "steps[0]");
        Assert.Equal(4, d.Line);
    }

    [Fact]
    public void Raw_line_numbers_are_right_with_windows_line_endings_and_several_raw_steps()
    {
        var m = Fan();
        m.Steps.Clear();
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "REGEN_MDL" });
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "\r\nREGEN_MDL\r\n\r\nTHIS_IS_NOT_A_STATEMENT 1\r\n" });   // bad statement is typed line 4 of the second step
        var r = DesignDiagnostics.Check(m);

        var d = Single(r, "steps[1]");
        Assert.Equal(4, d.Line);
        Assert.DoesNotContain(r.Diagnostics, x => x.Path == "steps[0]" && x.Severity == "error");
    }

    [Fact]
    public void Several_errors_are_all_reported_each_on_its_own_element()
    {
        var m = Fan();
        m.Rules.Add(new DesignRule { Expr = "FN_DoesNotExist > 1 mm", Message = "never" });
        m.Vars[0] = new DesignVar { Name = "radius", Type = "LENGTH", Expr = "5" };
        m.Steps[0] = new DesignStep { Kind = "raw", Text = "THIS_IS_NOT_A_STATEMENT 1" };
        var r = DesignDiagnostics.Check(m);

        var paths = r.Diagnostics.Where(d => d.Severity == "error").Select(d => d.Path).ToHashSet();
        Assert.Contains("rules[2]", paths);
        Assert.Contains("vars[0].expr", paths);
        Assert.Contains("steps[0]", paths);
    }

    // ------------------------------------------------------------------ validator issues come first

    [Fact]
    public void A_model_the_validator_refuses_is_not_compiled()
    {
        var m = Fan();
        m.Groups[0].Params[0].Name = "Diameter";           // does not start with the prefix
        var r = DesignDiagnostics.Check(m);

        Assert.Null(r.Output);
        Assert.Empty(r.Diagnostics);
        Assert.Contains(r.Issues, i => i.Path == "groups[0].params[0].name" && !i.IsWarning);
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void Validator_warnings_are_kept_next_to_the_compiled_result()
    {
        var m = Fan();
        m.Groups[0].Params[0].Min = 2000;       // the default 1200 is outside the range: a warning
        var r = DesignDiagnostics.Check(m);

        Assert.NotNull(r.Output);
        Assert.Contains(r.Issues, i => i.IsWarning && i.Path == "groups[0].params[0].default");
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void A_null_model_is_refused_without_throwing()
    {
        var r = DesignDiagnostics.Check(null);
        Assert.Null(r.Output);
        Assert.True(r.HasErrors);
    }

    // ------------------------------------------------------------------ the mapping itself (synthetic compiler messages)

    private static DesignOutput FanOutput() => DesignGenerator.Generate(Fan());

    [Fact]
    public void A_compiler_message_on_a_parameter_line_maps_to_that_parameter()
    {
        var output = FanOutput();
        var lines = output.Text.Split('\n');
        int line = Array.FindIndex(lines, l => l.Contains("USER_INPUT_PARAM FN_Ratio")) + 1;

        var mapped = DesignDiagnostics.Map(output, new[] { new GeaDiagnostic("GEA1003", GeaSeverity.Error, line, 5, "Invalid option") });

        var d = Assert.Single(mapped);
        Assert.Equal("groups[1].params[1]", d.Path);
        Assert.Null(d.Line);
        Assert.Equal("error", d.Severity);
        Assert.Equal("GEA1003", d.Code);
        Assert.Equal("Invalid option", d.Message);
    }

    [Fact]
    public void A_compiler_message_on_a_file_level_line_has_no_path()
    {
        var output = FanOutput();
        var mapped = DesignDiagnostics.Map(output, new[]
        {
            new GeaDiagnostic("GEA1001", GeaSeverity.Error, 1, 1, "GEA_VERSION is missing at the top of the file."),
            new GeaDiagnostic("GEA1002", GeaSeverity.Error, 10_000, 1, "past the end"),
        });

        Assert.All(mapped, d => { Assert.Null(d.Path); Assert.Null(d.Line); });
        Assert.Equal(2, mapped.Count);
    }

    [Fact]
    public void A_compiler_warning_keeps_its_severity()
    {
        var output = FanOutput();
        int line = output.Text.Split('\n').ToList().FindIndex(l => l.TrimStart().StartsWith("ASSERT FN_Blades")) + 1;
        var mapped = DesignDiagnostics.Map(output, new[] { new GeaDiagnostic("GEA3001", GeaSeverity.Warning, line, 1, "something") });

        var d = Assert.Single(mapped);
        Assert.Equal("warning", d.Severity);
        Assert.Equal("rules[1]", d.Path);
    }

    [Fact]
    public void Mapped_messages_keep_the_order_of_the_compiler()
    {
        var output = FanOutput();
        var mapped = DesignDiagnostics.Map(output, new[]
        {
            new GeaDiagnostic("GEA2001", GeaSeverity.Error, 30, 1, "second"),
            new GeaDiagnostic("GEA2002", GeaSeverity.Error, 12, 1, "first"),
        });
        Assert.Equal(new[] { "second", "first" }, mapped.Select(d => d.Message));
    }
}
