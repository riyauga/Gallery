namespace Gallery.Services;

/// <summary>
/// 应用服务装配（批次 0 简易 DI）。
/// 启动时打开 AppPaths 指定的库，并启动文件监听；支持 --dataroot 沙箱。
/// </summary>
public static class AppService
{
    /// <summary>
    /// 调试开关：命令行加 <c>--nsfw-fake-scorer</c> 时判定用替身打分器（不加载 ONNX）。
    /// 只给自动化验收用，正常启动永远是 false。
    /// </summary>
    public static bool FakeNsfwScorer { get; set; }

    /// <summary>按当前配置造一个判定服务（必要时挂上替身打分器）。</summary>
    public static NsfwService CreateNsfwService()
    {
        var service = new NsfwService(Store!);
        if (!FakeNsfwScorer) return service;

        return new NsfwService(Store!)
        {
            OverrideScorer = (path, _) => Math.Abs(path.GetHashCode()) % 2 == 0
                ? new NsfwProbs(0.05, 0.10, 0.15, 0.70)
                : new NsfwProbs(0.86, 0.08, 0.04, 0.02),
        };
    }

    public static SettingsStore Settings { get; } = new();
    public static StoreService? Store { get; private set; }
    public static TagCatalogService? Catalog { get; private set; }
    public static FolderService? Folders { get; private set; }
    public static ScannerService? Scanner { get; private set; }
    public static WatcherService? Watcher { get; private set; }
    public static Gallery.Services.Bridge.BridgeServer? Bridge { get; private set; }
    public static ArchiveService? Archiver { get; private set; }
    public static string BridgeStatus { get; private set; } = "桥接未启动";

    public static bool DbReady { get; private set; }
    public static bool IsShuttingDown { get; private set; }
    public static string InitError { get; private set; } = "";
    public static int RootCount { get; private set; }
    public static int ImageCount { get; private set; }
    public static int FavoriteCount { get; private set; }

    /// <summary>
    /// 自动化验收用的一行日志：只写 %TEMP%\ptv\gallery-log.txt（托盘/单实例这类看不清的行为靠它断言）。
    /// </summary>
    private static readonly object LogGate = new();

    public static void DebugLog(string line)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ptv");
            lock (LogGate)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "gallery-log.txt"),
                    $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // 记日志失败不该影响运行
        }
    }

    /// <summary>「敏感内容显示方式」这类影响缩略图重建的设置变更，通知图库重刷。</summary>
    public static event Action? DisplayModeChanged;

    public static void NotifyDisplayModeChanged() => DisplayModeChanged?.Invoke();

    public static void Recount()
    {
        if (Store is null) return;
        RootCount = Store.Roots().Count(r => r.Enabled);
        ImageCount = Store.CountImages();
        FavoriteCount = Store.CountFavorites();
    }

    /// <summary>跨线程串行化（批次 0 简化：扫描/监听与 UI 读共用一把锁）。</summary>
    private static readonly object InitLock = new();
    public static object DbLock => Store?.SyncRoot ?? InitLock;

    /// <summary>主窗口句柄（FolderPicker 等需要 InitializeWithWindow）。</summary>
    public static IntPtr MainWindowHandle { get; set; }

    public static void Init()
    {
        lock (InitLock)
        {
            if (DbReady) return;
            InitCore();
        }
    }

    private static void InitCore()
    {
        InitError = "";
        try
        {
            Settings.Load();
            AppPaths.EnsureDirs();
            Store = new StoreService(AppPaths.DbPath);
        Catalog = new TagCatalogService(Store);
            Archiver = new ArchiveService(Store);
            Store.SidecarEnabled = Settings.Settings.sidecar_enabled;
            Store.HideSidecar = Settings.Settings.hide_sidecar;
            // 修历史漂移（nsfw 列与评分表不一致、分辨率标签被旧版「整批替换」吃掉）
            try
            {
                var originsFixed = Store.RestoreArchivedOrigins();
                if (originsFixed > 0) DebugLog($"restored origins={originsFixed}");
                var (nsfwFixed, resolutionFixed) = Store.RepairDerived();
                if (nsfwFixed + resolutionFixed > 0)
                    DebugLog($"repaired nsfw={nsfwFixed} resolution={resolutionFixed}");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                DebugLog("repair failed: " + ex.Message);
            }

            RootCount = Store.Roots().Count(r => r.Enabled);
            ImageCount = Store.CountImages();
            FavoriteCount = Store.CountFavorites();

            var scanner = new ScannerService(Store, Settings.Settings);
            Scanner = scanner;
            Folders = new FolderService(Store, Settings);
            Watcher = new WatcherService(Store, scanner, Settings.Settings);
            Watcher.Start();
            Settings.Changed += ApplyWriteSettings;
            DbReady = true;
            StartCatalogImport();
            StartPackWatch();
            StartBridge();
            StartIngestReconciliation();
        }
        catch (Exception ex) when (ex is IOException
            or Microsoft.Data.Sqlite.SqliteException
            or UnauthorizedAccessException)
        {
            Watcher?.Dispose();
            Watcher = null;
            Folders = null;
            Scanner = null;
            Store?.Dispose();
            Store = null;
            InitError = ex.Message;
        }
    }

    /// <summary>启动本地桥接（浏览器扩展入库）。令牌为空时生成并落盘。</summary>
    private static void StartBridge()
    {
        var store = Store;
        if (store is null) return;

        if (!Settings.Settings.bridge_enabled)
        {
            BridgeStatus = "桥接已关闭";
            return;
        }

        try
        {
            store.EnsureBridgeSchema();
            var token = Settings.Settings.bridge_token;
            if (token.Length < 16)
            {
                token = Gallery.Services.Bridge.BridgeServer.NewToken();
                Settings.Settings.bridge_token = token;
                Settings.Save();
            }

            var bridge = new Gallery.Services.Bridge.BridgeServer(store, AppVersion());
            LogBridge($"启动：port={Settings.Settings.bridge_port} tokenLen={token.Length}");
            if (bridge.Start(Settings.Settings.bridge_port, token))
            {
                Bridge = bridge;
                BridgeStatus = $"桥接监听 127.0.0.1:{bridge.Port}";
                LogBridge(BridgeStatus);
            }
            else
            {
                BridgeStatus = "桥接启动失败：" + bridge.LastError;
                LogBridge(BridgeStatus);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            BridgeStatus = "桥接启动失败：" + ex.Message;
        }
    }

    /// <summary>设置页切换插件开关或端口后重启监听。</summary>
    public static void RestartBridge()
    {
        if (IsShuttingDown) return;
        var old = Bridge;
        Bridge = null;
        _retiredBridgeTasks.RemoveAll(task => task.IsCompletedSuccessfully);
        if (old is not null) _retiredBridgeTasks.Add(old.StopAsync());
        StartBridge();
    }

    private static readonly List<Task> _retiredBridgeTasks = [];

    /// <summary>桥接诊断日志（写 %APPDATA%\Gallery\bridge.log，失败不影响主流程）。</summary>
    private static void LogBridge(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.AppDataDir, "bridge.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写不了就算了
        }
    }

    /// <summary>给 /ping 用的应用版本串。</summary>
    private static string AppVersion()
    {
        try
        {
            var v = typeof(AppService).Assembly.GetName().Version;
            return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "1.0";
        }
    }

    /// <summary>后台导入角色词库：只有摘要变化才会真正写库，失败只记日志不打扰用户。</summary>
    private static void StartCatalogImport()    {
        if (Store is null) return;
        var store = Store;
        _catalogTask = Task.Run(() =>
        {
            try
            {
                var catalog = Catalog ?? new TagCatalogService(store);
                var reports = catalog.ImportAll(log: null);
                CatalogImported = reports.Count;
                var align = catalog.AlignTags();
                CatalogAligned = align;
                store.NormalizePendingSkins();
            }
            catch (Exception ex)
            {
                CatalogError = ex.Message;
            }
        });
    }

    /// <summary>资料包（词库／识别模型）热插拔完成后通知界面，参数是给用户看的一句话。</summary>
    public static event Action<string>? PackChanged;

    internal static void NotifyPackRemoved() => PackChanged?.Invoke("导入项已移除");
    internal static void NotifyPersonalRulesRestored() => PackChanged?.Invoke("个人规则已恢复");

    private static PackWatcher? _packWatcher;

    /// <summary>
    /// 开始监听 <c>archive</c>（资料库包）和 <c>models</c>（识别模型）：
    /// 用户把下载到的包拖进设置页或直接拷进目录，静置 1.5 秒后自动导入，不用重启。
    /// </summary>
    public static void StartPackWatch()
    {
        try
        {
            PackFolders.EnsureDirs();
            ApplyModelPack();
            _packWatcher?.Dispose();
            var watcher = new PackWatcher(PackFolders.Archive, PackFolders.Models);
            if (!watcher.IsWatching)
            {
                watcher.Dispose();
                DebugLog("pack watch skipped: " + PackFolders.Root);
                return;
            }
            watcher.CatalogChanged += ReloadCatalogPacks;
            watcher.ModelsChanged += ApplyModelPack;
            _packWatcher = watcher;
            DebugLog("pack watch started: " + watcher.Describe());
        }
        catch (Exception ex)
        {
            DebugLog("pack watch failed: " + ex.Message);
        }
    }

    /// <summary>资料库包有变化：按摘要重导一次；摘要没变（或文件还没写完）就什么都不做。</summary>
    public static void ReloadCatalogPacks()
    {
        try { ImportCatalogPacks(); }
        catch (Exception ex) { DebugLog("hot catalog import failed: " + ex.Message); }
    }

    /// <summary>交互导入必须向调用方报告入库错误，不能只记日志后显示成功。</summary>
    public static void ImportCatalogPacks()
    {
        if (IsShuttingDown || Store is null) throw new InvalidOperationException("资料库尚未就绪");
        var catalog = Catalog ?? new TagCatalogService(Store);
        var reports = catalog.ImportAll(log: null);
        if (reports.Count > 0)
        {
            catalog.AlignTags();
            Store.NormalizePendingSkins();
        }
        PackChanged?.Invoke(reports.Count > 1 ? $"资料库已更新（{reports.Count} 份）" : "资料库已更新");
    }

    /// <summary>模型目录有新包：仅启用唯一的可用模型，写回设置；下一次识别就用它。</summary>
    public static void ApplyModelPack()
    {
        if (IsShuttingDown || PackFolders.ModelPacks().Count == 0) return;
        try { ActivateModelPack(); }
        catch (Exception ex) { DebugLog("hot model switch failed: " + ex.Message); }
    }

    public static void ActivateModelPack()
    {
        var dir = PackFolders.SingleModelDir()
            ?? throw new InvalidOperationException("需要且只能有一个有效模型包");
        if (string.Equals(Settings.Settings.nsfw_model_dir, dir, StringComparison.OrdinalIgnoreCase)) return;
        Settings.Settings.nsfw_model_dir = dir;
        Settings.Save();
        PackChanged?.Invoke("识别模型已就绪");
    }

    private static void StopPackWatch()
    {
        try { _packWatcher?.Dispose(); }
        catch { /* 退出阶段忽略 */ }
        _packWatcher = null;
    }

    /// <summary>扫描/入库/手改标签后，后台补一次对齐；同一时间只跑一个。</summary>
    public static void RequestCatalogAlign()
    {
        lock (CatalogWorkGate)
        {
            var catalog = Catalog;
            if (IsShuttingDown || catalog is null || Store is null || _catalogAlignTask is { IsCompleted: false }) return;
            _catalogAlignTask = Task.Run(() =>
            {
                try { catalog.AlignTags(); }
                catch (Exception ex) { DebugLog("catalog align failed: " + ex); }
            });
        }
    }

    private static readonly object CatalogWorkGate = new();
    private static Task? _catalogAlignTask;

    public static void BeginShutdown()
    {
        lock (CatalogWorkGate) IsShuttingDown = true;
    }

    public static void CancelShutdown()
    {
        lock (CatalogWorkGate) IsShuttingDown = false;
    }

    public static int CatalogImported { get; private set; }
    private static Task? _catalogTask;

    /// <summary>对应 Gallery_Old quit_app/check_quit：停止新输入、等待已有工作，再关闭数据库。</summary>
    public static async Task ShutdownAsync()
    {
        BeginShutdown();
        Settings.Save();
        StopPackWatch();
        await Task.WhenAll(Watcher?.StopAsync() ?? Task.CompletedTask,
            Bridge?.StopAsync() ?? Task.CompletedTask);
        await Task.WhenAll(_retiredBridgeTasks.ToArray());
        await StopIngestReconciliationAsync();
        if (_catalogTask is not null) await _catalogTask;
        Task? align;
        lock (CatalogWorkGate) align = _catalogAlignTask;
        if (align is not null) await align;
        if (Store is { } store)
        {
            lock (store.SyncRoot) store.Dispose();
        }
        DbReady = false;
    }
    public static TagCatalogService.AlignReport? CatalogAligned { get; private set; }
    public static string CatalogError { get; private set; } = "";
    public static event Action? IngestQueueReconciled;

    private static readonly SemaphoreSlim IngestReconcileGate = new(1, 1);
    private static readonly object IngestReconcileSchedule = new();
    private static Timer? _ingestReconcileTimer;
    private static CancellationTokenSource? _ingestReconcileCts;

    private static void StartIngestReconciliation()
    {
        if (Store is null) return;
        lock (IngestReconcileSchedule)
        {
            if (_ingestReconcileTimer is not null) return;
            _ingestReconcileCts = new CancellationTokenSource();
            var token = _ingestReconcileCts.Token;
            _ingestReconcileTimer = new Timer(
                _ => _ = ReconcileIngestQueueAsync(token),
                null, Timeout.Infinite, Timeout.Infinite);
        }
        ScheduleIngestReconcile(0);
    }

    /// <summary>合并短时间内的多次入库事件，延迟执行一次队列对账。</summary>
    public static void ScheduleIngestReconcile(int delayMs = 750)
    {
        lock (IngestReconcileSchedule)
        {
            try
            {
                _ingestReconcileTimer?.Change(Math.Max(0, delayMs), Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task ReconcileIngestQueueAsync(CancellationToken token)
    {
        var store = Store;
        if (store is null || !DbReady || token.IsCancellationRequested) return;
        try
        {
            if (!await IngestReconcileGate.WaitAsync(0, token).ConfigureAwait(false))
            {
                ScheduleIngestReconcile(250);
                return;
            }
            try
            {
                var result = await Task.Run(() => IngestReconciler.Reconcile(store, token), token)
                    .ConfigureAwait(false);
                if (result.Completed + result.Missing == 0) return;
                DebugLog($"ingest reconcile: completed={result.Completed} missing={result.Missing}");
                IngestQueueReconciled?.Invoke();
            }
            finally
            {
                IngestReconcileGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ObjectDisposedException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            DebugLog("ingest reconcile failed: " + ex.Message);
        }
    }

    private static async Task StopIngestReconciliationAsync()
    {
        Timer? timer;
        CancellationTokenSource? cts;
        lock (IngestReconcileSchedule)
        {
            timer = _ingestReconcileTimer;
            cts = _ingestReconcileCts;
            _ingestReconcileTimer = null;
            _ingestReconcileCts = null;
        }
        try { timer?.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
        timer?.Dispose();
        cts?.Cancel();
        await IngestReconcileGate.WaitAsync().ConfigureAwait(false);
        IngestReconcileGate.Release();
        cts?.Dispose();
    }

    private static void ApplyWriteSettings()
    {
        if (Store is null) return;
        lock (Store.SyncRoot)
        {
            Store.SidecarEnabled = Settings.Settings.sidecar_enabled;
            Store.HideSidecar = Settings.Settings.hide_sidecar;
        }
    }
}
