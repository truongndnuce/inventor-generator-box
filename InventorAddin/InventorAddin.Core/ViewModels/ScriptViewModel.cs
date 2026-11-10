using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using InventorAddin.Gea;
using InventorAddin.Host;
using InventorAddin.Services;
using InventorAddin.Services.Gea;

namespace InventorAddin.ViewModels;

/// <summary>One field of the form generated from the FORM block of the script.</summary>
public sealed class ScriptFieldViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _text;
    private bool _checked;
    private string? _choice;
    private bool _visible = true;
    private string? _error;

    public ScriptFieldViewModel(GeaFormField def, Action changed)
    {
        Def = def;
        _changed = changed;
        _text = DefaultText();
        _checked = def.Default.Bool;
        _choice = def.Kind == GeaFieldKind.Radio ? def.Default.Str : null;
    }

    public GeaFormField Def { get; }
    public string Name => Def.Name;
    public string Label => Def.Label;
    public string ToolTip => string.IsNullOrEmpty(Def.Tooltip) ? Def.Name : $"{Def.Tooltip}\n({Def.Name})";
    public string Unit => Def.Unit;
    public bool IsNumber => Def.Kind == GeaFieldKind.Number;
    public bool IsText => Def.Kind is GeaFieldKind.Number or GeaFieldKind.Text;
    public bool IsCheckbox => Def.Kind == GeaFieldKind.Checkbox;
    public bool IsRadio => Def.Kind == GeaFieldKind.Radio;
    public IReadOnlyList<string> Options => Def.Options;

    /// <summary>Hint for the value range, for example "1 – 10".</summary>
    public string Range
    {
        get
        {
            if (!IsNumber || (Def.Min == null && Def.Max == null)) return "";
            string F(double? v) => v.HasValue ? (v.Value / Def.UnitFactor).ToString("0.###", CultureInfo.InvariantCulture) : "";
            return $"{F(Def.Min)} – {F(Def.Max)}";
        }
    }

    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) _changed(); }
    }

    public bool Checked
    {
        get => _checked;
        set { if (Set(ref _checked, value)) _changed(); }
    }

    public string? Choice
    {
        get => _choice;
        set { if (Set(ref _choice, value)) _changed(); }
    }

    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => _error != null;

    /// <summary>Value fed to FORM (a number in the display unit as text, so FORM checks it itself).</summary>
    public object? Raw => Def.Kind switch
    {
        GeaFieldKind.Checkbox => _checked,
        GeaFieldKind.Radio => _choice,
        _ => _text,
    };

    /// <summary>Value to store and reload (a real number in the display unit).</summary>
    public object? Stored => Def.Kind switch
    {
        GeaFieldKind.Checkbox => _checked,
        GeaFieldKind.Radio => _choice,
        GeaFieldKind.Number when double.TryParse(_text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => _text,
    };

    /// <summary>Sets the value without raising a change event (the caller recomputes the form).</summary>
    public void Load(object? value)
    {
        switch (Def.Kind)
        {
            case GeaFieldKind.Checkbox when value is bool b: _checked = b; Raise(nameof(Checked)); break;
            case GeaFieldKind.Radio when value is string s && Def.Options.Contains(s): _choice = s; Raise(nameof(Choice)); break;
            case GeaFieldKind.Number when value is double d: _text = d.ToString("0.######", CultureInfo.InvariantCulture); Raise(nameof(Text)); break;
            case GeaFieldKind.Number or GeaFieldKind.Text when value is string t: _text = t; Raise(nameof(Text)); break;
        }
    }

    public void ResetToDefault()
    {
        _text = DefaultText();
        _checked = Def.Default.Bool;
        _choice = Def.Kind == GeaFieldKind.Radio ? Def.Default.Str : null;
        Raise(nameof(Text));
        Raise(nameof(Checked));
        Raise(nameof(Choice));
    }

    private string DefaultText() => Def.Kind switch
    {
        GeaFieldKind.Number => Def.DisplayNumber(Def.Default).ToString("0.######", CultureInfo.InvariantCulture),
        GeaFieldKind.Text => Def.Default.Str ?? "",
        _ => "",
    };
}

public sealed class ScriptGroupViewModel : ObservableObject
{
    private bool _visible = true;

    public ScriptGroupViewModel(string title, IReadOnlyList<ScriptFieldViewModel> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public bool HasTitle => Title.Length > 0;
    public IReadOnlyList<ScriptFieldViewModel> Items { get; }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
}

public enum ScriptMessageKind { Error, Warning, Note }

/// <summary>One diagnostic or message line of the form; <see cref="Line"/> is used to jump to the line in the editor.</summary>
public sealed record ScriptMessage(ScriptMessageKind Kind, string Text, int Line)
{
    public bool IsError => Kind == ScriptMessageKind.Error;
    public bool IsWarning => Kind == ScriptMessageKind.Warning;
}

public sealed record ScriptTemplate(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// ViewModel of the Script (.gea) tab, working like SmartAssembly: open or write a script, the form is generated from the FORM block,
/// pressing Apply runs BUILD again and the model changes to follow the script. Edit the file in an external editor and save, and the add-in
/// reloads it on its own (and applies it automatically if that is on). The script and values are stored in the Part, so reopening the file lets you keep editing.
/// </summary>
public sealed class ScriptViewModel : ObservableObject, IDisposable
{
    private const string Filter = "GEA script (*.gea)|*.gea|All files (*.*)|*.*";

    private readonly IScriptGateway _gateway;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _compileTimer;
    private readonly DispatcherTimer _fileTimer;
    private readonly List<ScriptFieldViewModel> _fields = new();
    private FileSystemWatcher? _watcher;

    private GeaScript? _script;
    private GeaFormResult? _form;
    private string _fieldSignature = "";
    private string _scriptText = "";
    private string? _fileText;
    private string? _scriptPath;
    private bool _autoApply;
    private bool _suspend;
    private bool _busy;
    private string _status = "";
    private bool _statusIsError;
    private string _docMessage = "";
    private ScriptDocState? _docState;
    private string? _lastDocKey;
    private ScriptTemplate? _selectedTemplate;

    /// <param name="templateFilter">List only the templates whose file name satisfies the condition (each product add-in shows only its own templates); null = all.</param>
    public ScriptViewModel(IScriptGateway gateway, IDialogService dialogs, Func<string, bool>? templateFilter = null)
    {
        _gateway = gateway;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _compileTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _compileTimer!.Stop(); Recompile(); }, _dispatcher);
        _fileTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => { _fileTimer!.Stop(); OnFileChangedOnDisk(); }, _dispatcher);
        _compileTimer.Stop();
        _fileTimer.Stop();

        Templates = new ObservableCollection<ScriptTemplate>(GeaScriptLibrary.BundledScripts()
            .Where(p => templateFilter == null || templateFilter(Path.GetFileName(p)))
            .Select(p => new ScriptTemplate(p)));
        _selectedTemplate = Templates.FirstOrDefault();

        OpenCommand = new RelayCommand(Open);
        OpenTemplateCommand = new RelayCommand(OpenTemplate, () => SelectedTemplate != null);
        NewCommand = new RelayCommand(NewScript);
        SaveCommand = new RelayCommand(() => Save(), () => _scriptPath != null && IsTextDirty);
        SaveAsCommand = new RelayCommand(() => SaveAs());
        ReloadCommand = new RelayCommand(Reload, () => _scriptPath != null);
        EditExternallyCommand = new RelayCommand(EditExternally, () => _scriptPath != null);
        ApplyCommand = new RelayCommand(Apply, () => CanApply);
        DefaultsCommand = new RelayCommand(Defaults, () => _fields.Count > 0);
        LoadFromModelCommand = new RelayCommand(LoadFromModel, () => _docState?.Stored != null);
        RemoveCommand = new RelayCommand(Remove, () => _docState?.Kind == ScriptDocKind.ScriptPart);
        RefreshCommand = new RelayCommand(Refresh);

        SetStatus("Open a .gea script (or pick a template), adjust the values, then Apply.", false);
    }

    // ------------------------------------------------------------ properties for the View

    public ObservableCollection<ScriptGroupViewModel> Groups { get; } = new();
    public ObservableCollection<ScriptMessage> FormMessages { get; } = new();
    public ObservableCollection<ScriptMessage> Diagnostics { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public ObservableCollection<ScriptTemplate> Templates { get; }

    public ScriptTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set { if (Set(ref _selectedTemplate, value)) OpenTemplateCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>Content of the editor. Typing recompiles after a short delay.</summary>
    public string ScriptText
    {
        get => _scriptText;
        set
        {
            if (!Set(ref _scriptText, value ?? "")) return;
            RaiseFileState();
            if (!_suspend)
            {
                _compileTimer.Stop();
                _compileTimer.Start();
            }
        }
    }

    public string? ScriptPath
    {
        get => _scriptPath;
        private set
        {
            if (!Set(ref _scriptPath, value)) return;
            Raise(nameof(FileTitle));
            Watch(value);
            RaiseCommands();
        }
    }

    public string FileTitle => _scriptPath == null ? "(no file — script lives only in the editor)" : _scriptPath + (IsTextDirty ? "  *" : "");

    public bool IsTextDirty => _scriptPath != null && _fileText != null && _fileText != _scriptText;

    public string ProductName => _script?.Program.ProductName is { Length: > 0 } p ? p : "(no script)";

    /// <summary>Saving the file in an external editor (or pressing Save) applies it automatically.</summary>
    public bool AutoApply { get => _autoApply; set => Set(ref _autoApply, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }
    public string DocMessage { get => _docMessage; private set => Set(ref _docMessage, value); }
    public bool HasScriptErrors => _script?.HasErrors ?? false;
    public string DiagnosticsHeader => _script == null ? "Problems" : $"Problems ({_script.Diagnostics.Count})";

    public bool CanApply => !_busy && _script != null && !_script.HasErrors && _form is { Ok: true }
                            && _docState?.Kind is ScriptDocKind.EmptyPart or ScriptDocKind.ForeignContent or ScriptDocKind.ScriptPart;

    public RelayCommand OpenCommand { get; }
    public RelayCommand OpenTemplateCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand EditExternallyCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DefaultsCommand { get; }
    public RelayCommand LoadFromModelCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RefreshCommand { get; }

    // ------------------------------------------------------------ file

    private void Open()
    {
        var path = _dialogs.OpenFile("Open .gea script", Filter, _scriptPath != null ? Path.GetDirectoryName(_scriptPath) : GeaScriptLibrary.UserDirectory);
        if (path == null || !ConfirmDiscard()) return;
        LoadFile(path, null);
        ApplyExportedValues(path);
    }

    /// <summary>A .gea file exported from the web has a !@ value block: it fills the form (converting the internal unit to the display unit).</summary>
    private void ApplyExportedValues(string path)
    {
        if (_fileText == null || _script == null || _script.HasErrors) return;
        var block = GeaExportBlock.Parse(_fileText);
        if (block == null) return;
        int applied = 0;
        foreach (var f in _fields)
        {
            switch (f.Def.Kind)
            {
                case GeaFieldKind.Number when block.Values.TryGetValue(f.Name, out var n):
                    f.Load(n / f.Def.UnitFactor); applied++; break;
                case GeaFieldKind.Checkbox when block.Values.TryGetValue(f.Name, out var b):
                    f.Load(b != 0); applied++; break;
                case GeaFieldKind.Radio or GeaFieldKind.Text when block.Texts.TryGetValue(f.Name, out var t):
                    f.Load(t); applied++; break;
            }
        }
        EvaluateForm();
        SetStatus($"Opened {Path.GetFileName(path)}; loaded {applied} value(s) from the web export.", false);
    }

    /// <summary>The first time the add-in window opens: if there is no script yet, open the product template so the form shows right away.</summary>
    public void OpenDefaultTemplateIfEmpty()
    {
        if (_scriptPath != null || _scriptText.Length > 0 || SelectedTemplate == null) return;
        OpenTemplate();
    }

    private void OpenTemplate()
    {
        if (SelectedTemplate == null || !ConfirmDiscard()) return;
        try
        {
            // The template is in the add-in folder (read-only): open a copy in the user folder so it can be edited
            var copy = GeaScriptLibrary.EditableCopy(SelectedTemplate.Path);
            LoadFile(copy, null);
            SetStatus($"Opened {Path.GetFileName(copy)} (your editable copy in {GeaScriptLibrary.UserDirectory}).", false);
        }
        catch (Exception ex)
        {
            SetStatus("Could not open the template: " + ex.Message, true);
        }
    }

    private void NewScript()
    {
        if (!ConfirmDiscard()) return;
        _fileText = null;
        ScriptPath = null;
        SetText(NewScriptTemplate, null);
        SetStatus("New script. Use Save as… to store it in a file.", false);
    }

    private const string NewScriptTemplate =
        "GEA_VERSION 1\nPRODUCT_NAME \"My product\"\nPARAM_PREFIX MY_\nTARGET PART\n\nBEGIN_FORM\n" +
        "  BEGIN_GROUP \"Size\"\n    USER_INPUT_PARAM MY_Length LENGTH 200 mm MIN 50 MAX 1000 LABEL \"Length\"\n" +
        "    USER_INPUT_PARAM MY_Width  LENGTH 100 mm MIN 50 MAX 1000 LABEL \"Width\"\n" +
        "    USER_INPUT_PARAM MY_Height LENGTH 50 mm  MIN 10 MAX 500  LABEL \"Height\"\n  END_GROUP\nEND_FORM\n\n" +
        "BEGIN_BUILD\n  DECLARE_REFERENCE body\n  CREATE_FEATURE builder:Block\n    FEATURE_IN \"SizeX\"  MY_Length\n" +
        "    FEATURE_IN \"SizeD\"  MY_Width\n    FEATURE_IN \"Height\" MY_Height\n    FEATURE_OUT body \"Body\"\n  END_CREATE_FEATURE\n" +
        "  SET_REF_NAME body \"MY_Block\"\n  SET_APPEARANCE body \"Grey\"\nEND_BUILD\n";

    private bool ConfirmDiscard() =>
        !IsTextDirty || _dialogs.Confirm("The script in the editor has unsaved changes. Discard them?");

    private void LoadFile(string path, IReadOnlyDictionary<string, object?>? values)
    {
        try
        {
            var text = ReadFile(path);
            _fileText = text;
            ScriptPath = path;
            SetText(text, values);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read {path}: {ex.Message}", true);
        }
    }

    private static string ReadFile(string path)
    {
        // The external editor may be writing: open with sharing allowed
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private bool Save()
    {
        if (_scriptPath == null) return SaveAs();
        try
        {
            File.WriteAllText(_scriptPath, _scriptText, new UTF8Encoding(false));
            _fileText = _scriptText;
            RaiseFileState();
            SetStatus($"Saved {Path.GetFileName(_scriptPath)}.", false);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Could not save: " + ex.Message, true);
            return false;
        }
    }

    private bool SaveAs()
    {
        var path = _dialogs.SaveFile("Save .gea script", Filter,
            _scriptPath != null ? Path.GetDirectoryName(_scriptPath) : GeaScriptLibrary.UserDirectory,
            _scriptPath != null ? Path.GetFileName(_scriptPath) : "product.gea");
        if (path == null) return false;
        _fileText = null;
        ScriptPath = path;
        return Save();
    }

    private void Reload()
    {
        if (_scriptPath == null || !ConfirmDiscard()) return;
        var values = CurrentValues();
        LoadFile(_scriptPath, values);
        SetStatus($"Reloaded {Path.GetFileName(_scriptPath)} from disk.", false);
    }

    private void EditExternally()
    {
        if (_scriptPath == null) return;
        if (IsTextDirty && !Save()) return;
        try
        {
            Process.Start(new ProcessStartInfo(_scriptPath) { UseShellExecute = true });
        }
        catch
        {
            // .gea is not associated with any application: open it in Notepad
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_scriptPath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("Could not start an editor: " + ex.Message, true); return; }
        }
        SetStatus("Edit the file and save it; the add-in reloads it" + (AutoApply ? " and applies it automatically." : ". Then click Apply."), false);
    }

    // ------------------------------------------------------------ watching the file on disk

    private void Watch(string? path)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (path == null) return;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir == null || !Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            FileSystemEventHandler onChange = (_, _) => _dispatcher.BeginInvoke(new Action(() => { _fileTimer.Stop(); _fileTimer.Start(); }));
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (_, _) => _dispatcher.BeginInvoke(new Action(() => { _fileTimer.Stop(); _fileTimer.Start(); }));
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            InventorAddin.Host.Log.Warn("Could not watch the script file: " + ex.Message);
        }
    }

    private void OnFileChangedOnDisk()
    {
        if (_scriptPath == null || !File.Exists(_scriptPath)) return;
        string text;
        try { text = ReadFile(_scriptPath); }
        catch
        {
            _fileTimer.Start(); // locked right now: try again
            return;
        }
        if (text == _fileText) return;
        if (IsTextDirty)
        {
            _fileText = text;
            RaiseFileState();
            SetStatus("The file changed on disk, but the editor has unsaved changes. Click Reload to use the file, or Save to overwrite it.", true);
            return;
        }

        var values = CurrentValues();
        _fileText = text;
        SetText(text, values);
        AddLog($"Reloaded {Path.GetFileName(_scriptPath)} (changed on disk).");
        if (_script!.HasErrors)
        {
            SetStatus($"Script reloaded but has {_script.Diagnostics.Count(d => d.Severity == GeaSeverity.Error)} error(s); see Problems.", true);
            return;
        }
        if (AutoApply)
        {
            if (CanApply) Apply();
            else SetStatus("Script reloaded; not applied automatically because the form has errors or no Part is active.", true);
        }
        else SetStatus("Script reloaded from disk. Click Apply to update the model.", false);
    }

    // ------------------------------------------------------------ compile and form

    private void SetText(string text, IReadOnlyDictionary<string, object?>? values)
    {
        _suspend = true;
        try { ScriptText = text; }
        finally { _suspend = false; }
        _compileTimer.Stop();
        Recompile(values);
    }

    private void Recompile() => Recompile(null);

    private void Recompile(IReadOnlyDictionary<string, object?>? values)
    {
        var previous = values ?? CurrentValues();
        _script = GeaScript.Compile(_scriptText, GeaScriptLibrary.LibraryResolver(_scriptPath), _scriptPath != null ? Path.GetFileName(_scriptPath) : "script.gea");

        Diagnostics.Clear();
        var file = _scriptPath != null ? Path.GetFileName(_scriptPath) : "script.gea";
        foreach (var d in _script.Diagnostics)
            Diagnostics.Add(new ScriptMessage(d.Severity == GeaSeverity.Error ? ScriptMessageKind.Error : ScriptMessageKind.Warning, d.Format(file), d.Line));
        Raise(nameof(HasScriptErrors));
        Raise(nameof(DiagnosticsHeader));
        Raise(nameof(ProductName));

        // Rebuild the fields when the parameter list changes; keep the entered values by name
        var probe = _script.EvaluateForm(null);
        var signature = string.Join("\n", probe.Fields.Select(f =>
            $"{f.Name}|{f.Kind}|{f.Label}|{f.Group}|{f.Unit}|{f.Min}|{f.Max}|{f.Tooltip}|{f.Default}|{string.Join(",", f.Options)}"));
        if (signature != _fieldSignature)
        {
            _fieldSignature = signature;
            RebuildFields(probe, previous);
        }
        else if (values != null)
        {
            foreach (var f in _fields)
                if (values.TryGetValue(f.Name, out var v)) f.Load(v);
        }
        EvaluateForm();
    }

    private void RebuildFields(GeaFormResult probe, IReadOnlyDictionary<string, object?> previous)
    {
        _fields.Clear();
        Groups.Clear();
        foreach (var f in probe.Fields)
        {
            var vm = new ScriptFieldViewModel(f, OnFieldChanged);
            if (previous.TryGetValue(f.Name, out var v)) vm.Load(v);
            _fields.Add(vm);
        }
        // Group by BEGIN_GROUP, keeping the order of appearance
        foreach (var g in _fields.GroupBy(f => f.Def.Group ?? ""))
            Groups.Add(new ScriptGroupViewModel(g.Key, g.ToList()));
        DefaultsCommand.RaiseCanExecuteChanged();
    }

    private void OnFieldChanged()
    {
        if (!_suspend) EvaluateForm();
    }

    private void EvaluateForm()
    {
        if (_script == null) return;
        var inputs = _fields.ToDictionary(f => f.Name, f => f.Raw);
        _form = _script.EvaluateForm(inputs);

        foreach (var vm in _fields)
        {
            var f = _form.Fields.FirstOrDefault(x => x.Name == vm.Name);
            vm.Visible = f?.Visible ?? false;
            var own = f?.Errors.FirstOrDefault();
            var rule = _form.Errors.FirstOrDefault(e => e.Params.Contains(vm.Name))?.Message;
            vm.Error = vm.Visible ? own ?? rule : null;
        }
        foreach (var g in Groups) g.Visible = g.Items.Any(i => i.Visible);

        FormMessages.Clear();
        foreach (var e in _form.Errors) FormMessages.Add(new ScriptMessage(ScriptMessageKind.Error, e.Message, e.Line));
        foreach (var w in _form.Warnings) FormMessages.Add(new ScriptMessage(ScriptMessageKind.Warning, w.Message, w.Line));
        foreach (var n in _form.Notes) FormMessages.Add(new ScriptMessage(ScriptMessageKind.Note, n, 0));

        if (_script.HasErrors)
            SetStatus($"The script has {_script.Diagnostics.Count(d => d.Severity == GeaSeverity.Error)} error(s); see the Script tab.", true);
        else if (!_form.Ok)
            SetStatus(_form.Errors[0].Message, true);
        else if (StatusIsError)
            SetStatus(_docMessage, false);
        RaiseCommands();
    }

    private Dictionary<string, object?> CurrentValues() => _fields.ToDictionary(f => f.Name, f => f.Stored);

    private void Defaults()
    {
        _suspend = true;
        try { foreach (var f in _fields) f.ResetToDefault(); }
        finally { _suspend = false; }
        EvaluateForm();
    }

    // ------------------------------------------------------------ Inventor

    /// <summary>Re-reads the document state. Switching to a Part that has a stored script loads that script and its values.</summary>
    public void Refresh()
    {
        _docState = _gateway.GetState();
        DocMessage = _docState.Message;
        if (_docState.Stored != null && _docState.DocKey != _lastDocKey && !IsTextDirty)
            LoadStored(_docState.Stored, announce: true);
        _lastDocKey = _docState.DocKey;
        if (!StatusIsError || _script == null) SetStatus(DocMessage, false);
        RaiseCommands();
    }

    private void LoadFromModel()
    {
        if (_docState?.Stored == null || !ConfirmDiscard()) return;
        LoadStored(_docState.Stored, announce: true);
    }

    /// <summary>Loads the script and values stored in the Part. If the source file is still on disk, that file is watched.</summary>
    private void LoadStored(StoredScript stored, bool announce)
    {
        var path = !string.IsNullOrEmpty(stored.Path) && File.Exists(stored.Path) ? stored.Path : null;
        string? disk = null;
        try { if (path != null) disk = ReadFile(path); } catch { /* cannot read: treat it as no file */ }

        _fileText = disk;
        ScriptPath = path;
        SetText(stored.Text, stored.Values);

        if (!announce) return;
        if (path != null && disk != null && disk != stored.Text)
            SetStatus($"Loaded the script stored in this Part. The file {Path.GetFileName(path)} on disk is different: click Reload to use it, or Save to overwrite it.", true);
        else
            SetStatus($"Loaded the script stored in this Part ({stored.Product}).", false);
    }

    private void Apply()
    {
        if (_script == null || _form == null) return;
        if (IsTextDirty && !Save()) return; // like "save then run": the file always matches the model

        var state = _gateway.GetState();
        bool allowForeign = false;
        if (state.Kind == ScriptDocKind.ForeignContent)
        {
            if (!_dialogs.Confirm("This Part already has other geometry or parameters. Build the script model into this Part?")) return;
            allowForeign = true;
        }

        _busy = true;
        RaiseCommands();
        try
        {
            AddLog($"Apply {Path.GetFileName(_scriptPath ?? "script.gea")} — {ProductName}");
            var result = _gateway.Apply(_script, _form, _scriptPath, AddLog, allowForeign);
            AddLog(result.Message);
            Refresh();
            _lastDocKey = _docState?.DocKey; // do not reload the script that was just applied
            SetStatus(result.Message, !result.Ok);
        }
        finally
        {
            _busy = false;
            RaiseCommands();
        }
    }

    private void Remove()
    {
        if (!_dialogs.Confirm("Delete all bodies, features and parameters created by the script in this Part? (Ctrl+Z can undo.)")) return;
        var result = _gateway.Remove();
        AddLog(result.Message);
        Refresh();
        SetStatus(result.Message, !result.Ok);
    }

    // ------------------------------------------------------------ utilities

    private void AddLog(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > 500) Log.RemoveAt(0);
    }

    private void SetStatus(string message, bool isError)
    {
        Status = message;
        StatusIsError = isError;
    }

    private void RaiseFileState()
    {
        Raise(nameof(IsTextDirty));
        Raise(nameof(FileTitle));
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommands()
    {
        Raise(nameof(CanApply));
        ApplyCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
        EditExternallyCommand.RaiseCanExecuteChanged();
        DefaultsCommand.RaiseCanExecuteChanged();
        LoadFromModelCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _compileTimer.Stop();
        _fileTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }
}
