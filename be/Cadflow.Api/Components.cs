using System.Text.RegularExpressions;
using InventorAddin.Gea;

namespace Cadflow.Api;

public sealed record ComponentParam(string Name, string Label, string? Group, string Unit, string Type, bool Integer, object Default, double? Min, double? Max, bool Required);

public sealed record ComponentVar(string Name, string Type);

/// <param name="Description">The leading comment lines of the file (without the ! marks and rule lines), keeping the blank lines between paragraphs.</param>
/// <param name="Summary">The first paragraph of <paramref name="Description"/> on one line.</param>
/// <param name="SharedVariables">Variables declared in the BUILD block of the library; the including script can use them after INCLUDE.</param>
/// <param name="Errors">Library compile errors (empty when fine); a broken library is still listed.</param>
public sealed record ComponentInfo(string Name, string Title, string Summary, string Description, IReadOnlyList<string> Prefixes,
    IReadOnlyList<ComponentVar> SharedVariables, IReadOnlyList<ComponentParam> Parameters, IReadOnlyList<string> Errors);

/// <summary>Result of generating a starter script: <see cref="Text"/> on success, otherwise <see cref="Status"/> (400/404) and <see cref="Error"/>.</summary>
public sealed record StarterResult(string? Text, int Status, string? Error);

/// <summary>
/// Catalog of components (libraries <c>Scripts/lib/*.gea</c>) for the Components page: description, parameters, shared variables and the original file.
/// Read-only. The name must be valid and in the list before the file is read, so nothing outside the library folder can be read.
/// </summary>
public sealed partial class ComponentCatalog
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^\s*DECLARE_VARIABLE\s+(?<type>\w+)\s+(?<name>\w+)")]
    private static partial Regex DeclareVar();

    [GeneratedRegex(@"^\s*DECLARE_REFERENCE\s+(?<name>\w+)")]
    private static partial Regex DeclareRef();

    private readonly Func<IEnumerable<string>> _names;
    private readonly Func<string, string?> _read;

    public ComponentCatalog() : this(GeaLibraries.Names, GeaLibraries.Resolve) { }

    public ComponentCatalog(Func<IEnumerable<string>> names, Func<string, string?> read)
    {
        _names = names;
        _read = read;
    }

    public IReadOnlyList<ComponentInfo> List() =>
        _names().Distinct().OrderBy(n => n, StringComparer.Ordinal).Select(Get).Where(c => c != null).Select(c => c!).ToList();

    /// <summary>Describes one component; null if the name is invalid or unknown.</summary>
    public ComponentInfo? Get(string name) => Raw(name) is { } text ? Describe(name, text) : null;

    /// <summary>Content of the original library file; null if the name is invalid or unknown.</summary>
    public string? Raw(string name) =>
        ValidName().IsMatch(name) && _names().Contains(name) ? _read(name) : null;

    /// <summary>Annotated starter script (every step explained) that uses this component; see <see cref="ComponentStarter"/>.</summary>
    public StarterResult Starter(string name, string? productName, string? prefix)
    {
        productName = string.IsNullOrWhiteSpace(productName) ? ComponentStarter.DefaultProductName : productName.Trim();
        prefix = string.IsNullOrWhiteSpace(prefix) ? ComponentStarter.DefaultPrefix : prefix.Trim();
        if (Get(name) is not { } info) return new(null, 404, $"Unknown component '{name}'.");
        if (ComponentStarter.Validate(productName, prefix) is { } bad) return new(null, 400, bad);
        if (info.Errors.Count > 0) return new(null, 400, $"The component '{name}' has errors, so a starter script cannot be generated: {info.Errors[0]}");
        return new(ComponentStarter.Build(info, productName, prefix), 200, null);
    }

    private ComponentInfo Describe(string name, string text)
    {
        var (description, summary) = LeadingComment(text);
        var script = GeaScript.Compile(text, _read, name + ".gea");
        var errors = script.Diagnostics.Where(d => d.Severity == GeaSeverity.Error)
            .Select(d => $"{d.Code} (line {d.Line}): {d.Message}").ToList();

        var prefixes = new List<string>();
        if (!string.IsNullOrEmpty(script.Program.ParamPrefix)) prefixes.Add(script.Program.ParamPrefix);
        prefixes.AddRange(script.Program.ExtraParamPrefixes.Where(p => !prefixes.Contains(p)));

        var parameters = new List<ComponentParam>();
        if (errors.Count == 0)
        {
            foreach (var f in script.EvaluateFormNumbers(null).Fields)
            {
                parameters.Add(new ComponentParam(f.Name, f.Label, f.Group, f.Unit, f.Type.ToString(), f.Type == GeaType.Integer,
                    f.Kind switch
                    {
                        GeaFieldKind.Text or GeaFieldKind.Radio => f.Default.Str ?? "",
                        GeaFieldKind.Checkbox => f.Default.Bool ? 1 : 0,
                        _ => GeaForms.Round(f.DisplayNumber(f.Default)),
                    },
                    f.Min.HasValue ? GeaForms.Round(f.Min.Value / f.UnitFactor) : null,
                    f.Max.HasValue ? GeaForms.Round(f.Max.Value / f.UnitFactor) : null,
                    f.Required));
            }
        }

        return new ComponentInfo(name, string.IsNullOrWhiteSpace(script.Program.ProductName) ? name : script.Program.ProductName,
            summary, description, prefixes, SharedVariables(text), parameters, errors);
    }

    /// <summary>Leading comment of the file: drops the `!` marker and lines made only of `=`; the first paragraph (up to a blank line) is the summary.</summary>
    private static (string Description, string Summary) LeadingComment(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) { if (lines.Count == 0) continue; break; }
            if (!t.StartsWith('!')) break;
            var c = t.TrimStart('!').Trim();
            if (c.Length > 0 && c.All(ch => ch == '=')) continue;
            lines.Add(c);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var summary = string.Join(" ", lines.TakeWhile(l => l.Length > 0));
        return (string.Join("\n", lines), summary);
    }

    private static IReadOnlyList<ComponentVar> SharedVariables(string text)
    {
        var vars = new List<ComponentVar>();
        var inBuild = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t == "BEGIN_BUILD") { inBuild = true; continue; }
            if (t == "END_BUILD") break;
            if (!inBuild) continue;
            if (DeclareVar().Match(raw) is { Success: true } v) vars.Add(new ComponentVar(v.Groups["name"].Value, v.Groups["type"].Value.ToUpperInvariant()));
            else if (DeclareRef().Match(raw) is { Success: true } r) vars.Add(new ComponentVar(r.Groups["name"].Value, "REFERENCE"));
        }
        return vars;
    }
}

/// <summary>
/// Generates a starter script for a component: a skeleton with two INCLUDE lines (FORM and BUILD), places to add your own parameters and build steps,
/// and English comments explaining every step. The skeleton compiles without errors and can be imported without any edit.
/// </summary>
public static partial class ComponentStarter
{
    public const string DefaultProductName = "New product";
    public const string DefaultPrefix = "MY_";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,15}_$")]
    private static partial Regex ValidPrefix();

    /// <summary>Error message if the product name or prefix cannot be used (they go straight into a string and into parameter names of the script), otherwise null.</summary>
    public static string? Validate(string productName, string prefix)
    {
        if (productName.Length is 0 or > 80) return "productName must be 1-80 characters.";
        if (productName.Any(c => c is '"' or '\\' || char.IsControl(c))) return "productName must not contain quotes, backslashes or line breaks.";
        if (!ValidPrefix().IsMatch(prefix)) return "prefix must start with a letter, use letters and digits only, and end with '_' (for example MY_).";
        return null;
    }

    public static string Build(ComponentInfo c, string productName, string prefix)
    {
        var sb = new System.Text.StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');

        var parameters = Wrap(string.Join(", ", c.Parameters.Select(p => $"{p.Name} ({p.Label})")), 100);
        var lengthVar = c.SharedVariables.LastOrDefault(v => v.Type == "LENGTH"); // usually the top elevation (hTop)
        var vars = c.SharedVariables.Count == 0 ? "(this component has no shared variables)" : string.Join(", ", c.SharedVariables.Select(v => v.Name));
        var example = prefix + "Example";

        L("! ============================================================================");
        L($"! Starter script: uses the component \"{c.Title}\" (library {c.Name}).");
        L("! Generated by the Components page of CAD Automation. Edit this file, then Import it again to place an order.");
        L("!");
        L("! How to read this file:");
        L("!   - The header (GEA_VERSION ... TARGET) declares the product.");
        L("!   - BEGIN_FORM ... END_FORM are the input fields of the order form.");
        L("!   - BEGIN_BUILD ... END_BUILD are the steps that build the model in Inventor.");
        L("!   - A line starting with ! is a comment and is ignored when the script runs.");
        L("! ============================================================================");
        L();
        L("! Step 1: version of the .gea language (currently 1). Required, and it must be the first statement of the file.");
        L("GEA_VERSION 1");
        L("! Step 2: product name, shown on the web and in the order list.");
        L($"PRODUCT_NAME \"{productName}\"");
        L($"! Step 3: prefix for the parameters you add (for example {example}). The component's own parameters keep their prefix");
        L($"! ({string.Join(", ", c.Prefixes)}); both kinds of prefix are valid.");
        L($"PARAM_PREFIX {prefix}");
        L("! Step 4: the kind of document to build. PART is a Part (.ipt); the web accepts only PART for now.");
        L("TARGET PART");
        L();
        L("BEGIN_FORM");
        L();
        L($"  ! Step 5: insert the parameter group of the component. This line is replaced by the FORM part of the library {c.Name},");
        L("  ! which contains these parameters:");
        foreach (var w in parameters) L("  !   " + w);
        L($"  INCLUDE lib:{c.Name}");
        L();
        L("  ! Step 6: add your own parameters here. Each USER_INPUT_PARAM line declares one input field");
        L("  ! (name, type and unit, default value, MIN/MAX, LABEL). Remove the leading ! from the line below to enable the example:");
        L($"  !   USER_INPUT_PARAM {example} LENGTH 100 mm MIN 10 MAX 500 LABEL \"Example\"");
        L();
        L("END_FORM");
        L();
        L("BEGIN_BUILD");
        L();
        L($"  ! Step 7: insert the build steps of the component. This line is replaced by the BUILD part of the library {c.Name}.");
        L("  ! After this line you can use the shared variables (do not declare them again):");
        L($"  !   {vars}");
        L($"  INCLUDE lib:{c.Name}");
        L();
        L("  ! Step 8: add your own build steps here. The example below builds a box with the Block builder:");
        L("  ! SizeX, SizeD, Height are the dimensions, FEATURE_OUT receives the solid body just created, SET_REF_NAME names it.");
        if (lengthVar != null)
            L($"  ! Add FEATURE_IN \"Elevation\" {lengthVar.Name} to place the box at the elevation of the variable {lengthVar.Name} (from the component).");
        L("  ! Remove the leading ! from the lines below to enable the example:");
        L("  !   DECLARE_REFERENCE myBody");
        L("  !   CREATE_FEATURE builder:Block");
        L("  !     FEATURE_IN \"SizeX\"  200 mm");
        L("  !     FEATURE_IN \"SizeD\"  200 mm");
        L("  !     FEATURE_IN \"Height\" 100 mm");
        L("  !     FEATURE_OUT myBody \"Body\"");
        L("  !   END_CREATE_FEATURE");
        L($"  !   SET_REF_NAME myBody \"{example}\"");
        L();
        L("END_BUILD");
        return sb.ToString();
    }

    /// <summary>Splits text into lines no longer than <paramref name="width"/> characters, breaking after a comma.</summary>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var part in text.Split(", "))
        {
            var piece = cur.Length == 0 ? part : cur + ", " + part;
            if (piece.Length > width && cur.Length > 0) { lines.Add(cur + ","); cur = part; }
            else cur = piece;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }
}
