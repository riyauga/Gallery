using Microsoft.UI.Xaml;
using Gallery.Services;

namespace Gallery;

public partial class App : Application
{
    private Window? _window;
    private DesktopPipe? _desktopPipe;
    private Controls.Viewer.ViewerHost? _viewerHost;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteCrash("XAML", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => FaultLog.Write("unobserved-task", e.Exception);
    }

    private static void WriteCrash(string source, Exception? error)
    {
        FaultLog.Write("crash-" + source, error);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cli = Environment.GetCommandLineArgs();

#if GALLERY_DEVELOPMENT
        // --dataroot <dir>：沙箱重定向（自动化测试零接触真实数据）
        var drIdx = Array.IndexOf(cli, "--dataroot");
        if (drIdx >= 0 && drIdx + 1 < cli.Length)
            AppPaths.ApplySandbox(cli[drIdx + 1]);
#endif

#if GALLERY_DEVELOPMENT
        if (RunDevelopmentCommand(cli)) return;
#endif

        var externalFiles = new List<string>();
        for (var i = 1; i < cli.Length; i++)
        {
#if GALLERY_DEVELOPMENT
            if (cli[i] == "--dataroot") { i++; continue; }
#endif
            if (cli[i] == "--update-token") { i++; continue; }
            if (cli[i] == "--view") continue;
            if (!cli[i].StartsWith("--", StringComparison.Ordinal))
            {
                try { externalFiles.Add(Path.GetFullPath(cli[i])); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                { NativeMessage("文件位置无效，请重新选择图片或视频。"); Exit(); return; }
            }
        }
        if (cli.Contains("--view") && externalFiles.Count == 0)
        { NativeMessage("请选择要用 Gallery 打开的图片或视频文件。"); Exit(); return; }
        if (ViewerCoordinator.UpdateInProgress && !cli.Contains("--update-token"))
        { NativeMessage("Gallery 正在更新，请完成后再打开。"); Exit(); return; }
        if (externalFiles.Count > 0)
        {
            if (!SingleInstance.TryAcquire(DesktopPipe.ViewerScope))
            {
                var reply = await DesktopPipe.SendAsync(true, new("open", externalFiles.ToArray()), 15000);
                if (reply?.Success != true) NativeMessage(reply?.Message ?? "未能打开查看窗口，请稍后重试。");
                Exit(); return;
            }
            ImageLoader.SetGalleryActive(false);
            _viewerHost = new Controls.Viewer.ViewerHost();
            _viewerHost.LastWindowClosed = Exit;
            _desktopPipe = new DesktopPipe(true, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), _viewerHost.HandleAsync);
            try { _viewerHost.Open(externalFiles); AppService.DebugLog("viewer open ok files=" + externalFiles.Count); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { NativeMessage("无法打开文件：" + ex.Message); Exit(); }
            catch (Exception ex)
            { AppService.DebugLog("VIEWER-OPEN-FAIL " + ex); throw; }
            return;
        }

        // 单实例：同一份数据只允许一个进程；再启动时把已运行的那个唤到前面（名字按数据根散列，沙箱之间互不影响）
        var instanceScope = AppPaths.LocalDataDir;
        if (!SingleInstance.TryAcquire(instanceScope))
        {
            SingleInstance.SignalExisting(instanceScope);
            Exit();
            return;
        }

#if GALLERY_DEVELOPMENT
        AppService.FakeNsfwScorer = cli.Contains("--nsfw-fake-scorer");
#endif
        UpdateService.CleanupCompleted();
        AppService.Init();
        AutoStart.SyncFromSettings();
        AppService.DebugLog("started scope=" + instanceScope);

        var window = new MainWindow();
        _window = window;
        window.Activate();
        _desktopPipe = new DesktopPipe(false, window.DispatcherQueue, window.HandleDesktopCommandAsync);

        SingleInstance.ListenForActivation(() =>
            window.DispatcherQueue.TryEnqueue(window.BringToFront));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    private static void NativeMessage(string message) => MessageBoxW(IntPtr.Zero, message, "Gallery", 0x40);

}
