using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Gallery.Services;
using Gallery.Views;
using Windows.Graphics;
using System.Runtime.InteropServices;

namespace Gallery;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private readonly Library _library = Library.Load();
    private readonly GalleryPage _page;
    private readonly SettingsPage _settingsPage = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        Title = "Gallery";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        // 照搬 photos：标题栏 48 DIP，系统三件套撑满（48x48）
        // WinUI 默认 Standard 是 46x32，与设置按钮（40x48）高度不齐
        var appWindow = WinRT.Interop.WindowNative.GetWindowHandle(this)
            is IntPtr hwnd
                ? Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd))
                : null;
        if (appWindow is not null)
            appWindow.TitleBar.PreferredHeightOption =
                Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
        }

        // 主题切到根元素（对应 photos 的 IThemeService.SetTheme）
        ThemeService.Root = RootGrid;

        AppService.Init();
        if (AppService.DbReady)
        {
            // 批次 0 验收可见化：库连接状态
            ShowToast($"库已连接：{AppService.RootCount} 个目录 · " +
                      $"{AppService.ImageCount} 张图片 · {AppService.FavoriteCount} 收藏");
        }
        else if (AppService.InitError.Length > 0)
        {
            ShowToast("库打开失败：" + AppService.InitError);
        }

        _page = new GalleryPage(_library);
        _page.ToastRequested += ShowToast;
        _page.PreviewRequested += (item, items) => Preview.Open(items, item);
        ContentHost.Children.Add(_page);

        Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        Nav.Loaded += (_, _) =>
        {
            Nav.IsPaneOpen = true;
            Nav.SelectedItem = Nav.MenuItems[0];
        };

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            AnimateToast(0, 200);
        };

        Activated += OnFirstActivated;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        DispatcherQueue.TryEnqueue(() =>
        {
            ResizeAndCenter(1290, 700);
        });
    }

    private void ResizeAndCenter(int dipWidth, int dipHeight)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        int screenW = GetSystemMetrics(0);
        int screenH = GetSystemMetrics(1);
        int width = Math.Min((int)(dipWidth * scale), screenW - 80);
        int height = Math.Min((int)(dipHeight * scale), screenH - 80);
        int x = (screenW - width) / 2;
        int y = (screenH - height) / 2;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x0004);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        int index = args.SelectedItem is NavigationViewItem item ? Nav.MenuItems.IndexOf(item) : 0;
        ShowPage(index);
    }

    /// <summary>
    /// 对应 photos 的 _navigationService.NavigateTo(typeof(SettingsPage))：
    /// 整页替换内容区，标题栏与左侧导航原地保留。
    /// </summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowPage(-1);

    private void ShowPage(int navIndex)
    {
        ContentHost.Children.Clear();
        if (navIndex < 0)
        {
            ContentHost.Children.Add(_settingsPage);
            return;
        }
        ContentHost.Children.Add(_page);
        _page.SetMode(navIndex == 1);
        // 离开设置页时同步导航选中态（photos 进设置页会清掉导航选中高亮）
        if (!ReferenceEquals(Nav.SelectedItem, Nav.MenuItems[navIndex]))
            Nav.SelectedItem = Nav.MenuItems[navIndex];
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => _page.SetSearch(SearchBox.Text);

    /// <summary>photos 的「添加文件夹」：选目录 → 备份 → 事务入库 → 首扫。</summary>
    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppService.Folders is null)
        {
            ShowToast("库未连接，无法添加目录");
            return;
        }
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        AddFolderButton.IsEnabled = false;
        ShowToast("正在扫描新目录…");
        var path = folder.Path;
        var outcome = await Task.Run(() =>
        {
            lock (AppService.DbLock)
            {
                try
                {
                    var (root, scan) = AppService.Folders.AddRootAndScan(path);
                    AppService.Scanner?.Thumbs.PruneOrphans();
                    return AddRootOutcome.Ok(root, scan);
                }
                catch (Exception ex)
                {
                    return AddRootOutcome.Fail(ex.Message);
                }
            }
        });
        AddFolderButton.IsEnabled = true;
        if (outcome.Error is not null)
        {
            ShowToast(outcome.Error);
            return;
        }
        ShowToast($"已添加：{Path.GetFileName(outcome.Root!.Path)}（{outcome.Scan!.Summary()}）");
        AppService.Recount();
        _page.RefreshEmptyState();
    }

    private sealed record AddRootOutcome(RootInfo? Root, ScanResult? Scan, string? Error)
    {
        public static AddRootOutcome Ok(RootInfo root, ScanResult scan) => new(root, scan, null);
        public static AddRootOutcome Fail(string error) => new(null, null, error);
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen) { Preview.HandleKey(Windows.System.VirtualKey.Escape); args.Handled = true; }
    }

    private void OnLeftInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen) { Preview.HandleKey(Windows.System.VirtualKey.Left); args.Handled = true; }
    }

    private void OnRightInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen) { Preview.HandleKey(Windows.System.VirtualKey.Right); args.Handled = true; }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        AnimateToast(1, 150);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void AnimateToast(double to, int ms)
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, Toast);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }
}
