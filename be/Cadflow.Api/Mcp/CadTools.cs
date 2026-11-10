using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Cadflow.Api.Mcp;

/// <summary>A job draft waiting for the user to confirm (not in the queue yet). Expires after 30 minutes.</summary>
public sealed record Draft(string Id, CreateJobRequest Request, string Product, Dictionary<string, double> Values, DateTimeOffset Expires);

public sealed class DraftStore
{
    private readonly ConcurrentDictionary<string, Draft> _drafts = new();

    public Draft Add(CreateJobRequest req, string product, Dictionary<string, double> values)
    {
        foreach (var kv in _drafts.Where(kv => kv.Value.Expires < DateTimeOffset.UtcNow)) _drafts.TryRemove(kv.Key, out _);
        var d = new Draft(Guid.NewGuid().ToString("n")[..12], req, product, values, DateTimeOffset.UtcNow.AddMinutes(30));
        _drafts[d.Id] = d;
        return d;
    }

    /// <summary>Takes and removes the draft (each draft can be confirmed only once).</summary>
    public Draft? Take(string id) =>
        _drafts.TryRemove(id, out var d) && d.Expires >= DateTimeOffset.UtcNow ? d : null;
}

/// <summary>
/// The single set of tools for both the MCP server (/mcp) and chat (/api/chat). Every tool returns a JSON string.
/// The tools only wrap existing logic (Products, ConfigStore, JobService, JobStore); they add no new business rules.
/// </summary>
[McpServerToolType]
public sealed class CadTools(JobStore store, ConfigStore configs, JobService jobs, DraftStore drafts)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Ser(object o) => JsonSerializer.Serialize(o, Json);
    private static string Err(string message, object? extra = null) => Ser(new { error = message, details = extra });

    [McpServerTool(Name = "list_products", ReadOnly = true), Description("List the products that can be ordered (code, name, description).")]
    public string ListProducts() =>
        Ser(Products.All.Select(p => new { code = p.Code, name = p.Name, description = p.Description }));

    [McpServerTool(Name = "get_product_schema", ReadOnly = true),
     Description("Get a product's parameters: name, label, group, unit (mm/degree/count), integer flag, default, min, max. Values are in mm, degrees or integer counts.")]
    public string GetProductSchema(
        [Description("Product code, e.g. 'gas-turbine'")] string code,
        [Description("Optional configuration id; its values replace the defaults")] string? configId = null)
    {
        if (Products.Find(code) is not { } p) return Err($"Unknown product '{code}'.");
        var cfg = string.IsNullOrWhiteSpace(configId) ? null : configs.Get(p, configId);
        if (!string.IsNullOrWhiteSpace(configId) && cfg == null) return Err("Unknown configuration.");
        return Ser(p.Schema(configs.Effective(p, null, cfg)));
    }

    [McpServerTool(Name = "list_configs", ReadOnly = true), Description("List the ACTIVE configurations of a product (only active ones can be ordered).")]
    public string ListConfigs([Description("Product code")] string code) =>
        Products.Find(code) is not { } p ? Err($"Unknown product '{code}'.")
        : Ser(configs.List(p).Where(c => c.Status == "active")
            .Select(c => new { id = c.Id, name = c.Name, description = c.Description, isDefault = c.IsDefault, operation = c.Operation, cad = c.Cad }));

    [McpServerTool(Name = "validate_job", ReadOnly = true),
     Description("Validate parameter values for a product without creating anything. Missing values take defaults. Returns the complete values and any rule violations.")]
    public string ValidateJob(
        [Description("Product code")] string code,
        [Description("Parameter name -> number (mm / degrees / count). Only the values to override.")] Dictionary<string, double>? values = null,
        [Description("Optional active configuration id")] string? configId = null)
    {
        if (Products.Find(code) is not { } p) return Err($"Unknown product '{code}'.");
        ProductConfig? cfg = null;
        if (!string.IsNullOrWhiteSpace(configId) && (cfg = configs.Get(p, configId)) == null) return Err("Unknown configuration.");
        var unknown = (values?.Keys ?? Enumerable.Empty<string>()).Where(k => p.Parameters.All(d => d.Name != k)).ToArray();
        var full = configs.Effective(p, values, cfg);
        var issues = p.Validate(full);
        return Ser(new { valid = issues.Count == 0, values = full, issues, unknownParameters = unknown });
    }

    /// <summary>Chat only: validates, then stores a draft; the job is created only when the user presses confirm in the UI.</summary>
    public (string Json, Draft? Draft) ProposeJob(string? code, Dictionary<string, double>? values, string? configId,
        string? scriptId, Dictionary<string, string>? textInputs, string? requestedBy)
    {
        var req = new CreateJobRequest(code, values, requestedBy, scriptId, textInputs, configId);
        if (!string.IsNullOrWhiteSpace(scriptId))
        {
            var form = jobs.ScriptForm(scriptId, values, textInputs);
            if (form.Status != 200) return (Ser(form.Body), null);
            var d = drafts.Add(req, JobService.ImportedProduct, values ?? new());
            return (Ser(new { drafted = true, draftId = d.Id, note = "Shown to the user for confirmation. The job is NOT created yet.", form = form.Body }), d);
        }
        if (Products.Find(string.IsNullOrWhiteSpace(code) ? "box" : code) is not { } p) return (Err($"Unknown product '{code}'."), null);
        ProductConfig? cfg = null;
        if (!string.IsNullOrWhiteSpace(configId))
        {
            cfg = configs.Get(p, configId);
            if (cfg == null) return (Err("Unknown configuration."), null);
            if (cfg.Status != "active") return (Err($"Configuration '{cfg.Name}' is {cfg.Status}; only active configurations can be ordered."), null);
        }
        var full = configs.Effective(p, values, cfg);
        var issues = p.Validate(full);
        if (issues.Count > 0) return (Ser(new { drafted = false, error = "The values violate product rules.", issues }), null);
        var draft = drafts.Add(req with { ProductCode = p.Code }, p.Code, full);
        return (Ser(new { drafted = true, draftId = draft.Id, product = p.Code, values = full, note = "Shown to the user for confirmation. The job is NOT created yet." }), draft);
    }

    /// <summary>Confirms a draft (called from the chat UI, not from the LLM).</summary>
    public CreateJobResult Confirm(string draftId, string? requestedBy)
    {
        if (drafts.Take(draftId) is not { } d) return new(null, 404, new { error = "The draft expired or was already used." });
        return jobs.Create(d.Request with { RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? d.Request.RequestedBy : requestedBy });
    }

    [McpServerTool(Name = "create_job"),
     Description("Create (enqueue) a job immediately. Validate first with validate_job and confirm with the user before calling. Provide productCode, or scriptId for an imported .gea script.")]
    public string CreateJob(
        [Description("Product code, e.g. 'box', 'transformer', 'gas-turbine', 'generator'")] string? productCode = null,
        [Description("Parameter name -> number (mm / degrees / count)")] Dictionary<string, double>? values = null,
        [Description("Optional active configuration id")] string? configId = null,
        [Description("Script id from import_gea (instead of productCode)")] string? scriptId = null,
        [Description("Text/radio fields of an imported script")] Dictionary<string, string>? textInputs = null,
        [Description("Who is ordering")] string? requestedBy = null)
    {
        var r = CreateNow(productCode, values, configId, scriptId, textInputs, requestedBy ?? "mcp");
        return r.Ok ? Ser(JobView(r.Job!)) : Ser(r.Error!);
    }

    /// <summary>Creates the job right away (the server still re-checks everything). Used by MCP and by chat in auto-create mode.</summary>
    public CreateJobResult CreateNow(string? code, Dictionary<string, double>? values, string? configId, string? scriptId,
        Dictionary<string, string>? textInputs, string? requestedBy) =>
        jobs.Create(new CreateJobRequest(code, values, requestedBy, scriptId, textInputs, configId));

    [McpServerTool(Name = "import_gea"), Description("Import the text of a .gea script; returns scriptId and the form (parameters) so a job can be created from it.")]
    public string ImportGea([Description("Full .gea file text")] string text) => Ser(jobs.Import(text).Body);

    [McpServerTool(Name = "get_script_form", ReadOnly = true), Description("Evaluate an imported script's form for given values (fields can show/hide depending on values).")]
    public string GetScriptForm(string scriptId, Dictionary<string, double>? values = null, Dictionary<string, string>? texts = null) =>
        Ser(jobs.ScriptForm(scriptId, values, texts).Body);

    [McpServerTool(Name = "get_job", ReadOnly = true), Description("Get a job's status, step, inputs, summary and error.")]
    public string GetJob(string jobId) => store.Get(jobId) is { } j ? Ser(JobView(j, full: true)) : Err("Job not found.");

    [McpServerTool(Name = "list_jobs", ReadOnly = true), Description("List recent jobs, newest first. Optional filters: status (queued, running, succeeded, failed, cancelled), productCode.")]
    public string ListJobs(string? status = null, string? productCode = null, int limit = 10)
    {
        IEnumerable<Job> list = store.List();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<JobStatus>(status, true, out var st)) return Err($"Unknown status '{status}'.");
            list = list.Where(j => j.Status == st);
        }
        if (!string.IsNullOrWhiteSpace(productCode))
            list = list.Where(j => string.Equals(j.ProductCode, productCode, StringComparison.OrdinalIgnoreCase));
        return Ser(list.Take(Math.Clamp(limit, 1, 50)).Select(j => JobView(j)));
    }

    [McpServerTool(Name = "cancel_job", Destructive = true), Description("Cancel a queued or running job.")]
    public string CancelJob(string jobId) => store.Cancel(jobId) ? Ser(new { cancelled = true }) : Err("The job is not found or already finished.");

    public static object JobView(Job j, bool full = false) => new
    {
        id = j.Id, product = j.ProductCode, status = j.Status.ToString().ToLowerInvariant(), step = j.Step,
        requestedBy = j.RequestedBy, createdAt = j.CreatedAt, error = j.Error,
        inputs = full ? j.Inputs : null, summary = full ? j.Summary : null,
        artifacts = full ? j.Artifacts.Select(a => a.Name) : null,
    };
}
