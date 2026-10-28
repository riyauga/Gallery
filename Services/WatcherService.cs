namespace Gallery.Services;

/// <summary>
/// 文件监听，移植 galleryold <c>app/ui/watcher.py</c>：
/// 递归监听每个启用根目录；忽略 .tmp/.crdownload/.part；
/// 事件归并为 (root_id, dir_rel)，1200ms 防抖后每个受影响根只扫描一次。
/// </summary>
public sealed class WatcherService : IDisposable
{
    private const int DebounceMs = 1200;
    private static readonly string[] IgnoreSuffixes = [".tmp", ".crdownload", ".part"];

    private readonly StoreService _store;
    private readonly ScannerService _scanner;
    private readonly AppSettings _settings;
    private readonly Dictionary<int, FileSystemWatcher> _watchers = [];
    private readonly System.Threading.Timer _timer;
    private readonly System.Threading.Timer _reconnectTimer;
    private int _refreshing;
    private readonly object _gate = new();
    private readonly HashSet<(int RootId, string DirRel)> _pending = [];
    private bool _flushing;
    private volatile bool _disposed;

    public event Action<IReadOnlyCollection<(int RootId, string DirRel)>>? Changed;
    public event Action<string>? Failed;

    public WatcherService(StoreService store, ScannerService scanner, AppSettings settings)
    {
        _store = store;
        _scanner = scanner;
        _settings = settings;
        _timer = new System.Threading.Timer(Flush, null, Timeout.Infinite, Timeout.Infinite);
        _reconnectTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
            try { if (!_disposed) RefreshRoots(scanNew: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or Microsoft.Data.Sqlite.SqliteException) { Failed?.Invoke(ex.Message); }
            finally { Volatile.Write(ref _refreshing, 0); }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        RefreshRoots(scanNew: false);
        lock (_gate)
            if (!_disposed) _reconnectTimer.Change(3000, 3000);
    }

    private void RefreshRoots(bool scanNew)
    {
        var roots = _settings.watch_filesystem
            ? _store.Roots().Where(root => root.Enabled && Directory.Exists(root.Path)).ToArray() : [];
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var id in _watchers.Keys.Except(roots.Select(root => root.Id)).ToArray())
            {
                _watchers[id].Dispose();
                _watchers.Remove(id);
            }
            foreach (var root in roots)
            {
                if (_watchers.ContainsKey(root.Id)) continue;
                var watcher = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => OnEvent(root.Id, root.Path, e.FullPath, isDirectory: null);
                watcher.Deleted += (_, e) => OnEvent(root.Id, root.Path, e.FullPath, isDirectory: null);
                watcher.Renamed += (_, e) =>
                {
                    OnEvent(root.Id, root.Path, e.OldFullPath, isDirectory: null);
                    OnEvent(root.Id, root.Path, e.FullPath, isDirectory: null);
                };
                watcher.Changed += (_, e) =>
                {
                    try
                    {
                        if (Directory.Exists(e.FullPath)) return; // modified 仅文件
                        OnEvent(root.Id, root.Path, e.FullPath, isDirectory: false);
                    }
                    catch (IOException) { }
                };
                watcher.Error += (_, _) =>
                {
                    lock (_gate)
                    {
                        if (_disposed) return;
                        if (_watchers.Remove(root.Id, out var broken)) broken.Dispose();
                    }
                    // 设备断开或监听溢出后由重连计时器重新建监听，并补扫整根。
                };
                _watchers[root.Id] = watcher;
                watcher.EnableRaisingEvents = true;
                if (scanNew)
                {
                    _pending.Add((root.Id, ""));
                    _timer.Change(DebounceMs, Timeout.Infinite);
                }
            }
        }
    }

    private void OnEvent(int rootId, string rootPath, string fullPath, bool? isDirectory)
    {
        var name = Path.GetFileName(fullPath);
        if (IgnoreSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return;

        string? dir = null;
        try
        {
            if (isDirectory is not false && Directory.Exists(fullPath))
                dir = fullPath; // 目录事件从该目录本身开始重扫
            else
                dir = Path.GetDirectoryName(fullPath);
        }
        catch (IOException) { return; }
        if (dir is null) return;

        string dirRel;
        try
        {
            dirRel = Path.GetRelativePath(rootPath, dir).Replace('\\', '/');
            if (dirRel == ".") dirRel = "";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return; }

        Queue(rootId, dirRel);
    }

    private void Queue(int rootId, string dirRel)
    {
        lock (_gate)
        {
            if (_disposed || !_watchers.ContainsKey(rootId)) return;
            _pending.Add((rootId, dirRel));
            _timer.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private void Flush(object? state)
    {
        (int, string)[] batch;
        lock (_gate)
        {
            if (_disposed || _flushing || _pending.Count == 0) return;
            _flushing = true;
            batch = [.. _pending];
            _pending.Clear();
        }
        try
        {
            foreach (var rootId in batch.Select(pair => pair.Item1).Distinct())
            {
                try
                {
                    if (_disposed) break;
                    if (!_store.Roots().Any(root => root.Id == rootId && root.Enabled)) continue;
                    var result = _scanner.ScanRoot(rootId, cancel: () => _disposed);
                    if (result.Errors.Count > 0) Failed?.Invoke(result.Errors[0]);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException
                    or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                { Failed?.Invoke(ex.Message); }
            }
            Changed?.Invoke(batch);
        }
        finally
        {
            lock (_gate)
            {
                _flushing = false;
                if (!_disposed && _pending.Count > 0) _timer.Change(DebounceMs, Timeout.Infinite);
            }
        }
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
        }
        await _reconnectTimer.DisposeAsync();
        await _timer.DisposeAsync();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
            _reconnectTimer.Dispose();
            _timer.Dispose();
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
