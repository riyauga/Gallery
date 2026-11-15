namespace Gallery.Controls.Viewer;

/// <summary>
/// Gallery_Old/app/ui/preview.py:PhotoCanvas 的几何状态。
/// Scale 表示一个原图像素占用的屏幕物理像素数；Qt 绘制由 WinUI 画布适配。
/// 平移边界按图片自身尺寸算：图比视口大就拖到边缘即停（不留空白），图比视口小就居中不动。
/// </summary>
internal sealed class PhotoCanvasState
{
    public double PixelWidth { get; private set; } = 1;
    public double PixelHeight { get; private set; } = 1;
    public double Width { get; private set; }
    public double Height { get; private set; }
    public double Dpi { get; private set; } = 1;
    public double Scale { get; private set; } = 1;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public bool FitMode { get; private set; } = true;
    private int _doubleStep;

    public bool AllowFitUpscale { get; set; } = true;

    public double FitScale => Math.Clamp(Math.Min(Math.Max(1, Width) * Dpi / PixelWidth,
        Math.Max(1, Height) * Dpi / PixelHeight), .05, AllowFitUpscale ? 8 : 1);

    public CanvasRect ImageRect
    {
        get
        {
            double width = PixelWidth * Scale / Dpi, height = PixelHeight * Scale / Dpi;
            return new((Width - width) / 2 + OffsetX, (Height - height) / 2 + OffsetY, width, height);
        }
    }

    public void SetPhoto(double width, double height)
    {
        PixelWidth = Math.Max(1, width);
        PixelHeight = Math.Max(1, height);
        Fit();
    }

    public void SetViewport(double width, double height, double dpi)
    {
        Width = width;
        Height = height;
        Dpi = dpi;
        if (FitMode) Fit(); else Clamp();
    }

    public void Fit()
    {
        _doubleStep = 0;
        FitMode = true;
        Scale = FitScale;
        OffsetX = OffsetY = 0;
    }

    public void Actual()
    {
        _doubleStep = 0;
        FitMode = false;
        Scale = 1;
        OffsetX = OffsetY = 0;
        Clamp();
    }

    /// <summary>把偏移夹回「图片盖住视口」的范围：图大就按多出来的部分给拖动余量，
    /// 图小就归零（居中），避免把图片拖出画布留出空白。</summary>
    private void Clamp()
    {
        OffsetX = ClampAxis(OffsetX, PixelWidth * Scale / Dpi, Width);
        OffsetY = ClampAxis(OffsetY, PixelHeight * Scale / Dpi, Height);
    }

    private static double ClampAxis(double offset, double image, double viewport)
    {
        var slack = (image - viewport) / 2;
        return slack > 0 ? Math.Clamp(offset, -slack, slack) : 0;
    }

    public void ZoomAt(double factor, double x, double y, bool cycle = false)
    {
        if (!cycle) _doubleStep = 0;
        var before = ImageRect;
        double old = Scale;
        FitMode = false;
        Scale = Math.Clamp(old * factor, .05, Math.Max(8, FitScale * 4));
        OffsetX += (x - before.X) * (1 - Scale / old) + PixelWidth * (Scale - old) / (2 * Dpi);
        OffsetY += (y - before.Y) * (1 - Scale / old) + PixelHeight * (Scale - old) / (2 * Dpi);
        Clamp();
    }

    public void Pan(double x, double y)
    {
        FitMode = false;
        OffsetX += x;
        OffsetY += y;
        Clamp();
    }

    public void DoubleTap(double x, double y)
    {
        _doubleStep = (_doubleStep + 1) % 3;
        if (_doubleStep == 0) Fit();
        else ZoomAt(FitScale * Math.Pow(2, _doubleStep) / Scale, x, y, cycle: true);
    }
}

internal readonly record struct CanvasRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && y >= Y && x <= X + Width && y <= Y + Height;
}
