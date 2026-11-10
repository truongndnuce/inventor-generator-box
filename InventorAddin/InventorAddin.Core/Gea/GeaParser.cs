using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InventorAddin.Gea;

/// <summary>
/// Hand-written line-based parser (section 15 of the spec). Each logical line is one statement; nested blocks
/// (IF, FOR, WHILE, BEGIN_GROUP, BEGIN_CATCH_ERROR, CREATE_FEATURE) are read recursively up to the matching END_*.
/// On an error it records a diagnostic, skips that line and keeps reading, so several errors can be reported at once.
/// </summary>
public sealed class GeaParser
{
    public const int SupportedVersion = 1;

    /// <summary>Statements reserved for P1-P3 (section 11): recognized but not supported yet.</summary>
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

    /// <summary>Keywords (not usable as variable or parameter names).</summary>
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

    /// <summary>Statements usable in FORM (besides declarations, assignments and control flow).</summary>
    private static readonly HashSet<string> FormOnly = new() { "BEGIN_GROUP", "USER_INPUT_PARAM", "CHECKBOX_PARAM", "RADIOBUTTON_PARAM" };

    private static readonly HashSet<string> BuildOnly = new()
    {
        "WHILE", "SET_PARAM", "SET_PARAM_EXPR", "GET_PARAM", "CREATE_FEATURE", "SET_REF_NAME", "GET_REF_NAME", "SET_REF_ATTR",
        "GET_REF_ATTR", "SET_APPEARANCE", "REGEN_MDL", "PROCESSING_BOX_START", "PROCESSING_BOX_SET_STATE", "PROCESSING_BOX_END",
        "BEGIN_CATCH_ERROR", "CLEAR_CATCH_ERROR", "FAIL",
    };

    private readonly List<GeaLine> _lines;
    private readonly List<GeaDiagnostic> _diags;
    private readonly List<string[]> _open = new(); // the blocks currently open (the end keyword of each block)
    private int _li;

    // Cursor inside the current line
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
        var extraPrefixes = new List<string>();
        bool sawVersion = false;

        // Header: the statements before the first block
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
                            Error("GEA1001", kw, "GEA_VERSION must be the first statement of the file.");
                        var v = Expect(TokKind.Number, "version number");
                        if (!int.TryParse(v.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
                            Error("GEA1003", v, "The version must be an integer.");
                        else if (version > SupportedVersion)
                            Error("GEA1001", v, $"The file uses GEA_VERSION {version}, the interpreter only supports up to {SupportedVersion}.");
                        sawVersion = true;
                        break;
                    case "PRODUCT_NAME":
                        product = Expect(TokKind.String, "product name (string)").Text;
                        break;
                    case "PARAM_PREFIX":
                        prefix = Expect(TokKind.Word, "parameter prefix").Text;
                        // The second and later prefixes (added by INCLUDE lib: into the expanded copy, or listed by the author).
                        while (!AtEnd && Peek().Kind == TokKind.Word) extraPrefixes.Add(Next().Text);
                        break;
                    case "TARGET":
                        var t = Expect(TokKind.Word, "PART or ASSEMBLY");
                        if (t.Text is not ("PART" or "ASSEMBLY")) Error("GEA1003", t, "TARGET must be PART or ASSEMBLY.");
                        target = t.Text;
                        break;
                    default:
                        Error("GEA1001", kw, $"Statement '{kw.Text}' is not allowed in the header (only GEA_VERSION, PRODUCT_NAME, PARAM_PREFIX, TARGET).");
                        continue;
                }
                EndOfLine();
            }
            catch (ParseError) { /* diagnostic already recorded */ }
        }

        if (!sawVersion) _diags.Add(new GeaDiagnostic("GEA1001", GeaSeverity.Error, 1, 1, "GEA_VERSION is missing at the top of the file."));
        if (product == null) _diags.Add(new GeaDiagnostic("GEA1001", GeaSeverity.Error, 1, 1, "PRODUCT_NAME is missing."));
        if (target == null) _diags.Add(new GeaDiagnostic("GEA1001", GeaSeverity.Error, 1, 1, "TARGET (PART or ASSEMBLY) is missing."));

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
                    if (hasForm || hasTables || hasBuild) Error("GEA1001", kw, "The FORM block must come before TABLES and BUILD, and there can be only one.", throwAfter: false);
                    TryEndOfLine();
                    hasForm = true;
                    form = ParseBlock(GeaBlockKind.Form, kw, "END_FORM", out _);
                    break;
                case "BEGIN_TABLES":
                    if (hasTables || hasBuild) Error("GEA1001", kw, "The TABLES block must come before BUILD, and there can be only one.", throwAfter: false);
                    TryEndOfLine();
                    hasTables = true;
                    tables = ParseTables(kw);
                    break;
                case "BEGIN_BUILD":
                    if (hasBuild) Error("GEA1001", kw, "There can be only one BUILD block.", throwAfter: false);
                    TryEndOfLine();
                    hasBuild = true;
                    build = ParseBlock(GeaBlockKind.Build, kw, "END_BUILD", out _);
                    break;
                default:
                    Error("GEA1001", kw, $"Statement '{kw.Text}' is outside a block. Every statement must be inside BEGIN_FORM, BEGIN_TABLES or BEGIN_BUILD.", throwAfter: false);
                    break;
            }
        }
        if (!hasBuild) _diags.Add(new GeaDiagnostic("GEA1002", GeaSeverity.Error, Math.Max(1, _lineNo), 1, "The BEGIN_BUILD ... END_BUILD block is missing."));

        return new GeaProgram
        {
            Version = version,
            ProductName = product ?? "",
            ParamPrefix = prefix,
            ExtraParamPrefixes = extraPrefixes,
            Target = target ?? "PART",
            Form = form,
            HasForm = hasForm,
            Tables = tables,
            Build = build,
        };
    }

    // ================================================================ statement blocks

    /// <summary>Reads statements until one of the end keywords is met. Returns the end keyword that was met (null at the end of the file).</summary>
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
                // ELSE_IF keeps the rest of the line for the caller
                if (!head.Is("ELSE_IF")) TryEndOfLine();
                return list;
            }
            if (head.Kind == TokKind.Word && head.Text is "BEGIN_FORM" or "BEGIN_TABLES" or "BEGIN_BUILD")
            {
                // A new file-level block: the current block is missing its END_*; let the file reader handle this line
                Error("GEA1002", opener, $"{terms[^1]} is missing for {opener.Text} at line {opener.Line} (met {head.Text} at line {head.Line}).", throwAfter: false);
                end = null;
                return list;
            }
            if (head.Kind == TokKind.Word && IsBlockEnd(head.Text))
            {
                bool closesOuter = _open.Take(_open.Count - 1).Any(t => t.Contains(head.Text));
                if (closesOuter)
                {
                    // The current block is missing its END_*: let the outer block take this line
                    Error("GEA1002", opener, $"{terms[^1]} is missing for {opener.Text} at line {opener.Line}.", throwAfter: false);
                    end = null;
                    return list;
                }
                Error("GEA1002", head, $"{head.Text} does not match any block (open block: {opener.Text} at line {opener.Line}, expected {terms[^1]}).", throwAfter: false);
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
            catch (ParseError) { /* diagnostic already recorded */ }
        }
        Error("GEA1002", opener, $"{terms[^1]} is missing for {opener.Text} at line {opener.Line}.", throwAfter: false);
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

        if (kw.Kind != TokKind.Word) throw Error("GEA1001", kw, $"A line must start with a statement or a variable name, not '{kw.Text}'.");

        // Assignment and increment/decrement: <name> = ..., <name>++
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
            throw Error("GEA1001", kw, $"Unknown statement: '{kw.Text}'. Statements are written in capitals (for example IF, SET_PARAM); an assignment is written as 'name = expression'.");
        }

        if (Reserved.Contains(kw.Text))
            throw Error("GEA1001", kw, $"Statement '{kw.Text}' is reserved but not supported in GEA_VERSION 1.");

        if (block == GeaBlockKind.Form && BuildOnly.Contains(kw.Text))
            Error("GEA2020", kw, $"Statement '{kw.Text}' can only be used in the BUILD block.", throwAfter: false);
        if (block == GeaBlockKind.Build && FormOnly.Contains(kw.Text))
            Error("GEA2020", kw, $"Statement '{kw.Text}' can only be used in the FORM block.", throwAfter: false);

        switch (kw.Text)
        {
            case "DECLARE_VARIABLE":
            {
                var typeTok = Expect(TokKind.Word, "type (INTEGER, DOUBLE, BOOL, STRING, LENGTH, ANGLE)");
                if (!GeaUnits.TryParseType(typeTok.Text, out var type) || type == GeaType.Reference)
                    throw Error("GEA1003", typeTok, $"Invalid type: '{typeTok.Text}'. Use INTEGER, DOUBLE, BOOL, STRING, LENGTH, ANGLE (use DECLARE_REFERENCE for references).");
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
                if (mode.Text == "REF") throw Error("GEA1001", mode, "FOR ... REF (iterating an array) is not supported in GEA_VERSION 1.");
                if (mode.Text != "FROM") throw Error("GEA1003", mode, "Syntax: FOR <i> FROM <a> TO <b> [STEP <s>].");
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
                var title = Expect(TokKind.String, "group title (string)");
                EndOfLine();
                var body = ParseBlock(block, kw, "END_GROUP", out _);
                return new GroupStmt(L, C, title.Text, body);
            }
            case "USER_INPUT_PARAM": return ParseInputParam(L, C);
            case "CHECKBOX_PARAM":
            {
                var name = ExpectName();
                var def = Expect(TokKind.Word, "TRUE or FALSE");
                if (def.Text is not ("TRUE" or "FALSE")) throw Error("GEA1003", def, "The default value of CHECKBOX_PARAM is TRUE or FALSE.");
                ParseLabelTooltip(out var label, out var tip, allowDefault: false, out _);
                return new CheckboxParamStmt(L, C, name.Text, def.Text == "TRUE", label, tip);
            }
            case "RADIOBUTTON_PARAM":
            {
                var name = ExpectName();
                var options = new List<string>();
                while (!AtEnd && Peek().Kind == TokKind.String) options.Add(Next().Text);
                if (options.Count == 0) throw Error("GEA1003", name, "RADIOBUTTON_PARAM needs at least one choice (string).");
                ParseLabelTooltip(out var label, out var tip, allowDefault: true, out var def);
                if (def != null && !options.Contains(def)) throw Error("GEA1003", name, $"DEFAULT \"{def}\" is not one of the choices.");
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
                var k = Expect(TokKind.String, "attribute key (string)");
                var e = ParseExpr();
                EndOfLine();
                return new SetRefAttrStmt(L, C, r.Text, new StringExpr(k.Line, k.Col, k.Text), e);
            }
            case "GET_REF_ATTR":
            {
                var r = ExpectName();
                var k = Expect(TokKind.String, "attribute key (string)");
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
                throw Error("GEA1001", kw, $"'{kw.Text}' can only be used in the header of the file.");
            case "BEGIN_FORM":
            case "BEGIN_BUILD":
            case "BEGIN_TABLES":
                throw Error("GEA1002", kw, $"{kw.Text} cannot be opened inside another block (is an END_* missing above?).");
            case "BEGIN_TABLE":
            case "COLUMN":
            case "ROW":
            case "SOURCE":
                throw Error("GEA2020", kw, $"'{kw.Text}' can only be used in the TABLES block.");
            case "FEATURE_IN":
            case "FEATURE_OUT":
                throw Error("GEA2020", kw, $"'{kw.Text}' can only be used inside CREATE_FEATURE ... END_CREATE_FEATURE.");
        }
        throw Error("GEA1001", kw, $"Unknown statement or not usable here: '{kw.Text}'.");
    }

    private GeaStmt ParseInputParam(int L, int C)
    {
        var name = ExpectName();
        var typeTok = Expect(TokKind.Word, "type (LENGTH, ANGLE, INTEGER, DOUBLE, STRING)");
        if (!GeaUnits.TryParseType(typeTok.Text, out var type) || type is GeaType.Bool or GeaType.Reference)
            throw Error("GEA1003", typeTok, "The type of USER_INPUT_PARAM must be LENGTH, ANGLE, INTEGER, DOUBLE or STRING (use CHECKBOX_PARAM for BOOL).");

        GeaValue def;
        string unit = GeaUnits.DefaultUnit(type);
        double factor = 1;
        if (type == GeaType.String)
        {
            def = GeaValue.Text(Expect(TokKind.String, "default value (string)").Text);
        }
        else
        {
            var (num, u, numTok) = SignedNumber("default value");
            if (u != null)
            {
                if (!GeaUnits.TryGet(u, out var ut, out factor) || ut != type)
                    throw Error("GEA2002", numTok, $"Unit '{u}' does not fit type {GeaUnits.TypeName(type)}.");
                unit = u;
            }
            else if (GeaOps.IsDimensioned(type)) factor = 1; // no unit written: mm or degrees
            if (type == GeaType.Integer && Math.Abs(num - Math.Round(num)) > 1e-12)
                throw Error("GEA2002", numTok, "The default value of an INTEGER must be a whole number.");
            def = GeaValue.Number(type, num * factor);
        }

        double? min = null, max = null, step = null;
        bool required = false;
        string? label = null, tip = null;
        while (!AtEnd)
        {
            var opt = Expect(TokKind.Word, "MIN, MAX, STEP, REQUIRED, LABEL or TOOLTIP");
            switch (opt.Text)
            {
                case "MIN": min = Bound(type, factor); break;
                case "MAX": max = Bound(type, factor); break;
                case "STEP": step = Bound(type, factor); break;
                case "REQUIRED": required = true; break;
                case "LABEL": label = Expect(TokKind.String, "label (string)").Text; break;
                case "TOOLTIP": tip = Expect(TokKind.String, "hint (string)").Text; break;
                default: throw Error("GEA1003", opt, $"Invalid option: '{opt.Text}'.");
            }
        }
        if (type == GeaType.String && (min != null || max != null || step != null))
            throw Error("GEA1003", typeTok, "MIN, MAX, STEP do not apply to STRING.");
        if (min != null && max != null && min > max)
            throw Error("GEA1003", name, "MIN is greater than MAX.");
        return new InputParamStmt(L, C, name.Text, type, def, unit, min, max, step, required, label, tip);
    }

    /// <summary>MIN/MAX/STEP: a number (may be negative); without a unit the unit of the parameter is used.</summary>
    private double Bound(GeaType type, double paramFactor)
    {
        if (type == GeaType.String) throw Error("GEA1003", Peek(), "MIN, MAX, STEP do not apply to STRING.");
        var (num, u, tok) = SignedNumber("number");
        if (u == null) return num * paramFactor;
        if (!GeaUnits.TryGet(u, out var ut, out var f) || ut != type)
            throw Error("GEA2002", tok, $"Unit '{u}' does not fit type {GeaUnits.TypeName(type)}.");
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
            var opt = Expect(TokKind.Word, "LABEL or TOOLTIP");
            switch (opt.Text)
            {
                case "LABEL": label = Expect(TokKind.String, "label (string)").Text; break;
                case "TOOLTIP": tooltip = Expect(TokKind.String, "hint (string)").Text; break;
                case "DEFAULT" when allowDefault: def = Expect(TokKind.String, "default choice (string)").Text; break;
                default: throw Error("GEA1003", opt, $"Invalid option: '{opt.Text}'.");
            }
        }
    }

    private GeaStmt ParseCreateFeature(GeaToken kw)
    {
        var kind = Expect(TokKind.Word, "source (builder:<Name>)");
        ExpectSymbol(":");
        var src = Expect(TokKind.Word, "builder name");
        if (kind.Text == "lib") throw Error("GEA1001", kind, "The lib: source (iFeature, iPart) is not supported in GEA_VERSION 1. Use builder:<Name>.");
        if (kind.Text != "builder") throw Error("GEA1003", kind, "The source must have the form builder:<Name>.");
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
                Error("GEA1002", kw, $"END_CREATE_FEATURE is missing for CREATE_FEATURE at line {kw.Line}.", throwAfter: false);
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
                    var n = Expect(TokKind.String, "input name (string)");
                    var e = ParseExpr();
                    EndOfLine();
                    inputs.Add(new FeatureInput(head.Line, head.Col, n.Text, e));
                }
                else if (head.Is("FEATURE_OUT"))
                {
                    var v = ExpectName();
                    var n = Expect(TokKind.String, "output name (string)");
                    EndOfLine();
                    outputs.Add(new FeatureOutput(head.Line, head.Col, v.Text, n.Text));
                }
                else if (head.Kind == TokKind.Word && (IsBlockEnd(head.Text) || head.Text is "BEGIN_FORM" or "BEGIN_TABLES" or "BEGIN_BUILD"))
                {
                    _li--; // let the outer block handle it
                    Error("GEA1002", kw, $"END_CREATE_FEATURE is missing for CREATE_FEATURE at line {kw.Line}.", throwAfter: false);
                    break;
                }
                else throw Error("GEA1001", head, "Inside CREATE_FEATURE only FEATURE_IN, FEATURE_OUT and END_CREATE_FEATURE are allowed.");
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
                if (!kw.Is("BEGIN_TABLE")) throw Error("GEA2020", kw, "Inside the TABLES block only BEGIN_TABLE ... END_TABLE is allowed.");
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
                            var ct = Expect(TokKind.Word, "column type");
                            if (!GeaUnits.TryParseType(ct.Text, out var type) || type == GeaType.Reference)
                                throw Error("GEA1003", ct, $"Invalid column type: '{ct.Text}'.");
                            double f = 1;
                            if (!AtEnd)
                            {
                                var u = Expect(TokKind.Word, "unit");
                                if (!GeaUnits.TryGet(u.Text, out var ut, out f) || ut != type)
                                    throw Error("GEA2002", u, $"Unit '{u.Text}' does not fit type {ct.Text}.");
                            }
                            EndOfLine();
                            if (rows.Count > 0) throw Error("GEA1003", k2, "COLUMN must be declared before every ROW.");
                            cols.Add(new TableColumn(cn.Text, type, f));
                        }
                        else if (k2.Is("ROW"))
                        {
                            var vals = new List<GeaValue>();
                            foreach (var col in cols)
                            {
                                if (AtEnd) throw Error("GEA1003", k2, $"ROW is missing values: {cols.Count} values are needed.");
                                vals.Add(TableCell(col));
                            }
                            EndOfLine();
                            rows.Add(vals);
                        }
                        else if (k2.Is("SOURCE")) throw Error("GEA1001", k2, "SOURCE (loading CSV/XLSX) is not supported in GEA_VERSION 1.");
                        else throw Error("GEA1001", k2, "Inside BEGIN_TABLE only COLUMN, ROW and END_TABLE are allowed.");
                    }
                    catch (ParseError) { }
                }
                if (!closed) Error("GEA1002", kw, $"END_TABLE is missing for table {name.Text}.", throwAfter: false);
                tables.Add(new GeaTable(kw.Line, kw.Col, name.Text, cols, rows));
            }
            catch (ParseError) { }
        }
        Error("GEA1002", opener, "END_TABLES is missing.", throwAfter: false);
        return tables;
    }

    private GeaValue TableCell(TableColumn col)
    {
        switch (col.Type)
        {
            case GeaType.String: return GeaValue.Text(Expect(TokKind.String, "string").Text);
            case GeaType.Bool:
                var b = Expect(TokKind.Word, "TRUE or FALSE");
                return GeaValue.Boolean(b.Text == "TRUE");
            default:
                var (num, u, tok) = SignedNumber("number");
                double f = col.Factor;
                if (u != null && (!GeaUnits.TryGet(u, out var ut, out f) || ut != col.Type))
                    throw Error("GEA2002", tok, $"Unit '{u}' does not fit column {col.Name}.");
                return GeaValue.Number(col.Type, num * f);
        }
    }

    // ================================================================ expressions

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
                throw Error("GEA1003", t, "Use '==' to compare for equality ('=' is only for assignment).");
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
        if (AtEnd) throw Error("GEA1003", LastTok(), "An expression is missing at the end of the line.");
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
                        throw Error("GEA2001", t, $"Function does not exist: '{t.Text}'. Functions: {string.Join(", ", GeaOps.Functions)}.");
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
                    throw Error("GEA1003", t, $"Function {t.Text} needs parentheses: {t.Text}(...).");
                if (Keywords.Contains(t.Text) && t.Text is not ("ERROR" or "ERROR_MESSAGE"))
                    throw Error("GEA1003", t, $"Keyword '{t.Text}' cannot be used in an expression.");
                if (!AtEnd && Peek().Is("."))
                {
                    Next();
                    var m = Expect(TokKind.Word, "member name");
                    return new MemberExpr(t.Line, t.Col, t.Text, m.Text);
                }
                return new NameExpr(t.Line, t.Col, t.Text);
        }
        throw Error("GEA1003", t, $"Unexpected '{t.Text}' in the expression.");
    }

    // ================================================================ token helpers

    private sealed class ParseError : Exception { }

    private void Begin(GeaLine line)
    {
        _toks = line.Tokens;
        _ti = 0;
        _lineNo = line.Line;
    }

    private bool AtEnd => _ti >= _toks.Count;

    private GeaToken Peek() => AtEnd ? new GeaToken(TokKind.Symbol, "<end of line>", _lineNo, LastTok().Col + 1) : _toks[_ti];

    private GeaToken LastTok() => _toks.Count > 0 ? _toks[^1] : new GeaToken(TokKind.Symbol, "", _lineNo, 1);

    private GeaToken Next()
    {
        if (AtEnd) throw Error("GEA1003", Peek(), "An argument is missing at the end of the line.");
        return _toks[_ti++];
    }

    private GeaToken Expect(TokKind kind, string what)
    {
        if (AtEnd) throw Error("GEA1003", Peek(), $"{what} is missing.");
        var t = _toks[_ti];
        if (t.Kind != kind) throw Error("GEA1003", t, $"Expected {what}, got '{t.Text}'.");
        _ti++;
        return t;
    }

    private GeaToken ExpectName()
    {
        var t = Expect(TokKind.Word, "name");
        if (Keywords.Contains(t.Text)) throw Error("GEA1003", t, $"'{t.Text}' is a keyword, it cannot be used as a name.");
        return t;
    }

    private void ExpectWord(string word)
    {
        var t = Expect(TokKind.Word, word);
        if (t.Text != word) throw Error("GEA1003", t, $"Expected '{word}', got '{t.Text}'.");
    }

    private void ExpectSymbol(string sym)
    {
        if (AtEnd) throw Error("GEA1003", Peek(), $"'{sym}' is missing.");
        var t = _toks[_ti];
        if (!t.Is(sym)) throw Error("GEA1003", t, $"Expected '{sym}', got '{t.Text}'.");
        _ti++;
    }

    private void EndOfLine()
    {
        if (!AtEnd) throw Error("GEA1003", _toks[_ti], $"Extra argument: '{_toks[_ti].Text}'.");
    }

    private void TryEndOfLine()
    {
        if (!AtEnd) Error("GEA1003", _toks[_ti], $"Extra argument: '{_toks[_ti].Text}'.", throwAfter: false);
    }

    private ParseError Error(string code, GeaToken at, string message, bool throwAfter = true)
    {
        _diags.Add(new GeaDiagnostic(code, GeaSeverity.Error, at.Line, at.Col, message));
        return new ParseError();
    }
}
