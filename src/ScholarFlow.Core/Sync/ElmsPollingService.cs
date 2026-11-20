using Microsoft.Extensions.Logging;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Sync;

/// <summary>
/// Two-tier background polling service (PeriodicTimer):
/// - Fast Poll: checks assignment lists for new tasks, due-date updates, and newly posted grades.
/// - Full Sync: downloads new handouts/submissions and regenerates stale Master PDFs.
/// </summary>
public sealed class ElmsPollingService : IAsyncDisposable
{
    private readonly ElmsBasinSyncService _syncService;
    private readonly ILogger<ElmsPollingService>? _logger;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event EventHandler<AppNotification>? NotificationEmitted;

    public bool IsRunning => _loopTask is not null && !_loopTask.IsCompleted;
    public bool IsPaused { get; private set; }
    public DateTimeOffset? PausedUntilUtc { get; private set; }

    public void Pause(TimeSpan? duration = null)
    {
        IsPaused = true;
        PausedUntilUtc = duration.HasValue ? DateTimeOffset.UtcNow.Add(duration.Value) : null;
    }

    public void Resume()
    {
        IsPaused = false;
        PausedUntilUtc = null;
    }

    public ElmsPollingService(ElmsBasinSyncService syncService, ILogger<ElmsPollingService>? logger = null)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _logger = logger;
    }

    public void Start(string basinRoot, BasinConfig config)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var fastInterval = TimeSpan.FromMinutes(Math.Max(1, config.FastPollIntervalMinutes));
        var fullInterval = TimeSpan.FromMinutes(Math.Max(5, config.FullSyncIntervalMinutes));

        _loopTask = Task.Run(() => RunLoopAsync(basinRoot, fastInterval, fullInterval, ct), ct);
    }

    public void Stop()
    {
        if (_cts is null)
            return;

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }

    public async Task<IReadOnlyList<AppNotification>> TriggerFastPollAsync(
        string basinRoot,
        CancellationToken ct = default,
        bool allowInteractiveAuth = true)
    {
        try
        {
            var notifs = await _syncService.FastPollAssignmentsAsync(basinRoot, ct, allowInteractiveAuth);
            foreach (var n in notifs)
                NotificationEmitted?.Invoke(this, n);
            return notifs;
        }
        catch (ScholarFlow.Elms.Models.ElmsAuthenticationRequiredException ex)
        {
            _logger?.LogInformation("Skipping background fast poll: {Message}", ex.Message);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Fast poll error");
            var err = new AppNotification(NotificationKind.Error, "Fast Poll Failed", ex.Message);
            NotificationEmitted?.Invoke(this, err);
            return [err];
        }
    }

    public async Task<BasinSyncReport> TriggerFullSyncAsync(
        string basinRoot,
        bool force = false,
        CancellationToken ct = default,
        bool allowInteractiveAuth = true)
    {
        var report = await _syncService.SyncAllAsync(
            basinRoot,
            forceRedownload: force,
            ct: ct,
            allowInteractiveAuth: allowInteractiveAuth);
        foreach (var n in report.Notifications)
            NotificationEmitted?.Invoke(this, n);
        return report;
    }

    private async Task RunLoopAsync(string basinRoot, TimeSpan fastInterval, TimeSpan fullInterval, CancellationToken ct)
    {
        var lastFullSync = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(fastInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for the first interval tick before polling in the background so app startup never spams eLMS
                if (!await timer.WaitForNextTickAsync(ct))
                    break;

                if (IsPaused)
                {
                    if (PausedUntilUtc.HasValue && DateTimeOffset.UtcNow >= PausedUntilUtc.Value)
                        Resume();
                    else
                        continue;
                }

                if (DateTimeOffset.UtcNow - lastFullSync >= fullInterval)
                {
                    await TriggerFullSyncAsync(basinRoot, force: false, ct, allowInteractiveAuth: false);
                    lastFullSync = DateTimeOffset.UtcNow;
                }
                else
                {
                    await TriggerFastPollAsync(basinRoot, ct, allowInteractiveAuth: false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ScholarFlow.Elms.Models.ElmsAuthenticationRequiredException ex)
            {
                _logger?.LogInformation("Background sync deferred until user re-authenticates: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled error in polling loop");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var task = _loopTask;
        Stop();
        if (task is not null)
        {
            try { await task; } catch (OperationCanceledException) { }
        }
    }
}
