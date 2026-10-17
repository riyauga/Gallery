namespace Gallery.Services;

/// <summary>
/// 文件监听，移植 galleryold <c>app/ui/watcher.py</c>：
/// 递归监听每个启用根目录；忽略 .tmp/.crdownload/.part；
/// 事件归并为 (root_id, dir_rel)，1200ms 防抖后对受影响目录重扫。
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
    private readonly object _gate = new();
    private readonly HashSet<(int RootId, string DirRel)> _pending = [];

    public event Action<IReadOnlyCollection<(int RootId, string DirRel)>>? Changed;

    public WatcherService(StoreService store, ScannerService scanner, AppSettings settings)
    {
        _store = store;
        _scanner = scanner;
        _settings = settings;
        _timer = new System.Threading.Timer(Flush, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (!_settings.watch_filesystem) return;
        foreach (var root in _store.Roots())
        {
            if (!root.Enabled || !Directory.Exists(root.Path)) continue;
            if (_watchers.ContainsKey(root.Id)) continue;
            var watcher = new FileSystemWatcher(root.Path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, e) => OnEvent(root.Id, e.FullPath, isDirectory: false);
            watcher.Deleted += (_, e) => OnEvent(root.Id, e.FullPath, isDirectory: null);
            watcher.Renamed += (_, e) =>
            {
                OnEvent(root.Id, e.OldFullPath, isDirectory: null);
                OnEvent(root.Id, e.FullPath, isDirectory: null);
            };
            watcher.Changed += (_, e) =>
            {
                try
                {
                    if (Directory.Exists(e.FullPath)) return; // modified 仅文件
                    OnEvent(root.Id, e.FullPath, isDirectory: false);
                }
                catch (IOException) { }
            };
            watcher.Error += (_, _) => { /* 单次溢出可容忍，下次扫描兜底 */ };
            _watchers[root.Id] = watcher;
        }
    }

    private void OnEvent(int rootId, string fullPath, bool? isDirectory)
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

        var rootPath = _store.RootPath(rootId);
        if (rootPath is null) return;

        string dirRel;
        try
        {
            dirRel = Path.GetRelativePath(rootPath, dir).Replace('\\', '/');
            if (dirRel == ".") dirRel = "";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return; }

        lock (_gate)
        {
            _pending.Add((rootId, dirRel));
            _timer.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private void Flush(object? state)
    {
        (int, string)[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }
        foreach (var (rootId, dirRel) in batch)
        {
            try { _scanner.ScanRoot(rootId); } // 简化：整根重扫，幂等且成本低
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }
        Changed?.Invoke(batch);
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
