using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using InventorAddin.Host;

namespace InventorAddin.Web;

/// <summary>
/// Connection settings to the web server, read from %AppData%\InventorBoxAddin\worker.json (a default one is created if missing).
/// Set "enabled": false so the add-in does not take jobs from the web.
/// </summary>
public sealed class WorkerSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = "http://localhost:18080";
    public string WorkerKey { get; set; } = "dev-worker-key";
    public string WorkerName { get; set; } = Environment.MachineName;
    public int PollSeconds { get; set; } = 3;
    public int HeartbeatSeconds { get; set; } = 10;
    /// <summary>
    /// The queues this machine takes jobs from, in order of preference (like the node settings of SDS): "build" = full builds and exports,
    /// "filter" = fast previews. ["build", "filter"] takes a full build first and a preview only when no build waits;
    /// ["filter", "build"] does the opposite. An empty list takes any queue, most urgent job first.
    /// </summary>
    public string[] Queues { get; set; } = { "build", "filter" };
    /// <summary>A separate working folder for each job (the .ipt, .stp, .png files before they are uploaded).</summary>
    public string WorkDir { get; set; } = Path.Combine(Log.DataDirectory, "jobs");

    /// <summary>A copy for one add-in: the worker name gets a suffix so each add-in is its own worker on the server.</summary>
    public WorkerSettings ForAddin(string addinId) => new()
    {
        Enabled = Enabled, ServerUrl = ServerUrl, WorkerKey = WorkerKey, WorkerName = WorkerName + "-" + addinId,
        PollSeconds = PollSeconds, HeartbeatSeconds = HeartbeatSeconds, WorkDir = WorkDir, Queues = Queues,
    };

    public static string FilePath => Path.Combine(Log.DataDirectory, "worker.json");

    public static WorkerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<WorkerSettings>(File.ReadAllText(FilePath), Json) ?? new WorkerSettings();
                s.PollSeconds = Math.Clamp(s.PollSeconds, 1, 60);
                s.HeartbeatSeconds = Math.Clamp(s.HeartbeatSeconds, 2, 60);
                s.Queues = (s.Queues ?? new[] { "build", "filter" })
                    .Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim().ToLowerInvariant()).Distinct().ToArray();
                if (string.IsNullOrWhiteSpace(s.WorkerName)) s.WorkerName = Environment.MachineName;
                return s;
            }

            var def = new WorkerSettings();
            Directory.CreateDirectory(Log.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(def, Json));
            return def;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read worker.json, using defaults", ex);
            return new WorkerSettings();
        }
    }
}
