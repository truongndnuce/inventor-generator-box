using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Gea;

/// <summary>A parameter declared in FORM (by a *_PARAM statement).</summary>
public sealed record GeaParamInfo(string Name, GeaType Type, GeaStmt Declaration);

/// <summary>
/// Semantic analysis (sections 3, 4, 5, 9.3 of the spec): undeclared or duplicate names, types and units, parameter prefix,
/// statements used in the wrong place, CREATE_FEATURE checked against the manifest. Runs before the script runs, no Inventor needed.
/// </summary>
public sealed class GeaChecker
{
    private enum SymKind { Param, Variable, Reference, LoopVar, Builtin }

    private sealed record Sym(string Name, GeaType Type, SymKind Kind);

    private sealed class Scope
    {
        private readonly Dictionary<string, Sym> _syms = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _folded = new(StringComparer.OrdinalIgnoreCase);

        public Sym? Find(string name) => _syms.TryGetValue(name, out var s) ? s : null;

        public string? CaseClash(string name) =>
            _folded.TryGetValue(name, out var existing) && existing != name ? existing : null;

        public void Add(Sym s)
        {
            _syms[s.Name] = s;
            _folded[s.Name] = s.Name;
        }

        public void Remove(string name)
        {
            _syms.Remove(name);
            _folded.Remove(name);
        }
    }

    private readonly GeaProgram _p;
    private readonly List<GeaDiagnostic> _diags;
    private readonly List<GeaParamInfo> _params = new();
    private Scope _scope = new();
    private GeaBlockKind _block;
    private int _loopDepth;

    private GeaChecker(GeaProgram program, List<GeaDiagnostic> diagnostics)
    {
        _p = program;
        _diags = diagnostics;
    }

    public static IReadOnlyList<GeaParamInfo> Check(GeaProgram program, List<GeaDiagnostic> diagnostics)
    {
        var c = new GeaChecker(program, diagnostics);
        c.Run();
        return c._params;
    }

    private void Run()
    {
        if (_p.Target == "ASSEMBLY")
            Warn("GEA2020", 1, 1, "TARGET ASSEMBLY is not supported by the Inventor Host in this version yet; it can only be dry-run.");

        _block = GeaBlockKind.Form;
        Stmts(_p.Form);

        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in _p.Tables)
        {
            if (!tableNames.Add(t.Name)) Error("GEA2003", t.Line, t.Col, $"Table '{t.Name}' is declared twice.");
            var cols = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in t.Columns)
                if (!cols.Add(c.Name)) Error("GEA2003", t.Line, t.Col, $"Column '{c.Name}' is duplicated in table {t.Name}.");
        }

        // BUILD sees every FORM parameter (read-only) and the two built-in error variables
        _block = GeaBlockKind.Build;
        _scope = new Scope();
        foreach (var p in _params) _scope.Add(new Sym(p.Name, p.Type, SymKind.Param));
        _scope.Add(new Sym("ERROR", GeaType.Bool, SymKind.Builtin));
        _scope.Add(new Sym("ERROR_MESSAGE", GeaType.String, SymKind.Builtin));
        Stmts(_p.Build);
    }

    // ================================================================ statements

    private void Stmts(IReadOnlyList<GeaStmt> list)
    {
        foreach (var s in list) Stmt(s);
    }

    private void Stmt(GeaStmt s)
    {
        switch (s)
        {
            case DeclareVarStmt d:
                if (d.Init != null) ExpectAssignable(d.Type, d.Init, $"Initial value of {d.Name}");
                Declare(d.Name, d.Type, SymKind.Variable, d.Line, d.Col);
                break;
            case DeclareRefStmt d:
                Declare(d.Name, GeaType.Reference, SymKind.Reference, d.Line, d.Col);
                break;
            case AssignStmt a:
            {
                var sym = Writable(a.Name, a.Line, a.Col);
                var t = TypeOf(a.Value);
                if (sym != null && !GeaOps.Assignable(sym.Type, t, IsZero(a.Value)))
                    Error("GEA2002", a.Line, a.Col, $"Cannot assign {Name(t)} to {a.Name} ({Name(sym.Type)}).{Hint(sym.Type, t)}");
                break;
            }
            case IncDecStmt i:
            {
                var sym = Writable(i.Name, i.Line, i.Col);
                if (sym != null && sym.Type != GeaType.Integer)
                    Error("GEA2002", i.Line, i.Col, $"++ and -- only work on INTEGER ({i.Name} is {Name(sym.Type)}).");
                break;
            }
            case IfStmt f:
                foreach (var b in f.Branches)
                {
                    ExpectType(GeaType.Bool, b.Condition, "IF condition");
                    Stmts(b.Body);
                }
                if (f.Else != null) Stmts(f.Else);
                break;
            case ForStmt f:
            {
                ExpectType(GeaType.Integer, f.From, "FROM");
                ExpectType(GeaType.Integer, f.To, "TO");
                if (f.Step != null)
                {
                    ExpectType(GeaType.Integer, f.Step, "STEP");
                    if (f.Step is NumberExpr { Value: 0 }) Error("GEA2002", f.Step.Line, f.Step.Col, "STEP must not be 0.");
                }
                bool added = false;
                if (_scope.Find(f.Var) != null) Error("GEA2003", f.Line, f.Col, $"Loop variable '{f.Var}' clashes with an already declared name.");
                else
                {
                    CheckCase(f.Var, f.Line, f.Col);
                    _scope.Add(new Sym(f.Var, GeaType.Integer, SymKind.LoopVar));
                    added = true;
                }
                _loopDepth++;
                Stmts(f.Body);
                _loopDepth--;
                if (added) _scope.Remove(f.Var);
                break;
            }
            case WhileStmt w:
                ExpectType(GeaType.Bool, w.Condition, "WHILE condition");
                _loopDepth++;
                Stmts(w.Body);
                _loopDepth--;
                break;
            case BreakStmt or ContinueStmt:
                if (_loopDepth == 0) Error("GEA2020", s.Line, s.Col, "BREAK and CONTINUE only work inside FOR or WHILE.");
                break;
            case ReturnStmt:
                break;
            case GroupStmt g:
                Stmts(g.Body);
                break;
            case InputParamStmt ip:
                DeclareParam(ip.Name, ip.Type, ip);
                break;
            case CheckboxParamStmt cb:
                DeclareParam(cb.Name, GeaType.Bool, cb);
                break;
            case RadioParamStmt rb:
                if (rb.Options.Distinct().Count() != rb.Options.Count)
                    Error("GEA2003", rb.Line, rb.Col, $"{rb.Name}: the choices are duplicated.");
                DeclareParam(rb.Name, GeaType.String, rb);
                break;
            case AssertStmt a:
                ExpectType(GeaType.Bool, a.Condition, "ASSERT condition");
                ExpectType(GeaType.String, a.Message, "ASSERT message");
                break;
            case PrintStmt pr:
                TypeOf(pr.Value);
                break;
            case LookupStmt lk:
            {
                var table = _p.Tables.FirstOrDefault(t => t.Name == lk.Table);
                var keyType = TypeOf(lk.Key);
                var sym = Writable(lk.Var, lk.Line, lk.Col);
                if (table == null)
                {
                    Error("GEA2001", lk.Line, lk.Col, $"Table '{lk.Table}' is not declared in BEGIN_TABLES.");
                    break;
                }
                var kc = table.Columns.FirstOrDefault(c => c.Name == lk.KeyColumn);
                var vc = table.Columns.FirstOrDefault(c => c.Name == lk.ValueColumn);
                if (kc == null) Error("GEA2001", lk.Line, lk.Col, $"Table {lk.Table} has no column '{lk.KeyColumn}'.");
                else if (!GeaOps.Assignable(kc.Type, keyType) && !(kc.Type == GeaType.Integer && keyType == GeaType.Double))
                    Error("GEA2002", lk.Key.Line, lk.Key.Col, $"Key {Name(keyType)} cannot be compared with column {kc.Name} ({Name(kc.Type)}).");
                if (vc == null) Error("GEA2001", lk.Line, lk.Col, $"Table {lk.Table} has no column '{lk.ValueColumn}'.");
                else if (sym != null && !GeaOps.Assignable(sym.Type, vc.Type))
                    Error("GEA2002", lk.Line, lk.Col, $"Column {vc.Name} ({Name(vc.Type)}) cannot be assigned to {lk.Var} ({Name(sym.Type)}).");
                break;
            }
            case SetParamStmt sp:
            {
                ParamNameForHost(sp.Name, sp.Line, sp.Col);
                var t = TypeOf(sp.Value);
                if (t == GeaType.Reference) Error("GEA2002", sp.Line, sp.Col, "SET_PARAM does not accept a reference.");
                break;
            }
            case SetParamExprStmt se:
                ParamNameForHost(se.Name, se.Line, se.Col);
                ExpectType(GeaType.String, se.Expression, "Inventor expression");
                break;
            case GetParamStmt gp:
            {
                var sym = Writable(gp.Var, gp.Line, gp.Col);
                if (sym != null && sym.Type == GeaType.Reference) Error("GEA2002", gp.Line, gp.Col, "GET_PARAM needs a value variable, not a reference.");
                break;
            }
            case CreateFeatureStmt cf:
                CreateFeature(cf);
                break;
            case SetRefNameStmt rn:
                RefArg(rn.Ref, rn.Line, rn.Col);
                ExpectType(GeaType.String, rn.Name, "Name");
                break;
            case GetRefNameStmt gn:
            {
                RefArg(gn.Ref, gn.Line, gn.Col);
                var sym = Writable(gn.Var, gn.Line, gn.Col);
                if (sym != null && sym.Type != GeaType.String) Error("GEA2002", gn.Line, gn.Col, $"GET_REF_NAME needs a STRING variable ({gn.Var} is {Name(sym.Type)}).");
                break;
            }
            case SetRefAttrStmt ra:
            {
                RefArg(ra.Ref, ra.Line, ra.Col);
                var t = TypeOf(ra.Value);
                if (t == GeaType.Reference) Error("GEA2002", ra.Line, ra.Col, "An attribute value must not be a reference.");
                break;
            }
            case GetRefAttrStmt ga:
            {
                RefArg(ga.Ref, ga.Line, ga.Col);
                var sym = Writable(ga.Var, ga.Line, ga.Col);
                if (sym != null && sym.Type == GeaType.Reference) Error("GEA2002", ga.Line, ga.Col, "GET_REF_ATTR needs a value variable.");
                break;
            }
            case SetAppearanceStmt ap:
                RefArg(ap.Ref, ap.Line, ap.Col);
                ExpectType(GeaType.String, ap.Appearance, "Appearance name");
                break;
            case RegenStmt or ProcessingEndStmt or ClearCatchStmt:
                break;
            case ProcessingStartStmt ps:
                ExpectType(GeaType.String, ps.Title, "Title");
                break;
            case ProcessingStateStmt st:
                ExpectType(GeaType.String, st.Text, "Step text");
                if (st.Percent != null)
                {
                    var t = TypeOf(st.Percent);
                    if (!GeaOps.IsPlainNumber(t) && t != GeaType.Unknown) Error("GEA2002", st.Percent.Line, st.Percent.Col, "The percentage must be a number.");
                }
                break;
            case CatchStmt c:
                Stmts(c.Body);
                break;
            case FailStmt fl:
                ExpectType(GeaType.String, fl.Message, "FAIL message");
                break;
            default:
                Error("GEA1001", s.Line, s.Col, $"Statement not supported: {s.GetType().Name}.");
                break;
        }
    }

    private void CreateFeature(CreateFeatureStmt cf)
    {
        var m = GeaBuilderCatalog.Find(cf.SourceName);
        if (m == null)
        {
            Error("GEA2013", cf.Line, cf.Col,
                $"Builder '{cf.SourceName}' does not exist. Available: {string.Join(", ", GeaBuilderCatalog.All.Select(b => b.Name))}.");
            foreach (var i in cf.Inputs) TypeOf(i.Value);
            return;
        }
        if (cf.OnRef != null) RefArg(cf.OnRef, cf.Line, cf.Col);
        if (m.Target != _p.Target) Error("GEA2013", cf.Line, cf.Col, $"Builder {m.Name} is for {m.Target}, the file has TARGET {_p.Target}.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in cf.Inputs)
        {
            var t = TypeOf(i.Value);
            if (!seen.Add(i.Name)) Error("GEA2003", i.Line, i.Col, $"Input \"{i.Name}\" is passed twice.");
            var port = m.Input(i.Name);
            if (port == null)
            {
                Error("GEA2010", i.Line, i.Col, $"Builder {m.Name} has no input \"{i.Name}\". Available: {string.Join(", ", m.Inputs.Select(p => p.Name))}.");
                continue;
            }
            if (!GeaOps.Assignable(port.Type, t, IsZero(i.Value)))
                Error("GEA2002", i.Value.Line, i.Value.Col, $"Input \"{i.Name}\" of {m.Name} needs {Name(port.Type)}, got {Name(t)}.{Hint(port.Type, t)}");
        }
        foreach (var p in m.Inputs.Where(p => p.Required && !seen.Contains(p.Name)))
            Error("GEA2011", cf.Line, cf.Col, $"Required input \"{p.Name}\" of builder {m.Name} is missing.");

        foreach (var o in cf.Outputs)
        {
            var port = m.Output(o.Name);
            if (port == null)
                Error("GEA2012", o.Line, o.Col, $"Builder {m.Name} has no output \"{o.Name}\". Available: {string.Join(", ", m.Outputs.Select(p => p.Name))}.");
            var sym = Writable(o.Var, o.Line, o.Col);
            if (sym != null && port != null && sym.Type != port.Type)
                Error("GEA2002", o.Line, o.Col, $"Output \"{o.Name}\" is {Name(port.Type)}, variable {o.Var} is {Name(sym.Type)} (declare it with DECLARE_REFERENCE).");
        }
    }

    // ================================================================ declarations

    private void Declare(string name, GeaType type, SymKind kind, int line, int col)
    {
        var existing = _scope.Find(name);
        if (existing != null)
        {
            var what = existing.Kind == SymKind.Param ? "parameter" : "variable";
            Error("GEA2003", line, col, $"'{name}' is already declared (as a {what}).");
            return;
        }
        CheckCase(name, line, col);
        _scope.Add(new Sym(name, type, kind));
    }

    private void DeclareParam(string name, GeaType type, GeaStmt decl)
    {
        if (_p.ParamPrefix is { Length: > 0 } prefix && !_p.HasAllowedPrefix(name))
            Error("GEA2030", decl.Line, decl.Col, $"Parameter '{name}' must start with the prefix {prefix} (PARAM_PREFIX).");
        if (_params.Any(p => p.Name == name))
        {
            Error("GEA2003", decl.Line, decl.Col, $"Parameter '{name}' is declared twice.");
            return;
        }
        Declare(name, type, SymKind.Param, decl.Line, decl.Col);
        _params.Add(new GeaParamInfo(name, type, decl));
    }

    private void CheckCase(string name, int line, int col)
    {
        var clash = _scope.CaseClash(name);
        if (clash != null)
            Error("GEA2003", line, col, $"'{name}' differs from '{clash}' only in letter case. Two such names are not allowed (Inventor may not tell them apart).");
    }

    private Sym? Writable(string name, int line, int col)
    {
        var sym = _scope.Find(name);
        if (sym == null)
        {
            Error("GEA2001", line, col, $"'{name}' is not declared.{DeclareHint(name)}");
            return null;
        }
        switch (sym.Kind)
        {
            case SymKind.Param when _block == GeaBlockKind.Build:
                Error("GEA2002", line, col, $"FORM parameter '{name}' is read-only in BUILD. Declare a variable of your own for calculations.");
                return null;
            case SymKind.Param:
                Error("GEA2002", line, col, $"Do not assign directly to parameter '{name}'; its value is entered by the user.");
                return null;
            case SymKind.Builtin:
                Error("GEA2002", line, col, $"'{name}' is a built-in variable, read-only.");
                return null;
            case SymKind.LoopVar:
                Error("GEA2002", line, col, $"Do not assign to loop variable '{name}'.");
                return null;
        }
        return sym;
    }

    private void RefArg(string name, int line, int col)
    {
        var sym = _scope.Find(name);
        if (sym == null) Error("GEA2001", line, col, $"Reference '{name}' is not declared (use DECLARE_REFERENCE).");
        else if (sym.Type != GeaType.Reference) Error("GEA2002", line, col, $"'{name}' is not a reference (it is {Name(sym.Type)}).");
    }

    private void ParamNameForHost(string name, int line, int col)
    {
        var sym = _scope.Find(name);
        if (sym is { Kind: SymKind.Param })
            Error("GEA2002", line, col, $"'{name}' is a FORM parameter, synchronized into Inventor automatically; do not set it again with SET_PARAM.");
        if (_p.ParamPrefix is { Length: > 0 } prefix && !_p.HasAllowedPrefix(name))
            Error("GEA2030", line, col, $"Parameter '{name}' must start with the prefix {prefix}.");
    }

    // ================================================================ expressions

    private GeaType TypeOf(GeaExpr e)
    {
        switch (e)
        {
            case NumberExpr n: return n.Type;
            case StringExpr: return GeaType.String;
            case BoolExpr: return GeaType.Bool;
            case NameExpr n:
            {
                var sym = _scope.Find(n.Name);
                if (sym == null)
                {
                    Error("GEA2001", n.Line, n.Col, $"'{n.Name}' is not declared.{DeclareHint(n.Name)}");
                    return GeaType.Unknown;
                }
                return sym.Type;
            }
            case MemberExpr m:
                Error("GEA1001", m.Line, m.Col, $"'{m.Target}.{m.Member}' (a member of a map) is not supported in GEA_VERSION 1.");
                return GeaType.Unknown;
            case UnaryExpr u:
            {
                var t = TypeOf(u.Operand);
                if (t == GeaType.Unknown) return t;
                if (u.Op == "NOT")
                {
                    if (t != GeaType.Bool) Error("GEA2002", u.Line, u.Col, $"NOT needs BOOL, got {Name(t)}.");
                    return GeaType.Bool;
                }
                if (t is GeaType.Integer or GeaType.Double or GeaType.Length or GeaType.Angle) return t;
                Error("GEA2002", u.Line, u.Col, $"The minus sign does not work on {Name(t)}.");
                return GeaType.Unknown;
            }
            case BinaryExpr b:
            {
                var l = TypeOf(b.Left);
                var r = TypeOf(b.Right);
                var res = GeaOps.Binary(b.Op, l, r, IsZero(b.Left), IsZero(b.Right), out var err);
                if (res == null)
                {
                    Error("GEA2002", b.Line, b.Col, err!);
                    return GeaType.Unknown;
                }
                return res.Value;
            }
            case CallExpr c:
            {
                var args = c.Args.Select(TypeOf).ToList();
                var res = GeaOps.Function(c.Name, args, out var err);
                if (res == null)
                {
                    Error("GEA2002", c.Line, c.Col, err!);
                    return GeaType.Unknown;
                }
                return res.Value;
            }
        }
        return GeaType.Unknown;
    }

    private void ExpectType(GeaType expected, GeaExpr e, string what)
    {
        var t = TypeOf(e);
        if (!GeaOps.Assignable(expected, t, IsZero(e)))
            Error("GEA2002", e.Line, e.Col, $"{what} must be {Name(expected)}, got {Name(t)}.{Hint(expected, t)}");
    }

    private void ExpectAssignable(GeaType target, GeaExpr e, string what)
    {
        var t = TypeOf(e);
        if (!GeaOps.Assignable(target, t, IsZero(e)))
            Error("GEA2002", e.Line, e.Col, $"{what}: cannot assign {Name(t)} to {Name(target)}.{Hint(target, t)}");
    }

    private static bool IsZero(GeaExpr e) => e is NumberExpr { Value: 0 } || e is UnaryExpr { Op: "-", Operand: NumberExpr { Value: 0 } };

    private static string Name(GeaType t) => GeaUnits.TypeName(t);

    private static string Hint(GeaType target, GeaType value) =>
        GeaOps.IsDimensioned(target) && GeaOps.IsPlainNumber(value)
            ? $" Multiply by 1 {GeaUnits.DefaultUnit(target)} or write the number with a unit."
            : target == GeaType.Integer && value == GeaType.Double
                ? " Use ROUND(x), FLOOR(x) or CEIL(x) to convert to INTEGER."
                : target == GeaType.String && value != GeaType.String && value != GeaType.Unknown
                    ? " Use STR(x) to convert to a string."
                    : "";

    private string DeclareHint(string name)
    {
        if (_block == GeaBlockKind.Build && _p.ParamPrefix is { Length: > 0 } prefix && name.StartsWith(prefix, StringComparison.Ordinal))
            return " Parameters must be declared in FORM with USER_INPUT_PARAM, CHECKBOX_PARAM or RADIOBUTTON_PARAM.";
        return " Declare it with DECLARE_VARIABLE or DECLARE_REFERENCE.";
    }

    private void Error(string code, int line, int col, string message) =>
        _diags.Add(new GeaDiagnostic(code, GeaSeverity.Error, line, col, message));

    private void Warn(string code, int line, int col, string message) =>
        _diags.Add(new GeaDiagnostic(code, GeaSeverity.Warning, line, col, message));
}
