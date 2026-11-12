namespace Cadflow.Api;

/// <summary>The kinds of job a user can order. The kind decides the queue and the priority (see <see cref="QueuePolicy"/>) and how much the worker builds.</summary>
public static class JobKinds
{
    /// <summary>Full build: .ipt, .stp, pictures, PDF and DWG drawing.</summary>
    public const string Generate = "generate";
    /// <summary>Fast build for the screen: one picture only, no STEP and no drawing.</summary>
    public const string Preview = "preview";
    /// <summary>Rebuild of an older job (same script and values), for example to get back files removed by the retention policy.</summary>
    public const string Export = "export";

    public static readonly string[] All = [Generate, Preview, Export];

    public static bool IsKnown(string? kind) => kind != null && All.Contains(kind, StringComparer.Ordinal);
}

public static class JobQueues
{
    public const string Build = "build";
    public const string Filter = "filter";

    public static readonly string[] All = [Build, Filter];
}

/// <summary>
/// Maps a job kind to its queue and priority, the way SDS does (Main Build / Main Filter, priority 0-100 with 0 the most urgent).
/// The server decides: a web user does not pick a priority (there is no login yet). Defaults can be overridden in appsettings.json:
/// <c>"Queue": { "Kinds": { "preview": { "Queue": "filter", "Priority": 0 } } }</c>.
/// </summary>
public sealed class QueuePolicy
{
    public const int DefaultPriority = 20;
    public const int MinPriority = 0;
    public const int MaxPriority = 100;

    private static readonly Dictionary<string, (string Queue, int Priority)> Defaults = new(StringComparer.Ordinal)
    {
        [JobKinds.Preview] = (JobQueues.Filter, 0),
        [JobKinds.Generate] = (JobQueues.Build, DefaultPriority),
        [JobKinds.Export] = (JobQueues.Build, DefaultPriority),
    };

    private readonly Dictionary<string, (string Queue, int Priority)> _map = new(Defaults, StringComparer.Ordinal);

    public QueuePolicy(IConfiguration? config = null)
    {
        if (config == null) return;
        foreach (var kind in JobKinds.All)
        {
            var section = config.GetSection($"Queue:Kinds:{kind}");
            var queue = section["Queue"]?.Trim().ToLowerInvariant();
            var priority = section.GetValue<int?>("Priority");
            var (defQueue, defPriority) = Defaults[kind];
            _map[kind] = (
                queue != null && JobQueues.All.Contains(queue) ? queue : defQueue,
                Math.Clamp(priority ?? defPriority, MinPriority, MaxPriority));
        }
    }

    /// <summary>Queue and priority of a job kind; an unknown kind falls back to Generate.</summary>
    public (string Queue, int Priority) Resolve(string? kind) =>
        kind != null && _map.TryGetValue(kind, out var v) ? v : _map[JobKinds.Generate];
}
