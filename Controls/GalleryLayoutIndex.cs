using Windows.Foundation;

namespace Gallery.Controls;

/// <summary>后台计算的不可变布局快照。复用原方格/河流公式，并建立行索引，使滚动定位不必从头扫描。</summary>
internal sealed class GalleryLayoutIndex
{
    private readonly IReadOnlyList<double> _aspects;
    private readonly List<Rect> _rects = new();
    private readonly List<(int First,int Last,double Top,double Bottom)> _rows = new();
    private double _totalHeight;
    private int Count => _aspects.Count;
    public GalleryLayoutMode Mode { get; }
    public ThumbnailSize Size { get; }
    public IReadOnlyList<Rect> Rects => _rects;
    public double TotalHeight => _totalHeight;
    private GalleryLayoutIndex(IReadOnlyList<double> aspects, GalleryLayoutMode mode, ThumbnailSize size)
    { _aspects=aspects; Mode=mode; Size=size; }
    public static GalleryLayoutIndex Build(IReadOnlyList<double> aspects, GalleryLayoutMode mode, ThumbnailSize size, double width)
    {
        var result=new GalleryLayoutIndex(aspects,mode,size);
        if(aspects.Count==0 || width<=0) return result;
        if(mode==GalleryLayoutMode.Grid) result.LayoutGrid(width); else result.LayoutRiver(width);
        for(int first=0;first<result._rects.Count;)
        {
            int last=first;
            var rect=result._rects[first];
            while(last+1<result._rects.Count && result._rects[last+1].Top==rect.Top) last++;
            result._rows.Add((first,last,rect.Top,rect.Bottom));
            first=last+1;
        }
        return result;
    }
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

    private double GetAspect(int index) => _aspects[index];
    private int RowAt(double offset)
    {
        int lo=0,hi=_rows.Count;
        while(lo<hi) { int mid=(lo+hi)/2; if(_rows[mid].Bottom<=offset) lo=mid+1; else hi=mid; }
        return Math.Min(lo,Math.Max(0,_rows.Count-1));
    }
    public int FirstVisibleIndex(double offset) => _rows.Count==0?0:_rows[RowAt(offset)].First;
    public double TopOfIndex(int index) => index>=0 && index<_rects.Count ? _rects[index].Top : 0;
    public (int First,int Last) VisibleRange(double offset,double height,double buffer)
    {
        if(_rows.Count==0 || height<=0) return (0,-1);
        int first=RowAt(Math.Max(0,offset-buffer)),lo=first,hi=_rows.Count;
        double bottom=offset+height+buffer;
        while(lo<hi) { int mid=(lo+hi)/2; if(_rows[mid].Top<=bottom) lo=mid+1; else hi=mid; }
        return (_rows[first].First,_rows[Math.Max(first,lo-1)].Last);
    }
}
