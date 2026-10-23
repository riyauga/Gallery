using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Controls;

/// <summary>
/// 图库格子。视觉结构与状态机对齐 Photos 的 GalleryTileContainer：
/// 圆角 4、悬停零时长硬切、选中 167ms cubic-bezier(0,0,0,1)、按压 0.98 / 100ms 线性。
/// </summary>
public sealed class PhotoTile : Grid
{
    private const double CornerRadiusValue = 4;
    internal const double KeyboardFocusOutset = 6;
    private const double PressScale = 0.98;
    private const int PressDurationMs = 100;

    private readonly Border _frame;
    private readonly Image _image;
    private readonly Rectangle _hover;
    private readonly Rectangle _selectionBorder;
    private readonly Border _keyboardFocusBorder;
    private readonly CheckBox _check;
    private readonly FavoriteButton _favorite;
    private readonly FontIcon _videoPlaceholder;
    private readonly Border _durationBadge;
    private readonly TextBlock _durationText;
    private readonly Storyboard _selectIn;
    private readonly Storyboard _selectOut;

    private SolidColorBrush _hoverBrush = new(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
    private SolidColorBrush _pressedBrush = new(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    internal PhotoItem Item { get; private set; } = null!;
    internal bool EventsWired { get; set; }
    public event Action? ThumbnailInvalidated;
    public Func<PhotoItem, bool, Task<bool>>? FavoriteRequested { get; set; }
    private (string Path, double? Mtime, long Size, int Blur, bool Hidden)? _binding;
    public ImageSource? PreviewThumbnail => _image.Source;

    /// <summary>瓦片被虚拟化回收后置位；异步加载完成时不再回贴 UI。</summary>
    public volatile bool Detached;
    private int _requestedEdge;
    private int _loadedEdge;
    private int _thumbnailToken;
    private CancellationTokenSource? _thumbnailCancellation;
    private XamlRoot? _thumbnailRoot;
    private bool _pointerOver;

    private async Task LoadThumbnailAsync(int edge, int token, ImagePriority priority)
    {
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _thumbnailCancellation = cancellation;
        try
        {
            var item = Item;
            var bitmap = await ImageLoader.LoadThumbnailAsync(item, edge, ImageLoader.BlurStrength(item), cancellation.Token, priority);
            if (Detached || token != _thumbnailToken) return;
            _image.Source = bitmap;
            _loadedEdge = edge;
            UpdateVideoOverlay();
        }
        catch (OperationCanceledException) { if (token == _thumbnailToken) _requestedEdge = _loadedEdge; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException
            or SixLabors.ImageSharp.UnknownImageFormatException
            or SixLabors.ImageSharp.InvalidImageContentException or System.Runtime.InteropServices.COMException)
        {
            // 保护处理失败保持空白，绝不回退到未模糊原图。
            AppService.DebugLog("thumbnail failed: " + ex.Message);
        }
    }

    private void RefreshThumbnail()
    {
        if (IsLoaded && !Detached && Item is not null) ThumbnailInvalidated?.Invoke();
    }

    /// <summary>由图库统一按可见性提交，瓦片自身不启动后台生成任务。</summary>
    public void RequestThumbnail(double pixels, ImagePriority priority)
    {
        if (Detached || Item is null) return;
        int edge = ImageLoader.ThumbnailEdge(Item, pixels);
        if (edge == 0) { _image.Source = null; _videoPlaceholder.Visibility = Visibility.Collapsed; return; }
        if (edge <= _requestedEdge) return;
        _image.Source ??= ImageLoader.FindThumbnail(Item, edge, ImageLoader.BlurStrength(Item));
        UpdateVideoOverlay();
        _requestedEdge = edge;
        _ = LoadThumbnailAsync(edge, ++_thumbnailToken, priority);
    }

    public void Bind(PhotoItem item)
    {
        var binding = (ImageLoader.Identity(item), item.ModifiedAtUnix, item.FileSize, ImageLoader.BlurStrength(item), ImageLoader.IsHidden(item));
        if (_binding != binding)
        {
            // 解除遮挡或调节强度时保留已有模糊画面；新增遮挡时不能保留清晰图。
            var retained = _binding is { } old && old.Path == binding.Item1 && old.Mtime == binding.ModifiedAtUnix
                && old.Size == binding.FileSize && old.Blur > 0 && !binding.Item5 ? _image.Source : null;
            Recycle();
            _image.Source = retained;
            _binding = binding;
            _selectIn.Stop(); _selectOut.Stop();
            _selected = false; _selectMode = false; _check.IsChecked = false;
            _selectionBorder.Opacity = 0; _check.Opacity = 0;
        }
        Item = item; Detached = false;
        _favorite.Favorite = item.Favorite;
        UpdateFavoriteVisibility();
        UpdateVideoOverlay();
    }

    /// <summary>视频时长角标 + 无封面时的胶片占位；图片保持隐藏。</summary>
    private void UpdateVideoOverlay()
    {
        bool video = Item is not null && Item.IsVideo;
        bool known = video && Item!.DurationSeconds is > 0;
        _durationText.Text = known ? FormatDuration(Item!.DurationSeconds!.Value) : "";
        _durationBadge.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        _videoPlaceholder.Visibility = video && _image.Source is null && !ImageLoader.IsHidden(Item!)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatDuration(double seconds)
    {
        var total = (int)Math.Round(seconds);
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
    public void Recycle()
    {
        Detached = true;
        ++_thumbnailToken;
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose(); _thumbnailCancellation = null;
        _requestedEdge = _loadedEdge = 0;
        _image.Source = null; _binding = null;
        _hover.Opacity = 0; _hover.Fill = _hoverBrush;
        _pointerOver = false;
        _favorite.Opacity = 0;
        _favorite.IsHitTestVisible = false;
        _durationBadge.Visibility = Visibility.Collapsed;
        _videoPlaceholder.Visibility = Visibility.Collapsed;
        KeyboardFocused = false;
        var visual = ElementCompositionPreview.GetElementVisual(this);
        visual.StopAnimation("Scale"); visual.Scale = System.Numerics.Vector3.One;
        ReleasePointerCaptures();
    }

    private void OnThumbnailRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshThumbnail();
    private bool _selectMode;
    /// <summary>photos 选择模式：勾选框常显（单击=切换选中，而非打开）。</summary>
    public bool SelectMode
    {
        get => _selectMode;
        set
        {
            if (_selectMode == value) return;
            _selectMode = value;
            (_selectMode || _selected ? _selectIn : _selectOut).Begin();
        }
    }

    private bool _selected;
    /// <summary>图库逻辑键盘焦点，与勾选独立；不参与虚拟化布局测量。</summary>
    public bool KeyboardFocused
    {
        get => _keyboardFocusBorder.Visibility == Visibility.Visible;
        set => _keyboardFocusBorder.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            _check.IsChecked = value;
            (_selected ? _selectIn : _selectOut).Begin();
        }
    }

    public PhotoTile(PhotoItem item) : this() => Bind(item);

    public PhotoTile()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        UseSystemFocusVisuals = false;

        _image = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        Loaded += (_, _) =>
        {
            _thumbnailRoot = XamlRoot;
            if (_thumbnailRoot is not null) _thumbnailRoot.Changed += OnThumbnailRootChanged;
            RefreshThumbnail();
        };
        Unloaded += (_, _) =>
        {
            if (_thumbnailRoot is not null) _thumbnailRoot.Changed -= OnThumbnailRootChanged;
            _thumbnailRoot = null;
            ++_thumbnailToken;
            _thumbnailCancellation?.Cancel();
            _thumbnailCancellation?.Dispose();
            _thumbnailCancellation = null;
            _requestedEdge = _loadedEdge;
        };

        _frame = new Border
        {
            CornerRadius = new CornerRadius(CornerRadiusValue),
            Background = Brush("ControlFillColorDefaultBrush", Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)),
            Child = _image
        };

        _hover = new Rectangle
        {
            Fill = _hoverBrush,
            Opacity = 0,
            IsHitTestVisible = false,
            RadiusX = CornerRadiusValue,
            RadiusY = CornerRadiusValue
        };

        _selectionBorder = new Rectangle
        {
            Stroke = Brush("AccentFillColorDefaultBrush", Color.FromArgb(0xFF, 0x00, 0x67, 0xC0)),
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false,
            RadiusX = CornerRadiusValue,
            RadiusY = CornerRadiusValue,
            Margin = new Thickness(0)
        };

        _check = new CheckBox
        {
            IsChecked = false,
            IsHitTestVisible = false,
            IsTabStop = false,
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0
        };
        _favorite = new FavoriteButton
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _favorite.GotFocus += (_, _) => UpdateFavoriteVisibility();
        _favorite.LostFocus += (_, _) => UpdateFavoriteVisibility();
        _favorite.ToggleRequested += favorite =>
            FavoriteRequested is null ? Task.FromResult(Item.Favorite) : FavoriteRequested(Item, favorite);

        _videoPlaceholder = new FontIcon
        {
            Glyph = "\uE714",
            FontSize = 34,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _durationText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Colors.White),
        };
        _durationBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _durationText,
        };

        Children.Add(_frame);
        Children.Add(_videoPlaceholder);
        Children.Add(_hover);
        Children.Add(_selectionBorder);
        _keyboardFocusBorder = new Border
        {
            BorderThickness = new Thickness(4),
            CornerRadius = new CornerRadius(CornerRadiusValue + KeyboardFocusOutset),
            Margin = new Thickness(-KeyboardFocusOutset),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_keyboardFocusBorder);
        Children.Add(_check);
        Children.Add(_favorite);
        Children.Add(_durationBadge);

        _selectIn = BuildOpacityStoryboard(1);
        _selectOut = BuildOpacityStoryboard(0);

        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerCanceled += OnPointerCanceled;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += (_, _) => ApplyTheme();
        ApplyTheme();
    }

    private Storyboard BuildOpacityStoryboard(double to)
    {
        var sb = new Storyboard();
        foreach (FrameworkElement target in new FrameworkElement[] { _selectionBorder, _check })
        {
            var anim = new DoubleAnimationUsingKeyFrames();
            var kf = new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(167)),
                KeySpline = Spline(0, 0, 0, 1),
                Value = to
            };
            anim.KeyFrames.Add(kf);
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
        }
        return sb;
    }

    private static KeySpline Spline(double x1, double y1, double x2, double y2) => new()
    {
        ControlPoint1 = new Windows.Foundation.Point(x1, y1),
        ControlPoint2 = new Windows.Foundation.Point(x2, y2)
    };

    private static SolidColorBrush Brush(string key, Color fallback)
    {
        if (Application.Current.Resources.ContainsKey(key) && Application.Current.Resources[key] is SolidColorBrush b)
            return b;
        return new SolidColorBrush(fallback);
    }

    private void ApplyTheme()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        _hoverBrush = new SolidColorBrush(dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x19, 0x00, 0x00, 0x00));
        _pressedBrush = new SolidColorBrush(dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        _hover.Fill = _hoverBrush;
        _keyboardFocusBorder.BorderBrush = Brush("AccentTextFillColorPrimaryBrush",
            dark ? Color.FromArgb(255, 153, 217, 255) : Color.FromArgb(255, 0, 95, 184));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshThumbnail();
        // 圆角裁剪由 Border 负责；这里只更新按压缩放的中心点。
        var visual = ElementCompositionPreview.GetElementVisual(this);
        visual.CenterPoint = new System.Numerics.Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
    }

    private void UpdateFavoriteVisibility()
    {
        bool visible = _pointerOver || _favorite.FocusState == FocusState.Keyboard;
        _favorite.Opacity = visible ? 1 : 0;
        _favorite.IsHitTestVisible = visible;
    }

    private void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        _hover.Opacity = 1;
        UpdateFavoriteVisibility();
    }

    private void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        _hover.Opacity = 0;
        UpdateFavoriteVisibility();
        AnimateScale(1.0, PressDurationMs);
    }

    private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _hover.Fill = _pressedBrush;
        AnimateScale(PressScale, PressDurationMs);
    }

    private void OnPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _hover.Fill = _hoverBrush;
        AnimateScale(1.0, PressDurationMs);
    }

    private void OnPointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _hover.Fill = _hoverBrush;
        AnimateScale(1.0, PressDurationMs);
    }

    private void OnPointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _hover.Fill = _hoverBrush;
        AnimateScale(1.0, PressDurationMs);
    }

    private void AnimateScale(double to, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;
        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new System.Numerics.Vector3((float)to, (float)to, 1f));
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Scale", anim);
    }
}
