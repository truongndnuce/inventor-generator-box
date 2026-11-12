using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InventorAddin.Gea;

public enum GeaFieldKind { Number, Text, Checkbox, Radio }

/// <summary>One field on the form, the result of one FORM evaluation (section 7.3).</summary>
public sealed class GeaFormField
{
    public string Name { get; init; } = "";
    public GeaType Type { get; init; }
    public GeaFieldKind Kind { get; init; }
    public string Label { get; init; } = "";
    public string? Tooltip { get; init; }
    public string? Group { get; init; }
    /// <summary>Display unit and the factor that converts from the display unit to the internal unit (mm, degrees).</summary>
    public string Unit { get; init; } = "";
    public double UnitFactor { get; init; } = 1;
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public bool Required { get; init; }
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    public GeaValue Default { get; init; } = GeaValue.Dbl(0);
    public int Line { get; init; }

    public bool Visible { get; internal set; }
    public GeaValue Value { get; internal set; } = GeaValue.Dbl(0);
    public List<string> Errors { get; } = new();

    /// <summary>Value in the display unit (for numeric input fields).</summary>
    public double DisplayNumber(GeaValue v) => v.Num / UnitFactor;
}

public sealed record GeaFormMessage(string Code, string Message, IReadOnlyList<string> Params, int Line);

public sealed class GeaFormResult
{
    public List<GeaFormField> Fields { get; } = new();
    public List<GeaFormMessage> Errors { get; } = new();
    public List<GeaFormMessage> Warnings { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>Final value of every parameter (hidden fields take their default), in declaration order. This is the input of BUILD.</summary>
    public List<KeyValuePair<string, GeaValue>> Values { get; } = new();

    public bool Ok => Errors.Count == 0;
}

public sealed class GeaRunResult
{
    public bool Ok { get; init; }
    public GeaDiagnostic? Error { get; init; }
    public List<string> Log { get; } = new();
    public HashSet<int> ExecutedLines { get; } = new();
}

/// <summary>A compiled .gea file: syntax tree, parameter list and diagnostics.</summary>
public sealed class GeaScript
{
    private GeaScript(GeaProgram program, IReadOnlyList<GeaParamInfo> parameters, IReadOnlyList<GeaDiagnostic> diagnostics, string text, string expandedText)
    {
        Program = program;
        Parameters = parameters;
        Diagnostics = diagnostics;
        Text = text;
        ExpandedText = expandedText;
    }

    public GeaProgram Program { get; }
    public IReadOnlyList<GeaParamInfo> Parameters { get; }
    public IReadOnlyList<GeaDiagnostic> Diagnostics { get; }
    /// <summary>The original text the user supplied (it may still contain <c>INCLUDE</c> lines).</summary>
    public string Text { get; }
    /// <summary>The text after <c>INCLUDE lib:</c> expansion; equal to <see cref="Text"/> when the script has no INCLUDE. This is the copy to store and to send to the worker.</summary>
    public string ExpandedText { get; }
    public bool HasErrors => Diagnostics.Any(d => d.Severity == GeaSeverity.Error);

    /// <summary>
    /// Expands <c>INCLUDE lib:</c> (if any), then runs the lexer, the parser and the semantic check. All GEA1xxx/GEA2xxx errors are listed at once.
    /// <paramref name="resolveLib"/> returns the library content by name (null if there is none). Diagnostics always use the line numbers of the original file;
    /// an error inside a library gets the file name prefixed to the message.
    /// </summary>
    public static GeaScript Compile(string text, Func<string, string?>? resolveLib = null, string fileName = "script")
    {
        text ??= "";
        var expansion = GeaIncludes.Expand(text, resolveLib, fileName);
        var diags = new List<GeaDiagnostic>();
        var program = GeaParser.Parse(expansion.Text, diags);
        program.ExtraParamPrefixes = program.ExtraParamPrefixes.Union(expansion.LibPrefixes).ToList();
        // The parser recovers from syntax errors on its own, so the semantic check still runs, to list all errors at once
        var pars = GeaChecker.Check(program, diags);

        // Diagnostics of the parser/checker use lines of the expanded text: map them back to the original (file, line).
        for (var i = 0; i < diags.Count; i++)
        {
            var d = diags[i];
            if (d.Line < 1 || d.Line > expansion.Lines.Count) continue;
            var src = expansion.Lines[d.Line - 1];
            diags[i] = d with { Line = src.Line, Message = src.File == fileName ? d.Message : $"{src.File}: {d.Message}" };
        }
        diags.AddRange(expansion.Diagnostics);
        diags.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
        return new GeaScript(program, pars, diags, text, expansion.Text);
    }

    public GeaFormResult EvaluateForm(IReadOnlyDictionary<string, object?>? inputs) => new GeaInterpreter(this).EvaluateForm(inputs);

    /// <summary>
    /// Evaluates the FORM with the numeric values sent by the web (the display unit of each field; checkboxes are 1/0). Names that are not in the FORM are ignored.
    /// </summary>
    public GeaFormResult EvaluateFormNumbers(IReadOnlyDictionary<string, double>? values) => EvaluateFormValues(values, null);

    /// <summary>Like <see cref="EvaluateFormNumbers"/>, plus text values for text fields and single-choice (radio) fields.</summary>
    public GeaFormResult EvaluateFormValues(IReadOnlyDictionary<string, double>? numbers, IReadOnlyDictionary<string, string>? texts)
    {
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (numbers != null || texts != null)
        {
            var kinds = EvaluateForm(null).Fields.ToDictionary(f => f.Name, f => f.Kind, StringComparer.Ordinal);
            if (numbers != null)
                foreach (var (name, v) in numbers)
                {
                    if (!kinds.TryGetValue(name, out var kind) || kind is GeaFieldKind.Text or GeaFieldKind.Radio) continue;
                    inputs[name] = kind == GeaFieldKind.Checkbox ? v != 0 : v;
                }
            if (texts != null)
                foreach (var (name, s) in texts)
                    if (kinds.TryGetValue(name, out var kind) && kind is GeaFieldKind.Text or GeaFieldKind.Radio)
                        inputs[name] = s;
        }
        return EvaluateForm(inputs);
    }

    public GeaRunResult RunBuild(GeaFormResult form, IGeaHost host) => new GeaInterpreter(this).RunBuild(form, host);
}

/// <summary>
/// Interprets the FORM block (a pure function) and the BUILD block (calls the Host). Each FOR/WHILE loop runs at most <see cref="MaxIterations"/> times.
/// </summary>
public sealed class GeaInterpreter
{
    public const int MaxIterations = 10000;
    /// <summary>Maximum number of statements executed in one FORM or BUILD run. Stops nested FOR loops from running too long (the script is supplied by users).</summary>
    public const int MaxSteps = 1_000_000;
    private const string FailCode = "GEA4000";

    private int _steps;

    private enum Flow { Normal, Break, Continue, Return }

    private readonly GeaScript _script;
    private readonly Dictionary<string, GeaValue> _env = new(StringComparer.Ordinal);
    private GeaBlockKind _block;

    // FORM
    private GeaFormResult? _form;
    private Dictionary<string, GeaFormField>? _fields;
    private IReadOnlyDictionary<string, object?>? _inputs;

    // BUILD
    private IGeaHost? _host;
    private GeaRunResult? _run;

    public GeaInterpreter(GeaScript script) => _script = script;

    // ================================================================ FORM

    public GeaFormResult EvaluateForm(IReadOnlyDictionary<string, object?>? inputs)
    {
        _block = GeaBlockKind.Form;
        _inputs = inputs ?? new Dictionary<string, object?>();
        _form = new GeaFormResult();
        _fields = new Dictionary<string, GeaFormField>(StringComparer.Ordinal);

        // Build the list of fields in declaration order (including fields inside IF), hidden by default
        CollectFields(_script.Program.Form, null);
        foreach (var f in _form.Fields)
        {
            f.Visible = false;
            f.Value = f.Default;
        }

        if (_script.HasErrors)
        {
            _form.Errors.Add(new GeaFormMessage("GEA1000", "The script still has syntax or semantic errors; fix them before using the form.", Array.Empty<string>(), 0));
        }
        else
        {
            try
            {
                Exec(_script.Program.Form);
            }
            catch (GeaRuntimeException ex)
            {
                _form.Errors.Add(new GeaFormMessage(ex.Code, $"Line {ex.Line}: {ex.Message}", Array.Empty<string>(), ex.Line));
            }
        }

        foreach (var f in _form.Fields) _form.Values.Add(new(f.Name, f.Value));
        return _form;
    }

    private void CollectFields(IReadOnlyList<GeaStmt> stmts, string? group)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case GroupStmt g: CollectFields(g.Body, g.Title); break;
                case IfStmt i:
                    foreach (var b in i.Branches) CollectFields(b.Body, group);
                    if (i.Else != null) CollectFields(i.Else, group);
                    break;
                case ForStmt f: CollectFields(f.Body, group); break;
                case InputParamStmt p:
                    GeaUnits.TryGet(p.Unit, out _, out var factor);
                    AddField(new GeaFormField
                    {
                        Name = p.Name, Type = p.Type, Kind = p.Type == GeaType.String ? GeaFieldKind.Text : GeaFieldKind.Number,
                        Label = p.Label ?? p.Name, Tooltip = p.Tooltip, Group = group, Unit = p.Unit, UnitFactor = factor,
                        Min = p.Min, Max = p.Max, Step = p.Step ?? (p.Type == GeaType.Integer ? 1 : null), Required = p.Required,
                        Default = p.Default, Line = p.Line,
                    });
                    break;
                case CheckboxParamStmt c:
                    AddField(new GeaFormField
                    {
                        Name = c.Name, Type = GeaType.Bool, Kind = GeaFieldKind.Checkbox, Label = c.Label ?? c.Name, Tooltip = c.Tooltip,
                        Group = group, Default = GeaValue.Boolean(c.Default), Line = c.Line,
                    });
                    break;
                case RadioParamStmt r:
                    AddField(new GeaFormField
                    {
                        Name = r.Name, Type = GeaType.String, Kind = GeaFieldKind.Radio, Label = r.Label ?? r.Name, Tooltip = r.Tooltip,
                        Group = group, Options = r.Options, Default = GeaValue.Text(r.Default), Line = r.Line,
                    });
                    break;
            }
        }
    }

    private void AddField(GeaFormField f)
    {
        if (_fields!.ContainsKey(f.Name)) return;
        _fields[f.Name] = f;
        _form!.Fields.Add(f);
    }

    /// <summary>Input value for one field: taken from the input if the type matches, otherwise the default. Checks MIN/MAX/REQUIRED.</summary>
    private GeaValue InputValue(GeaFormField f)
    {
        _inputs!.TryGetValue(f.Name, out var raw);
        GeaValue value = f.Default;
        var ci = CultureInfo.InvariantCulture;

        switch (f.Kind)
        {
            case GeaFieldKind.Number:
                double? n = raw switch
                {
                    null => null,
                    double d => d,
                    float fl => fl,
                    int i => i,
                    long l => l,
                    decimal m => (double)m,
                    string s when double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, ci, out var parsed) => parsed,
                    string s when s.Trim().Length == 0 => null,
                    _ => double.NaN,
                };
                if (n.HasValue)
                {
                    if (double.IsNaN(n.Value) || double.IsInfinity(n.Value))
                    {
                        f.Errors.Add($"{f.Label}: enter a number.");
                        break;
                    }
                    if (f.Type == GeaType.Integer && Math.Abs(n.Value - Math.Round(n.Value)) > 1e-9)
                        f.Errors.Add($"{f.Label} must be a whole number.");
                    value = GeaValue.Number(f.Type, n.Value * f.UnitFactor);
                }
                break;
            case GeaFieldKind.Text:
                if (raw is string str) value = GeaValue.Text(str);
                if (f.Required && string.IsNullOrWhiteSpace(value.Str)) f.Errors.Add($"{f.Label} must not be empty.");
                break;
            case GeaFieldKind.Checkbox:
                if (raw is bool b) value = GeaValue.Boolean(b);
                else if (raw is string bs && bool.TryParse(bs, out var pb)) value = GeaValue.Boolean(pb);
                break;
            case GeaFieldKind.Radio:
                if (raw is string choice)
                {
                    if (f.Options.Contains(choice)) value = GeaValue.Text(choice);
                    else f.Errors.Add($"{f.Label}: \"{choice}\" is not a valid choice ({string.Join(", ", f.Options)}).");
                }
                break;
        }

        if (f.Kind == GeaFieldKind.Number && f.Errors.Count == 0)
        {
            const double eps = 1e-9;
            if ((f.Min.HasValue && value.Num < f.Min.Value - eps) || (f.Max.HasValue && value.Num > f.Max.Value + eps))
            {
                string Fmt(double? v) => v.HasValue ? (v.Value / f.UnitFactor).ToString("0.###", ci) + (f.Unit.Length > 0 ? " " + f.Unit : "") : "∞";
                f.Errors.Add($"{f.Label} must be within [{Fmt(f.Min)}, {Fmt(f.Max)}].");
            }
        }

        foreach (var e in f.Errors)
            _form!.Errors.Add(new GeaFormMessage(f.Kind == GeaFieldKind.Text ? "GEA3003" : "GEA3002", e, new[] { f.Name }, f.Line));
        return value.WithDeps(new HashSet<string> { f.Name });
    }

    // ================================================================ BUILD

    public GeaRunResult RunBuild(GeaFormResult form, IGeaHost host)
    {
        if (_script.HasErrors)
            return new GeaRunResult { Ok = false, Error = _script.Diagnostics.First(d => d.Severity == GeaSeverity.Error) };
        if (!form.Ok)
        {
            var first = form.Errors[0];
            return new GeaRunResult { Ok = false, Error = new GeaDiagnostic(first.Code, GeaSeverity.Error, first.Line, 1, first.Message) };
        }

        _block = GeaBlockKind.Build;
        _host = host;
        _run = new GeaRunResult();
        try
        {
            foreach (var (name, value) in form.Values) _env[name] = value.WithDeps(null);
            _env["ERROR"] = GeaValue.Boolean(false);
            _env["ERROR_MESSAGE"] = GeaValue.Text("");

            HostCall(0, 0, () => host.SyncParameters(form.Values));
            Exec(_script.Program.Build);

            var ok = new GeaRunResult { Ok = true };
            ok.Log.AddRange(_run.Log);
            ok.ExecutedLines.UnionWith(_run.ExecutedLines);
            return ok;
        }
        catch (GeaRuntimeException ex)
        {
            var failed = new GeaRunResult { Ok = false, Error = ex.ToDiagnostic() };
            failed.Log.AddRange(_run.Log);
            failed.ExecutedLines.UnionWith(_run.ExecutedLines);
            return failed;
        }
    }

    private void HostCall(int line, int col, Action call)
    {
        try { call(); }
        catch (GeaRuntimeException ex)
        {
            if (ex.Line == 0) { ex.Line = line; ex.Column = col; }
            throw;
        }
        catch (Exception ex)
        {
            throw new GeaRuntimeException("GEA4099", "Inventor error: " + ex.Message, line, col, ex);
        }
    }

    private T HostCall<T>(int line, int col, Func<T> call)
    {
        T result = default!;
        HostCall(line, col, () => { result = call(); });
        return result;
    }

    // ================================================================ executing statements

    private Flow Exec(IReadOnlyList<GeaStmt> stmts)
    {
        foreach (var s in stmts)
        {
            _run?.ExecutedLines.Add(s.Line);
            if (++_steps > MaxSteps)
                throw new GeaRuntimeException("GEA5001", $"The script exceeded {MaxSteps} executed statements.", s.Line, s.Col);
            Flow flow;
            try
            {
                flow = ExecOne(s);
            }
            catch (GeaRuntimeException ex) when (ex.Line == 0)
            {
                ex.Line = s.Line;
                ex.Column = s.Col;
                throw;
            }
            if (flow != Flow.Normal) return flow;
        }
        return Flow.Normal;
    }

    private Flow ExecOne(GeaStmt s)
    {
        switch (s)
        {
            case DeclareVarStmt d:
                _env[d.Name] = d.Init == null ? GeaValue.Default(d.Type) : Eval(d.Init).CoerceTo(d.Type);
                return Flow.Normal;
            case DeclareRefStmt d:
                _env[d.Name] = GeaValue.Reference(null);
                return Flow.Normal;
            case AssignStmt a:
                _env[a.Name] = Eval(a.Value).CoerceTo(Var(a.Name, a).Type);
                return Flow.Normal;
            case IncDecStmt i:
            {
                var v = Var(i.Name, i);
                _env[i.Name] = GeaValue.Int((long)v.Num + i.Delta).WithDeps(v.Deps);
                return Flow.Normal;
            }
            case IfStmt f:
                foreach (var b in f.Branches)
                    if (Eval(b.Condition).Bool)
                        return Exec(b.Body);
                return f.Else != null ? Exec(f.Else) : Flow.Normal;
            case ForStmt f:
            {
                long from = (long)Eval(f.From).Num, to = (long)Eval(f.To).Num;
                long step = f.Step == null ? 1 : (long)Eval(f.Step).Num;
                if (step == 0) throw new GeaRuntimeException("GEA5001", "The STEP of the FOR is 0.", f.Line, f.Col);
                int count = 0;
                try
                {
                    for (long i = from; step > 0 ? i <= to : i >= to; i += step)
                    {
                        if (++count > MaxIterations)
                            throw new GeaRuntimeException("GEA5001", $"The FOR loop exceeded {MaxIterations} iterations.", f.Line, f.Col);
                        _env[f.Var] = GeaValue.Int(i);
                        var flow = Exec(f.Body);
                        if (flow == Flow.Break) break;
                        if (flow == Flow.Return) return flow;
                    }
                }
                finally
                {
                    _env.Remove(f.Var);
                }
                return Flow.Normal;
            }
            case WhileStmt w:
            {
                int count = 0;
                while (Eval(w.Condition).Bool)
                {
                    if (++count > MaxIterations)
                        throw new GeaRuntimeException("GEA5001", $"The WHILE loop exceeded {MaxIterations} iterations.", w.Line, w.Col);
                    var flow = Exec(w.Body);
                    if (flow == Flow.Break) break;
                    if (flow == Flow.Return) return flow;
                }
                return Flow.Normal;
            }
            case BreakStmt: return Flow.Break;
            case ContinueStmt: return Flow.Continue;
            case ReturnStmt: return Flow.Return;
            case GroupStmt g:
                return Exec(g.Body);

            // ---------------------------------------------------- FORM
            case InputParamStmt or CheckboxParamStmt or RadioParamStmt:
            {
                var name = s switch { InputParamStmt p => p.Name, CheckboxParamStmt c => c.Name, RadioParamStmt r => r.Name, _ => "" };
                var field = _fields![name];
                if (!field.Visible)
                {
                    field.Visible = true;
                    field.Value = InputValue(field);
                }
                _env[name] = field.Value;
                return Flow.Normal;
            }
            case AssertStmt a:
            {
                var cond = Eval(a.Condition);
                if (cond.Bool) return Flow.Normal;
                var message = Eval(a.Message).Str ?? "";
                if (_block == GeaBlockKind.Build)
                    throw new GeaRuntimeException("GEA3001", message, a.Line, a.Col);
                var pars = cond.Deps?.OrderBy(x => x, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
                var msg = new GeaFormMessage("GEA3001", message, pars, a.Line);
                if (a.Warn) _form!.Warnings.Add(msg);
                else _form!.Errors.Add(msg);
                return Flow.Normal;
            }
            case PrintStmt p:
            {
                var text = Eval(p.Value).Display();
                if (_block == GeaBlockKind.Form) _form!.Notes.Add(text);
                else
                {
                    _run!.Log.Add($"[{p.Line}] {text}");
                    HostCall(p.Line, p.Col, () => _host!.Print(text, p.Line));
                }
                return Flow.Normal;
            }
            case LookupStmt lk:
                _env[lk.Var] = Lookup(lk).CoerceTo(Var(lk.Var, lk).Type);
                return Flow.Normal;

            // ---------------------------------------------------- BUILD
            case SetParamStmt sp:
            {
                var v = Eval(sp.Value);
                HostCall(sp.Line, sp.Col, () => _host!.SetParam(sp.Name, v));
                return Flow.Normal;
            }
            case SetParamExprStmt se:
            {
                var e = Eval(se.Expression).Str ?? "";
                HostCall(se.Line, se.Col, () => _host!.SetParamExpr(se.Name, e));
                return Flow.Normal;
            }
            case GetParamStmt gp:
            {
                var type = Var(gp.Var, gp).Type;
                _env[gp.Var] = HostCall(gp.Line, gp.Col, () => _host!.GetParam(gp.Name, type));
                return Flow.Normal;
            }
            case CreateFeatureStmt cf:
                CreateFeature(cf);
                return Flow.Normal;
            case AddComponentStmt ac:
            {
                var part = Eval(ac.Part).Str ?? "";
                if (part.Length == 0) throw new GeaRuntimeException("GEA4012", "The part name of ADD_COMPONENT must not be empty.", ac.Line, ac.Col);
                var at = ac.At == null ? new double[3] : ac.At.Select(e => Eval(e).Num).ToArray();
                var rot = ac.Rot == null ? new double[3] : ac.Rot.Select(e => Eval(e).Num).ToArray();
                _env[ac.Var] = GeaValue.Reference(HostCall(ac.Line, ac.Col, () => _host!.AddComponent(part, at, rot)));
                return Flow.Normal;
            }
            case SetRefNameStmt rn:
            {
                var r = Ref(rn.Ref, rn);
                var name = Eval(rn.Name).Str ?? "";
                HostCall(rn.Line, rn.Col, () => _host!.SetRefName(r, name));
                return Flow.Normal;
            }
            case GetRefNameStmt gn:
            {
                var r = Ref(gn.Ref, gn);
                _env[gn.Var] = GeaValue.Text(HostCall(gn.Line, gn.Col, () => _host!.GetRefName(r)));
                return Flow.Normal;
            }
            case SetRefAttrStmt ra:
            {
                var r = Ref(ra.Ref, ra);
                var key = Eval(ra.Key).Str ?? "";
                var value = Eval(ra.Value);
                HostCall(ra.Line, ra.Col, () => _host!.SetRefAttr(r, key, value));
                return Flow.Normal;
            }
            case GetRefAttrStmt ga:
            {
                var r = Ref(ga.Ref, ga);
                var key = Eval(ga.Key).Str ?? "";
                var target = Var(ga.Var, ga).Type;
                var value = HostCall(ga.Line, ga.Col, () => _host!.GetRefAttr(r, key));
                if (value == null) throw new GeaRuntimeException("GEA4012", $"The object has no attribute \"{key}\".", ga.Line, ga.Col);
                if (value.IsNumeric && target is GeaType.Integer or GeaType.Double or GeaType.Length or GeaType.Angle)
                    value = GeaValue.Number(target, value.Num);
                _env[ga.Var] = value.CoerceTo(target);
                return Flow.Normal;
            }
            case SetAppearanceStmt ap:
            {
                var r = Ref(ap.Ref, ap);
                var name = Eval(ap.Appearance).Str ?? "";
                HostCall(ap.Line, ap.Col, () => _host!.SetAppearance(r, name));
                return Flow.Normal;
            }
            case RegenStmt rg:
                HostCall(rg.Line, rg.Col, () => _host!.Regen());
                return Flow.Normal;
            case ProcessingStartStmt ps:
            {
                var title = Eval(ps.Title).Str;
                _run!.Log.Add($"[{ps.Line}] == {title}");
                HostCall(ps.Line, ps.Col, () => _host!.Progress(GeaProgressKind.Start, title, null));
                return Flow.Normal;
            }
            case ProcessingStateStmt st:
            {
                var text = Eval(st.Text).Str;
                double? pct = st.Percent == null ? null : Eval(st.Percent).Num;
                _run!.Log.Add($"[{st.Line}] {text}{(pct.HasValue ? $" ({pct:0}%)" : "")}");
                HostCall(st.Line, st.Col, () => _host!.Progress(GeaProgressKind.State, text, pct));
                return Flow.Normal;
            }
            case ProcessingEndStmt pe:
                HostCall(pe.Line, pe.Col, () => _host!.Progress(GeaProgressKind.End, null, null));
                return Flow.Normal;
            case CatchStmt c:
            {
                // Improvement over .tab: entering the block resets ERROR to FALSE
                _env["ERROR"] = GeaValue.Boolean(false);
                _env["ERROR_MESSAGE"] = GeaValue.Text("");
                try
                {
                    return Exec(c.Body);
                }
                catch (GeaRuntimeException ex) when (ex.Code.StartsWith("GEA4", StringComparison.Ordinal))
                {
                    _env["ERROR"] = GeaValue.Boolean(true);
                    _env["ERROR_MESSAGE"] = GeaValue.Text(ex.Message);
                    _run!.Log.Add($"[{ex.Line}] Caught error: {ex.Code} {ex.Message}");
                    return Flow.Normal;
                }
            }
            case ClearCatchStmt:
                _env["ERROR"] = GeaValue.Boolean(false);
                _env["ERROR_MESSAGE"] = GeaValue.Text("");
                return Flow.Normal;
            case FailStmt fl:
                throw new GeaRuntimeException(FailCode, Eval(fl.Message).Str ?? "FAIL", fl.Line, fl.Col);
        }
        throw new GeaRuntimeException("GEA1001", $"Statement not supported: {s.GetType().Name}.", s.Line, s.Col);
    }

    private void CreateFeature(CreateFeatureStmt cf)
    {
        var m = GeaBuilderCatalog.Find(cf.SourceName)
                ?? throw new GeaRuntimeException("GEA2013", $"Builder '{cf.SourceName}' does not exist.", cf.Line, cf.Col);
        GeaRef? on = cf.OnRef == null ? null : Ref(cf.OnRef, cf);

        var inputs = new Dictionary<string, GeaValue>(StringComparer.Ordinal);
        foreach (var i in cf.Inputs)
        {
            var port = m.Input(i.Name)!;
            var v = Eval(i.Value);
            if (port.Type == GeaType.Reference && v.Ref == null)
                throw new GeaRuntimeException("GEA4012", $"Input \"{i.Name}\": the reference has not been assigned.", i.Line, i.Col);
            inputs[i.Name] = v.WithDeps(null).CoerceTo(port.Type);
        }
        var complete = GeaBuilderCatalog.CompleteInputs(m, inputs);
        var outputs = HostCall(cf.Line, cf.Col, () => _host!.CreateFeature(m, on, complete));
        foreach (var o in cf.Outputs)
        {
            if (!outputs.TryGetValue(o.Name, out var r))
                throw new GeaRuntimeException("GEA2012", $"Builder {m.Name} did not return the output \"{o.Name}\".", o.Line, o.Col);
            _env[o.Var] = GeaValue.Reference(r);
        }
    }

    private GeaValue Lookup(LookupStmt lk)
    {
        var table = _script.Program.Tables.First(t => t.Name == lk.Table);
        int ki = table.Columns.ToList().FindIndex(c => c.Name == lk.KeyColumn);
        int vi = table.Columns.ToList().FindIndex(c => c.Name == lk.ValueColumn);
        var key = Eval(lk.Key);
        foreach (var row in table.Rows)
        {
            var cell = row[ki];
            bool match = cell.Type == GeaType.String ? cell.Str == key.Str
                : cell.Type == GeaType.Bool ? cell.Bool == key.Bool
                : Math.Abs(cell.Num - key.Num) < 1e-9;
            if (match) return row[vi].WithDeps(key.Deps);
        }
        throw new GeaRuntimeException("GEA4030", $"Table {lk.Table} has no row with {lk.KeyColumn} = {key.Display()}.", lk.Line, lk.Col);
    }

    private GeaValue Var(string name, GeaNode at)
    {
        if (_env.TryGetValue(name, out var v)) return v;
        throw new GeaRuntimeException("GEA2001", $"'{name}' is not declared or has no value yet.", at.Line, at.Col);
    }

    private GeaRef Ref(string name, GeaNode at)
    {
        var v = Var(name, at);
        return v.Ref ?? throw new GeaRuntimeException("GEA4012", $"Reference '{name}' has not been assigned.", at.Line, at.Col);
    }

    // ================================================================ expressions

    private GeaValue Eval(GeaExpr e)
    {
        try
        {
            return EvalCore(e);
        }
        catch (GeaRuntimeException ex) when (ex.Line == 0)
        {
            ex.Line = e.Line;
            ex.Column = e.Col;
            throw;
        }
    }

    private GeaValue EvalCore(GeaExpr e)
    {
        switch (e)
        {
            case NumberExpr n: return GeaValue.Number(n.Type, n.Value);
            case StringExpr s: return GeaValue.Text(s.Value);
            case BoolExpr b: return GeaValue.Boolean(b.Value);
            case NameExpr n: return Var(n.Name, n);
            case UnaryExpr u:
            {
                var v = Eval(u.Operand);
                if (u.Op == "NOT") return GeaValue.Boolean(!v.Bool).WithDeps(v.Deps);
                return GeaValue.Number(v.Type, -v.Num).WithDeps(v.Deps);
            }
            case BinaryExpr b:
            {
                // AND/OR short-circuit but still keep the dependencies of the left side
                if (b.Op == "AND" || b.Op == "OR")
                {
                    var l = Eval(b.Left);
                    if (b.Op == "AND" && !l.Bool) return GeaValue.Boolean(false).WithDeps(l.Deps);
                    if (b.Op == "OR" && l.Bool) return GeaValue.Boolean(true).WithDeps(l.Deps);
                    return GeaOps.ApplyBinary(b.Op, l, Eval(b.Right));
                }
                return GeaOps.ApplyBinary(b.Op, Eval(b.Left), Eval(b.Right));
            }
            case CallExpr c:
                return GeaOps.CallFunction(c.Name, c.Args.Select(Eval).ToList());
            case MemberExpr m:
                throw new GeaRuntimeException("GEA1001", $"'{m.Target}.{m.Member}' is not supported.", m.Line, m.Col);
        }
        throw new GeaRuntimeException("GEA1001", "Expression not supported.", e.Line, e.Col);
    }
}
