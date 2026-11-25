using System.Globalization;

namespace Cadflow.Api;

/// <summary>A material the engineer can pick on the order form. Density is in g/cm3 (= kg/dm3).</summary>
public sealed record MaterialInfo(string Code, string Name, string Standard, double Density);

/// <summary>A surface finish / appearance. Color is a CSS hex value used for the swatch on the web page.</summary>
public sealed record FinishInfo(string Code, string Name, string Process, string Color);

/// <summary>The fixed lists behind the Material and Finish choices (one place, shared by the order form, the server check and the part information card).</summary>
public static class MaterialCatalog
{
    public const string MaterialKey = "PI_Material";   // "PI_" = part information; avoids clashing with the BOM's own Material column
    public const string FinishKey = "PI_Finish";

    public static readonly IReadOnlyList<MaterialInfo> Materials = new MaterialInfo[]
    {
        new("steel-s355", "Structural steel S355", "EN 10025-2", 7.85),
        new("stainless-304", "Stainless steel 304 (1.4301)", "EN 10088-2", 8.00),
        new("alu-6061", "Aluminium 6061-T6", "EN 573-3 / EN 755", 2.70),
        new("abs", "ABS plastic", "ISO 2580", 1.05),
    };

    public static readonly IReadOnlyList<FinishInfo> Finishes = new FinishInfo[]
    {
        new("natural", "Natural (bare metal)", "None", "#B8BEC4"),
        new("ral7035", "Powder coat RAL 7035 (light grey)", "Powder coating", "#CBD0CC"),
        new("ral5010", "Powder coat RAL 5010 (gentian blue)", "Powder coating", "#0E4C92"),
        new("galvanised", "Hot-dip galvanised", "Zinc coating (EN ISO 1461)", "#9AA3A8"),
        new("black-anodised", "Black anodised", "Anodising", "#1F2328"),
    };

    public static MaterialInfo? FindMaterial(string? code) =>
        Materials.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));

    public static FinishInfo? FindFinish(string? code) =>
        Finishes.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Error text when the order carries a Material / Finish that is not in the catalog; null when fine or absent.</summary>
    public static string? Check(IReadOnlyDictionary<string, string>? texts)
    {
        if (texts == null) return null;
        if (texts.TryGetValue(MaterialKey, out var m) && !string.IsNullOrWhiteSpace(m) && FindMaterial(m) == null)
            return $"Unknown material '{m}'.";
        if (texts.TryGetValue(FinishKey, out var f) && !string.IsNullOrWhiteSpace(f) && FindFinish(f) == null)
            return $"Unknown finish '{f}'.";
        return null;
    }

    /// <summary>Keeps only the Material / Finish entries (the part information the built-in products accept), trimmed.</summary>
    public static Dictionary<string, string> Pick(IReadOnlyDictionary<string, string>? texts)
    {
        var r = new Dictionary<string, string>(StringComparer.Ordinal);
        if (texts == null) return r;
        foreach (var key in new[] { MaterialKey, FinishKey })
            if (texts.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) r[key] = v.Trim();
        return r;
    }
}

/// <summary>One line of the part information table. Color (CSS hex) is only set on the appearance row.</summary>
public sealed record PartInfoRow(string Label, string Value, string? Color = null);

public sealed record PartInfoGroup(string Title, IReadOnlyList<PartInfoRow> Rows);

/// <summary>
/// Builds the "Part information" table of a finished job from data the server already holds (inputs, text inputs, measurements, script hash),
/// plus the material catalog. Nothing here talks to Inventor, so the table also works for old jobs.
/// </summary>
public static class PartInfoBuilder
{
    private static string N(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// The table as flat text pairs for the Vault (label -> value; rows that say "Not specified" are left out), plus <c>materialCode</c> and
    /// <c>finishCode</c> for filtering. Empty when the job has no part information yet.
    /// </summary>
    public static Dictionary<string, string> Flatten(Job job)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Build(job).SelectMany(g => g.Rows))
            if (row.Value != "Not specified") d[row.Label] = row.Value;
        if (d.Count == 0) return d;
        if (job.TextInputs.TryGetValue(MaterialCatalog.MaterialKey, out var m) && MaterialCatalog.FindMaterial(m) is { } mat) d["materialCode"] = mat.Code;
        if (job.TextInputs.TryGetValue(MaterialCatalog.FinishKey, out var f) && MaterialCatalog.FindFinish(f) is { } fin) d["finishCode"] = fin.Code;
        return d;
    }

    public static IReadOnlyList<PartInfoGroup> Build(Job job)
    {
        if (job.Status != JobStatus.Succeeded || job.Kind == JobKinds.Preview) return Array.Empty<PartInfoGroup>();

        var product = Products.Find(job.ProductCode);
        var productName = product?.Name ?? job.ProductCode;
        job.TextInputs.TryGetValue(MaterialCatalog.MaterialKey, out var materialCode);
        job.TextInputs.TryGetValue(MaterialCatalog.FinishKey, out var finishCode);
        var material = MaterialCatalog.FindMaterial(materialCode);
        var finish = MaterialCatalog.FindFinish(finishCode);

        var groups = new List<PartInfoGroup>();

        // Identification: the part number follows one rule, so two engineers never name the same part differently.
        var ident = new List<PartInfoRow>
        {
            new("Part number", $"{job.ProductCode.ToUpperInvariant()}-{job.Id}"),
            new("Description", material == null ? productName : $"{productName}, {material.Name}"),
            new("Product", $"{productName} (v{job.ProductVersion})"),
            new("Designer", string.IsNullOrWhiteSpace(job.RequestedBy) ? "-" : job.RequestedBy),
            new("Created", job.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"),
        };
        groups.Add(new PartInfoGroup("Identification", ident));

        // Material and appearance
        var mat = new List<PartInfoRow>();
        if (material != null)
        {
            mat.Add(new("Material", material.Name));
            mat.Add(new("Standard", material.Standard));
            mat.Add(new("Density", $"{N(material.Density, "0.##")} g/cm3"));
        }
        else mat.Add(new("Material", "Not specified"));
        if (finish != null)
        {
            mat.Add(new("Appearance", finish.Name, finish.Color));
            mat.Add(new("Surface treatment", finish.Process));
        }
        else mat.Add(new("Appearance", "Not specified"));
        groups.Add(new PartInfoGroup("Material and appearance", mat));

        // Physical properties (measured by Inventor after the build)
        var phys = new List<PartInfoRow>();
        if (job.Summary.TryGetValue("volumeCm3", out var volume)) phys.Add(new("Volume", $"{N(volume, "0.###")} cm3"));
        if (job.Summary.TryGetValue("massKg", out var massKg)) phys.Add(new("Mass (from the model)", $"{N(massKg, "0.###")} kg"));
        else if (material != null && volume > 0 && job.ProductCode == "box")
            phys.Add(new("Mass (volume x density)", $"{N(volume * material.Density / 1000.0, "0.###")} kg"));
        if (job.Summary.TryGetValue("bodies", out var bodies)) phys.Add(new("Solid bodies", N(bodies, "0")));
        if (phys.Count > 0) groups.Add(new PartInfoGroup("Physical properties", phys));

        // Traceability: which rules (script) and which values made this part.
        var trace = new List<PartInfoRow>();
        if (job.ScriptName != null) trace.Add(new("Script", job.ScriptName));
        if (job.ScriptSha256 != null) trace.Add(new("Script hash", job.ScriptSha256.Length > 12 ? job.ScriptSha256[..12] : job.ScriptSha256));
        if (job.Summary.TryGetValue("drawingViews", out var views)) trace.Add(new("Drawing", $"{N(views, "0")} views" + (job.Summary.TryGetValue("drawingDims", out var dims) ? $", {N(dims, "0")} dimensions" : "")));
        if (trace.Count > 0) groups.Add(new PartInfoGroup("Traceability", trace));

        return groups;
    }
}
