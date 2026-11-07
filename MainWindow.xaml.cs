using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Gallery.Controls;
using Gallery.Services;
using Gallery.Views;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using System.Runtime.InteropServices;

namespace Gallery;


public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private readonly Library _library = Library.Load();
    private readonly GalleryPage _page;
    // Photos MenuItemsViewModel.CreateSettingsPageMenuItem：隐藏项仍参与导航选择。
    private readonly NavigationViewItem _settingsNavigationItem = new()
    {
        Tag = "settings", Content = "设置", Visibility = Visibility.Collapsed,
    };
    private readonly SettingsPage _settingsPage = new();

    // 导航历史：主页与设置页共用一条，记的是「位置标识」而不是控件引用（目录树会重建）。
    private readonly List<string> _navHistory = [];
    private int _navIndex = -1;
    private bool _replayingHistory;
    private const int NavHistoryLimit = 50;
    /// <summary>建待审核页并挂上角标刷新。</summary>
    private Controls.Review.ReviewPage CreateReviewPage()
    {
        var page = new Controls.Review.ReviewPage(this);
        page.Changed += UpdateReviewBadge;
        return page;
    }

    /// <summary>侧栏「待审核」显示待处理条数（0 时隐藏）。</summary>
    private void UpdateReviewBadge()
    {
        var store = Services.AppService.Store;
        if (store is null) return;

        var pending = store.IngestStats().TryGetValue("pending", out var n) ? n : 0;

        // 待审核数量显示在标题栏和侧栏文字里。侧栏不再用 InfoBadge：
        // 角标会挤占内容列，导致右侧导入按钮和「图库」的添加按钮对不齐。
        ReviewNavText.Text = pending > 0 ? $"待审核 {pending}" : "待审核";
        ReviewChipButton.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewChipText.Text = pending > 0 ? $"待审核 {pending}" : "待审核";
        UpdateTitleBarRegions();
        // 刚切完可见性还没排版，等一轮低优先级再算一次（否则角标拿不到 ActualWidth）
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTitleBarRegions);
    }

    /// <summary>
    /// 标题栏整条被 SetTitleBar 当成拖动区，按钮默认收不到鼠标。
    /// 把搜索框、「待审核」角标和设置按钮登记成 passthrough 区域，允许文本编辑与按钮交互。
    /// </summary>
    private double _titleBarRasterizationScale = 1.0;
    private XamlRoot? _titleBarXamlRoot;

    private void OnTitleBarXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != _titleBarRasterizationScale) UpdateTitleBarRegions();
    }

    private void UpdateTitleBarRegions()
    {
        if (AppWindowInstance is not { } window) return;
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        _titleBarRasterizationScale = scale;
        var rects = new List<Windows.Graphics.RectInt32>();

        void Add(FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible) return;
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var bounds = element.TransformToVisual(null)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var left = (int)Math.Floor(bounds.Left * scale);
            var top = (int)Math.Floor(bounds.Top * scale);
            var right = (int)Math.Ceiling(bounds.Right * scale);
            var bottom = (int)Math.Ceiling(bounds.Bottom * scale);
            rects.Add(new Windows.Graphics.RectInt32(
                left, top, right - left, bottom - top));
        }

        Add(SearchBox);
        Add(ReviewChipButton);
        Add(SettingsButton);

        try
        {
            var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(window.Id);
            if (rects.Count == 0)
                source.ClearRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough);
            else
                source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch (Exception ex)
        {
            Services.AppService.DebugLog("titlebar passthrough failed: " + ex.Message);
        }
    }

    private void ReviewChip_Click(object sender, RoutedEventArgs e)
    {
        ActivateWindow();
        Nav.SelectedItem = ReviewNavItem;
    }

    /// <summary>启动后刷新一次角标，并挂在桥接事件上（新记录进来就加一）。</summary>
    private void HookDisplayMode()
    {
        Services.AppService.DisplayModeChanged += () =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _page.RefreshFromStore();
                if (_reviewPage is { IsLoaded: true }) _reviewPage.RefreshPreview();
            });
    }

    private void HookReviewBadge()
    {
        UpdateReviewBadge();
        if (Services.AppService.Bridge is { } bridge)
        {
            bridge.Ingested += (id, name) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_quitting) return;
                UpdateReviewBadge();
                _reviewPage?.Reload();
                NotifyIngest(id, name);
                Services.AppService.ScheduleIngestReconcile();
            });
            bridge.UiRequested += (view, path) => DispatcherQueue.TryEnqueue(async () => await HandleUiRequestAsync(view, path));
        }
        Services.AppService.IngestQueueReconciled += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_quitting) return;
            UpdateReviewBadge();
            _reviewPage?.Reload();
        });
        Services.AppService.ScheduleIngestReconcile(0);
    }

    private Controls.Review.IngestNotificationWindow? _ingestWindow;
    private Controls.Review.IngestNotificationCard? _ingestCard;
    private int _notificationCount, _latestIngestId;

    /// <summary>同一时段的新图片合并为一张提示；前台用应用内卡片，后台用无激活桌面浮层。</summary>
    private void NotifyIngest(int id, string name)
    {
        if (!Services.AppService.Settings.Settings.notify_new_images) return;
        var foreground = GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this);
        var count = _notificationCount + 1;
        if (_ingestCard is null || foreground != (_ingestWindow is null))
        {
            _ingestCard?.Dismiss();
            var window = foreground ? null : new Controls.Review.IngestNotificationWindow(this);
            var card = window?.Card ?? new Controls.Review.IngestNotificationCard
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 18, 18),
            };
            _ingestWindow = window;
            _ingestCard = card;
            card.OpenRequested += () =>
            {
                BringToFront();
                Nav.SelectedItem = ReviewNavItem;
                _reviewPage ??= CreateReviewPage();
                _reviewPage.OpenPending(_latestIngestId);
            };
            card.Dismissed += () =>
            {
                if (!ReferenceEquals(_ingestCard, card)) return;
                if (window is null) RootGrid.Children.Remove(card);
                _ingestCard = null;
                _ingestWindow = null;
                _notificationCount = 0;
            };
            if (foreground) { Grid.SetRowSpan(card, 2); Canvas.SetZIndex(card, 100); RootGrid.Children.Add(card); }
            else window!.ShowWithoutActivation();
        }
        _notificationCount = count;
        _latestIngestId = id;
        _ingestCard.Update(count, name, "下载完成 · 点击查看并归档");
    }

    private Controls.Catalog.CatalogPage? _catalogPage;
    private Services.TrayIcon? _tray;
    private TrayMenuHost? _trayMenu;
    private bool _quitting;
    private bool _refreshCaptionAfterTrayHide;
    private UIElement? _lowPowerPage;
    private string? _captionInputTracePhase;
    private int _captionInputTraceMask;

    /// <summary>主窗口的 AppWindow（托盘唤回、关窗收托盘都要用）。</summary>
    public Microsoft.UI.Windowing.AppWindow? AppWindowInstance { get; private set; }

    /// <summary>从托盘／通知唤回：窗口可能是隐藏状态，先 Show 再抢前台（ActivateWindow 含 SetForegroundWindow）。</summary>
    public void BringToFront()
    {
        AppWindowInstance?.Show();
        ExitLowPowerMode();
        ActivateWindow();
        _captionInputTracePhase = "restored";
        _captionInputTraceMask = 0;
        AppService.DebugLog($"caption restored custom={ExtendsContentIntoTitleBar}");
        Services.AppService.DebugLog("brought to front");
    }

    /// <summary>收进托盘时释放可重建的像素与会话；后台服务、页面和滚动位置按约定保留。</summary>
    private void EnterLowPowerMode()
    {
        if (_lowPowerPage is not null) return;
        _lowPowerPage = ContentHost.Children.FirstOrDefault(child => child.Visibility == Visibility.Visible);
        Preview.CloseImmediate();
        ImageLoader.SetPreviewOpen(false);
        ImageLoader.SetGalleryActive(false);
        if (ReferenceEquals(_lowPowerPage, _page)) _page.SetActive(false);
        else if (_reviewPage is not null && ReferenceEquals(_lowPowerPage, _reviewPage)) _reviewPage.SetActive(false);
        ImageLoader.ClearCaches();
        PixaiScorer.RefreshCache(null);
        AppService.DebugLog("low power: pixel caches and scorer released");
    }

    /// <summary>唤回时恢复页面活动状态：图库沿用当前瓦片画面并按需重新解码，审核页重新预取。</summary>
    private void ExitLowPowerMode()
    {
        var page = _lowPowerPage;
        _lowPowerPage = null;
        if (page is null) return;
        ImageLoader.SetGalleryActive(true);
        if (ReferenceEquals(page, _page)) _page.SetActive(true);
        else if (_reviewPage is not null && ReferenceEquals(page, _reviewPage)) _reviewPage.SetActive(true);
        AppService.DebugLog("low power: resumed");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    /// <summary>托盘唤回后按真实鼠标位置刷新系统按钮状态，不移动鼠标。</summary>
    private void RefreshCaptionAfterTrayHide(IntPtr hwnd)
    {
        if (!_refreshCaptionAfterTrayHide) return;
        _refreshCaptionAfterTrayHide = false;
        UpdateTitleBarRegions();
        if (!GetCursorPos(out var point)) return;
        // Win32 鼠标消息以两个有符号 16 位屏幕坐标打包，兼容负坐标显示器。
        var position = (IntPtr)unchecked((int)(((uint)point.Y & 0xFFFF) << 16 | ((uint)point.X & 0xFFFF)));
        var hit = SendMessage(hwnd, 0x0084, IntPtr.Zero, position); // WM_NCHITTEST
        SendMessage(hwnd, 0x00A0, hit, position); // WM_NCMOUSEMOVE
        AppService.DebugLog($"caption refresh hit={hit} cursor={point.X},{point.Y}");
    }

    /// <summary>建托盘图标：左键唤回、右键菜单（显示 / 待审核数 / 退出）。</summary>
    private void SetupTray()
    {
        _tray = new Services.TrayIcon();
        _tray.ActivateRequested += () => DispatcherQueue.TryEnqueue(BringToFront);
        _tray.MenuRequested += (x, y) =>
        {
            var queued = DispatcherQueue.TryEnqueue(() =>
            {
                AppService.DebugLog($"tray menu UI quitting={_quitting}");
                if (_quitting) return;
                _trayMenu?.Dismiss();
                var menu = new TrayMenuHost(RootGrid.ActualTheme, x, y,
                    AppService.Store?.PendingIngestCount() ?? 0,
                    BringToFront,
                    () => { BringToFront(); Nav.SelectedItem = ReviewNavItem; },
                    () => _ = QuitAsync());
                _trayMenu = menu;
                menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; };
                menu.Activate();
            });
            if (!queued) AppService.DebugLog("tray menu UI enqueue failed");
        };
        _tray.Start();
        Services.AppService.DebugLog("tray start requested");
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        AppService.BeginShutdown();
        _ingestCard?.Dismiss();
        RootGrid.IsHitTestVisible = false;
        _tray?.UpdateTip("Gallery · 正在完成保存并退出");
        try
        {
            DialogService.Dismiss();
            await _settingsPage.StopBackgroundWorkAsync();
            if (_reviewPage is not null) await _reviewPage.DrainWorkAsync();
            await Task.WhenAll(_folderOperations.ToArray());
            await _page.DrainFileOperationsAsync();
            await AppService.ShutdownAsync();
            _tray?.Dispose();
            _tray = null;
            Close();
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            _quitting = false;
            AppService.CancelShutdown();
            RootGrid.IsHitTestVisible = true;
            BringToFront();
            ShowToast("退出失败：" + ex.Message);
        }
    }

    private void UpdateTrayTip()
    {
        var pending = Services.AppService.Store?.PendingIngestCount() ?? 0;
        _tray?.UpdateTip(pending > 0 ? $"Gallery · 待审核 {pending} 条" : "Gallery");
    }
    private Controls.Review.ReviewPage? _reviewPage;

    // 本机 Photos 的 WM_GETMINMAXINFO：200% 缩放时最小 1032×1032 像素，即 516×516 DIP。
    private const int MinimumWindowWidthDip = 516, MinimumWindowHeightDip = 516;
    private const uint WM_GETMINMAXINFO = 0x0024;
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_XBUTTONUP = 0x020C;
    private const uint WM_APPCOMMAND = 0x0319;
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint WM_WINDOWPOSCHANGED = 0x0047;

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    private bool _restoringMaximizedViewer;
    private int _suppressedViewerRestoreBounds;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    /// <summary>保留最后一条 Win32 位置消息，在松手时同步框架状态。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    private bool _inWindowMove;
    private uint _windowMoveDpi;
    private WindowPosition? _deferredWindowPosition;
    private delegate IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr hWnd, WindowSubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, WindowSubclassProc pfnSubclass, UIntPtr uIdSubclass);
    private WindowSubclassProc? _windowSubclass;

    private IntPtr OnWindowSubclass(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_WINDOWPOSCHANGING && _restoringMaximizedViewer)
        {
            // 先保留框架的非客户区／Z 序处理，再阻止 presenter 恢复期间的普通窗口尺寸落地。
            // 不调用 SetWindowPos/Maximize，不派发新消息，避免窗口消息重入。
            var result = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            if (!IsZoomed(hWnd))
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                const uint noSizeOrMove = 0x0001 | 0x0002; // SWP_NOSIZE | SWP_NOMOVE
                if ((position.Flags & noSizeOrMove) != noSizeOrMove)
                {
                    position.Flags |= noSizeOrMove;
                    Marshal.StructureToPtr(position, lParam, false);
                    _suppressedViewerRestoreBounds++;
                }
            }
            return result;
        }
        if (uMsg == WM_GETMINMAXINFO)
        {
            // 先保留框架计算的最大化／屏幕边界，只补最小跟踪尺寸；拖边时由系统直接约束。
            var result = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            var limits = Marshal.PtrToStructure<WindowMinMaxInfo>(lParam);
            var scale = GetDpiForWindow(hWnd) / 96.0;
            limits.MinTrackSize.X = Math.Max(limits.MinTrackSize.X, (int)Math.Ceiling(MinimumWindowWidthDip * scale));
            limits.MinTrackSize.Y = Math.Max(limits.MinTrackSize.Y, (int)Math.Ceiling(MinimumWindowHeightDip * scale));
            Marshal.StructureToPtr(limits, lParam, false);
            return result;
        }
        if ((uMsg == 0x00A1 || uMsg == 0x00A2) && wParam == (IntPtr)20) // HTCLOSE
        {
            if (uMsg == 0x00A1)
            {
                _captionInputTracePhase = "close-click";
                _captionInputTraceMask = 0;
            }
            AppService.DebugLog($"caption close message=0x{uMsg:X}");
        }
        // XAML 内容位于子窗口，侧键可能经默认窗口过程转换成浏览器前进／后退命令。
        if (uMsg == WM_APPCOMMAND)
        {
            var command = (int)(((long)lParam >> 16) & 0x0FFF);
            if (command is 1 or 2)
            {
                var delta = command == 1 ? -1 : 1;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!HasKeyboardPopup()) HandleHistoryInput(delta);
                });
                return (IntPtr)1;
            }
        }
        // 鼠标侧键：XBUTTON1 后退、XBUTTON2 前进。
        // 抬起也一并吞掉，避免控件收到半个手势；没有可去的位置时同样不往下传。
        if (uMsg == WM_XBUTTONDOWN)
        {
            var button = (int)((long)wParam >> 16) & 0xFFFF;
            if (button is 1 or 2)
            {
                // 不能在窗口过程里直接切页：NavigationView 选区变化会同步创建／刷新页面内容，
                // 在输入处理回调里重入 XAML 会把输入队列卡住（实测：第二下侧键就再也收不到消息）。
                var delta = button == 1 ? -1 : 1;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (HasKeyboardPopup()) return;
                    HandleHistoryInput(delta);
                });
                return (IntPtr)1;
            }
        }
        else if (uMsg == WM_XBUTTONUP)
        {
            var button = (int)((long)wParam >> 16) & 0xFFFF;
            if (button is 1 or 2) return (IntPtr)1;
        }
        var inputBit = uMsg switch
        {
            0x0200 => 1, // WM_MOUSEMOVE
            0x00A0 => 2, // WM_NCMOUSEMOVE
            0x02A3 => 4, // WM_MOUSELEAVE
            0x02A2 => 8, // WM_NCMOUSELEAVE
            0x0245 => 16, // WM_POINTERUPDATE
            _ => 0,
        };
        if (_captionInputTracePhase is { } phase && inputBit != 0 &&
            (_captionInputTraceMask & inputBit) == 0)
        {
            _captionInputTraceMask |= inputBit;
            AppService.DebugLog($"caption input phase={phase} message=0x{uMsg:X} wParam=0x{wParam.ToInt64():X} lParam=0x{lParam.ToInt64():X}");
        }
        if (uMsg == WM_ENTERSIZEMOVE)
        {
            _inWindowMove = true;
            _windowMoveDpi = GetDpiForWindow(hWnd);
            _reviewPage?.BeginWindowMove();
        }
        else if (uMsg == WM_EXITSIZEMOVE)
        {
            _inWindowMove = false;
            FlushWindowPosition(hWnd);
            _reviewPage?.EndWindowMove();
        }
        else if (uMsg == WM_WINDOWPOSCHANGED)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004,
                SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020,
                SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
            if (_inWindowMove && (position.Flags & (SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)) ==
                    (SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE) &&
                (position.Flags & (SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_HIDEWINDOW)) == 0 &&
                GetDpiForWindow(hWnd) == _windowMoveDpi)
            {
                // 纯移动时客户端几何不变。保留 DefWindowProc 发出的 WM_MOVE，
                // 将 AppWindow 标题栏的输入窗口重排合并到松手后一次。
                _deferredWindowPosition = position;
                return DefWindowProc(hWnd, uMsg, wParam, lParam);
            }
            _deferredWindowPosition = null;
        }
        else if (uMsg == 0x02E0) // WM_DPICHANGED：跨 DPI 后恢复完整框架处理。
        {
            _inWindowMove = false;
            _deferredWindowPosition = null;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>松手时同步最终位置，再恢复待审核网页；缩放和 DPI 变化不合并。</summary>
    private void FlushWindowPosition(IntPtr hwnd)
    {
        if (_deferredWindowPosition is not { } position) return;
        _deferredWindowPosition = null;
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowPosition>());
        try
        {
            Marshal.StructureToPtr(position, buffer, false);
            SendMessage(hwnd, WM_WINDOWPOSCHANGED, IntPtr.Zero, buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>当前窗口实例（查看器进全屏时需要暂时隐藏应用标题栏）。</summary>
    public static MainWindow? Instance { get; private set; }

    /// <summary>查看器全屏：隐藏 48 高标题栏行，让查看器占满窗口（返回时恢复）。</summary>
    public void SetTitleBarHidden(bool hidden)
    {
        TitleBar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        RootGrid.RowDefinitions[0].Height = new GridLength(hidden ? 0 : 48);
    }
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, in int value, uint size);

    private OverlappedPresenter? _viewerWindowedPresenter;
    private bool _viewerWindowWasMaximized;

    /// <summary>
    /// 查看器进出全屏：临时禁用该窗口的 DWM 过渡，统一提交 presenter、标题栏与查看器布局。
    /// applyViewerLayout 必须同步更新布局属性，不得入队、等待或开启缩放动画。
    /// </summary>
    public void SetViewerFullscreen(bool full, Action applyViewerLayout)
    {
        if (AppWindowInstance is not { } appWindow) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        const uint transitionsForceDisabled = 3; // DWMWA_TRANSITIONS_FORCEDISABLED，Win32 BOOL
        int disabled = 1;
        int result = DwmSetWindowAttribute(hwnd, transitionsForceDisabled, in disabled, sizeof(int));
        if (result < 0) AppService.DebugLog($"fullscreen: disable DWM transitions failed hr=0x{result:X8}");
        try
        {
            if (full && appWindow.Presenter is OverlappedPresenter windowed)
            {
                _viewerWindowedPresenter = windowed;
                _viewerWindowWasMaximized = windowed.State == OverlappedPresenterState.Maximized;
            }
            // SetPresenter 可能先恢复普通窗口，再恢复最大化。期间禁止普通尺寸覆盖全屏边界。
            _restoringMaximizedViewer = !full && _viewerWindowWasMaximized;
            _suppressedViewerRestoreBounds = 0;
            SetTitleBarHidden(full);
            applyViewerLayout();
            if (full)
                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            else
            {
                // 复用进入全屏前的实例；不创建新的普通窗口 presenter。
                if (_viewerWindowedPresenter is { } saved) appWindow.SetPresenter(saved);
                else appWindow.SetPresenter(AppWindowPresenterKind.Default);
                if (_viewerWindowWasMaximized && appWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Maximize();
            }
            // 只在本次窗口切换末尾统一排版一次；不在 SizeChanged/LayoutUpdated 内重入。
            RootGrid.UpdateLayout();
            if (_restoringMaximizedViewer)
                AppService.DebugLog($"fullscreen exit: suppressedRestoreBounds={_suppressedViewerRestoreBounds} "
                    + $"maximized={IsZoomed(hwnd)} client={appWindow.ClientSize.Width}x{appWindow.ClientSize.Height}");
        }
        finally
        {
            _restoringMaximizedViewer = false;
            // 本项目没有其他 DWM 过渡禁用者；恢复系统动画只影响后续窗口操作。
            if (result >= 0)
            {
                disabled = 0;
                int restored = DwmSetWindowAttribute(hwnd, transitionsForceDisabled, in disabled, sizeof(int));
                if (restored < 0) AppService.DebugLog($"fullscreen: restore DWM transitions failed hr=0x{restored:X8}");
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        Nav.MenuItems.Add(_settingsNavigationItem);
        // 设置页内部换面板也算一步，和主页共用同一条历史。
        _settingsPage.SectionChanged += section => RecordLocation("set:" + section);
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
        AppWindowInstance = appWindow;
        if (appWindow is not null)
        {
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "assets", "Gallery.ico"));
            appWindow.TitleBar.PreferredHeightOption =
                Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

            // 拖动/缩放窗口时把待审核页的 WebView 冻结成快照，松开后恢复。
            if (hwnd != IntPtr.Zero)
            {
                _windowSubclass = OnWindowSubclass;
                SetWindowSubclass(hwnd, _windowSubclass, 1, IntPtr.Zero);
                Closed += (_, _) =>
                {
                    if (_windowSubclass is { } subclass) RemoveWindowSubclass(hwnd, subclass, 1);
                    _inWindowMove = false;
                    _deferredWindowPosition = null;
                };
            }

            // 关窗行为对齐 galleryold：minimize_to_tray 时收进托盘而不是退出
            appWindow.Closing += (sender, e) =>
            {
                if (_quitting) return;
                e.Cancel = true;
                if (AppService.Settings.Settings.minimize_to_tray && _tray is { IsCreated: true })
                {
                    AppService.DebugLog($"caption hiding custom={ExtendsContentIntoTitleBar} inputMask=0x{_captionInputTraceMask:X}");
                    appWindow.Hide();
                    EnterLowPowerMode();
                    // 隐藏不会清理系统按钮的悬停状态，唤回后同步真实鼠标位置。
                    _refreshCaptionAfterTrayHide = true;
                    AppService.DebugLog("window hidden to tray");
                }
                else _ = QuitAsync();
            };
        }

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
        ThemeService.SetTheme(ThemeService.Parse(AppService.Settings.Settings.theme));

        AppService.Init();
        AppService.MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!AppService.DbReady && AppService.InitError.Length > 0)
            ShowToast("库打开失败：" + AppService.InitError);
        BuildDirectoryTree();

        _page = new GalleryPage(_library);
        SearchBox.DefaultFocusAnchor = _page.FocusAnchor;
        _page.ToastRequested += ShowToast;
        _page.ToastKindRequested += (msg, kind, detail) => ShowToast(msg, kind, detail);
        _page.PreviewRequested += req => Preview.Open(req);
        _page.ItemsChanged += Preview.RefreshItems;
        _page.TagsEdited += () =>
        {
            Preview.RefreshItems(_page.CurrentItems);
            Preview.RefreshCurrentTags();
        };
        Preview.TagsEdited += _ => _page.RefreshFromStore();
        Preview.ToastRequested += ShowToast;
        // 资料包热插拔（data\tag_catalog / data\models 新增或替换）后提示一下，并刷新图库标签
        Services.AppService.PackChanged += message => DispatcherQueue.TryEnqueue(() =>
        {
            ShowToast(message);
            _page.RefreshFromStore();
            if (_reviewPage is { IsLoaded: true }) _reviewPage.RefreshPreview();
        });
        Preview.ReturnFocusRequested += () => SearchBox.DefaultFocusAnchor?.Focus(FocusState.Programmatic);
        Preview.DeleteRequested = _page.DeleteAsync;
        Preview.RenameRequested = _page.RenameAsync;
        Preview.FavoriteRequested = _page.ToggleFavoriteAsync;
        Preview.ContextMenuBuilder = item => _page.BuildContextMenu(item, includeOpen: false);
        _page.FileOperationStarting += Preview.ReleaseVideo;
        ContentHost.Children.Add(_page);
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnGalleryBlankPointerPressed), true);
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnNavigationPointerPressed), true);
        RootGrid.PreviewKeyDown += OnGalleryPreviewKeyDown;
        RootGrid.PreviewKeyDown += OnNavHistoryKeyDown;
        RootGrid.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        SearchBox.SizeChanged += (_, _) => UpdateTitleBarRegions();
        RootGrid.Loaded += (_, _) =>
        {
            _titleBarXamlRoot = RootGrid.XamlRoot;
            _titleBarXamlRoot.Changed += OnTitleBarXamlRootChanged;
            UpdateTitleBarRegions();
        };
        RootGrid.Unloaded += (_, _) =>
        {
            if (_titleBarXamlRoot is not { } root) return;
            root.Changed -= OnTitleBarXamlRootChanged;
            _titleBarXamlRoot = null;
        };
        if (AppService.Watcher is { } watcher)
        {
            watcher.Changed += OnLibraryChanged;
            watcher.Failed += OnWatchFailed;
            Closed += (_, _) =>
            {
                watcher.Changed -= OnLibraryChanged;
                watcher.Failed -= OnWatchFailed;
                watcher.Dispose();
            };
        }

        Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        Nav.Loaded += (_, _) =>
        {
            Nav.IsPaneOpen = true;
            Nav.SelectedItem = Nav.MenuItems[0];
            HookReviewBadge();
            HookDisplayMode();
            SetupTray();
            UpdateTitleBarRegions();
            UpdateTrayTip();
        };

        // 照搬 photos 的 Gripper：把手在 Content 区左缘（= pane 右缘），
        // 拖动改 OpenPaneLength；宽度范围沿用 galleryold 180–500（D4），拖完记忆 sidebar_width。
        // 悬停光标 = 双向箭头（PaneResizer 内部，对应 SizerBase 内置行为）。
        Gripper.CurrentLength = () => Nav.OpenPaneLength;
        Gripper.MinLength = 180;
        Gripper.MaxLength = 500;
        Gripper.WidthChanged += len => Nav.OpenPaneLength = len;
        Gripper.DragFinished += () =>
        {
            AppService.Settings.Settings.sidebar_width = (int)Math.Round(Nav.OpenPaneLength);
            AppService.Settings.Save();
        };
        Nav.LayoutUpdated += (_, _) => UpdateGripperPosition();
        Nav.PaneOpening += (_, _) => AppService.DebugLog($"nav pane opening pid={Environment.ProcessId}");
        Nav.PaneOpened += (_, _) => AppService.DebugLog($"nav pane opened pid={Environment.ProcessId}");
        Nav.PaneClosing += (_, _) => AppService.DebugLog($"nav pane closing pid={Environment.ProcessId}");
        Nav.PaneClosed += (_, _) => AppService.DebugLog($"nav pane closed pid={Environment.ProcessId}");
        Nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdateGripperPosition());
        Nav.OpenPaneLength = Math.Clamp(AppService.Settings.Settings.sidebar_width, 180, 500);

        Activated += OnFirstActivated;
    }

    /// <summary>照 photos GetFrameMargin：展开时内容让出 Gripper 宽度（16），折叠时不留。</summary>
    private void UpdateGripperPosition()
    {
        ContentHost.Margin = Nav.IsPaneOpen ? new Thickness(16, 0, 0, 0) : new Thickness(0);
    }

    private void OnLibraryChanged(IReadOnlyCollection<(int RootId, string DirRel)> changes)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (_quitting) return;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            AppService.DebugLog($"nav library refresh begin pid={Environment.ProcessId} changes={changes.Count}");
            AppService.Recount();
            AppService.DebugLog($"nav library recount done elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            BuildDirectoryTree();
            AppService.DebugLog($"nav directory refresh done elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            _page.RefreshFromStore();
            AppService.DebugLog($"nav library refresh end elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        });

    private void OnWatchFailed(string message)
        => DispatcherQueue.TryEnqueue(() => ShowToast("目录监听扫描失败：" + message, ToastKind.Pending));

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
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        var kind = tag.StartsWith("dir:", StringComparison.Ordinal) ? "directory" : tag;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        AppService.DebugLog($"nav selection begin pid={Environment.ProcessId} target={kind} paneOpen={Nav.IsPaneOpen}");
        try { NavigateSelection(tag); }
        finally { AppService.DebugLog($"nav selection end pid={Environment.ProcessId} target={kind} elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}"); }
    }

    private void NavigateSelection(string tag)
    {
        // 详情层是全窗遮罩：切页前先收掉，否则新页面被压在下面（用户实测：开着详情点「查看并归档」没反应）。
        if (Preview.IsOpen) Preview.CloseImmediate();
        if (tag == "settings")
        {
            SearchBox.DefaultFocusAnchor = ContentHost;
            ShowContent(_settingsPage);
            RecordLocation("set:" + _settingsPage.CurrentSection);
            return;
        }
        if (tag == "review")
        {
            SearchBox.DefaultFocusAnchor = ContentHost;
            _reviewPage ??= CreateReviewPage();
            _reviewPage.Reload();
            ShowContent(_reviewPage);
            RecordLocation("nav:" + tag);
            return;
        }
        if (tag == "catalog")
        {
            if (Services.AppService.Catalog is null)
            {
                Nav.SelectedItem = Nav.MenuItems[0];
                return;
            }
            SearchBox.DefaultFocusAnchor = ContentHost;
            _catalogPage ??= new Controls.Catalog.CatalogPage(Services.AppService.Catalog);
            ShowContent(_catalogPage);
            RecordLocation("nav:" + tag);
            return;
        }
        SearchBox.DefaultFocusAnchor = _page.FocusAnchor;
        ShowContent(_page);
        switch (tag)
        {
            case "fav":
                _page.SetMode(true);
                break;
            case "all":
                _page.SetMode(false);
                _page.SetDirectory(null, null);
                break;
            default:
                if (tag.StartsWith("dir:", StringComparison.Ordinal))
                {
                    var parts = tag.Split(':', 3);
                    var rootId = int.Parse(parts[1]);
                    var dirRel = parts.Length > 2 ? parts[2] : "";
                    _page.SetMode(false);
                    _page.SetDirectory(rootId, dirRel);
                }
                break;
        }
        RecordLocation("nav:" + tag);
    }

    /// <summary>把一次真实切换记进历史；历史回放期间和重复位置都不记。</summary>
    private void RecordLocation(string location)
    {
        if (_replayingHistory) return;
        if (_navIndex >= 0 && _navIndex < _navHistory.Count && _navHistory[_navIndex] == location) return;
        if (_navIndex < _navHistory.Count - 1)
            _navHistory.RemoveRange(_navIndex + 1, _navHistory.Count - _navIndex - 1);
        _navHistory.Add(location);
        if (_navHistory.Count > NavHistoryLimit) _navHistory.RemoveAt(0);
        _navIndex = _navHistory.Count - 1;
    }

    /// <summary>沿历史前移／后移一步；没有可去的位置时返回 false。</summary>
    private bool NavigateHistory(int delta)
    {
        if (_replayingHistory) return false;
        var next = _navIndex + delta;
        while (next >= 0 && next < _navHistory.Count && !CanApplyLocation(_navHistory[next]))
            next += delta;
        if (next < 0 || next >= _navHistory.Count) return false;
        _navIndex = next;
        ApplyLocation(_navHistory[next]);
        return true;
    }

    private bool HandleHistoryInput(int delta)
    {
        if (Preview.IsOpen)
        {
            if (delta < 0) Preview.CloseFromNavigation();
            return true;
        }
        return NavigateHistory(delta);
    }

    private void OnNavigationPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(RootGrid).Properties.PointerUpdateKind switch
        {
            Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed => -1,
            Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed => 1,
            _ => 0,
        };
        if (delta == 0) return;
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!HasKeyboardPopup()) HandleHistoryInput(delta);
        });
    }

    private bool CanApplyLocation(string location)
    {
        if (location.StartsWith("set:", StringComparison.Ordinal)) return true;
        return location.StartsWith("nav:", StringComparison.Ordinal)
            && FindNavItem(Nav.MenuItems, location[4..]) is not null;
    }

    /// <summary>回放一个历史位置。期间所有切换都不再写入历史，避免自激。</summary>
    private void ApplyLocation(string location)
    {
        _replayingHistory = true;
        try
        {
            if (location.StartsWith("set:", StringComparison.Ordinal))
            {
                Nav.SelectedItem = _settingsNavigationItem;
                _settingsPage.ShowSection(location[4..]);
            }
            else if (location.StartsWith("nav:", StringComparison.Ordinal))
            {
                var tag = location[4..];
                if (tag == "all") Nav.SelectedItem = Nav.MenuItems[0];
                else if (FindNavItem(Nav.MenuItems, tag) is { } item) Nav.SelectedItem = item;
            }
        }
        finally { _replayingHistory = false; }
    }

    /// <summary>按 Tag 在导航树里找项；目录树会重建，所以每次回放都重新找。</summary>
    private static NavigationViewItem? FindNavItem(IList<object> items, string tag)
    {
        foreach (var entry in items)
        {
            if (entry is not NavigationViewItem item) continue;
            if (item.Tag is string value && value == tag) return item;
            if (FindNavItem(item.MenuItems, tag) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>审阅页「在图库中查看」：把窗口唤到前台并定位到当前文件。</summary>
    public System.Threading.Tasks.Task ShowInGalleryAsync(string path)
        => HandleUiRequestAsync("gallery", path);

    /// <summary>
    /// 浏览器扩展的 /ui/open：把窗口唤到前台并切到对应视图；
    /// 带 path 时定位到文件所在目录并直接打开查看器。
    /// </summary>
    private async System.Threading.Tasks.Task HandleUiRequestAsync(string view, string path)
    {
        if (_quitting) return;
        ActivateWindow();

        if (view is "pending" or "review")
        {
            Nav.SelectedItem = ReviewNavItem;
            return;
        }

        Nav.SelectedItem = Nav.MenuItems[0];
        if (path.Length == 0) return;

        var store = Services.AppService.Store;
        if (store is null) return;

        if (store.RootContaining(path) is not { } hit)
        {
            ShowToast("这个文件不在已登记的图库目录里：" + Path.GetFileName(path));
            return;
        }

        var dirRel = hit.Rel.Contains('/')
            ? hit.Rel[..hit.Rel.LastIndexOf('/')]
            : "";
        _page.SetMode(false);
        _page.SetDirectory(hit.RootId, dirRel);

        // SetDirectory 是异步重新查询的，等结果集装载完再定位，否则会误报「还没收录」
        var target = Path.GetFullPath(path);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (_quitting) return;
            if (_page.RevealPath(target)) return;
            await System.Threading.Tasks.Task.Delay(50);
        }

        ShowToast("图库还没收录这个文件：" + Path.GetFileName(path));
    }

    /// <summary>窗口最小化时先还原，再抢前台（浏览器点「在图库中查看」的感觉）。</summary>
    private void ActivateWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        SetForegroundWindow(hwnd);
        RefreshCaptionAfterTrayHide(hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>离开设置页（供设置页内的返回类交互使用，批次 4 收口）。</summary>
    public void LeaveSettings()
    {
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>
    /// 对应 photos 的 _navigationService.NavigateTo(typeof(SettingsPage))：
    /// 整页替换内容区，标题栏与左侧导航原地保留。
    /// </summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => Nav.SelectedItem = _settingsNavigationItem;

    // 页面留在同一棵可视树中，往返不触发图库卸载和图片重建。
    private void ShowContent(UIElement page)
    {
        _page.SetActive(ReferenceEquals(page, _page));
        if (!ContentHost.Children.Contains(page)) ContentHost.Children.Add(page);
        foreach (var child in ContentHost.Children)
            child.Visibility = ReferenceEquals(child, page) ? Visibility.Visible : Visibility.Collapsed;
        _reviewPage?.SetActive(ReferenceEquals(page, _reviewPage));
        // 归档审阅页自己铺一层不透明炭灰，比内容岛底色深，而内容区左边还留着 16 DIP 给拖拽把手，
        // 那段露出的是岛底色，看上去就是页面左缘一条灰缝。切到该页时把这段留白刷成同一底色，
        // 切走就还原（其它页是透明的，留白要和内容岛一致）。
        MainPanel.Background = ReferenceEquals(page, _reviewPage) ? _reviewPage?.SurfaceBrush : null;
    }

    private void ShowPage(int navIndex)
    {
        // 在卸载旧页前更新锚点；图库锚点位于快捷键路由内部。
        SearchBox.DefaultFocusAnchor = navIndex < 0 ? ContentHost : _page.FocusAnchor;
        if (navIndex < 0)
        {
            ShowContent(_settingsPage);
            return;
        }
        ShowContent(_page);
        _page.SetMode(navIndex == 1);
        // 离开设置页时同步导航选中态（photos 进设置页会清掉导航选中高亮）
        if (!ReferenceEquals(Nav.SelectedItem, Nav.MenuItems[navIndex]))
            Nav.SelectedItem = Nav.MenuItems[navIndex];
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => _page.SetSearch(SearchBox.Text);

    private void OnSearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen || HasKeyboardPopup()) return;
        SearchBox.FocusSearch();
        args.Handled = true;
    }

    /// <summary>
    /// F5 主动刷新：重扫当前所在目录后重画网格。停在「图库/全部」时扫全部已添加目录。
    /// 扫描与缺失判定都是根目录级的，所以文件夹里刷新的是它所在的根，避免把别的目录误标成缺失。
    /// </summary>
    private async void OnRefreshInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen || HasKeyboardPopup()) return;
        args.Handled = true;
        await RefreshLibraryAsync();
    }

    private bool _refreshing;

    private async Task RefreshLibraryAsync()
    {
        if (_refreshing || _quitting) return;
        if (AppService.Store is not { } store || AppService.Scanner is not { } scanner)
        {
            ShowToast("库未连接，无法刷新");
            return;
        }

        var (rootId, _) = _page.CurrentDirectory;
        var rootIds = rootId is int id
            ? [id]
            : store.Roots().Where(r => r.Enabled).Select(r => r.Id).ToArray();
        if (rootIds.Length == 0)
        {
            ShowToast("还没有图库目录，先添加文件夹");
            return;
        }

        _refreshing = true;
        ShowToast(rootId is null ? "正在刷新图库…" : "正在刷新当前目录…");
        try
        {
            var results = await Task.Run(() => rootIds
                .Select(scanId => scanner.ScanRoot(scanId, cancel: () => _quitting || AppService.IsShuttingDown))
                .ToArray());
            if (_quitting || AppService.IsShuttingDown) return;
            var scanned = results.Sum(r => r.Scanned);
            var added = results.Sum(r => r.Added);
            var updated = results.Sum(r => r.Updated);
            var missing = results.Sum(r => r.Missing);
            var error = results.SelectMany(r => r.Errors).FirstOrDefault();

            AppService.Recount();
            BuildDirectoryTree();
            _page.RefreshFromStore();

            var parts = new List<string> { $"扫描 {scanned} 个文件" };
            if (added > 0) parts.Add($"新增 {added}");
            if (updated > 0) parts.Add($"更新 {updated}");
            if (missing > 0) parts.Add($"移出 {missing}");
            ShowToast(error is null ? "已刷新：" + string.Join("，", parts)
                : "刷新完成，但有文件读不了：" + error,
                error is null ? ToastKind.Success : ToastKind.Pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            AppService.DebugLog("refresh failed: " + ex);
            ShowToast("刷新失败：" + ex.Message, ToastKind.Pending);
        }
        finally { _refreshing = false; }
    }

    /// <summary>photos 的「添加文件夹」：选目录 → 备份 → 事务入库 → 首扫。</summary>
    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        => await TrackFolderOperationAsync(AddFolderAsync);

    private readonly HashSet<Task> _folderOperations = [];

    private async Task TrackFolderOperationAsync(Func<Task> operation)
    {
        if (_quitting) return;
        var task = operation();
        _folderOperations.Add(task);
        try { await task; }
        catch (Exception ex)
        {
            AppService.DebugLog("folder operation failed: " + ex);
            ShowToast("文件夹操作失败：" + ex.Message, ToastKind.Pending);
        }
        finally { _folderOperations.Remove(task); }
    }

    private async Task AddFolderAsync()
    {
        if (AppService.Folders is null)
        {
            ShowToast("库未连接，无法添加目录");
            return;
        }
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            AddFolderButton.IsEnabled = false;
            ShowToast("正在扫描新目录…");
            var path = folder.Path;
            var outcome = await Task.Run(() =>
            {
                try
                {
                    var (root, scan) = AppService.Folders.AddRootAndScan(path);
                    AppService.Scanner?.Thumbs.PruneOrphans();
                    return AddRootOutcome.Ok(root, scan);
                }
                catch (Exception ex) { return AddRootOutcome.Fail(ex.Message); }
        });
        AddFolderButton.IsEnabled = true;
        if (outcome.Error is not null)
        {
            AppService.DebugLog("add-root failed: " + outcome.Error);
            ShowToast(outcome.Error, ToastKind.Pending);
            return;
        }
        ShowToast($"已添加：{Path.GetFileName(outcome.Root!.Path)}（{outcome.Scan!.Summary()}）", ToastKind.Success);
        AppService.Recount();
        AppService.Watcher?.Start();
        _page.RefreshFromStore();
        BuildDirectoryTree();
        }
        catch (Exception ex)
        {
            // 非打包应用的 FolderPicker 常见环境性失败：给出可见反馈而不是静默
            ShowToast($"打开文件夹选择器失败：{ex.Message}", ToastKind.Pending);
        }
    }

    /// <summary>「待审核」右侧的导入：选目录 → 选项弹窗 → 写入待归档队列。</summary>
    private async void ImportToReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppService.Store is null)
        {
            ShowToast("库未连接，无法导入");
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            var folder = await picker.PickSingleFolderAsync();
            AppService.DebugLog("import-review: picked=" + (folder?.Path ?? "<null>"));
            if (folder is null) return;

            var page = _reviewPage ??= CreateReviewPage();
            AppService.DebugLog("import-review: page xamlRoot="
                + (page.XamlRoot is null ? "<null>" : "ok"));
            Nav.SelectedItem = ReviewNavItem;
            AppService.DebugLog("import-review: navSelected="
                + ReferenceEquals(Nav.SelectedItem, ReviewNavItem)
                + " attached=" + ContentHost.Children.Contains(page)
                + " visible=" + (page.Visibility == Visibility.Visible)
                + " loaded=" + page.IsLoaded);
            await WaitUntilLoadedAsync(page);
            AppService.DebugLog("import-review: loaded=" + page.IsLoaded);
            await page.ImportAsync([folder.Path]);
            AppService.DebugLog("import-review: returned");
        }
        catch (Exception ex)
        {
            AppService.DebugLog("import-review: exception " + ex);
            ShowToast($"打开文件夹选择器失败：{ex.Message}", ToastKind.Pending);
        }
    }

    /// <summary>等元素真正进入可视树并完成首次排版：刚挂上去的页面立刻弹 ContentDialog 会卡住不显示。</summary>
    private static Task WaitUntilLoadedAsync(FrameworkElement element)
    {
        if (element.IsLoaded) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            element.Loaded -= OnLoaded;
            done.TrySetResult();
        }
        element.Loaded += OnLoaded;
        return done.Task;
    }

    private sealed record AddRootOutcome(RootInfo? Root, ScanResult? Scan, string? Error)    {
        public static AddRootOutcome Ok(RootInfo root, ScanResult scan) => new(root, scan, null);
        public static AddRootOutcome Fail(string error) => new(null, null, error);
    }

    private bool HasKeyboardPopup()
        => DialogService.IsShowing || RootGrid.XamlRoot is { } root
            && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.IsOpen && popup.Child is not ToolTip);

    private static bool IsTextKeyboardFocus(DependencyObject? focused)
    {
        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or SearchInputBox
                or TextBlock { IsTextSelectionEnabled: true }) return true;
        return false;
    }

    private async void OnGalleryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _page.Visibility != Visibility.Visible || Preview.IsOpen || HasKeyboardPopup()) return;
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            _page.HideKeyboardFocus();
            return;
        }
        if (IsTextKeyboardFocus(focused)
            || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        await _page.HandleGalleryKeyAsync(e, _page.HasContentKeyboardFocus(focused));
    }

    private void OnGalleryBlankPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _page.HideKeyboardFocus();
        if (_page.Visibility != Visibility.Visible || Preview.IsOpen
            || !e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
        // 窗口 XAML 内非交互空白（含卡片间隙、工具栏及侧栏留白）统一退出。
        for (var current = e.OriginalSource as DependencyObject; current is not null && current != RootGrid;
            current = VisualTreeHelper.GetParent(current))
        {
            if (current is PhotoTile or ButtonBase or TextBox or PasswordBox or AutoSuggestBox or SearchInputBox
                or RangeBase or ToggleSwitch or SelectorItem or NavigationViewItem or PaneResizer
                or TimelineScrollbar or ContinuousScrollBar or ContentDialog
                or TextBlock { IsTextSelectionEnabled: true }) return;
        }
        _page.ExitSelection();
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Esc 优先给查看器（退全屏/关闭），其次返回设置首页或退出图库多选。
        // 注意不能放在图库页面 Root 的 KeyDown 里：单击缩略图后焦点会落到弹层里的
        // ScrollViewer 上，页面收不到按键（实测），而窗口级加速器不受焦点位置影响。
        if (HasKeyboardPopup()) return;
        // 查看器先走：焦点停在详情里可选中文字上也要能关，输入框由 TryHandleKey 自己挡。
        if (Preview.IsOpen) { args.Handled = Preview.TryHandleKey(Windows.System.VirtualKey.Escape); return; }
        if (IsTextKeyboardFocus(FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject)) return;
        if (_settingsPage.Visibility == Visibility.Visible && _settingsPage.TryGoBack())
        {
            args.Handled = true;
            return;
        }
        if (_page.Visibility == Visibility.Visible && _page.ExitSelection()) args.Handled = true;
    }

    /// <summary>
    /// Alt+← / Alt+→：导航历史后退／前进。
    /// 走隧道阶段而不是 KeyboardAccelerator：图库已经用无修饰的 ← → 翻图，
    /// 两个同名加速键共存时无法保证按修饰键分流。
    /// </summary>
    private void OnNavHistoryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || HasKeyboardPopup()) return;
        if (e.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        if (!AltDown()) return;
        var moved = e.Key == Windows.System.VirtualKey.Left ? HandleHistoryInput(-1) : HandleHistoryInput(1);
        if (moved) e.Handled = true;
    }

    private static bool AltDown()
        => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnLeftInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.TryHandleKey(Windows.System.VirtualKey.Left)) args.Handled = true;
    }

    private void OnRightInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.TryHandleKey(Windows.System.VirtualKey.Right)) args.Handled = true;
    }

    /// <summary>查看器 Delete：焦点落在按钮/滑块/详情文字上也要生效（图库页自有多选删除，这里只认预览）。</summary>
    private void OnDeleteInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.TryHandleKey(Windows.System.VirtualKey.Delete)) args.Handled = true;
    }

    private void ShowToast(string message) => Toasts.Show(message, ToastKind.Info);

    private void ShowToast(string message, ToastKind kind, string? detail = null)
        => Toasts.Show(message, kind, detail);


    // ------------------------------------------------------------------ 目录树

    // 拖拽落点高亮：直接借用导航项自带的悬停视觉（PointerStates），同一时刻最多亮一项。
    private NavigationViewItem? _dragTargetItem;
    private readonly Dictionary<int, string> _directoryRootPaths = [];

    /// <summary>亮起/切换拖拽命中的目录项；传 null 表示全部熄灭。</summary>
    private void SetDragTarget(NavigationViewItem? item)
    {
        if (ReferenceEquals(_dragTargetItem, item)) return;
        if (_dragTargetItem is not null) SetItemPointerState(_dragTargetItem, pointerOver: false);
        _dragTargetItem = item;
        if (item is not null) SetItemPointerState(item, pointerOver: true);
    }

    /// <summary>只熄灭自己那项——父项 DragLeave 晚于子项 DragOver 时不会误灭子项。</summary>
    private void ClearDragTarget(NavigationViewItem item)
    {
        if (ReferenceEquals(_dragTargetItem, item)) SetDragTarget(null);
    }

    /// <summary>
    /// 照搬 NavigationViewItem::UpdateVisualStateForPointer 的状态选择：
    /// 悬停态与选中态同属 PointerStates 组，选中项要回 Selected 而不是 Normal。
    /// 状态组定义在 NavigationViewItemPresenter 的模板里，必须对 presenter 调 GoToState，
    /// 对 item 本身调无效（item 模板只有列表位置组）。
    /// </summary>
    private static void SetItemPointerState(NavigationViewItem item, bool pointerOver)
    {
        if (FindPresenter(item) is not { } presenter) return;
        var state = item.IsSelected
            ? pointerOver ? "PointerOverSelected" : "Selected"
            : pointerOver ? "PointerOver" : "Normal";
        VisualStateManager.GoToState(presenter, state, useTransitions: true);
    }

    private static NavigationViewItemPresenter? FindPresenter(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is NavigationViewItemPresenter presenter) return presenter;
            if (FindPresenter(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>重建目录树（挂在「收藏夹」之后的分隔线 + 各启用根）。</summary>
    private void BuildDirectoryTree()
    {
        var store = AppService.Store;
        if (store is null) return;
        var roots = store.Roots().Where(r => r.Enabled)
            .Select(root => (Root: root, Path: store.RootPath(root.Id)))
            .Where(pair => pair.Path is not null).ToArray();
        var wantedTags = roots.Select(pair => $"dir:{pair.Root.Id}:").ToHashSet(StringComparer.Ordinal);
        var wantedIds = roots.Select(pair => pair.Root.Id).ToHashSet();
        foreach (var staleId in _directoryRootPaths.Keys.Where(id => !wantedIds.Contains(id)).ToArray())
            _directoryRootPaths.Remove(staleId);
        for (var i = Nav.MenuItems.Count - 1; i >= 0; i--)
        {
            if (Nav.MenuItems[i] is not NavigationViewItemBase { Tag: string tag }) continue;
            if (tag.StartsWith("dir:", StringComparison.Ordinal) && !wantedTags.Contains(tag))
            {
                if (ReferenceEquals(_dragTargetItem, Nav.MenuItems[i])) _dragTargetItem = null;
                Nav.MenuItems.RemoveAt(i);
            }
        }
        var separator = Nav.MenuItems.OfType<NavigationViewItemSeparator>()
            .FirstOrDefault(item => Equals(item.Tag, "dirsep"));
        if (roots.Length == 0)
        {
            if (separator is not null) Nav.MenuItems.Remove(separator);
            return;
        }
        if (separator is null) Nav.MenuItems.Add(new NavigationViewItemSeparator { Tag = "dirsep" });
        foreach (var (root, rootPath) in roots)
        {
            var tag = $"dir:{root.Id}:";
            var existing = Nav.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(item => Equals(item.Tag, tag));
            if (existing is not null && _directoryRootPaths.TryGetValue(root.Id, out var oldPath)
                && !string.Equals(oldPath, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                if (ReferenceEquals(_dragTargetItem, existing)) _dragTargetItem = null;
                Nav.MenuItems.Remove(existing);
                existing = null;
            }
            var item = BuildDirNode(root.Id, "", store.Folders(root.Id), rootPath!, isRoot: true, existing: existing);
            if (existing is null) Nav.MenuItems.Add(item);
            _directoryRootPaths[root.Id] = rootPath!;
        }
    }

    /// <summary>
    /// 递归构建目录节点（计数 = 自身 + 子树合计，照搬 galleryold）。
    /// 关键：无图的中间目录也要建节点，否则层级断裂（testimages/sub 教训）。
    /// </summary>
    private NavigationViewItem BuildDirNode(
        int rootId, string dirRel, List<FolderCount> folders, string rootPath, bool isRoot,
        NavigationViewItem? existing = null)
    {
        var prefix = dirRel.Length > 0 ? dirRel + "/" : "";
        var own = folders.FirstOrDefault(f => f.DirRel == dirRel)?.Count ?? 0;

        // 直接子前缀：从任一 dir_rel 截出下一层（含无图的中间层）
        var childPrefixes = folders
            .Select(f => f.DirRel)
            .Where(d => d.StartsWith(prefix, StringComparison.Ordinal) && d.Length > prefix.Length)
            .Select(d =>
            {
                var rest = d[prefix.Length..];
                var cut = rest.IndexOf('/');
                return cut < 0 ? rest : rest[..cut];
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var childRels = childPrefixes.Select(c => prefix + c).ToList();
        int total = own + childRels.Sum(full => SubtreeCount(folders, full));

        var display = isRoot
            ? Path.GetFileName(rootPath.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : rootPath
            : dirRel[(dirRel.LastIndexOf('/') + 1)..];

        if (existing is not null)
        {
            var count = (TextBlock)((Grid)existing.Content).Children[1];
            count.Text = total > 0 ? total.ToString() : "";
            var wanted = childRels.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();
            var wantedTags = wanted.Select(rel => $"dir:{rootId}:{rel}").ToHashSet(StringComparer.Ordinal);
            for (var i = existing.MenuItems.Count - 1; i >= 0; i--)
            {
                if (existing.MenuItems[i] is not NavigationViewItem { Tag: string tag } child
                    || wantedTags.Contains(tag)) continue;
                if (ReferenceEquals(_dragTargetItem, child)) _dragTargetItem = null;
                existing.MenuItems.RemoveAt(i);
            }
            foreach (var childRel in wanted)
            {
                var tag = $"dir:{rootId}:{childRel}";
                var child = existing.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(item => Equals(item.Tag, tag));
                if (child is null) existing.MenuItems.Add(BuildDirNode(rootId, childRel, folders, rootPath, isRoot: false));
                else BuildDirNode(rootId, childRel, folders, rootPath, isRoot: false, existing: child);
            }
            return existing;
        }

        var content = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var nameText = new TextBlock
        {
            Text = display,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (isRoot) nameText.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        content.Children.Add(nameText);
        var countText = new TextBlock
        {
            Text = total > 0 ? total.ToString() : "",
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countText, 1);
        content.Children.Add(countText);

        var item = new NavigationViewItem
        {
            Tag = $"dir:{rootId}:{dirRel}",
            Content = content,
            Icon = new FontIcon { Glyph = "\uE8B7", FontSize = 16 },
            IsExpanded = false,
        };
        var target = dirRel.Length > 0 ? Path.Combine(rootPath, dirRel.Replace('/', '\\')) : rootPath;
        item.ContextFlyout = FolderFlyout(rootId, dirRel, target, isRoot);

        // 应用内拖拽：把图库里的图片拖到侧栏目录即移动；外部文件拖入不接收。
        item.AllowDrop = true;
        item.DragOver += (_, e) => OnDirectoryDragOver(e, display, item);
        item.DragLeave += (_, _) => ClearDragTarget(item);
        item.Drop += (_, e) =>
        {
            ClearDragTarget(item);
            _ = OnDirectoryDropAsync(e, target, display);
        };

        foreach (var childRel in childRels.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            item.MenuItems.Add(BuildDirNode(rootId, childRel, folders, rootPath, isRoot: false));
        return item;
    }

    private static int SubtreeCount(List<FolderCount> folders, string prefix)
        => folders
            .Where(f => f.DirRel == prefix ||
                        f.DirRel.StartsWith(prefix + "/", StringComparison.Ordinal))
            .Sum(f => f.Count);

    /// <summary>只接受图库自己发起的拖拽；显示「移动到 目录名」，不显示复制图标。</summary>
    private void OnDirectoryDragOver(DragEventArgs e, string displayName, NavigationViewItem item)
    {
        if (!e.DataView.Contains(GalleryPage.DragImageIdsFormat)) return;
        SetDragTarget(item);
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"移动到「{displayName}」";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
        e.Handled = true;
    }

    /// <summary>侧栏目录落点：解析拖拽图片 ID，交给图库页做确认与批量移动。</summary>
    private async Task OnDirectoryDropAsync(DragEventArgs e, string target, string displayName)
    {
        if (!e.DataView.Contains(GalleryPage.DragImageIdsFormat)) return;
        e.Handled = true;
        var deferral = e.GetDeferral();
        string? raw;
        try { raw = await e.DataView.GetDataAsync(GalleryPage.DragImageIdsFormat) as string; }
        finally { deferral.Complete(); }
        var ids = (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return;
        await _page.MoveImagesToFolderAsync(ids, target, displayName);
    }

    /// <summary>
    /// 照搬 photos 的 FolderGalleryNavigationViewItem 菜单语义：
    /// 根目录（IsRemovable）→「移除文件夹」（从图库移除，磁盘文件保留）；
    /// 子目录 →「删除文件夹」（移到回收站）。
    /// </summary>
    private MenuFlyout FolderFlyout(int rootId, string dirRel, string target, bool isRoot)
    {
        var flyout = new MenuFlyout();
        var create = new MenuFlyoutItem
        {
            Text = "创建文件夹",
            Icon = new FontIcon { Glyph = "\uE8F4" }, // FolderCreateGlyph
        };
        create.Click += (_, _) => _ = CreateChildFolderAsync(rootId, dirRel);
        var open = new MenuFlyoutItem
        {
            Text = "在文件资源管理器中打开",
            Icon = new FontIcon { Glyph = "\uEC50" }, // FolderInFileExplorerGlyph
        };
        open.Click += (_, _) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        flyout.Items.Add(create);
        flyout.Items.Add(open);

        if (isRoot)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem
            {
                Text = "移除文件夹",
                Icon = new FontIcon { Glyph = "\uECC9" }, // FolderRemoveGlyph
            };
            remove.Click += (_, _) => _ = RemoveFolderAsync(rootId, target);
            flyout.Items.Add(remove);
        }
        else
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var delete = new MenuFlyoutItem
            {
                Text = "删除文件夹",
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x99, 0xA4)),
                Icon = new FontIcon { Glyph = "\uE74D" }, // FolderDeleteGlyph
            };
            delete.Click += (_, _) => _ = DeleteFolderAsync(rootId, dirRel, target);
            flyout.Items.Add(delete);
        }
        return flyout;
    }

    /// <summary>photos 的 RemoveFolderDialog 语义：从图库移除，磁盘文件不受影响。</summary>
    private async Task RemoveFolderAsync(int rootId, string target)
        => await TrackFolderOperationAsync(() => RemoveFolderCoreAsync(rootId, target));

    private async Task RemoveFolderCoreAsync(int rootId, string target)
    {
        var dialog = new ContentDialog
        {
            Title = "移除文件夹",
            Content = $"将从图库移除“{Path.GetFileName(target)}”。\n磁盘上的文件夹与图片不受影响。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await Gallery.Services.DialogService.ShowAsync(dialog);
        if (result != ContentDialogResult.Primary) return;
        var error = await Task.Run(() =>
        {
            lock (AppService.DbLock)
            {
                try
                {
                    AppService.Folders!.RemoveRoot(rootId, forget: true);
                    return (string?)null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            }
        });
        if (error is not null)
        {
            ShowToast(error, ToastKind.Pending);
            return;
        }
        BuildDirectoryTree();
        AppService.Recount();
        AppService.Watcher?.Start();
        _page.SetDirectory(null, null, force: true); // 目录没变（停在「图库/全部」）也要重建：被移除目录的图要立刻消失
        Nav.SelectedItem = Nav.MenuItems[0];
        ShowToast($"已从图库移除：{Path.GetFileName(target)}（磁盘文件保留）", ToastKind.Success);
    }

    private async Task CreateChildFolderAsync(int rootId, string parentRel)
        => await TrackFolderOperationAsync(() => CreateChildFolderCoreAsync(rootId, parentRel));

    private async Task CreateChildFolderCoreAsync(int rootId, string parentRel)
    {
        var input = new TextBox { PlaceholderText = "文件夹名称" };
        var dialog = new ContentDialog
        {
            Title = "创建文件夹",
            Content = input,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await Gallery.Services.DialogService.ShowAsync(dialog);
        if (result != ContentDialogResult.Primary || input.Text.Trim().Length == 0) return;
        var name = input.Text.Trim();
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowToast("创建失败：名称包含非法字符", ToastKind.Pending);
            return;
        }
        var store = AppService.Store!;
        var rootPath = store.RootPath(rootId)!;
        var parent = parentRel.Length > 0 ? Path.Combine(rootPath, parentRel.Replace('/', '\\')) : rootPath;
        var target = Path.Combine(parent, name);
        try
        {
            Directory.CreateDirectory(target);
            await Task.Run(() => AppService.Scanner!.ScanRoot(rootId));
            BuildDirectoryTree();
            ShowToast($"已创建文件夹：{name}", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"创建失败：{ex.Message}", ToastKind.Pending);
        }
    }

    private async Task DeleteFolderAsync(int rootId, string dirRel, string target)
        => await TrackFolderOperationAsync(() => DeleteFolderCoreAsync(rootId, dirRel, target));

    private async Task DeleteFolderCoreAsync(int rootId, string dirRel, string target)
    {
        var dialog = new ContentDialog
        {
            Title = "删除文件夹",
            Content = $"将文件夹“{Path.GetFileName(target)}”及其中全部内容移到回收站？\n{target}",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Secondary, // 取消为默认
            XamlRoot = Content.XamlRoot,
        };
        var result = await Gallery.Services.DialogService.ShowAsync(dialog);
        if (result != ContentDialogResult.Primary) return;
        var ok = await Task.Run(() => FileActions.DeleteToRecycle(target));
        if (!ok)
        {
            ShowToast("删除失败", ToastKind.Pending);
            return;
        }
        await Task.Run(() => AppService.Scanner!.ScanRoot(rootId)); // mark_missing
        BuildDirectoryTree();
        _page.SetDirectory(null, null);
        Nav.SelectedItem = Nav.MenuItems[0];
        ShowToast($"已删除文件夹：{Path.GetFileName(target)}", ToastKind.Success);
    }

}
