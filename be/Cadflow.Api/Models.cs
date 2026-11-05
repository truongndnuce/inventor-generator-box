using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cadflow.Api;

/// <summary>Chuỗi trạng thái trên JSON viết thường (queued, running...) để web dùng trực tiếp làm khóa hiển thị.</summary>
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
}

public sealed class Job
{
    public string Id { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public int ProductVersion { get; set; } = 1;
    public Dictionary<string, double> Inputs { get; set; } = new();
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
    public List<ArtifactInfo> Artifacts { get; set; } = new();
    public List<JobEvent> Events { get; set; } = new();
}

public sealed class WorkerInfo
{
    public string Name { get; set; } = "";
    public string? InventorVersion { get; set; }
    public string? AddinVersion { get; set; }
    public bool Busy { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public bool Online { get; set; }
}

// ---- request/response contracts

public sealed record CreateJobRequest(string? ProductCode, Dictionary<string, double>? Inputs, string? RequestedBy);

public sealed record ValidateRequest(Dictionary<string, double>? Values);

public sealed record WorkerHello(string? Worker, string? InventorVersion, string? AddinVersion, bool Busy);

public sealed record ClaimRequest(string? Worker);

public sealed record ProgressRequest(string? Step, string? Message);

public sealed record CompleteRequest(Dictionary<string, double>? Summary);

public sealed record FailRequest(string? Error, bool Retryable);

/// <summary>Phần job gửi xuống worker: chỉ những gì worker cần để dựng.</summary>
public sealed record WorkItem(string Id, string ProductCode, int ProductVersion, Dictionary<string, double> Inputs);
