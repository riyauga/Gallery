using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Gallery.Controls;
using Gallery.Models;
using Gallery.Services;
using Windows.System;
using Windows.UI.Core;

namespace Gallery.Views;

public sealed partial class GalleryPage : UserControl
{
    private enum SortKey { DateTaken, DateCreated, DateModified, Name }
    private enum FilterKind { All, Photos, Videos }

    private readonly Library _library;
    private readonly List<PhotoItem> _items = new();
    private readonly HashSet<PhotoTile> _selected = new();

    private bool _favoritesMode;
    private bool _selectMode; // photos 选择模式（工具栏「选择」切换）
    private SortKey _sort = SortKey.DateTaken;          // manifest 路径用
    private string _sortKey = "taken";                   // 真实数据路径（galleryold 键）
    private bool _ascending = false;
    private FilterKind _filter = FilterKind.All;
    private string _query = string.Empty;
    private int? _dirRootId;                             // 目录树选择（null=全部）
    private string? _dirRel;

    private double _lastWidth;
    private int _anchorIndex;
    private double _anchorDelta;
    private bool? _lastTimelineVisible;
    private bool? _lastDefaultBar;

    public event Action<string>? ToastRequested;
    public event Action<string, Gallery.Controls.ToastKind, string?>? ToastKindRequested = null!;

    /// <summary>带类型的通知（Info/Success/Pending），MainWindow 转 ToastHost。</summary>
    public void SendToast(string message, Gallery.Controls.ToastKind kind,
        string? detail = null) => ToastKindRequested?.Invoke(message, kind, detail);
    public sealed record PreviewRequest(
        PhotoItem Item, IReadOnlyList<PhotoItem> Items,
        Windows.Foundation.Rect SourceRect, bool FullyVisible);
    public event Action<PreviewRequest>? PreviewRequested;

    public GalleryPage(Library library)
    {
        _library = library;
        InitializeComponent();

        Scroller.ViewChanged += (_, _) =>
        {
            if (_items.Count == 0) return;
            _anchorIndex = Panel.FirstVisibleIndex(Scroller.VerticalOffset);
            _anchorDelta = Panel.TopOfIndex(_anchorIndex) - Scroller.VerticalOffset;
            UpdateDateHeader();
            if (Panel.IsVirtual) ReconcileViewport();
        };

        Scroller.SizeChanged += (_, e) =>
        {
            UpdateScrollbarType();
            if (_items.Count == 0) return;
            if (Math.Abs(e.NewSize.Width - _lastWidth) < 0.5) return;
            _lastWidth = e.NewSize.Width;
            int index = _anchorIndex;
            double delta = _anchorDelta;
            DispatcherQueue.TryEnqueue(() =>
            {
                Panel.InvalidateMeasure();
                Scroller.UpdateLayout();
                Scroller.ChangeView(null, Math.Max(0, Panel.TopOfIndex(index) - delta), null, true);
            });
        };

        Loaded += (_, _) =>
        {
            _lastWidth = Scroller.ActualWidth;
            Rebuild();
        };

        Panel.SizeChanged += (_, _) =>
        {
            if (IsLoaded) UpdateScrollbarType();
        };

        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            if (IsLoaded) Rebuild();
        };
    }

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    /// <summary>galleryold：搜索 160ms 防抖（标签/中文别名/文件名）。</summary>
    public void SetSearch(string query)
    {
        _query = (query ?? string.Empty).Trim();
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    public void SetMode(bool favorites)
    {
        if (_favoritesMode == favorites && _items.Count > 0) return;
        _favoritesMode = favorites;
        _selected.Clear();
        if (!IsLoaded) return;
        _ = FadeSwapAsync();
    }

    private async Task FadeSwapAsync()
    {
        await AnimateOpacityAsync(0, 30);
        Rebuild();
        await AnimateOpacityAsync(1, 20);
    }

    private Task AnimateOpacityAsync(double to, int ms)
    {
        var tcs = new TaskCompletionSource();
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, Root);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Completed += (_, _) => tcs.SetResult();
        sb.Begin();
        return tcs.Task;
    }

    private IEnumerable<PhotoItem> BuildSource()
    {
        IEnumerable<PhotoItem> src = _library.All;
        if (_favoritesMode) src = src.Where(i => i.Favorite);
        if (!string.IsNullOrEmpty(_query))
            src = src.Where(i => i.Name.Contains(_query, StringComparison.OrdinalIgnoreCase));
        src = _filter switch
        {
            FilterKind.Photos => src.Where(i => i.PixelWidth > 0),
            FilterKind.Videos => Array.Empty<PhotoItem>(),
            _ => src
        };

        src = _sort switch
        {
            SortKey.Name => _ascending ? src.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                                       : src.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => _ascending ? src.OrderBy(i => i.Date) : src.OrderByDescending(i => i.Date)
        };
        return src;
    }

    private void Rebuild()
    {
        _items.Clear();
        _items.AddRange(BuildSource());
        _selected.Clear();

        // 批次 1：真实数据（库就绪）走虚拟化 + Query；manifest 示例图保持原路径
        bool virtualMode = AppService.DbReady && AppService.Store is not null;
        SyncSortMenuChecks();
        if (virtualMode)
        {
            _items.Clear();
            _items.AddRange(LoadRealItems());
        }

        _selected.Clear();
        if (virtualMode)
        {
            Panel.SetVirtualSource(_items.Select(i => i.AspectRatio).ToList());
            Panel.InvalidateMeasure();
            Scroller.UpdateLayout();
            Scroller.ChangeView(null, 0, null, true);
            ReconcileViewport();
        }
        else
        {
            Panel.ClearVirtualSource();
            Panel.Children.Clear();
            foreach (var item in _items)
            {
                var tile = new PhotoTile(item);
                WireTile(tile, item);
                Panel.Children.Add(tile);
            }
            Panel.InvalidateMeasure();
            Scroller.UpdateLayout();
            Scroller.ChangeView(null, 0, null, true);
        }

        Timeline.IsAscending = _ascending;
        UpdateScrollbarType();
        Timeline.Attach(Scroller, _items, Panel);
        UpdateDateHeader();
        UpdateHeader();
        UpdateEmptyState();
    }

    /// <summary>批次 1：真实数据走 StoreService.Query（目录/收藏/搜索/8 键排序）。</summary>
    private IEnumerable<PhotoItem> LoadRealItems()
    {
        var store = AppService.Store;
        if (store is null) yield break;
        var rows = store.Query(new StoreService.QuerySpec
        {
            RootId = _dirRootId,
            DirRel = _dirRel,
            Recursive = AppService.Settings.Settings.recursive,
            FavoritesOnly = _favoritesMode,
            Search = _query,
            Sort = _sortKey,
            Desc = !_ascending,
        });
        var favorites = store.FavoriteIds();
        foreach (var row in rows)
        {
            if (_filter == FilterKind.Videos) yield break;
            DateTime date = row.TakenAt is { } t
                ? DateTime.UnixEpoch.AddSeconds(t).ToLocalTime()
                : DateTime.UnixEpoch.AddSeconds(row.Mtime).ToLocalTime();
            yield return new PhotoItem
            {
                File = row.Name,
                FullPath = row.AbsPath,
                PixelWidth = row.Width ?? 0,
                PixelHeight = row.Height ?? 0,
                Date = date,
                Favorite = favorites.Contains(row.Id),
                ImageId = row.Id,
                RootId = row.RootId,
                DirRel = row.DirRel,
                RelPath = row.RelPath,
                TakenAtUnix = row.TakenAt,
                CreatedAtUnix = row.Ctime,
                ModifiedAtUnix = row.Mtime,
                Rating = row.Rating,
                Nsfw = row.Nsfw,
                FileSize = row.Size,
                PageUrl = row.PageUrl,
                SourceUrl = row.SourceUrl,
                Tags = row.Tags,
            };
        }
    }

    /// <summary>目录树选择（null = 全部图库）。</summary>
    public void SetDirectory(int? rootId, string? dirRel)
    {
        _dirRootId = rootId;
        _dirRel = dirRel;
        if (IsLoaded) Rebuild();
    }

    private void WireTile(PhotoTile tile, PhotoItem item)
    {
        tile.Tapped += OnTileTapped;
        tile.DoubleTapped += OnTileDoubleTapped;
        tile.RightTapped += OnTileRightTapped;
        FlyoutBase.SetAttachedFlyout(tile, BuildContextMenu(item));
        tile.SelectMode = _selectMode;

        // 拖拽外发（galleryold drag_out）：按下起手、位移超阈值发起；
        // 拖已选中图 = 拖整个选区；Windows 标记复制语义（不搬移）。
        Windows.Foundation.Point? dragStart = null;
        tile.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
                dragStart = e.GetCurrentPoint(tile).Position;
        };
        tile.PointerMoved += async (_, e) =>
        {
            if (dragStart is null) return;
            var pos = e.GetCurrentPoint(tile).Position;
            var dist = Math.Abs(pos.X - dragStart.Value.X) + Math.Abs(pos.Y - dragStart.Value.Y);
            if (dist < 12) return; // galleryold 用系统 startDragDistance，近似 12
            dragStart = null;
            await StartDragOutAsync(tile, e.GetCurrentPoint(tile));
        };
        tile.PointerReleased += (_, _) => dragStart = null;
    }

    /// <summary>拖拽外发：文件 URI 交给系统（CF_HDROP），复制语义防同盘搬移。</summary>
    private async System.Threading.Tasks.Task StartDragOutAsync(PhotoTile tile, Microsoft.UI.Input.PointerPoint start)
    {
        var items = new List<PhotoItem>();
        if (tile.Selected && _selected.Count > 0)
            items.AddRange(_selected.Select(t => t.Item));
        else
            items.Add(tile.Item);

        var storageFiles = new List<Windows.Storage.StorageFile>();
        foreach (var it in items)
        {
            try
            {
                storageFiles.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(it.FullPath));
            }
            catch (Exception ex) when (ex is IOException or System.IO.FileNotFoundException
                or UnauthorizedAccessException)
            {
                SendToast($"{it.Name}：文件已不在原位，无法拖出", Gallery.Controls.ToastKind.Pending);
                return;
            }
        }

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage
        { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
        dp.SetStorageItems(storageFiles);
        // NSFW 模糊保护中拒绝拖出：nsfw 分级数据在批次 3 接入后补判断（nsfw_display=blur 时）
        await tile.StartDragAsync(start);
    }

    /// <summary>
    /// 虚拟化视口对账：视口 ± 缓冲区内的索引才有瓦片，其余回收。
    /// 选中/事件按数据索引走，不依赖控件实例存活。
    /// </summary>
    private void ReconcileViewport()
    {
        if (!Panel.IsVirtual) return;
        double vh = Scroller.ViewportHeight;
        if (vh <= 0) vh = Scroller.ActualHeight;
        if (vh <= 0) return;
        var (first, last) = Panel.VisibleRange(Scroller.VerticalOffset, vh, 800);
        var byIndex = new Dictionary<int, PhotoTile>();
        foreach (var child in Panel.Children.OfType<PhotoTile>())
        {
            var idx = Panel.IndexOfTile(child);
            if (idx >= 0) byIndex[idx] = child;
        }

        for (int i = first; i <= last && i < _items.Count; i++)
        {
            if (byIndex.TryGetValue(i, out var existing))
            {
                byIndex.Remove(i);
                continue;
            }
            var tile = new PhotoTile(_items[i]);
            WireTile(tile, _items[i]);
            Panel.PlaceTile(tile, i);
        }

        // 视口外的回收：置 Detached（取消在途异步贴图）后移出面板
        foreach (var stale in byIndex.Values)
        {
            stale.Detached = true;
            Panel.RemoveTile(stale);
        }
    }

    /// <summary>
    /// 对应 Photos 的 GalleryViewModel.EvaluateScrollbarType + ShouldShowDateRelatedUI：
    /// 未加载 / 不可滚动 → 两个都不显示；名字排序（非日期维度）→ 系统默认滚动条；
    /// 日期排序且可滚动 → 时间线。
    /// </summary>
    private void UpdateScrollbarType()
    {
        bool loaded = _items.Count > 0 && IsLoaded;
        bool scrollable = loaded && Panel.ActualHeight > Scroller.ViewportHeight + 0.5;
        bool dateSort = _sort != SortKey.Name;
        bool timeline = scrollable && dateSort;
        bool defaultBar = scrollable && !dateSort;

        if (_lastTimelineVisible == timeline && _lastDefaultBar == defaultBar) return;
        _lastTimelineVisible = timeline;
        _lastDefaultBar = defaultBar;

        Timeline.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
        Scroller.VerticalScrollBarVisibility = defaultBar
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Hidden;
        // 从隐藏切回可见时补一次显示，否则控件可见但仍是淡出后的透明度
        if (timeline) Timeline.ShowWithTimer();
    }

    private void UpdateDateHeader()
    {
        if (_items.Count == 0 || _sort == SortKey.Name)
        {
            DateHeader.Text = string.Empty;
            return;
        }
        int first = Math.Clamp(Panel.FirstVisibleIndex(Scroller.VerticalOffset), 0, _items.Count - 1);
        double viewportBottom = Scroller.VerticalOffset + Scroller.ViewportHeight;
        int last = first;
        for (int i = first; i < _items.Count; i++)
        {
            if (Panel.TopOfIndex(i) > viewportBottom) break;
            last = i;
        }
        var a = _items[first].Date.Date;
        var b = _items[last].Date.Date;
        DateHeader.Text = a == b ? a.ToString("yyyy年M月d日") : $"{a:yyyy年M月d日} - {b:yyyy年M月d日}";
    }

    /// <summary>空态：无根目录 → 引导添加文件夹（批次 0 语义：图库默认空）。</summary>
    public void RefreshEmptyState()
    {
        bool noRoots = !AppService.DbReady || AppService.RootCount == 0;
        EmptyTitle.Text = noRoots ? "把你的图片目录添加进图库" : EmptyTitle.Text;
        EmptySubtitle.Text = noRoots
            ? "点击左侧「添加文件夹」，选择一个图片目录。图库默认为空，只有被添加的目录才会显示。"
            : EmptySubtitle.Text;
    }

    private void UpdateEmptyState()
    {
        bool empty = _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        bool noRoots = !AppService.DbReady || AppService.RootCount == 0;
        EmptyTitle.Text = noRoots
            ? "把你的图片目录添加进图库"
            : _filter == FilterKind.Videos ? "没有视频" : "这里还没有照片";
        EmptySubtitle.Text = noRoots
            ? "点击左侧「添加文件夹」，选择一个图片目录。图库默认为空，只有被添加的目录才会显示。"
            : _filter == FilterKind.Videos
                ? "尝试将筛选器改为“所有媒体”。"
                : "把照片放进 assets 文件夹后重新启动。";
    }

    private void UpdateHeader()
    {
        int n = _selected.Count;
        string pageName = _favoritesMode ? "收藏夹" : "图库";
        TitleText.Text = n > 0 ? $"已选择 {n} 个项目" : pageName;
        SubtitleText.Text = n > 0 ? pageName : $"{_items.Count} 张照片";
        // 可见文案来自 GetSelectionText()（CommandBar_SelectSelectionText / _CancelSelectionText）
        // → 「选择」/「取消选择」；UIA 的 Name 才是「选择项目」（GetSelectionButtonFriendlyName）
        SelectButton.Label = n > 0 ? "取消选择" : "选择";
        // GetSelectionIcon(): 取消=E711，勾选=E73A
        SelectIcon.Glyph = n > 0 ? "\uE711" : "\uE73A";
    }

    private static bool IsCtrlDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// 文件操作快捷键（galleryold）：Delete 删除 / F2 重命名 / F3 资源管理器 /
    /// Ctrl+C 复制 / Ctrl+Shift+C 复制路径 / Ctrl+E 编辑标签（批次 3）/ Enter 打开详情。
    /// </summary>
    private async void FileOps_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.System.VirtualKeyModifiers.Control);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        var firstTile = _selected.Count > 0 ? _selected.First() : null;
        var first = firstTile?.Item;

        switch (e.Key)
        {
            case VirtualKey.Delete:
                e.Handled = true;
                await DeleteSelectedAsync();
                break;
            case VirtualKey.F2 when first is not null:
                e.Handled = true;
                await RenameAsync(first);
                break;
            case VirtualKey.F3 when first is not null:
                e.Handled = true;
                FileActions.RevealInExplorer(first.FullPath);
                break;
            case VirtualKey.C when ctrl && shift && first is not null:
                e.Handled = true;
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(string.Join("\n", SelectedPaths()));
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                    SendToast("已复制路径", Gallery.Controls.ToastKind.Success);
                }
                break;
            case VirtualKey.C when ctrl && first is not null:
                e.Handled = true;
                await CopyImageToClipboardAsync(first);
                break;
        }
    }

    private List<string> SelectedPaths()
    {
        var paths = new List<string>();
        foreach (var t in Panel.Children.OfType<PhotoTile>())
            if (t.Selected) paths.Add(t.Item.FullPath);
        foreach (var t in _selected)
            if (!paths.Contains(t.Item.FullPath)) paths.Add(t.Item.FullPath);
        return paths;
    }

    /// <summary>
    /// photos 语义：正常模式单击=打开查看器；选择模式单击=切换选中；
    /// Ctrl+单击=进入选择模式并选中该张。
    /// </summary>
    private void OnTileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not PhotoTile tile) return;
        if (_selectMode)
        {
            tile.Selected = !tile.Selected;
            SyncSelection();
            return;
        }
        if (IsCtrlDown())
        {
            SetSelectMode(true);
            tile.Selected = true;
            SyncSelection();
            return;
        }
        OpenPreview(tile);
    }

    /// <summary>算缩略图窗口 rect 与可见性（galleryold：完整可见→飞入，被遮挡→渐入）。</summary>
    private (Windows.Foundation.Rect Rect, bool FullyVisible)? TileFlightRect(PhotoTile tile)
    {
        var transform = tile.TransformToVisual(null);
        var rect = transform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, tile.ActualWidth, tile.ActualHeight));
        // 完整可见 = tile 完全落在滚动视口内
        var svRect = TransformToVisual(Scroller).TransformBounds(
            new Windows.Foundation.Rect(0, 0, Scroller.ActualWidth, Scroller.ActualHeight));
        bool fully = rect.Top >= svRect.Top && rect.Bottom <= svRect.Bottom;
        return (rect, fully);
    }

    private void OpenPreview(PhotoTile tile)
    {
        var (rect, fully) = TileFlightRect(tile) ?? (default, false);
        PreviewRequested?.Invoke(new PreviewRequest(tile.Item, _items, rect, fully));
    }

    private void SyncSelection()
    {
        _selected.Clear();
        foreach (var child in Panel.Children)
            if (child is PhotoTile { Selected: true } t) _selected.Add(t);
        UpdateHeader();
    }

    private void OnTileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not PhotoTile tile) return;
        OpenPreview(tile);
    }

    private void OnTileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not PhotoTile tile) return;
        if (!tile.Selected)
        {
            foreach (var other in _selected.ToArray()) other.Selected = false;
            _selected.Clear();
            tile.Selected = true;
            SyncSelection();
        }

        // photos/galleryold：右键菜单在鼠标指针处弹出（ShowAttachedFlyout 会锚到整个瓦片）
        var position = e.GetPosition(tile);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FlyoutBase.GetAttachedFlyout(tile) is MenuFlyout flyout)
                flyout.ShowAt(tile, position);
        });
    }

    /// <summary>includeOpen=false 供查看器内右键使用（photos 语义：查看器内无「打开」）。</summary>
    public MenuFlyout BuildContextMenu(PhotoItem item, bool includeOpen = true)
    {
        var menu = new MenuFlyout();
        if (includeOpen)
            menu.Items.Add(Item("打开", "\uE8E5", () =>
            {
                var t = Panel.Children.OfType<PhotoTile>().FirstOrDefault(x => x.Item == item);
                if (t is not null) OpenPreview(t);
            }));
        menu.Items.Add(Item(item.Favorite ? "从收藏夹中移除" : "添加到收藏夹", item.Favorite ? "\uEB52" : "\uEB51", () =>
        {
            if (item.ImageId > 0)
                lock (AppService.DbLock)
                    AppService.Store!.SetFavorite(item.ImageId, !item.Favorite);
            item.Favorite = !item.Favorite;
            if (_favoritesMode) Rebuild(); else UpdateHeader();
            ToastKindRequested?.Invoke(item.Favorite ? "已添加到收藏夹" : "已从收藏夹中移除",
                Gallery.Controls.ToastKind.Success, null);
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("复制", "\uE8C8", () => _ = CopyImageToClipboardAsync(item)));
        menu.Items.Add(Item("复制为路径", "\uE71B", () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(item.FullPath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            ToastRequested?.Invoke("已复制路径");
        }));
        menu.Items.Add(Item("复制/移动", "\uE8DE", () => _ = CopyMoveAsync(new[] { item.ImageId })));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("在文件资源管理器中打开", "\uEC50", () =>
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
        }));
        menu.Items.Add(Item("重命名", "\uE8AC", () => _ = RenameAsync(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = Item("删除", "\uE74D", () => _ = DeleteSelectedAsync());
        delete.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x99, 0xA4)); // #ff99a4（删除红）
        menu.Items.Add(delete);
        return menu;
    }

    // ---------------------------------------------------------- 批次 2：真实操作

    /// <summary>复制位图到剪贴板（单图；galleryold copy_image_to_clipboard）。</summary>
    private async System.Threading.Tasks.Task CopyImageToClipboardAsync(PhotoItem item)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.FullPath);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            ToastRequested?.Invoke($"已复制图片：{item.Name}");
        }
        catch (Exception ex) when (ex is IOException or System.IO.FileNotFoundException
            or UnauthorizedAccessException)
        {
            SendToast($"这张图读不出来，复制失败：{ex.Message}", Gallery.Controls.ToastKind.Pending);
        }
    }

    /// <summary>删除选中（回收站 + 二次确认，取消为默认，红 #ff99a4）。</summary>
    private async System.Threading.Tasks.Task DeleteSelectedAsync()
    {
        var ids = SelectedIds();
        if (ids.Count == 0) { ToastRequested?.Invoke("没有选中的项目"); return; }
        var confirm = new ContentDialog
        {
            Title = "删除",
            Content = $"将选中的 {ids.Count} 个项目移到回收站？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Secondary, // 取消为默认
            XamlRoot = XamlRoot,
        };
        confirm.Resources["ContentDialogButton1BackgroundBrush"] =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x99, 0xA4));
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var (done, errors) = await System.Threading.Tasks.Task.Run(() =>
        {
            lock (AppService.DbLock)
                return FileActions.DeleteImages(AppService.Store!, ids);
        });
        AppService.Recount();
        Rebuild();
        if (errors.Count > 0) SendToast($"删除失败：{errors[0]}", Gallery.Controls.ToastKind.Pending);
        else SendToast($"已删除 {done.Count} 个项目", Gallery.Controls.ToastKind.Success);
    }

    /// <summary>重命名（F2，单图，扩展名不动；file_key 保身份）。</summary>
    private async System.Threading.Tasks.Task RenameAsync(PhotoItem item)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(item.File);
        var input = new TextBox { Text = stem };
        var dialog = new ContentDialog
        {
            Title = "重命名",
            Content = input,
            PrimaryButtonText = "重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        var newName = input.Text.Trim();
        if (newName.Length == 0 || newName == stem) return;
        var (ok, error) = await System.Threading.Tasks.Task.Run(() =>
        {
            lock (AppService.DbLock)
                return FileActions.RenameImage(AppService.Store!, item.ImageId, newName);
        });
        if (!ok) { SendToast(error, Gallery.Controls.ToastKind.Pending); return; }
        SendToast($"已重命名：{newName}", Gallery.Controls.ToastKind.Success);
        Rebuild();
    }

    /// <summary>复制/移动到指定目录（FolderPicker；移动限图库内）。</summary>
    private async System.Threading.Tasks.Task CopyMoveAsync(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) { ToastRequested?.Invoke("没有选中的项目"); return; }
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, AppService.MainWindowHandle);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var dialog = new ContentDialog
        {
            Title = "复制或移动",
            Content = $"将 {ids.Count} 个项目复制还是移动到\n{folder.Path}？",
            PrimaryButtonText = "移动",
            SecondaryButtonText = "复制",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        var move = result == ContentDialogResult.Primary;
        var (count, firstError) = await System.Threading.Tasks.Task.Run(() =>
        {
            lock (AppService.DbLock)
            {
                try
                {
                    var (done, errors) = move
                        ? FileActions.MoveImages(AppService.Store!, ids, folder.Path)
                        : FileActions.CopyImages(AppService.Store!, ids, folder.Path);
                    return (done.Count, errors.Count > 0 ? errors[0] : null);
                }
                finally
                {
                    if (move) AppService.Recount();
                }
            }
        });
        SendToast(
            firstError is not null
                ? $"{(move ? "移动" : "复制")}失败：{firstError}"
                : $"已{(move ? "移动" : "复制")} {count} 个项目",
            firstError is not null ? Gallery.Controls.ToastKind.Pending : Gallery.Controls.ToastKind.Success);
        if (move) Rebuild();
    }

    private List<int> SelectedIds()
    {
        var ids = new List<int>();
        foreach (var tile in _selected)
            if (tile.Item.ImageId > 0) ids.Add(tile.Item.ImageId);
        if (ids.Count == 0 && Panel.IsVirtual)
            foreach (var t in Panel.Children.OfType<PhotoTile>())
                if (t.Selected && t.Item.ImageId > 0) ids.Add(t.Item.ImageId);
        return ids.Distinct().ToList();
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var mi = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }
        };
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>photos 选择模式：工具栏「选择」进入（勾选框常显、单击=选中），
    /// 有选中时再点=退出并清空（照 photos 按钮状态机）。</summary>
    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectMode && _selected.Count == 0)
        {
            SetSelectMode(false);
            return;
        }
        if (_selected.Count > 0)
        {
            foreach (var t in _selected.ToArray()) t.Selected = false;
            _selected.Clear();
            SyncSelection();
            SetSelectMode(false);
            return;
        }
        SetSelectMode(true);
    }

    private void SetSelectMode(bool on)
    {
        _selectMode = on;
        foreach (var child in Panel.Children)
            if (child is PhotoTile t) t.SelectMode = on;
        if (!on)
        {
            foreach (var t in _selected.ToArray()) t.Selected = false;
            _selected.Clear();
            SyncSelection();
        }
        UpdateHeader();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in Panel.Children)
            if (child is PhotoTile t) t.Selected = true;
        SyncSelection();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in Panel.Children)
            if (child is PhotoTile t) t.Selected = false;
        SyncSelection();
    }

    private void SortKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item && item.Tag is string key)
        {
            _sortKey = key;
            // manifest 路径的旧 enum 同步（仅 4 键可映射）
            _sort = key switch
            {
                "name" => SortKey.Name,
                "mtime" => SortKey.DateModified,
                "created" => SortKey.DateCreated,
                _ => SortKey.DateTaken,
            };
            RebuildKeepScroll();
        }
    }

    private void SortDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item && item.Tag is string dir)
        {
            _ascending = dir == "asc";
            RebuildKeepScroll();
        }
    }

    private void SyncSortMenuChecks()
    {
        SortTaken.IsChecked = _sortKey == "taken";
        SortCreated.IsChecked = _sortKey == "created";
        SortModified.IsChecked = _sortKey == "mtime";
        SortName.IsChecked = _sortKey == "name";
        SortAdded.IsChecked = _sortKey == "added";
        SortSize.IsChecked = _sortKey == "size";
        SortRating.IsChecked = _sortKey == "rating";
        SortRandom.IsChecked = _sortKey == "random";
        SortAsc.IsChecked = _ascending;
        SortDesc.IsChecked = !_ascending;
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e) => ApplyFilter(FilterKind.All);
    private void FilterPhotos_Click(object sender, RoutedEventArgs e) => ApplyFilter(FilterKind.Photos);
    private void FilterVideos_Click(object sender, RoutedEventArgs e) => ApplyFilter(FilterKind.Videos);

    private void ApplyFilter(FilterKind kind)
    {
        _filter = kind;
        Rebuild();
    }

    private void LayoutRiver_Click(object sender, RoutedEventArgs e) => ApplyLayout(GalleryLayoutMode.River);
    private void LayoutGrid_Click(object sender, RoutedEventArgs e) => ApplyLayout(GalleryLayoutMode.Grid);

    private void SizeSmall_Click(object sender, RoutedEventArgs e) => ApplySize(ThumbnailSize.Small);
    private void SizeMedium_Click(object sender, RoutedEventArgs e) => ApplySize(ThumbnailSize.Medium);
    private void SizeLarge_Click(object sender, RoutedEventArgs e) => ApplySize(ThumbnailSize.Large);

    private void ApplyLayout(GalleryLayoutMode mode)
    {
        int index = _anchorIndex;
        double delta = _anchorDelta;
        Panel.Mode = mode;
        LayoutIcon.Glyph = mode == GalleryLayoutMode.Grid ? "\uE8A9" : "\uECA5";
        Panel.InvalidateMeasure();
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Math.Max(0, Panel.TopOfIndex(index) - delta), null, true);
        Timeline.Refresh();
    }

    private void ApplySize(ThumbnailSize size)
    {
        int index = _anchorIndex;
        double delta = _anchorDelta;
        Panel.Size = size;
        Panel.InvalidateMeasure();
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Math.Max(0, Panel.TopOfIndex(index) - delta), null, true);
    }

    private void RebuildKeepScroll()
    {
        int index = _anchorIndex;
        double delta = _anchorDelta;
        Rebuild();
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Math.Max(0, Panel.TopOfIndex(index) - delta), null, true);
    }
}
