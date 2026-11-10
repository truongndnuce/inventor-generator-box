using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InventorAddin.Gea;

// Core of the .gea language (gea-language-spec.md): diagnostics, types, units, values and operator rules.
// Depends on neither Inventor nor WPF so it can be tested and reused on the server.

public enum GeaSeverity { Error, Warning }

/// <summary>A diagnostic with a code, line and column (section 13 of the spec).</summary>
public sealed record GeaDiagnostic(string Code, GeaSeverity Severity, int Line, int Column, string Message)
{
    public string Format(string file) =>
        $"{file}({Line},{Column}): {(Severity == GeaSeverity.Error ? "error" : "warning")} {Code}: {Message}";

    public override string ToString() => Format("script");
}

/// <summary>A runtime error (GEA3xxx, GEA4xxx, GEA5xxx) with the line position of the statement.</summary>
public sealed class GeaRuntimeException : Exception
{
    public GeaRuntimeException(string code, string message, int line = 0, int column = 0, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Line = line;
        Column = column;
    }

    public string Code { get; }
    public int Line { get; internal set; }
    public int Column { get; internal set; }

    public GeaDiagnostic ToDiagnostic() => new(Code, GeaSeverity.Error, Line, Column, Message);

    public override string ToString() => $"({Line},{Column}) {Code}: {Message}";
}

public enum GeaType
{
    Integer,
    Double,
    Bool,
    String,
    Length,
    Angle,
    Reference,
    /// <summary>Unknown type (after an error), used to avoid cascading errors.</summary>
    Unknown,
}

public static class GeaUnits
{
    /// <summary>Factor that converts to the internal unit (mm for lengths, degrees for angles).</summary>
    public static bool TryGet(string unit, out GeaType type, out double factor)
    {
        switch (unit)
        {
            case "mm": type = GeaType.Length; factor = 1; return true;
            case "cm": type = GeaType.Length; factor = 10; return true;
            case "m": type = GeaType.Length; factor = 1000; return true;
            case "in": type = GeaType.Length; factor = 25.4; return true;
            case "deg": type = GeaType.Angle; factor = 1; return true;
            case "rad": type = GeaType.Angle; factor = 180.0 / Math.PI; return true;
            default: type = GeaType.Unknown; factor = 1; return false;
        }
    }

    public static bool IsUnit(string word) => TryGet(word, out _, out _);

    public static string DefaultUnit(GeaType t) => t switch
    {
        GeaType.Length => "mm",
        GeaType.Angle => "deg",
        _ => "",
    };

    public static string TypeName(GeaType t) => t switch
    {
        GeaType.Integer => "INTEGER",
        GeaType.Double => "DOUBLE",
        GeaType.Bool => "BOOL",
        GeaType.String => "STRING",
        GeaType.Length => "LENGTH",
        GeaType.Angle => "ANGLE",
        GeaType.Reference => "REFERENCE",
        _ => "?",
    };

    public static bool TryParseType(string word, out GeaType type)
    {
        type = word switch
        {
            "INTEGER" => GeaType.Integer,
            "DOUBLE" => GeaType.Double,
            "BOOL" => GeaType.Bool,
            "STRING" => GeaType.String,
            "LENGTH" => GeaType.Length,
            "ANGLE" => GeaType.Angle,
            "REFERENCE" => GeaType.Reference,
            _ => GeaType.Unknown,
        };
        return type != GeaType.Unknown;
    }
}

/// <summary>
/// A reference to an object in the model (body, feature...). <see cref="Handle"/> is the object of the Host
/// (a COM object in Inventor, a virtual object in the simulation). <see cref="Props"/> are the values the builder recorded (dimensions...).
/// </summary>
public sealed class GeaRef
{
    public GeaRef(string kind, object handle, string builder, IReadOnlyDictionary<string, GeaValue>? props = null)
    {
        Kind = kind;
        Handle = handle;
        Builder = builder;
        Props = props ?? new Dictionary<string, GeaValue>();
    }

    public string Kind { get; }
    public object Handle { get; }
    public string Builder { get; }
    public IReadOnlyDictionary<string, GeaValue> Props { get; }
}

/// <summary>Value at run time. Lengths are stored in mm, angles in degrees.</summary>
public sealed class GeaValue
{
    private GeaValue(GeaType type, double num, string? str, bool b, GeaRef? r)
    {
        Type = type;
        Num = num;
        Str = str;
        Bool = b;
        Ref = r;
    }

    public GeaType Type { get; }
    public double Num { get; }
    public string? Str { get; }
    public bool Bool { get; }
    public GeaRef? Ref { get; }

    /// <summary>The FORM parameters this value was computed from (so the right field turns red when an ASSERT fails). May be null.</summary>
    public IReadOnlySet<string>? Deps { get; private init; }

    public static GeaValue Int(long v) => new(GeaType.Integer, v, null, false, null);
    public static GeaValue Dbl(double v) => new(GeaType.Double, v, null, false, null);
    public static GeaValue Boolean(bool v) => new(GeaType.Bool, 0, null, v, null);
    public static GeaValue Text(string v) => new(GeaType.String, 0, v, false, null);
    public static GeaValue Length(double mm) => new(GeaType.Length, mm, null, false, null);
    public static GeaValue Angle(double deg) => new(GeaType.Angle, deg, null, false, null);
    public static GeaValue Reference(GeaRef? r) => new(GeaType.Reference, 0, null, false, r);

    public static GeaValue Number(GeaType type, double v) => type switch
    {
        GeaType.Integer => Int((long)Math.Round(v)),
        GeaType.Length => Length(v),
        GeaType.Angle => Angle(v),
        _ => Dbl(v),
    };

    public static GeaValue Default(GeaType type) => type switch
    {
        GeaType.Integer => Int(0),
        GeaType.Double => Dbl(0),
        GeaType.Bool => Boolean(false),
        GeaType.String => Text(""),
        GeaType.Length => Length(0),
        GeaType.Angle => Angle(0),
        _ => Reference(null),
    };

    public GeaValue WithDeps(IReadOnlySet<string>? deps) =>
        new(Type, Num, Str, Bool, Ref) { Deps = deps is { Count: > 0 } ? deps : null };

    public bool IsNumeric => Type is GeaType.Integer or GeaType.Double or GeaType.Length or GeaType.Angle;

    /// <summary>Converts to the target type on assignment (only INTEGER is promoted to DOUBLE). Call it after compatibility was checked.</summary>
    public GeaValue CoerceTo(GeaType target)
    {
        if (target == Type || target == GeaType.Unknown) return this;
        if (target == GeaType.Double && Type == GeaType.Integer) return Dbl(Num).WithDeps(Deps);
        // A bare 0 may be assigned to LENGTH/ANGLE
        if (target is GeaType.Length or GeaType.Angle && Type is GeaType.Integer or GeaType.Double && Num == 0)
            return Number(target, 0).WithDeps(Deps);
        throw new GeaRuntimeException("GEA2002", $"Cannot assign {GeaUnits.TypeName(Type)} to {GeaUnits.TypeName(target)}.");
    }

    public string Display()
    {
        var ci = CultureInfo.InvariantCulture;
        return Type switch
        {
            GeaType.Integer => ((long)Num).ToString(ci),
            GeaType.Double => Num.ToString("0.######", ci),
            GeaType.Bool => Bool ? "TRUE" : "FALSE",
            GeaType.String => Str ?? "",
            GeaType.Length => Num.ToString("0.###", ci) + " mm",
            GeaType.Angle => Num.ToString("0.###", ci) + " deg",
            GeaType.Reference => Ref == null ? "<unassigned ref>" : $"<{Ref.Kind} from {Ref.Builder}>",
            _ => "?",
        };
    }

    public override string ToString() => Display();

    /// <summary>Raw value used for storage (numbers in mm/degrees, strings, bool).</summary>
    public object? ToPlain() => Type switch
    {
        GeaType.Bool => Bool,
        GeaType.String => Str,
        GeaType.Reference => null,
        GeaType.Integer => (long)Num,
        _ => Num,
    };
}

/// <summary>Type rules of the operators (section 4.2). Shared by the static check and by run time.</summary>
public static class GeaOps
{
    public static bool IsPlainNumber(GeaType t) => t is GeaType.Integer or GeaType.Double;

    public static bool IsDimensioned(GeaType t) => t is GeaType.Length or GeaType.Angle;

    /// <summary>Whether a value of type <paramref name="value"/> can be assigned to a place of type <paramref name="target"/>.</summary>
    public static bool Assignable(GeaType target, GeaType value, bool valueIsZeroLiteral = false)
    {
        if (target == GeaType.Unknown || value == GeaType.Unknown) return true;
        if (target == value) return true;
        if (target == GeaType.Double && value == GeaType.Integer) return true;
        if (IsDimensioned(target) && IsPlainNumber(value) && valueIsZeroLiteral) return true;
        return false;
    }

    /// <summary>Result type of a binary operator, or null with an error message.</summary>
    public static GeaType? Binary(string op, GeaType l, GeaType r, bool lZero, bool rZero, out string? error)
    {
        error = null;
        if (l == GeaType.Unknown || r == GeaType.Unknown)
            return op is "==" or "<>" or "<" or "<=" or ">" or ">=" or "AND" or "OR" ? GeaType.Bool : GeaType.Unknown;

        string Names() => $"{GeaUnits.TypeName(l)} {op} {GeaUnits.TypeName(r)}";

        switch (op)
        {
            case "AND":
            case "OR":
                if (l == GeaType.Bool && r == GeaType.Bool) return GeaType.Bool;
                error = $"{op} needs two BOOL values ({Names()}).";
                return null;

            case "+":
                if (l == GeaType.String && r == GeaType.String) return GeaType.String;
                if (l == GeaType.String || r == GeaType.String)
                {
                    error = $"Cannot add {Names()}. Use STR(x) to convert a number to a string.";
                    return null;
                }
                goto case "-";
            case "-":
                if (IsPlainNumber(l) && IsPlainNumber(r)) return l == GeaType.Integer && r == GeaType.Integer ? GeaType.Integer : GeaType.Double;
                if (IsDimensioned(l) && l == r) return l;
                if (IsDimensioned(l) && IsPlainNumber(r) && rZero) return l;
                if (IsDimensioned(r) && IsPlainNumber(l) && lZero) return r;
                error = $"Wrong unit: {Names()}.";
                return null;

            case "*":
                if (IsPlainNumber(l) && IsPlainNumber(r)) return l == GeaType.Integer && r == GeaType.Integer ? GeaType.Integer : GeaType.Double;
                if (IsDimensioned(l) && IsPlainNumber(r)) return l;
                if (IsDimensioned(r) && IsPlainNumber(l)) return r;
                error = IsDimensioned(l) && IsDimensioned(r)
                    ? $"{Names()}: v1 does not support areas or the product of two quantities that have units."
                    : $"Cannot multiply {Names()}.";
                return null;

            case "/":
                if (IsPlainNumber(l) && IsPlainNumber(r)) return GeaType.Double;
                if (IsDimensioned(l) && IsPlainNumber(r)) return l;
                if (IsDimensioned(l) && l == r) return GeaType.Double;
                error = $"Cannot divide {Names()}.";
                return null;

            case "%":
                if (l == GeaType.Integer && r == GeaType.Integer) return GeaType.Integer;
                error = "% only works on INTEGER.";
                return null;

            case "==":
            case "<>":
                if (l == r && l is GeaType.Bool or GeaType.String or GeaType.Reference) return GeaType.Bool;
                goto case "<";
            case "<":
            case "<=":
            case ">":
            case ">=":
                if (IsPlainNumber(l) && IsPlainNumber(r)) return GeaType.Bool;
                if (IsDimensioned(l) && l == r) return GeaType.Bool;
                if (IsDimensioned(l) && IsPlainNumber(r) && rZero) return GeaType.Bool;
                if (IsDimensioned(r) && IsPlainNumber(l) && lZero) return GeaType.Bool;
                if (l == GeaType.String && r == GeaType.String) return GeaType.Bool;
                error = IsDimensioned(l) || IsDimensioned(r)
                    ? $"Comparing {Names()}: a quantity with a unit can only be compared with the same kind (or the number 0). For example write 5 mm instead of 5."
                    : $"Cannot compare {Names()}.";
                return null;
        }
        error = $"Operator not supported: {op}.";
        return null;
    }

    public static GeaValue ApplyBinary(string op, GeaValue l, GeaValue r)
    {
        var type = Binary(op, l.Type, r.Type, l.IsNumeric && l.Num == 0, r.IsNumeric && r.Num == 0, out var error)
                   ?? throw new GeaRuntimeException("GEA2002", error!);

        GeaValue result;
        switch (op)
        {
            case "AND": result = GeaValue.Boolean(l.Bool && r.Bool); break;
            case "OR": result = GeaValue.Boolean(l.Bool || r.Bool); break;
            case "+":
                result = type == GeaType.String ? GeaValue.Text(l.Str + r.Str) : GeaValue.Number(type, l.Num + r.Num);
                break;
            case "-": result = GeaValue.Number(type, l.Num - r.Num); break;
            case "*": result = GeaValue.Number(type, l.Num * r.Num); break;
            case "/":
                if (r.Num == 0) throw new GeaRuntimeException("GEA5002", "Chia cho 0.");
                result = GeaValue.Number(type, l.Num / r.Num);
                break;
            case "%":
                if (r.Num == 0) throw new GeaRuntimeException("GEA5002", "Division by 0 (the % operator).");
                result = GeaValue.Int((long)l.Num % (long)r.Num);
                break;
            default:
                result = GeaValue.Boolean(Compare(op, l, r));
                break;
        }
        return result.WithDeps(Union(l.Deps, r.Deps));
    }

    private static bool Compare(string op, GeaValue l, GeaValue r)
    {
        const double eps = 1e-9;
        int c;
        if (l.Type == GeaType.Bool) c = l.Bool == r.Bool ? 0 : 1;
        else if (l.Type == GeaType.String) c = string.CompareOrdinal(l.Str, r.Str);
        else if (l.Type == GeaType.Reference) c = ReferenceEquals(l.Ref?.Handle, r.Ref?.Handle) ? 0 : 1;
        else c = Math.Abs(l.Num - r.Num) <= eps * Math.Max(1, Math.Abs(l.Num)) ? 0 : l.Num < r.Num ? -1 : 1;

        return op switch
        {
            "==" => c == 0,
            "<>" => c != 0,
            "<" => c < 0,
            "<=" => c <= 0,
            ">" => c > 0,
            ">=" => c >= 0,
            _ => false,
        };
    }

    public static IReadOnlySet<string>? Union(params IReadOnlySet<string>?[] sets)
    {
        HashSet<string>? result = null;
        foreach (var s in sets)
        {
            if (s == null || s.Count == 0) continue;
            result ??= new HashSet<string>();
            result.UnionWith(s);
        }
        return result;
    }

    // ---------------------------------------------------------------- built-in functions (section 10)

    public static readonly IReadOnlySet<string> Functions = new HashSet<string>
    {
        "MIN", "MAX", "ABS", "ROUND", "FLOOR", "CEIL", "SQRT", "STR", "NUM", "FIND",
    };

    /// <summary>Result type of a function, or null with an error.</summary>
    public static GeaType? Function(string name, IReadOnlyList<GeaType> args, out string? error)
    {
        error = null;
        bool unknown = args.Any(a => a == GeaType.Unknown);
        string? countError = null;

        bool Count(int min, int max)
        {
            if (args.Count >= min && args.Count <= max) return true;
            countError = min == max ? $"{name} needs {min} argument(s)." : $"{name} needs {min} to {max} arguments.";
            return false;
        }

        var result = FunctionCore(name, args, unknown, Count, out error);
        if (countError != null) error = countError;
        return result;
    }

    private static GeaType? FunctionCore(string name, IReadOnlyList<GeaType> args, bool unknown, Func<int, int, bool> Count, out string? error)
    {
        error = null;

        switch (name)
        {
            case "MIN":
            case "MAX":
                if (!Count(2, 2)) return null;
                if (unknown) return GeaType.Unknown;
                if (IsPlainNumber(args[0]) && IsPlainNumber(args[1]))
                    return args[0] == GeaType.Integer && args[1] == GeaType.Integer ? GeaType.Integer : GeaType.Double;
                if (IsDimensioned(args[0]) && args[0] == args[1]) return args[0];
                error = $"{name}: the two arguments must be of the same kind ({GeaUnits.TypeName(args[0])}, {GeaUnits.TypeName(args[1])}).";
                return null;
            case "ABS":
                if (!Count(1, 1)) return null;
                if (unknown || args[0] is GeaType.Integer or GeaType.Double or GeaType.Length or GeaType.Angle) return args[0];
                error = "ABS needs a number.";
                return null;
            case "ROUND":
                if (!Count(1, 2)) return null;
                if (unknown) return GeaType.Unknown;
                if (args.Count == 2 && args[1] != GeaType.Integer) { error = "ROUND: the number of decimal places must be INTEGER."; return null; }
                if (IsDimensioned(args[0])) return args[0];
                if (IsPlainNumber(args[0])) return args.Count == 1 ? GeaType.Integer : GeaType.Double;
                error = "ROUND needs a number.";
                return null;
            case "FLOOR":
            case "CEIL":
                if (!Count(1, 1)) return null;
                if (unknown) return GeaType.Unknown;
                if (IsDimensioned(args[0])) return args[0];
                if (IsPlainNumber(args[0])) return GeaType.Integer;
                error = $"{name} needs a number.";
                return null;
            case "SQRT":
                if (!Count(1, 1)) return null;
                if (unknown || IsPlainNumber(args[0])) return GeaType.Double;
                error = "SQRT only works on numbers without a unit.";
                return null;
            case "STR":
                if (!Count(1, 1)) return null;
                return GeaType.String;
            case "NUM":
                if (!Count(1, 1)) return null;
                if (unknown || args[0] == GeaType.String) return GeaType.Double;
                error = "NUM needs a string.";
                return null;
            case "FIND":
                if (!Count(2, 2)) return null;
                if (unknown || args[0] == GeaType.String && args[1] == GeaType.String) return GeaType.Integer;
                error = "FIND needs two strings.";
                return null;
        }
        error = $"Function does not exist: {name}.";
        return null;
    }

    public static GeaValue CallFunction(string name, IReadOnlyList<GeaValue> args)
    {
        var type = Function(name, args.Select(a => a.Type).ToList(), out var error)
                   ?? throw new GeaRuntimeException("GEA2002", error!);
        var deps = Union(args.Select(a => a.Deps).ToArray());

        GeaValue result;
        switch (name)
        {
            case "MIN": result = GeaValue.Number(type, Math.Min(args[0].Num, args[1].Num)); break;
            case "MAX": result = GeaValue.Number(type, Math.Max(args[0].Num, args[1].Num)); break;
            case "ABS": result = GeaValue.Number(type, Math.Abs(args[0].Num)); break;
            case "ROUND":
                result = GeaValue.Number(type, Math.Round(args[0].Num, args.Count == 2 ? (int)Math.Clamp(args[1].Num, 0, 15) : 0, MidpointRounding.AwayFromZero));
                break;
            case "FLOOR": result = GeaValue.Number(type, Math.Floor(args[0].Num + 1e-9)); break;
            case "CEIL": result = GeaValue.Number(type, Math.Ceiling(args[0].Num - 1e-9)); break;
            case "SQRT":
                if (args[0].Num < 0) throw new GeaRuntimeException("GEA5002", "SQRT of a negative number.");
                result = GeaValue.Dbl(Math.Sqrt(args[0].Num));
                break;
            case "STR": result = GeaValue.Text(args[0].Display()); break;
            case "NUM":
                if (!double.TryParse((args[0].Str ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    throw new GeaRuntimeException("GEA5003", $"Cannot convert \"{args[0].Str}\" to a number.");
                result = GeaValue.Dbl(n);
                break;
            case "FIND": result = GeaValue.Int((args[0].Str ?? "").IndexOf(args[1].Str ?? "", StringComparison.Ordinal)); break;
            default: throw new GeaRuntimeException("GEA2001", $"Function does not exist: {name}.");
        }
        return result.WithDeps(deps);
    }
}
