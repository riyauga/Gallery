namespace Gallery.Services;

/// <summary>
/// 资料包目录监听：把词库 json 或模型文件夹拷进 <c>archive\</c> 或 <c>models\</c> 后，静置一段时间再通知一次。
/// 拷贝大文件会连续抛很多事件，去抖同时避免读到写了一半的文件。
/// </summary>
public sealed class PackWatcher : IDisposable
{
    private const int QuietMs = 1500;

    private readonly FileSystemWatcher? _catalog;
    private readonly FileSystemWatcher? _models;
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _catalogSlot;
    private CancellationTokenSource? _modelsSlot;

    /// <summary>词库目录静置后触发（后台线程）。</summary>
    public event Action? CatalogChanged;

    /// <summary>模型目录静置后触发（后台线程）。</summary>
    public event Action? ModelsChanged;

    public PackWatcher(string catalogDir, string modelsDir)
    {
        _catalog = TryWatch(catalogDir, "*", includeSubdirectories: true, () => Schedule(ref _catalogSlot, CatalogChanged));
        _models = TryWatch(modelsDir, "*", includeSubdirectories: true, () => Schedule(ref _modelsSlot, ModelsChanged));
    }

    public bool IsWatching => _catalog is not null || _models is not null;

    public string Describe()
    {
        var parts = new List<string>();
        if (_catalog is not null) parts.Add("词库目录");
        if (_models is not null) parts.Add("模型目录");
        return parts.Count == 0 ? "未能监听资料包目录" : "正在监听" + string.Join(" 与 ", parts);
    }

    private static FileSystemWatcher? TryWatch(string dir, string filter, bool includeSubdirectories, Action onQuiet)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var watcher = new FileSystemWatcher(dir, filter)
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 32 * 1024,
            };
            watcher.Changed += (_, _) => onQuiet();
            watcher.Created += (_, _) => onQuiet();
            watcher.Deleted += (_, _) => onQuiet();
            watcher.Renamed += (_, _) => onQuiet();
            watcher.Error += (_, _) => onQuiet();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>同一个通道收到新事件就重置计时，只有真正静下来才通知。</summary>
    private void Schedule(ref CancellationTokenSource? slot, Action? handler)
    {
        if (handler is null || _life.IsCancellationRequested) return;
        var next = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var previous = Interlocked.Exchange(ref slot, next);
        previous?.Cancel();
        previous?.Dispose();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(QuietMs, next.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (next.IsCancellationRequested) return;
            try { handler(); }
            catch (Exception ex) { AppService.DebugLog("pack watcher handler failed: " + ex.Message); }
        });
    }

    public void Dispose()
    {
        _life.Cancel();
        foreach (var watcher in new[] { _catalog, _models })
        {
            if (watcher is null) continue;
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // 退出阶段忽略
            }
        }
        _life.Dispose();
    }
}
