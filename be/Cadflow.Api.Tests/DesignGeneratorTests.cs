using System.Text.RegularExpressions;
using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

public class DesignGeneratorTests
{
    private static string Bin(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    private static DesignModel Fixture(string name)
    {
        var json = File.ReadAllText(Bin("Fixtures", "designer", name + ".design.json"));
        return DesignJson.Deserialize(json) ?? throw new InvalidOperationException($"Fixture {name} is not a design.");
    }

    /// <summary>The BEGIN_BUILD ... END_BUILD content of a shipped script (without the two marker lines).</summary>
    private static string BuildBlockOf(string scriptFile)
    {
        var lines = File.ReadAllText(Bin("Scripts", scriptFile)).Replace("\r\n", "\n").Split('\n');
        int a = Array.FindIndex(lines, l => l.Trim() == "BEGIN_BUILD");
        int b = Array.FindIndex(lines, l => l.Trim() == "END_BUILD");
        return string.Join("\n", lines[(a + 1)..b]);
    }

    private static GeaScript Compile(string text) => GeaScript.Compile(text, GeaLibraries.Resolve);

    private static string Errors(GeaScript s) => string.Join("\n", s.Diagnostics.Where(d => d.Severity == GeaSeverity.Error));

    private static DesignModel BoxWithBuild()
    {
        var m = Fixture("box-form");
        m.Steps.Add(new DesignStep { Kind = "raw", Text = BuildBlockOf("box.gea") });
        return m;
    }

    // ------------------------------------------------------------------ compiles

    [Theory]
    [InlineData("minimal")]
    [InlineData("all-types")]
    public void Generated_script_compiles_without_errors(string fixture)
    {
        var text = DesignGenerator.Generate(Fixture(fixture)).Text;
        var script = Compile(text);
        Assert.False(script.HasErrors, Errors(script) + "\n" + text);
    }

    [Fact]
    public void Box_form_and_build_compile_without_errors()
    {
        var script = Compile(DesignGenerator.Generate(BoxWithBuild()).Text);
        Assert.False(script.HasErrors, Errors(script));
        Assert.Equal("Box with lid", script.Program.ProductName);
        Assert.Equal("BX_", script.Program.ParamPrefix);
        Assert.Equal("PART", script.Program.Target);
    }

    [Fact]
    public void A_model_without_build_steps_still_has_a_valid_empty_build_block()
    {
        var text = DesignGenerator.Generate(Fixture("minimal")).Text;
        Assert.Contains("BEGIN_BUILD", text);
        Assert.Contains("! (no build steps yet)", text);
        Assert.False(Compile(text).HasErrors);
    }

    // ------------------------------------------------------------------ equivalent to a shipped script

    private static string FieldSummary(GeaFormField f) =>
        string.Join("|", f.Name, f.Kind, f.Type, f.Group, f.Label, f.Unit, f.Min, f.Max, f.Step, f.Required, string.Join(",", f.Options),
            f.Default.Num, f.Default.Str, f.Tooltip);

    public static IEnumerable<object[]> BoxValueSets() =>
    [
        [new Dictionary<string, double>()],
        [new Dictionary<string, double> { ["BX_Length"] = 200, ["BX_Width"] = 120, ["BX_Height"] = 80 }],
        [new Dictionary<string, double> { ["BX_Wall"] = 30 }],                                  // wall too thick for the short side
        [new Dictionary<string, double> { ["BX_Wall"] = 9, ["BX_Height"] = 20, ["BX_Bottom"] = 5 }],
        [new Dictionary<string, double> { ["BX_LidLip"] = 15, ["BX_Height"] = 20 }],             // lip longer than the inner height
        [new Dictionary<string, double> { ["BX_LidClearance"] = 1, ["BX_Wall"] = 1 }],           // clearance not less than the wall
        [new Dictionary<string, double> { ["BX_Length"] = 10 }],                                 // violates MIN
        [new Dictionary<string, double> { ["BX_CornerRadius"] = 25 }],                           // violates MAX
    ];

    [Fact]
    public void Generated_box_form_has_the_same_fields_as_box_gea()
    {
        var original = GeaScript.Compile(File.ReadAllText(Bin("Scripts", "box.gea")), GeaLibraries.Resolve, "box.gea").EvaluateForm(null);
        var generated = Compile(DesignGenerator.Generate(BoxWithBuild()).Text).EvaluateForm(null);

        Assert.Equal(original.Fields.Select(FieldSummary), generated.Fields.Select(FieldSummary));
    }

    [Theory]
    [MemberData(nameof(BoxValueSets))]
    public void Generated_box_validates_exactly_like_box_gea(Dictionary<string, double> values)
    {
        var original = GeaScript.Compile(File.ReadAllText(Bin("Scripts", "box.gea")), GeaLibraries.Resolve, "box.gea").EvaluateFormValues(values, null);
        var generated = Compile(DesignGenerator.Generate(BoxWithBuild()).Text).EvaluateFormValues(values, null);

        Assert.Equal(original.Ok, generated.Ok);
        Assert.Equal(original.Errors.Select(e => e.Message), generated.Errors.Select(e => e.Message));
        Assert.Equal(original.Warnings.Select(e => e.Message), generated.Warnings.Select(e => e.Message));
        Assert.Equal(original.Values.Select(v => $"{v.Key}={v.Value}"), generated.Values.Select(v => $"{v.Key}={v.Value}"));
    }

    [Fact]
    public void Generated_box_builds_exactly_the_same_commands_as_box_gea_on_the_simulated_host()
    {
        static List<string> Trace(GeaScript s)
        {
            var host = new GeaSimulationHost();
            var run = s.RunBuild(s.EvaluateFormValues(null, null), host);
            Assert.True(run.Ok, run.Error?.ToString());
            // A PRINT trace line carries the line number in the file, which differs between the two files; everything else must be identical
            return host.Trace.Select(t => Regex.Replace(t, @"^PRINT \(\d+\)", "PRINT")).ToList();
        }

        var original = GeaScript.Compile(File.ReadAllText(Bin("Scripts", "box.gea")), GeaLibraries.Resolve, "box.gea");
        var generated = Compile(DesignGenerator.Generate(BoxWithBuild()).Text);

        var expected = Trace(original);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Trace(generated));
    }

    // ------------------------------------------------------------------ golden file

    [Fact]
    public void Output_matches_the_golden_file()
    {
        var expected = File.ReadAllText(Bin("Fixtures", "designer", "all-types.gea")).Replace("\r\n", "\n");
        var actual = DesignGenerator.Generate(Fixture("all-types")).Text;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Output_is_stable_text()
    {
        var text = DesignGenerator.Generate(Fixture("all-types")).Text;
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\t', text);
        Assert.False(text.StartsWith('﻿'));
        Assert.EndsWith("END_BUILD\n", text);                 // exactly one newline at the end
        Assert.False(text.EndsWith("\n\n"));
        Assert.Equal(text, DesignGenerator.Generate(Fixture("all-types")).Text);   // deterministic
    }

    // ------------------------------------------------------------------ shape of the output

    [Fact]
    public void Sections_come_in_the_documented_order()
    {
        var text = DesignGenerator.Generate(Fixture("all-types")).Text;
        int Idx(string s) { var i = text.IndexOf(s, StringComparison.Ordinal); Assert.True(i >= 0, $"missing: {s}"); return i; }

        var order = new[]
        {
            "GEA_VERSION 1", "PRODUCT_NAME \"Axial fan\"", "PARAM_PREFIX FN_", "TARGET PART", "BEGIN_FORM",
            "BEGIN_GROUP \"Casing\"", "BEGIN_GROUP \"Rotor and options\"", "DECLARE_VARIABLE LENGTH radius", "radius = FN_Diameter / 2",
            "ASSERT FN_Length > radius / 4", "END_FORM", "BEGIN_BUILD", "REGEN_MDL", "END_BUILD",
        };
        var positions = order.Select(Idx).ToList();
        Assert.Equal(positions.OrderBy(x => x), positions);
    }

    [Fact]
    public void Each_parameter_type_is_written_in_its_own_form()
    {
        var text = DesignGenerator.Generate(Fixture("all-types")).Text;

        Assert.Contains("USER_INPUT_PARAM FN_Diameter LENGTH 1200 mm MIN 400 MAX 3000 STEP 50 REQUIRED LABEL \"Casing diameter\" TOOLTIP \"Inner diameter of the casing\"", text);
        Assert.Contains("USER_INPUT_PARAM FN_Length LENGTH 0.4 m MIN 0.1 MAX 2 LABEL \"Casing length\"", text);   // the unit the author chose is kept
        Assert.Contains("USER_INPUT_PARAM FN_Pitch ANGLE 30 deg MIN 0 MAX 90 LABEL \"Blade pitch\"", text);
        Assert.Contains("USER_INPUT_PARAM FN_Blades INTEGER 6 MIN 2 MAX 24 REQUIRED LABEL \"Blade count\"", text);   // no decimal point for an INTEGER
        Assert.Contains("USER_INPUT_PARAM FN_Ratio DOUBLE 0.5 MIN 0.1 MAX 0.9 LABEL \"Hub ratio\"", text);
        Assert.Contains("USER_INPUT_PARAM FN_Tag STRING \"Fan \\\"A\\\"\" LABEL \"Tag\" TOOLTIP \"Shown on the drawing\"", text);
        Assert.Contains("CHECKBOX_PARAM FN_Guard TRUE LABEL \"With guard\"", text);
        Assert.Contains("RADIOBUTTON_PARAM FN_Mount \"Floor\" \"Wall\" \"Ceiling\" LABEL \"Mounting\" DEFAULT \"Wall\"", text);   // DEFAULT because it is not the first option
        Assert.Contains("RADIOBUTTON_PARAM FN_Drive \"Direct\" \"Belt\" LABEL \"Drive\"\n", text);                                // no DEFAULT: the first option is the default
    }

    [Fact]
    public void A_parameter_with_visibleWhen_is_wrapped_in_IF_inside_its_group()
    {
        var text = DesignGenerator.Generate(Fixture("all-types")).Text;
        var m = Regex.Match(text, @"( *)IF FN_Guard\n( *)USER_INPUT_PARAM FN_GuardGap [^\n]*\n( *)END_IF\n");
        Assert.True(m.Success, text);
        Assert.True(m.Groups[2].Length > m.Groups[1].Length);      // the parameter is indented inside the IF
        Assert.Equal(m.Groups[1].Length, m.Groups[3].Length);

        var script = Compile(text);
        var hidden = script.EvaluateFormValues(new Dictionary<string, double> { ["FN_Guard"] = 0 }, null);
        Assert.False(hidden.Fields.Single(f => f.Name == "FN_GuardGap").Visible);
        var shown = script.EvaluateFormValues(new Dictionary<string, double> { ["FN_Guard"] = 1 }, null);
        Assert.True(shown.Fields.Single(f => f.Name == "FN_GuardGap").Visible);
    }

    [Fact]
    public void A_warning_rule_ends_with_WARN_and_only_warns()
    {
        var text = DesignGenerator.Generate(Fixture("all-types")).Text;
        Assert.Contains("ASSERT FN_Blades * 10 <= 200 \"That many blades is unusual.\" WARN\n", text);

        var form = Compile(text).EvaluateFormValues(new Dictionary<string, double> { ["FN_Blades"] = 24 }, null);
        Assert.True(form.Ok, string.Join("; ", form.Errors.Select(e => e.Message)));
        Assert.Contains(form.Warnings, w => w.Message == "That many blades is unusual.");
    }

    [Fact]
    public void Numbers_are_written_with_a_decimal_point_whatever_the_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("vi-VN");
            var text = DesignGenerator.Generate(Fixture("all-types")).Text;
            Assert.Contains("FN_Ratio DOUBLE 0.5 MIN 0.1 MAX 0.9", text);
            Assert.False(Compile(text).HasErrors);
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }

    [Fact]
    public void Header_comment_does_not_contain_text_typed_by_the_author()
    {
        var m = Fixture("minimal");
        m.Product = m.Product with { Name = "Weird * name ! with marks" };
        var text = DesignGenerator.Generate(m).Text;
        var comments = text.Split('\n').Where(l => l.TrimStart().StartsWith('!'));
        Assert.DoesNotContain(comments, c => c.Contains("Weird"));
    }

    // ------------------------------------------------------------------ the build text

    [Fact]
    public void Raw_build_text_is_inserted_indented_and_line_endings_are_normalized()
    {
        var m = Fixture("minimal");
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "\r\n\r\n  REGEN_MDL\r\n\r\nPRINT \"done\"   \r\n\r\n" });
        var text = DesignGenerator.Generate(m).Text;

        Assert.DoesNotContain('\r', text);
        Assert.Contains("BEGIN_BUILD\n\n  ! Build steps (text).\n    REGEN_MDL\n\n  PRINT \"done\"\n\nEND_BUILD\n", text);
        Assert.False(Compile(text).HasErrors);
    }

    [Fact]
    public void Two_raw_steps_are_joined_with_a_blank_line()
    {
        var m = Fixture("minimal");
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "REGEN_MDL" });
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "PRINT \"x\"" });
        var text = DesignGenerator.Generate(m).Text;
        Assert.Contains("  REGEN_MDL\n\n  PRINT \"x\"\n", text);
    }

    [Fact]
    public void A_variable_declared_for_the_rules_is_not_visible_in_the_build_block()
    {
        // Language spec section 3: a variable is valid only in its own block. The Designer documents this; the compiler enforces it.
        var m = Fixture("all-types");
        m.Steps.Clear();
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "PRINT STR(radius)" });
        var script = Compile(DesignGenerator.Generate(m).Text);
        Assert.True(script.HasErrors);
    }

    // ------------------------------------------------------------------ escaping

    [Fact]
    public void Quotes_and_backslashes_in_labels_and_messages_survive_a_round_trip_through_the_compiler()
    {
        var m = Fixture("minimal");
        m.Groups[0].Title = "The \"main\" group";
        m.Groups[0].Params[0].Label = "Length (L\\d) \"x\"";
        m.Groups[0].Params[0].Tooltip = "back\\slash \"q\"";
        m.Rules.Add(new DesignRule { Expr = "MN_Length > 20 mm", Message = "Say \"no\" to a back\\slash." });
        var text = DesignGenerator.Generate(m).Text;

        var script = Compile(text);
        Assert.False(script.HasErrors, Errors(script) + "\n" + text);
        var field = script.EvaluateForm(null).Fields.Single();
        Assert.Equal("Length (L\\d) \"x\"", field.Label);
        Assert.Equal("back\\slash \"q\"", field.Tooltip);
        Assert.Equal("The \"main\" group", field.Group);

        var violated = script.EvaluateFormValues(new Dictionary<string, double> { ["MN_Length"] = 15 }, null);
        Assert.Contains(violated.Errors, e => e.Message == "Say \"no\" to a back\\slash.");
    }

    [Fact]
    public void An_invalid_model_is_refused_with_its_issues()
    {
        var m = Fixture("minimal");
        m.Groups[0].Params[0].Name = "MN_a\nPRINT \"injected\"";
        var ex = Assert.Throws<InvalidDesignException>(() => DesignGenerator.Generate(m));
        Assert.Contains(ex.Issues, i => i.Path == "groups[0].params[0].name");
    }

    [Fact]
    public void A_model_with_only_warnings_is_generated()
    {
        var m = Fixture("minimal");
        m.Groups[0].Params[0].Default = "5";       // below MIN 10: a warning
        var text = DesignGenerator.Generate(m).Text;
        Assert.Contains("MN_Length LENGTH 5 mm", text);
    }

    // ------------------------------------------------------------------ line map

    [Fact]
    public void Line_map_points_each_parameter_line_to_its_model_path()
    {
        var model = Fixture("all-types");
        var output = DesignGenerator.Generate(model);
        var lines = output.Text.Split('\n');

        for (int g = 0; g < model.Groups.Count; g++)
            for (int p = 0; p < model.Groups[g].Params.Count; p++)
            {
                var name = model.Groups[g].Params[p].Name;
                int line = Array.FindIndex(lines, l => Regex.IsMatch(l, $@"^\s*(USER_INPUT_PARAM|CHECKBOX_PARAM|RADIOBUTTON_PARAM) {name}\b")) + 1;
                Assert.True(line > 0, name);
                Assert.Equal($"groups[{g}].params[{p}]", output.Find(line));
            }
    }

    [Fact]
    public void Line_map_covers_groups_variables_rules_the_visibility_condition_and_the_raw_build()
    {
        var output = DesignGenerator.Generate(Fixture("all-types"));
        var lines = output.Text.Split('\n');
        int LineOf(string startsWith) => Array.FindIndex(lines, l => l.TrimStart().StartsWith(startsWith, StringComparison.Ordinal)) + 1;

        Assert.Equal("groups[0]", output.Find(LineOf("BEGIN_GROUP \"Casing\"")));
        Assert.Equal("groups[1]", output.Find(LineAfter("END_GROUP", LineOf("BEGIN_GROUP \"Rotor"), lines)));
        Assert.Equal("groups[1].params[4].visibleWhen", output.Find(LineOf("IF FN_Guard")));       // the most specific entry wins
        Assert.Equal("vars[0]", output.Find(LineOf("DECLARE_VARIABLE LENGTH radius")));
        Assert.Equal("vars[0].expr", output.Find(LineOf("radius = ")));
        Assert.Equal("rules[0]", output.Find(LineOf("ASSERT FN_Length")));
        Assert.Equal("rules[1]", output.Find(LineOf("ASSERT FN_Blades")));
        Assert.Equal("steps[0]", output.Find(LineOf("REGEN_MDL")));
        Assert.Equal("product", output.Find(LineOf("PRODUCT_NAME")));
        Assert.Null(output.Find(LineOf("BEGIN_FORM")));                                           // file-level lines have no model element
        Assert.Null(output.Find(0));
        Assert.Null(output.Find(10_000));
    }

    [Fact]
    public void Line_map_raw_entry_lets_the_caller_compute_the_line_inside_the_typed_text()
    {
        var m = Fixture("minimal");
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "REGEN_MDL\nPRINT \"b\"\nPRINT \"c\"" });
        var output = DesignGenerator.Generate(m);
        var entry = output.Entry("steps[0]")!;
        var lines = output.Text.Split('\n');

        Assert.Equal(3, entry.LastLine - entry.FirstLine + 1);
        Assert.Contains("PRINT \"c\"", lines[entry.LastLine - 1]);
        Assert.Contains("REGEN_MDL", lines[entry.FirstLine - 1]);
    }

    // helper kept below the tests that use it
    private static int LineAfter(string startsWith, int after, string[] lines)
    {
        for (int i = after; i < lines.Length; i++)
            if (lines[i].TrimStart().StartsWith(startsWith, StringComparison.Ordinal)) return i + 1;
        return 0;
    }
}
