using System.Collections.Generic;

namespace InventorAddin.Gea;

// Cây cú pháp của file .gea. Mỗi nút giữ dòng/cột để báo lỗi và ghi vết thực thi.

public enum GeaBlockKind { Form, Tables, Build }

public abstract record GeaNode(int Line, int Col);

// ---------------------------------------------------------------- biểu thức

public abstract record GeaExpr(int Line, int Col) : GeaNode(Line, Col);

/// <summary>Số (có thể có đơn vị). <see cref="Value"/> đã đổi về mm hoặc độ.</summary>
public sealed record NumberExpr(int Line, int Col, double Value, GeaType Type, string Source) : GeaExpr(Line, Col)
{
    public bool IsZero => Value == 0;
}

public sealed record StringExpr(int Line, int Col, string Value) : GeaExpr(Line, Col);

public sealed record BoolExpr(int Line, int Col, bool Value) : GeaExpr(Line, Col);

public sealed record NameExpr(int Line, int Col, string Name) : GeaExpr(Line, Col);

public sealed record MemberExpr(int Line, int Col, string Target, string Member) : GeaExpr(Line, Col);

public sealed record UnaryExpr(int Line, int Col, string Op, GeaExpr Operand) : GeaExpr(Line, Col);

public sealed record BinaryExpr(int Line, int Col, string Op, GeaExpr Left, GeaExpr Right) : GeaExpr(Line, Col);

public sealed record CallExpr(int Line, int Col, string Name, IReadOnlyList<GeaExpr> Args) : GeaExpr(Line, Col);

// ---------------------------------------------------------------- lệnh

public abstract record GeaStmt(int Line, int Col) : GeaNode(Line, Col);

public sealed record DeclareVarStmt(int Line, int Col, GeaType Type, string Name, GeaExpr? Init) : GeaStmt(Line, Col);

public sealed record DeclareRefStmt(int Line, int Col, string Name) : GeaStmt(Line, Col);

public sealed record AssignStmt(int Line, int Col, string Name, GeaExpr Value) : GeaStmt(Line, Col);

public sealed record IncDecStmt(int Line, int Col, string Name, int Delta) : GeaStmt(Line, Col);

public sealed record IfBranch(GeaExpr Condition, IReadOnlyList<GeaStmt> Body);

public sealed record IfStmt(int Line, int Col, IReadOnlyList<IfBranch> Branches, IReadOnlyList<GeaStmt>? Else) : GeaStmt(Line, Col);

public sealed record ForStmt(int Line, int Col, string Var, GeaExpr From, GeaExpr To, GeaExpr? Step, IReadOnlyList<GeaStmt> Body) : GeaStmt(Line, Col);

public sealed record WhileStmt(int Line, int Col, GeaExpr Condition, IReadOnlyList<GeaStmt> Body) : GeaStmt(Line, Col);

public sealed record BreakStmt(int Line, int Col) : GeaStmt(Line, Col);

public sealed record ContinueStmt(int Line, int Col) : GeaStmt(Line, Col);

public sealed record ReturnStmt(int Line, int Col) : GeaStmt(Line, Col);

public sealed record GroupStmt(int Line, int Col, string Title, IReadOnlyList<GeaStmt> Body) : GeaStmt(Line, Col);

/// <summary>USER_INPUT_PARAM. Default/Min/Max/Step đã đổi về đơn vị nội bộ; <see cref="Unit"/> là đơn vị hiển thị.</summary>
public sealed record InputParamStmt(
    int Line, int Col, string Name, GeaType Type, GeaValue Default, string Unit,
    double? Min, double? Max, double? Step, bool Required, string? Label, string? Tooltip) : GeaStmt(Line, Col);

public sealed record CheckboxParamStmt(int Line, int Col, string Name, bool Default, string? Label, string? Tooltip) : GeaStmt(Line, Col);

public sealed record RadioParamStmt(int Line, int Col, string Name, IReadOnlyList<string> Options, string Default, string? Label, string? Tooltip) : GeaStmt(Line, Col);

public sealed record AssertStmt(int Line, int Col, GeaExpr Condition, GeaExpr Message, bool Warn) : GeaStmt(Line, Col);

public sealed record PrintStmt(int Line, int Col, GeaExpr Value) : GeaStmt(Line, Col);

public sealed record LookupStmt(int Line, int Col, string Table, string KeyColumn, GeaExpr Key, string ValueColumn, string Var) : GeaStmt(Line, Col);

public sealed record SetParamStmt(int Line, int Col, string Name, GeaExpr Value) : GeaStmt(Line, Col);

public sealed record SetParamExprStmt(int Line, int Col, string Name, GeaExpr Expression) : GeaStmt(Line, Col);

public sealed record GetParamStmt(int Line, int Col, string Name, string Var) : GeaStmt(Line, Col);

public sealed record FeatureInput(int Line, int Col, string Name, GeaExpr Value);

public sealed record FeatureOutput(int Line, int Col, string Var, string Name);

public sealed record CreateFeatureStmt(
    int Line, int Col, string SourceKind, string SourceName, string? OnRef,
    IReadOnlyList<FeatureInput> Inputs, IReadOnlyList<FeatureOutput> Outputs) : GeaStmt(Line, Col);

public sealed record SetRefNameStmt(int Line, int Col, string Ref, GeaExpr Name) : GeaStmt(Line, Col);

public sealed record GetRefNameStmt(int Line, int Col, string Ref, string Var) : GeaStmt(Line, Col);

public sealed record SetRefAttrStmt(int Line, int Col, string Ref, GeaExpr Key, GeaExpr Value) : GeaStmt(Line, Col);

public sealed record GetRefAttrStmt(int Line, int Col, string Ref, GeaExpr Key, string Var) : GeaStmt(Line, Col);

public sealed record SetAppearanceStmt(int Line, int Col, string Ref, GeaExpr Appearance) : GeaStmt(Line, Col);

public sealed record RegenStmt(int Line, int Col) : GeaStmt(Line, Col);

public sealed record ProcessingStartStmt(int Line, int Col, GeaExpr Title) : GeaStmt(Line, Col);

public sealed record ProcessingStateStmt(int Line, int Col, GeaExpr Text, GeaExpr? Percent) : GeaStmt(Line, Col);

public sealed record ProcessingEndStmt(int Line, int Col) : GeaStmt(Line, Col);

public sealed record CatchStmt(int Line, int Col, IReadOnlyList<GeaStmt> Body) : GeaStmt(Line, Col);

public sealed record ClearCatchStmt(int Line, int Col) : GeaStmt(Line, Col);

public sealed record FailStmt(int Line, int Col, GeaExpr Message) : GeaStmt(Line, Col);

// ---------------------------------------------------------------- bảng (TABLES)

public sealed record TableColumn(string Name, GeaType Type, double Factor);

public sealed record GeaTable(int Line, int Col, string Name, IReadOnlyList<TableColumn> Columns, IReadOnlyList<IReadOnlyList<GeaValue>> Rows) : GeaNode(Line, Col);

// ---------------------------------------------------------------- cả file

public sealed class GeaProgram
{
    public int Version { get; init; }
    public string ProductName { get; init; } = "";
    public string? ParamPrefix { get; init; }
    public string Target { get; init; } = "PART";
    public IReadOnlyList<GeaStmt> Form { get; init; } = new List<GeaStmt>();
    public bool HasForm { get; init; }
    public IReadOnlyList<GeaTable> Tables { get; init; } = new List<GeaTable>();
    public IReadOnlyList<GeaStmt> Build { get; init; } = new List<GeaStmt>();
}
