using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

public class GeaIncludesTests
{
    private const string Lib = """
        GEA_VERSION 1
        PRODUCT_NAME "Tank lib"
        PARAM_PREFIX TR_
        TARGET PART
        BEGIN_FORM
          USER_INPUT_PARAM TR_Len LENGTH 100 mm
        END_FORM
        BEGIN_BUILD
          x = 1
          y = 2
        END_BUILD
        """;

    private static Func<string, string?> Libs(params (string Name, string Text)[] libs) =>
        name => libs.Where(l => l.Name == name).Select(l => l.Text).FirstOrDefault();

    private static string[] LinesOf(string s) => s.Replace("\r\n", "\n").Split('\n');

    private const string Host = """
        GEA_VERSION 1
        PARAM_PREFIX GT_
        BEGIN_FORM
          INCLUDE lib:tank
          USER_INPUT_PARAM GT_A LENGTH 1 mm
        END_FORM
        BEGIN_BUILD
          INCLUDE lib:tank
          z = 3
        END_BUILD
        """;

    [Fact]
    public void Include_inside_form_and_build_inserts_the_matching_block_body()
    {
        var r = GeaIncludes.Expand(Host, Libs(("tank", Lib)));

        Assert.Empty(r.Diagnostics);
        var lines = LinesOf(r.Text).Select(l => l.Trim()).ToArray();
        Assert.Equal(
            ["GEA_VERSION 1", "PARAM_PREFIX GT_", "BEGIN_FORM", "USER_INPUT_PARAM TR_Len LENGTH 100 mm", "USER_INPUT_PARAM GT_A LENGTH 1 mm",
             "END_FORM", "BEGIN_BUILD", "x = 1", "y = 2", "z = 3", "END_BUILD"], lines);
        Assert.DoesNotContain("INCLUDE", r.Text);
        Assert.DoesNotContain("PRODUCT_NAME", r.Text); // header của thư viện không được chèn
    }

    [Fact]
    public void Library_prefix_is_reported()
    {
        var r = GeaIncludes.Expand(Host, Libs(("tank", Lib)));
        Assert.Equal(["TR_"], r.LibPrefixes);
    }

    [Fact]
    public void Library_without_the_block_inserts_nothing()
    {
        const string formOnly = "GEA_VERSION 1\nPARAM_PREFIX TR_\nBEGIN_FORM\n  USER_INPUT_PARAM TR_Len LENGTH 1 mm\nEND_FORM\n";
        var r = GeaIncludes.Expand(Host, Libs(("tank", formOnly)));
        Assert.Empty(r.Diagnostics);
        Assert.DoesNotContain("x = 1", r.Text);
        Assert.Contains("z = 3", r.Text);
    }

    [Fact]
    public void Line_map_points_to_the_right_file_and_line()
    {
        var r = GeaIncludes.Expand(Host, Libs(("tank", Lib)), "host.gea");
        var lines = LinesOf(r.Text);
        Assert.Equal(lines.Length, r.Lines.Count);

        var i = Array.FindIndex(lines, l => l.Contains("TR_Len"));
        Assert.Equal(new GeaSourceLine("tank.gea", 6), r.Lines[i]);          // dòng 6 của thư viện
        i = Array.FindIndex(lines, l => l.Contains("GT_A"));
        Assert.Equal(new GeaSourceLine("host.gea", 5), r.Lines[i]);          // dòng 5 của script cha, dù dòng INCLUDE phía trên đã được thay
        i = Array.FindIndex(lines, l => l.Contains("z = 3"));
        Assert.Equal(new GeaSourceLine("host.gea", 9), r.Lines[i]);
    }

    [Fact]
    public void Text_without_include_is_returned_unchanged()
    {
        const string plain = "GEA_VERSION 1\nBEGIN_BUILD\n  a = 1\nEND_BUILD\n";
        var r = GeaIncludes.Expand(plain, null);
        Assert.Empty(r.Diagnostics);
        Assert.Equal(plain, r.Text);
        Assert.Empty(r.LibPrefixes);
    }

    [Fact]
    public void Nested_include_works_and_prefixes_are_collected()
    {
        const string outer = "PARAM_PREFIX AA_\nBEGIN_BUILD\n  INCLUDE lib:inner\n  o = 1\nEND_BUILD\n";
        const string inner = "PARAM_PREFIX BB_\nBEGIN_BUILD\n  i = 1\nEND_BUILD\n";
        var r = GeaIncludes.Expand("BEGIN_BUILD\n INCLUDE lib:outer\nEND_BUILD\n", Libs(("outer", outer), ("inner", inner)));
        Assert.Empty(r.Diagnostics);
        Assert.Contains("i = 1", r.Text);
        Assert.Contains("o = 1", r.Text);
        Assert.Equal(["AA_", "BB_"], r.LibPrefixes.OrderBy(p => p));
    }

    [Fact]
    public void Include_is_case_sensitive_keyword_and_allows_trailing_comment()
    {
        var r = GeaIncludes.Expand("BEGIN_BUILD\n  INCLUDE lib:tank ! the tank\nEND_BUILD\n", Libs(("tank", Lib)));
        Assert.Empty(r.Diagnostics);
        Assert.Contains("x = 1", r.Text);
    }

    [Fact]
    public void Commented_include_is_ignored()
    {
        var r = GeaIncludes.Expand("BEGIN_BUILD\n  ! INCLUDE lib:nope\nEND_BUILD\n", null);
        Assert.Empty(r.Diagnostics);
    }

    // ---------------------------------------------------------------- lỗi

    private static GeaDiagnostic Single(GeaExpansion r)
    {
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(GeaSeverity.Error, d.Severity);
        return d;
    }

    [Fact]
    public void Include_outside_a_block_is_an_error()
    {
        var d = Single(GeaIncludes.Expand("GEA_VERSION 1\nINCLUDE lib:tank\nBEGIN_BUILD\nEND_BUILD\n", Libs(("tank", Lib))));
        Assert.Equal("GEA1010", d.Code);
        Assert.Equal(2, d.Line);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("bad name")]
    public void Unsafe_library_names_are_rejected_without_calling_the_resolver(string name)
    {
        var called = false;
        var r = GeaIncludes.Expand($"BEGIN_BUILD\n  INCLUDE lib:{name}\nEND_BUILD\n", n => { called = true; return Lib; });
        Assert.Equal("GEA1011", Single(r).Code);
        Assert.False(called);
    }

    [Fact]
    public void Name_longer_than_64_chars_is_rejected()
    {
        var r = GeaIncludes.Expand($"BEGIN_BUILD\n  INCLUDE lib:{new string('a', 65)}\nEND_BUILD\n", _ => Lib);
        Assert.Equal("GEA1011", Single(r).Code);
    }

    [Fact]
    public void Missing_library_is_an_error_with_the_line()
    {
        var d = Single(GeaIncludes.Expand("BEGIN_BUILD\n  INCLUDE lib:nope\nEND_BUILD\n", Libs()));
        Assert.Equal("GEA1012", d.Code);
        Assert.Equal(2, d.Line);
        Assert.Contains("nope", d.Message);
    }

    [Fact]
    public void Include_without_a_resolver_is_an_error()
    {
        Assert.Equal("GEA1012", Single(GeaIncludes.Expand("BEGIN_BUILD\n  INCLUDE lib:tank\nEND_BUILD\n", null)).Code);
    }

    [Fact]
    public void Cycle_is_detected()
    {
        const string a = "BEGIN_BUILD\n  INCLUDE lib:b\nEND_BUILD\n";
        const string b = "BEGIN_BUILD\n  INCLUDE lib:a\nEND_BUILD\n";
        var r = GeaIncludes.Expand("BEGIN_BUILD\n  INCLUDE lib:a\nEND_BUILD\n", Libs(("a", a), ("b", b)));
        Assert.Contains(r.Diagnostics, d => d.Code == "GEA1014");
    }

    [Fact]
    public void Including_the_same_library_twice_in_one_block_kind_is_an_error()
    {
        var d = Single(GeaIncludes.Expand("BEGIN_BUILD\n  INCLUDE lib:tank\n  INCLUDE lib:tank\nEND_BUILD\n", Libs(("tank", Lib))));
        Assert.Equal("GEA1015", d.Code);
        Assert.Equal(3, d.Line);
    }

    [Fact]
    public void Depth_above_the_limit_is_an_error()
    {
        var libs = Enumerable.Range(0, GeaIncludes.MaxDepth + 2)
            .Select(i => ($"l{i}", $"BEGIN_BUILD\n  INCLUDE lib:l{i + 1}\nEND_BUILD\n")).ToArray();
        var r = GeaIncludes.Expand("BEGIN_BUILD\n  INCLUDE lib:l0\nEND_BUILD\n", Libs(libs));
        Assert.Contains(r.Diagnostics, d => d.Code == "GEA1013");
    }

    [Fact]
    public void Error_inside_a_library_names_that_library_file()
    {
        const string bad = "BEGIN_BUILD\n  INCLUDE lib:missing\nEND_BUILD\n";
        var d = Single(GeaIncludes.Expand("BEGIN_BUILD\n  INCLUDE lib:bad\nEND_BUILD\n", Libs(("bad", bad))));
        Assert.Equal("GEA1012", d.Code);
        Assert.Equal(2, d.Line);
        Assert.Contains("bad.gea", d.Message);
    }
}
