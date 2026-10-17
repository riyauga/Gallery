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
    private readonly HashSet<int> _selected = new();

    // 示例素材没有数据库 ID，使用其在固定 manifest 中的负数索引。
    private int SelectionId(PhotoItem item) => item.ImageId > 0 ? item.ImageId : -_library.All.IndexOf(item) - 1;
    private IEnumerable<PhotoItem> SelectedItems() => _items.Where(item => _selected.Contains(SelectionId(item)));

    private bool _favoritesMode;
    private bool _selectMode; // photos 选择模式（工具栏「选择」切换）
    private SortKey _sort = SortKey.DateTaken;          // manifest 路径用
    private string _sortKey = "taken";                   // 真实数据路径（galleryold 键）
    private bool _ascending = false;
    private FilterKind _filter = FilterKind.All;
    private string _query = string.Empty;
    private int? _dirRootId;                             // 目录树选择（null=全部）
    private string? _dirRel;
    private string? _dirTitle;                           // 目录页标题（根/末段文件夹名）

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
        Windows.Foundation.Rect SourceRect, bool FullyVisible,
        Func<PhotoItem, Windows.Foundation.Rect?> ResolveSourceRect);
    public event Action<PreviewRequest>? PreviewRequested;
    public event Action<IReadOnlyList<PhotoItem>>? ItemsChanged;
    public UIElement FocusAnchor => Root;

    public GalleryPage(Library library)
    {
        _library = library;
        _sortKey = AppService.Settings.Settings.sort_key;
        _ascending = !AppService.Settings.Settings.sort_desc;
        _filter = AppService.Settings.Settings.media_filter switch
        {
            "photos" => FilterKind.Photos,
            "videos" => FilterKind.Videos,
            _ => FilterKind.All,
        };
        _sort = _sortKey switch
        {
            "name" => SortKey.Name,
            "modified" => SortKey.DateModified,
            "created" => SortKey.DateCreated,
            _ => SortKey.DateTaken,
        };
        InitializeComponent();

        // 布局与尺寸从配置恢复（galleryold 键：gallery_mode = justified|square，thumbbar_size = 目标行高）
        Panel.Mode = AppService.Settings.Settings.gallery_mode == "square"
            ? GalleryLayoutMode.Grid : GalleryLayoutMode.River;
        Panel.Size = SizeFromRowHeight(AppService.Settings.Settings.thumbbar_size);
        LayoutIcon.Glyph = Panel.Mode == GalleryLayoutMode.Grid ? "\uE8A9" : "\uECA5";

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
        ItemsChanged?.Invoke(_items.ToArray());
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
            double timestamp = _sortKey switch
            {
                "modified" => row.Mtime,
                "created" => row.Ctime ?? row.Mtime,
                "added" => row.AddedAt,
                _ => row.TakenAt ?? row.Mtime,
            };
            DateTime date = DateTime.UnixEpoch.AddSeconds(timestamp).ToLocalTime();
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
        // photos 的页头标题就是当前集合名：根目录 = 根文件夹名，子目录 = 最后一段目录名
        _dirTitle = ResolveDirectoryTitle(rootId, dirRel);
        if (IsLoaded) Rebuild();
    }

    /// <summary>
    /// 目录页标题（photos SimplePageHeader.Title ← CurrentPageInfo 的名称）。
    /// 落盘路径在 RootInfo.Path / dirRel，只取文件夹末段显示，不再拼完整路径。
    /// </summary>
    private static string? ResolveDirectoryTitle(int? rootId, string? dirRel)
    {
        if (rootId is null) return null;
        if (!string.IsNullOrEmpty(dirRel))
        {
            var leaf = System.IO.Path.GetFileName(dirRel.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(leaf)) return leaf;
        }
        var root = AppService.Store?.Roots().FirstOrDefault(r => r.Id == rootId);
        if (root is null) return null;
        var name = System.IO.Path.GetFileName(root.Path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? root.Path : name;
    }

    private void WireTile(PhotoTile tile, PhotoItem item)
    {
        tile.Tapped += OnTileTapped;
        tile.RightTapped += OnTileRightTapped;
        FlyoutBase.SetAttachedFlyout(tile, BuildContextMenu(item));
        tile.SelectMode = _selectMode;
        tile.Selected = _selected.Contains(SelectionId(item));

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
            items.AddRange(SelectedItems());
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

    /// <summary>扫描或监听完成后重取当前查询，保留仍在结果中的图片 ID 选区。</summary>
    public void RefreshFromStore()
    {
        if (!IsLoaded) return;
        var selection = _selected.ToArray();
        RebuildKeepScroll();
        _selected.UnionWith(selection);
        _selected.IntersectWith(_items.Select(SelectionId));
        SyncSelection();
    }

    private void UpdateEmptyState()
    {
        bool empty = _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        bool noRoots = !AppService.DbReady || AppService.RootCount == 0;
        bool inFolder = _dirRootId is not null;
        bool recursive = AppService.Settings.Settings.recursive;
        EmptyTitle.Text = noRoots
            ? "把你的图片目录添加进图库"
            : _filter == FilterKind.Videos ? "没有视频" : inFolder ? "此文件夹中没有照片" : "这里还没有照片";
        EmptySubtitle.Text = noRoots
            ? "点击左侧「添加文件夹」，选择一个图片目录。图库默认为空，只有被添加的目录才会显示。"
            : _filter == FilterKind.Videos
                ? "尝试将筛选器改为“所有媒体”。"
                : inFolder
                    ? recursive
                        ? "这个文件夹里没有可显示的图片。"
                        : "打开「筛选器」里的“显示子文件夹内容”，就能看到子文件夹里的照片。"
                    : "把照片放进 assets 文件夹后重新启动。";
    }

    private void UpdateHeader()
    {
        int n = _selected.Count;
        string pageName = _favoritesMode ? "收藏夹" : _dirTitle ?? "图库";
        IconAndTitleHeader.Title = n > 0 ? $"已选择 {n} 个项目" : pageName;
        SubtitleText.Text = n > 0 ? pageName : $"{_items.Count} 张照片";
        // 可见文案来自 GetSelectionText()（CommandBar_SelectSelectionText / _CancelSelectionText）
        // → 「选择」/「取消选择」；UIA 的 Name 才是「选择项目」（GetSelectionButtonFriendlyName）
        SelectButton.Label = n > 0 ? "取消选择" : "选择";
        // GetSelectionIcon(): 取消=E711，勾选=E73A
        SelectIcon.Glyph = n > 0 ? "\uE711" : "\uE73A";

        // photos GalleryHeader.xaml 的 VisualStateGroup ControlBackgroundGroup：
        // Normal = 透明，Selected = CardBackgroundFillColorDefaultBrush + CardStrokeColorDefaultBrush
        bool selected = n > 0;
        Header.Background = selected ? CardFillBrush(HeaderBackgroundKey) : TransparentBrush;
        Header.BorderBrush = selected ? CardFillBrush(HeaderBorderKey) : TransparentBrush;

        // photos 这几个按钮的可见性绑命令（VisibilityBoundCommand / HasSelection）
        var selectionVis = selected ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = selectionVis;
        MoveCopyButton.Visibility = selectionVis;
        ShareButton.Visibility = selectionVis;

        SyncSortMenuChecks();
        SyncFilterMenuChecks();
        SyncLayoutMenuChecks();
        SyncSubfolderMenuChecks();
    }

    private const string HeaderBackgroundKey = "CardBackgroundFillColorDefaultBrush";
    private const string HeaderBorderKey = "CardStrokeColorDefaultBrush";

    /// <summary>透明画笔单例（头部 Normal 态用）。</summary>
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush TransparentBrush =
        new(Microsoft.UI.Colors.Transparent);

    /// <summary>按主题资源键取画笔；缺键时退回透明，避免 ElementNotFound 崩。</summary>
    private static Microsoft.UI.Xaml.Media.Brush CardFillBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value)
            && value is Microsoft.UI.Xaml.Media.Brush brush)
        {
            return brush;
        }
        return TransparentBrush;
    }

    private void SyncFilterMenuChecks()
    {
        FilterAll.IsChecked = _filter == FilterKind.All;
        FilterPhotos.IsChecked = _filter == FilterKind.Photos;
        FilterVideos.IsChecked = _filter == FilterKind.Videos;
    }

    private void SyncLayoutMenuChecks()
    {
        LayoutRiver.IsChecked = Panel.Mode == GalleryLayoutMode.River;
        LayoutGrid.IsChecked = Panel.Mode == GalleryLayoutMode.Grid;
        SizeSmall.IsChecked = Panel.Size == ThumbnailSize.Small;
        SizeMedium.IsChecked = Panel.Size == ThumbnailSize.Medium;
        SizeLarge.IsChecked = Panel.Size == ThumbnailSize.Large;
    }

    /// <summary>
    /// 子文件夹内容的两项（photos CommandBar_ShowSubfolderContentButton / …Hide…）只在目录页出现，
    /// 对应原版 IsSubfolderOptionVisible(CurrentPageInfo)。
    /// </summary>
    private void SyncSubfolderMenuChecks()
    {
        bool inFolder = _dirRootId is not null || !string.IsNullOrEmpty(_dirRel);
        var vis = inFolder ? Visibility.Visible : Visibility.Collapsed;
        SubfolderContentSeparator.Visibility = vis;
        ShowSubfolderContentItem.Visibility = vis;
        HideSubfolderContentItem.Visibility = vis;
        bool recursive = AppService.Settings.Settings.recursive;
        ShowSubfolderContentItem.IsChecked = recursive;
        HideSubfolderContentItem.IsChecked = !recursive;
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
                .HasFlag(CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);
        var first = SelectedItems().FirstOrDefault();

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
        => SelectedItems().Select(item => item.FullPath).ToList();

    /// <summary>
    /// photos 语义：正常模式单击=打开查看器；选择模式单击=切换选中；
    /// Ctrl+单击=进入选择模式并选中该张。
    /// </summary>
    private void OnTileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not PhotoTile tile) return;
        if (_selectMode)
        {
            int id = SelectionId(tile.Item);
            if (!_selected.Add(id)) _selected.Remove(id);
            SyncSelection();
            return;
        }
        if (IsCtrlDown())
        {
            SetSelectMode(true);
            _selected.Add(SelectionId(tile.Item));
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
        var svRect = Scroller.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, Scroller.ActualWidth, Scroller.ActualHeight));
        bool fully = rect.Top >= svRect.Top && rect.Bottom <= svRect.Bottom;
        return (rect, fully);
    }

    private void OpenPreview(PhotoTile tile)
    {
        var (rect, fully) = TileFlightRect(tile) ?? (default, false);
        PreviewRequested?.Invoke(new PreviewRequest(tile.Item, _items.ToArray(), rect, fully, ResolveSourceRect));
    }

    private Windows.Foundation.Rect? ResolveSourceRect(PhotoItem item)
    {
        int index = _items.FindIndex(candidate => SelectionId(candidate) == SelectionId(item));
        if (index < 0 || index >= Panel.Rects.Count) return null;
        return Panel.TransformToVisual(null).TransformBounds(Panel.Rects[index]);
    }

    private void SyncSelection()
    {
        foreach (var tile in Panel.Children.OfType<PhotoTile>())
            tile.Selected = _selected.Contains(SelectionId(tile.Item));
        UpdateHeader();
    }

    private void OnTileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not PhotoTile tile) return;
        if (!tile.Selected)
        {
            _selected.Clear();
            _selected.Add(SelectionId(tile.Item));
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
        var delete = Item("删除", "\uE74D", () => _ = DeleteAsync(
            includeOpen && _selected.Contains(SelectionId(item)) ? SelectedIds() : new[] { item.ImageId }));
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
    private Task DeleteSelectedAsync() => DeleteAsync(SelectedIds());

    public async Task DeleteAsync(IReadOnlyCollection<int> targets)
    {
        var ids = targets.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) { ToastRequested?.Invoke("没有选中的项目"); return; }
        var confirm = new ContentDialog
        {
            Title = "删除",
            Content = $"将选中的 {ids.Length} 个项目移到回收站？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
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
    public async System.Threading.Tasks.Task RenameAsync(PhotoItem item)
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

    /// <summary>
    /// 复制/移动到指定目录（FolderPicker；移动限图库内）。
    /// requestedMove: null=先问「复制还是移动」（右键菜单路径）；true/false=命令栏
    /// 「移动到文件夹」/「复制到文件夹」已定语义，只做一次确认。
    /// </summary>
    private async System.Threading.Tasks.Task CopyMoveAsync(IReadOnlyList<int> ids, bool? requestedMove = null)
    {
        if (ids.Count == 0) { ToastRequested?.Invoke("没有选中的项目"); return; }
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, AppService.MainWindowHandle);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        bool move;
        if (requestedMove is bool fixedMove)
        {
            move = fixedMove;
            var confirm = new ContentDialog
            {
                Title = move ? "移动" : "复制",
                Content = $"将 {ids.Count} 个项目{(move ? "移动" : "复制")}到\n{folder.Path}？",
                PrimaryButtonText = move ? "移动" : "复制",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        else
        {
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
            move = result == ContentDialogResult.Primary;
        }
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
        => SelectedItems().Where(item => item.ImageId > 0).Select(item => item.ImageId).ToList();

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
            _selected.Clear();
            SyncSelection();
        }
        UpdateHeader();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _selected.UnionWith(_items.Select(SelectionId));
        SyncSelection();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        _selected.Clear();
        SyncSelection();
    }

    /// <summary>命令栏「删除」（photos DeleteButton / DeleteCommand）。</summary>
    private async void DeleteButton_Click(object sender, RoutedEventArgs e) => await DeleteAsync(SelectedIds());

    /// <summary>「移动或复制」→ 移动到文件夹（photos CommandBar_MoveToFolderFlyoutItem）。</summary>
    private async void MoveToFolder_Click(object sender, RoutedEventArgs e) => await CopyMoveAsync(SelectedIds(), true);

    /// <summary>「移动或复制」→ 复制到文件夹（photos CommandBar_CopyToFolderFlyoutItem）。</summary>
    private async void CopyToFolder_Click(object sender, RoutedEventArgs e) => await CopyMoveAsync(SelectedIds(), false);

    /// <summary>筛选器 → 显示子文件夹内容（photos CommandBar_ShowSubfolderContentButton）。</summary>
    private void ShowSubfolderContent_Click(object sender, RoutedEventArgs e) => SetRecursive(true);

    /// <summary>筛选器 → 隐藏子文件夹内容（photos CommandBar_HideSubfolderContentButton）。</summary>
    private void HideSubfolderContent_Click(object sender, RoutedEventArgs e) => SetRecursive(false);

    /// <summary>目录递归开关落盘到 settings.recursive（galleryold 同名键），再重取当前查询。</summary>
    private void SetRecursive(bool recursive)
    {
        if (AppService.Settings.Settings.recursive != recursive)
        {
            AppService.Settings.Settings.recursive = recursive;
            AppService.Settings.Save();
            RebuildKeepScroll();
        }
        SyncSubfolderMenuChecks();
    }

    /// <summary>命令栏「共享」（photos ShareButton / ShouldShowDefaultShareOption → 系统共享面板）。</summary>
    private async void ShareButton_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedItems()
            .Where(item => !item.Missing)
            .Select(item => item.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            .ToArray();
        if (paths.Length == 0)
        {
            ToastRequested?.Invoke("这些项目没有可用文件");
            return;
        }

        var files = new List<Windows.Storage.StorageFile>(paths.Length);
        foreach (var path in paths)
        {
            try
            {
                files.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
            }
            catch (Exception)
            {
                // 单项读不到就跳过，剩下的仍然共享（原版也是这样逐项取 StorageFile）
            }
        }
        if (files.Count == 0)
        {
            ToastRequested?.Invoke("这些项目没有可用文件");
            return;
        }

        try
        {
            var manager = GetShareManager(AppService.MainWindowHandle);
            // 每次点「共享」重挂一次，取完数据立刻摘掉，避免 DataRequested 订阅累积
            Windows.Foundation.TypedEventHandler<
                Windows.ApplicationModel.DataTransfer.DataTransferManager,
                Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                manager.DataRequested -= handler;
                args.Request.Data.SetStorageItems(files, true);
            };
            manager.DataRequested += handler;
            Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop.ShowShareUIForWindow(AppService.MainWindowHandle);
        }
        catch (Exception ex)
        {
            SendToast($"共享面板打不开：{ex.Message}", Gallery.Controls.ToastKind.Pending);
        }
    }

    /// <summary>
    /// 桌面应用没有 CoreWindow，DataTransferManager 必须经 IDataTransferManagerInterop 取窗口实例
    /// （Microsoft 桌面共享标准做法：GetForWindow(hwnd) + ShowShareUIForWindow(hwnd)）。
    /// </summary>
    private static Windows.ApplicationModel.DataTransfer.DataTransferManager GetShareManager(IntPtr hwnd)
        => Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop.GetForWindow(hwnd);

    private void SortKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item && item.Tag is string key)
        {
            _sortKey = key;
            // manifest 路径的旧 enum 同步（仅 4 键可映射）
            _sort = key switch
            {
                "name" => SortKey.Name,
                "modified" => SortKey.DateModified,
                "created" => SortKey.DateCreated,
                _ => SortKey.DateTaken,
            };
            AppService.Settings.Settings.sort_key = _sortKey;
            AppService.Settings.Save();
            RebuildKeepScroll();
        }
    }

    private void SortDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item && item.Tag is string dir)
        {
            _ascending = dir == "asc";
            AppService.Settings.Settings.sort_desc = !_ascending;
            AppService.Settings.Save();
            RebuildKeepScroll();
        }
    }

    private void SyncSortMenuChecks()
    {
        SortTaken.IsChecked = _sortKey == "taken";
        SortCreated.IsChecked = _sortKey == "created";
        SortModified.IsChecked = _sortKey == "modified";
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
        AppService.Settings.Settings.media_filter = kind switch
        {
            FilterKind.Photos => "photos",
            FilterKind.Videos => "videos",
            _ => "all",
        };
        AppService.Settings.Save();
        Rebuild();
    }

    /// <summary>
    /// 布局/尺寸落盘到 galleryold 的配置键：gallery_mode = justified|square，
    /// thumbbar_size = 当前档位的目标行高（Photos 的布局/尺寸选择同样是持久的）。
    /// </summary>
    private void PersistLayout()
    {
        var settings = AppService.Settings.Settings;
        settings.gallery_mode = Panel.Mode == GalleryLayoutMode.Grid ? "square" : "justified";
        var (_, target, _) = Panel.SizeRange;
        settings.thumbbar_size = (int)Math.Round(target);
        AppService.Settings.Save();
    }

    /// <summary>目标行高 → 三档尺寸（小于 200 小、小于 350 中等，其余大）。</summary>
    private static ThumbnailSize SizeFromRowHeight(int rowHeight) => rowHeight switch
    {
        < 200 => ThumbnailSize.Small,
        < 350 => ThumbnailSize.Medium,
        _ => ThumbnailSize.Large,
    };

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
        PersistLayout();
        Panel.InvalidateMeasure();
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Math.Max(0, Panel.TopOfIndex(index) - delta), null, true);
        Timeline.Refresh();
        SyncLayoutMenuChecks();
    }

    private void ApplySize(ThumbnailSize size)
    {
        int index = _anchorIndex;
        double delta = _anchorDelta;
        Panel.Size = size;
        PersistLayout();
        Panel.InvalidateMeasure();
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Math.Max(0, Panel.TopOfIndex(index) - delta), null, true);
        SyncLayoutMenuChecks();
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
