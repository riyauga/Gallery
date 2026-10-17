using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 单图缩放/平移画布，行为照搬 galleryold preview.py 的 PhotoCanvas：
/// 滚轮 1.2^(delta/120) 鼠标锚点缩放、范围 [0.05, max(8, fit*4)]；
/// 双击三档循环 适应 → 2×fit → 4×fit；100% = 1 图像像素对 1 物理像素；
/// 左键拖动平移（ScrollViewer 承载，边界自然受限）。
/// </summary>
public sealed class ImageViewer : Grid
{
    private readonly ScrollViewer _sv;
    private readonly Image _image;
    private readonly ContentPresenter _presenter;
    private BitmapImage? _bitmap;
    private double _pixelW, _pixelH;
    private double _scale = 1;      // 显示 DIP / (像素/RasterizationScale)
    private double _fit = 1;
    private bool _panning;
    private double _panStartX, _panStartY, _svStartX, _svStartY;

    public event Action<double>? ZoomChanged; // 百分比（100 = 100%）

    public ImageViewer()
    {
        _sv = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            ZoomMode = ZoomMode.Disabled,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _image = new Image { Stretch = Stretch.None };
        _presenter = new ContentPresenter { Content = _image };
        _sv.Content = _presenter;
        Children.Add(_sv);

        PointerWheelChanged += OnWheel;
        DoubleTapped += OnDoubleTapped;
        PointerPressed += OnDown;
        PointerMoved += OnMove;
        PointerReleased += (_, e) => { _panning = false; ReleasePointerCaptures(); };
        SizeChanged += (_, _) => ComputeFit();
    }

    /// <summary>载入图像（路径或缓存 webp）。</summary>
    public void SetSource(string path, int pixelW, int pixelH)
    {
        _bitmap = new BitmapImage(new Uri(path));
        _image.Source = _bitmap;
        _image.SizeChanged += (_, _) => ComputeFit();
        // 读取位图尺寸（BitmapImage 解码后）
        _bitmap.ImageOpened += (_, _) =>
        {
            _pixelW = _bitmap.PixelWidth;
            _pixelH = _bitmap.PixelHeight;
            ComputeFit();
            ApplyFit();
        };
    }

    public void Clear()
    {
        _image.Source = null;
        _bitmap = null;
        _scale = 1;
    }

    private double RasterScale => XamlRoot?.RasterizationScale is { } s && s > 0 ? s : 1.0;

    /// <summary>100% 时图像的 DIP 尺寸（1 图像像素 = 1 物理像素）。</summary>
    private double BaseW => _pixelW / RasterScale;
    private double BaseH => _pixelH / RasterScale;

    private void ComputeFit()
    {
        if (_pixelW <= 0 || ActualWidth <= 0) return;
        _fit = Math.Min(ActualWidth / BaseW, ActualHeight / BaseH);
        if (_fit <= 0) _fit = 1;
    }

    private void ApplyFit()
    {
        _scale = _fit;
        ApplyScale();
        CenterContent();
        ZoomChanged?.Invoke(Percent);
    }

    private void ApplyScale()
    {
        _image.Width = BaseW * _scale;
        _image.Height = BaseH * _scale;
    }

    private void CenterContent()
    {
        _sv.UpdateLayout();
        _sv.ChangeView(
            (_image.ActualWidth - _sv.ViewportWidth) / 2,
            (_image.ActualHeight - _sv.ViewportHeight) / 2, null, true);
    }

    public double Percent => _scale <= 0 ? 100 : Math.Round(_scale / _fit * 100);

    public void ZoomFit() { _scale = _fit; ApplyScale(); CenterContent(); ZoomChanged?.Invoke(Percent); }
    public void Zoom100() { _scale = 1; ApplyScale(); ZoomChanged?.Invoke(Percent); }

    private void ZoomAt(double factor, Windows.Foundation.Point anchorInSv)
    {
        var max = Math.Max(8, _fit * 4);
        var next = Math.Clamp(_scale * factor, 0.05, max);
        if (Math.Abs(next - _scale) < 1e-9) return;

        // 锚点不动：锚点处的内容比例保持
        double oldW = BaseW * _scale, newW = BaseW * next;
        double anchorRatioX = _sv.HorizontalOffset + anchorInSv.X;
        double anchorRatioY = _sv.VerticalOffset + anchorInSv.Y;
        double cx = oldW > 0 ? anchorRatioX / oldW : 0.5;
        double cy = (BaseH * _scale) > 0
            ? (_sv.VerticalOffset + anchorInSv.Y) / (BaseH * _scale) : 0.5;

        _scale = next;
        ApplyScale();
        _sv.UpdateLayout();
        _sv.ChangeView(cx * newW - anchorInSv.X, cy * (BaseH * _scale) - anchorInSv.Y, null, true);
        ZoomChanged?.Invoke(Percent);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_sv);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        ZoomAt(Math.Pow(1.2, delta / 120.0), point.Position);
        e.Handled = true;
    }

    private int _tapCount; // 双击三档：fit → 2×fit → 4×fit → fit
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _tapCount = (_tapCount + 1) % 3;
        _scale = _tapCount switch { 1 => _fit * 2, 2 => _fit * 4, _ => _fit };
        ApplyScale();
        if (_tapCount == 0) CenterContent();
        ZoomChanged?.Invoke(Percent);
        e.Handled = true;
    }

    private void OnDown(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _panning = true;
        _panStartX = e.GetCurrentPoint(this).Position.X;
        _panStartY = e.GetCurrentPoint(this).Position.Y;
        _svStartX = _sv.HorizontalOffset;
        _svStartY = _sv.VerticalOffset;
        CapturePointer(e.Pointer);
    }

    private void OnMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetCurrentPoint(this).Position;
        _sv.ChangeView(_svStartX - (p.X - _panStartX), _svStartY - (p.Y - _panStartY), null, true);
    }
}
