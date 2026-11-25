using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

/// <summary>The transformer assembly example (docs/examples/transformer-assembly.gea): 15 components from 7 bundled parts. No Inventor needed.</summary>
public class TransformerAssemblyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "examples"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found from " + AppContext.BaseDirectory);
    }

    private static string CoreScripts => Path.Combine(RepoRoot(), "InventorAddin", "InventorAddin.Core", "Scripts");

    private static (GeaScript Script, GeaSimulationHost Host, GeaRunResult Run) Run(IReadOnlyDictionary<string, double>? values = null)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "examples", "transformer-assembly.gea"));
        var s = GeaScript.Compile(text);
        var host = new GeaSimulationHost();
        var run = s.RunBuild(s.EvaluateFormNumbers(values), host);
        return (s, host, run);
    }

    [Fact]
    public void The_assembly_compiles_without_diagnostics_and_places_15_components_of_7_parts()
    {
        var (s, host, run) = Run();
        Assert.Empty(s.Diagnostics);
        Assert.True(run.Ok, run.Error?.Message);
        Assert.Equal(15, host.Objects.Count);
        Assert.Equal(7, host.Objects.Select(o => o.Builder).Distinct().Count());
        Assert.Equal(6, host.Objects.Count(o => o.Builder == "component:tf-bushing"));
        Assert.Equal(3, host.Objects.Count(o => o.Builder == "component:tf-radiator"));
    }

    [Fact]
    public void Names_are_unique_and_numbered()
    {
        var (_, host, _) = Run();
        var names = host.Objects.Select(o => o.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("TF_Bushing_6", names);
        Assert.Contains("TF_Radiator_3", names);
    }

    [Fact]
    public void Parameters_change_the_number_of_components()
    {
        var (_, host, run) = Run(new Dictionary<string, double> { ["TA_RadiatorCount"] = 0, ["TA_BushingsPerRow"] = 1 });
        Assert.True(run.Ok, run.Error?.Message);
        Assert.Equal(3 + 2 + 1 + 2, host.Objects.Count); // base, tank, cover + 2 legs + conservator + 2 bushings
    }

    [Fact]
    public void Stacking_is_consistent_with_the_part_dimensions()
    {
        var (_, host, _) = Run();
        double Y(string name) => host.Objects.Single(o => o.Name == name).Inputs["Y"].Num;
        Assert.Equal(0, Y("TF_Base"));
        Assert.Equal(80, Y("TF_Tank"));            // on the base plate
        Assert.Equal(80 + 1500, Y("TF_Cover"));    // on the tank
        Assert.Equal(80 + 1500 + 50, Y("TF_Bushing_1")); // on the cover
    }

    [Fact]
    public void Every_part_the_assembly_uses_is_registered_and_its_script_compiles_and_builds_one_body()
    {
        // ScriptedProduct belongs to the Windows add-in (the server does not compile it), so the registration is checked in its source text.
        var registry = File.ReadAllText(Path.Combine(RepoRoot(), "InventorAddin", "InventorAddin.Core", "Web", "ScriptedProducts.cs"));
        var (_, host, _) = Run();
        foreach (var code in host.Objects.Select(o => o.Builder["component:".Length..]).Distinct())
        {
            Assert.Contains($"Internal(\"{code}\", \"{code}.gea\"", registry);
            var file = code + ".gea";
            var script = GeaScript.Compile(File.ReadAllText(Path.Combine(CoreScripts, file)));
            Assert.False(script.HasErrors, $"{file}: " + string.Join("\n", script.Diagnostics));
            var form = script.EvaluateFormNumbers(new Dictionary<string, double>()); // the defaults of the form, as the add-in builds the part
            Assert.True(form.Ok, $"{file}: " + form.Errors.FirstOrDefault()?.Message);
            var sim = new GeaSimulationHost();
            var run = script.RunBuild(form, sim);
            Assert.True(run.Ok, $"{file}: " + run.Error?.Message);
            Assert.Single(sim.Objects); // one solid body per part
        }
    }
}
