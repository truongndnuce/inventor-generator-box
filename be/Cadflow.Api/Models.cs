using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cadflow.Api;

/// <summary>Status strings in JSON are lowercase (queued, running...) so the web can use them directly as display keys.</summary>
public sealed class JobStatusConverter() : JsonStringEnumConverter<JobStatus>(JsonNamingPolicy.CamelCase);

[JsonConverter(typeof(JobStatusConverter))]
public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed class JobEvent
{
    public DateTimeOffset Time { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ArtifactInfo
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Object key in the R2 bucket (<c>jobs/&lt;jobId&gt;/&lt;name&gt;</c>). Null means the file is on the server disk (an old job or R2 is not configured).</summary>
    public string? Key { get; set; }
}

public sealed class Job
{
    public string Id { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public int ProductVersion { get; set; } = 1;
    public Dictionary<string, double> Inputs { get; set; } = new();
    /// <summary>Text value of a text/radio field (only present for jobs built from a .gea script that has such fields).</summary>
    public Dictionary<string, string> TextInputs { get; set; } = new();
    public string RequestedBy { get; set; } = "";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? Step { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Worker { get; set; }
    public DateTimeOffset? LastHeartbeat { get; set; }
    public int Attempts { get; set; }
    public Dictionary<string, double> Summary { get; set; } = new();
    public string? Error { get; set; }
    /// <summary>The .gea script used to build the job (null if the product is not built through .gea). The content lives in <see cref="ScriptStore"/>, keyed by SHA-256.</summary>
    public string? ScriptName { get; set; }
    public string? ScriptSha256 { get; set; }
    /// <summary>"bundled" (script that ships with the product) or "imported" (a .gea file imported by the user). Null if no script is used.</summary>
    public string? ScriptSource { get; set; }
    public List<ArtifactInfo> Artifacts { get; set; } = new();
    /// <summary>When the result files were deleted by the retention policy (Retention:Days). The job remains for statistics and duplicating the order.</summary>
    public DateTimeOffset? ArtifactsPurgedAt { get; set; }
    public List<JobEvent> Events { get; set; } = new();
    /// <summary>generate (full build), preview (fast, image only) or export (rebuild of an older job). See <see cref="JobKinds"/>.</summary>
    public string Kind { get; set; } = JobKinds.Generate;
    /// <summary>The queue the job waits in (build or filter); decided by <see cref="QueuePolicy"/> from the kind.</summary>
    public string Queue { get; set; } = JobQueues.Build;
    /// <summary>0 = most urgent, 100 = least. Inside one queue, lower numbers are handed out first.</summary>
    public int Priority { get; set; } = QueuePolicy.DefaultPriority;
    /// <summary>For an export job: the job it rebuilds.</summary>
    public string? SourceJobId { get; set; }
    /// <summary>How many queued jobs of the same queue will be handed out before this one. Only set by GET /api/jobs/{id} for a queued job.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? QueuePosition { get; set; }
}

public sealed class WorkerInfo
{
    public string Name { get; set; } = "";
    /// <summary>The queues the worker asked for in its last claim, in its order of preference; null = takes any queue.</summary>
    public string[]? Queues { get; set; }
    public string? InventorVersion { get; set; }
    public string? AddinVersion { get; set; }
    public bool Busy { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public bool Online { get; set; }
}

// ---- request/response contracts

/// <summary><paramref name="ScriptId"/>: order from an imported .gea script (see POST /api/gea/import); when present, ProductCode is ignored.</summary>
public sealed record CreateJobRequest(string? ProductCode, Dictionary<string, double>? Inputs, string? RequestedBy, string? ScriptId = null, Dictionary<string, string>? TextInputs = null, string? ConfigId = null, string? Kind = null);

/// <summary>Who asks for the rebuild of an older job (POST /api/jobs/{id}/rebuild).</summary>
public sealed record RebuildRequest(string? RequestedBy);

public sealed record GeaTextRequest(string? Text);

public sealed record GeaFormRequest(Dictionary<string, double>? Values, Dictionary<string, string>? Texts = null);

public sealed record ValidateRequest(Dictionary<string, double>? Values);

public sealed record WorkerHello(string? Worker, string? InventorVersion, string? AddinVersion, bool Busy);

/// <param name="Products">Product codes the worker accepts (each Inventor add-in accepts only its own products); null/empty = accepts all (old worker).</param>
/// <param name="Queues">The worker's order of preference between queues (build, filter); null/empty = takes any queue by priority.</param>
public sealed record ClaimRequest(string? Worker, string[]? Products = null, string[]? Queues = null);

public sealed record ProgressRequest(string? Step, string? Message);

public sealed record CompleteRequest(Dictionary<string, double>? Summary);

public sealed record FailRequest(string? Error, bool Retryable);

/// <summary>The part of the job sent down to the worker: only what the worker needs to build.</summary>
public sealed record WorkItem(string Id, string ProductCode, int ProductVersion, Dictionary<string, double> Inputs, string? ScriptText = null, string? ScriptName = null, Dictionary<string, string>? TextInputs = null, string Kind = JobKinds.Generate);

