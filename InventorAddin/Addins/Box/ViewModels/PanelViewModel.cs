using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using InventorAddin.Host;
using InventorAddin.Models;
using InventorAddin.Services;

namespace InventorAddin.ViewModels;

public sealed class PresetItem
{
    public PresetItem(Preset preset) => Preset = preset;

    public Preset Preset { get; }
    public string Display => Preset.IsValid ? Preset.Name : $"{Preset.Name} (invalid)";
    public bool IsSelectable => Preset.IsValid;
    public string? ToolTip => Preset.Error;
}

/// <summary>
/// ViewModel of the dockable window: holds the parameter values, the validity state and the commands. It knows nothing about WPF controls or Inventor
/// (only through <see cref="IBoxGateway"/>). The canvas only reads <see cref="PlanScene"/>/<see cref="SectionScene"/>.
/// </summary>
public sealed class PanelViewModel : ObservableObject
{
    private readonly IBoxGateway _gateway;
    private readonly PresetStore _store;
    private readonly IDialogService _dialogs;
    private readonly List<ParamViewModel> _params;
    private bool _suspend;
    private IReadOnlyDictionary<string, double>? _modelValues;

    private DocState _docState = new(DocKind.NoDocument, "");
    private string _status = "";
    private bool _statusIsError;
    private bool _hasErrors;
    private Scene _planScene = Scene.Empty;
    private Scene _sectionScene = Scene.Empty;
    private IReadOnlySet<string> _errorParams = new HashSet<string>();
    private string? _focusedParam;
    private PresetItem? _selectedPreset;

    public PanelViewModel(IBoxGateway gateway, PresetStore store, IDialogService dialogs)
    {
        _gateway = gateway;
        _store = store;
        _dialogs = dialogs;

        _params = BoxParams.All.Select(d => new ParamViewModel(d, OnParamChanged)).ToList();
        Groups = _params.GroupBy(p => p.Def.Group).Select(g => new ParamGroupViewModel(g.Key, g.ToList())).ToList();
        Presets = new ObservableCollection<PresetItem>();

        CreateCommand = new RelayCommand(Create, () => CanCreate);
        ApplyCommand = new RelayCommand(Apply, () => CanApply);
        RevertCommand = new RelayCommand(Revert, () => _modelValues != null);
        DefaultCommand = new RelayCommand(() => SetValues(BoxParams.Defaults()));
        RefreshCommand = new RelayCommand(Refresh);
        SavePresetCommand = new RelayCommand(SaveAsNewPreset, () => !HasErrors);
        OverwritePresetCommand = new RelayCommand(OverwritePreset, () => !HasErrors && SelectedPreset != null);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => SelectedPreset != null);

        LoadPresets();
        Recompute();
        Refresh();
    }

    // ------------------------------------------------------------ properties for the View

    public IReadOnlyList<ParamGroupViewModel> Groups { get; }
    public ObservableCollection<PresetItem> Presets { get; }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }
    public bool HasErrors { get => _hasErrors; private set => Set(ref _hasErrors, value); }
    public Scene PlanScene { get => _planScene; private set => Set(ref _planScene, value); }
    public Scene SectionScene { get => _sectionScene; private set => Set(ref _sectionScene, value); }
    public IReadOnlySet<string> ErrorParams { get => _errorParams; private set => Set(ref _errorParams, value); }

    /// <summary>The parameter that has focus in the form; the canvas highlights the matching dimension.</summary>
    public string? FocusedParam { get => _focusedParam; set => Set(ref _focusedParam, value); }

    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!Set(ref _selectedPreset, value)) return;
            if (value is { Preset.IsValid: true }) SetValues(value.Preset.Values);
            RaiseCommands();
        }
    }

    public string? PresetWarning { get; private set; }

    public bool CanCreate => _docState.Kind is DocKind.EmptyPart or DocKind.ForeignContent && !HasErrors;
    public bool CanApply => _docState.Kind == DocKind.BoxPart && !HasErrors && IsDirty;
    public bool IsBoxPart => _docState.Kind == DocKind.BoxPart;
    public bool IsDirty => _modelValues != null && BoxParams.All.Any(d => Math.Abs(_modelValues[d.Name] - Current()[d.Name]) > 1e-9);

    public RelayCommand CreateCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand DefaultCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand OverwritePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }

    // ------------------------------------------------------------ values

    private Dictionary<string, double> Current() => _params.ToDictionary(p => p.Name, p => p.Value);

    private void SetValues(IReadOnlyDictionary<string, double> values)
    {
        _suspend = true;
        try
        {
            foreach (var p in _params)
                if (values.TryGetValue(p.Name, out var v)) p.Value = v;
        }
        finally { _suspend = false; }
        // Overwrite Text even when Value did not change (for example when it holds invalid text)
        foreach (var p in _params) p.Text = p.Value.ToString("0.###", CultureInfo.InvariantCulture);
        Recompute();
    }

    private void OnParamChanged()
    {
        if (!_suspend) Recompute();
    }

    /// <summary>Checks the constraints; redraws the canvas only when valid (on an error the canvas keeps the last valid drawing).</summary>
    private void Recompute()
    {
        var values = Current();
        var issues = BoxParams.Validate(values);

        foreach (var p in _params)
            p.SetConstraintError(issues.FirstOrDefault(i => i.Params.Contains(p.Name))?.Message);

        bool parseError = _params.Any(p => p.HasParseError);
        HasErrors = issues.Count > 0 || parseError;
        ErrorParams = issues.SelectMany(i => i.Params).Concat(_params.Where(p => p.HasParseError).Select(p => p.Name)).ToHashSet();

        if (!HasErrors)
        {
            PlanScene = PreviewBuilder.Plan(values);
            SectionScene = PreviewBuilder.Section(values);
        }

        if (HasErrors) SetStatus(issues.Count > 0 ? issues[0].Message : "Some input fields are not valid numbers.", true);
        else SetStatus(_docState.Message, false);
        RaiseCommands();
    }

    private void SetStatus(string message, bool isError)
    {
        Status = message;
        StatusIsError = isError;
    }

    private void RaiseCommands()
    {
        Raise(nameof(CanCreate));
        Raise(nameof(CanApply));
        Raise(nameof(IsBoxPart));
        CreateCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
        OverwritePresetCommand.RaiseCanExecuteChanged();
        DeletePresetCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------ commands with Inventor

    /// <summary>Re-reads the document state and (if there is a box) the values from the model. Called when the UI opens, the document changes or Refresh is pressed.</summary>
    public void Refresh()
    {
        _docState = _gateway.GetState();
        _modelValues = null;
        string? readError = null;

        if (_docState.Kind == DocKind.BoxPart)
        {
            var read = _gateway.ReadValues();
            if (read.Values != null)
            {
                _modelValues = read.Values;
                SetValues(read.Values);
            }
            else readError = read.Error;
        }

        Recompute();
        if (readError != null) SetStatus(readError, true);
    }

    private void Create()
    {
        if (_docState.Kind == DocKind.ForeignContent &&
            !_dialogs.Confirm("This Part already has other geometry or parameters. Add the box to this Part?"))
            return;

        var result = _gateway.Create(Current(), _docState.Kind == DocKind.ForeignContent);
        Refresh();
        SetStatus(result.Message, !result.Ok);
    }

    private void Apply()
    {
        var result = _gateway.Apply(Current());
        Refresh();
        SetStatus(result.Message, !result.Ok);
    }

    private void Revert()
    {
        if (_modelValues != null) SetValues(_modelValues);
    }

    // ------------------------------------------------------------ Preset

    private void LoadPresets(string? selectName = null)
    {
        var result = _store.Load();
        _selectedPreset = null;
        Presets.Clear();
        foreach (var p in result.Presets) Presets.Add(new PresetItem(p));
        if (selectName != null) _selectedPreset = Presets.FirstOrDefault(p => p.Preset.Name == selectName);
        Raise(nameof(SelectedPreset));
        if (result.Warning != null) Log.Warn(result.Warning);
        PresetWarning = result.Warning;
        Raise(nameof(PresetWarning));
    }

    private void SavePresets(IEnumerable<Preset> presets, string? selectName)
    {
        try
        {
            _store.Save(presets);
            LoadPresets(selectName);
        }
        catch (Exception ex)
        {
            Log.Error("Saving presets failed", ex);
            SetStatus("Could not save presets: " + ex.Message, true);
        }
        RaiseCommands();
    }

    private IEnumerable<Preset> AllPresets() => Presets.Select(p => p.Preset);

    private void SaveAsNewPreset()
    {
        var name = _dialogs.Prompt("Save as new preset", "Preset name:", SelectedPreset?.Preset.Name ?? "")?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var existing = Presets.FirstOrDefault(p => string.Equals(p.Preset.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !_dialogs.Confirm($"Preset '{existing.Preset.Name}' already exists. Overwrite it?")) return;

        name = existing?.Preset.Name ?? name;
        var list = AllPresets().Where(p => p.Name != name).Append(new Preset(name, Current())).ToList();
        SavePresets(list, name);
    }

    private void OverwritePreset()
    {
        if (SelectedPreset == null) return;
        var name = SelectedPreset.Preset.Name;
        if (!_dialogs.Confirm($"Overwrite preset '{name}' with the current values?")) return;
        SavePresets(AllPresets().Select(p => p.Name == name ? new Preset(name, Current()) : p).ToList(), name);
    }

    private void DeletePreset()
    {
        if (SelectedPreset == null) return;
        var name = SelectedPreset.Preset.Name;
        if (!_dialogs.Confirm($"Delete preset '{name}'?")) return;
        SavePresets(AllPresets().Where(p => p.Name != name).ToList(), null);
    }
}
