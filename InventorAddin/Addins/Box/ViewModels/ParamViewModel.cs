using System;
using System.Collections.Generic;
using System.Globalization;
using InventorAddin.Models;

namespace InventorAddin.ViewModels;

/// <summary>One parameter on the form: label, input box, slider. Built from <see cref="ParameterDef"/>, not hand-written field by field.</summary>
public sealed class ParamViewModel : ObservableObject
{
    private readonly Action _changed;
    private double _value;
    private string _text;
    private string? _parseError;
    private string? _rangeError;
    private bool _fromText;

    public ParamViewModel(ParameterDef def, Action changed)
    {
        Def = def;
        _changed = changed;
        _value = def.Default;
        _text = Format(def.Default);
    }

    public ParameterDef Def { get; }
    public string Name => Def.Name;
    public string Label => Def.Label;
    public string Unit => "mm";
    public double Min => Def.Min;
    public double Max => Def.Max;

    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(_value - value) < 1e-12) return;
            _value = value;
            Raise();
            Raise(nameof(SliderValue));
            if (!_fromText)
            {
                _text = Format(value);
                _parseError = null;
                Raise(nameof(Text));
                Raise(nameof(Error));
                Raise(nameof(HasError));
            }
            _changed();
        }
    }

    /// <summary>The slider always stays within [Min, Max]; it writes back to Value only when the user really drags it.</summary>
    public double SliderValue
    {
        get => Math.Clamp(_value, Min, Max);
        set
        {
            if (Math.Abs(value - Math.Clamp(_value, Min, Max)) < 1e-9) return;
            Value = Math.Round(value, 2);
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            Raise();
            if (TryParse(value, out var parsed))
            {
                _parseError = null;
                _fromText = true;
                try { Value = parsed; } finally { _fromText = false; }
            }
            else
            {
                _parseError = "Enter a number.";
                _changed();
            }
            Raise(nameof(Error));
            Raise(nameof(HasError));
        }
    }

    /// <summary>Error of the input box (not a number) or a constraint error assigned by <see cref="PanelViewModel"/>.</summary>
    public string? Error => _parseError ?? _rangeError;

    public bool HasError => Error != null;

    public bool HasParseError => _parseError != null;

    public void SetConstraintError(string? message)
    {
        if (_rangeError == message) return;
        _rangeError = message;
        Raise(nameof(Error));
        Raise(nameof(HasError));
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && !double.IsNaN(value) && !double.IsInfinity(value);

    private static string Format(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed class ParamGroupViewModel
{
    public ParamGroupViewModel(string title, IReadOnlyList<ParamViewModel> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<ParamViewModel> Items { get; }
}
