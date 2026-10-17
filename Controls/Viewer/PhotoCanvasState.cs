namespace Gallery.Controls.Viewer;

/// <summary>
/// Gallery_Old/app/ui/preview.py:PhotoCanvas 的几何状态。
/// Scale 表示一个原图像素占用的屏幕物理像素数；Qt 绘制由 WinUI 画布适配。
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

    public double FitScale => Math.Clamp(Math.Min(Math.Max(1, Width) * Dpi / PixelWidth,
        Math.Max(1, Height) * Dpi / PixelHeight), .05, 8);

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

    private void Clamp()
    {
        OffsetX = Math.Clamp(OffsetX, -Width / 2, Width / 2);
        OffsetY = Math.Clamp(OffsetY, -Height / 2, Height / 2);
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
