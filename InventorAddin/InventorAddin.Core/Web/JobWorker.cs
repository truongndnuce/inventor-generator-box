using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using InventorAddin.Host;
using Inv = Inventor;

namespace InventorAddin.Web;

/// <summary>
/// The loop that takes jobs from the web server (runs in the background, does not block Inventor): ask the server → move the build onto the UI thread
/// of Inventor → upload the results → report completion or an error. It does one job at a time (one Inventor = one job).
/// </summary>
public sealed class JobWorker : IDisposable
{
    private readonly WorkerSettings _settings;
    private readonly ServerClient _client;
    private readonly Func<Func<object?>, Task<object?>> _onUiThread;
    private readonly InventorJobExecutor _executor;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _addinVersion = typeof(JobWorker).Assembly.GetName().Version?.ToString(3) ?? "0";
    private Task? _loop;
    private Task? _heartbeat;
    private volatile bool _busy;
    private string? _inventorVersion;
    private bool _versionRead;
    private string _status = "";

    /// <summary>Raised when the connection state changes (runs on a background thread; the receiver must switch to the UI thread itself).</summary>
    public event Action<string>? StatusChanged;

    /// <param name="products">Product codes this add-in can build (the server only hands out jobs of those codes); null = all.</param>
    public JobWorker(Inv.Application app, WorkerSettings settings, Func<Func<object?>, Task<object?>> onUiThread, string[]? products = null)
    {
        _settings = settings;
        _client = new ServerClient(settings, products);
        _onUiThread = onUiThread;
        _executor = new InventorJobExecutor(app);
    }

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _heartbeat = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll(new[] { _loop ?? Task.CompletedTask, _heartbeat ?? Task.CompletedTask }, TimeSpan.FromSeconds(2)); }
        catch { /* cancelled */ }
        _client.Dispose();
        _cts.Dispose();
    }

    private void SetStatus(string status)
    {
        if (status == _status) return;
        _status = status;
        Log.Info("Web worker: " + status);
        StatusChanged?.Invoke(status);
    }

    // ---------------------------------------------------------------- heartbeat

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Read the version exactly once: asking the UI thread while it is building would leave the heartbeat stuck behind the job.
                if (!_versionRead)
                {
                    _versionRead = true;
                    _inventorVersion = (string?)await _onUiThread(() => _executor.InventorVersion());
                }
                await _client.HeartbeatAsync(_inventorVersion, _addinVersion, _busy, ct);
                if (!_busy) SetStatus("connected");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                SetStatus("offline");
            }
            catch (Exception ex)
            {
                Log.Warn("Heartbeat error: " + ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_settings.HeartbeatSeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ---------------------------------------------------------------- receiving and handling jobs

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var job = await _client.ClaimAsync(ct);
                if (job == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_settings.PollSeconds), ct);
                    continue;
                }
                await ProcessAsync(job, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (HttpRequestException)
            {
                SetStatus("offline");
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(_settings.PollSeconds, 5)), ct); }
                catch (OperationCanceledException) { return; }
            }
            catch (Exception ex)
            {
                Log.Error("Worker loop error", ex);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ProcessAsync(WorkItem job, CancellationToken ct)
    {
        Log.Info($"Job {job.Id} claimed");
        _busy = true;
        SetStatus($"building {job.Id}");

        // Report progress sequentially through one channel, because the build runs on the UI thread and must not wait on the network.
        var steps = Channel.CreateUnbounded<(string Step, string? Message)>();
        var pump = Task.Run(async () =>
        {
            await foreach (var (step, message) in steps.Reader.ReadAllAsync(CancellationToken.None))
            {
                try { await _client.ProgressAsync(job.Id, step, message, ct); }
                catch (Exception ex) { Log.Warn("Progress report failed: " + ex.Message); }
            }
        }, CancellationToken.None);

        var workDir = Path.Combine(_settings.WorkDir, job.Id);
        try
        {
            var result = (JobResult)(await _onUiThread(() =>
                _executor.Run(job, workDir, (step, message) => steps.Writer.TryWrite((step, message)))))!;
            steps.Writer.Complete();
            await pump;

            if (!result.Ok)
            {
                await _client.FailAsync(job.Id, result.Error ?? "Unknown error.", result.Retryable, ct);
                Log.Warn($"Job {job.Id} failed: {result.Error}");
                return;
            }

            await _client.ProgressAsync(job.Id, "Uploading", "Uploading result files.", ct);
            foreach (var f in result.Files)
                await _client.UploadAsync(job.Id, f.Path, f.Kind, ct);

            if (await _client.CompleteAsync(job.Id, result.Summary, ct))
                Log.Info($"Job {job.Id} done");
            else
                Log.Warn($"Job {job.Id}: server no longer accepts results (cancelled or re-queued).");

            TryDeleteDir(workDir);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Job {job.Id} failed in worker", ex);
            steps.Writer.TryComplete();
            try { await _client.FailAsync(job.Id, "Worker error: " + ex.Message, retryable: true, CancellationToken.None); }
            catch (Exception reportEx) { Log.Warn("Could not report failure: " + reportEx.Message); }
        }
        finally
        {
            _busy = false;
            SetStatus("connected");
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) { Log.Warn("Could not clean job directory: " + ex.Message); }
    }
}
