using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Gallery.Models;
using Windows.Foundation;

namespace Gallery.Controls;

public enum GalleryLayoutMode { River, Grid }
public enum ThumbnailSize { Small, Medium, Large }

/// <summary>
/// 图库网格。两套算法与 Photos 的 VirtualGridLayoutAlgorithm / VirtualRiverLayoutAlgorithm 对齐：
/// 方格：n = floor((net+g)/(minH+g))，边长 floor(clamp(...))，末行左对齐，总高含尾部 gutter。
/// 河流：按 aspect(clamp 0.56..3.0) 凑行，再整体缩放；末行不放大。
///
/// 双模式：
/// - 普通模式：Children 即全部瓦片（小数据量，行为与此前一致）。
/// - 虚拟模式（SetVirtualSource）：Rects 由数据侧宽高比驱动（万级无压力），
///   Children 只保留视口内的瓦片（PlaceTile 放入 / 回收），Arrange 按挂接索引定位。
/// </summary>
public sealed class PhotoGalleryPanel : Panel
{
    private readonly List<Rect> _rects = new();
    private double _totalHeight;
    private IReadOnlyList<double>? _virtualAspects;
    private readonly Dictionary<PhotoTile, int> _virtualIndex = new();

    public GalleryLayoutMode Mode { get; set; } = GalleryLayoutMode.River;
    public ThumbnailSize Size { get; set; } = ThumbnailSize.Medium;

    public double Gutter => Size switch
    {
        ThumbnailSize.Small => 12,
        ThumbnailSize.Medium => 14,
        _ => 16
    };

    public (double Min, double Target, double Max) SizeRange => Mode == GalleryLayoutMode.Grid
        ? Size switch
        {
            ThumbnailSize.Small => (120, 135, 150),
            ThumbnailSize.Medium => (240, 270, 300),
            _ => (330, 390, 450)
        }
        : Size switch
        {
            ThumbnailSize.Small => (100.0, 142, 240),
            ThumbnailSize.Medium => (112.0, 280, 800),
            _ => (154.0, 440, 980)
        };

    public IReadOnlyList<Rect> Rects => _rects;
    public bool IsVirtual => _virtualAspects is not null;

    /// <summary>进入虚拟模式：传入每项的宽高比（与数据列表同序）。</summary>
    public void SetVirtualSource(IReadOnlyList<double> aspects)
    {
        _virtualAspects = aspects;
        _virtualIndex.Clear();
        Children.Clear();
    }

    public void ClearVirtualSource()
    {
        _virtualAspects = null;
        _virtualIndex.Clear();
    }

    /// <summary>虚拟模式：把一张瓦片放进面板并登记其数据索引。</summary>
    public void PlaceTile(PhotoTile tile, int index)
    {
        if (_virtualAspects is null) return;
        if (_virtualIndex.ContainsKey(tile)) return;
        _virtualIndex[tile] = index;
        Children.Add(tile);
        var r = index < _rects.Count ? _rects[index] : new Rect();
        tile.Measure(new Size(r.Width, r.Height));
    }

    public void RemoveTile(PhotoTile tile)
    {
        _virtualIndex.Remove(tile);
        Children.Remove(tile);
    }

    public int IndexOfTile(PhotoTile tile)
        => _virtualIndex.TryGetValue(tile, out var i) ? i : -1;

    private int Count => _virtualAspects?.Count ?? Children.Count;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        _rects.Clear();
        _totalHeight = 0;

        if (Count == 0 || width <= 0)
            return new Size(width, 0);

        if (Mode == GalleryLayoutMode.Grid) LayoutGrid(width);
        else LayoutRiver(width);

        if (_virtualAspects is null)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                var r = i < _rects.Count ? _rects[i] : new Rect();
                Children[i].Measure(new Size(r.Width, r.Height));
            }
        }
        else
        {
            foreach (var child in Children)
            {
                var idx = child is PhotoTile t ? IndexOfTile(t) : -1;
                var r = idx >= 0 && idx < _rects.Count ? _rects[idx] : new Rect();
                child.Measure(new Size(r.Width, r.Height));
            }
        }
        return new Size(width, _totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_virtualAspects is null)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                var r = i < _rects.Count ? _rects[i] : new Rect();
                Children[i].Arrange(r);
            }
        }
        else
        {
            foreach (var child in Children)
            {
                if (child is PhotoTile t && _virtualIndex.TryGetValue(t, out var idx))
                    child.Arrange(idx < _rects.Count ? _rects[idx] : new Rect());
            }
        }
        return finalSize;
    }

    private void LayoutGrid(double net)
    {
        var (minH, _, maxH) = SizeRange;
        double g = Gutter;
        int n = (int)Math.Max(1, Math.Floor((net + g) / (Math.Max(1, minH) + g)));
        double s = Math.Floor(Math.Clamp((net - (n - 1) * g) / n, minH, maxH));
        double pitch = s + g;

        double y = 0;
        for (int i = 0; i < Count; i += n)
        {
            int k = Math.Min(n, Count - i);
            double x = 0;
            for (int j = 0; j < k; j++)
            {
                _rects.Add(new Rect(x, y, s, s));
                x += pitch;
            }
            y += pitch;
        }
        _totalHeight = Math.Ceiling((double)Count / n) * pitch;
    }

    private void LayoutRiver(double net)
    {
        var (minH, target, maxH) = SizeRange;
        double g = Gutter;
        double targetH = Math.Floor(target);
        double y = 0;
        int index = 0;

        while (index < Count)
        {
            // 1) 凑行
            var widths = new List<double>();
            double acc = 0;
            int i = index;
            while (i < Count && acc < net)
            {
                double aspect = Math.Clamp(GetAspect(i), 0.56, 3.0);
                double w = aspect * targetH;
                widths.Add(w);
                acc += w + g;
                i++;
            }

            int k = widths.Count;
            bool isLastRow = index + k >= Count;
            double gutterW = g * (k - 1);
            double rowRight = widths.Sum() + gutterW;
            double netRow = rowRight - gutterW;
            double avail = net - gutterW;
            double scale;

            if (rowRight > net)
            {
                if (k > 1)
                {
                    double down = Math.Max(avail / netRow, minH / targetH);
                    double overflow = rowRight - net;
                    double lastW = widths[^1];
                    if (overflow > (lastW - overflow) || down * netRow > avail)
                    {
                        // 丢掉最后一张，改为放大
                        widths.RemoveAt(widths.Count - 1);
                        k--;
                        isLastRow = index + k >= Count;
                        gutterW = g * (k - 1);
                        netRow = widths.Sum();
                        avail = net - gutterW;
                        scale = isLastRow ? Math.Min(avail / netRow, 1.0) : Math.Min(avail / netRow, maxH / targetH);
                    }
                    else
                    {
                        scale = down;
                    }
                }
                else
                {
                    scale = Math.Max(avail / netRow, minH / targetH);
                }
            }
            else
            {
                scale = isLastRow ? Math.Min(avail / netRow, 1.0) : Math.Min(avail / netRow, maxH / targetH);
            }

            double itemH = Math.Floor(targetH * scale);
            double x = 0;
            for (int j = 0; j < widths.Count; j++)
            {
                double itemW = widths[j] * scale;
                _rects.Add(new Rect(x, y, itemW, itemH));
                x += itemW + g;
            }
            y += itemH + g;
            index += widths.Count;
        }

        _totalHeight = y;
    }

    private double GetAspect(int index)
        => _virtualAspects is not null
            ? (_virtualAspects[index])
            : Children[index] is PhotoTile tile ? tile.Item.AspectRatio : 1.0;

    public int FirstVisibleIndex(double offset)
    {
        for (int i = 0; i < _rects.Count; i++)
            if (_rects[i].Bottom > offset) return i;
        return Math.Max(0, _rects.Count - 1);
    }

    public double TopOfIndex(int index)
        => index >= 0 && index < _rects.Count ? _rects[index].Top : 0;

    /// <summary>虚拟模式：视口 ± buffer 内的数据索引区间（按 Rects 线性扫描，原型足够）。</summary>
    public (int First, int Last) VisibleRange(double offset, double viewportHeight, double buffer)
    {
        if (_rects.Count == 0) return (0, -1);
        double top = Math.Max(0, offset - buffer);
        double bottom = offset + viewportHeight + buffer;
        int first = -1, last = -1;
        for (int i = 0; i < _rects.Count; i++)
        {
            if (_rects[i].Bottom < top) continue;
            if (_rects[i].Top > bottom) break;
            if (first < 0) first = i;
            last = i;
        }
        return first < 0 ? (0, -1) : (first, last);
    }
}
