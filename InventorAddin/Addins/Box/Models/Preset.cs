using System.Collections.Generic;

namespace InventorAddin.Models;

/// <summary>Bộ giá trị thông số có tên. <see cref="Error"/> khác null nghĩa là preset không hợp lệ và không được chọn.</summary>
public sealed record Preset(string Name, IReadOnlyDictionary<string, double> Values, string? Error = null)
{
    public bool IsValid => Error == null;
}
