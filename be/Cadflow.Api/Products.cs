using InventorAddin.Models;

namespace Cadflow.Api;

/// <summary>
/// Một sản phẩm đặt hàng được: mô tả, danh sách thông số và luật kiểm tra. Server và add-in dùng chung các định nghĩa
/// này (file nguồn được link từ add-in), nên form web, kiểm tra ở server và kiểm tra lại ở worker không bao giờ lệch nhau.
/// </summary>
public sealed record Product(
    string Code,
    string Name,
    string Description,
    int Version,
    IReadOnlyList<ParameterDef> Parameters,
    Func<IReadOnlyDictionary<string, double>?, Dictionary<string, double>> Complete,
    Func<IReadOnlyDictionary<string, double>, IReadOnlyList<ValidationIssue>> Validate)
{
    public object Schema() => new
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
        defaults = Complete(null),
    };
}

public static class Products
{
    public static readonly IReadOnlyList<Product> All = new[]
    {
        new Product("box", "Box with lid", "Parametric box (body + lid) built in Inventor.", 1,
            BoxParams.All, BoxParams.Complete, BoxParams.Validate),
        new Product(TransformerParams.Code, "Power transformer",
            "Simplified power transformer: tank on a base plate, cover, radiators, conservator and bushings (levels 1-3 of the plan).", 1,
            TransformerParams.All, TransformerParams.Complete, TransformerParams.Validate),
        new Product(TurbineParams.Code, "Gas turbine",
            "Simplified industrial gas turbine: inlet, compressor with rings, combustor with a ring of cans, turbine, exhaust, enclosure and skid.", 1,
            TurbineParams.All, TurbineParams.Complete, TurbineParams.Validate),
    };

    public static Product? Find(string? code) =>
        All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
}
