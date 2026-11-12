using InventorAddin.Gea;

namespace Cadflow.Api;

/// <summary>A problem found by a dry run, placed on the model element that caused it.</summary>
/// <param name="Path">Model path (<c>rules[3]</c>, <c>groups[0].params[2]</c>, <c>steps[0]</c>), or null when it concerns the whole design.</param>
/// <param name="Line">Only for a raw build step: the line of the text the author typed, 1-based.</param>
public sealed record DesignRunMessage(string Severity, string Code, string Message, string? Path, int? Line);

/// <summary>
/// Result of <see cref="DesignRunner.Run"/>.
/// <see cref="Stage"/> says how far it got: <c>design</c> (the validator refused the model), <c>compile</c> (the compiler found errors),
/// <c>form</c> (the values break a rule or a limit: no build was run), <c>build</c> (the simulated build ran, <see cref="Valid"/> tells if it passed) or <c>timeout</c>.
/// </summary>
/// <param name="Form">The form for the entered values, in the shape of <see cref="GeaForms.Describe"/>; null for stages <c>design</c> and <c>compile</c>.</param>
/// <param name="Trace">The commands the simulated build issued (builder calls with their evaluated inputs); empty unless the build ran.</param>
public sealed record DesignRunResult(
    bool Valid,
    string Stage,
    object? Form,
    IReadOnlyList<DesignRunMessage> Errors,
    IReadOnlyList<DesignRunMessage> Warnings,
    IReadOnlyList<string> Trace,
    IReadOnlyList<string> Log);

/// <summary>
/// Dry run of a design: generate, compile, compute the form for the values, and (when the form is valid) run BUILD on the simulation host.
/// It never touches Inventor and never throws for a bad design or bad values: everything comes back as messages.
/// The simulated build records the builder calls; it does not make geometry.
/// </summary>
public static class DesignRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static DesignRunResult Run(DesignModel? model, IReadOnlyDictionary<string, double>? numbers = null, IReadOnlyDictionary<string, string>? texts = null) =>
        Run(model, numbers, texts, DefaultTimeout, () => new GeaSimulationHost());

    /// <param name="timeout">Wall-clock limit. The interpreter has its own step limit; this one protects the request if a host is slow.</param>
    /// <param name="hostFactory">Creates the host for BUILD; a host that is a <see cref="GeaSimulationHost"/> also gives the trace.</param>
    public static DesignRunResult Run(DesignModel? model, IReadOnlyDictionary<string, double>? numbers, IReadOnlyDictionary<string, string>? texts,
        TimeSpan timeout, Func<IGeaHost> hostFactory)
    {
        var task = Task.Run(() => RunCore(model, numbers, texts, hostFactory));
        if (task.Wait(timeout)) return task.Result;

        // The worker thread cannot be cancelled; it stops by itself at the interpreter's step limit.
        return Fail("timeout", new DesignRunMessage("error", "DESIGNER_TIMEOUT", "Dry run timed out", null, null));
    }

    private static DesignRunResult RunCore(DesignModel? model, IReadOnlyDictionary<string, double>? numbers, IReadOnlyDictionary<string, string>? texts, Func<IGeaHost> hostFactory)
    {
        try
        {
            var check = DesignDiagnostics.Check(model);
            var warnings = new List<DesignRunMessage>();
            foreach (var i in check.Issues.Where(i => i.IsWarning))
                warnings.Add(new("warning", "DESIGN", i.Message, i.Path, null));

            if (check.Output is null)
                return new(false, "design", null, check.Issues.Where(i => !i.IsWarning).Select(i => new DesignRunMessage("error", "DESIGN", i.Message, i.Path, null)).ToList(), warnings, [], []);

            var output = check.Output;
            if (check.Diagnostics.Any(d => d.Severity == "error"))
            {
                warnings.AddRange(check.Diagnostics.Where(d => d.Severity == "warning").Select(d => new DesignRunMessage("warning", d.Code, d.Message, d.Path, d.Line)));
                var errs = check.Diagnostics.Where(d => d.Severity == "error").Select(d => new DesignRunMessage("error", d.Code, d.Message, d.Path, d.Line)).ToList();
                return new(false, "compile", null, errs, warnings, [], []);
            }
            warnings.AddRange(check.Diagnostics.Where(d => d.Severity == "warning").Select(d => new DesignRunMessage("warning", d.Code, d.Message, d.Path, d.Line)));

            var script = GeaScript.Compile(output.Text, GeaLibraries.Resolve);
            if (GeaForms.Reject(script) is { } rejected)
                return new(false, "compile", null, [new("error", "DESIGNER_LIMIT", rejected, null, null)], warnings, [], []);

            var form = GeaForms.Evaluate(script, numbers, texts);
            var described = GeaForms.Describe(form);

            // form warnings (ASSERT WARN) are reported in every case
            foreach (var w in form.Warnings)
                warnings.Add(new("warning", w.Code, w.Message, output.Find(w.Line), null));

            if (!form.Ok)
            {
                var errs = form.Errors.Select(e => new DesignRunMessage("error", e.Code, e.Message, output.Find(e.Line), null)).ToList();
                return new(false, "form", described, errs, warnings, [], []);
            }

            var host = hostFactory();
            var run = script.RunBuild(form, host);
            var trace = host is GeaSimulationHost sim ? sim.Trace.ToList() : new List<string>();
            if (!run.Ok)
            {
                var d = run.Error!;
                var mapped = DesignDiagnostics.Map(output, [d])[0];
                return new(false, "build", described, [new("error", mapped.Code, mapped.Message, mapped.Path, mapped.Line)], warnings, trace, run.Log);
            }
            return new(true, "build", described, [], warnings, trace, run.Log);
        }
        catch (Exception ex)
        {
            return Fail("build", new DesignRunMessage("error", "DESIGNER_INTERNAL", "The dry run failed: " + ex.Message, null, null));
        }
    }

    private static DesignRunResult Fail(string stage, DesignRunMessage error) => new(false, stage, null, [error], [], [], []);
}
