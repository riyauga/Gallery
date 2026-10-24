using System.Text.Json;
using System.Text.Json.Nodes;
using Gallery.Services;
using Gallery.Controls.Tags;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Gallery.Controls.Review;

public sealed partial class ReviewPage
{
    private sealed record PaletteTag(string Category, string Name, string Display, string EntityKey = "");
    private sealed record PaletteHit(string Category, TagSearchSuggestion Suggestion)
    {
        public override string ToString() => $"{IngestReview.Titles[Array.IndexOf(IngestReview.Categories, Category)]} · {Suggestion.SearchDisplay}";
    }
    private sealed record TagChange(int Id, string[] Before, string[] After, string RatingSource);
    private sealed record TagStroke(PaletteTag Tag, List<TagChange> Changes);
    private const string PaletteDragFormat = "Gallery.ReviewPaletteTag";
    private const double DragArmDistance = 24;
    private const double DragCancelDistance = 120;
    private readonly StackPanel _workbench = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TagWrapPanel _paletteWrap = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Button _undoPaint = new() { Content = new FontIcon { Glyph = "\uE7A7", FontSize = 16 }, MinWidth = 0, Padding = new Thickness(0) };
    private readonly Button _redoPaint = new() { Content = new FontIcon { Glyph = "\uE7A6", FontSize = 16 }, MinWidth = 0, Padding = new Thickness(0) };
    private readonly SearchInputBox _paletteSearch = new() { EnableSuggestionTabNavigation = true, MinWidth = 0 };
    private readonly StackPanel _railPanel = new()
    {
        Spacing = 2, Width = 28, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0),
    };
    private readonly Dictionary<string, Button> _railButtons = [];
    private string? _railCategory;
    private Border _paletteContainer = null!;
    private readonly Border _paletteTagContainer = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), BorderThickness = new Thickness(1) };
    private readonly List<PaletteTag> _palette = [];
    private readonly Dictionary<int, (string Payload, string Rating, Dictionary<string, List<string>> Tags)> _workbenchTagCache = [];
    private readonly List<TagStroke> _paintUndo = [], _paintRedo = [];
    private PaletteTag? _paintTag;
    private PaletteTag? _observedTag;
    private bool _paintGesture, _paletteDragging;
    private int _paletteQueryVersion;
    private Task _paintTask = Task.CompletedTask;
    private Storyboard? _glowStoryboard;

    // 拖拽回收层：全屏遮罩 + 原位虚线空槽 + 底部黑洞。只在拖拽时接管命中。
    private readonly Grid _paletteDragLayer = new()
    {
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        Margin = new Thickness(-28, -24, -28, -22),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };
    private readonly Canvas _paletteDragCanvas = new() { IsHitTestVisible = false };
    private readonly Rectangle _dragMask = new()
    {
        Fill = new SolidColorBrush(Microsoft.UI.Colors.Black), Opacity = .3, IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    private readonly Rectangle _dragSlot = new()
    {
        RadiusX = 7, RadiusY = 7, StrokeThickness = 1.5, IsHitTestVisible = false,
        StrokeDashArray = new DoubleCollection { 4, 3 }, Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _dragGhostText = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _dragGhost = new()
    {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(1),
        RenderTransformOrigin = new Point(.5, .5), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    private readonly Grid _dragHole = new() { Width = 72, Height = 72, IsHitTestVisible = false };
    private readonly Ellipse _holeOuter = new() { Width = 56, Height = 56, StrokeThickness = 1.5 };
    private readonly Ellipse _holeInner = new() { Width = 34, Height = 34, StrokeThickness = 1 };
    private readonly Ellipse _holeCore = new() { Width = 20, Height = 20, Fill = new SolidColorBrush(Microsoft.UI.Colors.Black) };
    private readonly Grid _dragHoleHost = new()
    {
        Width = 72, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24),
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    private Storyboard? _holeSpin, _dragAnim;
    private PaletteTag? _dragTag;
    private Point _dragOriginPage, _dragPressPoint;
    private bool _dragArmed, _dragFinishing;

    private UIElement BuildWorkbench()
    {
        // 撤销/重做留在容器外右上方，无背景纯图标。
        var header = new Grid();
        var history = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        StyleHistoryButton(_undoPaint, "撤销 (Ctrl+Z)");
        StyleHistoryButton(_redoPaint, "重做 (Ctrl+Y)");
        _undoPaint.Click += (_, _) => StartUndoRedo(false);
        _redoPaint.Click += (_, _) => StartUndoRedo(true);
        history.Children.Add(_undoPaint);
        history.Children.Add(_redoPaint);
        header.Children.Add(history);
        _workbench.Children.Add(header);

        _paletteContainer = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(0, 10, 16, 16), MinHeight = 320,
            Background = ResourceBrush("ControlFillColorSecondaryBrush"),
            BorderBrush = ResourceBrush("ControlStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        };
        // 左侧全部空隙归到同一列，图标居中后两侧净距各 11 DIP。
        var main = new Grid { RowSpacing = 12 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _paletteSearch.PlaceholderText = "搜索所有类型标签…";
        AutomationProperties.SetName(_paletteSearch, "搜索或添加调色板标签");
        _paletteSearch.TextChanged += async (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var version = ++_paletteQueryVersion;
            var query = _paletteSearch.Text.Trim();
            var category = _railCategory;
            if (query.Length == 0) { _paletteSearch.ItemsSource = null; return; }
            await Task.Delay(160);
            if (version != _paletteQueryVersion || !_active || !_batchMode) return;
            try
            {
                var hits = await Task.Run(() =>
                {
                    lock (AppService.DbLock)
                        return AppService.Store is { } store
                            ? (category is null ? IngestReview.Categories.Where(item => item != "rating") : [category])
                                .SelectMany(item => TagSearchService.Search(store, item, query)
                                    .Select(hit => new PaletteHit(item, hit))).ToList() : [];
                });
                if (version == _paletteQueryVersion && _active && _batchMode) _paletteSearch.ItemsSource = hits;
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
            { _status.Text = "标签搜索失败：" + ex.Message; }
        };
        _paletteSearch.QuerySubmitted += (_, args) =>
        {
            if (_busy) return;
            var selectedHit = args.ChosenSuggestion as PaletteHit;
            var hit = selectedHit?.Suggestion;
            var name = hit?.Name ?? args.QueryText.Trim();
            if (name.Length == 0) return;
            var category = selectedHit?.Category ?? _railCategory;
            if (category is null)
            {
                _status.Text = "请先选择左侧标签类型，再创建新标签；已有标签请从搜索候选中选择。";
                return;
            }
            if (category == "rating") return;
            if (!_palette.Any(tag => tag.Category == category && string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                var display = hit?.Display;
                if (string.IsNullOrWhiteSpace(display) && AppService.Store is { } store)
                    display = store.ResolveTagLabels([(category, name)]).GetValueOrDefault((category, name), name);
                _palette.Add(new(category, name, string.IsNullOrWhiteSpace(display) ? name : display, hit?.EntityKey ?? ""));
                PersistPalette();
                RebuildPalette();
            }
            ++_paletteQueryVersion;
            _paletteSearch.Text = "";
            _paletteSearch.ItemsSource = null;
        };
        Grid.SetColumn(_paletteSearch, 1);
        main.Children.Add(_paletteSearch);

        // 左侧图标直接放在外容器内；搜索与标签区共用右列，保持左边缘对齐。
        BuildCategoryRail();
        Grid.SetRow(_railPanel, 1);
        main.Children.Add(_railPanel);
        _paletteTagContainer.Child = _paletteWrap;
        Grid.SetColumn(_paletteTagContainer, 1);
        Grid.SetRow(_paletteTagContainer, 1);
        main.Children.Add(_paletteTagContainer);
        _paletteContainer.Child = main;
        _workbench.Children.Add(_paletteContainer);
        LoadPalette();
        RebuildPalette();
        return _workbench;
    }

    private static void StyleHistoryButton(Button button, string name)
    {
        // 用系统 Fluent 图标字体画撤销/重做，静止时无底无框，只在悬停时给一点反馈。
        button.Width = button.Height = 28;
        button.CornerRadius = new CornerRadius(4);
        button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.BorderThickness = new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Resources["ButtonBackgroundPressed"] = ResourceBrush("ControlFillColorTertiaryBrush");
        button.Resources["ButtonBackgroundPointerOver"] = ResourceBrush("ControlFillColorSecondaryBrush");
        button.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }

    private void BuildCategoryRail()
    {
        // 图标常驻，只表示类型；颜色与标签底色共用一套 TagColors，一眼对上分类。
        string[] glyphs = ["\uE91B", "\uE77B", "\uE76D", "\uF0E2", "R", "\uE710"];
        foreach (var ((category, title), glyph) in IngestReview.Categories.Zip(IngestReview.Titles).Zip(glyphs))
        {
            var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
            if (category == "rating")
            {
                icon.FontFamily = new FontFamily("Segoe UI Variable");
                icon.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                icon.FontSize = 18;
            }
            var button = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
                Content = icon,
            };
            button.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonBackgroundPressed"] = ResourceBrush("ControlFillColorTertiaryBrush");
            button.Resources["ButtonBackgroundPointerOver"] = ResourceBrush("ControlFillColorSecondaryBrush");
            AutomationProperties.SetName(button, title);
            ToolTipService.SetToolTip(button, title);
            var captured = category;
            button.Click += (_, _) => ToggleRailCategory(captured);
            _railButtons[category] = button;
            _railPanel.Children.Add(button);
        }
        UpdateRailState();
    }

    private void ToggleRailCategory(string category)
    {
        _railCategory = _railCategory == category ? null : category;
        if (_paintTag is { } active && _railCategory is not null && active.Category != _railCategory) ExitPaintMode();
        ++_paletteQueryVersion;
        _paletteSearch.Text = "";
        _paletteSearch.ItemsSource = null;
        _paletteSearch.PlaceholderText = _railCategory switch
        {
            null => "搜索所有类型标签…",
            "rating" => "分级无需搜索",
            _ => $"搜索或输入{IngestReview.Titles[Array.IndexOf(IngestReview.Categories, _railCategory)]}…",
        };
        RebuildPalette();
        UpdateWorkbenchState();
    }

    private static string PaletteKey(PaletteTag tag) => JsonSerializer.Serialize(new[] { tag.Category, tag.Name, tag.EntityKey });

    private void UpdateRailState()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        _paletteTagContainer.Background = new SolidColorBrush(TagColors.FromHex(dark ? "#242424" : "#EAEAEA"));
        _paletteTagContainer.BorderBrush = ResourceBrush("ControlStrokeColorDefaultBrush");
        var idle = ResourceBrush("TextFillColorSecondaryBrush");
        foreach (var (category, button) in _railButtons)
        {
            var selected = _railCategory == category;
            if (button.Content is not FontIcon icon) continue;
            icon.Foreground = selected
                ? new SolidColorBrush(TagColors.FromHex(TagColors.Get(category, dark).Border))
                : idle;
            icon.Opacity = selected ? 1 : .6;
        }
    }

    private void RebuildPalette()
    {
        _glowStoryboard?.Stop();
        _glowStoryboard = null;
        _paletteWrap.Children.Clear();
        var showAll = _railCategory is null;
        foreach (var (category, _) in IngestReview.Categories.Zip(IngestReview.Titles))
        {
            if (!showAll && category != _railCategory) continue;
            if (category == "rating")
            {
                foreach (var level in new[] { "g", "s", "q", "e" })
                    AddPaletteButton(new PaletteTag("rating", level, RatingText.Level(level)));
                continue;
            }
            foreach (var tag in _palette.Where(tag => tag.Category == category).ToArray())
                AddPaletteButton(tag);
        }
        UpdateRailState();
    }

    private void AddPaletteButton(PaletteTag tag)
    {
        var title = IngestReview.Titles[Array.IndexOf(IngestReview.Categories, tag.Category)];
        var button = new Button
        {
            Content = new TagChip(tag.Category, tag.Display), Padding = new Thickness(0),
            MinWidth = 0, MinHeight = 30, CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, title + "：" + tag.Display + (_paintTag == tag ? "，正在喷涂" : ""));
        ToolTipService.SetToolTip(button, tag.Display);
        ConnectPaletteButton(button, tag);
        var host = new Grid { Margin = new Thickness(2) };
        host.Children.Add(button);
        if (_paintTag == tag)
        {
            // 只旋转描边的渐变光带，标签和圆角轮廓不动；分类色保持低亮度。
            var color = TagColors.FromHex(TagColors.Get(tag.Category, ActualTheme == ElementTheme.Dark).Fg);
            Windows.UI.Color Tint(byte alpha) => Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
            var rotate = new RotateTransform { CenterX = .5, CenterY = .5 };
            var light = new LinearGradientBrush
            {
                StartPoint = new Point(0, .5), EndPoint = new Point(1, .5), RelativeTransform = rotate,
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Tint(0) },
                    new GradientStop { Offset = .62, Color = Tint(0) },
                    new GradientStop { Offset = .78, Color = Tint(40) },
                    new GradientStop { Offset = .9, Color = Tint(155) },
                    new GradientStop { Offset = 1, Color = Tint(0) },
                },
            };
            host.Children.Add(new Rectangle
            {
                RadiusX = 9, RadiusY = 9, Margin = new Thickness(-3), IsHitTestVisible = false,
                StrokeThickness = 1, Stroke = new SolidColorBrush(Tint(70)),
            });
            host.Children.Add(new Rectangle
            {
                RadiusX = 9, RadiusY = 9, Margin = new Thickness(-3), IsHitTestVisible = false,
                StrokeThickness = 4, Stroke = light, Opacity = .18,
            });
            var glow = new Rectangle
            {
                RadiusX = 9, RadiusY = 9, Margin = new Thickness(-3), IsHitTestVisible = false,
                StrokeThickness = 1.3, Stroke = light,
            };
            host.Children.Add(glow);
            var spin = new DoubleAnimation
            {
                From = 0, To = 360, Duration = TimeSpan.FromSeconds(4), RepeatBehavior = RepeatBehavior.Forever,
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(spin, rotate);
            Storyboard.SetTargetProperty(spin, "Angle");
            var story = new Storyboard();
            story.Children.Add(spin);
            _glowStoryboard = story;
            glow.Loaded += (_, _) =>
            {
                if (ReferenceEquals(_glowStoryboard, story) && _active && _batchMode && _paintTag == tag
                    && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                    story.Begin();
            };
            glow.Unloaded += (_, _) => story.Stop();
        }
        _paletteWrap.Children.Add(host);
    }

    private void ConnectPaletteButton(Button button, PaletteTag tag)
    {
        Point? pressOrigin = null;
        uint? pointerId = null;
        var suppressClick = false;
        button.Click += (_, _) => { if (!suppressClick) PaletteClicked(tag, false); };
        button.RightTapped += (_, args) =>
        {
            args.Handled = true;
            if (_batchSelected.Count > 0) PaletteClicked(tag, true);
        };
        button.PreviewKeyDown += (_, args) =>
        {
            if (!_paletteDragging && args.Key is VirtualKey.Enter or VirtualKey.Space) suppressClick = false;
            if (args.Key == VirtualKey.Delete && tag.Category != "rating" && !_busy && !_paintGesture && !_paletteDragging)
            {
                RemovePaletteTag(tag);
                args.Handled = true;
            }
        };
        if (tag.Category == "rating") return;

        button.DragStarting += (_, args) =>
        {
            if (_busy || _paintGesture) { args.Cancel = true; return; }
            args.Data.SetData(PaletteDragFormat, PaletteKey(tag));
            args.AllowedOperations = DataPackageOperation.Move;
            BeginPaletteDrag(tag, button);
        };
        button.DropCompleted += (_, _) => EndPaletteDrag();
        // Button 会处理并捕获指针；监听已处理事件，超过阈值后显式启动原生拖放。
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (_busy || _paintGesture || _paletteDragging || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
            var point = args.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;
            pressOrigin = point.Position;
            _dragPressPoint = point.Position;
            pointerId = args.Pointer.PointerId;
            suppressClick = false;
        }), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(async (_, args) =>
        {
            if (pressOrigin is not { } origin || pointerId != args.Pointer.PointerId || _paletteDragging || _busy || _paintGesture) return;
            var point = args.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) { pressOrigin = null; pointerId = null; return; }
            if (Math.Abs(point.Position.X - origin.X) < 6 && Math.Abs(point.Position.Y - origin.Y) < 6) return;
            var dragPoint = args.GetCurrentPoint(button);
            pressOrigin = null;
            pointerId = null;
            suppressClick = true;
            _paletteDragging = true;
            args.Handled = true;
            try
            {
                // 解除按钮按下状态，拖动完成或取消后都不能补发标签点击。
                button.ReleasePointerCaptures();
                await button.StartDragAsync(dragPoint);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                AppService.DebugLog("review palette drag: " + ex);
                _status.Text = "标签拖动启动失败：" + ex.Message;
                EndPaletteDrag();
            }
            finally { _paletteDragging = false; }
        }), true);
        PointerEventHandler clearPress = (_, _) => { pressOrigin = null; pointerId = null; };
        button.AddHandler(UIElement.PointerReleasedEvent, clearPress, true);
        button.AddHandler(UIElement.PointerCanceledEvent, clearPress, true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, clearPress, true);
    }

    private void BuildPaletteDragLayer()
    {
        _dragSlot.Stroke = ResourceBrush("AccentFillColorDefaultBrush");
        _holeOuter.Stroke = ResourceBrush("AccentFillColorDefaultBrush");
        _holeInner.Stroke = ResourceBrush("AccentFillColorDefaultBrush");
        _holeOuter.StrokeDashArray = new DoubleCollection { 3, 5 };
        _holeInner.StrokeDashArray = new DoubleCollection { 2, 4 };
        var outerRotate = new RotateTransform { CenterX = 28, CenterY = 28 };
        var innerRotate = new RotateTransform { CenterX = 17, CenterY = 17 };
        _holeOuter.RenderTransform = outerRotate;
        _holeInner.RenderTransform = innerRotate;
        _dragHole.Children.Add(_holeOuter);
        _dragHole.Children.Add(_holeInner);
        _dragHole.Children.Add(_holeCore);
        _dragHoleHost.Children.Add(_dragHole);
        _holeCore.Opacity = .9;

        _paletteDragCanvas.Children.Add(_dragSlot);
        _dragGhost.Child = _dragGhostText;
        _paletteDragCanvas.Children.Add(_dragGhost);
        _paletteDragLayer.Children.Add(_dragMask);
        _paletteDragLayer.Children.Add(_paletteDragCanvas);
        _paletteDragLayer.Children.Add(_dragHoleHost);
        _paletteDragLayer.AllowDrop = true;
        _paletteDragLayer.DragOver += OnPaletteDragOver;
        _paletteDragLayer.Drop += OnPaletteDrop;
        Children.Add(_paletteDragLayer);

        var spinOuter = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(5), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(spinOuter, outerRotate);
        Storyboard.SetTargetProperty(spinOuter, "Angle");
        var spinInner = new DoubleAnimation { From = 0, To = -360, Duration = TimeSpan.FromSeconds(2.4), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(spinInner, innerRotate);
        Storyboard.SetTargetProperty(spinInner, "Angle");
        _holeSpin = new Storyboard();
        _holeSpin.Children.Add(spinOuter);
        _holeSpin.Children.Add(spinInner);
    }

    private void BeginPaletteDrag(PaletteTag tag, FrameworkElement source)
    {
        _dragAnim?.Stop();
        _dragAnim = null;
        _dragFinishing = false;
        _dragTag = tag;
        _dragOriginPage = source.TransformToVisual(this).TransformPoint(new Point(0, 0));
        _dragSlot.Width = Math.Max(8, source.ActualWidth);
        _dragSlot.Height = Math.Max(8, source.ActualHeight);
        _dragGhost.Visibility = Visibility.Collapsed;
        _paletteDragLayer.Visibility = Visibility.Visible;
        _paletteDragLayer.IsHitTestVisible = true;
        SetDragArmed(false);
    }

    private void EndPaletteDrag()
    {
        if (_dragFinishing) return;
        HideDragLayer();
    }

    private void CancelPaletteDrag()
    {
        _dragAnim?.Stop();
        _dragAnim = null;
        _dragFinishing = false;
        _paletteDragging = false;
        HideDragLayer();
    }

    private void HideDragLayer()
    {
        _dragTag = null;
        _paletteDragLayer.Visibility = Visibility.Collapsed;
        _paletteDragLayer.IsHitTestVisible = false;
        SetDragArmed(false);
        _dragGhost.Visibility = Visibility.Collapsed;
        _dragGhost.RenderTransform = null;
    }

    private void SetDragArmed(bool armed)
    {
        if (_dragArmed == armed) return;
        _dragArmed = armed;
        _dragMask.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;
        _dragSlot.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;
        _dragHoleHost.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;
        _carousel.SetDragMask(armed);
        if (armed)
        {
            _holeSpin?.Begin();
        }
        else _holeSpin?.Stop();
    }

    private Point DragHoleCenter()
    {
        if (_dragHole.ActualWidth <= 0) return new Point(ActualWidth / 2, ActualHeight - 54);
        try
        {
            return _dragHole.TransformToVisual(this)
                .TransformPoint(new Point(_dragHole.ActualWidth / 2, _dragHole.ActualHeight / 2));
        }
        catch (ArgumentException) { return new Point(ActualWidth / 2, ActualHeight - 54); }
    }

    private void PositionDragSlot()
    {
        // 遮罩层带负外边距；换算成层内坐标后再画原位空槽。
        var offset = _paletteDragLayer.TransformToVisual(this).TransformPoint(new Point(0, 0));
        Canvas.SetLeft(_dragSlot, _dragOriginPage.X - offset.X);
        Canvas.SetTop(_dragSlot, _dragOriginPage.Y - offset.Y);
    }

    private void OnPaletteDragOver(object sender, DragEventArgs args)
    {
        if (_dragTag is null || !args.DataView.Contains(PaletteDragFormat)) return;
        var point = args.GetPosition(this);
        SetDragArmed(Distance(point, _dragPressPoint) > DragArmDistance);
        if (_dragArmed) PositionDragSlot();
        args.AcceptedOperation = DataPackageOperation.Move;
        args.Handled = true;
    }

    private void OnPaletteDrop(object sender, DragEventArgs args)
    {
        if (_dragTag is { } tag)
        {
            // 落点仍在原标签附近就当作取消，避免手抖把标签吸走。
            var origin = new Point(_dragOriginPage.X + _dragSlot.Width / 2, _dragOriginPage.Y + _dragSlot.Height / 2);
            if (_dragArmed && Distance(args.GetPosition(this), origin) > DragCancelDistance)
            {
                RemovePaletteTag(tag);
                StartDragSuction(tag, args.GetPosition(this));
            }
            else EndPaletteDrag();
        }
        else EndPaletteDrag();
        args.AcceptedOperation = DataPackageOperation.Move;
        args.Handled = true;
    }

    /// <summary>松手落入黑洞：标签形色块从松手点快速缩小飞入洞心，再收起遮罩。</summary>
    private void StartDragSuction(PaletteTag tag, Point dropPoint)
    {
        var offset = _paletteDragLayer.TransformToVisual(this).TransformPoint(new Point(0, 0));
        var start = new Point(dropPoint.X - offset.X, dropPoint.Y - offset.Y);
        var hole = DragHoleCenter();
        var target = new Point(hole.X - offset.X, hole.Y - offset.Y);
        var colors = TagColors.Get(tag.Category, ActualTheme == ElementTheme.Dark);
        _dragGhostText.Text = tag.Display;
        _dragGhostText.Foreground = new SolidColorBrush(TagColors.FromHex(colors.Fg));
        _dragGhost.Background = new SolidColorBrush(TagColors.FromHex(colors.Bg));
        _dragGhost.BorderBrush = new SolidColorBrush(TagColors.FromHex(colors.Border));
        _dragGhost.Width = Math.Max(28, _dragSlot.Width);
        _dragGhost.Height = Math.Max(26, _dragSlot.Height);
        Canvas.SetLeft(_dragGhost, start.X - _dragGhost.Width / 2);
        Canvas.SetTop(_dragGhost, start.Y - _dragGhost.Height / 2);
        var transform = new CompositeTransform();
        _dragGhost.RenderTransform = transform;
        _dragGhost.Visibility = Visibility.Visible;
        _dragFinishing = true;
        _dragTag = null;
        var story = new Storyboard();
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        AddAnimation(story, transform, "TranslateX", target.X - start.X, ease);
        AddAnimation(story, transform, "TranslateY", target.Y - start.Y, ease);
        AddAnimation(story, transform, "ScaleX", .12, ease);
        AddAnimation(story, transform, "ScaleY", .12, ease);
        AddAnimation(story, _dragGhost, "Opacity", 0, ease);
        story.Completed += (_, _) =>
        {
            _dragFinishing = false;
            HideDragLayer();
        };
        _dragAnim = story;
        story.Begin();
    }

    private static void AddAnimation(Storyboard story, DependencyObject target, string property, double to, EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = ease };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private void RemovePaletteTag(PaletteTag tag)
    {
        if (_busy || _paintGesture) return;
        if (_paintTag == tag) ExitPaintMode();
        if (_observedTag == tag) _observedTag = null;
        _palette.Remove(tag);
        PersistPalette(); RebuildPalette(); UpdateWorkbenchState();
    }

    private void PaletteClicked(PaletteTag tag, bool remove)
    {
        if (_busy || _paintGesture || _paletteDragging || !_batchMode || AppService.IsShuttingDown) return;
        _observedTag = tag;
        if (_batchSelected.Count > 0)
        {
            ExitPaintMode();
            // 混合状态先补齐；全都有时再次左击删除；右键始终删除。
            _paintTask = ApplyTagStrokeAsync(tag, _batchSelected.ToArray(), remove, toggleSelection: !remove);
        }
        else
        {
            if (_paintTag == tag) { ExitPaintMode(); return; }
            _paintTag = tag;
            RebuildPalette(); UpdateWorkbenchState();
        }
    }

    private bool HasTag(StoreService.PendingIngestRow row, PaletteTag tag)
    {
        if (tag.Category == "rating") return ArchiveService.NormalizeRating(row.Rating) == tag.Name;
        if (!_workbenchTagCache.TryGetValue(row.Id, out var cached) || !ReferenceEquals(cached.Payload, row.Payload) || cached.Rating != row.Rating)
            _workbenchTagCache[row.Id] = cached = (row.Payload, row.Rating, IngestReview.Tags(row.Payload, row.Rating));
        return cached.Tags[tag.Category].Contains(tag.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void ExitPaintMode()
    {
        _paintTag = null;
        _paintGesture = false;
        _carousel.CancelTagStroke();
        RebuildPalette(); UpdateButtons();
    }

    private void UpdateWorkbenchState()
    {
        if (!_batchMode) { _carousel.SetTagTool(null, "", []); return; }
        _undoPaint.IsEnabled = !_busy && !_paintGesture && _paintUndo.Count > 0;
        _redoPaint.IsEnabled = !_busy && !_paintGesture && _paintRedo.Count > 0;
        _paletteContainer.IsHitTestVisible = !_busy && !_paintGesture;
        _paletteWrap.IsHitTestVisible = !_busy && !_paintGesture;
        _paletteSearch.IsEnabled = !_busy && !_paintGesture && _railCategory != "rating";
        _batchToggle.IsEnabled &= !_paintGesture;
        _selectAll.IsEnabled &= !_paintGesture;
        _cardSize.IsEnabled &= !_paintGesture;
        _archive.IsEnabled &= !_paintGesture;
        _ignore.IsEnabled &= !_paintGesture;
        var observed = _paintTag ?? _observedTag;
        var matches = observed is null ? [] : _rows.Where(row => HasTag(row, observed)).Select(row => row.Id).ToArray();
        var colors = observed is null ? default : TagColors.Get(observed.Category, ActualTheme == ElementTheme.Dark);
        _carousel.SetTagTool(_batchMode && _paintTag is not null ? PaletteKey(_paintTag) : null, observed?.Display ?? "", matches,
            colors == default ? "" : colors.Bg, colors == default ? "" : colors.Border, colors == default ? "" : colors.Fg);
    }

    private void ConnectWorkbenchInput()
    {
        _carousel.TagStrokeRequested += (ids, remove, phase) =>
        {
            if (!_batchMode || _busy || !_active || AppService.IsShuttingDown) return;
            _paintGesture = phase == "start";
            if (phase == "end" && _paintTag is { } tag) _paintTask = ApplyTagStrokeAsync(tag, ids, remove);
            UpdateButtons();
        };
        _carousel.ExitTagToolRequested += ExitPaintMode;
        _carousel.TagUndoRequested += StartUndoRedo;
        PreviewKeyDown += (_, args) =>
        {
            if (!_batchMode || _paletteDragging || args.Handled || HasTagInputFocus()) return;
            if (args.Key == VirtualKey.Escape)
            {
                if (_paintTag is not null) ExitPaintMode();
                else ClearBatchSelection();
                args.Handled = true;
            }
            else if (args.Key is VirtualKey.Z or VirtualKey.Y && InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
            {
                StartUndoRedo(args.Key == VirtualKey.Y || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down));
                args.Handled = true;
            }
        };
    }

    private void StartUndoRedo(bool redo)
    {
        if (_busy || _paintGesture || !_batchMode || !_active || AppService.IsShuttingDown) return;
        var stack = redo ? _paintRedo : _paintUndo;
        if (stack.Count > 0) _paintTask = ApplyTagStrokeAsync(stack[^1].Tag, [], false, stack[^1], redo);
    }

    private async Task ApplyTagStrokeAsync(PaletteTag tag, IReadOnlyCollection<int> ids, bool remove, TagStroke? history = null, bool redo = false, bool toggleSelection = false)
    {
        if (_busy || AppService.Store is not { } store || AppService.IsShuttingDown) return;
        SetBusy(true); UpdateButtons();
        var committed = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var changes = new List<TagChange>();
                var updated = new List<StoreService.PendingIngestRow>();
                using var tx = store.Transaction();
                var previousChanges = history?.Changes.ToDictionary(change => change.Id);
                var targets = (history is null ? ids.Distinct() : history.Changes.Select(change => change.Id)).ToArray();
                if (toggleSelection)
                {
                    var current = targets.Select(store.IngestRow).Where(row => row is { State: "pending" }).ToArray();
                    remove = current.Length > 0 && current.All(row => IngestReview.Tags(row!.Payload, row.Rating)[tag.Category].Contains(tag.Name, StringComparer.OrdinalIgnoreCase));
                }
                foreach (var id in targets)
                {
                    if (store.IngestRow(id) is not { State: "pending" } row) continue;
                    var tags = IngestReview.Tags(row.Payload, row.Rating);
                    var before = tags[tag.Category].ToArray();
                    var after = before.ToList();
                    var previous = previousChanges?.GetValueOrDefault(id);
                    if (previous is not null)
                    {
                        var expected = redo ? previous.Before : previous.After;
                        if (!before.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase)) continue;
                        after = (redo ? previous.After : previous.Before).ToList();
                    }
                    else if (remove) after.RemoveAll(name => string.Equals(name, tag.Name, StringComparison.OrdinalIgnoreCase));
                    else
                    {
                        if (tag.Category is "work" or "rating") after.Clear();
                        if (tag.Category == "kind" && tag.Name is "单人" or "多人") after.RemoveAll(name => name is "单人" or "多人");
                        if (!after.Contains(tag.Name, StringComparer.OrdinalIgnoreCase)) after.Add(tag.Name);
                    }
                    if (before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase)) continue;
                    tags[tag.Category] = after;
                    var rating = tags["rating"].FirstOrDefault() ?? "";
                    var oldPayload = JsonNode.Parse(row.Payload)!.AsObject();
                    var source = oldPayload["rating_source"]?.GetValue<string>() ?? "";
                    var payload = JsonNode.Parse(IngestReview.UpdatePayload(row.Payload, tags, rating))!.AsObject();
                    if (tag.Category != "rating" || history is not null && !redo)
                    {
                        var keepSource = tag.Category != "rating" ? source : previous!.RatingSource;
                        if (keepSource.Length > 0) payload["rating_source"] = keepSource;
                        else payload.Remove("rating_source");
                    }
                    store.UpdateIngestReview(id, payload.ToJsonString(), rating);
                    changes.Add(new(id, before, after.ToArray(), source));
                    if (store.IngestRow(id) is { } latest) updated.Add(latest with { ResolvedPath = row.ResolvedPath });
                }
                tx.Success = true;
                return (changes, updated);
            });
            committed = true;
            var positions = _rows.Select((row, index) => (row.Id, index)).ToDictionary(item => item.Id, item => item.index);
            foreach (var row in result.updated)
                if (positions.TryGetValue(row.Id, out var index)) _rows[index] = row;
            if (history is not null)
            {
                var from = redo ? _paintRedo : _paintUndo;
                var to = redo ? _paintUndo : _paintRedo;
                from.RemoveAt(from.Count - 1);
                var applied = result.changes.Select(change => change.Id).ToHashSet();
                var kept = history.Changes.Where(change => applied.Contains(change.Id)).ToList();
                if (kept.Count > 0) to.Add(history with { Changes = kept });
                _status.Text = $"{(redo ? "已重做" : "已撤销")} {kept.Count} 张图片";
            }
            else if (result.changes.Count > 0)
            {
                _paintUndo.Add(new(tag, result.changes));
                if (_paintUndo.Count > 100) _paintUndo.RemoveAt(0);
                _paintRedo.Clear();
                _status.Text = $"已{(remove ? "移除" : "添加")}“{tag.Display}”：{result.changes.Count} 张图片";
            }
            UpdateWorkbenchState();
            _carousel.UpdateTagRows(_rows, tag.Category == "rating");
            RefreshDestination();
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { _status.Text = (committed ? "标签已保存，但界面刷新失败：" : "标签操作失败，整笔未保存：") + ex.Message; }
        finally { SetBusy(false); UpdateButtons(); OpenDeferredNotification(); }
    }

    private void LoadPalette()
    {
        if (!AppService.Settings.Settings.layout_options.TryGetValue("review_tag_palette", out var saved)) return;
        try
        {
            foreach (var tag in saved.Deserialize<List<PaletteTag>>() ?? [])
                if (tag is not null && IngestReview.Categories.Contains(tag.Category) && tag.Category != "rating" && !string.IsNullOrWhiteSpace(tag.Name)
                    && !_palette.Any(item => item.Category == tag.Category && string.Equals(item.Name, tag.Name, StringComparison.OrdinalIgnoreCase)))
                    _palette.Add(tag with { Display = string.IsNullOrWhiteSpace(tag.Display) ? tag.Name : tag.Display });
        }
        catch (JsonException ex) { AppService.DebugLog("review palette: " + ex.Message); }
    }

    private void PersistPalette()
    {
        AppService.Settings.Settings.layout_options["review_tag_palette"] = JsonSerializer.SerializeToElement(_palette);
        try { AppService.Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _status.Text = "调色板本次已保留，但保存设置失败：" + ex.Message; }
    }
}
