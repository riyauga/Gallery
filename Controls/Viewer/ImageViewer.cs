using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Gallery.Models;
using Gallery.Services;

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
    private readonly DispatcherTimer _upgradeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private string? _path;
    private int _sourceVersion;
    private int _loadVersion;
    private int _loadedEdge;
    private int _loadingEdge;
    private CancellationTokenSource? _loadCancellation;
    private PhotoItem? _item;
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
        _upgradeTimer.Tick += (_, _) =>
        {
            _upgradeTimer.Stop();
            RequestUpgrade();
        };
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

    /// <summary>先显示已有缩略图，点开详情即按原图尺寸解码一次；几何始终使用原图尺寸。</summary>
    public void SetSource(PhotoItem item, ImageSource? thumbnail = null)
    {
        Clear();
        ImageLoader.SetPreviewOpen(true);
        _item = item;
        _path = item.FullPath;
        int pixelW = item.PixelWidth, pixelH = item.PixelHeight;
        if (pixelW > 0 && pixelH > 0)
        {
            _state.SetPhoto(pixelW, pixelH);
            _ready = true;
            UpdateViewport();
        }
        _image.Source = thumbnail ?? ImageLoader.FindThumbnail(item, _ready ? DesiredEdge() : 2048, 0);
        _canvas.Visibility = _image.Source is null ? Visibility.Collapsed : Visibility.Visible;
        _ = LoadAsync(_ready ? DesiredEdge() : 0, _sourceVersion);
    }

    /// <summary>详情浮层点开时一次解到原图尺寸；缩放只改绘制矩阵，不再触发更高分辨率的整图解码。</summary>
    private int DesiredEdge() => (int)Math.Max(_state.PixelWidth, _state.PixelHeight);

    private void RequestUpgrade()
    {
        if (_path is null || !_ready || ActualWidth <= 0 || ActualHeight <= 0) return;
        int edge = DesiredEdge();
        if (edge > Math.Max(_loadedEdge, _loadingEdge))
            _ = LoadAsync(edge, _sourceVersion);
    }

    private async Task LoadAsync(int edge, int sourceVersion)
    {
        var item = _item!;
        int loadVersion = ++_loadVersion;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _loadingEdge = edge;
        LoadingChanged?.Invoke(true);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!_ready)
            {
                var meta = await Task.Run(() => ImageInfo.ReadMeta(item.FullPath), cancellation.Token);
                if (sourceVersion != _sourceVersion || loadVersion != _loadVersion) return;
                if (meta.Width is not > 0 || meta.Height is not > 0) throw new InvalidDataException("无法读取图片尺寸");
                _state.SetPhoto(meta.Width.Value, meta.Height.Value);
                _ready = true;
                UpdateViewport();
                edge = DesiredEdge();
                _loadingEdge = edge;
            }
            var bitmap = await ImageLoader.LoadPreviewAsync(item, edge, cancellation.Token);
            if (sourceVersion != _sourceVersion || loadVersion != _loadVersion) return;
            if (!_ready)
            {
                _state.SetPhoto(bitmap.PixelWidth, bitmap.PixelHeight);
                _ready = true;
            }
            _loadedEdge = edge > 0 ? edge : Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
            _loadingEdge = 0;
            _image.Source = bitmap;
            _canvas.Visibility = Visibility.Visible;
            UpdateViewport();
            LoadingChanged?.Invoke(false);
            AppService.DebugLog($"preview loaded edge={edge} elapsedMs={elapsed.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
            // 切图、关闭或更高分辨率请求已接管当前画面。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            if (sourceVersion != _sourceVersion || loadVersion != _loadVersion) return;
            LoadingChanged?.Invoke(false);
            LoadFailed?.Invoke(ex.Message);
        }
    }

    public void Clear()
    {
        EndDrag();
        ImageLoader.SetPreviewOpen(false);
        _upgradeTimer.Stop();
        _sourceVersion++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _item = null;
        _path = null;
        _loadedEdge = _loadingEdge = 0;
        _ready = false;
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
        if (_path is not null)
        {
            _upgradeTimer.Stop();
            _upgradeTimer.Start();
        }
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
        // 详情页滚轮直接缩放，锚在指针位置；原来「普通滚轮翻页、Ctrl+滚轮缩放」的 photos 语义已弃用。
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
