using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

public class GeaIncludeCompileTests
{
    private const string TankLib = """
        GEA_VERSION 1
        PRODUCT_NAME "Tank lib"
        PARAM_PREFIX TR_
        TARGET PART
        BEGIN_FORM
          USER_INPUT_PARAM TR_Len LENGTH 100 mm MIN 10 MAX 500 LABEL "Tank length"
        END_FORM
        BEGIN_BUILD
          DECLARE_REFERENCE body
        END_BUILD
        """;

    private const string Host = """
        GEA_VERSION 1
        PRODUCT_NAME "Host"
        PARAM_PREFIX GT_
        TARGET PART
        BEGIN_FORM
          INCLUDE lib:tank
          USER_INPUT_PARAM GT_Count INTEGER 4
        END_FORM
        BEGIN_BUILD
          INCLUDE lib:tank
        END_BUILD
        """;

    private static Func<string, string?> Libs(params (string Name, string Text)[] libs) =>
        name => libs.Where(l => l.Name == name).Select(l => l.Text).FirstOrDefault();

    [Fact]
    public void Host_with_other_prefix_can_include_a_library_with_its_own_prefix()
    {
        var script = GeaScript.Compile(Host, Libs(("tank", TankLib)));

        Assert.False(script.HasErrors, string.Join("\n", script.Diagnostics));
        Assert.Contains(script.Parameters, p => p.Name == "TR_Len");
        Assert.Contains(script.Parameters, p => p.Name == "GT_Count");
        Assert.DoesNotContain("INCLUDE", script.ExpandedText);
        Assert.Equal(Host, script.Text);
    }

    [Fact]
    public void Expanded_text_compiles_on_its_own_without_a_resolver()
    {
        var expanded = GeaScript.Compile(Host, Libs(("tank", TankLib))).ExpandedText;
        var again = GeaScript.Compile(expanded);   // đây là bản job lưu và worker nhận

        Assert.False(again.HasErrors, string.Join("\n", again.Diagnostics));
        Assert.Contains(again.Parameters, p => p.Name == "TR_Len");
    }

    [Fact]
    public void Param_prefix_accepts_several_prefixes()
    {
        const string host = "GEA_VERSION 1\nPRODUCT_NAME \"P\"\nPARAM_PREFIX GT_ TR_\nTARGET PART\nBEGIN_FORM\n  USER_INPUT_PARAM TR_A INTEGER 1\n  USER_INPUT_PARAM GT_B INTEGER 1\n  USER_INPUT_PARAM ZZ_C INTEGER 1\nEND_FORM\nBEGIN_BUILD\nEND_BUILD\n";
        var d = Assert.Single(GeaScript.Compile(host).Diagnostics);
        Assert.Equal("GEA2030", d.Code);
        Assert.Contains("ZZ_C", d.Message);
    }

    [Fact]
    public void Included_parameters_show_up_in_the_form_with_their_defaults()
    {
        var script = GeaScript.Compile(Host, Libs(("tank", TankLib)));
        var form = script.EvaluateFormNumbers(null);
        Assert.Contains(form.Fields, f => f.Name == "TR_Len" && f.Label == "Tank length");
    }

    [Fact]
    public void Unknown_prefix_is_still_rejected()
    {
        const string host = "GEA_VERSION 1\nPARAM_PREFIX GT_\nTARGET PART\nBEGIN_FORM\n  INCLUDE lib:tank\n  USER_INPUT_PARAM ZZ_Q INTEGER 1\nEND_FORM\n";
        var script = GeaScript.Compile(host, Libs(("tank", TankLib)));
        var d = Assert.Single(script.Diagnostics, d => d.Code == "GEA2030");
        Assert.Contains("ZZ_Q", d.Message);
    }

    [Fact]
    public void Prefix_of_a_library_is_not_accepted_when_the_library_is_not_included()
    {
        const string host = "GEA_VERSION 1\nPARAM_PREFIX GT_\nTARGET PART\nBEGIN_FORM\n  USER_INPUT_PARAM TR_Len LENGTH 1 mm\nEND_FORM\n";
        Assert.Contains(GeaScript.Compile(host, Libs(("tank", TankLib))).Diagnostics, d => d.Code == "GEA2030");
    }

    [Fact]
    public void Error_inside_a_library_reports_the_library_file_and_line()
    {
        const string bad = "GEA_VERSION 1\nPARAM_PREFIX TR_\nBEGIN_FORM\n  USER_INPUT_PARAM TR_A LENGTH 1 mm\n  USER_INPUT_PARAM TR_A LENGTH 2 mm\nEND_FORM\n";
        var script = GeaScript.Compile(Host, Libs(("tank", bad)));

        var d = Assert.Single(script.Diagnostics, d => d.Code == "GEA2003");
        Assert.Equal(5, d.Line);                        // dòng trong thư viện
        Assert.StartsWith("tank.gea:", d.Message);
    }

    [Fact]
    public void Error_in_the_host_after_an_include_keeps_the_original_line_number()
    {
        // Thư viện chèn thêm dòng ở phía trên, nhưng lỗi của cha phải báo dòng của file cha (dòng 7).
        const string host = "GEA_VERSION 1\nPARAM_PREFIX GT_\nTARGET PART\nBEGIN_FORM\n  INCLUDE lib:tank\n  USER_INPUT_PARAM GT_A INTEGER 1\n  USER_INPUT_PARAM GT_A INTEGER 2\nEND_FORM\n";
        var script = GeaScript.Compile(host, Libs(("tank", TankLib)));

        var d = Assert.Single(script.Diagnostics, d => d.Code == "GEA2003");
        Assert.Equal(7, d.Line);
        Assert.DoesNotContain("tank.gea", d.Message);
    }

    [Fact]
    public void Include_without_a_resolver_is_a_diagnostic_not_an_exception()
    {
        var script = GeaScript.Compile(Host);
        Assert.True(script.HasErrors);
        Assert.Contains(script.Diagnostics, d => d.Code == "GEA1012");
    }

    [Fact]
    public void Script_without_include_compiles_exactly_as_before()
    {
        const string plain = "GEA_VERSION 1\nPRODUCT_NAME \"P\"\nPARAM_PREFIX GT_\nTARGET PART\nBEGIN_FORM\n  USER_INPUT_PARAM GT_A INTEGER 1\nEND_FORM\nBEGIN_BUILD\nEND_BUILD\n";
        var script = GeaScript.Compile(plain);
        Assert.False(script.HasErrors, string.Join("\n", script.Diagnostics));
        Assert.Equal(plain, script.ExpandedText);
        Assert.Equal(plain, script.Text);
        Assert.Single(script.Parameters);
    }
}
