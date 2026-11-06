using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InventorAddin.Gea;

public enum GeaFieldKind { Number, Text, Checkbox, Radio }

/// <summary>Một ô trên form, kết quả của lần tính FORM (mục 7.3).</summary>
public sealed class GeaFormField
{
    public string Name { get; init; } = "";
    public GeaType Type { get; init; }
    public GeaFieldKind Kind { get; init; }
    public string Label { get; init; } = "";
    public string? Tooltip { get; init; }
    public string? Group { get; init; }
    /// <summary>Đơn vị hiển thị và hệ số đổi từ đơn vị hiển thị sang đơn vị nội bộ (mm, độ).</summary>
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

    /// <summary>Giá trị theo đơn vị hiển thị (cho ô nhập số).</summary>
    public double DisplayNumber(GeaValue v) => v.Num / UnitFactor;
}

public sealed record GeaFormMessage(string Code, string Message, IReadOnlyList<string> Params, int Line);

public sealed class GeaFormResult
{
    public List<GeaFormField> Fields { get; } = new();
    public List<GeaFormMessage> Errors { get; } = new();
    public List<GeaFormMessage> Warnings { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>Giá trị cuối của mọi thông số (ô ẩn lấy mặc định), theo thứ tự khai báo. Đây là đầu vào của BUILD.</summary>
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

/// <summary>File .gea đã biên dịch: cây cú pháp, danh sách thông số và chẩn đoán.</summary>
public sealed class GeaScript
{
    private GeaScript(GeaProgram program, IReadOnlyList<GeaParamInfo> parameters, IReadOnlyList<GeaDiagnostic> diagnostics, string text)
    {
        Program = program;
        Parameters = parameters;
        Diagnostics = diagnostics;
        Text = text;
    }

    public GeaProgram Program { get; }
    public IReadOnlyList<GeaParamInfo> Parameters { get; }
    public IReadOnlyList<GeaDiagnostic> Diagnostics { get; }
    public string Text { get; }
    public bool HasErrors => Diagnostics.Any(d => d.Severity == GeaSeverity.Error);

    /// <summary>Lexer, parser và kiểm tra ngữ nghĩa. Lỗi GEA1xxx/GEA2xxx được liệt kê hết một lần.</summary>
    public static GeaScript Compile(string text)
    {
        var diags = new List<GeaDiagnostic>();
        var program = GeaParser.Parse(text ?? "", diags);
        // Parser đã tự phục hồi sau lỗi cú pháp nên vẫn kiểm tra ngữ nghĩa, để liệt kê hết lỗi một lần
        var pars = GeaChecker.Check(program, diags);
        diags.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
        return new GeaScript(program, pars, diags, text ?? "");
    }

    public GeaFormResult EvaluateForm(IReadOnlyDictionary<string, object?>? inputs) => new GeaInterpreter(this).EvaluateForm(inputs);

    public GeaRunResult RunBuild(GeaFormResult form, IGeaHost host) => new GeaInterpreter(this).RunBuild(form, host);
}

/// <summary>
/// Thông dịch khối FORM (hàm thuần) và khối BUILD (gọi Host). Mỗi vòng FOR/WHILE tối đa <see cref="MaxIterations"/> lần.
/// </summary>
public sealed class GeaInterpreter
{
    public const int MaxIterations = 10000;
    private const string FailCode = "GEA4000";

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

        // Dựng danh sách ô theo thứ tự khai báo (kể cả ô nằm trong IF), mặc định là ẩn
        CollectFields(_script.Program.Form, null);
        foreach (var f in _form.Fields)
        {
            f.Visible = false;
            f.Value = f.Default;
        }

        if (_script.HasErrors)
        {
            _form.Errors.Add(new GeaFormMessage("GEA1000", "Script còn lỗi cú pháp hoặc ngữ nghĩa; sửa trước khi dùng form.", Array.Empty<string>(), 0));
        }
        else
        {
            try
            {
                Exec(_script.Program.Form);
            }
            catch (GeaRuntimeException ex)
            {
                _form.Errors.Add(new GeaFormMessage(ex.Code, $"Dòng {ex.Line}: {ex.Message}", Array.Empty<string>(), ex.Line));
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

    /// <summary>Giá trị nhập cho một ô: lấy từ đầu vào nếu đúng kiểu, nếu không dùng mặc định. Kiểm tra MIN/MAX/REQUIRED.</summary>
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
                        f.Errors.Add($"{f.Label}: cần nhập một số.");
                        break;
                    }
                    if (f.Type == GeaType.Integer && Math.Abs(n.Value - Math.Round(n.Value)) > 1e-9)
                        f.Errors.Add($"{f.Label} phải là số nguyên.");
                    value = GeaValue.Number(f.Type, n.Value * f.UnitFactor);
                }
                break;
            case GeaFieldKind.Text:
                if (raw is string str) value = GeaValue.Text(str);
                if (f.Required && string.IsNullOrWhiteSpace(value.Str)) f.Errors.Add($"{f.Label} không được để trống.");
                break;
            case GeaFieldKind.Checkbox:
                if (raw is bool b) value = GeaValue.Boolean(b);
                else if (raw is string bs && bool.TryParse(bs, out var pb)) value = GeaValue.Boolean(pb);
                break;
            case GeaFieldKind.Radio:
                if (raw is string choice)
                {
                    if (f.Options.Contains(choice)) value = GeaValue.Text(choice);
                    else f.Errors.Add($"{f.Label}: \"{choice}\" không phải lựa chọn hợp lệ ({string.Join(", ", f.Options)}).");
                }
                break;
        }

        if (f.Kind == GeaFieldKind.Number && f.Errors.Count == 0)
        {
            const double eps = 1e-9;
            if ((f.Min.HasValue && value.Num < f.Min.Value - eps) || (f.Max.HasValue && value.Num > f.Max.Value + eps))
            {
                string Fmt(double? v) => v.HasValue ? (v.Value / f.UnitFactor).ToString("0.###", ci) + (f.Unit.Length > 0 ? " " + f.Unit : "") : "∞";
                f.Errors.Add($"{f.Label} phải nằm trong [{Fmt(f.Min)}, {Fmt(f.Max)}].");
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
            throw new GeaRuntimeException("GEA4099", "Lỗi Inventor: " + ex.Message, line, col, ex);
        }
    }

    private T HostCall<T>(int line, int col, Func<T> call)
    {
        T result = default!;
        HostCall(line, col, () => { result = call(); });
        return result;
    }

    // ================================================================ thực thi lệnh

    private Flow Exec(IReadOnlyList<GeaStmt> stmts)
    {
        foreach (var s in stmts)
        {
            _run?.ExecutedLines.Add(s.Line);
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
                if (step == 0) throw new GeaRuntimeException("GEA5001", "STEP của FOR bằng 0.", f.Line, f.Col);
                int count = 0;
                try
                {
                    for (long i = from; step > 0 ? i <= to : i >= to; i += step)
                    {
                        if (++count > MaxIterations)
                            throw new GeaRuntimeException("GEA5001", $"Vòng FOR vượt quá {MaxIterations} lần lặp.", f.Line, f.Col);
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
                        throw new GeaRuntimeException("GEA5001", $"Vòng WHILE vượt quá {MaxIterations} lần lặp.", w.Line, w.Col);
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
                if (value == null) throw new GeaRuntimeException("GEA4012", $"Đối tượng không có thuộc tính \"{key}\".", ga.Line, ga.Col);
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
                // Cải tiến so với .tab: vào khối thì ERROR tự về FALSE
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
                    _run!.Log.Add($"[{ex.Line}] Lỗi đã bắt: {ex.Code} {ex.Message}");
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
        throw new GeaRuntimeException("GEA1001", $"Lệnh chưa hỗ trợ: {s.GetType().Name}.", s.Line, s.Col);
    }

    private void CreateFeature(CreateFeatureStmt cf)
    {
        var m = GeaBuilderCatalog.Find(cf.SourceName)
                ?? throw new GeaRuntimeException("GEA2013", $"Builder '{cf.SourceName}' không tồn tại.", cf.Line, cf.Col);
        GeaRef? on = cf.OnRef == null ? null : Ref(cf.OnRef, cf);

        var inputs = new Dictionary<string, GeaValue>(StringComparer.Ordinal);
        foreach (var i in cf.Inputs)
        {
            var port = m.Input(i.Name)!;
            var v = Eval(i.Value);
            if (port.Type == GeaType.Reference && v.Ref == null)
                throw new GeaRuntimeException("GEA4012", $"Đầu vào \"{i.Name}\": tham chiếu chưa được gán.", i.Line, i.Col);
            inputs[i.Name] = v.WithDeps(null).CoerceTo(port.Type);
        }
        var complete = GeaBuilderCatalog.CompleteInputs(m, inputs);
        var outputs = HostCall(cf.Line, cf.Col, () => _host!.CreateFeature(m, on, complete));
        foreach (var o in cf.Outputs)
        {
            if (!outputs.TryGetValue(o.Name, out var r))
                throw new GeaRuntimeException("GEA2012", $"Builder {m.Name} không trả về đầu ra \"{o.Name}\".", o.Line, o.Col);
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
        throw new GeaRuntimeException("GEA4030", $"Bảng {lk.Table} không có hàng {lk.KeyColumn} = {key.Display()}.", lk.Line, lk.Col);
    }

    private GeaValue Var(string name, GeaNode at)
    {
        if (_env.TryGetValue(name, out var v)) return v;
        throw new GeaRuntimeException("GEA2001", $"'{name}' chưa được khai báo hoặc chưa có giá trị.", at.Line, at.Col);
    }

    private GeaRef Ref(string name, GeaNode at)
    {
        var v = Var(name, at);
        return v.Ref ?? throw new GeaRuntimeException("GEA4012", $"Tham chiếu '{name}' chưa được gán.", at.Line, at.Col);
    }

    // ================================================================ biểu thức

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
                // AND/OR ngắn mạch nhưng vẫn giữ phụ thuộc của vế trái
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
                throw new GeaRuntimeException("GEA1001", $"'{m.Target}.{m.Member}' chưa hỗ trợ.", m.Line, m.Col);
        }
        throw new GeaRuntimeException("GEA1001", "Biểu thức không hỗ trợ.", e.Line, e.Col);
    }
}
