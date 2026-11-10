using InventorAddin.Models;

namespace Cadflow.Api;

/// <summary>
/// An orderable product: description, list of parameters and validation rules. The server and the add-in share these definitions
/// (the source file is linked from the add-in), so the web form, the server check and the re-check at the worker never drift apart.
/// </summary>
public sealed record Product(
    string Code,
    string Name,
    string Description,
    int Version,
    IReadOnlyList<ParameterDef> Parameters,
    Func<IReadOnlyDictionary<string, double>?, Dictionary<string, double>> Complete,
    Func<IReadOnlyDictionary<string, double>, IReadOnlyList<ValidationIssue>> Validate,
    string? ScriptFile = null) // the .gea file that ships with it (in the Scripts folder next to the server); null = product not built through .gea
{
    /// <param name="defaults">Defaults currently applied (including the Config tab); null = factory defaults.</param>
    public object Schema(IReadOnlyDictionary<string, double>? defaults = null) => new
    {
        code = Code,
        name = Name,
        version = Version,
        groups = Parameters.Select(d => d.Group).Distinct().ToArray(),
        parameters = Parameters.Select(d => new
        {
            name = d.Name, label = d.Label, group = d.Group, unit = d.Unit, integer = d.Integer,
            @default = d.Default, min = d.Min, max = d.Max,
        }),
        defaults = defaults ?? Complete(null),
        factory = Complete(null),
    };
}

public static class Products
{
    public static readonly IReadOnlyList<Product> All = new[]
    {
        new Product("box", "Box with lid", "Parametric box (body + lid) built in Inventor.", 1,
            BoxParams.All, BoxParams.Complete, BoxParams.Validate, ScriptFile: "box.gea"),
        new Product(TransformerParams.Code, "Power transformer",
            "Simplified power transformer: tank on a base plate, cover, radiators, conservator and bushings (levels 1-3 of the plan).", 1,
            TransformerParams.All, TransformerParams.Complete, TransformerParams.Validate, ScriptFile: "transformer.gea"),
        new Product(TurbineParams.Code, "Gas turbine",
            "Simplified industrial gas turbine: inlet, compressor with rings, combustor with a ring of cans, turbine, exhaust, enclosure and skid.", 1,
            TurbineParams.All, TurbineParams.Complete, TurbineParams.Validate, ScriptFile: "turbine.gea"),
        new Product(GeneratorParams.Code, "Water-cooled generator",
            "Simplified large water-cooled synchronous generator: box stator frame with side ribs, water cooler with pipes under the frame, top box with fans, bearing housings, rotor shaft, exciter cabinet and base frame.", 1,
            GeneratorParams.All, GeneratorParams.Complete, GeneratorParams.Validate, ScriptFile: "generator.gea"),
    };

    public static Product? Find(string? code) =>
        All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
}
