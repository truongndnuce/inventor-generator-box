using System.Collections.Generic;

namespace InventorAddin.Models;

public enum DocKind
{
    NoDocument,     // no document is open
    NotPart,        // an Assembly/Drawing/... is open
    EmptyPart,      // an empty Part, can be created right away
    ForeignContent, // a Part with other geometry/Parameters, needs confirmation
    BoxPart,        // a Part that already has a box created by the add-in
}

public sealed record DocState(DocKind Kind, string Message);

public sealed record ReadResult(IReadOnlyDictionary<string, double>? Values, string? Error);

public sealed record OperationResult(bool Ok, string Message);
