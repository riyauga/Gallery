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
        if (_reviewBadge is null)
        {
            _reviewBadge = new InfoBadge();
            ReviewNavItem.InfoBadge = _reviewBadge;
        }

        _reviewBadge.Value = pending;
        _reviewBadge.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 待审核数量保留为文字和侧栏 InfoBadge，不改写系统图标。
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
    private void UpdateTitleBarRegions()
    {
        if (AppWindowInstance is not { } window) return;
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var rects = new List<Windows.Graphics.RectInt32>();

        void Add(FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible) return;
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var bounds = element.TransformToVisual(null)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            // 实测：SetRegionRects 收的是 DIP（不是物理像素），乘 RasterizationScale 会偏到窗口外
            rects.Add(new Windows.Graphics.RectInt32(
                (int)Math.Round(bounds.X),
                (int)Math.Round(bounds.Y),
                (int)Math.Round(bounds.Width),
                (int)Math.Round(bounds.Height)));
        }

        Add(SearchBox);
        Add(ReviewChipButton);
        Add(SettingsButton);

        if (rects.Count == 0) return;
        try
        {
            var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(window.Id);
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
    private bool _restoreTitleBarAfterTrayHide;

    /// <summary>主窗口的 AppWindow（托盘唤回、关窗收托盘都要用）。</summary>
    public Microsoft.UI.Windowing.AppWindow? AppWindowInstance { get; private set; }

    /// <summary>从托盘唤回：窗口可能是隐藏状态，先 Show 再 Activate。</summary>
    public void BringToFront()
    {
        if (AppWindowInstance is { } window)
        {
            RestoreTitleBarAfterTrayHide();
            window.Show();
            if (window.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                presenter.Restore();
        }

        Activate();
        Services.AppService.DebugLog("brought to front");
    }

    /// <summary>在窗口仍隐藏时重新启用自定义标题栏，避免沿用关闭按钮的红色交互状态。</summary>
    private void RestoreTitleBarAfterTrayHide()
    {
        if (!_restoreTitleBarAfterTrayHide) return;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindowInstance!.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        UpdateTitleBarRegions();
        _restoreTitleBarAfterTrayHide = false;
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
        _ingestCard?.Dismiss();
        RootGrid.IsHitTestVisible = false;
        _tray?.UpdateTip("Gallery · 正在完成保存并退出");
        try
        {
            await _settingsPage.StopBackgroundWorkAsync();
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
    private InfoBadge? _reviewBadge;

    /// <summary>当前窗口实例（查看器进全屏时需要暂时隐藏应用标题栏）。</summary>
    public static MainWindow? Instance { get; private set; }

    /// <summary>查看器全屏：隐藏 48 高标题栏行，让查看器占满窗口（返回时恢复）。</summary>
    public void SetTitleBarHidden(bool hidden)
    {
        TitleBar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        RootGrid.RowDefinitions[0].Height = new GridLength(hidden ? 0 : 48);
    }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        Nav.MenuItems.Add(_settingsNavigationItem);
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
            appWindow.TitleBar.PreferredHeightOption =
                Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

            // 关窗行为对齐 galleryold：minimize_to_tray 时收进托盘而不是退出
            appWindow.Closing += (sender, e) =>
            {
                if (_quitting) return;
                e.Cancel = true;
                if (AppService.Settings.Settings.minimize_to_tray && _tray is { IsCreated: true })
                {
                    appWindow.Hide();
                    // WinUI #12232：自定义标题栏在 Hide/Show 后可能保留关闭按钮高亮。
                    // 隐藏期间退出自定义模式，唤回前重新启用；不改变可见时的标题栏外观。
                    SetTitleBar(null);
                    ExtendsContentIntoTitleBar = false;
                    _restoreTitleBarAfterTrayHide = true;
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

        AppService.Init();
        AppService.MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
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
        Preview.ReturnFocusRequested += () => SearchBox.DefaultFocusAnchor?.Focus(FocusState.Programmatic);
        Preview.DeleteRequested = _page.DeleteAsync;
        Preview.RenameRequested = _page.RenameAsync;
        Preview.ContextMenuBuilder = item => _page.BuildContextMenu(item, includeOpen: false);
        ContentHost.Children.Add(_page);
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
            RootGrid.SizeChanged += (_, _) => UpdateTitleBarRegions();
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
            AppService.Recount();
            BuildDirectoryTree();
            _page.RefreshFromStore();
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
        if (tag == "settings")
        {
            SearchBox.DefaultFocusAnchor = ContentHost;
            ShowContent(_settingsPage);
            return;
        }
        if (tag == "review")
        {
            SearchBox.DefaultFocusAnchor = ContentHost;
            _reviewPage ??= CreateReviewPage();
            _reviewPage.Reload();
            ShowContent(_reviewPage);
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
    }

    /// <summary>
    /// 浏览器扩展的 /ui/open：把窗口唤到前台并切到对应视图；
    /// 带 path 时定位到文件所在目录并直接打开查看器。
    /// </summary>
    private async System.Threading.Tasks.Task HandleUiRequestAsync(string view, string path)
    {
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
            if (_page.RevealPath(target)) return;
            await System.Threading.Tasks.Task.Delay(50);
        }

        ShowToast("图库还没收录这个文件：" + Path.GetFileName(path));
    }

    /// <summary>窗口最小化时先还原，再抢前台（浏览器点「在图库中查看」的感觉）。</summary>
    private void ActivateWindow()
    {
        RestoreTitleBarAfterTrayHide();
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
        _reviewPage?.SetActive(ReferenceEquals(page, _reviewPage));
        if (!ContentHost.Children.Contains(page)) ContentHost.Children.Add(page);
        foreach (var child in ContentHost.Children)
            child.Visibility = ReferenceEquals(child, page) ? Visibility.Visible : Visibility.Collapsed;
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => _page.SetSearch(SearchBox.Text);

    private void OnSearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen) return;
        SearchBox.FocusSearch();
        args.Handled = true;
    }

    /// <summary>photos 的「添加文件夹」：选目录 → 备份 → 事务入库 → 首扫。</summary>
    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
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

    private sealed record AddRootOutcome(RootInfo? Root, ScanResult? Scan, string? Error)
    {
        public static AddRootOutcome Ok(RootInfo root, ScanResult scan) => new(root, scan, null);
        public static AddRootOutcome Fail(string error) => new(null, null, error);
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Esc 优先给查看器（退全屏/关闭），其次退出图库多选。
        // 注意不能放在图库页面 Root 的 KeyDown 里：单击缩略图后焦点会落到弹层里的
        // ScrollViewer 上，页面收不到按键（实测），而窗口级加速器不受焦点位置影响。
        if (Preview.IsOpen) { Preview.HandleKey(Windows.System.VirtualKey.Escape); args.Handled = true; return; }
        if (_page.ExitSelection()) args.Handled = true;
    }

    private void OnLeftInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen) { Preview.HandleKey(Windows.System.VirtualKey.Left); args.Handled = true; }
    }

    private void OnRightInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen) { Preview.HandleKey(Windows.System.VirtualKey.Right); args.Handled = true; }
    }

    private void ShowToast(string message) => Toasts.Show(message, ToastKind.Info);

    private void ShowToast(string message, ToastKind kind, string? detail = null)
        => Toasts.Show(message, kind, detail);


    // ------------------------------------------------------------------ 目录树

    /// <summary>重建目录树（挂在「收藏夹」之后的分隔线 + 各启用根）。</summary>
    private void BuildDirectoryTree()
    {
        // 注意：NavigationViewItemSeparator 继承 NavigationViewItemBase 而非
        // NavigationViewItem——用 is NavigationViewItem 匹配会漏掉分隔线，导致
        // 每次重建目录树都累积一条横线（实测 bug）。
        for (var i = Nav.MenuItems.Count - 1; i >= 0; i--)
            if (Nav.MenuItems[i] is NavigationViewItemBase item &&
                item.Tag is string t && (t.StartsWith("dir:", StringComparison.Ordinal) || t == "dirsep"))
                Nav.MenuItems.RemoveAt(i);

        var store = AppService.Store;
        if (store is null) return;
        // 照搬 photos：整个目录树区上方只一条分隔线
        bool separatorAdded = false;
        foreach (var root in store.Roots().Where(r => r.Enabled))
        {
            var rootPath = store.RootPath(root.Id);
            if (rootPath is null) continue;
            if (!separatorAdded)
            {
                Nav.MenuItems.Add(new NavigationViewItemSeparator { Tag = "dirsep" });
                separatorAdded = true;
            }
            var folders = store.Folders(root.Id);
            var item = BuildDirNode(root.Id, "", folders, rootPath, isRoot: true);
            Nav.MenuItems.Add(item);
        }
    }

    /// <summary>
    /// 递归构建目录节点（计数 = 自身 + 子树合计，照搬 galleryold）。
    /// 关键：无图的中间目录也要建节点，否则层级断裂（testimages/sub 教训）。
    /// </summary>
    private NavigationViewItem BuildDirNode(
        int rootId, string dirRel, List<FolderCount> folders, string rootPath, bool isRoot)
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

        foreach (var childRel in childRels.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            item.MenuItems.Add(BuildDirNode(rootId, childRel, folders, rootPath, isRoot: false));
        return item;
    }

    private static int SubtreeCount(List<FolderCount> folders, string prefix)
        => folders
            .Where(f => f.DirRel == prefix ||
                        f.DirRel.StartsWith(prefix + "/", StringComparison.Ordinal))
            .Sum(f => f.Count);

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
        var result = await dialog.ShowAsync();
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
        _page.SetDirectory(null, null);
        Nav.SelectedItem = Nav.MenuItems[0];
        ShowToast($"已从图库移除：{Path.GetFileName(target)}（磁盘文件保留）", ToastKind.Success);
    }

    private async Task CreateChildFolderAsync(int rootId, string parentRel)
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
        var result = await dialog.ShowAsync();
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
            lock (AppService.DbLock)
                AppService.Scanner!.ScanRoot(rootId);
            BuildDirectoryTree();
            ShowToast($"已创建文件夹：{name}", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"创建失败：{ex.Message}", ToastKind.Pending);
        }
    }

    private async Task DeleteFolderAsync(int rootId, string dirRel, string target)
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
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        var ok = await Task.Run(() => FileActions.DeleteToRecycle(target));
        if (!ok)
        {
            ShowToast("删除失败", ToastKind.Pending);
            return;
        }
        lock (AppService.DbLock)
            AppService.Scanner!.ScanRoot(rootId); // mark_missing
        BuildDirectoryTree();
        _page.SetDirectory(null, null);
        Nav.SelectedItem = Nav.MenuItems[0];
        ShowToast($"已删除文件夹：{Path.GetFileName(target)}", ToastKind.Success);
    }

}
