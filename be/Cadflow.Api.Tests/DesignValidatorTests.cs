namespace Cadflow.Api.Tests;

public class DesignValidatorTests
{
    private static DesignParam P(string name, string type = "LENGTH", string? def = "100") => new() { Name = name, Type = type, Default = def };

    /// <summary>A small valid model; tests change one thing and expect exactly the matching issue.</summary>
    private static DesignModel Valid() => new()
    {
        Product = new() { Name = "Axial fan", Prefix = "FN_", Target = "PART" },
        Groups =
        {
            new DesignGroup { Title = "Casing", Params = { P("FN_Diameter"), P("FN_Length", "LENGTH", "400") } },
        },
    };

    private static List<DesignIssue> Check(DesignModel m) => DesignValidator.Validate(m);

    private static void AssertIssue(List<DesignIssue> issues, string path, string? contains = null, bool warning = false)
    {
        var hit = issues.FirstOrDefault(i => i.Path == path && i.IsWarning == warning && (contains == null || i.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)));
        Assert.True(hit != null, $"Expected {(warning ? "warning" : "issue")} at '{path}'" + (contains != null ? $" containing '{contains}'" : "")
            + ". Got: " + (issues.Count == 0 ? "(none)" : string.Join(" | ", issues.Select(i => $"{i.Path}: {i.Message}"))));
    }

    private static void AssertNoIssues(List<DesignIssue> issues) =>
        Assert.True(issues.Count == 0, string.Join(" | ", issues.Select(i => $"{i.Path}: {i.Message}")));

    // ------------------------------------------------------------------ valid models

    [Fact]
    public void Minimal_model_is_valid()
    {
        AssertNoIssues(Check(Valid()));
    }

    [Fact]
    public void Model_with_every_parameter_type_rule_variable_and_raw_step_is_valid()
    {
        var m = Valid();
        m.Groups[0].Params.AddRange(new[]
        {
            new DesignParam { Name = "FN_Angle", Type = "ANGLE", Default = "30", Unit = "deg", Min = 0, Max = 90, Step = 5, Required = true, Label = "Pitch angle", Tooltip = "Blade pitch" },
            new DesignParam { Name = "FN_Count", Type = "INTEGER", Default = "6", Min = 2, Max = 24 },
            new DesignParam { Name = "FN_Ratio", Type = "DOUBLE", Default = "0.5", Min = 0.1, Max = 0.9 },
            new DesignParam { Name = "FN_Tag", Type = "STRING", Default = "Fan 1" },
            new DesignParam { Name = "FN_Guard", Type = "CHECKBOX", Default = "TRUE", Label = "With guard" },
            new DesignParam { Name = "FN_Mount", Type = "RADIO", Default = "Wall", Options = new() { "Floor", "Wall" } },
            new DesignParam { Name = "FN_GuardGap", Type = "LENGTH", Default = "20", Unit = "mm", VisibleWhen = "FN_Guard" },
        });
        m.Vars.Add(new DesignVar { Name = "radius", Type = "LENGTH", Expr = "FN_Diameter / 2" });
        m.Rules.Add(new DesignRule { Expr = "FN_Length > radius", Message = "Length must exceed the radius." });
        m.Rules.Add(new DesignRule { Expr = "FN_Count * 10 <= 200", Message = "Too many blades.", Warn = true });
        m.Steps.Add(new DesignStep { Kind = "raw", Text = "  REGEN_MDL\n" });
        AssertNoIssues(Check(m));
    }

    [Fact]
    public void Quotes_and_backslashes_are_allowed_in_labels_and_messages_because_the_generator_escapes_them()
    {
        var m = Valid();
        m.Groups[0].Title = "The \"main\" group";
        m.Groups[0].Params[0].Label = "Diameter (D\\d)";
        m.Rules.Add(new DesignRule { Expr = "FN_Diameter > 0 mm", Message = "Say \"no\" to a back\\slash." });
        AssertNoIssues(Check(m));
    }

    // ------------------------------------------------------------------ product

    [Theory]
    [InlineData("FN_", false)]
    [InlineData("A_", false)]
    [InlineData("Fan2_", false)]
    [InlineData("", true)]
    [InlineData("FN", true)]            // must end with _
    [InlineData("_FN_", true)]          // must start with a letter
    [InlineData("1FN_", true)]
    [InlineData("F-N_", true)]
    [InlineData("ABCDEFGHIJKLMNOPQ_", true)] // 17 characters before the underscore is too long
    [InlineData("FN\n_", true)]
    public void Prefix_rule(string prefix, bool invalid)
    {
        var m = Valid();
        m.Product = m.Product with { Prefix = prefix };
        // keep the parameters valid for this prefix so only the prefix rule is exercised
        var issues = Check(m).Where(i => i.Path == "product.prefix").ToList();
        Assert.Equal(invalid, issues.Count > 0);
    }

    [Theory]
    [InlineData("Axial fan", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("a\"b", true)]
    [InlineData("a\\b", true)]
    [InlineData("a\nb", true)]
    public void Product_name_rule(string name, bool invalid)
    {
        var m = Valid();
        m.Product = m.Product with { Name = name };
        Assert.Equal(invalid, Check(m).Any(i => i.Path == "product.name"));
    }

    [Fact]
    public void Product_name_longer_than_80_characters_is_rejected()
    {
        var m = Valid();
        m.Product = m.Product with { Name = new string('x', 81) };
        AssertIssue(Check(m), "product.name", "80");
    }

    [Fact]
    public void Only_target_PART_is_accepted_in_this_version()
    {
        var m = Valid();
        m.Product = m.Product with { Target = "ASSEMBLY" };
        AssertIssue(Check(m), "product.target", "PART");
    }

    [Fact]
    public void Unknown_model_version_is_rejected()
    {
        var m = Valid() with { Version = 2 };
        AssertIssue(Check(m), "version", "version");
    }

    // ------------------------------------------------------------------ parameter names

    [Fact]
    public void Parameter_name_must_start_with_the_prefix()
    {
        var m = Valid();
        m.Groups[0].Params[1].Name = "Length";
        AssertIssue(Check(m), "groups[0].params[1].name", "prefix");
    }

    [Theory]
    [InlineData("FN_a b")]
    [InlineData("FN_a-b")]
    [InlineData("FN_a\"b")]
    [InlineData("FN_a\nb")]
    [InlineData("FN_é")]
    public void Parameter_name_must_be_an_identifier(string name)
    {
        var m = Valid();
        m.Groups[0].Params[0].Name = name;
        AssertIssue(Check(m), "groups[0].params[0].name");
    }

    [Fact]
    public void Parameter_name_must_be_unique_across_groups()
    {
        var m = Valid();
        m.Groups.Add(new DesignGroup { Title = "Other", Params = { P("FN_Diameter") } });
        AssertIssue(Check(m), "groups[1].params[0].name", "already used");
    }

    [Fact]
    public void Parameter_name_must_not_be_a_language_keyword()
    {
        var m = Valid();
        m.Product = m.Product with { Prefix = "END_" };
        m.Groups[0].Params[0].Name = "END_IF";
        m.Groups[0].Params[1].Name = "END_Length";
        AssertIssue(Check(m), "groups[0].params[0].name", "keyword");
    }

    [Fact]
    public void Parameter_name_must_not_equal_a_variable_name()
    {
        var m = Valid();
        m.Vars.Add(new DesignVar { Name = "FN_Diameter", Type = "LENGTH", Expr = "10 mm" });
        AssertIssue(Check(m), "vars[0].name", "already used");
    }

    // ------------------------------------------------------------------ parameter types and values

    [Fact]
    public void Unknown_type_is_rejected()
    {
        var m = Valid();
        m.Groups[0].Params[0].Type = "BOOL";
        AssertIssue(Check(m), "groups[0].params[0].type");
    }

    [Theory]
    [InlineData("LENGTH", "cm", false)]
    [InlineData("LENGTH", "in", false)]
    [InlineData("LENGTH", "deg", true)]
    [InlineData("ANGLE", "rad", false)]
    [InlineData("ANGLE", "mm", true)]
    [InlineData("LENGTH", "furlong", true)]
    [InlineData("INTEGER", "mm", true)]
    [InlineData("DOUBLE", "deg", true)]
    [InlineData("STRING", "mm", true)]
    public void Unit_must_fit_the_type(string type, string unit, bool invalid)
    {
        var m = Valid();
        m.Groups[0].Params[0] = new DesignParam { Name = "FN_Diameter", Type = type, Default = type == "STRING" ? "x" : "1", Unit = unit };
        Assert.Equal(invalid, Check(m).Any(i => i.Path == "groups[0].params[0].unit"));
    }

    [Theory]
    [InlineData("LENGTH", null)]
    [InlineData("LENGTH", "")]
    [InlineData("LENGTH", "abc")]
    [InlineData("LENGTH", "NaN")]
    [InlineData("LENGTH", "Infinity")]
    [InlineData("INTEGER", "2.5")]
    [InlineData("CHECKBOX", null)]
    [InlineData("CHECKBOX", "yes")]
    [InlineData("CHECKBOX", "true")]   // canonical form only: TRUE or FALSE
    public void Default_must_fit_the_type(string type, string? def)
    {
        var m = Valid();
        m.Groups[0].Params[0] = new DesignParam { Name = "FN_Diameter", Type = type, Default = def };
        AssertIssue(Check(m), "groups[0].params[0].default");
    }

    [Fact]
    public void Default_uses_the_invariant_decimal_point()
    {
        var m = Valid();
        m.Groups[0].Params[0] = new DesignParam { Name = "FN_Diameter", Type = "DOUBLE", Default = "0,5" };
        AssertIssue(Check(m), "groups[0].params[0].default");
    }

    [Fact]
    public void Min_must_not_exceed_max()
    {
        var m = Valid();
        m.Groups[0].Params[0].Min = 500;
        m.Groups[0].Params[0].Max = 100;
        AssertIssue(Check(m), "groups[0].params[0].min", "greater than");
    }

    [Fact]
    public void Step_must_be_positive()
    {
        var m = Valid();
        m.Groups[0].Params[0].Step = 0;
        AssertIssue(Check(m), "groups[0].params[0].step", "positive");
    }

    [Fact]
    public void Default_outside_min_max_is_a_warning_not_an_error()
    {
        var m = Valid();
        m.Groups[0].Params[0].Min = 200;
        m.Groups[0].Params[0].Max = 300;     // the default is 100
        var issues = Check(m);
        AssertIssue(issues, "groups[0].params[0].default", "outside", warning: true);
        Assert.DoesNotContain(issues, i => !i.IsWarning);
    }

    [Theory]
    [InlineData("STRING")]
    [InlineData("CHECKBOX")]
    [InlineData("RADIO")]
    public void Min_max_step_do_not_apply_to_text_checkbox_or_radio(string type)
    {
        var m = Valid();
        m.Groups[0].Params[0] = new DesignParam
        {
            Name = "FN_Diameter", Type = type, Min = 1, Max = 2, Step = 1,
            Default = type == "CHECKBOX" ? "TRUE" : "a", Options = type == "RADIO" ? new() { "a", "b" } : null,
        };
        var issues = Check(m);
        AssertIssue(issues, "groups[0].params[0].min");
        AssertIssue(issues, "groups[0].params[0].max");
        AssertIssue(issues, "groups[0].params[0].step");
    }

    [Theory]
    [InlineData("CHECKBOX")]
    [InlineData("RADIO")]
    public void Required_does_not_apply_to_checkbox_or_radio(string type)
    {
        var m = Valid();
        m.Groups[0].Params[0] = new DesignParam
        {
            Name = "FN_Diameter", Type = type, Required = true,
            Default = type == "CHECKBOX" ? "TRUE" : "a", Options = type == "RADIO" ? new() { "a" } : null,
        };
        AssertIssue(Check(m), "groups[0].params[0].required");
    }

    // ------------------------------------------------------------------ radio options

    [Fact]
    public void Radio_needs_at_least_one_option()
    {
        var m = Valid();
        m.Groups[0].Params[0] = new DesignParam { Name = "FN_Diameter", Type = "RADIO", Options = new() };
        AssertIssue(Check(m), "groups[0].params[0].options", "at least one");
    }

    [Fact]
    public void Radio_options_must_be_unique_and_clean()
    {
        var m = Valid();
        m.Groups[0].Params[0] = new DesignParam { Name = "FN_Diameter", Type = "RADIO", Options = new() { "A", "A", "B\nC", "" } };
        var issues = Check(m);
        AssertIssue(issues, "groups[0].params[0].options[1]", "duplicate");
        AssertIssue(issues, "groups[0].params[0].options[2]");
        AssertIssue(issues, "groups[0].params[0].options[3]");
    }

    [Fact]
    public void Radio_default_must_be_one_of_the_options()
    {
        var m = Valid();
        m.Groups[0].Params[0] = new DesignParam { Name = "FN_Diameter", Type = "RADIO", Default = "C", Options = new() { "A", "B" } };
        AssertIssue(Check(m), "groups[0].params[0].default", "options");
    }

    [Fact]
    public void Options_on_a_non_radio_parameter_are_rejected()
    {
        var m = Valid();
        m.Groups[0].Params[0].Options = new() { "A" };
        AssertIssue(Check(m), "groups[0].params[0].options");
    }

    // ------------------------------------------------------------------ free text

    [Theory]
    [InlineData("label")]
    [InlineData("tooltip")]
    [InlineData("title")]
    public void Control_characters_and_overlong_text_are_rejected(string field)
    {
        foreach (var bad in new[] { "a\nb", "a\tb", new string('x', 201) })
        {
            var m = Valid();
            switch (field)
            {
                case "label": m.Groups[0].Params[0].Label = bad; break;
                case "tooltip": m.Groups[0].Params[0].Tooltip = bad; break;
                default: m.Groups[0].Title = bad; break;
            }
            var path = field switch { "label" => "groups[0].params[0].label", "tooltip" => "groups[0].params[0].tooltip", _ => "groups[0].title" };
            AssertIssue(Check(m), path);
        }
    }

    [Fact]
    public void Group_title_is_required()
    {
        var m = Valid();
        m.Groups[0].Title = "  ";
        AssertIssue(Check(m), "groups[0].title");
    }

    // ------------------------------------------------------------------ expressions, rules, variables

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a >\nb")]
    [InlineData("a > 1 ! comment")]
    [InlineData("a > 1 /* open")]
    [InlineData("a > 1 \\")]
    [InlineData("a > \"open")]
    public void Rule_expression_must_be_a_single_clean_line(string expr)
    {
        var m = Valid();
        m.Rules.Add(new DesignRule { Expr = expr, Message = "msg" });
        AssertIssue(Check(m), "rules[0].expr");
    }

    [Fact]
    public void Exclamation_mark_inside_a_quoted_string_of_an_expression_is_fine()
    {
        var m = Valid();
        m.Groups[0].Params.Add(new DesignParam { Name = "FN_Tag", Type = "STRING", Default = "x" });
        m.Rules.Add(new DesignRule { Expr = "FN_Tag <> \"no! way\"", Message = "msg" });
        AssertNoIssues(Check(m));
    }

    [Fact]
    public void Rule_expression_longer_than_500_characters_is_rejected()
    {
        var m = Valid();
        m.Rules.Add(new DesignRule { Expr = "FN_Diameter > " + new string('1', 500), Message = "msg" });
        AssertIssue(Check(m), "rules[0].expr", "500");
    }

    [Fact]
    public void Rule_message_is_required_and_limited()
    {
        var m = Valid();
        m.Rules.Add(new DesignRule { Expr = "FN_Diameter > 0 mm", Message = "" });
        m.Rules.Add(new DesignRule { Expr = "FN_Diameter > 0 mm", Message = new string('x', 401) });
        var issues = Check(m);
        AssertIssue(issues, "rules[0].message");
        AssertIssue(issues, "rules[1].message");
    }

    [Fact]
    public void Variable_needs_a_valid_name_type_and_expression()
    {
        var m = Valid();
        m.Vars.Add(new DesignVar { Name = "bad name", Type = "REFERENCE", Expr = "" });
        var issues = Check(m);
        AssertIssue(issues, "vars[0].name");
        AssertIssue(issues, "vars[0].type");
        AssertIssue(issues, "vars[0].expr");
    }

    [Fact]
    public void Variable_names_must_be_unique_and_not_keywords()
    {
        var m = Valid();
        m.Vars.Add(new DesignVar { Name = "h", Type = "LENGTH", Expr = "1 mm" });
        m.Vars.Add(new DesignVar { Name = "h", Type = "LENGTH", Expr = "2 mm" });
        m.Vars.Add(new DesignVar { Name = "FOR", Type = "LENGTH", Expr = "2 mm" });
        var issues = Check(m);
        AssertIssue(issues, "vars[1].name", "already used");
        AssertIssue(issues, "vars[2].name", "keyword");
    }

    [Fact]
    public void Rule_or_variable_that_uses_a_parameter_hidden_by_visibleWhen_gets_a_warning()
    {
        var m = Valid();
        m.Groups[0].Params.Add(new DesignParam { Name = "FN_Guard", Type = "CHECKBOX", Default = "FALSE" });
        m.Groups[0].Params.Add(new DesignParam { Name = "FN_GuardGap", Type = "LENGTH", Default = "20", VisibleWhen = "FN_Guard" });
        m.Rules.Add(new DesignRule { Expr = "FN_GuardGap < FN_Diameter", Message = "Gap too large." });
        m.Vars.Add(new DesignVar { Name = "g", Type = "LENGTH", Expr = "FN_GuardGap * 2" });
        var issues = Check(m);
        AssertIssue(issues, "rules[0].expr", "FN_GuardGap", warning: true);
        AssertIssue(issues, "vars[0].expr", "FN_GuardGap", warning: true);
        Assert.DoesNotContain(issues, i => !i.IsWarning);
    }

    [Fact]
    public void VisibleWhen_expression_is_checked_like_any_expression()
    {
        var m = Valid();
        m.Groups[0].Params[0].VisibleWhen = "FN_Length >\n 1";
        AssertIssue(Check(m), "groups[0].params[0].visibleWhen");
    }

    // ------------------------------------------------------------------ steps

    [Fact]
    public void Only_raw_steps_are_supported_in_this_version()
    {
        var m = Valid();
        m.Steps.Add(new DesignStep { Kind = "feature", Text = null });
        AssertIssue(Check(m), "steps[0].kind", "raw");
    }

    [Fact]
    public void Raw_text_may_contain_line_breaks_and_tabs_but_not_other_control_characters()
    {
        var ok = Valid();
        ok.Steps.Add(new DesignStep { Kind = "raw", Text = "A\r\n\tB\n" });
        AssertNoIssues(Check(ok));

        var bad = Valid();
        bad.Steps.Add(new DesignStep { Kind = "raw", Text = "A\0B" });
        AssertIssue(Check(bad), "steps[0].text");
    }

    [Fact]
    public void Raw_text_over_100_KB_is_rejected()
    {
        var m = Valid();
        m.Steps.Add(new DesignStep { Kind = "raw", Text = new string('x', 100 * 1024 + 1) });
        AssertIssue(Check(m), "steps[0].text", "100 KB");
    }

    // ------------------------------------------------------------------ limits

    [Fact]
    public void More_than_200_parameters_are_rejected()
    {
        var m = Valid();
        for (int i = 0; i < 201; i++) m.Groups[0].Params.Add(P($"FN_P{i}"));
        AssertIssue(Check(m), "groups", "200");
    }

    [Fact]
    public void Exactly_200_parameters_are_accepted()
    {
        var m = Valid();
        m.Groups[0].Params.Clear();
        for (int i = 0; i < 200; i++) m.Groups[0].Params.Add(P($"FN_P{i}"));
        AssertNoIssues(Check(m));
    }

    [Fact]
    public void More_than_50_groups_100_rules_or_100_variables_are_rejected()
    {
        var m = Valid();
        for (int i = 0; i < 50; i++) m.Groups.Add(new DesignGroup { Title = "G" + i });
        AssertIssue(Check(m), "groups", "50");

        var r = Valid();
        for (int i = 0; i < 101; i++) r.Rules.Add(new DesignRule { Expr = "FN_Diameter > 0 mm", Message = "m" });
        AssertIssue(Check(r), "rules", "100");

        var v = Valid();
        for (int i = 0; i < 101; i++) v.Vars.Add(new DesignVar { Name = "v" + i, Type = "LENGTH", Expr = "1 mm" });
        AssertIssue(Check(v), "vars", "100");
    }

    [Fact]
    public void Model_over_256_KB_is_rejected_before_anything_else()
    {
        var m = Valid();
        for (int i = 0; i < 80; i++)
            m.Rules.Add(new DesignRule { Expr = "FN_Diameter > 0 mm", Message = new string('x', 399) });
        m.Steps.Add(new DesignStep { Kind = "raw", Text = new string('x', 100 * 1024) });
        m.Steps.Add(new DesignStep { Kind = "raw", Text = new string('y', 100 * 1024) });
        m.Steps.Add(new DesignStep { Kind = "raw", Text = new string('z', 100 * 1024) });
        var issues = Check(m);
        AssertIssue(issues, "model", "256 KB");
        Assert.Single(issues);   // the size check stops further processing
    }

    // ------------------------------------------------------------------ robustness against bad JSON

    [Fact]
    public void Model_deserialized_from_sparse_or_null_json_does_not_throw()
    {
        foreach (var json in new[] { "{}", "{\"product\":null}", "{\"groups\":null,\"rules\":null,\"vars\":null,\"steps\":null}",
                                     "{\"groups\":[{\"title\":\"T\",\"params\":null}]}", "{\"groups\":[{\"title\":\"T\",\"params\":[{}]}]}" })
        {
            var m = DesignJson.Deserialize(json);
            Assert.NotNull(m);
            var issues = Check(m!);
            Assert.NotEmpty(issues);   // an empty or half-filled model is never valid
        }
    }

    [Fact]
    public void Json_round_trip_keeps_the_model()
    {
        var m = Valid();
        m.Groups[0].Params[0].Label = "Diameter";
        m.Rules.Add(new DesignRule { Expr = "FN_Diameter > 0 mm", Message = "m", Warn = true });
        var again = DesignJson.Deserialize(DesignJson.Serialize(m));
        Assert.Equal(DesignJson.Serialize(m), DesignJson.Serialize(again!));
        AssertNoIssues(Check(again!));
    }
}
