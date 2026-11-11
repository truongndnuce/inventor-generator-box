using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using InventorAddin.Gea;

namespace Cadflow.Api;

/// <summary>A problem found in a design. <see cref="Path"/> names the model element (<c>groups[1].params[2].name</c>) so the UI can show the message next to that field.</summary>
public sealed record DesignIssue(string Path, string Message, bool IsWarning = false);

/// <summary>
/// Checks a <see cref="DesignModel"/> before it is turned into a script: names, types, limits and the text that will go into quoted strings or expressions.
/// It does not decide whether an expression is correct: that is the compiler's job (the generator compiles the result and maps those errors back to the model).
/// The checks on names and text also keep a model from injecting statements into the generated file.
/// </summary>
public static partial class DesignValidator
{
    public const int MaxParams = 200;          // same as GeaForms.MaxFields
    public const int MaxGroups = 50;
    public const int MaxRules = 100;
    public const int MaxVars = 100;
    public const int MaxRawBytes = 100 * 1024;
    public const int MaxModelBytes = 256 * 1024;
    public const int MaxProductName = 80;
    public const int MaxText = 200;            // labels, tooltips, group titles, radio options
    public const int MaxMessage = 400;         // rule messages (the shipped scripts have messages of about 150 characters)
    public const int MaxExpr = 500;

    public static readonly IReadOnlyList<string> ParamTypes = new[] { "LENGTH", "ANGLE", "INTEGER", "DOUBLE", "STRING", "CHECKBOX", "RADIO" };
    public static readonly IReadOnlyList<string> VarTypes = new[] { "LENGTH", "ANGLE", "INTEGER", "DOUBLE" };

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,15}_$")]
    private static partial Regex ValidPrefix();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    public static List<DesignIssue> Validate(DesignModel? model)
    {
        var issues = new List<DesignIssue>();
        if (model is null)
        {
            issues.Add(new("model", "The design is empty."));
            return issues;
        }

        // The size check comes first and stops everything else, so an oversized body is never walked.
        if (Encoding.UTF8.GetByteCount(DesignJson.Serialize(model)) > MaxModelBytes)
        {
            issues.Add(new("model", $"The design is larger than {MaxModelBytes / 1024} KB."));
            return issues;
        }

        if (model.Version != 1) issues.Add(new("version", $"Unknown design version {model.Version}; this Designer reads version 1."));

        var product = model.Product ?? new DesignProduct();
        CheckProduct(product, issues);

        var groups = model.Groups ?? new();
        var rules = model.Rules ?? new();
        var vars = model.Vars ?? new();
        var steps = model.Steps ?? new();

        if (groups.Count > MaxGroups) issues.Add(new("groups", $"At most {MaxGroups} groups are allowed."));
        if (rules.Count > MaxRules) issues.Add(new("rules", $"At most {MaxRules} rules are allowed."));
        if (vars.Count > MaxVars) issues.Add(new("vars", $"At most {MaxVars} variables are allowed."));
        int paramCount = groups.Sum(g => g?.Params?.Count ?? 0);
        if (paramCount > MaxParams) issues.Add(new("groups", $"At most {MaxParams} parameters are allowed in total (this design has {paramCount})."));

        var names = new Dictionary<string, string>(StringComparer.Ordinal);   // name -> path where it was first declared
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            if (group is null) { issues.Add(new($"groups[{g}]", "The group is empty.")); continue; }
            if (string.IsNullOrWhiteSpace(group.Title)) issues.Add(new($"groups[{g}].title", "The group needs a title."));
            else CheckText(group.Title, $"groups[{g}].title", MaxText, issues);

            var ps = group.Params ?? new();
            for (int p = 0; p < ps.Count; p++)
            {
                var path = $"groups[{g}].params[{p}]";
                if (ps[p] is null) { issues.Add(new(path, "The parameter is empty.")); continue; }
                CheckParam(ps[p], path, product.Prefix ?? "", names, issues);
                if (!string.IsNullOrWhiteSpace(ps[p].VisibleWhen) && !string.IsNullOrEmpty(ps[p].Name)) hidden.Add(ps[p].Name);
            }
        }

        for (int v = 0; v < vars.Count; v++)
        {
            var path = $"vars[{v}]";
            if (vars[v] is null) { issues.Add(new(path, "The variable is empty.")); continue; }
            CheckVar(vars[v], path, names, issues);
            WarnAboutHidden(vars[v].Expr, hidden, path + ".expr", issues);
        }

        for (int r = 0; r < rules.Count; r++)
        {
            var path = $"rules[{r}]";
            if (rules[r] is null) { issues.Add(new(path, "The rule is empty.")); continue; }
            CheckExpression(rules[r].Expr, path + ".expr", issues);
            if (string.IsNullOrWhiteSpace(rules[r].Message)) issues.Add(new(path + ".message", "The rule needs a message."));
            else CheckText(rules[r].Message, path + ".message", MaxMessage, issues);
            WarnAboutHidden(rules[r].Expr, hidden, path + ".expr", issues);
        }

        for (int s = 0; s < steps.Count; s++)
        {
            var path = $"steps[{s}]";
            if (steps[s] is null) { issues.Add(new(path, "The step is empty.")); continue; }
            if (steps[s].Kind != "raw")
            {
                issues.Add(new(path + ".kind", "This version of the Designer only supports raw steps (kind \"raw\")."));
                continue;
            }
            CheckRawText(steps[s].Text, path + ".text", issues);
        }

        return issues;
    }

    // ------------------------------------------------------------------ product

    private static void CheckProduct(DesignProduct p, List<DesignIssue> issues)
    {
        var name = p.Name ?? "";
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxProductName)
            issues.Add(new("product.name", $"The product name must be 1-{MaxProductName} characters."));
        else if (name.Any(c => c is '"' or '\\' || char.IsControl(c)))
            issues.Add(new("product.name", "The product name must not contain quotes, backslashes or line breaks."));

        if (!ValidPrefix().IsMatch(p.Prefix ?? ""))
            issues.Add(new("product.prefix", "The prefix must start with a letter, use letters and digits only, and end with '_' (for example FN_)."));

        if (p.Target != "PART")
            issues.Add(new("product.target", "Only the target PART is supported."));
    }

    // ------------------------------------------------------------------ parameters

    private static void CheckParam(DesignParam p, string path, string prefix, Dictionary<string, string> names, List<DesignIssue> issues)
    {
        var name = p.Name ?? "";
        if (!ValidName().IsMatch(name))
            issues.Add(new(path + ".name", "The name must start with a letter and use only letters, digits and '_' (at most 64 characters)."));
        else
        {
            if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.Ordinal))
                issues.Add(new(path + ".name", $"The name must start with the prefix \"{prefix}\"."));
            if (GeaParser.Keywords.Contains(name))
                issues.Add(new(path + ".name", $"\"{name}\" is a keyword of the .gea language."));
            if (names.TryGetValue(name, out var first))
                issues.Add(new(path + ".name", $"The name \"{name}\" is already used ({first})."));
            else
                names[name] = path;
        }

        var type = p.Type ?? "";
        if (!ParamTypes.Contains(type))
        {
            issues.Add(new(path + ".type", "The type must be LENGTH, ANGLE, INTEGER, DOUBLE, STRING, CHECKBOX or RADIO."));
            return;   // the other checks depend on the type
        }

        bool numeric = type is "LENGTH" or "ANGLE" or "INTEGER" or "DOUBLE";

        // unit
        if (!string.IsNullOrEmpty(p.Unit))
        {
            if (type is not ("LENGTH" or "ANGLE")) issues.Add(new(path + ".unit", $"A {type} parameter has no unit."));
            else if (!GeaUnits.TryGet(p.Unit, out var ut, out _) || ut != (type == "LENGTH" ? GeaType.Length : GeaType.Angle))
                issues.Add(new(path + ".unit", type == "LENGTH" ? "The unit of a LENGTH must be mm, cm, m or in." : "The unit of an ANGLE must be deg or rad."));
        }

        // min / max / step
        if (!numeric)
        {
            if (p.Min != null) issues.Add(new(path + ".min", $"MIN does not apply to {type}."));
            if (p.Max != null) issues.Add(new(path + ".max", $"MAX does not apply to {type}."));
            if (p.Step != null) issues.Add(new(path + ".step", $"STEP does not apply to {type}."));
        }
        else
        {
            if (p.Min is { } mn && !double.IsFinite(mn)) issues.Add(new(path + ".min", "MIN must be a finite number."));
            if (p.Max is { } mx && !double.IsFinite(mx)) issues.Add(new(path + ".max", "MAX must be a finite number."));
            if (p.Min is { } a && p.Max is { } b && double.IsFinite(a) && double.IsFinite(b) && a > b)
                issues.Add(new(path + ".min", "MIN is greater than MAX."));
            if (p.Step is { } st && !(double.IsFinite(st) && st > 0)) issues.Add(new(path + ".step", "STEP must be a positive number."));
        }

        if (p.Required && type is "CHECKBOX" or "RADIO")
            issues.Add(new(path + ".required", $"REQUIRED does not apply to {type}."));

        // default and options
        if (type == "RADIO")
        {
            var options = p.Options ?? new();
            if (options.Count == 0) issues.Add(new(path + ".options", "A RADIO parameter needs at least one option."));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                if (string.IsNullOrWhiteSpace(o)) { issues.Add(new($"{path}.options[{i}]", "An option must not be empty.")); continue; }
                if (!CheckText(o, $"{path}.options[{i}]", MaxText, issues)) continue;
                if (!seen.Add(o)) issues.Add(new($"{path}.options[{i}]", $"\"{o}\" is a duplicate option."));
            }
            if (!string.IsNullOrEmpty(p.Default) && !options.Contains(p.Default))
                issues.Add(new(path + ".default", "The default must be one of the options."));
        }
        else
        {
            if (p.Options is { Count: > 0 }) issues.Add(new(path + ".options", $"Options only apply to RADIO, not {type}."));

            switch (type)
            {
                case "CHECKBOX":
                    if (p.Default is not ("TRUE" or "FALSE")) issues.Add(new(path + ".default", "The default of a CHECKBOX must be TRUE or FALSE."));
                    break;
                case "STRING":
                    if (p.Default != null) CheckText(p.Default, path + ".default", MaxText, issues);
                    break;
                default:
                    CheckNumberDefault(p, type, path, issues);
                    break;
            }
        }

        // free text
        if (p.Label != null) CheckText(p.Label, path + ".label", MaxText, issues);
        if (p.Tooltip != null) CheckText(p.Tooltip, path + ".tooltip", MaxText, issues);
        if (p.VisibleWhen != null) CheckExpression(p.VisibleWhen, path + ".visibleWhen", issues);
    }

    private static void CheckNumberDefault(DesignParam p, string type, string path, List<DesignIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(p.Default))
        {
            issues.Add(new(path + ".default", "A default value is required."));
            return;
        }
        if (!double.TryParse(p.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
        {
            issues.Add(new(path + ".default", "The default must be a number (use '.' as the decimal point)."));
            return;
        }
        if (type == "INTEGER" && Math.Abs(v - Math.Round(v)) > 1e-12)
        {
            issues.Add(new(path + ".default", "The default of an INTEGER must be a whole number."));
            return;
        }
        if ((p.Min is { } mn && double.IsFinite(mn) && v < mn) || (p.Max is { } mx && double.IsFinite(mx) && v > mx))
            issues.Add(new(path + ".default", "The default is outside the MIN-MAX range.", IsWarning: true));
    }

    // ------------------------------------------------------------------ variables

    private static void CheckVar(DesignVar v, string path, Dictionary<string, string> names, List<DesignIssue> issues)
    {
        var name = v.Name ?? "";
        if (!ValidName().IsMatch(name))
            issues.Add(new(path + ".name", "The name must start with a letter and use only letters, digits and '_' (at most 64 characters)."));
        else
        {
            if (GeaParser.Keywords.Contains(name)) issues.Add(new(path + ".name", $"\"{name}\" is a keyword of the .gea language."));
            if (names.TryGetValue(name, out var first)) issues.Add(new(path + ".name", $"The name \"{name}\" is already used ({first})."));
            else names[name] = path;
        }
        if (!VarTypes.Contains(v.Type ?? "")) issues.Add(new(path + ".type", "The type of a variable must be LENGTH, ANGLE, INTEGER or DOUBLE."));
        CheckExpression(v.Expr, path + ".expr", issues);
    }

    // ------------------------------------------------------------------ text and expressions

    /// <summary>Text that goes into a quoted string (label, tooltip, message, title, option): no control characters, limited length. Quotes and backslashes are fine, the generator escapes them.</summary>
    private static bool CheckText(string text, string path, int max, List<DesignIssue> issues)
    {
        if (text.Length > max) { issues.Add(new(path, $"At most {max} characters are allowed.")); return false; }
        if (text.Any(char.IsControl)) { issues.Add(new(path, "Line breaks and other control characters are not allowed here.")); return false; }
        return true;
    }

    /// <summary>
    /// An expression goes into the file as it is, so it must stay one line and must not start a comment, a block comment or a line join
    /// (that would swallow the rest of the line or file). A quoted string inside the expression may contain any of these.
    /// </summary>
    private static void CheckExpression(string? expr, string path, List<DesignIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(expr)) { issues.Add(new(path, "The expression is empty.")); return; }
        if (expr.Length > MaxExpr) { issues.Add(new(path, $"At most {MaxExpr} characters are allowed in an expression.")); return; }
        if (expr.Any(char.IsControl)) { issues.Add(new(path, "An expression must be on one line.")); return; }

        bool inString = false;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (inString)
            {
                if (c == '\\') i++;                 // an escaped character inside the string
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '!') { issues.Add(new(path, "'!' starts a comment in .gea and cannot be used in an expression (use <> for \"not equal\").")); return; }
            if (c == '/' && i + 1 < expr.Length && expr[i + 1] == '*') { issues.Add(new(path, "'/*' starts a comment in .gea and cannot be used in an expression.")); return; }
            if (c == '\\') { issues.Add(new(path, "A backslash is not allowed in an expression (outside a quoted string).")); return; }
        }
        if (inString) issues.Add(new(path, "The quoted string in the expression is not closed."));
    }

    private static void CheckRawText(string? text, string path, List<DesignIssue> issues)
    {
        if (text is null) return;
        if (Encoding.UTF8.GetByteCount(text) > MaxRawBytes) { issues.Add(new(path, $"The build text is larger than {MaxRawBytes / 1024} KB.")); return; }
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            issues.Add(new(path, "The build text contains control characters."));
    }

    /// <summary>A hidden field has no value, so a rule or variable that reads it sees its default; usually that is not what the author meant.</summary>
    private static void WarnAboutHidden(string? expr, HashSet<string> hidden, string path, List<DesignIssue> issues)
    {
        if (hidden.Count == 0 || string.IsNullOrEmpty(expr)) return;
        var used = Identifier().Matches(expr).Select(m => m.Value).Where(hidden.Contains).Distinct().ToList();
        foreach (var name in used)
            issues.Add(new(path, $"{name} is hidden by a \"show only when\" condition; while it is hidden this expression sees its default value.", IsWarning: true));
    }
}
