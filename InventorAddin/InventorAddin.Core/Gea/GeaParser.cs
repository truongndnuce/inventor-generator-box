using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InventorAddin.Gea;

/// <summary>
/// Parser viết tay theo dòng (mục 15 của đặc tả). Mỗi dòng logic là một lệnh; khối lồng nhau
/// (IF, FOR, WHILE, BEGIN_GROUP, BEGIN_CATCH_ERROR, CREATE_FEATURE) đọc đệ quy tới END_* tương ứng.
/// Gặp lỗi thì ghi chẩn đoán, bỏ qua dòng đó và đọc tiếp để báo được nhiều lỗi một lần.
/// </summary>
public sealed class GeaParser
{
    public const int SupportedVersion = 1;

    /// <summary>Lệnh đã đặt chỗ cho P1-P3 (mục 11): nhận ra nhưng chưa hỗ trợ.</summary>
    private static readonly HashSet<string> Reserved = new()
    {
        "INCLUDE", "CHOICE_PARAM", "MESSAGE_BOX", "MESSAGE_BOX_EX", "SET_OUTPUT", "JSON_LOAD_DOCUMENT", "JSON_GET_VALUE",
        "JSON_SET_VALUE", "ADD_ARRAY_ELEM", "GET_ARRAY_ELEM", "GET_ARRAY_SIZE", "CLEAR_ARRAY", "SET_MAP_VALUE",
        "GET_MAP_VALUE", "HAS_MAP_KEY", "DECLARE_ARRAY", "DECLARE_MAP", "REF_VALID", "SEARCH_MDL_REF", "SEARCH_MDL_REFS",
        "INVALIDATE_REF", "COPY_REF", "REF_EQUAL", "ADD_COMPONENT", "BEGIN_ASSEMBLE", "END_ASSEMBLE", "MATE", "FLUSH", "INSERT",
        "ANGLE_CONSTRAINT", "EXPORT_STEP", "EXPORT_IMAGE", "EXPORT_PDF", "DRW_NEW", "DRW_ADD_VIEW", "DRW_ADD_SECTION",
        "DRW_ADD_BALLOONS", "DRW_ADD_PARTSLIST", "DRW_ADD_NOTE", "DECLARE_STRUCT", "START_STOPWATCH", "GET_STOPWATCH_TIME",
        "FILE_OPEN", "FILE_READ_LINE", "FILE_WRITE_LINE", "FILE_CLOSE", "USER_SELECT", "USER_SELECT_MULTIPLE",
        "GET_SURFACE_NORM", "MEASURE_DISTANCE", "GET_SURFACE_CONTOUR", "GET_REF_VERTEX", "GET_REF_POS",
        "VECTOR_NORMALIZE", "VECTOR_FROM_POINTS", "FIX_FAIL_FEATURE",
    };

    /// <summary>Từ khóa (không dùng làm tên biến hoặc thông số).</summary>
    public static readonly HashSet<string> Keywords = new(Reserved)
    {
        "GEA_VERSION", "PRODUCT_NAME", "PARAM_PREFIX", "TARGET", "PART", "ASSEMBLY",
        "BEGIN_FORM", "END_FORM", "BEGIN_TABLES", "END_TABLES", "BEGIN_BUILD", "END_BUILD",
        "DECLARE_VARIABLE", "DECLARE_REFERENCE", "IF", "ELSE_IF", "ELSE", "END_IF", "FOR", "FROM", "TO", "STEP", "REF",
        "END_FOR", "WHILE", "END_WHILE", "BREAK", "CONTINUE", "RETURN", "BEGIN_GROUP", "END_GROUP", "USER_INPUT_PARAM",
        "CHECKBOX_PARAM", "RADIOBUTTON_PARAM", "MIN", "MAX", "REQUIRED", "LABEL", "TOOLTIP", "DEFAULT", "ASSERT", "WARN",
        "PRINT", "BEGIN_TABLE", "END_TABLE", "COLUMN", "ROW", "SOURCE", "LOOKUP", "SET_PARAM", "SET_PARAM_EXPR", "GET_PARAM",
        "CREATE_FEATURE", "END_CREATE_FEATURE", "FEATURE_IN", "FEATURE_OUT", "ON", "SET_REF_NAME", "GET_REF_NAME",
        "SET_REF_ATTR", "GET_REF_ATTR", "SET_APPEARANCE", "REGEN_MDL", "PROCESSING_BOX_START", "PROCESSING_BOX_SET_STATE",
        "PROCESSING_BOX_END", "BEGIN_CATCH_ERROR", "END_CATCH_ERROR", "CLEAR_CATCH_ERROR", "FAIL",
        "TRUE", "FALSE", "AND", "OR", "NOT", "ERROR", "ERROR_MESSAGE",
        "INTEGER", "DOUBLE", "BOOL", "STRING", "LENGTH", "ANGLE", "REFERENCE",
        "mm", "cm", "m", "in", "deg", "rad",
    };

    /// <summary>Lệnh dùng được ở FORM (ngoài khai báo, gán, luồng điều khiển).</summary>
    private static readonly HashSet<string> FormOnly = new() { "BEGIN_GROUP", "USER_INPUT_PARAM", "CHECKBOX_PARAM", "RADIOBUTTON_PARAM" };

    private static readonly HashSet<string> BuildOnly = new()
    {
        "WHILE", "SET_PARAM", "SET_PARAM_EXPR", "GET_PARAM", "CREATE_FEATURE", "SET_REF_NAME", "GET_REF_NAME", "SET_REF_ATTR",
        "GET_REF_ATTR", "SET_APPEARANCE", "REGEN_MDL", "PROCESSING_BOX_START", "PROCESSING_BOX_SET_STATE", "PROCESSING_BOX_END",
        "BEGIN_CATCH_ERROR", "CLEAR_CATCH_ERROR", "FAIL",
    };

    private readonly List<GeaLine> _lines;
    private readonly List<GeaDiagnostic> _diags;
    private readonly List<string[]> _open = new(); // các khối đang mở (từ kết thúc của từng khối)
    private int _li;

    // Con trỏ trong dòng hiện tại
    private IReadOnlyList<GeaToken> _toks = Array.Empty<GeaToken>();
    private int _ti;
    private int _lineNo;

    private GeaParser(List<GeaLine> lines, List<GeaDiagnostic> diags)
    {
        _lines = lines;
        _diags = diags;
    }

    public static GeaProgram Parse(string text, List<GeaDiagnostic> diagnostics)
    {
        var lines = GeaLexer.Tokenize(text, diagnostics);
        return new GeaParser(lines, diagnostics).ParseFile();
    }

    // ================================================================ khung file

    private GeaProgram ParseFile()
    {
        int version = 0;
        string? product = null, prefix = null, target = null;
        bool sawVersion = false;

        // Header: các lệnh trước khối đầu tiên
        while (_li < _lines.Count)
        {
            var head = _lines[_li].Tokens[0];
            if (head.Is("BEGIN_FORM") || head.Is("BEGIN_TABLES") || head.Is("BEGIN_BUILD")) break;
            Begin(_lines[_li++]);
            var kw = Next();
            try
            {
                switch (kw.Text)
                {
                    case "GEA_VERSION":
                        if (product != null || prefix != null || target != null)
                            Error("GEA1001", kw, "GEA_VERSION phải là lệnh đầu tiên của file.");
                        var v = Expect(TokKind.Number, "số phiên bản");
                        if (!int.TryParse(v.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
                            Error("GEA1003", v, "Phiên bản phải là số nguyên.");
                        else if (version > SupportedVersion)
                            Error("GEA1001", v, $"File dùng GEA_VERSION {version}, trình thông dịch chỉ hỗ trợ tới {SupportedVersion}.");
                        sawVersion = true;
                        break;
                    case "PRODUCT_NAME":
                        product = Expect(TokKind.String, "tên sản phẩm (chuỗi)").Text;
                        break;
                    case "PARAM_PREFIX":
                        prefix = Expect(TokKind.Word, "tiền tố thông số").Text;
                        break;
                    case "TARGET":
                        var t = Expect(TokKind.Word, "PART hoặc ASSEMBLY");
                        if (t.Text is not ("PART" or "ASSEMBLY")) Error("GEA1003", t, "TARGET phải là PART hoặc ASSEMBLY.");
                        target = t.Text;
                        break;
                    default:
                        Error("GEA1001", kw, $"Lệnh '{kw.Text}' không được dùng ở phần đầu file (chỉ GEA_VERSION, PRODUCT_NAME, PARAM_PREFIX, TARGET).");
                        continue;
                }
                EndOfLine();
            }
            catch (ParseError) { /* đã ghi chẩn đoán */ }
        }

        if (!sawVersion) _diags.Add(new GeaDiagnostic("GEA1001", GeaSeverity.Error, 1, 1, "Thiếu GEA_VERSION ở đầu file."));
        if (product == null) _diags.Add(new GeaDiagnostic("GEA1001", GeaSeverity.Error, 1, 1, "Thiếu PRODUCT_NAME."));
        if (target == null) _diags.Add(new GeaDiagnostic("GEA1001", GeaSeverity.Error, 1, 1, "Thiếu TARGET (PART hoặc ASSEMBLY)."));

        IReadOnlyList<GeaStmt> form = Array.Empty<GeaStmt>(), build = Array.Empty<GeaStmt>();
        IReadOnlyList<GeaTable> tables = Array.Empty<GeaTable>();
        bool hasForm = false, hasBuild = false, hasTables = false;

        while (_li < _lines.Count)
        {
            var line = _lines[_li++];
            Begin(line);
            var kw = Next();
            switch (kw.Text)
            {
                case "BEGIN_FORM":
                    if (hasForm || hasTables || hasBuild) Error("GEA1001", kw, "Khối FORM phải đứng trước TABLES và BUILD, và chỉ có một.", throwAfter: false);
                    TryEndOfLine();
                    hasForm = true;
                    form = ParseBlock(GeaBlockKind.Form, kw, "END_FORM", out _);
                    break;
                case "BEGIN_TABLES":
                    if (hasTables || hasBuild) Error("GEA1001", kw, "Khối TABLES phải đứng trước BUILD, và chỉ có một.", throwAfter: false);
                    TryEndOfLine();
                    hasTables = true;
                    tables = ParseTables(kw);
                    break;
                case "BEGIN_BUILD":
                    if (hasBuild) Error("GEA1001", kw, "Chỉ có một khối BUILD.", throwAfter: false);
                    TryEndOfLine();
                    hasBuild = true;
                    build = ParseBlock(GeaBlockKind.Build, kw, "END_BUILD", out _);
                    break;
                default:
                    Error("GEA1001", kw, $"Lệnh '{kw.Text}' nằm ngoài khối. Mọi lệnh phải nằm trong BEGIN_FORM, BEGIN_TABLES hoặc BEGIN_BUILD.", throwAfter: false);
                    break;
            }
        }
        if (!hasBuild) _diags.Add(new GeaDiagnostic("GEA1002", GeaSeverity.Error, Math.Max(1, _lineNo), 1, "Thiếu khối BEGIN_BUILD ... END_BUILD."));

        return new GeaProgram
        {
            Version = version,
            ProductName = product ?? "",
            ParamPrefix = prefix,
            Target = target ?? "PART",
            Form = form,
            HasForm = hasForm,
            Tables = tables,
            Build = build,
        };
    }

    // ================================================================ khối lệnh

    /// <summary>Đọc lệnh tới khi gặp một trong các từ kết thúc. Trả về từ kết thúc đã gặp (null nếu hết file).</summary>
    private List<GeaStmt> ParseBlock(GeaBlockKind block, GeaToken opener, string terminators, out GeaToken? end)
    {
        var terms = terminators.Split('|');
        var list = new List<GeaStmt>();
        _open.Add(terms);
        try
        {
            return ParseBlockBody(block, opener, terms, list, out end);
        }
        finally
        {
            _open.RemoveAt(_open.Count - 1);
        }
    }

    private List<GeaStmt> ParseBlockBody(GeaBlockKind block, GeaToken opener, string[] terms, List<GeaStmt> list, out GeaToken? end)
    {
        while (_li < _lines.Count)
        {
            var line = _lines[_li];
            var head = line.Tokens[0];
            if (head.Kind == TokKind.Word && terms.Contains(head.Text))
            {
                _li++;
                Begin(line);
                end = Next();
                // ELSE_IF giữ phần còn lại của dòng cho nơi gọi
                if (!head.Is("ELSE_IF")) TryEndOfLine();
                return list;
            }
            if (head.Kind == TokKind.Word && head.Text is "BEGIN_FORM" or "BEGIN_TABLES" or "BEGIN_BUILD")
            {
                // Khối mới ở cấp file: khối hiện tại thiếu END_*; để phần đọc file xử lý dòng này
                Error("GEA1002", opener, $"Thiếu {terms[^1]} cho {opener.Text} ở dòng {opener.Line} (gặp {head.Text} ở dòng {head.Line}).", throwAfter: false);
                end = null;
                return list;
            }
            if (head.Kind == TokKind.Word && IsBlockEnd(head.Text))
            {
                bool closesOuter = _open.Take(_open.Count - 1).Any(t => t.Contains(head.Text));
                if (closesOuter)
                {
                    // Khối hiện tại thiếu END_*: để khối ngoài nhận dòng này
                    Error("GEA1002", opener, $"Thiếu {terms[^1]} cho {opener.Text} ở dòng {opener.Line}.", throwAfter: false);
                    end = null;
                    return list;
                }
                Error("GEA1002", head, $"{head.Text} không khớp với khối nào (khối đang mở: {opener.Text} ở dòng {opener.Line}, cần {terms[^1]}).", throwAfter: false);
                _li++;
                continue;
            }
            _li++;
            Begin(line);
            try
            {
                var stmt = ParseStatement(block);
                if (stmt != null) list.Add(stmt);
            }
            catch (ParseError) { /* đã ghi chẩn đoán */ }
        }
        Error("GEA1002", opener, $"Thiếu {terms[^1]} cho {opener.Text} ở dòng {opener.Line}.", throwAfter: false);
        end = null;
        return list;
    }

    private static bool IsBlockEnd(string w) =>
        w is "END_FORM" or "END_TABLES" or "END_BUILD" or "END_IF" or "ELSE" or "ELSE_IF" or "END_FOR" or "END_WHILE"
            or "END_GROUP" or "END_CATCH_ERROR" or "END_CREATE_FEATURE" or "END_TABLE";

    private GeaStmt? ParseStatement(GeaBlockKind block)
    {
        var kw = Next();
        int L = kw.Line, C = kw.Col;

        if (kw.Kind != TokKind.Word) throw Error("GEA1001", kw, $"Dòng phải bắt đầu bằng một lệnh hoặc tên biến, không phải '{kw.Text}'.");

        // Gán và tăng giảm: <tên> = ..., <tên>++
        if (!Keywords.Contains(kw.Text) || kw.Text is "ERROR" or "ERROR_MESSAGE")
        {
            var op = Peek();
            if (op.Is("="))
            {
                Next();
                var value = ParseExpr();
                EndOfLine();
                return new AssignStmt(L, C, kw.Text, value);
            }
            if (op.Is("++") || op.Is("--"))
            {
                Next();
                EndOfLine();
                return new IncDecStmt(L, C, kw.Text, op.Text == "++" ? 1 : -1);
            }
            throw Error("GEA1001", kw, $"Lệnh không nhận ra: '{kw.Text}'. Lệnh viết hoa (ví dụ IF, SET_PARAM); gán viết dạng 'tên = biểu thức'.");
        }

        if (Reserved.Contains(kw.Text))
            throw Error("GEA1001", kw, $"Lệnh '{kw.Text}' đã được dành riêng nhưng chưa hỗ trợ ở GEA_VERSION 1.");

        if (block == GeaBlockKind.Form && BuildOnly.Contains(kw.Text))
            Error("GEA2020", kw, $"Lệnh '{kw.Text}' chỉ dùng được trong khối BUILD.", throwAfter: false);
        if (block == GeaBlockKind.Build && FormOnly.Contains(kw.Text))
            Error("GEA2020", kw, $"Lệnh '{kw.Text}' chỉ dùng được trong khối FORM.", throwAfter: false);

        switch (kw.Text)
        {
            case "DECLARE_VARIABLE":
            {
                var typeTok = Expect(TokKind.Word, "kiểu (INTEGER, DOUBLE, BOOL, STRING, LENGTH, ANGLE)");
                if (!GeaUnits.TryParseType(typeTok.Text, out var type) || type == GeaType.Reference)
                    throw Error("GEA1003", typeTok, $"Kiểu không hợp lệ: '{typeTok.Text}'. Dùng INTEGER, DOUBLE, BOOL, STRING, LENGTH, ANGLE (tham chiếu dùng DECLARE_REFERENCE).");
                var name = ExpectName();
                GeaExpr? init = AtEnd ? null : ParseExpr();
                EndOfLine();
                return new DeclareVarStmt(L, C, type, name.Text, init);
            }
            case "DECLARE_REFERENCE":
            {
                var name = ExpectName();
                EndOfLine();
                return new DeclareRefStmt(L, C, name.Text);
            }
            case "IF":
            {
                var branches = new List<IfBranch>();
                var cond = ParseExpr();
                EndOfLine();
                List<GeaStmt>? elseBody = null;
                while (true)
                {
                    var body = ParseBlock(block, kw, "ELSE_IF|ELSE|END_IF", out var end);
                    branches.Add(new IfBranch(cond, body));
                    if (end == null) break;
                    if (end.Value.Is("ELSE_IF"))
                    {
                        try
                        {
                            cond = ParseExpr();
                            EndOfLine();
                        }
                        catch (ParseError) { cond = new BoolExpr(end.Value.Line, end.Value.Col, false); }
                        continue;
                    }
                    if (end.Value.Is("ELSE"))
                    {
                        elseBody = ParseBlock(block, kw, "END_IF", out _);
                    }
                    break;
                }
                return new IfStmt(L, C, branches, elseBody);
            }
            case "FOR":
            {
                var v = ExpectName();
                var mode = Expect(TokKind.Word, "FROM");
                if (mode.Text == "REF") throw Error("GEA1001", mode, "FOR ... REF (duyệt mảng) chưa hỗ trợ ở GEA_VERSION 1.");
                if (mode.Text != "FROM") throw Error("GEA1003", mode, "Cú pháp: FOR <i> FROM <a> TO <b> [STEP <s>].");
                var from = ParseExpr();
                ExpectWord("TO");
                var to = ParseExpr();
                GeaExpr? step = null;
                if (!AtEnd)
                {
                    ExpectWord("STEP");
                    step = ParseExpr();
                }
                EndOfLine();
                var body = ParseBlock(block, kw, "END_FOR", out _);
                return new ForStmt(L, C, v.Text, from, to, step, body);
            }
            case "WHILE":
            {
                var cond = ParseExpr();
                EndOfLine();
                var body = ParseBlock(block, kw, "END_WHILE", out _);
                return new WhileStmt(L, C, cond, body);
            }
            case "BREAK": EndOfLine(); return new BreakStmt(L, C);
            case "CONTINUE": EndOfLine(); return new ContinueStmt(L, C);
            case "RETURN": EndOfLine(); return new ReturnStmt(L, C);

            case "BEGIN_GROUP":
            {
                var title = Expect(TokKind.String, "tiêu đề nhóm (chuỗi)");
                EndOfLine();
                var body = ParseBlock(block, kw, "END_GROUP", out _);
                return new GroupStmt(L, C, title.Text, body);
            }
            case "USER_INPUT_PARAM": return ParseInputParam(L, C);
            case "CHECKBOX_PARAM":
            {
                var name = ExpectName();
                var def = Expect(TokKind.Word, "TRUE hoặc FALSE");
                if (def.Text is not ("TRUE" or "FALSE")) throw Error("GEA1003", def, "Giá trị mặc định của CHECKBOX_PARAM là TRUE hoặc FALSE.");
                ParseLabelTooltip(out var label, out var tip, allowDefault: false, out _);
                return new CheckboxParamStmt(L, C, name.Text, def.Text == "TRUE", label, tip);
            }
            case "RADIOBUTTON_PARAM":
            {
                var name = ExpectName();
                var options = new List<string>();
                while (!AtEnd && Peek().Kind == TokKind.String) options.Add(Next().Text);
                if (options.Count == 0) throw Error("GEA1003", name, "RADIOBUTTON_PARAM cần ít nhất một lựa chọn (chuỗi).");
                ParseLabelTooltip(out var label, out var tip, allowDefault: true, out var def);
                if (def != null && !options.Contains(def)) throw Error("GEA1003", name, $"DEFAULT \"{def}\" không nằm trong các lựa chọn.");
                return new RadioParamStmt(L, C, name.Text, options, def ?? options[0], label, tip);
            }
            case "ASSERT":
            {
                var cond = ParseExpr();
                var msg = ParseExpr();
                bool warn = false;
                if (!AtEnd)
                {
                    ExpectWord("WARN");
                    warn = true;
                }
                EndOfLine();
                return new AssertStmt(L, C, cond, msg, warn);
            }
            case "PRINT":
            {
                var e = ParseExpr();
                EndOfLine();
                return new PrintStmt(L, C, e);
            }
            case "LOOKUP":
            {
                var table = ExpectName();
                var keyCol = ExpectName();
                var key = ParseExpr();
                var valCol = ExpectName();
                var v = ExpectName();
                EndOfLine();
                return new LookupStmt(L, C, table.Text, keyCol.Text, key, valCol.Text, v.Text);
            }
            case "SET_PARAM":
            {
                var name = ExpectName();
                var e = ParseExpr();
                EndOfLine();
                return new SetParamStmt(L, C, name.Text, e);
            }
            case "SET_PARAM_EXPR":
            {
                var name = ExpectName();
                var e = ParseExpr();
                EndOfLine();
                return new SetParamExprStmt(L, C, name.Text, e);
            }
            case "GET_PARAM":
            {
                var name = ExpectName();
                var v = ExpectName();
                EndOfLine();
                return new GetParamStmt(L, C, name.Text, v.Text);
            }
            case "CREATE_FEATURE": return ParseCreateFeature(kw);
            case "SET_REF_NAME":
            {
                var r = ExpectName();
                var e = ParseExpr();
                EndOfLine();
                return new SetRefNameStmt(L, C, r.Text, e);
            }
            case "GET_REF_NAME":
            {
                var r = ExpectName();
                var v = ExpectName();
                EndOfLine();
                return new GetRefNameStmt(L, C, r.Text, v.Text);
            }
            case "SET_REF_ATTR":
            {
                var r = ExpectName();
                var k = Expect(TokKind.String, "khóa thuộc tính (chuỗi)");
                var e = ParseExpr();
                EndOfLine();
                return new SetRefAttrStmt(L, C, r.Text, new StringExpr(k.Line, k.Col, k.Text), e);
            }
            case "GET_REF_ATTR":
            {
                var r = ExpectName();
                var k = Expect(TokKind.String, "khóa thuộc tính (chuỗi)");
                var v = ExpectName();
                EndOfLine();
                return new GetRefAttrStmt(L, C, r.Text, new StringExpr(k.Line, k.Col, k.Text), v.Text);
            }
            case "SET_APPEARANCE":
            {
                var r = ExpectName();
                var e = ParseExpr();
                EndOfLine();
                return new SetAppearanceStmt(L, C, r.Text, e);
            }
            case "REGEN_MDL": EndOfLine(); return new RegenStmt(L, C);
            case "PROCESSING_BOX_START":
            {
                var e = ParseExpr();
                EndOfLine();
                return new ProcessingStartStmt(L, C, e);
            }
            case "PROCESSING_BOX_SET_STATE":
            {
                var e = ParseExpr();
                GeaExpr? pct = AtEnd ? null : ParseExpr();
                EndOfLine();
                return new ProcessingStateStmt(L, C, e, pct);
            }
            case "PROCESSING_BOX_END": EndOfLine(); return new ProcessingEndStmt(L, C);
            case "BEGIN_CATCH_ERROR":
            {
                EndOfLine();
                var body = ParseBlock(block, kw, "END_CATCH_ERROR", out _);
                return new CatchStmt(L, C, body);
            }
            case "CLEAR_CATCH_ERROR": EndOfLine(); return new ClearCatchStmt(L, C);
            case "FAIL":
            {
                var e = ParseExpr();
                EndOfLine();
                return new FailStmt(L, C, e);
            }
            case "GEA_VERSION":
            case "PRODUCT_NAME":
            case "PARAM_PREFIX":
            case "TARGET":
                throw Error("GEA1001", kw, $"'{kw.Text}' chỉ dùng ở phần đầu file.");
            case "BEGIN_FORM":
            case "BEGIN_BUILD":
            case "BEGIN_TABLES":
                throw Error("GEA1002", kw, $"Không được mở {kw.Text} bên trong một khối khác (thiếu END_* ở trên?).");
            case "BEGIN_TABLE":
            case "COLUMN":
            case "ROW":
            case "SOURCE":
                throw Error("GEA2020", kw, $"'{kw.Text}' chỉ dùng trong khối TABLES.");
            case "FEATURE_IN":
            case "FEATURE_OUT":
                throw Error("GEA2020", kw, $"'{kw.Text}' chỉ dùng bên trong CREATE_FEATURE ... END_CREATE_FEATURE.");
        }
        throw Error("GEA1001", kw, $"Lệnh không nhận ra hoặc không dùng được ở đây: '{kw.Text}'.");
    }

    private GeaStmt ParseInputParam(int L, int C)
    {
        var name = ExpectName();
        var typeTok = Expect(TokKind.Word, "kiểu (LENGTH, ANGLE, INTEGER, DOUBLE, STRING)");
        if (!GeaUnits.TryParseType(typeTok.Text, out var type) || type is GeaType.Bool or GeaType.Reference)
            throw Error("GEA1003", typeTok, "Kiểu của USER_INPUT_PARAM phải là LENGTH, ANGLE, INTEGER, DOUBLE hoặc STRING (BOOL dùng CHECKBOX_PARAM).");

        GeaValue def;
        string unit = GeaUnits.DefaultUnit(type);
        double factor = 1;
        if (type == GeaType.String)
        {
            def = GeaValue.Text(Expect(TokKind.String, "giá trị mặc định (chuỗi)").Text);
        }
        else
        {
            var (num, u, numTok) = SignedNumber("giá trị mặc định");
            if (u != null)
            {
                if (!GeaUnits.TryGet(u, out var ut, out factor) || ut != type)
                    throw Error("GEA2002", numTok, $"Đơn vị '{u}' không hợp với kiểu {GeaUnits.TypeName(type)}.");
                unit = u;
            }
            else if (GeaOps.IsDimensioned(type)) factor = 1; // không ghi đơn vị: mm hoặc độ
            if (type == GeaType.Integer && Math.Abs(num - Math.Round(num)) > 1e-12)
                throw Error("GEA2002", numTok, "Giá trị mặc định của INTEGER phải là số nguyên.");
            def = GeaValue.Number(type, num * factor);
        }

        double? min = null, max = null, step = null;
        bool required = false;
        string? label = null, tip = null;
        while (!AtEnd)
        {
            var opt = Expect(TokKind.Word, "MIN, MAX, STEP, REQUIRED, LABEL hoặc TOOLTIP");
            switch (opt.Text)
            {
                case "MIN": min = Bound(type, factor); break;
                case "MAX": max = Bound(type, factor); break;
                case "STEP": step = Bound(type, factor); break;
                case "REQUIRED": required = true; break;
                case "LABEL": label = Expect(TokKind.String, "nhãn (chuỗi)").Text; break;
                case "TOOLTIP": tip = Expect(TokKind.String, "gợi ý (chuỗi)").Text; break;
                default: throw Error("GEA1003", opt, $"Tùy chọn không hợp lệ: '{opt.Text}'.");
            }
        }
        if (type == GeaType.String && (min != null || max != null || step != null))
            throw Error("GEA1003", typeTok, "MIN, MAX, STEP không dùng cho STRING.");
        if (min != null && max != null && min > max)
            throw Error("GEA1003", name, "MIN lớn hơn MAX.");
        return new InputParamStmt(L, C, name.Text, type, def, unit, min, max, step, required, label, tip);
    }

    /// <summary>MIN/MAX/STEP: số (có thể âm), không ghi đơn vị thì dùng đơn vị của thông số.</summary>
    private double Bound(GeaType type, double paramFactor)
    {
        if (type == GeaType.String) throw Error("GEA1003", Peek(), "MIN, MAX, STEP không dùng cho STRING.");
        var (num, u, tok) = SignedNumber("số");
        if (u == null) return num * paramFactor;
        if (!GeaUnits.TryGet(u, out var ut, out var f) || ut != type)
            throw Error("GEA2002", tok, $"Đơn vị '{u}' không hợp với kiểu {GeaUnits.TypeName(type)}.");
        return num * f;
    }

    private (double num, string? unit, GeaToken tok) SignedNumber(string what)
    {
        bool neg = false;
        var first = Peek();
        if (first.Is("-")) { Next(); neg = true; }
        var t = Expect(TokKind.Number, what);
        var v = double.Parse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
        string? unit = null;
        if (!AtEnd && Peek().Kind == TokKind.Word && GeaUnits.IsUnit(Peek().Text)) unit = Next().Text;
        return (neg ? -v : v, unit, t);
    }

    private void ParseLabelTooltip(out string? label, out string? tooltip, bool allowDefault, out string? def)
    {
        label = tooltip = def = null;
        while (!AtEnd)
        {
            var opt = Expect(TokKind.Word, "LABEL hoặc TOOLTIP");
            switch (opt.Text)
            {
                case "LABEL": label = Expect(TokKind.String, "nhãn (chuỗi)").Text; break;
                case "TOOLTIP": tooltip = Expect(TokKind.String, "gợi ý (chuỗi)").Text; break;
                case "DEFAULT" when allowDefault: def = Expect(TokKind.String, "lựa chọn mặc định (chuỗi)").Text; break;
                default: throw Error("GEA1003", opt, $"Tùy chọn không hợp lệ: '{opt.Text}'.");
            }
        }
    }

    private GeaStmt ParseCreateFeature(GeaToken kw)
    {
        var kind = Expect(TokKind.Word, "nguồn (builder:<Tên>)");
        ExpectSymbol(":");
        var src = Expect(TokKind.Word, "tên builder");
        if (kind.Text == "lib") throw Error("GEA1001", kind, "Nguồn lib: (iFeature, iPart) chưa hỗ trợ ở GEA_VERSION 1. Dùng builder:<Tên>.");
        if (kind.Text != "builder") throw Error("GEA1003", kind, "Nguồn phải có dạng builder:<Tên>.");
        string? on = null;
        if (!AtEnd)
        {
            ExpectWord("ON");
            on = ExpectName().Text;
        }
        EndOfLine();

        var inputs = new List<FeatureInput>();
        var outputs = new List<FeatureOutput>();
        while (true)
        {
            if (_li >= _lines.Count)
            {
                Error("GEA1002", kw, $"Thiếu END_CREATE_FEATURE cho CREATE_FEATURE ở dòng {kw.Line}.", throwAfter: false);
                break;
            }
            var line = _lines[_li++];
            Begin(line);
            var head = Next();
            try
            {
                if (head.Is("END_CREATE_FEATURE")) { EndOfLine(); break; }
                if (head.Is("FEATURE_IN"))
                {
                    var n = Expect(TokKind.String, "tên đầu vào (chuỗi)");
                    var e = ParseExpr();
                    EndOfLine();
                    inputs.Add(new FeatureInput(head.Line, head.Col, n.Text, e));
                }
                else if (head.Is("FEATURE_OUT"))
                {
                    var v = ExpectName();
                    var n = Expect(TokKind.String, "tên đầu ra (chuỗi)");
                    EndOfLine();
                    outputs.Add(new FeatureOutput(head.Line, head.Col, v.Text, n.Text));
                }
                else if (head.Kind == TokKind.Word && (IsBlockEnd(head.Text) || head.Text is "BEGIN_FORM" or "BEGIN_TABLES" or "BEGIN_BUILD"))
                {
                    _li--; // để khối ngoài xử lý
                    Error("GEA1002", kw, $"Thiếu END_CREATE_FEATURE cho CREATE_FEATURE ở dòng {kw.Line}.", throwAfter: false);
                    break;
                }
                else throw Error("GEA1001", head, "Trong CREATE_FEATURE chỉ dùng FEATURE_IN, FEATURE_OUT và END_CREATE_FEATURE.");
            }
            catch (ParseError) { }
        }
        return new CreateFeatureStmt(kw.Line, kw.Col, kind.Text, src.Text, on, inputs, outputs);
    }

    // ================================================================ TABLES

    private List<GeaTable> ParseTables(GeaToken opener)
    {
        var tables = new List<GeaTable>();
        while (_li < _lines.Count)
        {
            var line = _lines[_li++];
            Begin(line);
            var kw = Next();
            if (kw.Is("END_TABLES")) { TryEndOfLine(); return tables; }
            try
            {
                if (!kw.Is("BEGIN_TABLE")) throw Error("GEA2020", kw, "Trong khối TABLES chỉ dùng BEGIN_TABLE ... END_TABLE.");
                var name = ExpectName();
                EndOfLine();
                var cols = new List<TableColumn>();
                var rows = new List<IReadOnlyList<GeaValue>>();
                bool closed = false;
                while (_li < _lines.Count)
                {
                    var l2 = _lines[_li++];
                    Begin(l2);
                    var k2 = Next();
                    try
                    {
                        if (k2.Is("END_TABLE")) { EndOfLine(); closed = true; break; }
                        if (k2.Is("COLUMN"))
                        {
                            var cn = ExpectName();
                            var ct = Expect(TokKind.Word, "kiểu cột");
                            if (!GeaUnits.TryParseType(ct.Text, out var type) || type == GeaType.Reference)
                                throw Error("GEA1003", ct, $"Kiểu cột không hợp lệ: '{ct.Text}'.");
                            double f = 1;
                            if (!AtEnd)
                            {
                                var u = Expect(TokKind.Word, "đơn vị");
                                if (!GeaUnits.TryGet(u.Text, out var ut, out f) || ut != type)
                                    throw Error("GEA2002", u, $"Đơn vị '{u.Text}' không hợp với kiểu {ct.Text}.");
                            }
                            EndOfLine();
                            if (rows.Count > 0) throw Error("GEA1003", k2, "COLUMN phải khai báo trước mọi ROW.");
                            cols.Add(new TableColumn(cn.Text, type, f));
                        }
                        else if (k2.Is("ROW"))
                        {
                            var vals = new List<GeaValue>();
                            foreach (var col in cols)
                            {
                                if (AtEnd) throw Error("GEA1003", k2, $"ROW thiếu giá trị: cần {cols.Count} giá trị.");
                                vals.Add(TableCell(col));
                            }
                            EndOfLine();
                            rows.Add(vals);
                        }
                        else if (k2.Is("SOURCE")) throw Error("GEA1001", k2, "SOURCE (nạp CSV/XLSX) chưa hỗ trợ ở GEA_VERSION 1.");
                        else throw Error("GEA1001", k2, "Trong BEGIN_TABLE chỉ dùng COLUMN, ROW và END_TABLE.");
                    }
                    catch (ParseError) { }
                }
                if (!closed) Error("GEA1002", kw, $"Thiếu END_TABLE cho bảng {name.Text}.", throwAfter: false);
                tables.Add(new GeaTable(kw.Line, kw.Col, name.Text, cols, rows));
            }
            catch (ParseError) { }
        }
        Error("GEA1002", opener, "Thiếu END_TABLES.", throwAfter: false);
        return tables;
    }

    private GeaValue TableCell(TableColumn col)
    {
        switch (col.Type)
        {
            case GeaType.String: return GeaValue.Text(Expect(TokKind.String, "chuỗi").Text);
            case GeaType.Bool:
                var b = Expect(TokKind.Word, "TRUE hoặc FALSE");
                return GeaValue.Boolean(b.Text == "TRUE");
            default:
                var (num, u, tok) = SignedNumber("số");
                double f = col.Factor;
                if (u != null && (!GeaUnits.TryGet(u, out var ut, out f) || ut != col.Type))
                    throw Error("GEA2002", tok, $"Đơn vị '{u}' không hợp với cột {col.Name}.");
                return GeaValue.Number(col.Type, num * f);
        }
    }

    // ================================================================ biểu thức

    private GeaExpr ParseExpr() => ParseOr();

    private GeaExpr ParseOr()
    {
        var left = ParseAnd();
        while (!AtEnd && Peek().Is("OR"))
        {
            var op = Next();
            left = new BinaryExpr(op.Line, op.Col, "OR", left, ParseAnd());
        }
        return left;
    }

    private GeaExpr ParseAnd()
    {
        var left = ParseNot();
        while (!AtEnd && Peek().Is("AND"))
        {
            var op = Next();
            left = new BinaryExpr(op.Line, op.Col, "AND", left, ParseNot());
        }
        return left;
    }

    private GeaExpr ParseNot()
    {
        if (!AtEnd && Peek().Is("NOT"))
        {
            var op = Next();
            return new UnaryExpr(op.Line, op.Col, "NOT", ParseNot());
        }
        return ParseCmp();
    }

    private GeaExpr ParseCmp()
    {
        var left = ParseAdd();
        if (!AtEnd)
        {
            var t = Peek();
            if (t.Kind == TokKind.Symbol && t.Text is "==" or "<>" or "<" or "<=" or ">" or ">=")
            {
                Next();
                return new BinaryExpr(t.Line, t.Col, t.Text, left, ParseAdd());
            }
            if (t.Is("="))
                throw Error("GEA1003", t, "So sánh bằng dùng '==' (dấu '=' chỉ dùng để gán).");
        }
        return left;
    }

    private GeaExpr ParseAdd()
    {
        var left = ParseMul();
        while (!AtEnd && (Peek().Is("+") || Peek().Is("-")))
        {
            var op = Next();
            left = new BinaryExpr(op.Line, op.Col, op.Text, left, ParseMul());
        }
        return left;
    }

    private GeaExpr ParseMul()
    {
        var left = ParseUnary();
        while (!AtEnd && (Peek().Is("*") || Peek().Is("/") || Peek().Is("%")))
        {
            var op = Next();
            left = new BinaryExpr(op.Line, op.Col, op.Text, left, ParseUnary());
        }
        return left;
    }

    private GeaExpr ParseUnary()
    {
        if (!AtEnd && Peek().Is("-"))
        {
            var op = Next();
            return new UnaryExpr(op.Line, op.Col, "-", ParseUnary());
        }
        if (!AtEnd && Peek().Is("+"))
        {
            Next();
            return ParseUnary();
        }
        return ParsePrimary();
    }

    private GeaExpr ParsePrimary()
    {
        if (AtEnd) throw Error("GEA1003", LastTok(), "Thiếu biểu thức ở cuối dòng.");
        var t = Next();
        switch (t.Kind)
        {
            case TokKind.Number:
            {
                var v = double.Parse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!AtEnd && Peek().Kind == TokKind.Word && GeaUnits.TryGet(Peek().Text, out var ut, out var f))
                {
                    var u = Next();
                    return new NumberExpr(t.Line, t.Col, v * f, ut, t.Text + " " + u.Text);
                }
                bool isInt = t.Text.IndexOfAny(new[] { '.', 'e', 'E' }) < 0;
                return new NumberExpr(t.Line, t.Col, v, isInt ? GeaType.Integer : GeaType.Double, t.Text);
            }
            case TokKind.String:
                return new StringExpr(t.Line, t.Col, t.Text);
            case TokKind.Symbol when t.Text == "(":
            {
                var e = ParseExpr();
                ExpectSymbol(")");
                return e;
            }
            case TokKind.Word:
                if (t.Text == "TRUE") return new BoolExpr(t.Line, t.Col, true);
                if (t.Text == "FALSE") return new BoolExpr(t.Line, t.Col, false);
                if (!AtEnd && Peek().Is("("))
                {
                    if (!GeaOps.Functions.Contains(t.Text))
                        throw Error("GEA2001", t, $"Hàm không tồn tại: '{t.Text}'. Các hàm: {string.Join(", ", GeaOps.Functions)}.");
                    Next();
                    var args = new List<GeaExpr>();
                    if (!Peek().Is(")"))
                    {
                        args.Add(ParseExpr());
                        while (!AtEnd && Peek().Is(","))
                        {
                            Next();
                            args.Add(ParseExpr());
                        }
                    }
                    ExpectSymbol(")");
                    return new CallExpr(t.Line, t.Col, t.Text, args);
                }
                if (GeaOps.Functions.Contains(t.Text) && t.Text is not ("MIN" or "MAX"))
                    throw Error("GEA1003", t, $"Hàm {t.Text} cần dấu ngoặc: {t.Text}(...).");
                if (Keywords.Contains(t.Text) && t.Text is not ("ERROR" or "ERROR_MESSAGE"))
                    throw Error("GEA1003", t, $"Từ khóa '{t.Text}' không dùng được trong biểu thức.");
                if (!AtEnd && Peek().Is("."))
                {
                    Next();
                    var m = Expect(TokKind.Word, "tên thành phần");
                    return new MemberExpr(t.Line, t.Col, t.Text, m.Text);
                }
                return new NameExpr(t.Line, t.Col, t.Text);
        }
        throw Error("GEA1003", t, $"Không mong đợi '{t.Text}' trong biểu thức.");
    }

    // ================================================================ tiện ích token

    private sealed class ParseError : Exception { }

    private void Begin(GeaLine line)
    {
        _toks = line.Tokens;
        _ti = 0;
        _lineNo = line.Line;
    }

    private bool AtEnd => _ti >= _toks.Count;

    private GeaToken Peek() => AtEnd ? new GeaToken(TokKind.Symbol, "<hết dòng>", _lineNo, LastTok().Col + 1) : _toks[_ti];

    private GeaToken LastTok() => _toks.Count > 0 ? _toks[^1] : new GeaToken(TokKind.Symbol, "", _lineNo, 1);

    private GeaToken Next()
    {
        if (AtEnd) throw Error("GEA1003", Peek(), "Thiếu đối số ở cuối dòng.");
        return _toks[_ti++];
    }

    private GeaToken Expect(TokKind kind, string what)
    {
        if (AtEnd) throw Error("GEA1003", Peek(), $"Thiếu {what}.");
        var t = _toks[_ti];
        if (t.Kind != kind) throw Error("GEA1003", t, $"Cần {what}, gặp '{t.Text}'.");
        _ti++;
        return t;
    }

    private GeaToken ExpectName()
    {
        var t = Expect(TokKind.Word, "tên");
        if (Keywords.Contains(t.Text)) throw Error("GEA1003", t, $"'{t.Text}' là từ khóa, không dùng làm tên.");
        return t;
    }

    private void ExpectWord(string word)
    {
        var t = Expect(TokKind.Word, word);
        if (t.Text != word) throw Error("GEA1003", t, $"Cần '{word}', gặp '{t.Text}'.");
    }

    private void ExpectSymbol(string sym)
    {
        if (AtEnd) throw Error("GEA1003", Peek(), $"Thiếu '{sym}'.");
        var t = _toks[_ti];
        if (!t.Is(sym)) throw Error("GEA1003", t, $"Cần '{sym}', gặp '{t.Text}'.");
        _ti++;
    }

    private void EndOfLine()
    {
        if (!AtEnd) throw Error("GEA1003", _toks[_ti], $"Thừa đối số: '{_toks[_ti].Text}'.");
    }

    private void TryEndOfLine()
    {
        if (!AtEnd) Error("GEA1003", _toks[_ti], $"Thừa đối số: '{_toks[_ti].Text}'.", throwAfter: false);
    }

    private ParseError Error(string code, GeaToken at, string message, bool throwAfter = true)
    {
        _diags.Add(new GeaDiagnostic(code, GeaSeverity.Error, at.Line, at.Col, message));
        return new ParseError();
    }
}
