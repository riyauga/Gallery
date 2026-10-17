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
            ContentHost.Children.Clear();
            ContentHost.Children.Add(_settingsPage);
            return;
        }
        if (ContentHost.Children.Count > 0 && ContentHost.Children[^1] == _settingsPage)
        {
            // 从设置页切回内容页
            SearchBox.DefaultFocusAnchor = _page.FocusAnchor;
            ContentHost.Children.Clear();
            ContentHost.Children.Add(_page);
        }
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

    private void ShowPage(int navIndex)
    {
        // 在卸载旧页前更新锚点；图库锚点位于快捷键路由内部。
        SearchBox.DefaultFocusAnchor = navIndex < 0 ? ContentHost : _page.FocusAnchor;
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
