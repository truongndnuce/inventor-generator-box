using InventorAddin.Gea;
using InventorAddin.Models;

namespace Cadflow.Api;

/// <summary>Result of creating a job: either a Job (201), or an HTTP error code with the error body.</summary>
public sealed record CreateJobResult(Job? Job, int Status, object? Error)
{
    public bool Ok => Job != null;
}

/// <summary>
/// Job-ordering logic shared by REST (POST /api/jobs), the MCP tools and chat: the server always checks once more at the end
/// and never trusts data from the browser or from the LLM.
/// </summary>
public sealed class JobService(JobStore store, ScriptStore scripts, ConfigStore configs, ILogger<JobService> log, QueuePolicy? queuePolicy = null)
{
    private readonly QueuePolicy _policy = queuePolicy ?? new QueuePolicy();

    public const string ImportedProduct = "imported"; // product code of a job ordered from a .gea script imported by the user

    public CreateJobResult Create(CreateJobRequest req)
    {
        var requestedByName = string.IsNullOrWhiteSpace(req.RequestedBy) ? "anonymous" : req.RequestedBy.Trim();
        if (requestedByName.Length > 80) requestedByName = requestedByName[..80];

        // A user orders a full build or a fast preview. `export` is only created by Rebuild.
        var kind = string.IsNullOrWhiteSpace(req.Kind) ? JobKinds.Generate : req.Kind.Trim().ToLowerInvariant();
        if (kind != JobKinds.Generate && kind != JobKinds.Preview)
            return new(null, 400, new { error = $"Unknown job kind '{req.Kind}'. Use '{JobKinds.Generate}' or '{JobKinds.Preview}'." });
        var (queue, priority) = _policy.Resolve(kind);

        // Ordering from an imported .gea script: the server recompiles it and computes the FORM one last time.
        if (!string.IsNullOrWhiteSpace(req.ScriptId))
        {
            if (scripts.Get(req.ScriptId) is not { } scriptSource)
                return new(null, 404, new { error = "Unknown script. Import the .gea file again." });
            var script = GeaScript.Compile(scriptSource, GeaLibraries.Resolve);
            if (GeaForms.Reject(script) is { } why)
                return new(null, 400, new { error = why, diagnostics = GeaForms.Diagnostics(script, errorsOnly: true) });
            var form = GeaForms.Evaluate(script, req.Inputs, req.TextInputs);
            if (!form.Ok)
                return new(null, 400, new { error = "The order violates the script's rules.", issues = form.Errors.Select(e => new { message = e.Message, @params = e.Params }) });

            var name = string.IsNullOrWhiteSpace(script.Program.ProductName) ? "imported.gea" : script.Program.ProductName;
            var (numbers, texts) = GeaForms.Complete(form);
            return new(store.Create(ImportedProduct, numbers, requestedByName, name, req.ScriptId, "imported", texts, kind, queue, priority), 201, null);
        }

        var product = Products.Find(string.IsNullOrWhiteSpace(req.ProductCode) ? "box" : req.ProductCode);
        if (product == null) return new(null, 404, new { error = $"Unknown product '{req.ProductCode}'." });

        ProductConfig? chosen = null;
        if (!string.IsNullOrWhiteSpace(req.ConfigId))
        {
            chosen = configs.Get(product, req.ConfigId);
            if (chosen == null) return new(null, 404, new { error = "Unknown configuration." });
            if (chosen.Status != "active") return new(null, 400, new { error = $"Configuration '{chosen.Name}' is {chosen.Status}; only active configurations can be ordered." });
        }
        var values = configs.Effective(product, req.Inputs, chosen);
        var issues = product.Validate(values);
        if (issues.Count > 0)
            return new(null, 400, new { error = "The order violates product rules.", issues });

        string? scriptSha = null;
        if (product.ScriptFile != null)
        {
            if (ScriptStore.ReadBundled(product.ScriptFile) is { } scriptText)
                scriptSha = scripts.Save(GeaScript.Compile(scriptText, GeaLibraries.Resolve).ExpandedText); // the worker receives the INCLUDE-expanded script
            else log.LogWarning("Bundled script {File} for product {Product} was not found; the job will have no .gea export.", product.ScriptFile, product.Code);
        }

        var job = store.Create(product.Code, values, requestedByName,
            scriptSha == null ? null : product.ScriptFile, scriptSha, scriptSha == null ? null : "bundled", kind: kind, queue: queue, priority: priority);
        return new(job, 201, null);
    }

    /// <summary>
    /// Queues an `export` job that rebuilds <paramref name="sourceId"/> with exactly the same script (by SHA-256) and values.
    /// The rules are checked again, because the product definition may have changed since the first run.
    /// </summary>
    public CreateJobResult Rebuild(string sourceId, string? requestedBy)
    {
        if (store.Get(sourceId) is not { } src) return new(null, 404, new { error = "Unknown job." });
        if (src.Status is JobStatus.Queued or JobStatus.Running)
            return new(null, 409, new { error = "The job is still waiting or running; rebuild it once it has finished." });

        string? scriptText = null;
        if (src.ScriptSha256 != null && (scriptText = scripts.Get(src.ScriptSha256)) == null)
            return new(null, 410, new { error = "The script of this job is no longer stored, so it cannot be rebuilt exactly." });

        if (src.ProductCode == ImportedProduct)
        {
            if (scriptText == null) return new(null, 410, new { error = "The imported script of this job is missing." });
            var script = GeaScript.Compile(scriptText, GeaLibraries.Resolve);
            if (GeaForms.Reject(script) is { } why) return new(null, 400, new { error = why });
            var form = GeaForms.Evaluate(script, src.Inputs, src.TextInputs);
            if (!form.Ok)
                return new(null, 400, new { error = "The values no longer satisfy the script's rules.", issues = form.Errors.Select(e => new { message = e.Message, @params = e.Params }) });
        }
        else
        {
            var product = Products.Find(src.ProductCode);
            if (product == null) return new(null, 404, new { error = $"Unknown product '{src.ProductCode}'." });
            var issues = product.Validate(src.Inputs);
            if (issues.Count > 0) return new(null, 400, new { error = "The values no longer satisfy the product rules.", issues });
        }

        var by = string.IsNullOrWhiteSpace(requestedBy) ? src.RequestedBy : requestedBy.Trim();
        if (by.Length > 80) by = by[..80];
        var (queue, priority) = _policy.Resolve(JobKinds.Export);
        var job = store.Create(src.ProductCode, new(src.Inputs), by, src.ScriptName, src.ScriptSha256, src.ScriptSource,
            new(src.TextInputs), JobKinds.Export, queue, priority, src.Id);
        return new(job, 201, null);
    }

    public sealed record Reply(int Status, object Body);

    /// <summary>Imports .gea content (it may carry a !@ value block), compiles it, stores it by hash and returns the form.</summary>
    public Reply Import(string? input)
    {
        var text = input ?? "";
        if (text.Trim().Length == 0) return new(400, new { error = "The file is empty." });
        if (System.Text.Encoding.UTF8.GetByteCount(text) > GeaForms.MaxScriptBytes)
            return new(400, new { error = $"The file is larger than {GeaForms.MaxScriptBytes / 1024} KB." });

        var block = GeaExport.ParseBlock(text);
        var clean = GeaExport.StripBlock(text);
        var script = GeaScript.Compile(clean, GeaLibraries.Resolve);
        if (GeaForms.Reject(script) is { } why)
            return new(400, new { error = why, diagnostics = GeaForms.Diagnostics(script) });

        var names = GeaForms.FieldNames(script).ToHashSet(StringComparer.Ordinal);
        var values = block?.Values.Where(kv => names.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, double>();
        var texts = block?.Texts.Where(kv => names.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, string>();
        var ignored = (block?.Values.Keys.Concat(block.Texts.Keys) ?? Enumerable.Empty<string>()).Where(k => !names.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        var warnings = new List<string>();
        if (block?.ScriptSha256 is { } exportedSha && exportedSha != ScriptStore.Sha256(clean))
            warnings.Add("The script was modified after this file was exported; the values may no longer fit it.");
        if (ignored.Length > 0)
            warnings.Add($"{ignored.Length} value(s) in the file do not match any parameter of the script and were ignored: {string.Join(", ", ignored.Take(5))}{(ignored.Length > 5 ? ", ..." : "")}.");

        var id = scripts.Save(script.ExpandedText); // store the INCLUDE-expanded script: it runs on its own, the worker needs no library
        return new(200, new
        {
            scriptId = id,
            name = script.Program.ProductName,
            hasValues = block != null,
            values,
            texts,
            ignored,
            warnings,
            diagnostics = GeaForms.Diagnostics(script),
            form = GeaForms.Describe(GeaForms.Evaluate(script, values, texts)),
        });
    }

    /// <summary>Form of an imported script, computed for the current values (the form can show/hide fields depending on the values).</summary>
    public Reply ScriptForm(string id, IReadOnlyDictionary<string, double>? values, IReadOnlyDictionary<string, string>? texts)
    {
        if (scripts.Get(id) is not { } text) return new(404, new { error = "Unknown script. Import the .gea file again." });
        var script = GeaScript.Compile(text, GeaLibraries.Resolve);
        if (GeaForms.Reject(script) is { } why) return new(400, new { error = why });
        return new(200, new { scriptId = id, name = script.Program.ProductName, form = GeaForms.Describe(GeaForms.Evaluate(script, values, texts)) });
    }
}
