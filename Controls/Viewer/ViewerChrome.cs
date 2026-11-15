using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 查看器共用度量、字形与配色。尺寸与颜色取自 Windows 照片 Lightbox 实测
/// （UIA 抓取为 200% 物理像素，此处已折算成 DIP），见 FILE_VIEWER.md。
/// </summary>
internal static class ViewerChrome
{
    public const double TitleBarHeight = 48;
    /// <summary>底部栏用 AppBarThemeCompactHeight：照片实测 32 DIP。</summary>
    public const double BarHeight = 32;
    public const double InfoPaneWidth = 362;
    public const double FilmstripItemWidth = 96;
    public const double FilmstripItemHeight = 72;
    public const double BarButtonSize = 32;
    public const double TitleButtonSize = 44;

    /// <summary>
    /// Photos 自带的图标字体（Photos Fluent Icons，286 字形）。App.xaml 里以
    /// <c>PhotosUWPFontFamily</c> 注册；取不到时退回系统 Fluent 图标。
    /// 注意 WinUI 的 FontFamily 不支持逗号回退列表，必须给单一族名。
    /// </summary>
    private static FontFamily? _iconFamily;

    public static FontFamily IconFamily
    {
        get
        {
            if (_iconFamily is not null) return _iconFamily;
            try
            {
                if (Application.Current?.Resources is { } resources &&
                    resources.TryGetValue("PhotosUWPFontFamily", out object value) &&
                    value is FontFamily family)
                    return _iconFamily = family;
            }
            catch (Exception)
            {
                // 资源未就绪时退回系统字体，不阻断窗口创建。
            }
            return _iconFamily = new FontFamily("Segoe Fluent Icons");
        }
    }

    /// <summary>
    /// 码位全部来自逆向物料：ViewerTitleBar.xbf / BottomBar.xbf / InfoPane.xbf /
    /// MediaViewerContextMenu.xbf 的字形串，以及 Photos-图标字形.csv。
    /// </summary>
    public static class Glyph
    {
        /// <summary>Photos-图标字形.csv: EditGlyph = U+EF77（照片 + 笔）。</summary>
        public const string Edit = "\uEF77";
        public const string Delete = "\uE74D";
        public const string More = "\uE712";
        public const string Info = "\uE946";
        public const string Fit = "\uE9A6";
        public const string ZoomOut = "\uE71F";
        public const string ZoomIn = "\uE8A3";
        /// <summary>BottomBar.xbf: FullscreenControlsFullscreenButton。</summary>
        public const string Fullscreen = "\uE740";
        public const string ExitFullscreen = "\uE740";
        public const string Previous = "\uE76B";
        public const string Next = "\uE76C";
        public const string Copy = "\uE8C8";
        public const string Rename = "\uE70F";
        /// <summary>像素尺寸：两个重叠的方框（对照照片底栏实测）。U+E7C4 不在该字体子集里。</summary>
        public const string Dimensions = "\uE8C8";
        public const string FileSize = "\uE74E";
        public const string Play = "\uE768";
        public const string Pause = "\uE769";
        public const string Volume = "\uE767";
        public const string Cast = "\uE8A7";
        public const string Close = "\uE711";
        public const string OpenFolder = "\uE838";
        public const string FileInfo = "\uE946";
        public const string Import = "\uE89D";
        /// <summary>MediaViewerContextMenu.xbf: SetAsAppBarButton = U+F75E。</summary>
        public const string SetAs = "\uF75E";
        /// <summary>SetAs_SetAsLockScreenMenuFlyoutItem = U+EE3F。</summary>
        public const string Lock = "\uEE3F";
        /// <summary>SetAs_SetAsBackgroundMenuFlyoutItem = U+E667。</summary>
        public const string Wallpaper = "\uE667";
        public const string Crop = "\uE7A8";
        public const string Rotate = "\uE7AD";
        /// <summary>水平翻转：方框中线两侧对称（字体里的专用字形）。</summary>
        public const string Flip = "\uF6EB";
        /// <summary>垂直翻转：波浪线 + 上下箭头。</summary>
        public const string FlipVertical = "\uF19C";
        /// <summary>ResizeAppBarButton / ResizeButton = U+E600。</summary>
        public const string Resize = "\uE600";
        public const string Undo = "\uE7A7";
        public const string Redo = "\uE7A6";
        public const string Adjust = "\uE790";
        /// <summary>Photos-图标字形.csv: FilterGlyph = U+E71C。</summary>
        public const string Filter = "\uE71C";
        public const string Pen = "\uE70F";
        public const string ChevronDown = "\uE70D";
        public const string EditPencil = "\uE70F";
        /// <summary>胶片条：带齿孔的圆角框（对照照片底栏实测，U+E8B2）。</summary>
        public const string Filmstrip = "\uE8B2";
        public const string Slideshow = "\uE786";
        public const string Save = "\uE792";
        public const string Print = "\uE749";
        public const string Share = "\uE72D";
        public const string Settings = "\uE713";
        public const string Feedback = "\uED15";
        public const string SubChevron = "\uE76C";
        public const string Refresh = "\uE72C";
        public const string Brightness = "\uE793";
        public const string Exposure = "\uE7B3";
        public const string Contrast = "\uE91B";
        public const string Highlights = "\uE9A6";
        public const string Shadows = "\uE8AB";
        public const string Blacks = "\uE7C4";
        public const string Saturation = "\uE790";
        public const string Warmth = "\uE706";
        public const string Tint = "\uE7C4";
        public const string Clarity = "\uE8E4";
        /// <summary>返回/后退箭头（照片 EditTitleBar 用 NavigationBackButtonNormalStyle）。</summary>
        public const string Back = "\uE72B";
    }

    /// <summary>Photos 深色为 #202020 内容底、#272729 工具条、#262729 侧板。</summary>
    public sealed class Palette
    {
        public Color Background { get; init; }
        public Color Bar { get; init; }
        public Color Pane { get; init; }
        public Color Panel { get; init; }
        public Color Text { get; init; }
        public Color SecondaryText { get; init; }
        public Color Divider { get; init; }
        public Color Hover { get; init; }
        public Color Pressed { get; init; }
        public Color SelectedBorder { get; init; }
        /// <summary>编辑页左侧按钮簇的底板，比工具条略亮。</summary>
        public Color Plate { get; init; }
        /// <summary>导航按钮静止态：亚克力半透明底（AcrylicInAppFillColorDefaultBrush）。</summary>
        public Color Acrylic { get; init; }
        /// <summary>导航按钮悬停态：ControlStrongFillColorDefaultBrush。</summary>
        public Color StrongFill { get; init; }

        public static Palette Dark { get; } = new()
        {
            Background = Color.FromArgb(255, 32, 32, 32),
            Bar = Color.FromArgb(255, 45, 45, 45),
            Pane = Color.FromArgb(255, 38, 39, 41),
            Panel = Color.FromArgb(255, 43, 43, 45),
            Text = Color.FromArgb(255, 255, 255, 255),
            SecondaryText = Color.FromArgb(255, 197, 197, 197),
            Divider = Color.FromArgb(255, 58, 58, 60),
            Hover = Color.FromArgb(23, 255, 255, 255),
            Pressed = Color.FromArgb(38, 255, 255, 255),
            SelectedBorder = Color.FromArgb(255, 255, 255, 255),
            Plate = Color.FromArgb(255, 51, 51, 51),
            Acrylic = Color.FromArgb(26, 255, 255, 255),
            StrongFill = Color.FromArgb(255, 69, 69, 69),
        };

        public static Palette Light { get; } = new()
        {
            Background = Color.FromArgb(255, 243, 243, 243),
            Bar = Color.FromArgb(255, 249, 249, 249),
            Pane = Color.FromArgb(255, 250, 250, 250),
            Panel = Color.FromArgb(255, 255, 255, 255),
            Text = Color.FromArgb(255, 26, 26, 26),
            SecondaryText = Color.FromArgb(255, 93, 93, 93),
            Divider = Color.FromArgb(255, 226, 226, 226),
            Hover = Color.FromArgb(18, 0, 0, 0),
            Pressed = Color.FromArgb(30, 0, 0, 0),
            SelectedBorder = Color.FromArgb(255, 26, 26, 26),
            Plate = Color.FromArgb(255, 235, 235, 235),
            Acrylic = Color.FromArgb(30, 0, 0, 0),
            StrongFill = Color.FromArgb(255, 227, 227, 227),
        };

        public static Palette For(ElementTheme theme) =>
            theme == ElementTheme.Light ? Light : Dark;
    }

    public static SolidColorBrush Brush(Color color) => new(color);

    public static SolidColorBrush Transparent() =>
        new(Color.FromArgb(0, 0, 0, 0));

    public static FontIcon Icon(string glyph, double size, Color color)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = size,
            Foreground = Brush(color),
            FontFamily = IconFamily,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        return icon;
    }

    /// <summary>Photos 工具条按钮：无底色、悬停淡填充、按下更深；悬停/按下由这里显式控制。</summary>
    public static Button IconButton(string glyph, string tooltip, Palette palette,
        double size = BarButtonSize, double glyphSize = 16)
    {
        var button = new Button
        {
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Transparent(),
            Content = Icon(glyph, glyphSize, palette.Text),
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        AttachHover(button, palette);
        return button;
    }

    public static TextBlock Label(string text, double size, Color color,
        Windows.UI.Text.FontWeight? weight = null, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = Brush(color),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (weight is { } w) block.FontWeight = w;
        return block;
    }

    /// <summary>按钮模板的悬停态会覆盖本地 Background，因此手动接管指针状态。</summary>
    public static void AttachHover(Button button, Palette palette)
    {
        var normal = Transparent();
        var hover = Brush(palette.Hover);
        var pressed = Brush(palette.Pressed);
        WirePressMotion(button);
        button.PointerEntered += (_, _) => button.Background = hover;
        button.PointerExited += (_, _) => button.Background = normal;
        button.PointerPressed += (_, _) => button.Background = pressed;
        button.PointerReleased += (_, _) => button.Background = hover;
        button.PointerCaptureLost += (_, _) => button.Background = normal;
    }

    public static void WirePressMotion(Button button)
    {
        void Animate(float target)
        {
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(button);
            visual.CenterPoint = new System.Numerics.Vector3((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, 0);
            var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(1, new System.Numerics.Vector3(target, target, 1));
            animation.Duration = TimeSpan.FromMilliseconds(120);
            visual.StartAnimation("Scale", animation);
        }
        button.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Animate(.92f)), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Animate(1)), true);
        button.PointerCaptureLost += (_, _) => Animate(1);
        button.PointerExited += (_, _) => Animate(1);
    }

    /// <summary>竖线分隔，Photos 底部文件信息组之间用 1px 细线。</summary>
    public static Border Divider(Palette palette, double height = 16)
    {
        return new Border
        {
            Width = 1,
            Height = height,
            Background = Brush(palette.Divider),
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }


    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
        return Math.Max(1, bytes / 1024.0).ToString("0.#") + " KB";
    }
}
