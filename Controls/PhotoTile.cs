using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Gallery.Models;

namespace Gallery.Controls;

/// <summary>
/// 图库格子。视觉结构与状态机对齐 Photos 的 GalleryTileContainer：
/// 圆角 4、悬停零时长硬切、选中 167ms cubic-bezier(0,0,0,1)、按压 0.98 / 100ms 线性。
/// </summary>
public sealed class PhotoTile : Grid
{
    private const double CornerRadiusValue = 4;
    private const double PressScale = 0.98;
    private const int PressDurationMs = 100;

    private readonly Border _frame;
    private readonly Image _image;
    private readonly Rectangle _hover;
    private readonly Rectangle _selectionBorder;
    private readonly CheckBox _check;
    private readonly Storyboard _selectIn;
    private readonly Storyboard _selectOut;

    private SolidColorBrush _hoverBrush = new(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
    private SolidColorBrush _pressedBrush = new(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    public PhotoItem Item { get; }

    private bool _selected;
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

    public PhotoTile(PhotoItem item)
    {
        Item = item;
        Background = new SolidColorBrush(Colors.Transparent);
        UseSystemFocusVisuals = false;

        _image = new Image
        {
            Source = item.Thumbnail,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
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

        Children.Add(_frame);
        Children.Add(_hover);
        Children.Add(_selectionBorder);
        Children.Add(_check);

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
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 圆角裁剪由 Border 负责；这里只更新按压缩放的中心点。
        var visual = ElementCompositionPreview.GetElementVisual(this);
        visual.CenterPoint = new System.Numerics.Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
    }

    private void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => _hover.Opacity = 1;

    private void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _hover.Opacity = 0;
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
