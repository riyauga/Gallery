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
    private SortKey _sort = SortKey.DateTaken;
    private bool _ascending = false;
    private FilterKind _filter = FilterKind.All;
    private string _query = string.Empty;

    private double _lastWidth;
    private int _anchorIndex;
    private double _anchorDelta;
    private bool? _lastTimelineVisible;
    private bool? _lastDefaultBar;

    public event Action<string>? ToastRequested;
    public event Action<PhotoItem, IReadOnlyList<PhotoItem>>? PreviewRequested;

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
    }

    public void SetSearch(string query)
    {
        _query = (query ?? string.Empty).Trim();
        if (IsLoaded) Rebuild();
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

        Panel.Children.Clear();
        foreach (var item in _items)
        {
            var tile = new PhotoTile(item);
            tile.Tapped += OnTileTapped;
            tile.DoubleTapped += OnTileDoubleTapped;
            tile.RightTapped += OnTileRightTapped;
            FlyoutBase.SetAttachedFlyout(tile, BuildContextMenu(item));
            Panel.Children.Add(tile);
        }

        Panel.InvalidateMeasure();
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, 0, null, true);

        Timeline.IsAscending = _ascending;
        UpdateScrollbarType();
        Timeline.Attach(Scroller, _items, Panel);
        UpdateDateHeader();
        UpdateHeader();
        UpdateEmptyState();
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

    private void UpdateEmptyState()
    {
        bool empty = _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        EmptyTitle.Text = _filter == FilterKind.Videos ? "没有视频" : "这里还没有照片";
        EmptySubtitle.Text = _filter == FilterKind.Videos ? "尝试将筛选器改为“所有媒体”。" : "把照片放进 assets 文件夹后重新启动。";
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

    private void OnTileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not PhotoTile tile) return;
        if (IsCtrlDown())
        {
            tile.Selected = !tile.Selected;
        }
        else
        {
            foreach (var other in _selected.ToArray()) other.Selected = false;
            _selected.Clear();
            tile.Selected = true;
        }

        SyncSelection();
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
        PreviewRequested?.Invoke(tile.Item, _items);
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

        DispatcherQueue.TryEnqueue(() => FlyoutBase.ShowAttachedFlyout(tile));
    }

    private MenuFlyout BuildContextMenu(PhotoItem item)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("打开", "\uE8E5", () => PreviewRequested?.Invoke(item, _items)));
        menu.Items.Add(Item(item.Favorite ? "从收藏夹中移除" : "添加到收藏夹", item.Favorite ? "\uEB52" : "\uEB51", () =>
        {
            item.Favorite = !item.Favorite;
            if (_favoritesMode) Rebuild(); else UpdateHeader();
            ToastRequested?.Invoke(item.Favorite ? "已添加到收藏夹" : "已从收藏夹中移除");
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("复制", "\uE8C8", () => ToastRequested?.Invoke("演示模式：未复制文件")));
        menu.Items.Add(Item("复制为路径", "\uE71B", () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(item.FullPath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            ToastRequested?.Invoke("已复制路径");
        }));
        menu.Items.Add(Item("移动/复制", "\uE8DE", () => ToastRequested?.Invoke("演示模式：未移动或复制文件")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("在文件资源管理器中打开", "\uEC50", () =>
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("删除", "\uE74D", () => ToastRequested?.Invoke("演示模式：未删除文件")));
        return menu;
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

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count > 0)
        {
            foreach (var t in _selected.ToArray()) t.Selected = false;
            _selected.Clear();
            SyncSelection();
            return;
        }
        ToastRequested?.Invoke("单击照片即可选择，Ctrl+单击可多选");
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

    private void SortByDateTaken_Click(object sender, RoutedEventArgs e) => ApplySort(SortKey.DateTaken);
    private void SortByDateCreated_Click(object sender, RoutedEventArgs e) => ApplySort(SortKey.DateCreated);
    private void SortByDateModified_Click(object sender, RoutedEventArgs e) => ApplySort(SortKey.DateModified);
    private void SortByName_Click(object sender, RoutedEventArgs e) => ApplySort(SortKey.Name);
    private void SortAscending_Click(object sender, RoutedEventArgs e) { _ascending = true; RebuildKeepScroll(); }
    private void SortDescending_Click(object sender, RoutedEventArgs e) { _ascending = false; RebuildKeepScroll(); }

    private void ApplySort(SortKey key)
    {
        _sort = key;
        RebuildKeepScroll();
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
