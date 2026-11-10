using System.Collections.Generic;

namespace InventorAddin.Models;

/// <summary>A named set of parameter values. <see cref="Error"/> not null means the preset is invalid and must not be selected.</summary>
public sealed record Preset(string Name, IReadOnlyDictionary<string, double> Values, string? Error = null)
{
    public bool IsValid => Error == null;
}
