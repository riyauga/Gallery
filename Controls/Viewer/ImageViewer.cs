using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Gallery.Controls.Viewer;

/// <summary>
/// Gallery_Old PhotoCanvas 的 WinUI 适配：原图按物理像素倍率进行矩阵绘制，
/// 保留旧版适应、100%、锚点缩放、双击三档和平移逻辑。
/// </summary>
public sealed class ImageViewer : Grid
{
    private readonly Canvas _canvas = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly MatrixTransform _transform = new();
    private readonly RectangleGeometry _clip = new();
    private readonly PhotoCanvasState _state = new();
    private BitmapImage? _bitmap;
    private XamlRoot? _observedRoot;
    private bool _ready;
    private bool _panning;
    private Point _lastPointer;

    public event Action<double>? ZoomChanged;
    public event Action<bool>? LoadingChanged;
    public event Action<string>? LoadFailed;
    public bool IsAnimating { get; set; }

    public ImageViewer()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Clip = _clip;
        _image.RenderTransform = _transform;
        _canvas.Children.Add(_image);
        Children.Add(_canvas);
        PointerWheelChanged += OnWheel;
        DoubleTapped += OnDoubleTapped;
        PointerPressed += OnDown;
        PointerMoved += OnMove;
        PointerReleased += (_, _) => EndDrag();
        PointerCanceled += (_, _) => EndDrag();
        PointerCaptureLost += (_, _) => _panning = false;
        SizeChanged += (_, _) => UpdateViewport();
        Loaded += (_, _) =>
        {
            _observedRoot = XamlRoot;
            if (_observedRoot is not null) _observedRoot.Changed += OnXamlRootChanged;
            UpdateViewport();
        };
        Unloaded += (_, _) =>
        {
            EndDrag();
            if (_observedRoot is not null) _observedRoot.Changed -= OnXamlRootChanged;
            _observedRoot = null;
        };
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateViewport();

    private void UpdateViewport()
    {
        _clip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        _state.SetViewport(ActualWidth, ActualHeight, XamlRoot?.RasterizationScale ?? 1);
        Render();
    }

    /// <summary>直接载入原文件；只允许当前请求的加载回调更新画布。</summary>
    public void SetSource(string path, int pixelW, int pixelH)
    {
        Clear();
        var bitmap = new BitmapImage();
        _bitmap = bitmap;
        LoadingChanged?.Invoke(true);
        bitmap.ImageOpened += (_, _) =>
        {
            if (!ReferenceEquals(_bitmap, bitmap)) return;
            _state.SetPhoto(pixelW > 0 ? pixelW : bitmap.PixelWidth, pixelH > 0 ? pixelH : bitmap.PixelHeight);
            _ready = true;
            _canvas.Visibility = Visibility.Visible;
            UpdateViewport();
            LoadingChanged?.Invoke(false);
        };
        bitmap.ImageFailed += (_, args) =>
        {
            if (!ReferenceEquals(_bitmap, bitmap)) return;
            _ready = false;
            _canvas.Visibility = Visibility.Collapsed;
            LoadingChanged?.Invoke(false);
            LoadFailed?.Invoke(args.ErrorMessage);
        };
        _canvas.Visibility = Visibility.Collapsed;
        _image.Source = bitmap;
        bitmap.UriSource = new Uri(path);
    }

    public void Clear()
    {
        EndDrag();
        _ready = false;
        _bitmap = null;
        _image.Source = null;
        LoadingChanged?.Invoke(false);
    }

    private void Render()
    {
        if (!_ready) return;
        var rect = _state.ImageRect;
        // Canvas 不把原图的固定宽高反向传播为父容器的最小尺寸。
        // 显式填充整张位图，再用矩阵变换缩放；避免 Stretch=None 只缩小裁切框。
        _image.Width = _state.PixelWidth / _state.Dpi;
        _image.Height = _state.PixelHeight / _state.Dpi;
        _transform.Matrix = new Matrix(_state.Scale, 0, 0, _state.Scale, rect.X, rect.Y);
        ZoomChanged?.Invoke(Percent);
    }

    public double Percent => Math.Round(_state.Scale * 100);
    public void ZoomFit() { if (!_ready) return; EndDrag(); _state.Fit(); Render(); }
    public void Zoom100() { if (!_ready) return; EndDrag(); _state.Actual(); Render(); }

    /// <summary>缩放到指定百分比（100% = 一个原图像素对一个屏幕物理像素），以视口中心为锚点。</summary>
    public void ZoomTo(double percent)
    {
        if (!_ready || IsAnimating) return;
        double target = Math.Clamp(percent, 5, 800) / 100.0;
        if (Math.Abs(target - _state.Scale) < 0.0001) return;
        EndDrag();
        _state.ZoomAt(target / _state.Scale, ActualWidth / 2, ActualHeight / 2);
        Render();
    }

    /// <summary>按倍率缩放（滚轮/按钮），以视口中心为锚点。</summary>
    public void ZoomBy(double factor)
    {
        if (!_ready || IsAnimating) return;
        EndDrag();
        _state.ZoomAt(factor, ActualWidth / 2, ActualHeight / 2);
        Render();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready || IsAnimating) return;
        var point = e.GetCurrentPoint(this);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        _state.ZoomAt(Math.Pow(1.2, delta / 120.0), point.Position.X, point.Position.Y);
        Render();
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(this);
        if (!_ready || IsAnimating || !_state.ImageRect.Contains(point.X, point.Y)) return;
        EndDrag();
        _state.DoubleTap(point.X, point.Y);
        Render();
        e.Handled = true;
    }

    private void OnDown(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!_ready || IsAnimating || !point.Properties.IsLeftButtonPressed ||
            !_state.ImageRect.Contains(point.Position.X, point.Position.Y)) return;
        _panning = CapturePointer(e.Pointer);
        _lastPointer = point.Position;
        _state.Pan(0, 0);
        e.Handled = true;
    }

    private void OnMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var point = e.GetCurrentPoint(this).Position;
        _state.Pan(point.X - _lastPointer.X, point.Y - _lastPointer.Y);
        _lastPointer = point;
        Render();
        e.Handled = true;
    }

    private void EndDrag()
    {
        _panning = false;
        ReleasePointerCaptures();
    }
}
