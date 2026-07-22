namespace MyPowerTools.Runtime;

/// <summary>
/// Watches development tool directories (tool.json folders and *.mpt.json quick panels)
/// and triggers a debounced tool catalog refresh when files appear, change, or disappear.
/// Saves in editors fire multiple filesystem events; the debounce coalesces them into one
/// refresh, and a gate prevents overlapping refreshes. Watcher failures never kill the
/// watcher — the manual Refresh path remains as a fallback.
/// </summary>
public sealed class DevelopmentToolCatalogWatcher : IAsyncDisposable
{
    private readonly IReadOnlyList<string> _roots;
    private readonly Func<CancellationToken, Task> _refreshAsync;
    private readonly Action<string> _log;
    private readonly TimeSpan _debounce;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _debounceCts;
    private int _disposed;

    public DevelopmentToolCatalogWatcher(
        IReadOnlyList<string> roots,
        Func<CancellationToken, Task> refreshAsync,
        Action<string>? log = null,
        TimeSpan? debounce = null)
    {
        _roots = roots;
        _refreshAsync = refreshAsync;
        _log = log ?? Console.WriteLine;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(400);
    }

    public void Start()
    {
        foreach (var root in _roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    // tool.json lives in subdirectories; *.mpt.json directly under the root.
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                };
                watcher.Filters.Add("*.json");
                watcher.Created += OnFileSystemChanged;
                watcher.Changed += OnFileSystemChanged;
                watcher.Deleted += OnFileSystemChanged;
                watcher.Renamed += OnFileSystemChanged;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception exception)
            {
                _log($"Tool watcher could not observe {root}: {exception.Message}");
            }
        }
    }

    public int WatchedRootCount => _watchers.Count;

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _log($"Tool watcher error: {e.GetException().Message}");
        if (Volatile.Read(ref _disposed) != 0 || sender is not FileSystemWatcher watcher)
        {
            return;
        }

        // Re-arm the watcher after an internal buffer error.
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception)
        {
            _log($"Tool watcher could not re-arm: {exception.Message}");
        }
    }

    private void ScheduleRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CancellationToken token;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, token);
                await RefreshGuardedAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer filesystem event within the debounce window.
            }
            catch (Exception exception)
            {
                _log($"Tool catalog refresh failed: {exception.Message}");
            }
        }, CancellationToken.None);
    }

    private async Task RefreshGuardedAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            // A refresh is already running; this change is picked up by the next event.
            return;
        }

        try
        {
            await _refreshAsync(cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        // Let an in-flight refresh finish before tearing down the gate.
        await _refreshGate.WaitAsync();
        _refreshGate.Dispose();
    }
}
