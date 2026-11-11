using System.Text.RegularExpressions;
using InventorAddin.Gea;

namespace Cadflow.Api;

/// <summary>
/// A message from the .gea compiler, moved from "line N of the generated file" to the model element that produced it.
/// </summary>
/// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
/// <param name="Path">Model path (<c>rules[2]</c>, <c>groups[1].params[4].visibleWhen</c>, <c>vars[0].expr</c>, <c>steps[0]</c>), or null when the message is about the file as a whole.</param>
/// <param name="Line">Only for a raw build step (<c>steps[i]</c>): the line of the text the author typed, 1-based. Null for everything else, so a line number of the generated file is never shown as a location.</param>
public sealed record DesignDiagnostic(string Severity, string Code, string Message, string? Path, int? Line);

/// <summary>
/// The result of checking a design: the validator's issues, and (when there were no validator errors) the generated script and the compiler's messages.
/// <see cref="Output"/> is null when the validator refused the model; then <see cref="Diagnostics"/> is empty.
/// </summary>
public sealed record DesignCheckResult(DesignOutput? Output, IReadOnlyList<DesignIssue> Issues, IReadOnlyList<DesignDiagnostic> Diagnostics)
{
    public bool HasErrors => Issues.Any(i => !i.IsWarning) || Diagnostics.Any(d => d.Severity == "error");
}

public static partial class DesignDiagnostics
{
    [GeneratedRegex(@"^steps\[\d+\]$")]
    private static partial Regex RawStepPath();

    /// <summary>
    /// Validates the model, then generates the script and compiles it with the real compiler (the same one that will run it, with <c>INCLUDE lib:</c> resolved),
    /// and maps every compiler message back to the model.
    /// </summary>
    public static DesignCheckResult Check(DesignModel? model)
    {
        var issues = DesignValidator.Validate(model);
        if (model is null || issues.Any(i => !i.IsWarning))
            return new DesignCheckResult(null, issues, Array.Empty<DesignDiagnostic>());

        var output = DesignGenerator.Generate(model);
        var script = GeaScript.Compile(output.Text, GeaLibraries.Resolve);
        return new DesignCheckResult(output, issues, Map(output, script.Diagnostics));
    }

    /// <summary>Moves compiler messages from generated-file lines to model paths, keeping the compiler's order.</summary>
    public static IReadOnlyList<DesignDiagnostic> Map(DesignOutput output, IEnumerable<GeaDiagnostic> diagnostics)
    {
        var result = new List<DesignDiagnostic>();
        foreach (var d in diagnostics)
        {
            var path = output.Find(d.Line);
            int? line = null;
            if (path != null && RawStepPath().IsMatch(path) && output.Entry(path) is { } entry)
                line = d.Line - entry.FirstLine + 1 + entry.LineOffset;
            result.Add(new DesignDiagnostic(d.Severity == GeaSeverity.Error ? "error" : "warning", d.Code, d.Message, path, line));
        }
        return result;
    }
}
