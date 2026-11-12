using InventorAddin.Gea;

namespace Cadflow.Api;

/// <summary>One input or output of a builder, as the Designer shows it.</summary>
/// <param name="Type">Type name as written in .gea (<c>LENGTH</c>, <c>REFERENCE</c>, ...).</param>
/// <param name="Default">The default shown as text, or null when there is none (a required input).</param>
public sealed record BuilderPortDto(string Name, string Type, bool Required, string? Default, string Description);

public sealed record BuilderDto(string Name, string Version, string Target, string Description,
    IReadOnlyList<BuilderPortDto> Inputs, IReadOnlyList<BuilderPortDto> Outputs);

/// <summary>Read-only projection of <see cref="GeaBuilderCatalog"/> for <c>GET /api/gea/builders</c>.</summary>
public static class BuilderCatalogDto
{
    public static IReadOnlyList<BuilderDto> List() => GeaBuilderCatalog.All.Select(b => new BuilderDto(
        b.Name, b.Version, b.Target, b.Description,
        b.Inputs.Select(Port).ToList(), b.Outputs.Select(Port).ToList())).ToList();

    private static BuilderPortDto Port(BuilderPort p) =>
        new(p.Name, GeaUnits.TypeName(p.Type), p.Required, p.Default?.ToString(), p.Description);
}
