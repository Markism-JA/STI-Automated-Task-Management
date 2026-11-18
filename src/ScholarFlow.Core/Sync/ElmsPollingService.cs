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

    public async Task<IReadOnlyList<AppNotification>> TriggerFastPollAsync(string basinRoot, CancellationToken ct = default)
    {
        try
        {
            var notifs = await _syncService.FastPollAssignmentsAsync(basinRoot, ct);
            foreach (var n in notifs)
                NotificationEmitted?.Invoke(this, n);
            return notifs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Fast poll error");
            var err = new AppNotification(NotificationKind.Error, "Fast Poll Failed", ex.Message);
            NotificationEmitted?.Invoke(this, err);
            return [err];
        }
    }

    public async Task<BasinSyncReport> TriggerFullSyncAsync(string basinRoot, bool force = false, CancellationToken ct = default)
    {
        var report = await _syncService.SyncAllAsync(basinRoot, forceRedownload: force, ct: ct);
        foreach (var n in report.Notifications)
            NotificationEmitted?.Invoke(this, n);
        return report;
    }

    private async Task RunLoopAsync(string basinRoot, TimeSpan fastInterval, TimeSpan fullInterval, CancellationToken ct)
    {
        var lastFullSync = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(fastInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastFullSync >= fullInterval)
                {
                    await TriggerFullSyncAsync(basinRoot, force: false, ct);
                    lastFullSync = DateTimeOffset.UtcNow;
                }
                else
                {
                    await TriggerFastPollAsync(basinRoot, ct);
                }

                if (!await timer.WaitForNextTickAsync(ct))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled error in polling loop");
                NotificationEmitted?.Invoke(this, new AppNotification(NotificationKind.Error, "Sync Loop Error", ex.Message));
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
