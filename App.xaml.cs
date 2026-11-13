using Microsoft.UI.Xaml;
using Gallery.Services;

namespace Gallery;

public partial class App : Application
{
    private Window? _window;

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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
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

        SingleInstance.ListenForActivation(() =>
            window.DispatcherQueue.TryEnqueue(window.BringToFront));
    }

}
