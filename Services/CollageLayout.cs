namespace Gallery.Services;

/// <summary>拼图排列方向。横向＝左右连成一条链，纵向＝上下连成一条链。</summary>
public enum CollageAxis
{
    Horizontal,
    Vertical,
}

/// <summary>尺寸策略。统一＝所有图缩放到同一条边；原始＝各自维持原始像素。</summary>
public enum CollageScaleMode
{
    Uniform,
    Original,
}

/// <summary>
/// 非拼接轴的对齐方式。横向时为上/中/下，纵向时为左/中/右；
/// 自由档使用每张图自己的 <see cref="CollageEntry.Offset"/>。
/// </summary>
public enum CollageAlign
{
    Start,
    Center,
    End,
    Free,
}

/// <summary>
/// 自由档的偏移参照物。首图＝每张各算各的绝对位置；上一张＝Offset 是相对前一张的增量，
/// 拖动某一张时后面整体跟着走。
/// </summary>
public enum CollageTarget
{
    First,
    Previous,
}

/// <summary>画布上的一张图。像素尺寸来自源文件，其余字段是布局输出（输出像素坐标系）。</summary>
public sealed class CollageEntry
{
    public string Path = "";
    public int PixelWidth;
    public int PixelHeight;
    /// <summary>
    /// 自由对齐时的非拼接轴偏移。参照首图时是绝对坐标，参照上一张时是相对增量；
    /// 其它对齐方式不使用。
    /// </summary>
    public double Offset;
    public double X;
    public double Y;
    public double Width;
    public double Height;
}

public sealed class CollageLayoutResult
{
    public double Width;
    public double Height;
    /// <summary>为了不超过画布上限而做的整体缩放系数，1 表示未缩放。</summary>
    public double Scale = 1;
    public bool Clamped => Scale < 1;
}

/// <summary>
/// 拼图排版。预览与导出共用这一份计算，避免两边结果不一致。
/// 坐标单位是输出像素：预览按窗口缩放显示，导出按原尺寸绘制。
/// </summary>
public static class CollageLayout
{
    /// <summary>画布单边上限。取 Direct3D 11 纹理上限，预览与导出都受它保护。</summary>
    public const int MaxEdge = 16384;

    /// <summary>自由对齐时相邻两张在接缝处必须保留的最小重叠（占较小图的比例）。</summary>
    public const double MinOverlapRatio = 0.1;

    public static CollageLayoutResult Compute(
        IReadOnlyList<CollageEntry> entries,
        CollageAxis axis,
        CollageScaleMode mode,
        CollageAlign align,
        CollageTarget target = CollageTarget.First)
    {
        var result = new CollageLayoutResult();
        if (entries.Count == 0) return result;

        bool horizontal = axis == CollageAxis.Horizontal;

        // 统一尺寸：以首图的对应边为准（固定规则，不设选项），其余等比缩放。
        double edge = 0;
        if (mode == CollageScaleMode.Uniform)
        {
            var anchor = entries[0];
            edge = horizontal ? anchor.PixelHeight : anchor.PixelWidth;
            if (edge <= 0)
                edge = entries.Max(entry => horizontal ? entry.PixelHeight : entry.PixelWidth);
            if (edge <= 0) edge = 1;
        }

        foreach (var entry in entries)
        {
            if (mode == CollageScaleMode.Uniform)
            {
                if (horizontal)
                {
                    entry.Height = edge;
                    entry.Width = Math.Max(1, Math.Round(entry.PixelWidth * edge / Math.Max(1, entry.PixelHeight)));
                }
                else
                {
                    entry.Width = edge;
                    entry.Height = Math.Max(1, Math.Round(entry.PixelHeight * edge / Math.Max(1, entry.PixelWidth)));
                }
            }
            else
            {
                entry.Width = Math.Max(1, entry.PixelWidth);
                entry.Height = Math.Max(1, entry.PixelHeight);
            }
        }

        // 拼接轴依次累加，同时求出非拼接轴的最大边长。
        double cursor = 0, crossMax = 0;
        foreach (var entry in entries)
        {
            if (horizontal)
            {
                entry.X = cursor;
                cursor += entry.Width;
                crossMax = Math.Max(crossMax, entry.Height);
            }
            else
            {
                entry.Y = cursor;
                cursor += entry.Height;
                crossMax = Math.Max(crossMax, entry.Width);
            }
        }

        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            double size = horizontal ? entry.Height : entry.Width;
            double cross = align switch
            {
                CollageAlign.Start => 0,
                CollageAlign.Center => (crossMax - size) / 2,
                CollageAlign.End => crossMax - size,
                _ => entry.Offset,
            };

            if (align == CollageAlign.Free && index > 0)
            {
                var previous = entries[index - 1];
                double previousSize = horizontal ? previous.Height : previous.Width;
                double previousCross = horizontal ? previous.Y : previous.X;
                double overlap = Math.Max(1, MinOverlapRatio * Math.Min(size, previousSize));
                // 参照上一张时 Offset 是增量，叠加到前一张的实际位置上再钳制。
                if (target == CollageTarget.Previous) cross = previousCross + entry.Offset;
                cross = Math.Clamp(cross, previousCross - size + overlap, previousCross + previousSize - overlap);
                // 绝对参照把钳制结果写回，用户下次拖动从这里继续；增量参照不回写，否则会重复累加。
                if (target == CollageTarget.First) entry.Offset = cross;
            }

            if (align == CollageAlign.Free && index == 0) entry.Offset = cross;

            if (horizontal) entry.Y = cross;
            else entry.X = cross;
            crossMax = Math.Max(crossMax, cross + size);
        }

        result.Width = horizontal ? cursor : crossMax;
        result.Height = horizontal ? crossMax : cursor;

        // 超出单边上限时整体等比缩小，避免预览纹理上传失败与导出内存爆掉。
        double fit = 1;
        if (result.Width > MaxEdge) fit = Math.Min(fit, MaxEdge / result.Width);
        if (result.Height > MaxEdge) fit = Math.Min(fit, MaxEdge / result.Height);
        if (fit < 1)
        {
            result.Scale = fit;
            foreach (var entry in entries)
            {
                entry.X *= fit;
                entry.Y *= fit;
                entry.Width *= fit;
                entry.Height *= fit;
                entry.Offset *= fit;
            }
            result.Width *= fit;
            result.Height *= fit;
        }

        return result;
    }
}
