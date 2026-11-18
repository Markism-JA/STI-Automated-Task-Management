namespace ScholarFlow.Core.Services;

/// <summary>
/// Watches a Basin directory for external edits to Markdown task files and ongoing/completed folders,
/// debouncing rapid filesystem bursts into a single change notification.
/// </summary>
public sealed class BasinWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private readonly object _gate = new();
    private bool _disposed;

    public event EventHandler? BasinChanged;

    public BasinWatcher(string basinRoot, int debounceMs = 150)
    {
        if (!Directory.Exists(basinRoot))
            throw new DirectoryNotFoundException($"Basin directory does not exist: {basinRoot}");

        _debounceTimer = new Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(basinRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += (_, e) => ScheduleIfRelevant(e.FullPath, debounceMs);
        _watcher.Created += (_, e) => ScheduleIfRelevant(e.FullPath, debounceMs);
        _watcher.Deleted += (_, e) => ScheduleIfRelevant(e.FullPath, debounceMs);
        _watcher.Renamed += (_, e) => ScheduleIfRelevant(e.FullPath, debounceMs);
    }

    private void ScheduleIfRelevant(string fullPath, int debounceMs)
    {
        if (fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_gate)
        {
            if (!_disposed)
                _debounceTimer.Change(debounceMs, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed()
    {
        if (!_disposed)
            BasinChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }
}
