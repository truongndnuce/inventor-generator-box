using InventorAddin.Gea;

namespace Cadflow.Api.Tests;

/// <summary>
/// transformer.gea using INCLUDE lib:tank-base-cover must give exactly the same result as the original (fixture, library not extracted yet):
/// the same parameters/form/rules and the same command sequence when BUILD runs on the simulated Host.
/// </summary>
public class TransformerIncludeEquivalenceTests
{
    private static string Bin(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    private static string? Lib(string name) =>
        File.Exists(Bin("Scripts", "lib", name + ".gea")) ? File.ReadAllText(Bin("Scripts", "lib", name + ".gea")) : null;

    private static GeaScript Original() => GeaScript.Compile(File.ReadAllText(Bin("Fixtures", "transformer.original.gea")));
    private static GeaScript Current() => GeaScript.Compile(File.ReadAllText(Bin("Scripts", "transformer.gea")), Lib, "transformer.gea");

    public static IEnumerable<object[]> ValueSets() =>
    [
        [new Dictionary<string, double>()],
        [new Dictionary<string, double> { ["TR_TankLength"] = 3000, ["TR_TankWidth"] = 1400, ["TR_TankHeight"] = 2000 }],
        [new Dictionary<string, double> { ["TR_BaseThickness"] = 120, ["TR_BaseOverhang"] = 200, ["TR_CoverThickness"] = 90, ["TR_CoverOverhang"] = 10 }],
        [new Dictionary<string, double> { ["TR_RadiatorCount"] = 0 }],
        [new Dictionary<string, double> { ["TR_TankLength"] = 500 }],      // violates MIN
        [new Dictionary<string, double> { ["TR_TankLength"] = 1000, ["TR_RadiatorCount"] = 6 }], // violates a cross rule
    ];

    [Fact]
    public void Transformer_now_uses_the_library()
    {
        Assert.Contains("INCLUDE lib:tank-base-cover", File.ReadAllText(Bin("Scripts", "transformer.gea")));
        Assert.NotNull(Lib("tank-base-cover"));
        Assert.False(Current().HasErrors, string.Join("\n", Current().Diagnostics));
        Assert.False(Original().HasErrors);
    }

    [Fact]
    public void Parameters_and_form_fields_are_identical()
    {
        var a = Original().EvaluateFormNumbers(null);
        var b = Current().EvaluateFormNumbers(null);
        Assert.Equal(Fields(a), Fields(b));
        Assert.Equal(Values(a), Values(b));
        Assert.Equal(Original().Parameters.Select(p => p.Name), Current().Parameters.Select(p => p.Name));
    }

    [Theory]
    [MemberData(nameof(ValueSets))]
    public void Form_results_and_build_trace_are_identical(Dictionary<string, double> values)
    {
        var o = Original();
        var c = Current();
        var fo = o.EvaluateFormNumbers(values);
        var fc = c.EvaluateFormNumbers(values);

        Assert.Equal(Fields(fo), Fields(fc));
        Assert.Equal(Values(fo), Values(fc));
        Assert.Equal(Messages(fo), Messages(fc));
        if (!fo.Ok) return;

        var ho = new GeaSimulationHost();
        var hc = new GeaSimulationHost();
        var ro = o.RunBuild(fo, ho);
        var rc = c.RunBuild(fc, hc);

        Assert.True(ro.Ok, ro.Error?.Message);
        Assert.True(rc.Ok, rc.Error?.Message);
        Assert.Contains("SET_REF_NAME TR_Tank", Trace(hc));   // do not compare two empty strings
        Assert.Equal(Trace(ho), Trace(hc));
        Assert.Equal(Objects(ho), Objects(hc));
    }

    private static string[] Fields(GeaFormResult f) => f.Fields.Select(x =>
        $"{x.Name}|{x.Type}|{x.Kind}|{x.Label}|{x.Tooltip}|{x.Group}|{x.Unit}|{x.UnitFactor}|{x.Min}|{x.Max}|{x.Step}|{x.Required}").ToArray();

    private static string[] Values(GeaFormResult f) => f.Values.Select(v => $"{v.Key}={v.Value}").ToArray();

    private static string[] Messages(GeaFormResult f) =>
        f.Errors.Concat(f.Warnings).Select(m => $"{m.Code}|{m.Message}|{string.Join(",", m.Params)}").ToArray();

    // PRINT records the script line number, so leave it out when comparing.
    private static string[] Trace(GeaSimulationHost h) => h.Trace.Where(t => !t.StartsWith("PRINT")).ToArray();

    private static string[] Objects(GeaSimulationHost h) => h.Objects.Select(o =>
        $"{o.Name}|{o.Builder}|{o.Appearance}|" + string.Join(",", o.Attributes.OrderBy(a => a.Key).Select(a => $"{a.Key}={a.Value}")) +
        "|" + string.Join(",", o.Inputs.OrderBy(i => i.Key).Select(i => $"{i.Key}={i.Value}"))).ToArray();
}
