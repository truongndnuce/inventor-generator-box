using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cadflow.Api;

/// <summary>
/// Kho job + hàng đợi, giữ trong bộ nhớ và ghi snapshot JSON xuống đĩa mỗi lần đổi.
/// Bản đầu (W0) đủ cho một server; khi cần thì thay bằng PostgreSQL (nhận job bằng FOR UPDATE SKIP LOCKED) mà không đổi API.
/// </summary>
public sealed class JobStore
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan WorkerOnlineWindow = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();
    private readonly Dictionary<string, WorkerInfo> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDir;
    private readonly string _snapshotPath;

    public JobStore(string dataDir)
    {
        _dataDir = dataDir;
        _snapshotPath = Path.Combine(dataDir, "jobs.json");
        Directory.CreateDirectory(ArtifactsRoot);
        Load();
    }

    public string ArtifactsRoot => Path.Combine(_dataDir, "artifacts");

    public string ArtifactDir(string jobId) => Path.Combine(ArtifactsRoot, jobId);

    // ---------------------------------------------------------------- truy vấn

    public IReadOnlyList<Job> List()
    {
        lock (_gate) return _jobs.OrderByDescending(j => j.CreatedAt).Select(Clone).ToList();
    }

    public Job? Get(string id)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.Id == id) is { } j ? Clone(j) : null;
    }

    public IReadOnlyList<WorkerInfo> Workers()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return _workers.Values
                .Select(w => new WorkerInfo
                {
                    Name = w.Name, InventorVersion = w.InventorVersion, AddinVersion = w.AddinVersion,
                    Busy = w.Busy, LastSeen = w.LastSeen, Online = now - w.LastSeen < WorkerOnlineWindow,
                })
                .OrderBy(w => w.Name).ToList();
        }
    }

    public bool AnyWorkerOnline()
    {
        lock (_gate) return _workers.Values.Any(w => DateTimeOffset.UtcNow - w.LastSeen < WorkerOnlineWindow);
    }

    // ---------------------------------------------------------------- người đặt hàng

    public Job Create(string productCode, Dictionary<string, double> inputs, string requestedBy)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new Job
        {
            Id = $"AJ-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}",
            ProductCode = productCode,
            Inputs = inputs,
            RequestedBy = requestedBy,
            Status = JobStatus.Queued,
            CreatedAt = now,
        };
        AddEvent(job, "Order received. Waiting for an Inventor worker to pick up the job...");
        lock (_gate)
        {
            _jobs.Add(job);
            Save();
            return Clone(job);
        }
    }

    public bool Cancel(string id)
    {
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job == null || job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled) return false;
            job.Status = JobStatus.Cancelled;
            job.FinishedAt = DateTimeOffset.UtcNow;
            AddEvent(job, "Cancelled by the user.");
            Save();
            return true;
        }
    }

    // ---------------------------------------------------------------- worker

    public void Heartbeat(WorkerHello hello)
    {
        if (string.IsNullOrWhiteSpace(hello.Worker)) return;
        lock (_gate)
        {
            var w = TouchWorker(hello.Worker!);
            w.InventorVersion = hello.InventorVersion ?? w.InventorVersion;
            w.AddinVersion = hello.AddinVersion ?? w.AddinVersion;
            w.Busy = hello.Busy;
            // Nhịp tim của worker cũng gia hạn các job nó đang chạy.
            foreach (var j in _jobs.Where(j => j.Status == JobStatus.Running && j.Worker == hello.Worker))
                j.LastHeartbeat = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Giao job Queued cũ nhất cho worker (khóa bằng lock, mỗi job chỉ một worker nhận).</summary>
    public WorkItem? Claim(string worker)
    {
        lock (_gate)
        {
            var w = TouchWorker(worker);
            var job = _jobs.Where(j => j.Status == JobStatus.Queued).OrderBy(j => j.CreatedAt).FirstOrDefault();
            if (job == null) return null;

            var now = DateTimeOffset.UtcNow;
            job.Status = JobStatus.Running;
            job.Worker = worker;
            job.StartedAt ??= now;
            job.LastHeartbeat = now;
            job.Attempts++;
            job.Step = "Starting";
            w.Busy = true;
            AddEvent(job, $"Picked up by worker '{worker}' (attempt {job.Attempts}).");
            Save();
            return new WorkItem(job.Id, job.ProductCode, job.ProductVersion, new Dictionary<string, double>(job.Inputs));
        }
    }

    public bool Progress(string id, string worker, string? step, string? message)
    {
        lock (_gate)
        {
            var job = OwnedRunning(id, worker);
            if (job == null) return false;
            job.LastHeartbeat = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(step)) job.Step = step;
            if (!string.IsNullOrWhiteSpace(message ?? step)) AddEvent(job, message ?? step!);
            Save();
            return true;
        }
    }

    public bool AddArtifact(string id, string worker, ArtifactInfo artifact)
    {
        lock (_gate)
        {
            var job = OwnedRunning(id, worker);
            if (job == null) return false;
            job.LastHeartbeat = DateTimeOffset.UtcNow;
            job.Artifacts.RemoveAll(a => a.Name == artifact.Name);
            job.Artifacts.Add(artifact);
            Save();
            return true;
        }
    }

    public bool Complete(string id, string worker, Dictionary<string, double>? summary)
    {
        lock (_gate)
        {
            var job = OwnedRunning(id, worker);
            if (job == null) return false;
            job.Status = JobStatus.Succeeded;
            job.Step = "Done";
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.Summary = summary ?? new();
            job.Error = null;
            AddEvent(job, "Build finished.");
            FreeWorker(worker);
            Save();
            return true;
        }
    }

    public bool Fail(string id, string worker, string error, bool retryable)
    {
        lock (_gate)
        {
            var job = OwnedRunning(id, worker);
            if (job == null) return false;
            FreeWorker(worker);
            if (retryable && job.Attempts < MaxAttempts)
            {
                job.Status = JobStatus.Queued;
                job.Worker = null;
                job.Step = null;
                AddEvent(job, $"Attempt {job.Attempts} failed: {error}. Re-queued.");
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.Error = error;
                AddEvent(job, "Failed: " + error);
            }
            Save();
            return true;
        }
    }

    /// <summary>Job Running mà quá hạn không có nhịp tim: trả về hàng đợi hoặc đánh dấu lỗi.</summary>
    public int ReapStale()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var count = 0;
            foreach (var job in _jobs.Where(j => j.Status == JobStatus.Running && now - (j.LastHeartbeat ?? j.StartedAt ?? j.CreatedAt) > HeartbeatTimeout))
            {
                count++;
                if (job.Worker != null) FreeWorker(job.Worker);
                if (job.Attempts < MaxAttempts)
                {
                    job.Status = JobStatus.Queued;
                    AddEvent(job, $"Worker '{job.Worker}' stopped responding. Re-queued.");
                    job.Worker = null;
                    job.Step = null;
                }
                else
                {
                    job.Status = JobStatus.Failed;
                    job.FinishedAt = now;
                    job.Error = $"Worker '{job.Worker}' stopped responding too many times.";
                    AddEvent(job, job.Error);
                }
            }
            if (count > 0) Save();
            return count;
        }
    }

    // ---------------------------------------------------------------- nội bộ

    private Job? OwnedRunning(string id, string worker)
    {
        var job = _jobs.FirstOrDefault(j => j.Id == id);
        return job is { Status: JobStatus.Running } && string.Equals(job.Worker, worker, StringComparison.OrdinalIgnoreCase) ? job : null;
    }

    private WorkerInfo TouchWorker(string name)
    {
        if (!_workers.TryGetValue(name, out var w)) _workers[name] = w = new WorkerInfo { Name = name };
        w.LastSeen = DateTimeOffset.UtcNow;
        return w;
    }

    private void FreeWorker(string name)
    {
        if (_workers.TryGetValue(name, out var w)) w.Busy = false;
    }

    private static void AddEvent(Job job, string message) =>
        job.Events.Add(new JobEvent { Time = DateTimeOffset.UtcNow, Message = message });

    private static Job Clone(Job j) =>
        JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(j, Json), Json)!;

    private void Load()
    {
        try
        {
            if (!File.Exists(_snapshotPath)) return;
            var jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(_snapshotPath), Json);
            if (jobs != null) _jobs.AddRange(jobs);
        }
        catch (Exception ex)
        {
            // File hỏng: giữ bản hỏng để xem lại, chạy tiếp với kho rỗng thay vì không khởi động được.
            Console.Error.WriteLine("Could not read jobs.json: " + ex.Message);
            try { File.Move(_snapshotPath, _snapshotPath + ".corrupt-" + DateTime.UtcNow.Ticks); } catch { /* bỏ qua */ }
        }
    }

    /// <summary>Gọi trong lock. Ghi file tạm rồi đổi tên để không hỏng file nếu bị ngắt giữa chừng.</summary>
    private void Save()
    {
        try
        {
            var tmp = _snapshotPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_jobs, Json));
            File.Move(tmp, _snapshotPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not write jobs.json: " + ex.Message);
        }
    }
}

public sealed class StaleJobReaper(JobStore store, ILogger<StaleJobReaper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var n = store.ReapStale();
            if (n > 0) log.LogWarning("Reaped {Count} stale job(s)", n);
        }
    }
}
