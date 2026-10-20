using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using XamlOrientation = Microsoft.UI.Xaml.Controls.Orientation;

namespace Gallery.Controls;

/// <summary>
/// 连续滚动条：值始终是 <see cref="double"/>。拖动时滑块 1:1 跟手；点击轨道任意位置时，
/// 滑块和内容一起用短缓动滑到点击处，不走原生 ScrollBar 的 LargeChange 跳页。
/// 竖向/横向、步长吸附、轨道/滑块厚度均可配置。
/// 宿主订阅 <see cref="ValueChanged"/> 同步内容位置，订阅 <see cref="InteractionStarted"/> /
/// <see cref="InteractionCompleted"/> 控制提交时机；组件统一管理活动唤醒、悬停保持和空闲自动淡出。
/// </summary>
public sealed class ContinuousScrollBar : Grid
{
    private readonly Grid _indicator = new() { Opacity = .01, IsHitTestVisible = false };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private Storyboard? _indicatorAnimation;
    private bool _hovered, _interactionActive, _indicatorVisible;
    private readonly Border _track;
    private readonly Border _thumb;
    private readonly TranslateTransform _thumbOffset = new();

    private XamlOrientation _orientation = XamlOrientation.Vertical;
    private double _minimum, _maximum, _value, _viewport = 9;
    private double _step = 1, _minimumThumbLength = 28;
    private double _trackThickness = 2, _thumbThickness = 6;
    private double _thumbCrossOffset;
    private uint? _pointerId;
    private bool _thumbDrag, _clickMode, _animating;
    private double _pressPosition, _dragOffset;
    private double _animFrom, _animTo, _animStarted, _animDuration;
    private Action? _animCompleted;

    public ContinuousScrollBar()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsHitTestVisible = true;

        _track = new Border
        {
            Background = ThemeBrush("ControlStrongStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray),
            Opacity = .35,
            IsHitTestVisible = false,
        };
        _thumb = new Border
        {
            Background = ThemeBrush("ControlStrongFillColorDefaultBrush", Microsoft.UI.Colors.Gray),
            RenderTransform = _thumbOffset,
            IsHitTestVisible = false,
        };
        _indicator.Children.Add(_track);
        _indicator.Children.Add(_thumb);
        Children.Add(_indicator);

        ApplyOrientation();
        SizeChanged += (_, _) => UpdateThumb();
        PointerEntered += (_, _) => { _hovered = true; NotifyActivity(); HoverChanged?.Invoke(true); };
        PointerExited += (_, _) => { _hovered = false; NotifyActivity(); HoverChanged?.Invoke(false); };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerCanceled += (_, _) => EndPointer();
        ActualThemeChanged += (_, _) => RefreshTheme();
        Loaded += (_, _) => NotifyActivity();
        Unloaded += (_, _) =>
        {
            StopAnimation();
            _hovered = _interactionActive = false;
            _pointerId = null;
            _thumbDrag = _clickMode = false;
            ReleasePointerCaptures();
            HideIndicator();
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_hovered && !_interactionActive) AnimateIndicator(false);
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible) NotifyActivity();
            else HideIndicator();
        });
    }

    /// <summary>值变化。拖动、点击动画、程序设置 <see cref="Value"/> 都会触发。</summary>
    public event Action<double>? ValueChanged;

    /// <summary>一次拖动/点击交互开始，组件保持显示；宿主据此开始同步内容。</summary>
    public event Action? InteractionStarted;

    /// <summary>交互结束（点击动画和吸附已完成），宿主可提交详情或最终位置。</summary>
    public event Action? InteractionCompleted;

    /// <summary>指针进入/离开；显隐由组件管理，宿主可据此调整滑块外观。</summary>
    public event Action<bool>? HoverChanged;

    /// <summary>内容滚动或用户输入时唤醒；空闲 700ms 后用 200ms 淡出，悬停和交互中保持显示。</summary>
    public void NotifyActivity()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || Range <= 0) return;
        _hideTimer.Stop();
        AnimateIndicator(true);
        if (!_hovered && !_interactionActive) _hideTimer.Start();
    }

    /// <summary>宿主页面隐藏时立即收起指示层并停止显隐计时，不改变组件布局和命中范围。</summary>
    public void HideIndicator()
    {
        _hideTimer.Stop();
        _hovered = false;
        _indicatorAnimation?.Stop();
        _indicatorAnimation = null;
        _indicatorVisible = false;
        _indicator.Opacity = .01;
    }

    private void AnimateIndicator(bool visible)
    {
        if (_indicatorVisible == visible) return;
        var from = _indicator.Opacity;
        _indicatorAnimation?.Stop();
        _indicator.Opacity = from;
        _indicatorVisible = visible;
        var target = visible ? 1 : .01;
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = from, To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(visible ? 30 : 200)),
        };
        Storyboard.SetTarget(animation, _indicator);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_indicatorAnimation, storyboard)) return;
            storyboard.Stop();
            _indicator.Opacity = target;
            _indicatorAnimation = null;
        };
        _indicatorAnimation = storyboard;
        storyboard.Begin();
    }

    /// <summary>竖向或横向。建议在设置 Width/Height 之前先设好方向。</summary>
    public XamlOrientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation == value) return;
            _orientation = value;
            ApplyOrientation();
        }
    }

    public double Minimum
    {
        get => _minimum;
        set
        {
            var changed = _minimum != value;
            _minimum = value;
            _maximum = Math.Max(_maximum, _minimum);
            SetValueCore(_value);
            UpdateThumb();
            if (Range <= 0) HideIndicator();
            else if (changed) NotifyActivity();
        }
    }

    public double Maximum
    {
        get => _maximum;
        set
        {
            var changed = _maximum != Math.Max(_minimum, value);
            _maximum = Math.Max(_minimum, value);
            SetValueCore(_value);
            UpdateThumb();
            if (Range <= 0) HideIndicator();
            else if (changed) NotifyActivity();
        }
    }

    /// <summary>可见范围，用于计算滑块长度（例如列表一屏 9 项）。</summary>
    public double ViewportSize
    {
        get => _viewport;
        set
        {
            _viewport = Math.Max(1, value);
            UpdateThumb();
        }
    }

    /// <summary>松手后的吸附步长；设为 0 表示不吸附、保留连续值。</summary>
    public double Step
    {
        get => _step;
        set => _step = Math.Max(0, value);
    }

    /// <summary>滑块最短长度，避免长列表里滑块小到抓不住。</summary>
    public double MinimumThumbLength
    {
        get => _minimumThumbLength;
        set
        {
            _minimumThumbLength = Math.Max(8, value);
            UpdateThumb();
        }
    }

    /// <summary>轨道厚度（竖向是宽，横向是高）。</summary>
    public double TrackThickness
    {
        get => _trackThickness;
        set
        {
            _trackThickness = Math.Max(1, value);
            UpdateTrack();
        }
    }

    /// <summary>滑块厚度（竖向是宽，横向是高）。</summary>
    public double ThumbThickness
    {
        get => _thumbThickness;
        set
        {
            _thumbThickness = Math.Max(2, value);
            ApplyThumbThickness();
        }
    }

    /// <summary>滑块在横轴上的额外偏移，用于匹配原滚动条的贴边位置。</summary>
    public double ThumbCrossOffset
    {
        get => _thumbCrossOffset;
        set { _thumbCrossOffset = value; UpdateThumb(); }
    }

    /// <summary>程序设置值会立即停止正在进行的点击/吸附动画。</summary>
    public double Value
    {
        get => _value;
        set
        {
            var finished = _interactionActive && _pointerId is null;
            StopAnimation();
            if (finished) _interactionActive = false;
            SetValueCore(value);
            if (finished) NotifyActivity();
        }
    }

    /// <summary>停止动画并释放捕获，不吸附；有进行中的交互时发出完成通知。</summary>
    public void CancelInteraction()
    {
        var active = _pointerId is not null || _animating || _thumbDrag || _clickMode;
        StopAnimation();
        _pointerId = null;
        _thumbDrag = false;
        _clickMode = false;
        ReleasePointerCaptures();
        _interactionActive = false;
        if (active) NotifyActivity();
        if (active) InteractionCompleted?.Invoke();
    }

    private double Range => Math.Max(0, _maximum - _minimum);
    private double ActualLength => _orientation == XamlOrientation.Vertical ? ActualHeight : ActualWidth;
    private double Travel => Math.Max(0, ActualLength - ThumbLength());

    private void ApplyOrientation()
    {
        // 默认尺寸只在这里给；需要自定义宽高时在设置 Orientation 之后覆盖。
        if (_orientation == XamlOrientation.Vertical)
        {
            Width = 16;
            Height = double.NaN;
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Stretch;
            _track.HorizontalAlignment = HorizontalAlignment.Center;
            _track.VerticalAlignment = VerticalAlignment.Stretch;
            _thumb.HorizontalAlignment = HorizontalAlignment.Center;
            _thumb.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            Height = 16;
            Width = double.NaN;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Bottom;
            _track.HorizontalAlignment = HorizontalAlignment.Stretch;
            _track.VerticalAlignment = VerticalAlignment.Center;
            _thumb.HorizontalAlignment = HorizontalAlignment.Left;
            _thumb.VerticalAlignment = VerticalAlignment.Center;
        }
        _thumbOffset.X = 0;
        _thumbOffset.Y = 0;
        UpdateTrack();
        ApplyThumbThickness();
        UpdateThumb();
    }

    private void UpdateTrack()
    {
        if (_orientation == XamlOrientation.Vertical)
        {
            _track.Width = _trackThickness;
            _track.Height = double.NaN;
        }
        else
        {
            _track.Height = _trackThickness;
            _track.Width = double.NaN;
        }
        _track.CornerRadius = new CornerRadius(_trackThickness / 2);
    }

    private void ApplyThumbThickness()
    {
        if (_orientation == XamlOrientation.Vertical) _thumb.Width = _thumbThickness;
        else _thumb.Height = _thumbThickness;
        _thumb.CornerRadius = new CornerRadius(_thumbThickness / 2);
        UpdateThumb();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
        if (_pointerId is not null || ActualLength <= 0 || Range <= 0) return;

        _pointerId = e.Pointer.PointerId;
        CapturePointer(e.Pointer);
        StopAnimation();
        _pressPosition = Position(point.Position);
        _thumbDrag = false;
        _clickMode = false;

        _interactionActive = true;
        NotifyActivity();
        InteractionStarted?.Invoke();
        var start = PositionFor(_value);
        var length = ThumbLength();
        if (_pressPosition >= start && _pressPosition <= start + length)
        {
            _thumbDrag = true;
            _dragOffset = _pressPosition - start;
        }
        else
        {
            _clickMode = true;
            _dragOffset = length / 2;
            var target = ValueAt(_pressPosition - _dragOffset);
            StartAnimation(target, DurationFor(target - _value), OnClickAnimationDone);
        }
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        var position = Position(e.GetCurrentPoint(this).Position);
        if (_clickMode)
        {
            if (Math.Abs(position - _pressPosition) <= 3) return;
            // 点击后继续拖：取消点击动画，切成普通拖动。
            _clickMode = false;
            StopAnimation();
            _thumbDrag = true;
            _dragOffset = ThumbLength() / 2;
            SetValueCore(ValueAt(position - _dragOffset));
            e.Handled = true;
            return;
        }
        if (!_thumbDrag) return;
        SetValueCore(ValueAt(position - _dragOffset));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        _pointerId = null;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (_clickMode)
        {
            // 点击动画继续走完，完成时再提交。
            _clickMode = false;
            return;
        }
        if (_thumbDrag)
        {
            _thumbDrag = false;
            SnapAndComplete();
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        EndPointer();
    }

    private void EndPointer()
    {
        _pointerId = null;
        if (_thumbDrag)
        {
            _thumbDrag = false;
            SnapAndComplete();
        }
        else if (_clickMode)
        {
            // 捕获丢失不影响点击动画；让它自己走完。
            _clickMode = false;
        }
    }

    private void SnapAndComplete()
    {
        var target = SnapTarget();
        if (Math.Abs(target - _value) > .001)
            StartAnimation(target, 90, CompleteInteraction);
        else
            CompleteInteraction();
    }

    private void OnClickAnimationDone()
    {
        var target = SnapTarget();
        if (Math.Abs(target - _value) > .001)
            StartAnimation(target, 90, CompleteInteraction);
        else
            CompleteInteraction();
    }

    private double SnapTarget()
    {
        if (_step <= 0) return Math.Clamp(_value, _minimum, _maximum);
        return Math.Clamp(Math.Round(_value / _step) * _step, _minimum, _maximum);
    }

    private void CompleteInteraction()
    {
        _clickMode = false;
        _thumbDrag = false;
        _animating = false;
        _interactionActive = false;
        NotifyActivity();
        InteractionCompleted?.Invoke();
    }

    private void StartAnimation(double target, double durationMs, Action? completed)
    {
        StopAnimation();
        target = Math.Clamp(target, _minimum, _maximum);
        if (durationMs <= 0 || Math.Abs(target - _value) < .0001)
        {
            SetValueCore(target);
            completed?.Invoke();
            return;
        }
        _animFrom = _value;
        _animTo = target;
        _animDuration = durationMs;
        _animStarted = Stopwatch.GetTimestamp();
        _animCompleted = completed;
        _animating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (_animating) CompositionTarget.Rendering -= OnRendering;
        _animating = false;
        _animCompleted = null;
    }

    private void OnRendering(object? sender, object e)
    {
        var elapsed = (Stopwatch.GetTimestamp() - _animStarted) * 1000.0 / Stopwatch.Frequency;
        var t = _animDuration <= 0 ? 1 : Math.Clamp(elapsed / _animDuration, 0, 1);
        SetValueCore(_animFrom + (_animTo - _animFrom) * Ease(t));
        if (t < 1) return;
        CompositionTarget.Rendering -= OnRendering;
        _animating = false;
        var completed = _animCompleted;
        _animCompleted = null;
        completed?.Invoke();
    }

    private static double Ease(double t) => t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private static double DurationFor(double distance) => Math.Clamp(120 + Math.Abs(distance) * 6, 150, 320);

    private void SetValueCore(double value)
    {
        var clamped = Math.Clamp(value, _minimum, _maximum);
        if (Math.Abs(clamped - _value) < 1e-9) { UpdateThumb(); return; }
        _value = clamped;
        UpdateThumb();
        NotifyActivity();
        ValueChanged?.Invoke(_value);
    }

    private double ThumbLength()
    {
        var length = ActualLength;
        if (length <= 0) return 0;
        if (Range <= 0) return length;
        var raw = length * _viewport / (Range + _viewport);
        return Math.Clamp(raw, Math.Min(_minimumThumbLength, length), length);
    }

    private double PositionFor(double value)
    {
        var travel = Travel;
        if (Range <= 0 || travel <= 0) return 0;
        return (value - _minimum) / Range * travel;
    }

    private double ValueAt(double position)
    {
        var travel = Travel;
        if (Range <= 0 || travel <= 0) return _minimum;
        return _minimum + Math.Clamp(position, 0, travel) / travel * Range;
    }

    private void UpdateThumb()
    {
        var length = ThumbLength();
        if (length <= 0) return;
        if (_orientation == XamlOrientation.Vertical)
        {
            // 初始 Height 是 NaN；NaN 参与比较会让“是否需要更新”恒为 false，滑块会一直 0 高。
            if (!double.IsFinite(_thumb.Height) || Math.Abs(_thumb.Height - length) > .01) _thumb.Height = length;
            _thumbOffset.X = _thumbCrossOffset;
            _thumbOffset.Y = PositionFor(_value);
        }
        else
        {
            if (!double.IsFinite(_thumb.Width) || Math.Abs(_thumb.Width - length) > .01) _thumb.Width = length;
            _thumbOffset.Y = _thumbCrossOffset;
            _thumbOffset.X = PositionFor(_value);
        }
    }

    private double Position(Point point) => _orientation == XamlOrientation.Vertical ? point.Y : point.X;

    private void RefreshTheme()
    {
        _track.Background = ThemeBrush("ControlStrongStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray);
        _thumb.Background = ThemeBrush("ControlStrongFillColorDefaultBrush", Microsoft.UI.Colors.Gray);
    }

    private static Brush ThemeBrush(string key, Color fallback)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);
}
