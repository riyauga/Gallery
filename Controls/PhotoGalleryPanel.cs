using Gallery.Models;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using Windows.Foundation;

namespace Gallery.Controls;

public enum GalleryLayoutMode { River, Grid }
public enum ThumbnailSize { Small, Medium, Large }

/// <summary>图库虚拟化宿主：ItemsRepeater 管控件回收，后台快照管行位置；对外保留时间线和飞入动画所需的几何接口。</summary>
public sealed class PhotoGalleryPanel : Grid
{
    private readonly ItemsRepeater _repeater = new() { HorizontalCacheLength = 0, VerticalCacheLength = 0 };
    private readonly GalleryItems _source = new();
    private readonly HashSet<PhotoTile> _realized = new();
    private readonly GalleryVirtualLayout _layout;
    private GalleryLayoutIndex _index = GalleryLayoutIndex.Build([], GalleryLayoutMode.River, ThumbnailSize.Medium, 0);
    private GalleryLayoutMode _mode;
    private ThumbnailSize _size = ThumbnailSize.Medium;
    private long _revision, _requestedRevision = -1;
    private double _requestedWidth = -1;
    private (double[] Aspects, GalleryLayoutMode Mode, ThumbnailSize Size, double Width, long Revision)? _pending;
    private bool _building;
    public long LayoutBuildCount { get; private set; }
    public long LayoutMilliseconds { get; private set; }
    public bool IsBuilding => _building;
    internal double RealizationBufferRatio { get; init; } = 1;
    public event Action<PhotoTile>? TilePrepared;
    public event Action? LayoutReady;
    public event Action? ViewportInvalidated;
    public DataTemplate TileTemplate { set => _repeater.ItemTemplate = value; }
    public new ICollection<PhotoTile> Children => _realized;
    internal IReadOnlyList<PhotoItem> Items => _source;
    public IReadOnlyList<Rect> Rects => _index.Rects;
    public bool IsVirtual => true;
    public GalleryLayoutMode Mode { get => _mode; set { if (_mode == value) return; _mode = value; Changed(); } }
    public ThumbnailSize Size { get => _size; set { if (_size == value) return; _size = value; Changed(); } }
    public double Gutter => _index.Mode == Mode && _index.Size == Size ? _index.Gutter : GalleryLayoutIndex.Build([], Mode, Size, 0).Gutter;
    public (double Min, double Target, double Max) SizeRange => GalleryLayoutIndex.Build([], Mode, Size, 0).SizeRange;

    public PhotoGalleryPanel()
    {
        _layout = new GalleryVirtualLayout(this);
        _repeater.Layout = _layout;
        _repeater.ItemsSource = _source;
        _repeater.ElementPrepared += (_, args) =>
        {
            var tile = (PhotoTile)args.Element;
            tile.Bind(_source[args.Index]);
            _realized.Add(tile);
            TilePrepared?.Invoke(tile);
            ViewportInvalidated?.Invoke();
        };
        _repeater.ElementClearing += (_, args) =>
        {
            var tile = (PhotoTile)args.Element;
            _realized.Remove(tile);
            tile.Recycle();
        };
        _repeater.ElementIndexChanged += (_, args) =>
        {
            var tile = (PhotoTile)args.Element;
            tile.Bind(_source[args.NewIndex]);
            _realized.Add(tile);
            TilePrepared?.Invoke(tile);
        };
        base.Children.Add(_repeater);
    }

    public void SetItems(IReadOnlyList<PhotoItem> items)
    {
        bool geometryChanged = _source.Count != items.Count || !_source.Select(i => i.AspectRatio).SequenceEqual(items.Select(i => i.AspectRatio));
        if (!_source.SequenceEqual(items)) _source.Replace(items);
        // Reset 通过稳定键保留控件时仍须刷新模型引用，不能继续读旧标签和收藏状态。
        foreach (var tile in _realized.ToArray())
        {
            int index = _source.IndexFromKey(GalleryItems.Key(tile.Item));
            if (index < 0) continue;
            tile.Bind(_source[index]);
            TilePrepared?.Invoke(tile);
        }
        if (geometryChanged) Changed();
        else if (!_building) LayoutReady?.Invoke();
    }
    internal void NotifyViewportChanged() => ViewportInvalidated?.Invoke();
    public int IndexOfTile(PhotoTile tile) => _repeater.GetElementIndex(tile);
    internal PhotoTile? TryGetTile(int index) => _repeater.TryGetElement(index) as PhotoTile;
    public int FirstVisibleIndex(double offset) => _index.FirstVisibleIndex(offset);
    public double TopOfIndex(int index) => _index.TopOfIndex(index);
    public (int First, int Last) VisibleRange(double offset, double height, double buffer) => _index.VisibleRange(offset, height, buffer);
    private void Changed() { _revision++; _layout.Refresh(); }

    private void EnsureLayout(double width)
    {
        if (!double.IsFinite(width) || width <= 0 || (_requestedRevision == _revision && Math.Abs(width - _requestedWidth) < .5)) return;
        _requestedRevision = _revision; _requestedWidth = width;
        _pending = (_source.Select(i => i.AspectRatio).ToArray(), Mode, Size, width, _revision);
        ImageLoader.SetResizing(true);
        if (!_building) _ = BuildAsync();
    }
    private async Task BuildAsync()
    {
        _building = true;
        try
        {
            while (_pending is { } request)
            {
                _pending = null;
                var watch = Stopwatch.StartNew();
                var snapshot = await Task.Run(() => GalleryLayoutIndex.Build(request.Aspects, request.Mode, request.Size, request.Width));
                LayoutBuildCount++; LayoutMilliseconds += watch.ElapsedMilliseconds;
                if (request.Revision != _revision || request.Width != _requestedWidth) continue;
                _index = snapshot;
                _layout.Refresh();
                LayoutReady?.Invoke();
            }
        }
        finally { _building = false; ImageLoader.SetResizing(false); ViewportInvalidated?.Invoke(); }
    }

    /// <summary>只实现视口与附近的控件，滚动测量不运行整表布局算法。</summary>
    private sealed class GalleryVirtualLayout(PhotoGalleryPanel owner) : VirtualizingLayout
    {
        private int _first, _last = -1;
        public void Refresh() => InvalidateMeasure();
        protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
        {
            owner.EnsureLayout(availableSize.Width);
            var viewport = context.RealizationRect;
            double height = double.IsFinite(viewport.Height) ? viewport.Height : 700;
            (_first, _last) = owner._index.VisibleRange(viewport.Y, height, height * owner.RealizationBufferRatio);
            _last = Math.Min(_last, context.ItemCount - 1);
            for (int i = _first; i <= _last; i++)
            {
                var rect = owner._index.Rects[i];
                context.GetOrCreateElementAt(i).Measure(new Size(rect.Width, rect.Height));
            }
            return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, owner._index.TotalHeight);
        }
        protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
        {
            for (int i = _first; i <= _last && i < owner._index.Rects.Count && i < context.ItemCount; i++)
                context.GetOrCreateElementAt(i).Arrange(owner._index.Rects[i]);
            return finalSize;
        }
    }

    /// <summary>稳定键让重排和数据更新保留仍有效的瓦片，避免 Reset 后整屏重新创建。</summary>
    private sealed class GalleryItems : IReadOnlyList<PhotoItem>, INotifyCollectionChanged, IKeyIndexMapping
    {
        private PhotoItem[] _items = [];
        private Dictionary<string, int> _indices = new(StringComparer.Ordinal);
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        public int Count => _items.Length;
        public PhotoItem this[int index] => _items[index];
        public static string Key(PhotoItem item) => item.ImageId > 0 ? $"id:{item.ImageId}" : ImageLoader.Identity(item);
        public string KeyFromIndex(int index) => Key(_items[index]);
        public int IndexFromKey(string key) => _indices.TryGetValue(key, out int index) ? index : -1;
        public void Replace(IReadOnlyList<PhotoItem> items)
        {
            bool sameKeys = _items.Select(Key).SequenceEqual(items.Select(Key));
            _items = items.ToArray();
            _indices = _items.Select((item, index) => (Key: Key(item), index)).ToDictionary(p => p.Key, p => p.index, StringComparer.Ordinal);
            if (!sameKeys) CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        public IEnumerator<PhotoItem> GetEnumerator() => ((IEnumerable<PhotoItem>)_items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
