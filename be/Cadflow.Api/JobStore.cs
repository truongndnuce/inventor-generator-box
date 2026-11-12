using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Cadflow.Api;

/// <summary>
/// Job store + queue on PostgreSQL (tables jobs, job_artifacts, job_events). Every operation is one short transaction;
/// workers take jobs with FOR UPDATE SKIP LOCKED, so several backend instances sharing one DB never hand out the same job twice.
/// Worker state (heartbeat, Busy) lives in memory only because it refreshes itself with every heartbeat.
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

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS jobs (
            id text PRIMARY KEY,
            product_code text NOT NULL,
            product_version int NOT NULL DEFAULT 1,
            status text NOT NULL,
            step text,
            error text,
            requested_by text NOT NULL DEFAULT '',
            created_at timestamptz NOT NULL,
            started_at timestamptz,
            finished_at timestamptz,
            last_heartbeat timestamptz,
            worker text,
            attempts int NOT NULL DEFAULT 0,
            inputs jsonb NOT NULL DEFAULT '{}',
            text_inputs jsonb NOT NULL DEFAULT '{}',
            summary jsonb NOT NULL DEFAULT '{}',
            script_name text,
            script_sha256 text,
            script_source text,
            artifacts_purged_at timestamptz
        );
        ALTER TABLE jobs ADD COLUMN IF NOT EXISTS kind text NOT NULL DEFAULT 'generate';
        ALTER TABLE jobs ADD COLUMN IF NOT EXISTS queue text NOT NULL DEFAULT 'build';
        ALTER TABLE jobs ADD COLUMN IF NOT EXISTS priority int NOT NULL DEFAULT 20;
        ALTER TABLE jobs ADD COLUMN IF NOT EXISTS source_job_id text;
        DROP INDEX IF EXISTS ix_jobs_status_created;
        CREATE INDEX IF NOT EXISTS ix_jobs_queue ON jobs (status, queue, priority, created_at);
        CREATE INDEX IF NOT EXISTS ix_jobs_requested_by ON jobs (requested_by);
        CREATE INDEX IF NOT EXISTS ix_jobs_product ON jobs (product_code);
        CREATE TABLE IF NOT EXISTS job_artifacts (
            job_id text NOT NULL REFERENCES jobs (id) ON DELETE CASCADE,
            name text NOT NULL,
            kind text NOT NULL,
            size bigint NOT NULL,
            r2_key text,
            PRIMARY KEY (job_id, name)
        );
        CREATE TABLE IF NOT EXISTS job_events (
            id bigserial PRIMARY KEY,
            job_id text NOT NULL REFERENCES jobs (id) ON DELETE CASCADE,
            time timestamptz NOT NULL,
            message text NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_job_events_job ON job_events (job_id, time);
        """;

    private const string JobCols = "id, product_code, product_version, status, step, error, requested_by, created_at, started_at, finished_at, " +
                                   "last_heartbeat, worker, attempts, inputs, text_inputs, summary, script_name, script_sha256, script_source, artifacts_purged_at, " +
                                   "kind, queue, priority, source_job_id";

    private readonly NpgsqlDataSource _db;
    private readonly object _wgate = new();
    private readonly Dictionary<string, WorkerInfo> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDir;

    public JobStore(string dataDir, string connectionString)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(ArtifactsRoot);
        _db = NpgsqlDataSource.Create(connectionString);
        Migrate();
        ImportLegacySnapshot();
    }

    public string ArtifactsRoot => Path.Combine(_dataDir, "artifacts");

    public string ArtifactDir(string jobId) => Path.Combine(ArtifactsRoot, jobId);

    // ---------------------------------------------------------------- queries

    public IReadOnlyList<Job> List()
    {
        using var conn = _db.OpenConnection();
        return LoadJobs(conn, null, null, "ORDER BY created_at DESC");
    }

    public Job? Get(string id)
    {
        using var conn = _db.OpenConnection();
        return LoadJobs(conn, "WHERE id = @id", [("id", id)], "").FirstOrDefault();
    }

    public sealed record QueueStat(string Queue, int Queued, int Running, double? OldestWaitSeconds, int OnlineWorkers);

    /// <summary>Depth of every queue (both queues are always listed) and how many online workers will take from it.</summary>
    public IReadOnlyList<QueueStat> QueueStats()
    {
        var found = new Dictionary<string, (int Queued, int Running, DateTimeOffset? Oldest)>();
        using (var conn = _db.OpenConnection())
        using (var cmd = Command(conn, null, """
            SELECT queue, count(*) FILTER (WHERE status = 'queued'), count(*) FILTER (WHERE status = 'running'),
                   min(created_at) FILTER (WHERE status = 'queued')
            FROM jobs WHERE status IN ('queued', 'running') GROUP BY queue
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                found[r.GetString(0)] = ((int)r.GetInt64(1), (int)r.GetInt64(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3));

        var online = Workers().Where(w => w.Online).ToList();
        var now = DateTimeOffset.UtcNow;
        return JobQueues.All.Select(q =>
        {
            var (queued, running, oldest) = found.TryGetValue(q, out var v) ? v : (0, 0, null);
            return new QueueStat(q, queued, running, oldest is { } o ? Math.Round((now - o).TotalSeconds, 1) : null,
                online.Count(w => w.Queues == null || w.Queues.Contains(q)));
        }).ToList();
    }

    /// <summary>How many queued jobs of the same queue are handed out before this one (null if the job is not queued).</summary>
    public int? QueuePosition(string id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = Command(conn, null, """
            SELECT (SELECT count(*) FROM jobs o WHERE o.status = 'queued' AND o.queue = j.queue
                    AND (o.priority < j.priority OR (o.priority = j.priority AND o.created_at < j.created_at)))
            FROM jobs j WHERE j.id = @id AND j.status = 'queued'
            """, ("id", id));
        return cmd.ExecuteScalar() is long n ? (int)n : null;
    }

    public IReadOnlyList<WorkerInfo> Workers()
    {
        lock (_wgate)
        {
            var now = DateTimeOffset.UtcNow;
            return _workers.Values
                .Select(w => new WorkerInfo
                {
                    Name = w.Name, Queues = w.Queues, InventorVersion = w.InventorVersion, AddinVersion = w.AddinVersion,
                    Busy = w.Busy, LastSeen = w.LastSeen, Online = now - w.LastSeen < WorkerOnlineWindow,
                })
                .OrderBy(w => w.Name).ToList();
        }
    }

    public bool AnyWorkerOnline()
    {
        lock (_wgate) return _workers.Values.Any(w => DateTimeOffset.UtcNow - w.LastSeen < WorkerOnlineWindow);
    }

    // ---------------------------------------------------------------- requesters

    public Job Create(string productCode, Dictionary<string, double> inputs, string requestedBy, string? scriptName = null, string? scriptSha256 = null, string? scriptSource = null, Dictionary<string, string>? textInputs = null,
        string kind = JobKinds.Generate, string queue = JobQueues.Build, int priority = QueuePolicy.DefaultPriority, string? sourceJobId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new Job
        {
            Id = $"AJ-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}",
            Kind = kind,
            Queue = queue,
            Priority = priority,
            SourceJobId = sourceJobId,
            ProductCode = productCode,
            Inputs = inputs,
            TextInputs = textInputs ?? new(),
            RequestedBy = requestedBy,
            ScriptName = scriptName,
            ScriptSha256 = scriptSha256,
            ScriptSource = scriptSource,
            Status = JobStatus.Queued,
            CreatedAt = now,
        };
        job.Events.Add(new JobEvent { Time = now, Message = "Order received. Waiting for an Inventor worker to pick up the job..." });
        Tx((c, t) => { Insert(c, t, job); return true; });
        return job;
    }

    public bool Cancel(string id) => Tx((c, t) =>
    {
        var n = Exec(c, t, "UPDATE jobs SET status = 'cancelled', finished_at = @now WHERE id = @id AND status IN ('queued', 'running')",
            ("id", id), ("now", DateTimeOffset.UtcNow));
        if (n == 0) return false;
        AddEvent(c, t, id, "Cancelled by the user.");
        return true;
    });

    // ---------------------------------------------------------------- worker

    public void Heartbeat(WorkerHello hello)
    {
        if (string.IsNullOrWhiteSpace(hello.Worker)) return;
        lock (_wgate)
        {
            var w = TouchWorker(hello.Worker!);
            w.InventorVersion = hello.InventorVersion ?? w.InventorVersion;
            w.AddinVersion = hello.AddinVersion ?? w.AddinVersion;
            w.Busy = hello.Busy;
        }
        // A worker heartbeat also extends the jobs it is running.
        using var conn = _db.OpenConnection();
        Exec(conn, null, "UPDATE jobs SET last_heartbeat = @now WHERE status = 'running' AND worker = @w",
            ("w", hello.Worker!), ("now", DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Hands the next Queued job to a worker. <paramref name="queues"/> is the worker's own order of preference (SDS: node 1-2 take Build first,
    /// node 3-5 take Filter first): the first queue that has a matching job wins, and inside a queue the lowest <c>priority</c> number goes first,
    /// then the oldest. Null or empty = every queue, sorted by priority (an old worker that does not send queues).
    /// FOR UPDATE SKIP LOCKED: each job goes to exactly one worker, even when several backends run in parallel.
    /// </summary>
    public WorkItem? Claim(string worker, string[]? products = null, string[]? queues = null)
    {
        var order = queues?.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim().ToLowerInvariant()).Distinct().ToArray();
        lock (_wgate) TouchWorker(worker).Queues = order is { Length: > 0 } ? order : null;
        var item = Tx((c, t) =>
        {
            if (order == null || order.Length == 0) return ClaimFrom(c, t, worker, products, null);
            foreach (var q in order)
                if (ClaimFrom(c, t, worker, products, q) is { } w) return w;
            return null;
        });
        if (item != null) SetBusy(worker, true);
        return item;
    }

    private static WorkItem? ClaimFrom(NpgsqlConnection c, NpgsqlTransaction t, string worker, string[]? products, string? queue)
    {
        var all = products == null || products.Length == 0;
        using var cmd = Command(c, t, """
            UPDATE jobs SET status = 'running', worker = @w, started_at = COALESCE(started_at, @now), last_heartbeat = @now,
                            attempts = attempts + 1, step = 'Starting'
            WHERE id = (SELECT id FROM jobs
                        WHERE status = 'queued' AND (@anyq OR queue = @q) AND (@all OR lower(product_code) = ANY(@p))
                        ORDER BY priority, created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id, product_code, product_version, inputs, text_inputs, attempts, kind
            """, ("w", worker), ("now", DateTimeOffset.UtcNow), ("all", all), ("anyq", queue == null), ("q", queue ?? ""));
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (products ?? []).Select(p => p.ToLowerInvariant()).ToArray() });
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var id = r.GetString(0);
        var inputs = JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(3), Json) ?? new();
        var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4), Json) ?? new();
        var attempt = r.GetInt32(5);
        var w = new WorkItem(id, r.GetString(1), r.GetInt32(2), inputs, TextInputs: texts.Count == 0 ? null : texts, Kind: r.GetString(6));
        r.Close();
        AddEvent(c, t, id, $"Picked up by worker '{worker}' (attempt {attempt}).");
        return w;
    }

    public bool Progress(string id, string worker, string? step, string? message) => Tx((c, t) =>
    {
        if (!LockOwned(c, t, id, worker, out _)) return false;
        Exec(c, t, "UPDATE jobs SET last_heartbeat = @now, step = COALESCE(@step, step) WHERE id = @id",
            ("id", id), ("now", DateTimeOffset.UtcNow), ("step", string.IsNullOrWhiteSpace(step) ? null : step));
        if (!string.IsNullOrWhiteSpace(message ?? step)) AddEvent(c, t, id, message ?? step!);
        return true;
    });

    public bool AddArtifact(string id, string worker, ArtifactInfo artifact) => Tx((c, t) =>
    {
        if (!LockOwned(c, t, id, worker, out _)) return false;
        Exec(c, t, "UPDATE jobs SET last_heartbeat = @now WHERE id = @id", ("id", id), ("now", DateTimeOffset.UtcNow));
        Exec(c, t, """
            INSERT INTO job_artifacts (job_id, name, kind, size, r2_key) VALUES (@id, @name, @kind, @size, @key)
            ON CONFLICT (job_id, name) DO UPDATE SET kind = EXCLUDED.kind, size = EXCLUDED.size, r2_key = EXCLUDED.r2_key
            """, ("id", id), ("name", artifact.Name), ("kind", artifact.Kind), ("size", artifact.Size), ("key", artifact.Key));
        return true;
    });

    public bool Complete(string id, string worker, Dictionary<string, double>? summary)
    {
        var ok = Tx((c, t) =>
        {
            if (!LockOwned(c, t, id, worker, out _)) return false;
            using var cmd = Command(c, t, "UPDATE jobs SET status = 'succeeded', step = 'Done', finished_at = @now, error = NULL, summary = @summary WHERE id = @id",
                ("id", id), ("now", DateTimeOffset.UtcNow));
            cmd.Parameters.Add(Jsonb("summary", summary ?? new Dictionary<string, double>()));
            cmd.ExecuteNonQuery();
            AddEvent(c, t, id, "Build finished.");
            return true;
        });
        if (ok) SetBusy(worker, false);
        return ok;
    }

    public bool Fail(string id, string worker, string error, bool retryable)
    {
        var ok = Tx((c, t) =>
        {
            if (!LockOwned(c, t, id, worker, out var attempts)) return false;
            if (retryable && attempts < MaxAttempts)
            {
                Exec(c, t, "UPDATE jobs SET status = 'queued', worker = NULL, step = NULL WHERE id = @id", ("id", id));
                AddEvent(c, t, id, $"Attempt {attempts} failed: {error}. Re-queued.");
            }
            else
            {
                Exec(c, t, "UPDATE jobs SET status = 'failed', finished_at = @now, error = @error WHERE id = @id",
                    ("id", id), ("now", DateTimeOffset.UtcNow), ("error", error));
                AddEvent(c, t, id, "Failed: " + error);
            }
            return true;
        });
        if (ok) SetBusy(worker, false);
        return ok;
    }

    /// <summary>Running jobs that passed their deadline without a heartbeat: returns them to the queue or marks them failed.</summary>
    public int ReapStale()
    {
        var freed = new List<string>();
        var count = Tx((c, t) =>
        {
            var stale = new List<(string Id, string? Worker, int Attempts)>();
            using (var cmd = Command(c, t, """
                SELECT id, worker, attempts FROM jobs
                WHERE status = 'running' AND COALESCE(last_heartbeat, started_at, created_at) < @cutoff
                FOR UPDATE SKIP LOCKED
                """, ("cutoff", DateTimeOffset.UtcNow - HeartbeatTimeout)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) stale.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2)));

            foreach (var (id, worker, attempts) in stale)
            {
                if (worker != null) freed.Add(worker);
                if (attempts < MaxAttempts)
                {
                    Exec(c, t, "UPDATE jobs SET status = 'queued', worker = NULL, step = NULL WHERE id = @id", ("id", id));
                    AddEvent(c, t, id, $"Worker '{worker}' stopped responding. Re-queued.");
                }
                else
                {
                    var msg = $"Worker '{worker}' stopped responding too many times.";
                    Exec(c, t, "UPDATE jobs SET status = 'failed', finished_at = @now, error = @error WHERE id = @id",
                        ("id", id), ("now", DateTimeOffset.UtcNow), ("error", msg));
                    AddEvent(c, t, id, msg);
                }
            }
            return stale.Count;
        });
        foreach (var w in freed) SetBusy(w, false);
        return count;
    }

    /// <summary>Jobs finished before <paramref name="cutoff"/> that still have result files.</summary>
    public IReadOnlyList<Job> ExpiredWithArtifacts(DateTimeOffset cutoff)
    {
        using var conn = _db.OpenConnection();
        return LoadJobs(conn, """
            WHERE status IN ('succeeded', 'failed', 'cancelled') AND COALESCE(finished_at, created_at) < @cutoff
              AND EXISTS (SELECT 1 FROM job_artifacts a WHERE a.job_id = jobs.id)
            """, [("cutoff", cutoff.ToUniversalTime())], "");
    }

    /// <summary>Drops the file list of a job after the real files were deleted.</summary>
    public void MarkArtifactsPurged(string id, int days) => Tx((c, t) =>
    {
        if (Exec(c, t, "UPDATE jobs SET artifacts_purged_at = @now WHERE id = @id", ("id", id), ("now", DateTimeOffset.UtcNow)) == 0) return false;
        Exec(c, t, "DELETE FROM job_artifacts WHERE job_id = @id", ("id", id));
        AddEvent(c, t, id, $"Result files deleted after {days} days (retention policy).");
        return true;
    });

    // ---------------------------------------------------------------- internal: in-memory workers

    private WorkerInfo TouchWorker(string name)
    {
        if (!_workers.TryGetValue(name, out var w)) _workers[name] = w = new WorkerInfo { Name = name };
        w.LastSeen = DateTimeOffset.UtcNow;
        return w;
    }

    private void SetBusy(string name, bool busy)
    {
        lock (_wgate)
            if (_workers.TryGetValue(name, out var w)) w.Busy = busy;
    }

    // ---------------------------------------------------------------- internal: SQL

    /// <summary>Locks the job row if it is Running and belongs to this worker.</summary>
    private static bool LockOwned(NpgsqlConnection c, NpgsqlTransaction t, string id, string worker, out int attempts)
    {
        using var cmd = Command(c, t, "SELECT attempts FROM jobs WHERE id = @id AND status = 'running' AND lower(worker) = lower(@w) FOR UPDATE", ("id", id), ("w", worker));
        var o = cmd.ExecuteScalar();
        attempts = o is int n ? n : 0;
        return o != null;
    }

    private T Tx<T>(Func<NpgsqlConnection, NpgsqlTransaction, T> work)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        var result = work(conn, tx);
        tx.Commit();
        return result;
    }

    private static NpgsqlCommand Command(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = new NpgsqlCommand(sql, c, t);
        foreach (var (name, value) in ps)
            cmd.Parameters.Add(new NpgsqlParameter(name, value switch
            {
                null => DBNull.Value,
                DateTimeOffset d => d.ToUniversalTime(),
                _ => value,
            }));
        return cmd;
    }

    private static int Exec(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Command(c, t, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    private static NpgsqlParameter Jsonb(string name, object value) =>
        new(name, NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(value, Json) };

    private static void AddEvent(NpgsqlConnection c, NpgsqlTransaction t, string jobId, string message) =>
        Exec(c, t, "INSERT INTO job_events (job_id, time, message) VALUES (@id, @time, @msg)",
            ("id", jobId), ("time", DateTimeOffset.UtcNow), ("msg", message));

    private static void Insert(NpgsqlConnection c, NpgsqlTransaction t, Job j)
    {
        using (var cmd = Command(c, t, $"""
            INSERT INTO jobs ({JobCols}) VALUES
            (@id, @product, @version, @status, @step, @error, @by, @created, @started, @finished, @hb, @worker, @attempts,
             @inputs, @texts, @summary, @sname, @ssha, @ssrc, @purged, @kind, @queue, @prio, @src)
            ON CONFLICT (id) DO NOTHING
            """,
            ("kind", j.Kind), ("queue", j.Queue), ("prio", j.Priority), ("src", j.SourceJobId),
            ("id", j.Id), ("product", j.ProductCode), ("version", j.ProductVersion), ("status", j.Status.ToString().ToLowerInvariant()),
            ("step", j.Step), ("error", j.Error), ("by", j.RequestedBy ?? ""), ("created", j.CreatedAt), ("started", j.StartedAt),
            ("finished", j.FinishedAt), ("hb", j.LastHeartbeat), ("worker", j.Worker), ("attempts", j.Attempts),
            ("sname", j.ScriptName), ("ssha", j.ScriptSha256), ("ssrc", j.ScriptSource), ("purged", j.ArtifactsPurgedAt)))
        {
            cmd.Parameters.Add(Jsonb("inputs", j.Inputs));
            cmd.Parameters.Add(Jsonb("texts", j.TextInputs));
            cmd.Parameters.Add(Jsonb("summary", j.Summary));
            if (cmd.ExecuteNonQuery() == 0) return; // already there (re-import)
        }
        foreach (var a in j.Artifacts)
            Exec(c, t, "INSERT INTO job_artifacts (job_id, name, kind, size, r2_key) VALUES (@id, @name, @kind, @size, @key) ON CONFLICT DO NOTHING",
                ("id", j.Id), ("name", a.Name), ("kind", a.Kind), ("size", a.Size), ("key", a.Key));
        foreach (var e in j.Events)
            Exec(c, t, "INSERT INTO job_events (job_id, time, message) VALUES (@id, @time, @msg)", ("id", j.Id), ("time", e.Time), ("msg", e.Message));
    }

    private static List<Job> LoadJobs(NpgsqlConnection conn, string? where, (string, object?)[]? ps, string order)
    {
        var jobs = new List<Job>();
        using (var cmd = Command(conn, null, $"SELECT {JobCols} FROM jobs {where} {order}", ps ?? []))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) jobs.Add(ReadJob(r));
        if (jobs.Count == 0) return jobs;

        var byId = jobs.ToDictionary(j => j.Id);
        var ids = byId.Keys.ToArray();
        using (var cmd = Command(conn, null, "SELECT job_id, name, kind, size, r2_key FROM job_artifacts WHERE job_id = ANY(@ids) ORDER BY name"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ids });
            using var r = cmd.ExecuteReader();
            while (r.Read())
                byId[r.GetString(0)].Artifacts.Add(new ArtifactInfo { Name = r.GetString(1), Kind = r.GetString(2), Size = r.GetInt64(3), Key = r.IsDBNull(4) ? null : r.GetString(4) });
        }
        using (var cmd = Command(conn, null, "SELECT job_id, time, message FROM job_events WHERE job_id = ANY(@ids) ORDER BY id"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ids });
            using var r = cmd.ExecuteReader();
            while (r.Read())
                byId[r.GetString(0)].Events.Add(new JobEvent { Time = r.GetFieldValue<DateTimeOffset>(1), Message = r.GetString(2) });
        }
        return jobs;
    }

    private static Job ReadJob(NpgsqlDataReader r)
    {
        static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static DateTimeOffset? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
        return new Job
        {
            Id = r.GetString(0),
            ProductCode = r.GetString(1),
            ProductVersion = r.GetInt32(2),
            Status = Enum.Parse<JobStatus>(r.GetString(3), ignoreCase: true),
            Step = S(r, 4),
            Error = S(r, 5),
            RequestedBy = r.GetString(6),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(7),
            StartedAt = D(r, 8),
            FinishedAt = D(r, 9),
            LastHeartbeat = D(r, 10),
            Worker = S(r, 11),
            Attempts = r.GetInt32(12),
            Inputs = JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(13), Json) ?? new(),
            TextInputs = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(14), Json) ?? new(),
            Summary = JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(15), Json) ?? new(),
            ScriptName = S(r, 16),
            ScriptSha256 = S(r, 17),
            ScriptSource = S(r, 18),
            ArtifactsPurgedAt = D(r, 19),
            Kind = r.GetString(20),
            Queue = r.GetString(21),
            Priority = r.GetInt32(22),
            SourceJobId = S(r, 23),
        };
    }

    private void Migrate()
    {
        using var conn = _db.OpenConnection();
        // Advisory lock: two backends starting at the same time do not race to create the tables.
        Exec(conn, null, "SELECT pg_advisory_lock(727001)");
        try { Exec(conn, null, Schema); }
        finally { Exec(conn, null, "SELECT pg_advisory_unlock(727001)"); }
    }

    /// <summary>First run with Postgres: imports jobs from the old jobs.json (if present), then renames the file so it is not imported again.</summary>
    private void ImportLegacySnapshot()
    {
        var path = Path.Combine(_dataDir, "jobs.json");
        if (!File.Exists(path)) return;
        try
        {
            var jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(path), Json) ?? new();
            Tx((c, t) => { foreach (var j in jobs) Insert(c, t, j); return true; });
            File.Move(path, path + ".migrated", overwrite: true);
            Console.WriteLine($"Imported {jobs.Count} job(s) from jobs.json into PostgreSQL (file renamed to jobs.json.migrated).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not import jobs.json: " + ex.Message);
        }
    }
}

/// <summary>
/// Deletes the result files (R2 and disk) of jobs finished more than <c>Retention:Days</c> days ago. Default 0 = keep forever.
/// Only files are deleted: the job record, parameters and script remain, so the Dashboard and "Duplicate order" keep working.
/// </summary>
public sealed class RetentionService(JobStore store, R2Store r2, IConfiguration config, ILogger<RetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var days = config.GetValue<int>("Retention:Days");
        if (days <= 0) return;
        log.LogInformation("Retention: result files are deleted {Days} day(s) after the job finishes.", days);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            foreach (var job in store.ExpiredWithArtifacts(DateTimeOffset.UtcNow.AddDays(-days)))
            {
                try
                {
                    foreach (var a in job.Artifacts.Where(a => a.Key != null && r2.IsConfigured))
                        await r2.DeleteAsync(a.Key!, stoppingToken);
                    var dir = store.ArtifactDir(job.Id);
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    store.MarkArtifactsPurged(job.Id, days);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Retention: could not delete files of job {Id}; will retry next hour.", job.Id);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
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
