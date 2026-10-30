using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
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
    private int? _selectionAnchorId;
    private int? _keyboardFocusId;
    private bool _showKeyboardFocus;

    // 示例素材没有数据库 ID，使用其在固定 manifest 中的负数索引。
    private int SelectionId(PhotoItem item) => item.ImageId > 0 ? item.ImageId : -_library.All.IndexOf(item) - 1;
    private IEnumerable<PhotoItem> SelectedItems() => _items.Where(item => _selected.Contains(SelectionId(item)));

    private bool _favoritesMode;
    private bool _selectMode; // photos 选择模式（工具栏「选择」切换）
    private SortKey _sort = SortKey.DateTaken;          // manifest 路径用
    private string _sortKey = "taken";                   // 真实数据路径（galleryold 键）
    private bool _ascending = false;
    private FilterKind _filter = FilterKind.All;
    private Controls.FilterPanel _filterPanel = null!;
    private string _query = string.Empty;
    private int? _dirRootId;                             // 目录树选择（null=全部）
    private string? _dirRel;
    private string? _dirTitle;                           // 目录页标题（根/末段文件夹名）

    private double _lastWidth;
    private int _anchorIndex;
    private double _anchorDelta;
    private bool? _lastTimelineVisible;
    private bool? _lastDefaultBar;
    private bool _rebuildPending = true;

    public event Action<string>? ToastRequested;
    public event Action<string, Gallery.Controls.ToastKind, string?>? ToastKindRequested = null!;

    /// <summary>带类型的通知（Info/Success/Pending），MainWindow 转 ToastHost。</summary>
    public void SendToast(string message, Gallery.Controls.ToastKind kind,
        string? detail = null) => ToastKindRequested?.Invoke(message, kind, detail);
    public sealed record PreviewRequest(
        PhotoItem Item, IReadOnlyList<PhotoItem> Items,
        Windows.Foundation.Rect SourceRect, bool FullyVisible,
        Func<PhotoItem, Windows.Foundation.Rect?> ResolveSourceRect,
        Microsoft.UI.Xaml.Media.ImageSource? Thumbnail = null);
    public event Action<PreviewRequest>? PreviewRequested;
    public event Action<IReadOnlyList<PhotoItem>>? ItemsChanged;
    public event Action? TagsEdited;

    /// <summary>删除/重命名/移动真正开始前触发（查看器用来释放视频文件句柄）。</summary>
    public event Action? FileOperationStarting;

    /// <summary>应用内拖拽携带的图片 ID（逗号分隔）；外部目标仍走 CF_HDROP 复制语义。</summary>
    public const string DragImageIdsFormat = "Gallery.DragImageIds";

    /// <summary>当前结果集快照（查看器翻页/标签刷新共用）。</summary>
    public IReadOnlyList<PhotoItem> CurrentItems => _items;
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
        Panel.TileTemplate = (DataTemplate)Resources["PhotoTileTemplate"];
        Panel.TilePrepared += tile => WireTile(tile, tile.Item);
        Panel.ViewportInvalidated += ScheduleViewport;
        Panel.LayoutReady += OnLayoutReady;
        ImageLoader.WorkCompleted += ScheduleViewport;
        _filterPanel = new Controls.FilterPanel();
        if (FilterButton.Flyout is Flyout filterFlyout) filterFlyout.Content = _filterPanel.Root;
        _filterPanel.Media = _filter switch
        {
            FilterKind.Photos => Controls.FilterPanel.MediaKind.Photos,
            FilterKind.Videos => Controls.FilterPanel.MediaKind.Videos,
            _ => Controls.FilterPanel.MediaKind.All,
        };
        _filterPanel.SyncSubfolder(AppService.Settings.Settings.recursive, true);
        _filterPanel.Changed += ApplyFilterPanel;

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
            ScheduleViewport();
        };

        Scroller.SizeChanged += (_, e) =>
        {
            UpdateScrollbarType();
            if (_items.Count == 0) return;
            if (Math.Abs(e.NewSize.Width - _lastWidth) < 0.5) return;
            _lastWidth = e.NewSize.Width;
            SaveAnchor();
            Panel.InvalidateMeasure();
        };

        Loaded += (_, _) =>
        {
            _lastWidth = Scroller.ActualWidth;
            if (_rebuildPending) RebuildKeepScroll();
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

        Root.GotFocus += (_, _) =>
        {
            // 进入区域本身不点亮某张图片；图片提示只由明确的图库按键建立。
            SyncSelection();
        };
        Root.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(SyncSelection);
        WireCommandBarFlyouts();
        FilterButton.Flyout.Closing += (sender, _) => _filterFlyoutTarget = sender.Target as Control;
        Bar.LayoutUpdated += (_, _) => RestoreFilterFlyoutFocus();
        Unloaded += (_, _) => _filterFlyoutTarget = null;
    }

    /// <summary>
    /// 各二级菜单与「更多」溢出菜单互斥，同时只挂一个；保留原生键盘焦点视觉。
    /// </summary>
    private void WireCommandBarFlyouts()
    {
        var flyouts = new[] { SortButton.Flyout, FilterButton.Flyout, LayoutButton.Flyout, MoveCopyButton.Flyout };
        foreach (var flyout in flyouts)
        {
            if (flyout is null) continue;
            var current = flyout;
            current.Opened += (_, _) =>
            {
                if (current.Target is AppBarButton { IsInOverflow: false }) Bar.IsOpen = false;
                foreach (var other in flyouts)
                {
                    if (!ReferenceEquals(other, current)) other?.Hide();
                }
            };
        }

        Bar.Opened += (_, _) =>
        {
            foreach (var flyout in flyouts) flyout?.Hide();
        };
    }

    private Control? _filterFlyoutTarget;

    /// <summary>Photos GalleryCommandBar：关闭时记住入口，布局更新后再校正一次返回焦点。</summary>
    private void RestoreFilterFlyoutFocus()
    {
        var target = _filterFlyoutTarget;
        _filterFlyoutTarget = null;
        if (target is null || !IsLoaded || !_active) return;
        // 菜单互斥关闭旧弹层时，新的弹层应继续持有焦点。
        if (new[] { SortButton.Flyout, LayoutButton.Flyout, MoveCopyButton.Flyout }.Any(f => f?.IsOpen == true)) return;
        if (FilterButton.IsInOverflow && !Bar.IsOpen)
        {
            target = FindCommandBarMoreButton(Bar);
            if (target is null) return;
        }
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), target))
            target.Focus(FocusState.Programmatic);
    }

    private static Button? FindCommandBarMoreButton(DependencyObject element)
    {
        if (element is Button { Name: "MoreButton" } button) return button;
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < count; i++)
        {
            var found = FindCommandBarMoreButton(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i));
            if (found is not null) return found;
        }
        return null;
    }

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    /// <summary>galleryold：搜索 160ms 防抖（标签/中文别名/文件名）。</summary>
    public void SetSearch(string query)
    {
        _query = (query ?? string.Empty).Trim();
        _rebuildPending = true;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    public void SetMode(bool favorites)
    {
        if (_favoritesMode == favorites) return;
        _favoritesMode = favorites;
        _rebuildPending = true;
        _selected.Clear();
        _selectionAnchorId = null;
        _keyboardFocusId = null;
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
            FilterKind.Photos => src.Where(i => !i.IsVideo),
            FilterKind.Videos => src.Where(i => i.IsVideo),
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

    private long _queryVersion;
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    public long QueryCount { get; private set; }
    public Task QueryCompletion { get; private set; } = Task.CompletedTask;

    private void Rebuild()
    {
        _rebuildPending = false;
        SyncSortMenuChecks();
        long version = ++_queryVersion;
        if (!AppService.DbReady || AppService.Store is null)
        {
            ApplyItems(BuildSource().ToList());
            return;
        }
        var spec = new StoreService.QuerySpec
        {
            RootId = _dirRootId, DirRel = _dirRel,
            Recursive = AppService.Settings.Settings.recursive,
            FavoritesOnly = _favoritesMode, Search = _query, Sort = _sortKey, Desc = !_ascending,
            TagIds = _filterPanel.Include.Select(x => x.Id).ToList(),
            ExcludedTagIds = _filterPanel.Exclude.Select(x => x.Id).ToList(),
            TagMode = _filterPanel.Mode, Untagged = _filterPanel.UntaggedOnly,
            HideNsfw = AppService.Settings.Settings.nsfw_display == "hide",
            MediaKind = _filter switch
            {
                FilterKind.Photos => "image",
                FilterKind.Videos => "video",
                _ => null,
            },
        };
        QueryCompletion = QueryAsync(AppService.Store.DbPath, spec, _sortKey, version);
    }

    // 每次后台查询独占自己的只读连接；排队的旧版本在打开连接前退出。
    private async Task QueryAsync(string path, StoreService.QuerySpec spec, string sortKey, long version)
    {
        await _queryGate.WaitAsync();
        try
        {
            if (version != _queryVersion) return;
            QueryCount++;
            var items = await Task.Run(() =>
            {
                using var store = new StoreService(path, readOnly: true);
                return MapRows(store.Query(spec), store.FavoriteIds(), sortKey);
            });
            if (version != _queryVersion) return;
            if (!_active) { _rebuildPending = true; return; }
            ApplyItems(items);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException)
        {
            if (version == _queryVersion) SendToast("图库读取失败", ToastKind.Pending, ex.Message);
        }
        finally { _queryGate.Release(); }
    }

    private static bool SameItem(PhotoItem a, PhotoItem b) =>
        (a.ImageId, a.FullPath, a.PixelWidth, a.PixelHeight, a.Date, a.ModifiedAtUnix, a.FileSize, a.Favorite,
         a.RootId, a.DirRel, a.RelPath, a.TakenAtUnix, a.CreatedAtUnix, a.Rating, a.Nsfw, a.PageUrl, a.SourceUrl, a.Missing,
         a.IsVideo, a.DurationSeconds) ==
        (b.ImageId, b.FullPath, b.PixelWidth, b.PixelHeight, b.Date, b.ModifiedAtUnix, b.FileSize, b.Favorite,
         b.RootId, b.DirRel, b.RelPath, b.TakenAtUnix, b.CreatedAtUnix, b.Rating, b.Nsfw, b.PageUrl, b.SourceUrl, b.Missing,
         b.IsVideo, b.DurationSeconds)
        && a.Tags.SequenceEqual(b.Tags);
    private static string ItemKey(PhotoItem item) => item.ImageId > 0 ? $"id:{item.ImageId}" : ImageLoader.Identity(item);
    private void ApplyItems(List<PhotoItem> items)
    {
        var previous = _items.ToDictionary(ItemKey);
        for (int i = 0; i < items.Count; i++)
            if (previous.TryGetValue(ItemKey(items[i]), out var old) && SameItem(old, items[i])) items[i] = old;
        SaveAnchor();
        _items.Clear(); _items.AddRange(items);
        _selected.IntersectWith(_items.Select(SelectionId));
        if (_selectionAnchorId is int anchor && !_items.Any(item => SelectionId(item) == anchor))
            _selectionAnchorId = null;
        if (_keyboardFocusId is int focus && !_items.Any(item => SelectionId(item) == focus))
            _keyboardFocusId = null;
        Panel.SetItems(_items);
        if (_items.Count == 0) ImageLoader.SetViewport([], []);
        ScheduleViewport();
        Timeline.IsAscending = _ascending;
        UpdateScrollbarType(); Timeline.Attach(Scroller, _items, Panel);
        UpdateDateHeader(); UpdateHeader(); UpdateEmptyState();
        ItemsChanged?.Invoke(_items.ToArray());
    }

    private static List<PhotoItem> MapRows(IEnumerable<ImageRow> rows, HashSet<int> favorites, string sortKey)
    {
        var result = new List<PhotoItem>();
        foreach (var row in rows)
        {
            double timestamp = sortKey switch
            {
                "modified" => row.Mtime,
                "created" => row.Ctime ?? row.Mtime,
                "added" => row.AddedAt,
                _ => row.TakenAt ?? row.Mtime,
            };
            DateTime date = DateTime.UnixEpoch.AddSeconds(timestamp).ToLocalTime();
            result.Add(new PhotoItem
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
                CommentaryTitle = row.CommentaryTitle,
                CommentaryDesc = row.CommentaryDesc,
                IsVideo = row.MediaKind == "video",
                DurationSeconds = row.Duration,
                Missing = row.Missing,
                Tags = row.Tags,
            });
        }
        return result;
    }

    /// <summary>目录树选择（null = 全部图库）；force = 目标没变也重建（移除目录后用）。</summary>
    public void SetDirectory(int? rootId, string? dirRel, bool force = false)
    {
        if (!force && _dirRootId == rootId && string.Equals(_dirRel ?? "", dirRel ?? "", StringComparison.Ordinal)) return;
        _dirRootId = rootId;
        _dirRel = dirRel;
        _rebuildPending = true;
        // photos 的页头标题就是当前集合名：根目录 = 根文件夹名，子目录 = 最后一段目录名
        _dirTitle = ResolveDirectoryTitle(rootId, dirRel);
        if (IsLoaded) Rebuild();
    }

    /// <summary>当前目录（null = 全部图库）；F5 刷新据此决定扫描范围。</summary>
    public (int? RootId, string? DirRel) CurrentDirectory => (_dirRootId, _dirRel);

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
        tile.SelectMode = _selectMode;
        tile.Selected = _selected.Contains(SelectionId(item));
        tile.KeyboardFocused = ShowKeyboardFocus && _keyboardFocusId == SelectionId(item);
        if (tile.EventsWired) return;
        tile.EventsWired = true;
        tile.ThumbnailInvalidated += ScheduleViewport;
        tile.Tapped += OnTileTapped;
        tile.RightTapped += OnTileRightTapped;
        tile.FavoriteRequested = ToggleFavoriteAsync;

        // 拖拽外发（galleryold drag_out）：按下起手、位移超阈值发起；
        // 拖已选中图 = 拖整个选区；Windows 标记复制语义（不搬移）。
        Windows.Foundation.Point? dragStart = null;
        tile.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(tile);
            if (point.Properties.IsLeftButtonPressed &&
                !(point.Position.X <= 44 && point.Position.Y <= 44))
            {
                // 起拖前也要跟踪移出卡片的移动，否则快速拖向侧栏会漏掉阈值事件。
                tile.CapturePointer(e.Pointer);
                dragStart = point.Position;
            }
        };
        tile.PointerMoved += async (_, e) =>
        {
            if (dragStart is null) return;
            var point = e.GetCurrentPoint(tile);
            var pos = point.Position;
            var dist = Math.Abs(pos.X - dragStart.Value.X) + Math.Abs(pos.Y - dragStart.Value.Y);
            if (dist < 12) return; // galleryold 用系统 startDragDistance，近似 12
            dragStart = null;
            tile.ReleasePointerCapture(e.Pointer);
            await StartDragOutAsync(tile, point);
        };
        tile.PointerReleased += (_, e) => { dragStart = null; tile.ReleasePointerCapture(e.Pointer); };
        tile.PointerCanceled += (_, e) => { dragStart = null; tile.ReleasePointerCapture(e.Pointer); };
        tile.PointerCaptureLost += (_, _) => dragStart = null;
        tile.Unloaded += (_, _) => { dragStart = null; tile.ReleasePointerCaptures(); };
    }

    /// <summary>
    /// 模糊保护模式下，含敏感分级的图不允许拖出（拖到资源管理器就等于绕过遮罩）。
    /// 抽成静态方法便于 --verify-drag-guard 直接验逻辑。
    /// </summary>
    internal static bool IsBlurBlocked(IReadOnlyList<PhotoItem> items, out string message)
    {
        message = "";
        if (AppService.Settings?.Settings.nsfw_display != "blur") return false;
        var blocked = items
            .Where(it => it.NeedsProtection)
            .Select(it => it.Name)
            .ToList();
        if (blocked.Count == 0) return false;
        message = $"敏感内容已模糊，不能拖出：{blocked[0]}" +
            (blocked.Count > 1 ? $" 等 {blocked.Count} 张" : "");
        return true;
    }

    /// <summary>拖拽外发：文件 URI 交给系统（CF_HDROP），复制语义防同盘搬移；
    /// 同时带上图片 ID，供侧栏目录在应用内识别为移动。</summary>
    private async System.Threading.Tasks.Task StartDragOutAsync(PhotoTile tile, Microsoft.UI.Input.PointerPoint start)
    {
        int id = SelectionId(tile.Item);
        if (_selectMode && !_selected.Contains(id))
        {
            _selected.Clear();
            _selected.Add(id);
            _selectionAnchorId = id;
            SyncSelection();
        }
        var items = new List<PhotoItem>();
        if (_selected.Contains(id))
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

        // 模糊保护中拒绝拖出：拖出到资源管理器等于绕过遮罩看到原图
        if (IsBlurBlocked(items, out var blockedMessage))
        {
            SendToast(blockedMessage, Gallery.Controls.ToastKind.Pending);
            return;
        }

        // 跟手缩略图（96px）而不是整张卡片，避免拖动时挡住目标位置。
        var dragVisual = await BuildDragVisualAsync(items[0]);

        // StartDragAsync 使用 DragStarting 的数据包；局部创建的 DataPackage 不会交给系统。
        void OnDragStarting(UIElement sender, DragStartingEventArgs args)
        {
            args.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            args.Data.SetStorageItems(storageFiles);
            var dragIds = items.Where(it => it.ImageId > 0).Select(it => it.ImageId).Distinct().ToArray();
            if (dragIds.Length > 0)
                args.Data.SetData(DragImageIdsFormat, string.Join(",", dragIds));
            if (dragVisual is not null)
            {
                try
                {
                    args.DragUI.SetContentFromSoftwareBitmap(dragVisual,
                        new Windows.Foundation.Point(dragVisual.PixelWidth / 2.0, dragVisual.PixelHeight / 2.0));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                    or ObjectDisposedException)
                {
                    AppService.DebugLog("drag visual failed: " + ex.Message);
                }
            }
        }
        tile.DragStarting += OnDragStarting;
        try { await tile.StartDragAsync(start); }
        finally
        {
            tile.DragStarting -= OnDragStarting;
            dragVisual?.Dispose();
        }
    }

    /// <summary>解码一张 96px 缩略图给系统拖拽层；失败时退回系统默认拖拽外观。</summary>
    private static async Task<SoftwareBitmap?> BuildDragVisualAsync(PhotoItem item)
    {
        try
        {
            var preview = await ImageLoader.LoadThumbnailAsync(item, 96, ImageLoader.BlurStrength(item),
                CancellationToken.None);
            if (preview is not WriteableBitmap bitmap) return null;
            return SoftwareBitmap.CreateCopyFromBuffer(bitmap.PixelBuffer, BitmapPixelFormat.Bgra8,
                bitmap.PixelWidth, bitmap.PixelHeight, BitmapAlphaMode.Premultiplied);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// 虚拟化视口对账：视口 ± 缓冲区内的索引才有瓦片，其余回收。
    /// 选中/事件按数据索引走，不依赖控件实例存活。
    /// </summary>
    private bool _viewportQueued;
    private string? _restoreAnchor;
    private double _restoreDelta;
    private bool _active = true;

    private void ScheduleViewport()
    {
        if (_viewportQueued || !_active) return;
        _viewportQueued = true;
        DispatcherQueue.TryEnqueue(() => { _viewportQueued = false; if (_active) ReconcileViewport(); });
    }
    private void OnLayoutReady()
    {
        if (_restoreAnchor is not null)
        {
            int index = _items.FindIndex(i => ItemKey(i) == _restoreAnchor);
            if (index >= 0)
            {
                double offset = Math.Max(0, Panel.TopOfIndex(index) - _restoreDelta);
                DispatcherQueue.TryEnqueue(() => { Scroller.UpdateLayout(); Scroller.ChangeView(null, offset, null, true); });
            }
            _restoreAnchor = null;
        }
        Timeline.Refresh();
        if (_showKeyboardFocus && _keyboardFocusId is int focus)
            DispatcherQueue.TryEnqueue(() => RevealKeyboardFocus(_items.FindIndex(item => SelectionId(item) == focus)));
        UpdateScrollbarType();
        ScheduleViewport();
    }
    private void SaveAnchor()
    {
        if (_items.Count == 0) return;
        int index = Math.Clamp(Panel.FirstVisibleIndex(Scroller.VerticalOffset), 0, _items.Count - 1);
        _restoreAnchor = ItemKey(_items[index]);
        _restoreDelta = Panel.TopOfIndex(index) - Scroller.VerticalOffset;
    }
    public void SetActive(bool active)
    {
        _active = active;
        ImageLoader.SetGalleryActive(active);
        if (!active) { SyncSelection(); return; }
        RefreshTileBindings();
        if (_rebuildPending) RebuildKeepScroll();
        else ScheduleViewport();
    }
    private void RefreshTileBindings()
    {
        foreach (var tile in Panel.Children)
        {
            tile.Bind(tile.Item);
            WireTile(tile, tile.Item);
        }
        ScheduleViewport();
    }
    /// <summary>
    /// 右键菜单改收藏只落在模型上，已实现的瓦片不会自己重读；
    /// 重绑一次让红心立刻跟着变（Bind 里解析相同会跳过缩略图重载）。
    /// </summary>
    private void SyncFavoriteTiles()
    {
        foreach (var tile in Panel.Children) tile.Bind(tile.Item);
    }
    private void ReconcileViewport()
    {
        if (!_active || _items.Count == 0) return;
        double height = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
        if (height <= 0) return;
        var visible = Panel.VisibleRange(Scroller.VerticalOffset, height, 0);
        var nearby = Panel.VisibleRange(Scroller.VerticalOffset, height, height);
        var current = _items.Skip(visible.First).Take(Math.Max(0, visible.Last - visible.First + 1));
        var desired = _items.Skip(nearby.First).Take(Math.Max(0, nearby.Last - nearby.First + 1));
        ImageLoader.SetViewport(current, desired);
        double dpi = XamlRoot?.RasterizationScale ?? 1;
        foreach (var tile in Panel.Children)
        {
            int index = Panel.IndexOfTile(tile);
            if (index < 0 || index >= Panel.Rects.Count) continue;
            var rect = Panel.Rects[index];
            tile.RequestThumbnail(Math.Max(rect.Width, rect.Height) * dpi,
                index >= visible.First && index <= visible.Last ? ImagePriority.Visible : ImagePriority.Prefetch);
        }
    }

    private bool IsDateSort => _sortKey is "taken" or "created" or "modified" or "added";

    /// <summary>
    /// 对应 Photos 的 GalleryViewModel.EvaluateScrollbarType + ShouldShowDateRelatedUI：
    /// 未加载 / 不可滚动 → 两个都不显示；名称、大小、星级、随机排序 → 系统默认滚动条；
    /// 日期排序且可滚动 → 时间线。
    /// </summary>
    private void UpdateScrollbarType()
    {
        bool loaded = _items.Count > 0 && IsLoaded;
        bool scrollable = loaded && Panel.ActualHeight > Scroller.ViewportHeight + 0.5;
        bool dateSort = IsDateSort;
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
        if (_items.Count == 0 || !IsDateSort)
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
        _rebuildPending = true;
        RefreshTileBindings();
        if (!IsLoaded || !_active) return;
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
                    : "把图片放进左侧已添加的文件夹，图库会自动更新。";
    }

    private void UpdateHeader()
    {
        int n = _selected.Count;
        string pageName = _favoritesMode ? "收藏夹" : _dirTitle ?? "图库";
        IconAndTitleHeader.Title = n > 0 ? $"已选择 {n} 个项目" : pageName;
        SubtitleText.Text = n > 0
            ? pageName
            : $"{_items.Count} {(_items.Any(i => i.IsVideo) ? "个项目" : "张照片")}";
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

    /// <summary>筛选面板变化：媒体类型/标签全部立刻生效（Photos 即时筛选）。</summary>
    private void ApplyFilterPanel()
    {
        _filter = _filterPanel.Media switch
        {
            Controls.FilterPanel.MediaKind.Photos => FilterKind.Photos,
            Controls.FilterPanel.MediaKind.Videos => FilterKind.Videos,
            _ => FilterKind.All,
        };
        if (AppService.Settings.Settings.recursive != _filterPanel.Subfolder)
        {
            AppService.Settings.Settings.recursive = _filterPanel.Subfolder;
            AppService.Settings.Save();
        }
        Rebuild();
    }
    private void SyncFilterMenuChecks()
    {
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
        _filterPanel.SyncSubfolder(AppService.Settings.Settings.recursive, inFolder);
    }

    private static bool IsCtrlDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsShiftDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>窗口预览按键的唯一图库入口；输入/弹层隔离由窗口负责。</summary>
    public async Task HandleGalleryKeyAsync(KeyRoutedEventArgs e, bool contentFocus)
    {
        bool ctrl = IsCtrlDown(), shift = IsShiftDown();
        bool arrow = e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down;
        if ((arrow || e.Key is VirtualKey.Space or VirtualKey.Enter or VirtualKey.Delete or VirtualKey.F2 or VirtualKey.F3)
            && !contentFocus) return;
        if (ctrl && !arrow && e.Key is not (VirtualKey.A or VirtualKey.C)) return;
        if (!(arrow || e.Key is VirtualKey.Space or VirtualKey.Enter or VirtualKey.Delete or VirtualKey.F2 or VirtualKey.F3
            || ctrl && e.Key is VirtualKey.A or VirtualKey.C)) return;
        if (_items.Count == 0) return;
        e.Handled = true; // 文件/剪贴板操作的首个 await 前截断路由。
        int previous = _keyboardFocusId is int focusId ? _items.FindIndex(item => SelectionId(item) == focusId) : -1;
        int index = previous;
        if (index < 0)
        {
            index = _items.FindIndex(item => _selected.Contains(SelectionId(item)));
            if (index < 0) index = Math.Clamp(Panel.FirstVisibleIndex(Scroller.VerticalOffset), 0, _items.Count - 1);
        }
        Root.Focus(FocusState.Keyboard);
        if (arrow)
        {
            if (previous >= 0) index = NeighborIndex(index, e.Key);
            if (shift && previous >= 0)
            {
                _selectionAnchorId ??= SelectionId(_items[previous]);
                SelectRange(index, ctrl);
            }
            SetKeyboardFocus(index);
            return;
        }
        SetKeyboardFocus(index);
        var focusItem = _items[index];
        var targets = SelectedItems().ToList();
        if (targets.Count == 0) targets.Add(focusItem);
        switch (e.Key)
        {
            case VirtualKey.A when ctrl:
                SelectAll();
                _selectionAnchorId ??= _keyboardFocusId;
                break;
            case VirtualKey.Space:
                SetSelectMode(true);
                int id = SelectionId(focusItem);
                if (!_selected.Add(id)) _selected.Remove(id);
                _selectionAnchorId = id;
                SyncSelection();
                break;
            case VirtualKey.Enter:
                RevealPath(focusItem.FullPath);
                break;
            case VirtualKey.Delete:
                await DeleteAsync(targets.Where(item => item.ImageId > 0).Select(item => item.ImageId).ToArray());
                break;
            case VirtualKey.F2:
                await RenameAsync(focusItem);
                break;
            case VirtualKey.F3:
                FileActions.RevealInExplorer(focusItem.FullPath);
                break;
            case VirtualKey.C when ctrl && shift:
                var paths = new Windows.ApplicationModel.DataTransfer.DataPackage();
                paths.SetText(string.Join("\n", targets.Select(item => item.FullPath)));
                try
                {
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(paths);
                    SendToast("已复制路径", Gallery.Controls.ToastKind.Success);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
                { SendToast("剪贴板暂不可用，复制路径失败", Gallery.Controls.ToastKind.Pending, ex.Message); }
                break;
            case VirtualKey.C when ctrl:
                await CopyFilesToClipboardAsync(targets);
                break;
        }
    }

    public bool HasContentKeyboardFocus(DependencyObject? focused)
    {
        if (ReferenceEquals(focused, Root)) return true;
        for (var current = focused; current is not null && current != Root;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or RangeBase or PaneResizer or TimelineScrollbar or ContinuousScrollBar) return false;
            if (ReferenceEquals(current, Scroller) || current is PhotoTile) return true;
        }
        return false;
    }

    private void SetKeyboardFocus(int index)
    {
        _keyboardFocusId = SelectionId(_items[index]);
        _showKeyboardFocus = true;
        _restoreAnchor = null;
        SyncSelection();
        RevealKeyboardFocus(index);
    }

    private void RevealKeyboardFocus(int index)
    {
        if (Panel.IsBuilding || index < 0 || index >= Panel.Rects.Count) return;
        var rect = Panel.TransformToVisual(Scroller).TransformBounds(Panel.Rects[index]);
        double top = rect.Top - PhotoTile.KeyboardFocusOutset;
        double bottom = rect.Bottom + PhotoTile.KeyboardFocusOutset;
        double offset = Scroller.VerticalOffset;
        if (top < 0) offset += top;
        else if (bottom > Scroller.ViewportHeight)
            offset += bottom - top > Scroller.ViewportHeight ? top : bottom - Scroller.ViewportHeight;
        offset = Math.Clamp(offset, 0, Scroller.ScrollableHeight);
        if (Math.Abs(offset - Scroller.VerticalOffset) > .5) Scroller.ChangeView(null, offset, null, true);
    }

    private int NeighborIndex(int index, VirtualKey key)
    {
        var rects = Panel.Rects;
        if (Panel.IsBuilding || rects.Count != _items.Count) return index;
        var rect = rects[index];
        if (key == VirtualKey.Left) return Math.Max(0, index - 1);
        if (key == VirtualKey.Right) return Math.Min(_items.Count - 1, index + 1);
        int step = key == VirtualKey.Up ? -1 : 1;
        int next = index + step;
        while (next >= 0 && next < rects.Count && rects[next].Top == rect.Top) next += step;
        if (next < 0 || next >= rects.Count) return index;
        double row = rects[next].Top, center = rect.Left + rect.Width / 2, bestDistance = double.MaxValue;
        int best = next;
        for (int i = next; i >= 0 && i < rects.Count && rects[i].Top == row; i += step)
        {
            double distance = Math.Abs(rects[i].Left + rects[i].Width / 2 - center);
            if (distance < bestDistance || distance == bestDistance && i < best)
            { best = i; bestDistance = distance; }
        }
        return best;
    }

    private void SelectRange(int index, bool additive)
    {
        int anchor = _selectionAnchorId is int id ? _items.FindIndex(item => SelectionId(item) == id) : -1;
        if (anchor < 0) { anchor = index; _selectionAnchorId = SelectionId(_items[index]); }
        SetSelectMode(true);
        if (!additive) _selected.Clear();
        for (int i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++)
            _selected.Add(SelectionId(_items[i]));
        SyncSelection();
    }

    private async Task CopyFilesToClipboardAsync(IReadOnlyList<PhotoItem> items)
    {
        var files = new List<Windows.Storage.StorageFile>();
        foreach (var item in items)
        {
            try { files.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(item.FullPath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or System.Runtime.InteropServices.COMException)
            {
                SendToast($"{item.Name}：文件无法读取，复制已取消", Gallery.Controls.ToastKind.Pending, ex.Message);
                return;
            }
        }
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage
        { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
        data.SetStorageItems(files, true);
        try
        {
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            SendToast($"已复制 {files.Count} 个文件", Gallery.Controls.ToastKind.Success);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        { SendToast("剪贴板暂不可用，复制失败", Gallery.Controls.ToastKind.Pending, ex.Message); }
    }

    /// <summary>标签编辑器（批次 3）：Photos 弹窗形态，单图/多图共用；保存后刷新标签与卡片徽章。</summary>
    public async System.Threading.Tasks.Task EditTagsAsync(IReadOnlyList<PhotoItem> items)
    {
        if (items.Count == 0 || XamlRoot is null) return;
        if (AppService.Store is null) { SendToast("数据库未就绪", Gallery.Controls.ToastKind.Pending, null); return; }

        bool saved = await Gallery.Controls.Tags.TagEditorDialog.ShowAsync(XamlRoot, items, SendToast);
        if (!saved) return;

        SendToast(items.Count == 1 ? "标签已保存" : $"已保存 {items.Count} 张的标签", Gallery.Controls.ToastKind.Success);
        RefreshFromStore();
        TagsEdited?.Invoke();
    }

    /// <summary>
    /// photos 语义：正常模式单击=打开查看器；选择模式单击=切换选中；
    /// Ctrl+单击切换单张；Shift 按当前完整列表选范围，Ctrl+Shift 追加范围。
    /// </summary>
    private void OnTileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not PhotoTile tile) return;
        int id = SelectionId(tile.Item);
        _keyboardFocusId = id;
        _showKeyboardFocus = false;
        SyncSelection();
        Root.Focus(FocusState.Pointer);
        bool ctrl = IsCtrlDown(), shift = IsShiftDown();
        if (_selectMode || ctrl || shift)
        {
            SetSelectMode(true);
            if (shift)
            {
                int index = _items.FindIndex(item => SelectionId(item) == id);
                if (index < 0) return; // 列表刷新后旧瓦片的点击不再参与范围计算。
                SelectRange(index, ctrl);
            }
            else
            {
                if (!_selected.Add(id)) _selected.Remove(id);
                _selectionAnchorId = id;
            }
            SyncSelection();
            e.Handled = true;
            return;
        }
        _selectionAnchorId = id;
        OpenPreview(tile);
    }

    public Task<bool> ToggleFavoriteAsync(PhotoItem item)
        => ToggleFavoriteAsync(item, !item.Favorite);

    /// <summary>持久化成功后才返回新状态，按钮不会出现“只变色但没写库”。</summary>
    public Task<bool> ToggleFavoriteAsync(PhotoItem item, bool favorite)
    {
        if (item.ImageId <= 0 || AppService.Store is null) return Task.FromResult(item.Favorite);
        try
        {
            lock (AppService.DbLock)
                AppService.Store.SetFavorite(item.ImageId, favorite);
            item.Favorite = favorite;
            AppService.Recount();
            if (_favoritesMode) Rebuild(); else UpdateHeader();
            return Task.FromResult(favorite);
        }
        catch (Exception ex)
        {
            SendToast("收藏状态保存失败", Gallery.Controls.ToastKind.Pending, ex.Message);
            return Task.FromResult(item.Favorite);
        }
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

    /// <summary>
    /// 外部按绝对路径定位并打开查看器（浏览器扩展的「在图库中查看」走这里）。
    /// 命中返回 true；文件还没被收录返回 false。
    /// </summary>
    public bool RevealPath(string absPath)
    {
        if (absPath.Length == 0 || _items.Count == 0) return false;
        var target = absPath.Replace('/', '\\');
        var index = -1;
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].FullPath.Replace('/', '\\'), target, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index < 0) return false;

        var item = _items[index];
        PreviewRequested?.Invoke(new PreviewRequest(
            item, _items.ToArray(), default, true, ResolveSourceRect));
        return true;
    }

    private void OpenPreview(PhotoTile tile)
    {
        var (rect, fully) = TileFlightRect(tile) ?? (default, false);
        PreviewRequested?.Invoke(new PreviewRequest(tile.Item, _items.ToArray(), rect, fully, ResolveSourceRect, tile.PreviewThumbnail));
    }

    private Windows.Foundation.Rect? ResolveSourceRect(PhotoItem item)
    {
        int index = _items.FindIndex(candidate => SelectionId(candidate) == SelectionId(item));
        if (index < 0 || index >= Panel.Rects.Count) return null;
        return Panel.TransformToVisual(null).TransformBounds(Panel.Rects[index]);
    }

    private bool ShowKeyboardFocus => _active && _showKeyboardFocus && XamlRoot is not null
        && HasContentKeyboardFocus(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject);

    /// <summary>鼠标输入隐藏键盘定位框，保留图片身份及选区。</summary>
    public void HideKeyboardFocus()
    {
        _showKeyboardFocus = false;
        SyncSelection();
    }

    private void SyncSelection()
    {
        bool showFocus = ShowKeyboardFocus;
        foreach (var tile in Panel.Children.OfType<PhotoTile>())
        {
            tile.Selected = _selected.Contains(SelectionId(tile.Item));
            tile.KeyboardFocused = showFocus && _keyboardFocusId == SelectionId(tile.Item);
        }
        UpdateHeader();
    }

    private void OnTileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not PhotoTile tile) return;
        _keyboardFocusId = SelectionId(tile.Item);
        _showKeyboardFocus = false;
        SyncSelection();
        if (!tile.Selected)
        {
            _selected.Clear();
            _selected.Add(SelectionId(tile.Item));
            _selectionAnchorId = SelectionId(tile.Item);
            SyncSelection();
        }

        // photos/galleryold：右键菜单在鼠标指针处弹出（ShowAttachedFlyout 会锚到整个瓦片）
        var position = e.GetPosition(tile);
        FlyoutBase.SetAttachedFlyout(tile, BuildContextMenu(tile.Item));
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
        // 选择模式下右键只列可批量执行的命令（打开/重命名/复制路径这些单图操作不出现）；
        // 非选择模式与查看器内（includeOpen=false）保持完整菜单，且只作用于当前这张。
        bool batching = includeOpen && _selectMode;
        var items = batching && _selected.Contains(SelectionId(item))
            ? SelectedItems().ToList()
            : new List<PhotoItem> { item };
        var ids = items.Where(x => x.ImageId > 0).Select(x => x.ImageId).ToList();
        if (includeOpen && !batching)
            menu.Items.Add(Item("打开", "\uE8E5", () =>
            {
                var t = Panel.Children.OfType<PhotoTile>().FirstOrDefault(x => x.Item == item);
                if (t is not null) OpenPreview(t);
            }));
        // 「选择」是进入多选的入口；已经在多选里时不再出现。
        if (includeOpen && !_selectMode)
            menu.Items.Add(Item("选择", "\uE73E", () => SelectFromMenu(item)));
        // 收藏夹：整批套用同一动作——全都没收藏（含混合）就全部收藏，已收藏的跳过；全都收藏了就全部移出
        bool allFavorited = items.All(x => x.Favorite);
        menu.Items.Add(Item(allFavorited ? "从收藏夹中移除" : "添加到收藏夹", allFavorited ? "\uEB52" : "\uEB51", () =>
        {
            var favorite = !allFavorited;
            lock (AppService.DbLock)
                foreach (var id in ids) AppService.Store!.SetFavorite(id, favorite);
            foreach (var target in items) target.Favorite = favorite;
            if (_favoritesMode) Rebuild();
            else { UpdateHeader(); SyncFavoriteTiles(); }
            ToastKindRequested?.Invoke(items.Count == 1
                    ? (favorite ? "已添加到收藏夹" : "已从收藏夹中移除")
                    : (favorite ? $"已添加 {items.Count} 张到收藏夹" : $"已移除 {items.Count} 张的收藏"),
                Gallery.Controls.ToastKind.Success, null);
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("编辑标签", "\uE8EC", () => _ = EditTagsAsync(items)));
        if (!batching)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            if (!item.IsVideo)
                menu.Items.Add(Item("复制图片内容", "\uE8C8", () => _ = CopyImageToClipboardAsync(item)));
            menu.Items.Add(Item("复制为路径", "\uE71B", () =>
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(item.FullPath);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                ToastRequested?.Invoke("已复制路径");
            }));
            // 视频不能做桌面壁纸；非图片格式由系统判定，失败会提示。
            if (!item.IsVideo)
                menu.Items.Add(Item("设置为桌面背景", "\uE91B", () => SetWallpaper(item)));
        }
        menu.Items.Add(Item("复制/移动", "\uE8DE", () => _ = CopyMoveAsync(ids)));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (!batching)
        {
            menu.Items.Add(Item("在文件资源管理器中打开", "\uEC50", () =>
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
            }));
            menu.Items.Add(Item("重命名", "\uE8AC", () => _ = RenameAsync(item)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        // 手动标分级：模型接入前的兜底（g/s/q/e 对齐 danbooru 四级，q/e 算敏感）
        var ratingMenu = new MenuFlyoutSubItem { Text = "标记分级" };
        foreach (var level in new[] { "g", "s", "q", "e" })
        {
            var rating = level;
            var mark = new MenuFlyoutItem { Text = RatingText.Level(level) };
            mark.Click += (_, _) =>
            {
                if (ids.Count == 0) return;
                lock (AppService.DbLock)
                    foreach (var id in ids)
                        AppService.Store!.SetNsfwScore(id, "manual", rating, tagIt: true);
                foreach (var target in items) target.Nsfw = rating;
                Rebuild();
                ToastKindRequested?.Invoke(
                    items.Count == 1
                        ? $"已标记为「{RatingText.Level(rating)}」"
                        : $"已标记 {items.Count} 张为「{RatingText.Level(rating)}」",
                    Gallery.Controls.ToastKind.Success, null);
            };
            ratingMenu.Items.Add(mark);
        }
        menu.Items.Add(ratingMenu);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = Item("删除", "\uE74D", () => _ = DeleteAsync(ids));
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

    /// <summary>SystemParametersInfo 设置桌面背景；超过 20 MB 时只提示、不提交。</summary>
    private void SetWallpaper(PhotoItem item)
    {
        if (item.FileSize > WallpaperService.MaxBytes)
        {
            SendToast("图片超过 20 MB，未设置为桌面背景", Gallery.Controls.ToastKind.Pending);
            return;
        }
        if (WallpaperService.TrySet(item.FullPath, out var error))
        {
            SendToast("已设置为桌面背景", Gallery.Controls.ToastKind.Success);
            return;
        }
        SendToast("设置桌面背景失败：" + error, Gallery.Controls.ToastKind.Pending);
    }

    /// <summary>删除选中（回收站 + 二次确认，取消为默认，红 #ff99a4）。</summary>
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
        if (await Gallery.Services.DialogService.ShowAsync(confirm) != ContentDialogResult.Primary) return;

        FileOperationStarting?.Invoke();
        var (done, errors) = await RunFileOperationAsync(() =>
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
        var result = await Gallery.Services.DialogService.ShowAsync(dialog);
        if (result != ContentDialogResult.Primary) return;
        var newName = input.Text.Trim();
        if (newName.Length == 0 || newName == stem) return;
        FileOperationStarting?.Invoke();
        var (ok, error) = await RunFileOperationAsync(() =>
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
            if (await Gallery.Services.DialogService.ShowAsync(confirm) != ContentDialogResult.Primary) return;
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
            var result = await Gallery.Services.DialogService.ShowAsync(dialog);
            if (result == ContentDialogResult.None) return;
            move = result == ContentDialogResult.Primary;
        }
        if (move) FileOperationStarting?.Invoke();
        var (count, firstError) = await RunFileOperationAsync(() =>
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

    /// <summary>侧栏目录拖放：二次确认后批量移动到该目录；返回是否真的移动了项目。</summary>
    public async Task<bool> MoveImagesToFolderAsync(IReadOnlyList<int> ids, string directory, string displayName)
    {
        var targets = ids.Where(id => id > 0).Distinct().ToList();
        if (targets.Count == 0) return false;
        var confirm = new ContentDialog
        {
            Title = "移动",
            Content = $"将 {targets.Count} 个项目移动到「{displayName}」？\n{directory}",
            PrimaryButtonText = "移动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await Gallery.Services.DialogService.ShowAsync(confirm) != ContentDialogResult.Primary)
            return false;

        FileOperationStarting?.Invoke();
        var (count, firstError) = await RunFileOperationAsync(() =>
        {
            lock (AppService.DbLock)
            {
                try
                {
                    var (done, errors) = FileActions.MoveImages(AppService.Store!, targets, directory);
                    return (done.Count, errors.Count > 0 ? errors[0] : null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or InvalidOperationException)
                {
                    return (0, ex.Message);
                }
                finally { AppService.Recount(); }
            }
        });
        if (count > 0) Rebuild();
        if (firstError is not null)
            SendToast($"移动失败：{firstError}", Gallery.Controls.ToastKind.Pending);
        else if (count == 0)
            SendToast("这些项目已经在目标文件夹中", Gallery.Controls.ToastKind.Info);
        else
            SendToast($"已移动 {count} 个项目", Gallery.Controls.ToastKind.Success);
        return count > 0;
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

    /// <summary>右键「选择」：进入多选，并只选中当前这张。</summary>
    private void SelectFromMenu(PhotoItem item)
    {
        int id = SelectionId(item);
        SetSelectMode(true);
        _selected.Clear();
        _selected.Add(id);
        _selectionAnchorId = id;
        SyncSelection();
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
            _selectionAnchorId = null;
            SyncSelection();
        }
        UpdateHeader();
    }

    /// <summary>
    /// Esc 退出多选（photos 语义）。返回是否真的处理了这次按键：
    /// true = 已清空选区并退出选择模式，供窗口级 Esc 加速器决定是否吞掉按键。
    /// </summary>
    public bool ExitSelection()
    {
        _showKeyboardFocus = false;
        SyncSelection();
        if (!_selectMode && _selected.Count == 0) return false;
        SetSelectMode(false); // 内部清空选区并同步
        return true;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();

    private void SelectAll()
    {
        SetSelectMode(true);
        _selected.UnionWith(_items.Select(SelectionId));
        SyncSelection();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        _selected.Clear();
        _selectionAnchorId = null;
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
        SortRandom.IsChecked = _sortKey == "random";
        SortAsc.IsChecked = _ascending;
        SortDesc.IsChecked = !_ascending;
    }


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
        SaveAnchor();
        Panel.Mode = mode;
        LayoutIcon.Glyph = mode == GalleryLayoutMode.Grid ? "\uE8A9" : "\uECA5";
        PersistLayout();
        Panel.InvalidateMeasure();
        Timeline.Refresh();
        SyncLayoutMenuChecks();
    }

    private void ApplySize(ThumbnailSize size)
    {
        SaveAnchor();
        Panel.Size = size;
        PersistLayout();
        Panel.InvalidateMeasure();
        SyncLayoutMenuChecks();
    }

    private void RebuildKeepScroll() => Rebuild();
    private readonly HashSet<Task> _fileOperations = [];

    private async Task<T> RunFileOperationAsync<T>(Func<T> operation)
    {
        var task = Task.Run(operation);
        _fileOperations.Add(task);
        try { return await task; }
        finally { _fileOperations.Remove(task); }
    }

    public Task DrainFileOperationsAsync() => Task.WhenAll(_fileOperations.ToArray());
}
