using System.IO;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Gallery.Controls.Viewer;
using Windows.Foundation;

namespace Gallery.Controls.Collage;

/// <summary>
/// 拼图画布。画布逻辑尺寸等于导出尺寸（输出像素），显示时按缩放系数换算每个单元的尺寸与位置，
/// 不使用 RenderTransform，避免变换中心与布局居中互相叠加。
/// </summary>
public sealed class CollageCanvas : Grid
{
    private const double MinScale = 0.02;
    private const double MaxScale = 4;
    private const double RemoveDistance = 60;

    /// <summary>画布四周留出的呼吸空间，避免画布边界贴着工具条和窗口边缘。</summary>
    private const double StagePadding = 28;

    private readonly Grid _viewport = new();
    private readonly Canvas _stage = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _board = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Padding = new Thickness(0),
        RenderTransform = new TranslateTransform(),
    };
    private readonly List<CollageEntry> _entries = [];
    private readonly Dictionary<CollageEntry, ItemHost> _hosts = [];

    private double _scale = 1;
    private double _panX, _panY;
    private bool _autoFit = true;

    private ItemHost? _pressed;
    private Point _pressPoint;
    private Point _pressOrigin;
    private bool _dragging;
    private bool _panning;
    private Point _panStart;
    private double _panOriginX, _panOriginY;

    public CollageCanvas()
    {
        ApplyWorkspace(Windows.UI.Color.FromArgb(255, 22, 22, 22), Windows.UI.Color.FromArgb(255, 58, 58, 60));
        _stage.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        _board.Child = _stage;
        _viewport.Children.Add(_board);
        Children.Add(_viewport);

        _viewport.SizeChanged += (_, _) =>
        {
            _viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _viewport.ActualWidth, _viewport.ActualHeight) };
            if (_autoFit) ZoomFit();
        };
        SizeChanged += (_, _) => { if (_autoFit) ZoomFit(); };
        _viewport.PointerWheelChanged += OnWheel;
        _viewport.PointerPressed += OnViewportPressed;
        _viewport.PointerMoved += OnViewportMoved;
        _viewport.PointerReleased += OnViewportReleased;
        _viewport.PointerCaptureLost += (_, _) => _panning = false;
        _viewport.AllowDrop = true;
        _viewport.DoubleTapped += (_, _) => ZoomFit();
        _viewport.DragOver += OnDragOver;
        _viewport.Drop += OnDrop;
    }

    public CollageAxis Axis { get; private set; } = CollageAxis.Horizontal;
    public CollageScaleMode ScaleMode { get; private set; } = CollageScaleMode.Uniform;
    public CollageAlign Align { get; private set; } = CollageAlign.Center;
    public CollageTarget Target { get; private set; } = CollageTarget.First;

    public CollageLayoutResult Layout { get; private set; } = new();
    public IReadOnlyList<CollageEntry> Entries => _entries;
    public int Count => _entries.Count;
    public string RootFolder { get; set; } = "";

    public event Action? Changed;
    public event Action<string>? Status;
    public event Action<string>? FileDropped;

    // ---------- 参数 ----------

    public void SetAxis(CollageAxis axis) { Axis = axis; Relayout(false); }
    public void SetScaleMode(CollageScaleMode mode) { ScaleMode = mode; Relayout(false); }
    public void SetAlign(CollageAlign align) { Align = align; Relayout(false); }
    public void SetTarget(CollageTarget target) { Target = target; Relayout(false); }

    public void SetTheme(ElementTheme theme)
    {
        var palette = ViewerChrome.Palette.For(theme);
        if (palette.Background.R > 128)
            ApplyWorkspace(Windows.UI.Color.FromArgb(255, 232, 232, 232), Windows.UI.Color.FromArgb(255, 200, 200, 200));
        else
            ApplyWorkspace(Windows.UI.Color.FromArgb(255, 22, 22, 22), Windows.UI.Color.FromArgb(255, 58, 58, 60));
    }

    /// <summary>工作区与画布必须是两种颜色，否则「留黑」的黑边会和工作区糊在一起，看不出画布在哪。</summary>
    private void ApplyWorkspace(Windows.UI.Color background, Windows.UI.Color border)
    {
        Background = ViewerChrome.Brush(background);
        _viewport.Background = ViewerChrome.Brush(background);
        _board.BorderBrush = ViewerChrome.Brush(border);
        _board.BorderThickness = new Thickness(1);
    }

    // ---------- 内容 ----------

    public bool Add(string path, int? index = null)
    {
        var size = CollageService.ReadSize(path);
        if (size.Width <= 0 || size.Height <= 0)
        {
            Status?.Invoke("无法读取这张图片的尺寸。");
            return false;
        }
        if (_entries.Any(entry => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Status?.Invoke("这张图片已经在拼图里了。");
            return false;
        }
        var item = new CollageEntry { Path = path, PixelWidth = size.Width, PixelHeight = size.Height };
        _entries.Insert(index is int value ? Math.Clamp(value, 0, _entries.Count) : _entries.Count, item);
        Mount(item);
        Relayout(true);
        Changed?.Invoke();
        return true;
    }

    public void Remove(CollageEntry entry)
    {
        if (!_hosts.Remove(entry, out var host)) return;
        _stage.Children.Remove(host);
        _entries.Remove(entry);
        Relayout(true);
        Changed?.Invoke();
    }

    public void Restore(IReadOnlyList<(string Path, int Width, int Height, double Offset)> snapshot)
    {
        foreach (var host in _hosts.Values) _stage.Children.Remove(host);
        _hosts.Clear();
        _entries.Clear();
        foreach (var item in snapshot)
            _entries.Add(new CollageEntry { Path = item.Path, PixelWidth = item.Width, PixelHeight = item.Height, Offset = item.Offset });
        foreach (var entry in _entries) Mount(entry);
        Relayout(false);
        Changed?.Invoke();
    }

    public List<(string Path, int Width, int Height, double Offset)> Snapshot() =>
        _entries.Select(entry => (entry.Path, entry.PixelWidth, entry.PixelHeight, entry.Offset)).ToList();

    // ---------- 布局 ----------

    public void Relayout(bool animate)
    {
        Layout = CollageLayout.Compute(_entries, Axis, ScaleMode, Align, Target);
        if (_autoFit) FitScale();
        ApplyCamera();
        foreach (var entry in _entries)
        {
            if (!_hosts.TryGetValue(entry, out var host)) continue;
            if (animate) AnimateTo(host, entry.X * _scale, entry.Y * _scale);
            else host.MoveTo(entry.X * _scale, entry.Y * _scale);
        }
    }

    private void Mount(CollageEntry entry)
    {
        var host = new ItemHost(entry);
        host.RemoveRequested += () =>
        {
            if (_entries.Count > 1) Remove(entry);
            else Status?.Invoke("拼图至少保留一张图片。");
        };
        host.Pressed += OnItemPressed;
        host.Moved += OnItemMoved;
        host.Released += OnItemReleased;
        host.Entered += () => { if (!_dragging) host.RemoveButtonVisible = true; };
        host.Exited += () => { if (!_dragging) host.RemoveButtonVisible = false; };
        _hosts[entry] = host;
        _stage.Children.Add(host);
    }

    // ---------- 拖拽 ----------

    private void OnItemPressed(ItemHost host, PointerRoutedEventArgs e)
    {
        if (_dragging) return;
        _pressed = host;
        _pressPoint = e.GetCurrentPoint(_stage).Position;
        _pressOrigin = host.Offset;
        _dragging = false;
        host.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnItemMoved(ItemHost host, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(_pressed, host)) return;
        var point = e.GetCurrentPoint(_stage).Position;
        double dx = point.X - _pressPoint.X, dy = point.Y - _pressPoint.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
            _dragging = true;
            host.BeginDrag();
        }
        host.DragTo(_pressOrigin.X + dx, _pressOrigin.Y + dy);
        e.Handled = true;
    }

    private void OnItemReleased(ItemHost host, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(_pressed, host)) return;
        _pressed = null;
        host.ReleasePointerCapture(e.Pointer);
        Canvas.SetZIndex(host, 0);
        if (!_dragging) { host.EndDrag(); e.Handled = true; return; }
        _dragging = false;
        host.EndDrag();

        // 拖动期间用的是屏幕坐标，回到画布坐标要先除以缩放。
        double droppedX = host.Offset.X / Math.Max(_scale, MinScale);
        double droppedY = host.Offset.Y / Math.Max(_scale, MinScale);
        bool horizontal = Axis == CollageAxis.Horizontal;

        bool outside = droppedX + host.Entry.Width < RemoveDistance
            || droppedY + host.Entry.Height < RemoveDistance
            || droppedX > Layout.Width - RemoveDistance
            || droppedY > Layout.Height - RemoveDistance;
        if (outside)
        {
            if (_entries.Count > 1) Remove(host.Entry);
            else { Status?.Invoke("拼图至少保留一张图片。"); Relayout(true); }
            e.Handled = true;
            return;
        }

        double center = horizontal ? droppedX + host.Entry.Width / 2 : droppedY + host.Entry.Height / 2;
        int from = _entries.IndexOf(host.Entry);
        _entries.RemoveAt(from);
        int insert = _entries.Count;
        for (int index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            double other = horizontal ? entry.X + entry.Width / 2 : entry.Y + entry.Height / 2;
            if (center < other) { insert = index; break; }
        }
        _entries.Insert(insert, host.Entry);
        if (Align == CollageAlign.Free)
        {
            double dropped = horizontal ? droppedY : droppedX;
            if (Target == CollageTarget.Previous && insert > 0)
            {
                // 参照上一张时存的是增量，换算成相对前一张的位置差。
                var previous = _entries[insert - 1];
                double previousCross = horizontal ? previous.Y : previous.X;
                host.Entry.Offset = dropped - previousCross;
            }
            else host.Entry.Offset = dropped;
        }

        Relayout(true);
        if (insert != from) Changed?.Invoke();
        e.Handled = true;
    }

    // ---------- 拖入 ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count == 0) return;
            var point = e.GetPosition(_stage);
            bool horizontal = Axis == CollageAxis.Horizontal;
            double position = (horizontal ? point.X : point.Y) / Math.Max(_scale, MinScale);
            int index = _entries.Count;
            for (int slot = 0; slot < _entries.Count; slot++)
            {
                var entry = _entries[slot];
                double center = horizontal ? entry.X + entry.Width / 2 : entry.Y + entry.Height / 2;
                if (position < center) { index = slot; break; }
            }
            foreach (var storage in items)
            {
                if (storage.Path.Length == 0) continue;
                if (!ScannerService.ImageExts.Contains(Path.GetExtension(storage.Path))) continue;
                FileDropped?.Invoke(storage.Path);
                Add(storage.Path, index);
                index++;
            }
        }
        catch (Exception error)
        {
            FaultLog.Write("collage-drop", error);
            Status?.Invoke("拖入的图片无法加入拼图。");
        }
    }

    // ---------- 缩放与平移 ----------

    public double Percent => _scale * 100;

    private void FitScale()
    {
        if (Layout.Width <= 0 || Layout.Height <= 0) return;
        double availableWidth = Math.Max(1, _viewport.ActualWidth - StagePadding * 2);
        double availableHeight = Math.Max(1, _viewport.ActualHeight - StagePadding * 2);
        _scale = Math.Clamp(Math.Min(availableWidth / Layout.Width, availableHeight / Layout.Height), MinScale, 1);
        CenterCanvas();
    }

    private void CenterCanvas()
    {
        _panX = 0;
        _panY = 0;
    }

    public void ZoomFit()
    {
        _autoFit = true;
        FitScale();
        ApplyCamera();
    }

    public void ZoomTo(double percent)
    {
        if (Layout.Width <= 0) return;
        _autoFit = false;
        _scale = Math.Clamp(percent / 100, MinScale, MaxScale);
        CenterCanvas();
        ApplyCamera();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (Layout.Width <= 0) return;
        double factor = e.GetCurrentPoint(_viewport).Properties.MouseWheelDelta > 0 ? 1.15 : 1 / 1.15;
        double next = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(next - _scale) < 1e-6) return;
        // 以视口中心为锚点缩放，画布不会跳走。
        double anchorX = _viewport.ActualWidth / 2, anchorY = _viewport.ActualHeight / 2;
        _panX = anchorX - (anchorX - _panX) * next / _scale;
        _panY = anchorY - (anchorY - _panY) * next / _scale;
        _scale = next;
        _autoFit = false;
        ApplyCamera();
        e.Handled = true;
    }

    private void OnViewportPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_viewport);
        if (!point.Properties.IsMiddleButtonPressed && !point.Properties.IsLeftButtonPressed) return;
        _panning = true;
        _panStart = point.Position;
        _panOriginX = _panX;
        _panOriginY = _panY;
        _viewport.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnViewportMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var point = e.GetCurrentPoint(_viewport).Position;
        _panX = _panOriginX + (point.X - _panStart.X);
        _panY = _panOriginY + (point.Y - _panStart.Y);
        _autoFit = false;
        ApplyCamera();
        e.Handled = true;
    }

    private void OnViewportReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        _viewport.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void ApplyCamera()
    {
        double width = Math.Max(1, Layout.Width * _scale);
        double height = Math.Max(1, Layout.Height * _scale);
        _stage.Width = width;
        _stage.Height = height;
        _board.Width = width + 2;
        _board.Height = height + 2;
        if (_board.RenderTransform is TranslateTransform shift)
        {
            shift.X = _panX;
            shift.Y = _panY;
        }
        _viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _viewport.ActualWidth, _viewport.ActualHeight) };
        foreach (var entry in _entries)
        {
            if (!_hosts.TryGetValue(entry, out var host)) continue;
            host.StopMotion();
            host.Width = Math.Max(1, entry.Width * _scale);
            host.Height = Math.Max(1, entry.Height * _scale);
            host.MoveTo(entry.X * _scale, entry.Y * _scale);
        }
    }

    private void AnimateTo(ItemHost host, double x, double y)
    {
        host.StopMotion();
        var storyboard = new Storyboard();
        void Step(string property, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(280),
                EnableDependentAnimation = true,
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
            };
            Storyboard.SetTarget(animation, host.Transform);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
        Step("X", host.Transform.X, x);
        Step("Y", host.Transform.Y, y);
        host.AttachMotion(storyboard);
        storyboard.Begin();
    }

    // ---------- 单元 ----------

    private sealed class ItemHost : Grid
    {
        private readonly Border _frame;
        private readonly Button _remove;

        public CollageEntry Entry { get; }
        public TranslateTransform Transform { get; } = new();
        public Point Offset => new(Transform.X, Transform.Y);

        public event Action<ItemHost, PointerRoutedEventArgs>? Pressed;
        public event Action<ItemHost, PointerRoutedEventArgs>? Moved;
        public event Action<ItemHost, PointerRoutedEventArgs>? Released;
        public event Action? Entered;
        public event Action? Exited;
        public event Action? RemoveRequested;

        public ItemHost(CollageEntry entry)
        {
            Entry = entry;
            Background = ViewerChrome.Transparent();
            RenderTransformOrigin = new Point(0, 0);
            RenderTransform = Transform;

            _frame = new Border
            {
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            };
            var image = new Image { Stretch = Stretch.Fill };
            try
            {
                var bitmap = new BitmapImage();
                bitmap.UriSource = new Uri(entry.Path);
                image.Source = bitmap;
            }
            catch (Exception) { /* 解不了的条目留黑底，不影响其它图 */ }
            _frame.Child = image;
            Children.Add(_frame);

            _remove = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 44, 44, 44)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 90, 90)),
                Content = new FontIcon
                {
                    Glyph = "\uE711",
                    FontSize = 10,
                    FontFamily = ViewerChrome.IconFamily,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                },
                Visibility = Visibility.Collapsed,
            };
            AutomationProperties.SetName(_remove, "移除");
            ToolTipService.SetToolTip(_remove, "移除");
            _remove.Click += (_, _) => RemoveRequested?.Invoke();
            Children.Add(_remove);

            PointerPressed += (_, e) => Pressed?.Invoke(this, e);
            PointerMoved += (_, e) => Moved?.Invoke(this, e);
            PointerReleased += (_, e) => Released?.Invoke(this, e);
            PointerEntered += (_, _) => Entered?.Invoke();
            PointerExited += (_, _) => Exited?.Invoke();
        }

        public bool RemoveButtonVisible
        {
            set => _remove.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public void MoveTo(double x, double y)
        {
            Transform.X = x;
            Transform.Y = y;
        }

        public void DragTo(double x, double y) => MoveTo(x, y);

        private Storyboard? _motion;
        public void AttachMotion(Storyboard storyboard) => _motion = storyboard;
        public void StopMotion()
        {
            if (_motion is null) return;
            _motion.Stop();
            _motion = null;
        }

        public void BeginDrag()
        {
            Opacity = .85;
            _frame.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            _frame.BorderThickness = new Thickness(2);
            Canvas.SetZIndex(this, 100);
        }

        public void EndDrag()
        {
            Opacity = 1;
            _frame.BorderThickness = new Thickness(0);
            _frame.BorderBrush = null;
        }
    }
}
