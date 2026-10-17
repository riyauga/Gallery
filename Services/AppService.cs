namespace Gallery.Services;

/// <summary>
/// 应用服务装配（批次 0 简易 DI）。
/// 启动时打开 AppPaths 指定的库，并启动文件监听；支持 --dataroot 沙箱。
/// </summary>
public static class AppService
{
    public static SettingsStore Settings { get; } = new();
    public static StoreService? Store { get; private set; }
    public static FolderService? Folders { get; private set; }
    public static ScannerService? Scanner { get; private set; }
    public static WatcherService? Watcher { get; private set; }

    public static bool DbReady { get; private set; }
    public static string InitError { get; private set; } = "";
    public static int RootCount { get; private set; }
    public static int ImageCount { get; private set; }
    public static int FavoriteCount { get; private set; }

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
        Settings.Load();
        try
        {
            AppPaths.EnsureDirs();
            Store = new StoreService(AppPaths.DbPath);
            Store.SidecarEnabled = Settings.Settings.sidecar_enabled;
            Store.HideSidecar = Settings.Settings.hide_sidecar;
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
