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
    public const double BarHeight = 48;
    public const double InfoPaneWidth = 362;
    public const double FilmstripItemWidth = 96;
    public const double FilmstripItemHeight = 72;
    public const double BarButtonSize = 40;
    public const double TitleButtonSize = 44;

    /// <summary>Photos 用同一族名的图标字体；系统字体名在 Fluent 与 MDL2 间回退。</summary>
    public const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public static class Glyph
    {
        public const string Delete = "\uE74D";
        public const string More = "\uE712";
        public const string Info = "\uE946";
        public const string Fit = "\uE9A6";
        public const string ZoomOut = "\uE71F";
        public const string ZoomIn = "\uE8A3";
        public const string Fullscreen = "\uE92D";
        public const string ExitFullscreen = "\uE92C";
        public const string Previous = "\uE76B";
        public const string Next = "\uE76C";
        public const string Copy = "\uE8C8";
        public const string Rename = "\uE8AC";
        public const string Dimensions = "\uE7C4";
        public const string FileSize = "\uE74E";
        public const string Play = "\uE768";
        public const string Pause = "\uE769";
        public const string Volume = "\uE767";
        public const string Cast = "\uE8A7";
        public const string Close = "\uE711";
        public const string OpenFolder = "\uE838";
        public const string FileInfo = "\uE946";
        public const string Import = "\uE8B5";
        public const string SetAs = "\uE793";
        public const string Lock = "\uE72E";
        public const string Wallpaper = "\uE91B";
        public const string EditPencil = "\uE70F";
        public const string Filmstrip = "\uE786";
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

        public static Palette Dark { get; } = new()
        {
            Background = Color.FromArgb(255, 32, 32, 32),
            Bar = Color.FromArgb(255, 39, 39, 41),
            Pane = Color.FromArgb(255, 38, 39, 41),
            Panel = Color.FromArgb(255, 43, 43, 45),
            Text = Color.FromArgb(255, 255, 255, 255),
            SecondaryText = Color.FromArgb(255, 197, 197, 197),
            Divider = Color.FromArgb(255, 58, 58, 60),
            Hover = Color.FromArgb(23, 255, 255, 255),
            Pressed = Color.FromArgb(38, 255, 255, 255),
            SelectedBorder = Color.FromArgb(255, 255, 255, 255),
        };

        public static Palette Light { get; } = new()
        {
            Background = Color.FromArgb(255, 243, 243, 243),
            Bar = Color.FromArgb(255, 250, 250, 250),
            Pane = Color.FromArgb(255, 250, 250, 250),
            Panel = Color.FromArgb(255, 255, 255, 255),
            Text = Color.FromArgb(255, 26, 26, 26),
            SecondaryText = Color.FromArgb(255, 93, 93, 93),
            Divider = Color.FromArgb(255, 226, 226, 226),
            Hover = Color.FromArgb(18, 0, 0, 0),
            Pressed = Color.FromArgb(30, 0, 0, 0),
            SelectedBorder = Color.FromArgb(255, 26, 26, 26),
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
            FontFamily = new FontFamily(IconFont),
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
        button.PointerEntered += (_, _) => button.Background = hover;
        button.PointerExited += (_, _) => button.Background = normal;
        button.PointerPressed += (_, _) => button.Background = pressed;
        button.PointerReleased += (_, _) => button.Background = hover;
        button.PointerCaptureLost += (_, _) => button.Background = normal;
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

    /// <summary>Photos 的胶片条图标是专用字形，Segoe 里没有；按截图手绘：圆角框 + 底部五个小格。</summary>
    public static Grid FilmstripIcon(Color color, double width = 16)
    {
        var grid = new Grid { Width = width, Height = Math.Round(width * 0.82) };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(2.5),
            BorderThickness = new Thickness(1.2),
            BorderBrush = Brush(color),
        });
        var frames = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2.6),
        };
        for (int index = 0; index < 5; index++)
            frames.Children.Add(new Border { Width = 1.7, Height = 1.7, Background = Brush(color) });
        grid.Children.Add(frames);
        return grid;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
        return Math.Max(1, bytes / 1024) + " KB";
    }
}
