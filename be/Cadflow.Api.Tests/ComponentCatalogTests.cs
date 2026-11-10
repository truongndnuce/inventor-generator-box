namespace Cadflow.Api.Tests;

public class ComponentCatalogTests
{
    private static ComponentCatalog Fake(params (string Name, string Text)[] libs) =>
        new(() => libs.Select(l => l.Name), n => libs.Where(l => l.Name == n).Select(l => l.Text).FirstOrDefault());

    private const string Good = """
        ! ==========================================
        ! Test library: a simple box.
        ! Second line of the first paragraph.
        !
        ! Paragraph two: more detail.
        ! ==========================================
        GEA_VERSION 1
        PRODUCT_NAME "Simple box"
        PARAM_PREFIX SB_
        TARGET PART
        BEGIN_FORM
          USER_INPUT_PARAM SB_Len LENGTH 100 mm MIN 10 MAX 500 REQUIRED LABEL "Length"
          USER_INPUT_PARAM SB_N INTEGER 3 MIN 1 MAX 9 LABEL "Count"
        END_FORM
        BEGIN_BUILD
          ! DECLARE_VARIABLE LENGTH inComment
          DECLARE_VARIABLE LENGTH hTop
          DECLARE_VARIABLE INTEGER count
          DECLARE_REFERENCE bodyRef
          hTop = SB_Len
        END_BUILD
        """;

    [Fact]
    public void Real_library_tank_base_cover_is_described()
    {
        var c = new ComponentCatalog().Get("tank-base-cover")!;

        Assert.Equal("tank-base-cover", c.Name);
        Assert.Equal("Tank, base and cover", c.Title);
        Assert.Equal(["TR_"], c.Prefixes);
        Assert.Empty(c.Errors);
        Assert.False(string.IsNullOrWhiteSpace(c.Summary));
        Assert.Equal(7, c.Parameters.Count);

        var len = Assert.Single(c.Parameters, p => p.Name == "TR_TankLength");
        Assert.Equal("Tank length", len.Label);
        Assert.Equal("Tank, base and cover", len.Group);
        Assert.Equal("mm", len.Unit);
        Assert.Equal(2000.0, Convert.ToDouble(len.Default));
        Assert.Equal(1000, len.Min);
        Assert.Equal(5000, len.Max);
        Assert.True(len.Required);

        Assert.Equal(["hCover", "hTank", "hTop", "tbcBody"], c.SharedVariables.Select(v => v.Name).OrderBy(n => n));
        Assert.Equal("LENGTH", c.SharedVariables.Single(v => v.Name == "hTop").Type);
        Assert.Equal("REFERENCE", c.SharedVariables.Single(v => v.Name == "tbcBody").Type);
    }

    [Fact]
    public void Description_and_summary_come_from_the_leading_comment_block()
    {
        var c = Fake(("box", Good)).Get("box")!;

        Assert.Equal("Test library: a simple box.\nSecond line of the first paragraph.\n\nParagraph two: more detail.", c.Description);
        Assert.Equal("Test library: a simple box. Second line of the first paragraph.", c.Summary);
    }

    [Fact]
    public void Parameters_and_variables_are_extracted_ignoring_commented_declarations()
    {
        var c = Fake(("box", Good)).Get("box")!;

        Assert.Equal(["SB_Len", "SB_N"], c.Parameters.Select(p => p.Name));
        Assert.True(c.Parameters[1].Integer);
        Assert.Equal(["hTop", "count", "bodyRef"], c.SharedVariables.Select(v => v.Name));
        Assert.DoesNotContain(c.SharedVariables, v => v.Name == "inComment");
    }

    [Fact]
    public void Broken_library_is_still_listed_with_errors()
    {
        var c = Fake(("bad", "GEA_VERSION 1\nPRODUCT_NAME \"Bad\"\nthis is not valid\n")).Get("bad")!;

        Assert.NotEmpty(c.Errors);
        Assert.Contains(c.Errors, e => e.Contains("GEA"));
        Assert.Empty(c.Parameters);
        Assert.Equal("Bad", c.Title);
    }

    [Fact]
    public void List_is_sorted_by_name_and_empty_without_libraries()
    {
        Assert.Equal(["a", "b"], Fake(("b", Good), ("a", Good)).List().Select(c => c.Name));
        Assert.Empty(Fake().List());
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("missing")]
    public void Unknown_or_unsafe_names_return_null_without_reading(string name)
    {
        var read = false;
        var c = new ComponentCatalog(() => ["box"], n => { read = true; return Good; });
        Assert.Null(c.Get(name));
        Assert.False(read);   // a wrong name or one not in the list reads nothing
    }

    [Fact]
    public void Raw_text_returns_the_original_file()
    {
        var c = Fake(("box", Good));
        Assert.Equal(Good, c.Raw("box"));
        Assert.Null(c.Raw("../box"));
        Assert.Null(c.Raw("nope"));
    }

    [Fact]
    public void Nested_include_inside_a_library_is_resolved_for_the_catalog()
    {
        const string outer = "GEA_VERSION 1\nPRODUCT_NAME \"Outer\"\nPARAM_PREFIX OT_\nTARGET PART\nBEGIN_FORM\n  INCLUDE lib:inner\nEND_FORM\nBEGIN_BUILD\nEND_BUILD\n";
        const string inner = "GEA_VERSION 1\nPRODUCT_NAME \"Inner\"\nPARAM_PREFIX IN_\nTARGET PART\nBEGIN_FORM\n  USER_INPUT_PARAM IN_A INTEGER 1\nEND_FORM\nBEGIN_BUILD\nEND_BUILD\n";
        var c = Fake(("outer", outer), ("inner", inner)).Get("outer")!;

        Assert.Empty(c.Errors);
        Assert.Contains(c.Parameters, p => p.Name == "IN_A");
        Assert.Contains("IN_", c.Prefixes);
    }
}
