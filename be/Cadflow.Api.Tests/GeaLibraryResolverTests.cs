using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

public sealed class GeaLibraryResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gea-res-" + Guid.NewGuid().ToString("N")[..8]);
    private string Bundled => Path.Combine(_root, "bundled", "lib");
    private string Mine => Path.Combine(_root, "mine");

    public GeaLibraryResolverTests()
    {
        Directory.CreateDirectory(Bundled);
        Directory.CreateDirectory(Path.Combine(Mine, "lib"));
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Finds_the_library_in_the_bundled_folder()
    {
        File.WriteAllText(Path.Combine(Bundled, "tank.gea"), "bundled");
        Assert.Equal("bundled", GeaLibraryResolver.For(null, Bundled)("tank"));
    }

    [Fact]
    public void Folder_of_the_open_file_wins_over_bundled_and_lib_subfolder_wins_over_the_folder_itself()
    {
        File.WriteAllText(Path.Combine(Bundled, "tank.gea"), "bundled");
        File.WriteAllText(Path.Combine(Mine, "tank.gea"), "beside");
        Assert.Equal("beside", GeaLibraryResolver.For(Mine, Bundled)("tank"));
        File.WriteAllText(Path.Combine(Mine, "lib", "tank.gea"), "sub");
        Assert.Equal("sub", GeaLibraryResolver.For(Mine, Bundled)("tank"));
    }

    [Fact]
    public void Missing_library_returns_null()
    {
        Assert.Null(GeaLibraryResolver.For(Mine, Bundled)("nope"));
        Assert.Null(GeaLibraryResolver.For(null, Path.Combine(_root, "does-not-exist"))("nope"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void Unsafe_names_never_touch_the_disk(string name)
    {
        File.WriteAllText(Path.Combine(_root, "x.gea"), "outside");
        Assert.Null(GeaLibraryResolver.For(Mine, Bundled)(name));
    }

    [Fact]
    public void Works_together_with_Compile()
    {
        File.WriteAllText(Path.Combine(Bundled, "tank.gea"),
            "GEA_VERSION 1\nPARAM_PREFIX TR_\nBEGIN_FORM\n  USER_INPUT_PARAM TR_L LENGTH 1 mm\nEND_FORM\n");
        const string host = "GEA_VERSION 1\nPRODUCT_NAME \"H\"\nPARAM_PREFIX GT_\nTARGET PART\nBEGIN_FORM\n  INCLUDE lib:tank\nEND_FORM\nBEGIN_BUILD\nEND_BUILD\n";
        var script = GeaScript.Compile(host, GeaLibraryResolver.For(Mine, Bundled));
        Assert.False(script.HasErrors, string.Join("\n", script.Diagnostics));
        Assert.Contains(script.Parameters, p => p.Name == "TR_L");
    }
}
