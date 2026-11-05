using System.Collections.Generic;

namespace InventorAddin.Models;

public enum DocKind
{
    NoDocument,     // không có document nào mở
    NotPart,        // đang mở Assembly/Drawing/...
    EmptyPart,      // Part trống, tạo được ngay
    ForeignContent, // Part có hình học/Parameter khác, cần xác nhận
    BoxPart,        // Part đã có hộp do add-in tạo
}

public sealed record DocState(DocKind Kind, string Message);

public sealed record ReadResult(IReadOnlyDictionary<string, double>? Values, string? Error);

public sealed record OperationResult(bool Ok, string Message);
