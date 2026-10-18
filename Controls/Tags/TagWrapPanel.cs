using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Gallery.Controls.Tags;

/// <summary>
/// 按可用宽度换行排列标签贴纸（详情面板与标签编辑器共用）：标签多时折到下一行，
/// 不做横向滚动，浮层滚动条不会压住贴纸或挤到相邻分类标题上。
/// </summary>
internal sealed class TagWrapPanel : Panel
{
    private const double Gap = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width) { y += height + Gap; x = height = 0; }
            x += size.Width + Gap;
            height = Math.Max(height, size.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? x : availableSize.Width, y + height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, height = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width) { y += height + Gap; x = height = 0; }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
            x += size.Width + Gap;
            height = Math.Max(height, size.Height);
        }
        return finalSize;
    }
}
