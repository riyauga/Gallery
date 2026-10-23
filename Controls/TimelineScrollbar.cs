using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;
using Gallery.Models;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace Gallery.Controls;

/// <summary>
/// 右侧时间线滚动条。结构与 Photos.Controls.Timeline / TimelineScrollbar.xaml 一一对应：
/// 行高 17 + 8 + * + 8 + 17；刻度右对齐、右缩进 10、Segoe UI 12 号 TrimToCapHeight；
/// 标记 26×3、圆角 2、右缩进 10；悬停出灰色预览标记 + 亚克力日期标签（28 高、圆角 4、12 号 SemiBold）；
/// 点击/拖动按"位置 → 条目索引"跳转（TimelineLayout 同款数学）；淡入 30ms、淡出 200ms、2s 自动隐藏。
/// </summary>
public sealed class TimelineScrollbar : Grid
{
    // TimelineScrollbar.cs 公开常量
    private const double ControlWidth = 48;
    private const double ArrowRowHeight = 17;
    private const double TargetAreaHeight = 8;
    private const double ScrollingOffset = 177;

    // TimelineScrollbarStyles.xaml
    private const double AreaRightInset = 10;
    private const double MarkerWidth = 26;
    private const double MarkerHeight = 3;
    private const double MarkerCornerRadius = 2;
    private const int HideDelayMs = 2000;

    // TimelineLabelTextBlockPool / TimelineIndicatorPool
    private const double LabelHeight = 12;
    private const double LabelFontSize = 12;
    private const double IndicatorDiameter = 3;
    private const double MinimumSpaceBetweenTwoElements = 12;

    // TimelineArrowIcon
    private const double ArrowFontSize = 9;
    private const double ArrowGlyphHeight = 7;
    private const double ArrowGlyphWidth = 9;

    // SecondaryLabelPopup
    private const double PopupVerticalOffset = 14;
    private const double PopupStaticHorizontalOffset = 7;

    /// <summary>视觉上等于不可见的最低不透明度；用作淡出终点，避免合成层停止绘制。</summary>
    private const double MinOpacity = 0.01;

    /// <summary>TimelineArea 顶部相对整个控件顶部的偏移（上箭头 17 + 目标区 8）。</summary>
    private const double AreaTop = ArrowRowHeight + TargetAreaHeight;

    private readonly Grid _area = new();
    private readonly Canvas _labelCanvas = new();
    private readonly Grid _markers = new();
    private readonly Border _anchoredMarker;
    private readonly Border _hoveringMarker;
    private readonly Popup _popup = new();
    private readonly Grid _secondaryPopup;
    private readonly TextBlock _secondaryText;
    private readonly TranslateTransform _markerTransform = new();
    private double _markerTargetTop = double.NaN;
    private readonly Storyboard _markerStoryboard = new();
    private readonly DoubleAnimation _markerAnimation = new()
    {
        Duration = new Duration(TimeSpan.FromMilliseconds(100)),
        EnableDependentAnimation = true,
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
    };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
    private readonly List<FrameworkElement> _labels = new();
    private readonly List<FrameworkElement> _indicators = new();

    private ScrollViewer? _scroller;
    private PhotoGalleryPanel? _panel;
    private IReadOnlyList<PhotoItem> _items = Array.Empty<PhotoItem>();

    private double _areaHeight;
    private bool _dragging;
    private bool _hovering;
    private bool _animateMarkerOnScroll = true;

    public TimelineScrollbar()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        GotFocus += (_, _) => Show();
        LostFocus += (_, _) => Hide();
        KeyDown += (_, args) =>
        {
            if (_scroller is null || _items.Count == 0) return;
            switch (args.Key)
            {
                case Windows.System.VirtualKey.Up: ScrollBy(-ScrollingOffset); break;
                case Windows.System.VirtualKey.Down: ScrollBy(ScrollingOffset); break;
                case Windows.System.VirtualKey.PageUp: ScrollBy(-_scroller.ViewportHeight); break;
                case Windows.System.VirtualKey.PageDown: ScrollBy(_scroller.ViewportHeight); break;
                case Windows.System.VirtualKey.Home: GoToIndex(0); break;
                case Windows.System.VirtualKey.End: GoToIndex(_items.Count - 1); break;
                default: return;
            }
            Show();
            args.Handled = true;
        };
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Focus(FocusState.Pointer);
        }), true);
        Width = ControlWidth;
        AutomationProperties.SetName(this, "照片时间线");
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Colors.Transparent);
        // 注意：初始 Opacity 不能写成 0 —— 代码构造时置 0 会让合成层一直不绘制，
        // 之后 animation 到 1 也不显示。MinOpacity 视觉上等于透明，且能正常淡入。
        Opacity = MinOpacity;

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(ArrowRowHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(TargetAreaHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(TargetAreaHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(ArrowRowHeight) });

        var up = CreateArrow("\uEDDB", -1);
        var down = CreateArrow("\uEDDC", 1);
        SetRow(up, 0);
        SetRow(down, 4);

        var topTarget = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        SetRow(topTarget, 1);
        topTarget.Tapped += (_, _) => GoToIndex(0);

        var bottomTarget = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        SetRow(bottomTarget, 3);
        bottomTarget.Tapped += (_, _) => GoToIndex(_items.Count - 1);

        _area.Background = new SolidColorBrush(Colors.Transparent);
        SetRow(_area, 2);

        // MarkersGrid：原版 Row=2 / Padding 0,0,10,0 / 右对齐，标记用 VerticalAlignment=Top
        _markers.Padding = new Thickness(0, 0, AreaRightInset, 0);
        _markers.HorizontalAlignment = HorizontalAlignment.Right;
        _markers.VerticalAlignment = VerticalAlignment.Stretch;
        SetRow(_markers, 2);

        _labelCanvas.Margin = new Thickness(0, 0, AreaRightInset, 0);
        _labelCanvas.IsHitTestVisible = false;

        _area.Children.Add(_labelCanvas);

        // Photos TimelineScrollbar.xaml：保留 ThemeResource 和默认文本主题，不在构造时固定颜色。
        _secondaryPopup = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  Height="28" Background="{ThemeResource AcrylicInAppFillColorDefaultBrush}"
                  CornerRadius="4" IsHitTestVisible="False">
                <TextBlock Margin="8,0,8,0" HorizontalAlignment="Center" VerticalAlignment="Center"
                           FontFamily="Segoe UI Variable" FontSize="12" FontWeight="SemiBold" />
            </Grid>
            """);
        _secondaryText = (TextBlock)_secondaryPopup.Children[0];
        // 原版 SecondaryLabelPopup：真 Popup，VerticalOffset 14，横向 = -文本宽 + 7
        _popup.Child = _secondaryPopup;
        _popup.IsHitTestVisible = false;
        _popup.VerticalOffset = PopupVerticalOffset;

        Children.Add(_popup);          // 原版：Popup 不参与布局，放在 Grid 首位
        Children.Add(up);
        Children.Add(topTarget);
        Children.Add(_area);
        Children.Add(_markers);
        Children.Add(bottomTarget);
        Children.Add(down);

        _anchoredMarker = new Border
        {
            Width = MarkerWidth,
            Height = MarkerHeight,
            CornerRadius = new CornerRadius(MarkerCornerRadius),
            Background = Brush("AccentFillColorDefaultBrush", Color.FromArgb(0xFF, 0x00, 0x67, 0xC0)),
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = _markerTransform,
            IsHitTestVisible = false
        };
        _hoveringMarker = new Border
        {
            Width = MarkerWidth,
            Height = MarkerHeight,
            CornerRadius = new CornerRadius(MarkerCornerRadius),
            Background = Brush("SystemFillColorSolidNeutralBrush", Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _markers.Children.Add(_anchoredMarker);
        _markers.Children.Add(_hoveringMarker);

        Storyboard.SetTarget(_markerAnimation, _markerTransform);
        Storyboard.SetTargetProperty(_markerAnimation, "Y");
        _markerStoryboard.Children.Add(_markerAnimation);

        _area.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - _areaHeight) < 0.5) return;
            _areaHeight = e.NewSize.Height;
            RebuildElements();
            UpdateAnchoredMarker(animate: false);
        };

        // 原版 TimelineScrollbar_PointerEntered / _PointerExited 挂在控件自身
        PointerEntered += (_, _) => Show();
        PointerExited += (_, _) =>
        {
            if (!_dragging) Hide();
        };

        _area.PointerEntered += OnAreaPointerEntered;
        _area.PointerMoved += OnAreaPointerMoved;
        _area.PointerExited += OnAreaPointerExited;
        _area.PointerPressed += OnAreaPointerPressed;
        _area.PointerReleased += OnAreaPointerReleased;
        _area.PointerCaptureLost += (_, _) => EndDrag();
        _area.PointerCanceled += (_, _) => EndDrag();

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_hovering) Hide();
        };
    }

    private static KeySpline Spline(double x1, double y1, double x2, double y2) => new()
    {
        ControlPoint1 = new Point(x1, y1),
        ControlPoint2 = new Point(x2, y2)
    };

    private static SolidColorBrush Brush(string key, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush b)
            return b;
        return new SolidColorBrush(fallback);
    }

    private static Brush AnyBrush(string key, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b)
            return b;
        return new SolidColorBrush(fallback);
    }

    private RepeatButton CreateArrow(string glyph, int direction)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = ArrowFontSize,
            Height = ArrowGlyphHeight,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Foreground = AnyBrush("ScrollBarButtonArrowForeground", Color.FromArgb(0x9E, 0, 0, 0))
        };
        var button = new RepeatButton
        {
            Content = icon,
            IsTabStop = false,
            AllowFocusOnInteraction = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Height = ArrowRowHeight,
            Padding = direction < 0 ? new Thickness(0, 6, 10, 4) : new Thickness(0, 4, 10, 6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Interval = 50
        };
        button.Click += (_, _) => ScrollBy(ScrollingOffset * direction);
        return button;
    }

    public bool IsAscending { get; set; }

    public void Attach(ScrollViewer scroller, IReadOnlyList<PhotoItem> items, PhotoGalleryPanel panel)
    {
        if (_scroller is not null)
        {
            _scroller.ViewChanged -= OnViewChanged;
            _scroller.SizeChanged -= OnScrollerSizeChanged;
        }
        if (_panel is not null)
        {
            _panel.SizeChanged -= OnPanelSizeChanged;
        }

        _scroller = scroller;
        _panel = panel;
        _items = items;
        IsTabStop = items.Count > 0;

        _scroller.ViewChanged += OnViewChanged;
        _scroller.SizeChanged += OnScrollerSizeChanged;
        _panel.SizeChanged += OnPanelSizeChanged;

        RebuildElements();
        UpdateAnchoredMarker(animate: false);
        // WinUI 3 的 Popup 必须显式给 XamlRoot，否则不显示（WPF/UWP 无此要求）
        if (XamlRoot is not null) _popup.XamlRoot = XamlRoot;
        // 进入图库时先亮 2 秒再淡出（Photos：Timeline 首次显示后 2s 自动隐藏）
        if (_items.Count > 0) ShowWithTimer();
    }

    /// <summary>条目或排序变化后重建刻度。</summary>
    public void Refresh()
    {
        RebuildElements();
        UpdateAnchoredMarker(animate: false);
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAnchoredMarker(animate: false);

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAnchoredMarker(animate: false);

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ShowWithTimer();
        UpdateAnchoredMarker(animate: _animateMarkerOnScroll && !_dragging);
    }

    private void ScrollBy(double delta)
    {
        if (_scroller is null) return;
        _animateMarkerOnScroll = true;
        double target = Math.Clamp(_scroller.VerticalOffset + delta, 0, _scroller.ScrollableHeight);
        _scroller.ChangeView(null, target, null, true);
    }

    private void GoToIndex(int index)
    {
        if (_scroller is null || _panel is null || _items.Count == 0) return;
        index = Math.Clamp(index, 0, _items.Count - 1);
        _animateMarkerOnScroll = false;
        _scroller.ChangeView(null, Math.Clamp(_panel.TopOfIndex(index), 0, _scroller.ScrollableHeight), null, true);
        MoveAnchoredMarkerToPosition(PositionFromIndices(index, index), animate: false);
    }

    #region 位置 ↔ 索引映射（TimelineLayout 同款数学）

    private double Pitch => _items.Count > 0 ? _areaHeight / _items.Count : 0;

    private (int Start, int End) IndexesFromPosition(int position)
    {
        if (_items.Count == 0 || Pitch <= 0) return (0, 0);
        double pitch = Pitch;
        int start = (int)(position / pitch);
        int end = (int)(start + 1.0 / pitch - 1.0);
        return (start, end);
    }

    private int IndexFromPosition(int position)
    {
        if (_items.Count == 0) return 0;
        var (start, end) = IndexesFromPosition(position);
        int index = position < _areaHeight / 2.0 ? start : end;
        return Math.Clamp(index, 0, _items.Count - 1);
    }

    private int PositionFromIndices(int first, int last)
    {
        if (_items.Count == 0 || _areaHeight < 1) return 0;
        double pitch = Pitch;
        int start = Math.Clamp((int)Math.Round(first * pitch), 0, (int)_areaHeight - 1);
        int end = Math.Clamp((int)(start + pitch), 0, (int)_areaHeight - 1);
        if (first == 0 && (_scroller is null || _scroller.VerticalOffset <= 0.5)) return start;
        if (end >= _areaHeight - 1 && _scroller is not null && _scroller.ScrollableHeight - _scroller.VerticalOffset <= 0.5)
            return end;
        return (start + end) / 2;
    }

    #endregion

    private void UpdateAnchoredMarker(bool animate)
    {
        if (_panel is null || _scroller is null || _items.Count == 0 || _areaHeight < 1) return;
        int first = _panel.FirstVisibleIndex(_scroller.VerticalOffset);
        int last = first;
        double bottom = _scroller.VerticalOffset + _scroller.ViewportHeight;
        for (int i = first; i < _items.Count; i++)
        {
            if (_panel.TopOfIndex(i) > bottom) break;
            last = i;
        }
        MoveAnchoredMarkerToPosition(PositionFromIndices(first, last), animate);
    }

    private void MoveAnchoredMarkerToPosition(int position, bool animate = false)
    {
        double top = Math.Clamp(position - MarkerHeight / 2, 0, Math.Max(0, _areaHeight - MarkerHeight));
        if (animate && top == _markerTargetTop) return;

        // 停止前保存正在显示的位置；基础值跟随目标，后续重播不退回旧位置。
        double current = _markerTransform.Y;
        _markerTargetTop = top;
        _markerStoryboard.Stop();
        _markerTransform.Y = top;
        if (animate && Math.Abs(current - top) > 0.5)
        {
            _markerAnimation.From = current;
            _markerAnimation.To = top;
            _markerStoryboard.Begin();
        }
    }

    #region 刻度

    private string PrimaryLabel(PhotoItem item) => item.Date.Year.ToString();

    private string SecondaryLabel(PhotoItem item) => $"{item.Date.Year}年{item.Date.Month}月";

    private void RebuildElements()
    {
        foreach (var e in _labels) _labelCanvas.Children.Remove(e);
        foreach (var e in _indicators) _labelCanvas.Children.Remove(e);
        _labels.Clear();
        _indicators.Clear();

        int height = (int)Math.Floor(_areaHeight);
        if (height < 1 || _items.Count == 0) return;

        double pitch = _areaHeight / _items.Count;
        var primary = new string[height];
        var secondary = new string[height];
        for (int y = 0; y < height; y++)
        {
            int index = Math.Clamp((int)(y / pitch), 0, _items.Count - 1);
            primary[y] = PrimaryLabel(_items[index]);
            secondary[y] = SecondaryLabel(_items[index]);
        }
        primary[height - 1] = PrimaryLabel(_items[^1]);
        secondary[height - 1] = SecondaryLabel(_items[^1]);

        var elements = FilterElements(GenerateElements(primary, secondary, height, IsAscending), IsAscending);

        // Canvas 宽度要等布局完成，取不到就按 48 - 右缩进 估算
        double canvasWidth = _labelCanvas.ActualWidth > 0 ? _labelCanvas.ActualWidth : ControlWidth - AreaRightInset;

        foreach (var element in elements)
        {
            if (element.IsLabel)
            {
                // Photos TimelineLabelTextBlockPool：文字前景由控件主题继承。
                // 40 是宽度上限（MaxWidth），不能写成硬 Width：硬宽度会把 DesiredSize 顶到 40，
                // 后面 canvasWidth - DesiredSize.Width 就恒为 0，刻度会被推到画布左缘（实测偏左 12 DIP）。
                // 原版标签实际宽度就是文字宽度（UIA 实测 26 DIP），右缘与标签画布右缘齐平（距控件右缘 10）。
                var tb = new TextBlock
                {
                    Text = element.Text,
                    MaxWidth = 40,
                    Height = LabelHeight,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = LabelFontSize,
                    IsTextScaleFactorEnabled = false,
                    TextLineBounds = Microsoft.UI.Xaml.TextLineBounds.TrimToCapHeight,
                    IsHitTestVisible = false
                };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                tb.Width = tb.DesiredSize.Width;
                Canvas.SetLeft(tb, Math.Max(0, canvasWidth - tb.DesiredSize.Width));
                Canvas.SetTop(tb, element.Top);
                _labelCanvas.Children.Add(tb);
                _labels.Add(tb);
            }
            else
            {
                var dot = new Ellipse
                {
                    Width = IndicatorDiameter,
                    Height = IndicatorDiameter,
                    Fill = AnyBrush("ControlStrongFillColorDefaultBrush", Color.FromArgb(0x72, 0, 0, 0)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, Math.Max(0, canvasWidth - ArrowGlyphWidth / 2.0 - IndicatorDiameter / 2.0));
                Canvas.SetTop(dot, element.Top);
                _labelCanvas.Children.Add(dot);
                _indicators.Add(dot);
            }
        }
    }

    private readonly record struct Element(int Top, string Text, bool IsLabel)
    {
        public int Bottom => Top + (IsLabel ? (int)LabelHeight : (int)IndicatorDiameter);
    }

    private static List<Element> GenerateElements(string[] primary, string[] secondary, int height, bool isAscending)
    {
        var list = new List<Element>();
        if (height < 1) return list;
        if (isAscending)
        {
            list.Add(new Element(0, primary[0], true));
        }
        for (int position = 0; position < height - 1; position++)
        {
            if (primary[position] != primary[position + 1])
            {
                int at = isAscending ? position + 1 : position;
                int top = isAscending
                    ? Math.Min(at, height - (int)LabelHeight)
                    : Math.Max(at - (int)LabelHeight, 0);
                list.Add(new Element(top, primary[at], true));
            }
            else if (secondary[position] != secondary[position + 1])
            {
                int at = isAscending ? position + 1 : position;
                int top = isAscending
                    ? Math.Min(at, height - (int)IndicatorDiameter)
                    : Math.Max(at - (int)IndicatorDiameter, 0);
                list.Add(new Element(top, secondary[at], false));
            }
        }
        if (!isAscending)
        {
            list.Add(new Element(Math.Max(height - 1 - (int)LabelHeight, 0), primary[height - 1], true));
        }
        return list;
    }

    /// <summary>移植 Photos TimelineElementDistanceFilter：预留末端刻度，相邻至少间隔 12px，年份优先于圆点。</summary>
    private static List<Element> FilterElements(List<Element> elements, bool isAscending)
    {
        var source = (isAscending ? Enumerable.Reverse(elements) : elements).ToList();
        if (source.Count < 2) return source;
        var result = new List<Element>();
        var first = source[0];
        var last = source[^1];
        if (Distance(last, first) >= MinimumSpaceBetweenTwoElements) result.Add(first);
        foreach (var element in source.Skip(1))
        {
            if ((element == last || Distance(last, element) >= MinimumSpaceBetweenTwoElements)
                && !TryAdd(element) && element.IsLabel && !result[^1].IsLabel)
            {
                result.RemoveAt(result.Count - 1);
                TryAdd(element);
            }
        }
        return result;

        int Distance(Element lowest, Element highest) => isAscending
            ? Math.Abs(lowest.Bottom - highest.Top)
            : Math.Abs(lowest.Top - highest.Bottom);

        bool TryAdd(Element element)
        {
            if (result.Count > 0 && Distance(element, result[^1]) < MinimumSpaceBetweenTwoElements) return false;
            result.Add(element);
            return true;
        }
    }

    #endregion

    #region 悬停与拖动

    /// <summary>
    /// 对应原版 TimelineArea_PointerEntered：先 CapturePointer。
    /// 捕获指针后 Popup 打开不会再触发 PointerExited，气泡与标记才能留住。
    /// </summary>
    private void OnAreaPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _area.CapturePointer(e.Pointer);
        CancelDirectManipulations();
        _hovering = true;
        var point = e.GetCurrentPoint(_area);
        ShowHover(point.Position.Y);
    }

    private void OnAreaPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_area);
        // Photos HandleEnteredExitedTransition：捕获后仍按坐标判断是否真正悬停。
        _hovering = point.Position.X >= 0 && point.Position.X < _area.ActualWidth
            && point.Position.Y >= 0 && point.Position.Y < _areaHeight;
        if (_hovering || _dragging) ShowHover(point.Position.Y);
        else CloseHover();
        if (_dragging)
        {
            Seek(point.Position.Y);
        }
    }

    private void ShowHover(double y)
    {
        if (_items.Count == 0 || _areaHeight < 1) return;
        int position = (int)Math.Clamp(y, 0, Math.Max(0, _areaHeight - 1));
        // Photos PositionToLabels 使用该像素对应的首个条目；跳转索引另按上下半区选首/末条目。
        int index = position == (int)_areaHeight - 1 ? _items.Count - 1
            : Math.Clamp(IndexesFromPosition(position).Start, 0, _items.Count - 1);
        _secondaryText.Text = SecondaryLabel(_items[index]);
        _secondaryText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // 原版 OnSecondaryLabelChanged：HorizontalOffset = -文本宽 + 7
        _popup.HorizontalOffset = 0.0 - _secondaryText.DesiredSize.Width + PopupStaticHorizontalOffset;
        // Popup 的垂直偏移由 PointerPositionAnimation 跟随指针；本实现按指针位置直接给值
        _popup.VerticalOffset = AreaTop + position - (int)(MarkerHeight / 2) + PopupVerticalOffset;
        _popup.XamlRoot = XamlRoot;
        _secondaryPopup.RequestedTheme = ActualTheme;
        _popup.IsOpen = true;

        _hoveringMarker.Translation = new Vector3(0, (float)HoverTop(y), 0);
        _hoveringMarker.Visibility = Visibility.Visible;
    }

    private double HoverTop(double y)
        => Math.Clamp(y - MarkerHeight / 2, 0, Math.Max(0, _areaHeight - MarkerHeight));

    /// <summary>对应原版 TimelineArea_PointerExited：只改状态，收起由 Hide 统一负责。</summary>
    private void OnAreaPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hovering = false;
        if (!_dragging) CloseHover();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>原版 IsHoveringOrDragging 同时控制气泡和预览标记，不改变气泡内部内容的可见性。</summary>
    private void CloseHover()
    {
        _popup.IsOpen = false;
        _hoveringMarker.Visibility = Visibility.Collapsed;
    }

    private void OnAreaPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_area);
        if (!point.Properties.IsLeftButtonPressed) return;
        _animateMarkerOnScroll = false;
        _dragging = true;
        _area.CapturePointer(e.Pointer);
        Show();
        _hideTimer.Stop();

        // Pressed 状态：悬停标记变强调色，锚定标记隐藏
        _hoveringMarker.Background = Brush("AccentFillColorDefaultBrush", Color.FromArgb(0xFF, 0x00, 0x67, 0xC0));
        _anchoredMarker.Visibility = Visibility.Collapsed;
        _hoveringMarker.Visibility = Visibility.Visible;

        Seek(point.Position.Y);
        e.Handled = true;
    }

    private void OnAreaPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        EndDrag();
        _area.ReleasePointerCapture(e.Pointer);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _hoveringMarker.Background = Brush("SystemFillColorSolidNeutralBrush", Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));
        _anchoredMarker.Visibility = Visibility.Visible;
        _animateMarkerOnScroll = true;
        if (!_hovering) CloseHover();
        UpdateAnchoredMarker(animate: false);
    }

    private void Seek(double y)
    {
        if (_scroller is null || _panel is null || _items.Count == 0 || _areaHeight < 1) return;
        int position = (int)Math.Clamp(y, 0, Math.Max(0, _areaHeight - 1));
        _markerStoryboard.Stop();
        _markerTransform.Y = HoverTop(y);
        ShowHover(y);
        int index = IndexFromPosition(position);
        _scroller.ChangeView(null, Math.Clamp(_panel.TopOfIndex(index), 0, _scroller.ScrollableHeight), null, true);
    }

    #endregion

    #region 显隐

    public void Show() => AnimateOpacity(1, 30);

    public void ShowWithTimer()
    {
        Show();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void Hide()
    {
        if (_hovering || _dragging || FocusState == FocusState.Keyboard) return;
        AnimateOpacity(MinOpacity, 200);
    }

    /// <summary>
    /// 淡入 30ms、淡出 200ms（Spline 0,0,0,1，对应 TimelineScrollbarStyles 的 Enter/Exit）。
    /// 淡出终点用 MinOpacity 而不是 0：Opacity 真正归零后 WinUI 合成层会停止绘制该元素，
    /// 之后即使再设回 1 也画不出来（等于时间线永久消失）。
    /// </summary>
    private void AnimateOpacity(double to, int ms)
    {
        if (Math.Abs(Opacity - to) < 0.005) return;
        var sb = new Storyboard();
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)),
            KeySpline = Spline(0, 0, 0, 1),
            Value = to
        });
        Storyboard.SetTarget(anim, this);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }

    #endregion
}
