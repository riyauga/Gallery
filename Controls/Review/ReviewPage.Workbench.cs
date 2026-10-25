using System.Diagnostics;
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
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System.Numerics;
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
    private sealed record TagChange(int Id, string[] Before, string[] After, string RatingSource, Dictionary<string, List<string>>? BeforeTags = null, string[]? BeforeSkins = null, string[]? AfterSkins = null);
    private sealed record TagStroke(PaletteTag Tag, List<TagChange> Changes);
    /// <summary>「清除」不是真标签：单独放一个分类值，既不进存储也不参与搜索。</summary>
    private const string ClearCategory = "clear";
    private static readonly PaletteTag ClearTag = new(ClearCategory, "清除", "清除");
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
    // 调色板按钮按 PaletteKey 登记，用来在重建后把键盘焦点放回原处。
    private readonly Dictionary<string, Button> _paletteButtons = [];
    // 「清除」是固定入口：外圈跟着类型走，绕行高光固定暗红。
    private const string ClearGlowColor = "#C42B1C";
    private string? _railCategory;
    private Border _paletteContainer = null!;
    private readonly Border _paletteTagContainer = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), BorderThickness = new Thickness(1) };
    private readonly List<PaletteTag> _palette = [];
    private readonly Dictionary<int, (string Payload, string Rating, Dictionary<string, List<string>> Tags)> _workbenchTagCache = [];
    private readonly List<TagStroke> _paintUndo = [], _paintRedo = [];
    private PaletteTag? _paintTag;
    private PaletteTag? _observedTag;
    // 「清除」动画的落点：点标签是标签位置，喷涂是松手位置。
    private Point? _clearFlyTarget;
    // 「清除」在喷涂模式下这一笔扫到的图片；和普通标签一样松手后才整笔写库。
    private readonly List<int> _clearStroke = [];
    // 网页最后一次落笔的位置（已换算成页面坐标），清除飞散动画把它当落点。
    private Point? _lastStrokePoint;
    private bool _paintGesture, _paletteDragging;
    // 拖拽过的按钮松手时不该再补发一次标签点击。
    private bool _suppressPaletteClick;
    private int _paletteQueryVersion;
    private Task _paintTask = Task.CompletedTask;
    // 当前喷涂标签上那道光带的停止句柄；重建调色板时先把它停掉。
    private Action? _stopGlow;

    // 拖拽回收层：全屏遮罩 + 原位虚线空槽 + 底部黑洞。只在拖拽时接管命中。
    private readonly Grid _paletteDragLayer = new()
    {
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        Margin = new Thickness(-28, -24, -28, -22),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };
    private readonly Canvas _paletteDragCanvas = new() { IsHitTestVisible = false };
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
    private Grid _dragHole = new() { Width = 112, Height = 112, IsHitTestVisible = false };
    private Grid? _holeArt, _holeBody;
    private DropShadow? _holeShadow;
    private ScalarKeyFrameAnimation? _holeBlurAnimation, _holeOpacityAnimation;
    private bool _holeHot;
    private CompositeTransform? _holeWobble;
    private RotateTransform? _holeSwirl;
    // 黑洞挂在左栏底部正中：位置在 PositionDragHole 里按卡片容器算，不靠对齐和边距。
    private readonly Grid _dragHoleHost = new()
    {
        Width = 112, Height = 112, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    private Canvas? _dragHoleCanvas;
    private Storyboard? _dragAnim;
    private readonly Stopwatch _dragPullClock = new();
    private Point _dragPullPoint;
    private bool _dragPullOutside;
    private double _dragPullAmount, _dragPullLastSeconds, _dragPullPhase, _dragJitterPhase;
    private Vector3 _dragJitterFrom, _dragJitterTo;
    private Pointer? _dragPointer;
    private FrameworkElement? _dragSource;
    private double _dragSourceOpacity;
    private bool _dragAnimationsEnabled;
    private Storyboard? _holeWobbleAnim, _holeSpinAnim;
    private PaletteTag? _dragTag;
    private Point _dragOriginPage;
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
        var previous = _railCategory;
        _railCategory = _railCategory == category ? null : category;
        if (_paintTag is { } active)
        {
            // 普通标签换了类型就不在调色板里了；「清除」换类型等于换作用范围，也退出，免得误清全库。
            var leavesPalette = active.Category == ClearCategory
                ? _railCategory != previous
                : _railCategory is not null && active.Category != _railCategory;
            if (leavesPalette) ExitPaintMode();
        }
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
        _stopGlow?.Invoke();
        _stopGlow = null;
        // 重建会换掉全部按钮：键盘焦点原本在调色板上时，重建后放回同一颗。
        var focusedKey = XamlRoot is { } root
            && FocusManager.GetFocusedElement(root) is Control { FocusState: FocusState.Keyboard, Tag: string key }
            ? key : null;
        _paletteWrap.Children.Clear();
        _paletteButtons.Clear();
        var showAll = _railCategory is null;
        // 「清除」是固定入口，任何视图都排第一；清哪些类型由左侧当前类型决定。
        AddPaletteButton(ClearTag);
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
        if (focusedKey is not null) DispatcherQueue.TryEnqueue(() => RestorePaletteFocus(focusedKey));
    }

    private void RestorePaletteFocus(string key)
    {
        if (!_paletteButtons.TryGetValue(key, out var button) || !button.Focus(FocusState.Keyboard)) return;
        button.StartBringIntoView();
    }

    private void AddPaletteButton(PaletteTag tag)
    {
        if (tag.Category == ClearCategory)
        {
            AddClearButton(_paintTag == tag);
            return;
        }
        var title = IngestReview.Titles[Array.IndexOf(IngestReview.Categories, tag.Category)];
        var button = new Button
        {
            Content = new TagChip(tag.Category, tag.Display), Padding = new Thickness(0),
            MinWidth = 0, MinHeight = 30, CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, title + "：" + tag.Display + (_paintTag == tag ? "，正在喷涂" : ""));
        ToolTipService.SetToolTip(button, tag.Display);
        var key = PaletteKey(tag);
        button.Tag = key;
        _paletteButtons[key] = button;
        ConnectPaletteButton(button, tag);
        var host = new Grid { Margin = new Thickness(2) };
        host.Children.Add(button);
        if (_paintTag == tag)
        {
            var color = TagColors.FromHex(TagColors.Get(tag.Category, ActualTheme == ElementTheme.Dark).Fg);
            AttachGlowRings(host, tag, WithAlpha(color, 70), color);
        }
        _paletteWrap.Children.Add(host);
    }

    /// <summary>
    /// 喷涂选中环：一圈静止描边 + 两处互成 180° 同步绕行的高光，高光各带细边和极淡外晕。
    /// 描边和标签本身都不旋转，只搬径向渐变的圆心，否则非正方形标签的圆角轮廓会被转到标签外面去。
    /// </summary>
    private void AttachGlowRings(Grid host, PaletteTag tag, Windows.UI.Color? outline, Windows.UI.Color light)
    {
        RadialGradientBrush CreateLight() => new()
        {
            MappingMode = BrushMappingMode.Absolute,
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = WithAlpha(light, 155) },
                new GradientStop { Offset = .5, Color = WithAlpha(light, 60) },
                new GradientStop { Offset = .8, Color = WithAlpha(light, 18) },
                new GradientStop { Offset = 1, Color = WithAlpha(light, 0) },
            },
        };
        Rectangle Rim(double thickness, Brush stroke, double opacity = 1) => new()
        {
            RadiusX = 9, RadiusY = 9, Margin = new Thickness(-3), IsHitTestVisible = false,
            StrokeThickness = thickness, Stroke = stroke, Opacity = opacity,
        };
        if (outline is { } ink) host.Children.Add(Rim(1, new SolidColorBrush(ink)));
        var lights = new[] { CreateLight(), CreateLight() };
        foreach (var brush in lights) host.Children.Add(Rim(4, brush, .18));
        var rings = lights.Select(brush => Rim(1.3, brush)).ToArray();
        foreach (var ring in rings) host.Children.Add(ring);
        AttachGlowOrbit(rings[0], lights, tag);
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) =>
        Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>两串标签名是否相同（忽略顺序和大小写），重做清除前核对当前状态用。</summary>
    private static bool SameNames(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && !left.Except(right, StringComparer.OrdinalIgnoreCase).Any();

    /// <summary>
    /// 喷涂高亮的光带：按标签实际尺寸算描边轨道，两处高光互成 180°，每 4 秒同步绕一圈。
    /// 只改径向渐变的圆心，旋转中心不再靠写死的尺寸（原来 60,60 只对 120×120 的方框成立）。
    /// </summary>
    private void AttachGlowOrbit(Rectangle ring, IReadOnlyList<RadialGradientBrush> brushes, PaletteTag tag)
    {
        long startedAt = 0;
        bool running = false;

        void Place()
        {
            var width = ring.ActualWidth;
            var height = ring.ActualHeight;
            if (width <= 0 || height <= 0 || startedAt == 0) return;
            var seconds = (Stopwatch.GetTimestamp() - startedAt) / (double)Stopwatch.Frequency;
            var angle = seconds * Math.PI / 2;   // 4 秒一圈
            var radius = Math.Clamp(Math.Min(width, height) * .75, 16, 34);
            var orbitX = width / 2 - 1;
            var orbitY = height / 2 - 1;
            for (var i = 0; i < brushes.Count; i++)
            {
                var spot = angle + i * Math.PI;   // 第二个高光永远在对角
                var brush = brushes[i];
                brush.RadiusX = brush.RadiusY = radius;
                var center = new Point(
                    width / 2 + Math.Cos(spot) * orbitX,
                    height / 2 + Math.Sin(spot) * orbitY);
                // 绝对映射下 GradientOrigin 也要跟着搬：默认值 (0.5,0.5) 是绝对坐标，
                // 不搬的话最亮的那一点会钉在描边左上角，看起来像多出一处不动的高光。
                brush.Center = center;
                brush.GradientOrigin = center;
            }
        }
        void OnRendering(object? sender, object args)
        {
            if (!_active) { Stop(); return; }
            Place();
        }
        void Start()
        {
            startedAt = Stopwatch.GetTimestamp();
            if (running || !_active || !_batchMode || _paintTag != tag) return;
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) { Place(); return; }
            running = true;
            CompositionTarget.Rendering += OnRendering;
            Place();
        }
        void Stop()
        {
            if (!running) return;
            running = false;
            CompositionTarget.Rendering -= OnRendering;
        }
        _stopGlow = Stop;
        ring.Loaded += (_, _) => Start();
        ring.Unloaded += (_, _) => Stop();
        ring.SizeChanged += (_, _) => Place();
    }

    /// <summary>
    /// 「清除」：灰底红字，固定排在最前；行为和分级标签一样是可选中的标签（点一下拿起工具，有选区时直接清空），
    /// 但不参与拖拽回收和 Delete；右键撤销最近一次清除。
    /// 清哪些类型跟着左侧当前类型走：没选类型清全部，选了类型只清该类。
    /// </summary>
    private void AddClearButton(bool spraying)
    {
        var dark = ActualTheme == ElementTheme.Dark;
        var (clearBg, _, clearInk) = ClearChipColors(dark);
        var scopeCategory = _railCategory;
        // 和 TagChip 完全同款：高 28、圆角 6、1 DIP 边框、左右内边距 8、字号 13。
        // 没选类型时是灰底无边框；选了类型后底色和外框直接换成该类型的标签配色，只有文字保持自己的红色。
        var typeColors = scopeCategory is { } scopeType ? TagColors.Get(scopeType, dark) : default;
        var background = scopeCategory is null ? clearBg : typeColors.Bg;
        var border = scopeCategory is null ? Microsoft.UI.Colors.Transparent : TagColors.FromHex(typeColors.Border);
        // 深色主题里如果这个类型的底色本身是亮的（如自定义的奶油白），红字用深红才看得清。
        var ink = dark && scopeCategory is not null && IsLightColor(TagColors.FromHex(background))
            ? "#8E2B1F"
            : clearInk;
        var button = new Button
        {
            Content = new Border
            {
                Height = 28, VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(border),
                Background = new SolidColorBrush(TagColors.FromHex(background)),
                Padding = new Thickness(8, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = "清除", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(TagColors.FromHex(ink)),
                },
            },
            Padding = new Thickness(0), MinWidth = 0, MinHeight = 30, CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(0),
        };
        var scope = scopeCategory is { } category
            ? $"{IngestReview.Titles[Array.IndexOf(IngestReview.Categories, category)]}标签"
            : "全部标签（含分级）";
        AutomationProperties.SetName(button, $"清除：清空这些图片的{scope}，右键撤销最近一次清除" + (spraying ? "，正在喷涂" : ""));
        ToolTipService.SetToolTip(button, $"清除这些图片的{scope}；右键撤销最近一次清除");
        var key = PaletteKey(ClearTag);
        button.Tag = key;
        _paletteButtons[key] = button;
        ConnectPaletteButton(button, ClearTag);
        var host = new Grid { Margin = new Thickness(2) };
        host.Children.Add(button);
        if (spraying)
        {
            // 选中时才出现普通标签那套外扩选中环＋两处绕行暗红高光；未选中不额外描边。
            var ringTint = scopeCategory is { } ringCategory
                ? TagColors.FromHex(TagColors.Get(ringCategory, dark).Fg)
                : dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            AttachGlowRings(host, ClearTag, WithAlpha(ringTint, 70), TagColors.FromHex(ClearGlowColor));
        }
        _paletteWrap.Children.Add(host);
    }

    private static (string Bg, string Border, string Fg) ClearChipColors(bool dark) =>
        dark ? ("#3A3A3A", "#6A6A6A", "#FF8A8A") : ("#E4E4E4", "#C0C0C0", "#C42B1C");

    /// <summary>底色够亮时改用深色前景，避免亮底分类（如奶油白的自定义）上的浅红字看不清。</summary>
    private static bool IsLightColor(Windows.UI.Color color) =>
        0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B > 150;

    /// <summary>内置入口（分级、清除）：固定存在、不可回收、不可删除。</summary>
    private static bool IsFixedTag(PaletteTag tag) => tag.Category is "rating" or ClearCategory;

    /// <summary>
    /// 按住拖出阈值就进入拖拽；标签中心出标签容器后表现吸力，框外松手才回收。
    /// 不走系统拖放（按钮上的 StartDragAsync 在 WinUI 3 里不稳定），后续移动/松手由页面级指针事件接管。
    /// </summary>
    private void ConnectPaletteDrag(Button button, PaletteTag tag)
    {
        Point? pressOrigin = null;
        uint? pointerId = null;
        void Reset() { pressOrigin = null; pointerId = null; }

        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            _suppressPaletteClick = false;
            if (_busy || _paintGesture || _paletteDragging || _dragFinishing || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
            var point = args.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;
            pressOrigin = point.Position;
            pointerId = args.Pointer.PointerId;
        }), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (pointerId != args.Pointer.PointerId) return;
            var point = args.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) { if (!_paletteDragging) Reset(); return; }
            if (pressOrigin is not { } origin || _busy || _paintGesture) { Reset(); return; }
            if (Math.Abs(point.Position.X - origin.X) < 5 && Math.Abs(point.Position.Y - origin.Y) < 5) return;
            _suppressPaletteClick = true;
            Reset();
            // 先转移捕获，再标记拖拽：按钮的 CaptureLost 不应取消新拖拽。
            button.ReleasePointerCapture(args.Pointer);
            if (!CapturePointer(args.Pointer)) return;
            _dragPointer = args.Pointer;
            _paletteDragging = true;
            args.Handled = true;
            BeginPaletteDrag(tag, button, point.Position);
        }), true);
        PointerEventHandler clearPress = (_, _) => Reset();
        button.AddHandler(UIElement.PointerReleasedEvent, clearPress, true);
        button.AddHandler(UIElement.PointerCanceledEvent, clearPress, true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, clearPress, true);
    }

    private void ConnectPaletteButton(Button button, PaletteTag tag)
    {
        // 这颗标签的中心：清除用它当飞散落点，普通标签用它当「贴上／摘下」动画的端点。
        Point Anchor() => button.TransformToVisual(this)
            .TransformPoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        button.Click += (_, _) =>
        {
            var skip = _suppressPaletteClick;
            _suppressPaletteClick = false;
            if (skip) return;
            PaletteClicked(tag, false, Anchor());
        };
        button.RightTapped += (_, args) =>
        {
            args.Handled = true;
            // 「清除」的右键是反向操作，不依赖选区；其余标签只在有选区时删除。
            if (tag.Category == ClearCategory || _batchSelected.Count > 0) PaletteClicked(tag, true, Anchor());
        };
        button.PreviewKeyDown += (_, args) =>
        {
            if (!_paletteDragging && !_dragFinishing && args.Key is VirtualKey.Enter or VirtualKey.Space) _suppressPaletteClick = false;
            if (args.Key == VirtualKey.Delete && !IsFixedTag(tag)
                && !_busy && !_paintGesture && !_paletteDragging && !_dragFinishing)
            {
                RemovePaletteTag(tag);
                args.Handled = true;
            }
        };
        // 内置项不能再拖进黑洞回收。
        if (IsFixedTag(tag)) return;
        ConnectPaletteDrag(button, tag);
    }

    private void BuildPaletteDragLayer()
    {
        _dragSlot.Stroke = ResourceBrush("AccentFillColorDefaultBrush");
        _holeArt = BuildHoleArt();
        _dragHole.Children.Add(_holeArt);
        _dragHoleHost.Children.Add(_dragHole);
        // 黑洞走 Canvas 绝对定位：跟卡片容器左对齐、贴底居中，不受层上负外边距影响。
        _dragHoleCanvas = new Canvas { IsHitTestVisible = false };
        _dragHoleCanvas.Children.Add(_dragHoleHost);

        _paletteDragCanvas.Children.Add(_dragSlot);
        _dragGhost.Child = _dragGhostText;
        _paletteDragCanvas.Children.Add(_dragGhost);
        // 黑洞在下、拖着的标签在上：黑洞先加，幽灵标签所在的层后加。
        _paletteDragLayer.Children.Add(_dragHoleCanvas);
        _paletteDragLayer.Children.Add(_paletteDragCanvas);
        Children.Add(_paletteDragLayer);
        // 拖拽自己管，不走系统 OLE 拖放：按钮上 StartDragAsync 在 WinUI 3 里要么直接失败，
        // 要么静默不起，改成跟着指针走更稳，也方便做悬停放大。
        _dragHole.RenderTransform = new ScaleTransform { CenterX = HolePixels / 2, CenterY = HolePixels / 2 };
        AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnDragPointerMoved), true);
        AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnDragPointerReleased), true);
        AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnDragPointerCanceled), true);
        AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, this)) OnDragPointerCanceled(this, args);
        }), true);
        BuildHoleAnimations();
    }

    /// <summary>把黑洞挪到卡片容器底边上方居中的位置。</summary>
    private void PositionDragHole()
    {
        if (_dragHoleCanvas is null || _carouselHost.ActualWidth <= 0) return;
        try
        {
            var origin = _dragHoleCanvas.TransformToVisual(this).TransformPoint(new Point(0, 0));
            var host = _carouselHost.TransformToVisual(this).TransformPoint(new Point(0, 0));
            Canvas.SetLeft(_dragHoleHost, host.X - origin.X + (_carouselHost.ActualWidth - HolePixels) / 2);
            Canvas.SetTop(_dragHoleHost, host.Y - origin.Y + _carouselHost.ActualHeight - HolePixels - 12);
        }
        catch (ArgumentException) { }
    }

    /// <summary>黑洞的三个动效：光晕呼吸、本体摇摆、漩涡自转。系统关闭动画时不启动。</summary>
    private void BuildHoleAnimations()
    {
        if (_holeArt is null || _holeBody is null || _holeWobble is null || _holeSwirl is null) return;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;

        // gh-wobble：本体左右轻摆 ±2°，7 秒一轮。
        var wobble = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(7), RepeatBehavior = RepeatBehavior.Forever };
        wobble.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = -2,
            KeySpline = new KeySpline { ControlPoint1 = new Point(.42, 0), ControlPoint2 = new Point(.58, 1) },
        });
        wobble.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.5)), Value = 2,
            KeySpline = new KeySpline { ControlPoint1 = new Point(.42, 0), ControlPoint2 = new Point(.58, 1) },
        });
        wobble.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(7)), Value = -2,
            KeySpline = new KeySpline { ControlPoint1 = new Point(.42, 0), ControlPoint2 = new Point(.58, 1) },
        });
        Storyboard.SetTarget(wobble, _holeWobble);
        Storyboard.SetTargetProperty(wobble, "Rotation");
        _holeWobbleAnim = new Storyboard { Children = { wobble } };
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(7), RepeatBehavior = RepeatBehavior.Forever };
            pulse.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1 });
            pulse.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.5)), Value = 1.014,
                KeySpline = new KeySpline { ControlPoint1 = new Point(.42, 0), ControlPoint2 = new Point(.58, 1) },
            });
            pulse.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(7)), Value = 1,
                KeySpline = new KeySpline { ControlPoint1 = new Point(.42, 0), ControlPoint2 = new Point(.58, 1) },
            });
            Storyboard.SetTarget(pulse, _holeWobble);
            Storyboard.SetTargetProperty(pulse, property);
            _holeWobbleAnim.Children.Add(pulse);
        }

        // 漩涡绕中心匀速转，5 秒一圈（浏览器原版的三倍速度）。
        var spin = new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromSeconds(5), RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(spin, _holeSwirl);
        Storyboard.SetTargetProperty(spin, "Angle");
        _holeSpinAnim = new Storyboard { Children = { spin } };
    }

    /// <summary>黑洞造型：照搬浏览器扩展 dropzone.js 的 HOLE_ART（120×120 的画布），缩到 HoleArtPixels 像素显示。</summary>
    private const double HoleArtSize = 120;
    private const double HoleArtPixels = 104;
    // 布局和命中区域不变；合成投影允许画到布局框外。
    private const double HolePixels = 112;

    /// <summary>
    /// 黑洞：光晕 + 本体（描边和填充，整体摇摆）+ 漩涡（绕中心匀速自转）+ 几个点。
    /// 三个动效各自对应扩展里的 gh-breathe / gh-wobble / gh-spin；外边距留够，不让光晕和摇摆被裁。
    /// </summary>
    private Grid BuildHoleArt()
    {
        const string blob = "M60 14.25A45.75 45.75 0 1 1 60 105.75A45.75 45.75 0 1 1 60 14.25Z";
        const string outerRing = "M60 13A47 47 0 1 1 60 107A47 47 0 1 1 60 13Z";
        // 绝对映射时 Center 和 GradientOrigin 都必须指定；默认起点 (0.5,0.5)
        // 是绝对坐标，漏设会把整片光晕拉向左上。
        var glow = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(60, 60), GradientOrigin = new Point(60, 60), RadiusX = 60, RadiusY = 60,
            GradientStops =
            {
                new GradientStop { Offset = .42, Color = Windows.UI.Color.FromArgb(77, 255, 255, 255) },
                new GradientStop { Offset = .55, Color = Windows.UI.Color.FromArgb(41, 255, 255, 255) },
                new GradientStop { Offset = .70, Color = Windows.UI.Color.FromArgb(13, 255, 255, 255) },
                new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0, 255, 255, 255) },
            },
        };
        var core = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(.41, .35), GradientOrigin = new Point(.41, .35), RadiusX = .68, RadiusY = .68,
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(255, 29, 29, 29) },
                new GradientStop { Offset = .52, Color = Windows.UI.Color.FromArgb(255, 7, 7, 7) },
                new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(255, 0, 0, 0) },
            },
        };
        var ink = new SolidColorBrush(Windows.UI.Color.FromArgb(126, 255, 255, 255));
        var ghost = new SolidColorBrush(Windows.UI.Color.FromArgb(58, 255, 255, 255));
        var swirl = new SolidColorBrush(Windows.UI.Color.FromArgb(87, 255, 255, 255));
        var dot = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 255, 255, 255));

        var body = new Grid { Width = HoleArtSize, Height = HoleArtSize };
        body.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = ParsePath(blob), Fill = core });
        // 两道同心正圆保留双线层次，描边透明度较浏览器原版降低约 25%。
        body.Children.Add(Stroke(ParsePath(blob), ink, 3.2));
        body.Children.Add(Stroke(ParsePath(outerRing), ghost, 1.8));

        // 浏览器只旋转五段弧，小点保持在 SVG 的固定位置。
        var spin = new Grid { Width = HoleArtSize, Height = HoleArtSize };
        foreach (var arc in new[]
        {
            "M40 84C56 94 82 90 89 71", "M31 60C29 40 47 24 66 27",
            "M52 73C43 64 46 51 57 47C67 43 76 51 74 62C72 70 63 73 57 68",
            "M92 46C98 56 97 68 90 78", "M62 51C71 54 72 65 64 69",
        })
            spin.Children.Add(Stroke(ParsePath(arc), swirl, 2.6));
        var dots = new Grid { Width = HoleArtSize, Height = HoleArtSize };
        foreach (var (x, y, r) in new[] { (36.0, 44.0, 1.6), (86.0, 90.0, 1.3), (70.0, 21.0, 1.0) })
            dots.Children.Add(new Ellipse
            {
                Width = r * 2, Height = r * 2, Margin = new Thickness(x - r, y - r, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Fill = dot,
            });

        // 本体和漩涡都按 120 单位的画布铺满，整体再缩到浏览器默认的 104 DIP。
        var art = new Grid { Width = HoleArtSize, Height = HoleArtSize };
        var shadowHost = new Grid { Width = HoleArtSize, Height = HoleArtSize };
        var content = new Grid { Width = HoleArtSize, Height = HoleArtSize };
        content.Children.Add(new Ellipse { Width = 120, Height = 120, Fill = glow });
        content.Children.Add(body);
        content.Children.Add(spin);
        content.Children.Add(dots);
        art.Children.Add(shadowHost);
        art.Children.Add(content);
        // 捕获完整 SVG 等价内容的 alpha，再做零偏移模糊投影；不用实心径向圆近似 CSS filter。
        content.Loaded += (_, _) => InitializeHoleShadow(content, shadowHost);
        // 绕中心缩到 104：origin 留在左上会把整个洞缩偏，后面的放大动画也会围绕错的中心。
        art.RenderTransform = new ScaleTransform { ScaleX = HoleArtPixels / HoleArtSize, ScaleY = HoleArtPixels / HoleArtSize };
        art.RenderTransformOrigin = new Point(.5, .5);
        art.HorizontalAlignment = HorizontalAlignment.Center;
        art.VerticalAlignment = VerticalAlignment.Center;

        // 摇摆和自转都绕画布中心。CompositeTransform 的默认原点是左上角，
        // 而 RenderTransformOrigin 管不到 Storyboard 驱动的 RenderTransform，必须显式给中心。
        body.RenderTransform = _holeWobble = new CompositeTransform { CenterX = HoleArtSize / 2, CenterY = HoleArtSize / 2 };
        spin.RenderTransform = _holeSwirl = new RotateTransform { CenterX = HoleArtSize / 2, CenterY = HoleArtSize / 2 };
        _holeBody = body;

        // Canvas 不参与子元素尺寸约束，投影和摇摆可越过布局框，不会切成方形。
        var canvas = new Canvas { Width = HolePixels, Height = HolePixels };
        Canvas.SetLeft(art, (HolePixels - HoleArtSize) / 2);
        Canvas.SetTop(art, (HolePixels - HoleArtSize) / 2);
        canvas.Children.Add(art);
        return new Grid { Width = HolePixels, Height = HolePixels, Children = { canvas }, HorizontalAlignment = HorizontalAlignment.Center };
    }

    /// <summary>与扩展的 drop-shadow 和 gh-breathe 相同：只改变投影，不淡化本体。</summary>
    private void InitializeHoleShadow(Grid content, Grid host)
    {
        if (_holeShadow is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(content).Compositor;
        var surface = compositor.CreateVisualSurface();
        surface.SourceVisual = ElementCompositionPreview.GetElementVisual(content);
        surface.SourceSize = new Vector2((float)HoleArtSize);
        var mask = compositor.CreateSurfaceBrush(surface);
        var shadow = compositor.CreateDropShadow();
        shadow.Mask = mask;
        shadow.Color = Microsoft.UI.Colors.White;
        shadow.Offset = Vector3.Zero;
        // CSS 的 px 作用在缩放后的 SVG 上，这里换回 120 单位的画布。
        shadow.BlurRadius = (float)(8 * HoleArtSize / HoleArtPixels);
        shadow.Opacity = .38f;
        var visual = compositor.CreateSpriteVisual();
        visual.Size = new Vector2((float)HoleArtSize);
        visual.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, visual);
        _holeShadow = shadow;
        content.Unloaded += Release;
        void Release(object sender, RoutedEventArgs args)
        {
            content.Unloaded -= Release;
            SetHoleMotion(HoleMotion.Breathe, false);
            ElementCompositionPreview.SetElementChildVisual(host, null);
            _holeShadow?.Dispose();
            _holeShadow = null;
            _holeBlurAnimation?.Dispose();
            _holeOpacityAnimation?.Dispose();
            _holeBlurAnimation = _holeOpacityAnimation = null;
            visual.Dispose();
            mask.Dispose();
            surface.Dispose();
        }
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(.42f, 0), new Vector2(.58f, 1));
        _holeBlurAnimation = compositor.CreateScalarKeyFrameAnimation();
        _holeBlurAnimation.InsertKeyFrame(0, (float)(8 * HoleArtSize / HoleArtPixels));
        _holeBlurAnimation.InsertKeyFrame(.5f, (float)(13 * HoleArtSize / HoleArtPixels), ease);
        _holeBlurAnimation.InsertKeyFrame(1, (float)(8 * HoleArtSize / HoleArtPixels), ease);
        _holeBlurAnimation.Duration = TimeSpan.FromSeconds(2.8);
        _holeBlurAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
        _holeOpacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        _holeOpacityAnimation.InsertKeyFrame(0, .38f);
        _holeOpacityAnimation.InsertKeyFrame(.5f, .58f, ease);
        _holeOpacityAnimation.InsertKeyFrame(1, .38f, ease);
        _holeOpacityAnimation.Duration = TimeSpan.FromSeconds(2.8);
        _holeOpacityAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
        if (_dragHoleHost.Visibility == Visibility.Visible) SetHoleMotion(HoleMotion.Breathe, true);
    }

    private enum HoleMotion { Breathe, Wobble, Spin }

    /// <summary>三个动效分开控制：拖拽开始时启动，收起时停止；悬停只放大，和浏览器一样保留呼吸。</summary>
    private void SetHoleMotion(HoleMotion motion, bool on)
    {
        if (motion == HoleMotion.Breathe)
        {
            if (_holeShadow is null) return;
            _holeShadow.StopAnimation(nameof(DropShadow.BlurRadius));
            _holeShadow.StopAnimation(nameof(DropShadow.Opacity));
            if (on && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                _holeShadow.StartAnimation(nameof(DropShadow.BlurRadius), _holeBlurAnimation);
                _holeShadow.StartAnimation(nameof(DropShadow.Opacity), _holeOpacityAnimation);
            }
            return;
        }
        var story = motion switch
        {
            HoleMotion.Wobble => _holeWobbleAnim,
            HoleMotion.Spin => _holeSpinAnim,
            _ => null,
        };
        if (story is null) return;
        if (on) story.Begin(); else story.Stop();
    }

    /// <summary>把 SVG 的 path 数据转成 XAML 的 PathGeometry；两者语法一致，用 XAML 解析器直接读。</summary>
    private static PathGeometry ParsePath(string data)
    {
        var geometry = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);
        return geometry as PathGeometry ?? new PathGeometry();
    }

    private static Microsoft.UI.Xaml.Shapes.Path Stroke(Geometry data, Brush stroke, double thickness, CompositeTransform? transform = null) => new()
    {
        Data = data, Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        RenderTransform = transform ?? new CompositeTransform(),
    };

    private void BeginPaletteDrag(PaletteTag tag, FrameworkElement source, Point point)
    {
        _dragAnim?.Stop();
        _dragAnim = null;
        _dragFinishing = false;
        _dragAnimationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        _dragTag = tag;
        _dragSource = source;
        _dragSourceOpacity = source.Opacity;
        source.Opacity = 0;
        _dragOriginPage = source.TransformToVisual(this).TransformPoint(new Point(0, 0));
        _dragSlot.Width = Math.Max(8, source.ActualWidth);
        _dragSlot.Height = Math.Max(8, source.ActualHeight);
        var colors = TagColors.Get(tag.Category, ActualTheme == ElementTheme.Dark);
        _dragGhostText.Text = tag.Display;
        _dragGhostText.Foreground = new SolidColorBrush(TagColors.FromHex(colors.Fg));
        _dragGhost.Background = new SolidColorBrush(TagColors.FromHex(colors.Bg));
        _dragGhost.BorderBrush = new SolidColorBrush(TagColors.FromHex(colors.Border));
        _dragGhost.Width = Math.Max(30, _dragSlot.Width);
        _dragGhost.Height = Math.Max(26, _dragSlot.Height);
        _dragGhost.RenderTransform = new CompositeTransform();
        _dragGhost.Opacity = 1;
        _dragGhost.Visibility = Visibility.Visible;
        _paletteDragLayer.Visibility = Visibility.Visible;
        SetDragArmed(true);
        // 一开始拖就把黑洞浮出来，不用等拖过更远的距离。
        ShowDragHole(true);
        _dragPullAmount = _dragPullLastSeconds = _dragJitterPhase = 0;
        _dragPullPhase = Random.Shared.NextDouble() * 2 * Math.PI;
        _dragJitterFrom = Vector3.Zero;
        _dragJitterTo = NextDragJitter();
        _dragPullClock.Restart();
        if (_dragAnimationsEnabled) CompositionTarget.Rendering += OnDragPullRendering;
        UpdatePaletteDrag(point);
    }

    /// <summary>框内跟手；框外朝黑洞偏移、倾斜和拉伸，尚不回收。</summary>
    private void UpdatePaletteDrag(Point point)
    {
        var offset = _paletteDragLayer.TransformToVisual(this).TransformPoint(new Point(0, 0));
        Canvas.SetLeft(_dragGhost, point.X - offset.X - _dragGhost.Width / 2);
        Canvas.SetTop(_dragGhost, point.Y - offset.Y - _dragGhost.Height / 2);
        PositionDragSlot();
        _dragPullPoint = point;
        _dragPullOutside = IsOutsideTagContainer(point);
        SetHoleHot(_dragPullOutside);
        if (!_dragAnimationsEnabled)
        {
            _dragPullAmount = _dragPullOutside ? 1 : 0;
            ApplyDragPull(0);
        }
    }

    // 按时间独立推进；指针移动只更新位置和方向，不重启动画或重置强弱相位。
    private void OnDragPullRendering(object? sender, object args)
    {
        var seconds = _dragPullClock.Elapsed.TotalSeconds;
        var dt = seconds - _dragPullLastSeconds;
        _dragPullLastSeconds = seconds;
        _dragPullAmount += ((_dragPullOutside ? 1 : 0) - _dragPullAmount) * (1 - Math.Exp(-dt / .08));
        ApplyDragPull(dt);
    }

    private void ApplyDragPull(double dt)
    {
        if (_dragGhost.RenderTransform is not CompositeTransform transform) return;
        var hole = DragHoleCenter();
        var dx = hole.X - _dragPullPoint.X;
        var dy = hole.Y - _dragPullPoint.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var proximity = 1 / (1 + distance / 180);
        var proximityGain = 1 + proximity;
        // 积分相位：靠近时加快，但移动鼠标不会让相位跳变或重新开始。
        _dragPullPhase += dt * (1.8 + 2.7 * proximity) * 2 * Math.PI;
        _dragJitterPhase += dt * (6 + 6 * proximity);
        if (_dragJitterPhase >= 1)
        {
            _dragJitterPhase %= 1;
            _dragJitterFrom = _dragJitterTo;
            _dragJitterTo = NextDragJitter();
        }
        // 随机目标之间平滑插值，避免逐帧白噪声造成闪跳；每次拖拽重新取样。
        var blend = _dragJitterPhase * _dragJitterPhase * (3 - 2 * _dragJitterPhase);
        var jitter = Vector3.Lerp(_dragJitterFrom, _dragJitterTo, (float)blend);
        var wave = .64 + .22 * Math.Sin(_dragPullPhase)
            + .08 * Math.Sin(_dragPullPhase * .61 + .6) + .06 * jitter.X;
        var force = _dragPullAmount * wave;
        var pull = Math.Min(26 * force * proximityGain, distance);
        var sideways = (3 + 5 * proximity) * jitter.Y * force;
        var ux = distance > 0 ? dx / distance : 0;
        var uy = distance > 0 ? dy / distance : 0;
        transform.TranslateX = ux * pull - uy * sideways;
        transform.TranslateY = uy * pull + ux * sideways;
        transform.Rotation = (Math.Clamp(Math.Atan2(dy, dx) * 180 / Math.PI * .12, -18, 18)
            + 4 * proximityGain * Math.Sin(_dragPullPhase * 1.37)
            + (7 + 7 * proximity) * jitter.Z) * force;
        transform.ScaleX = 1 + .25 * force;
        transform.ScaleY = 1 - .28 * force;
    }

    private static Vector3 NextDragJitter() => new(
        Random.Shared.NextSingle() * 2 - 1, Random.Shared.NextSingle() * 2 - 1, Random.Shared.NextSingle() * 2 - 1);

    private void StopDragPull()
    {
        CompositionTarget.Rendering -= OnDragPullRendering;
        _dragPullClock.Reset();
    }

    // point 是鼠标／标签未受吸力偏移时的中心；不能用吸力挪过的图形来反向影响回收条件。
    private bool IsOutsideTagContainer(Point point)
    {
        if (_paletteTagContainer.ActualWidth <= 0 || _paletteTagContainer.ActualHeight <= 0) return false;
        var bounds = _paletteTagContainer.TransformToVisual(this).TransformBounds(
            new Rect(0, 0, _paletteTagContainer.ActualWidth, _paletteTagContainer.ActualHeight));
        return !bounds.Contains(point);
    }

    private void SetHoleHot(bool hot)
    {
        if (_holeHot == hot) return;
        _holeHot = hot;
        if (_dragHole.RenderTransform is ScaleTransform scale)
        {
            if (!_dragAnimationsEnabled)
            {
                scale.ScaleX = scale.ScaleY = hot ? 1.06 : 1;
                return;
            }
            var story = new Storyboard();
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            AddAnimation(story, scale, "ScaleX", hot ? 1.06 : 1, ease);
            AddAnimation(story, scale, "ScaleY", hot ? 1.06 : 1, ease);
            story.Begin();
        }
    }

    private void OnDragPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_paletteDragging || _dragFinishing || args.Pointer.PointerId != _dragPointer?.PointerId) return;
        var point = args.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) { CancelPaletteDrag(); return; }
        UpdatePaletteDrag(point.Position);
        args.Handled = true;
    }

    private void OnDragPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_paletteDragging || args.Pointer.PointerId != _dragPointer?.PointerId) return;
        var drop = args.GetCurrentPoint(this).Position;
        UpdatePaletteDrag(drop);
        StopDragPull();
        _paletteDragging = false;
        _dragFinishing = true;
        args.Handled = true;
        ReleaseDragCapture();
        // 别在按钮自己的事件里重建调色板，推到下一轮再写库。
        DispatcherQueue.TryEnqueue(() => FinishPaletteDrag(drop));
    }

    private void OnDragPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_dragFinishing || !_paletteDragging || args.Pointer.PointerId != _dragPointer?.PointerId) return;
        CancelPaletteDrag();
    }

    /// <summary>只以标签容器边界判定：框外松手吸入回收，框内松手取消。</summary>
    private void FinishPaletteDrag(Point drop)
    {
        if (!_dragFinishing || _dragTag is not { } tag) return;
        if (!IsOutsideTagContainer(drop)) { CancelPaletteDrag(); return; }
        StartDragSuction(tag);
    }

    private void CancelPaletteDrag()
    {
        _dragAnim?.Stop();
        _dragAnim = null;
        _dragFinishing = false;
        _paletteDragging = false;
        HideDragLayer();
    }

    private void ReleaseDragCapture()
    {
        var pointer = _dragPointer;
        _dragPointer = null;
        if (pointer is not null) ReleasePointerCapture(pointer);
    }

    private void HideDragLayer()
    {
        ReleaseDragCapture();
        StopDragPull();
        if (_dragSource is not null) _dragSource.Opacity = _dragSourceOpacity;
        _dragSource = null;
        _dragTag = null;
        _paletteDragLayer.Visibility = Visibility.Collapsed;
        SetDragArmed(false);
        SetHoleHot(false);
        ShowDragHole(false);
        _dragGhost.Visibility = Visibility.Collapsed;
        _dragGhost.RenderTransform = null;
    }

    private void SetDragArmed(bool armed)
    {
        if (_dragArmed == armed) return;
        _dragArmed = armed;
        _dragSlot.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>黑洞在拖拽一开始就浮出来，不等拖过阈值；松手或取消时收起。</summary>
    private void ShowDragHole(bool on)
    {
        _dragHoleHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) PositionDragHole();
        foreach (var motion in new[] { HoleMotion.Breathe, HoleMotion.Wobble, HoleMotion.Spin })
            SetHoleMotion(motion, on);
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

    /// <summary>从当前吸力预览的位置沿弧线加速缩入洞心；完成后才移出调色板。</summary>
    private void StartDragSuction(PaletteTag tag)
    {
        var offset = _paletteDragLayer.TransformToVisual(this).TransformPoint(new Point(0, 0));
        var preview = (CompositeTransform)_dragGhost.RenderTransform;
        var start = new Point(Canvas.GetLeft(_dragGhost) + _dragGhost.Width / 2 + preview.TranslateX,
            Canvas.GetTop(_dragGhost) + _dragGhost.Height / 2 + preview.TranslateY);
        var rotation = preview.Rotation;
        var scaleX = preview.ScaleX;
        var scaleY = preview.ScaleY;
        StopDragPull();
        PositionDragHole();
        var hole = DragHoleCenter();
        var target = new Point(hole.X - offset.X, hole.Y - offset.Y);
        Canvas.SetLeft(_dragGhost, start.X - _dragGhost.Width / 2);
        Canvas.SetTop(_dragGhost, start.Y - _dragGhost.Height / 2);
        var transform = new CompositeTransform { Rotation = rotation, ScaleX = scaleX, ScaleY = scaleY };
        _dragGhost.RenderTransform = transform;
        _dragFinishing = true;
        if (!_dragAnimationsEnabled)
        {
            RemovePaletteTag(tag);
            _dragFinishing = false;
            HideDragLayer();
            return;
        }
        var story = new Storyboard();
        var dx = target.X - start.X;
        var dy = target.Y - start.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var bend = Math.Min(60, distance * .18);
        var cx = dx / 2 - (distance > 0 ? dy / distance * bend : 0);
        var cy = dy / 2 + (distance > 0 ? dx / distance * bend : 0);
        // 二次曲线分段采样，进度平方产生越靠近洞心越快的吸入感。
        foreach (var (property, horizontal) in new[] { ("TranslateX", true), ("TranslateY", false) })
        {
            var motion = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
            for (var i = 0; i <= 12; i++)
            {
                var time = i / 12.0;
                var t = time * time;
                var value = 2 * (1 - t) * t * (horizontal ? cx : cy) + t * t * (horizontal ? dx : dy);
                motion.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400 * time)), Value = value });
            }
            Storyboard.SetTarget(motion, transform);
            Storyboard.SetTargetProperty(motion, property);
            story.Children.Add(motion);
        }
        AddSuctionKeys(transform, "Rotation", (0, rotation), (.3, rotation + 15), (1, rotation + 90));
        AddSuctionKeys(transform, "ScaleX", (0, scaleX), (.25, 1.25), (.65, .65), (1, .03));
        AddSuctionKeys(transform, "ScaleY", (0, scaleY), (.25, .65), (.65, .24), (1, .03));
        AddSuctionKeys(_dragGhost, "Opacity", (0, 1), (.7, 1), (1, 0));
        story.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_dragAnim, story)) return;
            RemovePaletteTag(tag);
            _dragAnim = null;
            story.Stop();
            _dragFinishing = false;
            HideDragLayer();
        };
        _dragAnim = story;
        story.Begin();

        void AddSuctionKeys(DependencyObject element, string property, params (double Time, double Value)[] keys)
        {
            var motion = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
            foreach (var (time, value) in keys)
                motion.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400 * time)), Value = value });
            Storyboard.SetTarget(motion, element);
            Storyboard.SetTargetProperty(motion, property);
            story.Children.Add(motion);
        }
    }

    private static void AddAnimation(Storyboard story, DependencyObject target, string property, double to, EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = ease };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }

    private void RemovePaletteTag(PaletteTag tag)
    {
        if (_busy || _paintGesture) return;
        if (_paintTag == tag) ExitPaintMode();
        if (_observedTag == tag) _observedTag = null;
        _palette.Remove(tag);
        PersistPalette(); RebuildPalette(); UpdateWorkbenchState();
    }

    private void PaletteClicked(PaletteTag tag, bool remove, Point? chipAnchor = null)
    {
        if (_busy || _paintGesture || _paletteDragging || _dragFinishing || !_batchMode || AppService.IsShuttingDown) return;
        // 「清除」的右键是反向操作：撤销最近一次清除（点标签本身没有扫过的图片，撤整笔）。
        if (tag.Category == ClearCategory && remove) { UndoLastClear(null); return; }
        _observedTag = tag;
        if (tag.Category == ClearCategory)
        {
            if (_batchSelected.Count == 0)
            {
                // 和普通标签一样：没有选区时点它就是拿起工具，不立刻动手。
                if (_paintTag == tag) { ExitPaintMode(); return; }
                _paintTag = tag;
                RebuildPalette(); UpdateWorkbenchState();
                return;
            }
            ExitPaintMode();
            TiltPaletteTag(tag);
            _clearFlyTarget = chipAnchor;
            _paintTask = ClearTagsAsync(_batchSelected.ToArray(), _railCategory);
            return;
        }
        if (_batchSelected.Count > 0)
        {
            ExitPaintMode();
            TiltPaletteTag(tag);
            // 混合状态先补齐；全都有时再次左击删除；右键始终删除。动画落点就是刚点的这颗标签。
            _paintTask = ApplyTagStrokeAsync(tag, _batchSelected.ToArray(), remove, toggleSelection: !remove, flyAnchor: chipAnchor);
        }
        else
        {
            if (_paintTag == tag) { ExitPaintMode(); return; }
            _paintTag = tag;
            RebuildPalette(); UpdateWorkbenchState();
        }
    }

    /// <summary>
    /// 批量贴／撕时让刚点的那颗标签翘一下。离开喷涂会重建调色板、把原来那颗按钮换掉，
    /// 所以按 key 找重建后的新按钮，并等它排版拿到实际宽高再播。
    /// </summary>
    private void TiltPaletteTag(PaletteTag tag)
    {
        var key = PaletteKey(tag);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_batchMode || !_paletteButtons.TryGetValue(key, out var button)) return;
            if (button.ActualWidth > 0)
            {
                TiltChip(button);
                return;
            }
            // 刚重建的按钮可能还没排版，等它拿到宽度再补一次。
            void OnSize(object? sender, SizeChangedEventArgs e)
            {
                if (e.NewSize.Width <= 0) return;
                button.SizeChanged -= OnSize;
                TiltChip(button);
            }
            button.SizeChanged += OnSize;
        });
    }

    /// <summary>
    /// 点标签时的俏皮反馈：像被指尖按了一下，先向右翘起来再弹回去。
    /// 只在真的动到选区（批量贴／撕）时播；系统禁用动画时不动。
    /// </summary>
    private static void TiltChip(Button? chip)
    {
        if (chip is null || chip.ActualWidth <= 0 || chip.ActualHeight <= 0) return;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        if (chip.RenderTransform is not RotateTransform rotate)
        {
            rotate = new RotateTransform();
            chip.RenderTransform = rotate;
            // 轴心压在标签底部偏下，翘起来才像贴纸被指尖按了一下。
            chip.RenderTransformOrigin = new Point(.5, .8);
        }
        var story = new Storyboard();
        var tilt = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        foreach (var (time, angle, ease) in new[]
        {
            (90, 8.0, EasingMode.EaseOut),
            (190, -2.6, EasingMode.EaseInOut),
            (330, 0.0, EasingMode.EaseOut),
        })
        {
            tilt.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(time)),
                Value = angle,
                EasingFunction = new CubicEase { EasingMode = ease },
            });
        }
        Storyboard.SetTarget(tilt, rotate);
        Storyboard.SetTargetProperty(tilt, "Angle");
        story.Children.Add(tilt);
        story.Begin();
    }

    /// <summary>工作台类型图标的当前位置（相对本页），作为 AI 贴标签动画的起点；图标没显示时返回 null。</summary>
    private Point? RailAnchor(string category)
    {
        if (!_batchMode || !_railButtons.TryGetValue(category, out var button) || _railPanel.ActualHeight <= 0) return null;
        try
        {
            var point = button.TransformToVisual(this)
                .TransformPoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            return new Point(point.X, point.Y);
        }
        catch (ArgumentException) { return null; }
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
        // 退出即清掉工具：左侧图标下方的「已贴上」提示与卡片标记立刻消失，不等下一次点标签。
        _observedTag = null;
        _carousel.CancelTagStroke();
        RebuildPalette(); UpdateButtons();
    }

    private void UpdateWorkbenchState()
    {
        if (!_batchMode) { _carousel.SetTagTool(ReviewCarousel.TagToolState.None); return; }
        _undoPaint.IsEnabled = !_busy && !_paintGesture && _paintUndo.Count > 0;
        _redoPaint.IsEnabled = !_busy && !_paintGesture && _paintRedo.Count > 0;
        _paletteContainer.IsHitTestVisible = !_busy && !_paintGesture;
        _paletteWrap.IsHitTestVisible = !_busy && !_paintGesture;
        _paletteSearch.IsEnabled = !_busy && !_paintGesture && _railCategory != "rating";
        _batchToggle.IsEnabled &= !_paintGesture;
        _selectAll.IsEnabled &= !_paintGesture;
        _cardSize.IsEnabled &= !_paintGesture;
        _batchAiAnalyze.IsEnabled &= !_paintGesture;
        _archive.IsEnabled &= !_paintGesture;
        _ignore.IsEnabled &= !_paintGesture;
        UpdateTagToolState();
    }

    /// <summary>把当前观察到的工具/标签下发给卡片区：清除用固定色和标记，普通标签用分类色和匹配集合。</summary>
    private void UpdateTagToolState()
    {
        var observed = _paintTag ?? _observedTag;
        if (observed?.Category == ClearCategory)
        {
            var (bg, border, fg) = ClearChipColors(ActualTheme == ElementTheme.Dark);
            _carousel.SetTagTool(new(null, "", ClearCategory, _batchSelected.ToArray(), bg, border, fg));
            return;
        }
        IReadOnlyCollection<int> matches = observed is null
            ? []
            : _rows.Where(row => HasTag(row, observed)).Select(row => row.Id).ToArray();
        var colors = observed is null ? default : TagColors.Get(observed.Category, ActualTheme == ElementTheme.Dark);
        _carousel.SetTagTool(new(
            _batchMode && _paintTag is not null ? PaletteKey(_paintTag) : null,
            observed?.Display ?? "", null, matches,
            colors == default ? "" : colors.Bg, colors == default ? "" : colors.Border, colors == default ? "" : colors.Fg));
    }

    /// <summary>
    /// 撤销/重做一次「清除」：撤销只把这次清掉的那几类标签写回，其他类型保持现状；重做再清一次。
    /// subset 非空时只处理这几张（右键扫过哪些就只恢复哪些），没扫到的仍留在撤销栈里。
    /// </summary>
    private async Task RestoreClearedAsync(TagStroke history, bool redo, IReadOnlyList<TagChange>? subset = null)
    {
        if (_busy || AppService.Store is not { } store || AppService.IsShuttingDown) return;
        SetBusy(true);
        var committed = false;
        try
        {
            var changes = subset ?? history.Changes;
            var result = await Task.Run(() =>
            {
                var applied = new List<(int Id, Dictionary<string, List<string>> Before, Dictionary<string, List<string>> After)>();
                using var tx = store.Transaction();
                foreach (var change in changes)
                {
                    if (store.IngestRow(change.Id) is not { State: "pending" } row) continue;
                    var current = IngestReview.Tags(row.Payload, row.Rating);
                    var snapshot = change.BeforeTags ?? [];
                    if (redo)
                    {
                        // 只有当前标签仍等于撤销后恢复的那一套时才重做，避免覆盖期间的新改动。
                        if (snapshot.Count == 0
                            || !snapshot.All(pair => SameNames(current[pair.Key], pair.Value)))
                            continue;
                        var next = current.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
                        foreach (var key in snapshot.Keys) next[key] = [];
                        var rating = snapshot.ContainsKey("rating") ? "" : next["rating"].FirstOrDefault() ?? "";
                        var source = JsonNode.Parse(row.Payload)?["rating_source"]?.GetValue<string>() ?? "";
                        var payload = JsonNode.Parse(IngestReview.UpdatePayload(row.Payload, next, rating))!.AsObject();
                        if (snapshot.ContainsKey("character")) payload["review_skins"] = JsonSerializer.SerializeToNode(change.AfterSkins ?? []);
                        if (snapshot.ContainsKey("rating")) payload.Remove("rating_source");
                        else if (source.Length > 0) payload["rating_source"] = source;
                        else payload.Remove("rating_source");
                        store.UpdateIngestReview(change.Id, payload.ToJsonString(), rating);
                        applied.Add((change.Id, current, next));
                    }
                    else
                    {
                        // 只把那几类写回，别把清除后新贴的其他类型一起抹掉。
                        var restore = current.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
                        foreach (var (key, names) in snapshot) restore[key] = names.ToList();
                        var rating = restore["rating"].FirstOrDefault() ?? "";
                        var source = JsonNode.Parse(row.Payload)?["rating_source"]?.GetValue<string>() ?? "";
                        var payload = JsonNode.Parse(IngestReview.UpdatePayload(row.Payload, restore, rating))!.AsObject();
                        if (snapshot.ContainsKey("character")) payload["review_skins"] = JsonSerializer.SerializeToNode(change.BeforeSkins ?? []);
                        if (snapshot.ContainsKey("rating"))
                        {
                            if (change.RatingSource.Length > 0) payload["rating_source"] = change.RatingSource;
                            else payload.Remove("rating_source");
                        }
                        else if (source.Length > 0) payload["rating_source"] = source;
                        else payload.Remove("rating_source");
                        store.UpdateIngestReview(change.Id, payload.ToJsonString(), rating);
                        applied.Add((change.Id, current, restore));
                    }
                }
                tx.Success = true;
                return applied;
            });
            committed = true;
            var appliedIds = result.Select(item => item.Id).ToHashSet();
            var appliedChanges = changes.Where(change => appliedIds.Contains(change.Id)).ToList();
            var from = redo ? _paintRedo : _paintUndo;
            var to = redo ? _paintUndo : _paintRedo;
            from.RemoveAt(from.Count - 1);
            if (subset is null)
            {
                if (appliedChanges.Count > 0) to.Add(history with { Changes = appliedChanges });
            }
            else
            {
                var remaining = history.Changes.Where(change => !appliedIds.Contains(change.Id)).ToList();
                if (remaining.Count > 0) from.Add(history with { Changes = remaining });
                if (appliedChanges.Count > 0) to.Add(history with { Changes = appliedChanges });
            }
            foreach (var item in result)
            {
                ApplyRowTagUpdate(store, item.Id);
                // 撤销时恢复的标签从对应类型图标飞回卡片；重做没有配套的离场动画，交给卡片刷新。
                if (!redo) FlyTagApply(item.Id, AddedLabels(item.Before, item.After));
            }
            _status.Text = $"{(redo ? "已重做清除" : "已撤销清除")} {appliedChanges.Count} 张图片";
            UpdateWorkbenchState();
            _carousel.UpdateTagRows(_rows, true);
            RefreshDestination();
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { _status.Text = (committed ? "标签已保存，但界面刷新失败：" : "撤销清除失败，整笔未保存：") + ex.Message; }
        finally { SetBusy(false); UpdateButtons(); OpenDeferredNotification(); }
    }

    private void ConnectWorkbenchInput()
    {
        _carousel.TagStrokeRequested += (ids, intent, phase) =>
        {
            if (!_batchMode || _busy || !_active || AppService.IsShuttingDown) return;
            _paintGesture = phase == "start";
            if (intent == ReviewCarousel.TagStrokeIntent.UndoClear)
            {
                // 右键是「清除」的反向操作：只把这一笔扫过的图片恢复回来。
                if (phase == "end") UndoLastClear(ids);
            }
            else if (intent == ReviewCarousel.TagStrokeIntent.Clear)
            {
                if (phase == "start") _clearStroke.Clear();
                if (phase == "end")
                {
                    _clearStroke.AddRange(ids);
                    // 落点用松手位置；第一笔还没收到位置时由飞散动画自己退回卡片中心。
                    _clearFlyTarget = _lastStrokePoint;
                    _paintTask = ClearTagsAsync(_clearStroke.ToArray(), _railCategory);
                }
            }
            else if (phase == "end" && _paintTag is { } tag)
            {
                _paintTask = ApplyTagStrokeAsync(tag, ids, intent == ReviewCarousel.TagStrokeIntent.Erase);
            }
            UpdateButtons();
        };
        _carousel.ExitTagToolRequested += ExitPaintMode;
        _carousel.PointerReported += point =>
        {
            // 网页报的是视口内坐标，加回卡片区左上角偏移才是本页坐标。
            var origin = _carousel.ViewportOrigin();
            _lastStrokePoint = new Point(origin.X + point.X, origin.Y + point.Y);
        };
        _carousel.TagUndoRequested += StartUndoRedo;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape && (_paletteDragging || _dragFinishing))
            {
                CancelPaletteDrag();
                args.Handled = true;
                return;
            }
            if (!_batchMode || _paletteDragging || _dragFinishing || args.Handled || HasTagInputFocus()) return;
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

    /// <summary>
    /// 「清除」：清空这些图片的指定类型标签（scope 为空时清全部，含分级和分级来源），可以撤销。
    /// 只清指定类型时保留分级和它来源字段。
    /// </summary>
    private async Task ClearTagsAsync(IReadOnlyCollection<int> ids, string? scope)
    {
        if (_busy || _paintGesture || !_batchMode || !_active || AppService.IsShuttingDown || AppService.Store is not { } store) return;
        var targets = ids.Distinct().ToArray();
        // 空白处落笔不算一笔：不写库、不动撤销栈，也不弹提示。
        if (targets.Length == 0) return;
        var clearsAll = scope is null;
        var clearsRating = clearsAll || scope == "rating";
        SetBusy(true);
        try
        {
            var result = await Task.Run(() =>
            {
                var changes = new List<TagChange>();
                using var tx = store.Transaction();
                foreach (var id in targets)
                {
                    if (store.IngestRow(id) is not { State: "pending" } row) continue;
                    var before = IngestReview.Tags(row.Payload, row.Rating);
                    var keys = clearsAll ? before.Keys.ToArray() : [scope!];
                    var affected = keys.ToDictionary(key => key, key => before[key].ToList());
                    if (affected.Values.All(names => names.Count == 0)) continue;
                    var next = before.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
                    foreach (var key in affected.Keys) next[key] = [];
                    var rating = clearsRating ? "" : next["rating"].FirstOrDefault() ?? "";
                    var source = JsonNode.Parse(row.Payload)?["rating_source"]?.GetValue<string>() ?? "";
                    var payload = JsonNode.Parse(IngestReview.UpdatePayload(row.Payload, next, rating))!.AsObject();
                    // UpdatePayload 会把非空分级标成 manual；只清别的类型时把原来源写回去。
                    if (clearsRating) payload.Remove("rating_source");
                    else if (source.Length > 0) payload["rating_source"] = source;
                    else payload.Remove("rating_source");
                    store.UpdateIngestReview(id, payload.ToJsonString(), rating);
                    changes.Add(new(id, [], [], source, affected,
                        store.ReviewSkins(row.Payload).Select(s => s.SkinKey).ToArray(),
                        store.ReviewSkins(store.IngestRow(id)!.Payload).Select(s => s.SkinKey).ToArray()));
                }
                tx.Success = true;
                return changes;
            });
            if (result.Count == 0)
            {
                _status.Text = scope is null
                    ? "这些图片没有可清除的标签"
                    : $"这些图片没有可清除的{IngestReview.Titles[Array.IndexOf(IngestReview.Categories, scope)]}标签";
                return;
            }
            _paintUndo.Add(new TagStroke(ClearTag, result));
            if (_paintUndo.Count > 100) _paintUndo.RemoveAt(0);
            _paintRedo.Clear();
            foreach (var change in result)
            {
                ApplyRowTagUpdate(store, change.Id);
                ScatterClearedTags(change);
            }
            var what = scope is null ? "全部标签" : $"{IngestReview.Titles[Array.IndexOf(IngestReview.Categories, scope)]}标签";
            _status.Text = $"已清除 {result.Count} 张图片的{what}";
            UpdateWorkbenchState();
            _carousel.UpdateTagRows(_rows, true);
            RefreshDestination();
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { _status.Text = "清除标签失败，整笔未保存：" + ex.Message; }
        finally { SetBusy(false); UpdateButtons(); OpenDeferredNotification(); }
    }

    /// <summary>清除后的收尾动画：把这张图被清掉的那几类标签从卡片上一起飞向落点缩没。</summary>
    private void ScatterClearedTags(TagChange change)
    {
        if (!_batchMode || !_active || change.BeforeTags is null || change.BeforeTags.Count == 0) return;
        var labels = change.BeforeTags
            .SelectMany(pair => pair.Value.Select(name => new PaletteTag(pair.Key, name,
                pair.Key == "rating" ? RatingText.Level(name) : name)))
            .ToArray();
        if (labels.Length == 0) return;
        var box = _carousel.CardBox(change.Id);
        if (box is null) return;
        var target = _clearFlyTarget is { } point ? point : new Point(box.Value.X + box.Value.Width / 2, box.Value.Y + box.Value.Height / 2);
        _carousel.FlyTagScatter(labels.Select(tag =>
        {
            var colors = TagColors.Get(tag.Category, ActualTheme == ElementTheme.Dark);
            return new ReviewCarousel.ScatterRay([tag.Display], box.Value.X + box.Value.Width / 2, box.Value.Y + box.Value.Height * .46,
                target.X, target.Y, colors.Bg, colors.Border, colors.Fg);
        }).ToArray());
    }

    /// <summary>
    /// 「清除」的右键：撤销最近一次清除。ids 非空时只恢复这些图片（右键扫过哪些就恢复哪些），
    /// 为空时撤整笔（点「清除」标签本身）。最近一步不是清除时不动别的笔画。
    /// </summary>
    private void UndoLastClear(IReadOnlyCollection<int>? only)
    {
        if (_busy || _paintGesture || !_batchMode || !_active || AppService.IsShuttingDown) return;
        if (_paintUndo.Count == 0 || _paintUndo[^1].Tag.Category != ClearCategory)
        {
            _status.Text = "最近一步不是清除，没有可撤销的清除";
            return;
        }
        var history = _paintUndo[^1];
        if (only is null)
        {
            StartUndoRedo(false);
            return;
        }
        // 空白处右击没扫到任何图片：什么都不做，不能退化成撤整笔。
        if (only.Count == 0) return;
        var targets = only.ToHashSet();
        var subset = history.Changes.Where(change => targets.Contains(change.Id)).ToList();
        if (subset.Count == 0)
        {
            _status.Text = "扫过的图片不在最近一次清除里";
            return;
        }
        _paintTask = RestoreClearedAsync(history, false, subset);
    }

    private void StartUndoRedo(bool redo)
    {
        if (_busy || _paintGesture || !_batchMode || !_active || AppService.IsShuttingDown) return;
        var stack = redo ? _paintRedo : _paintUndo;
        if (stack.Count == 0) return;
        if (stack[^1].Tag.Category == ClearCategory) _paintTask = RestoreClearedAsync(stack[^1], redo);
        else _paintTask = ApplyTagStrokeAsync(stack[^1].Tag, [], false, stack[^1], redo);
    }

    private async Task ApplyTagStrokeAsync(PaletteTag tag, IReadOnlyCollection<int> ids, bool remove, TagStroke? history = null, bool redo = false, bool toggleSelection = false, Point? flyAnchor = null)
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
                    if (tag.Category == "character" && previous is not null)
                        payload["review_skins"] = JsonSerializer.SerializeToNode((redo ? previous.AfterSkins : previous.BeforeSkins) ?? []);
                    if (tag.Category != "rating" || history is not null && !redo)
                    {
                        var keepSource = tag.Category != "rating" ? source : previous!.RatingSource;
                        if (keepSource.Length > 0) payload["rating_source"] = keepSource;
                        else payload.Remove("rating_source");
                    }
                    store.UpdateIngestReview(id, payload.ToJsonString(), rating);
                    changes.Add(new(id, before, after.ToArray(), source, BeforeSkins:
                        store.ReviewSkins(row.Payload).Select(s => s.SkinKey).ToArray(), AfterSkins:
                        store.ReviewSkins(store.IngestRow(id)!.Payload).Select(s => s.SkinKey).ToArray()));
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
            // 点工作台标签时给每张卡片补「贴上／摘下」的动作；喷涂、撤销重做各有自己的动画，不重复播。
            if (flyAnchor is { } anchor)
                foreach (var change in result.changes)
                {
                    var added = change.After.Except(change.Before, StringComparer.OrdinalIgnoreCase)
                        .Select(name => LabelTag(tag.Category, name)).ToArray();
                    var removed = change.Before.Except(change.After, StringComparer.OrdinalIgnoreCase)
                        .Select(name => LabelTag(tag.Category, name)).ToArray();
                    if (added.Length > 0) FlyTagApply(change.Id, added, anchor);
                    if (removed.Length > 0) FlyTagDetach(change.Id, removed, anchor);
                }
            RefreshDestination();
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { _status.Text = (committed ? "标签已保存，但界面刷新失败：" : "标签操作失败，整笔未保存：") + ex.Message; }
        finally { SetBusy(false); UpdateButtons(); OpenDeferredNotification(); }
    }

    /// <summary>按分类把标签名补成工作台标签：分级显示成「普通／性感／暗示／露骨」，其余直接用原名。</summary>
    private static PaletteTag LabelTag(string category, string name) =>
        new(category, name, category == "rating" ? RatingText.Level(name) : name);

    private void LoadPalette()
    {
        if (!AppService.Settings.Settings.layout_options.TryGetValue("review_tag_palette", out var saved)) return;
        try
        {
            foreach (var tag in saved.Deserialize<List<PaletteTag>>() ?? [])
                if (tag.Category != ClearCategory && !string.IsNullOrWhiteSpace(tag.Name)
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
