using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls.Tags;

/// <summary>
/// 七类标签配色，逐值照搬 galleryold <c>app/ui/tag_chips.py:15-23</c>。
/// 每类（浅色底,浅色边框,浅色文字）/（深色底,深色边框,深色文字）。
/// </summary>
public static class TagColors
{
    public static readonly IReadOnlyDictionary<string, (string Bg, string Border, string Fg)[]> Table =
        new Dictionary<string, (string, string, string)[]>
        {
            ["work"] = [("#eaf1fb", "#b4c9e7", "#29496e"), ("#26364a", "#506d91", "#cfdef3")],
            ["character"] = [("#f2ecfa", "#cfbce7", "#614080"), ("#3a2e48", "#80639c", "#e5d4f3")],
            ["artist"] = [("#fbeceb", "#e4bab5", "#843f38"), ("#472f2f", "#9c6460", "#f3d3cf")],
            ["kind"] = [("#eaf3eb", "#b5d0b8", "#3c6442"), ("#2d3d30", "#64856a", "#d3e8d6")],
            ["rating"] = [("#fbf2df", "#ddca9d", "#785e28"), ("#453b28", "#95804f", "#efe0b9")],
            ["resolution"] = [("#e5f3f4", "#a8ced2", "#32666c"), ("#293e41", "#577f85", "#cbe7ea")],
            ["custom"] = [("#f7f7f4", "#ddddd6", "#565650"), ("#5c5a55", "#9c9a93", "#f1f0eb")],
        };

    public static (string Bg, string Border, string Fg) Get(string category, bool dark)
    {
        var pair = Table.GetValueOrDefault(category, Table["custom"]);
        return dark ? pair[1] : pair[0];
    }

    public static Windows.UI.Color FromHex(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        if (hex.TrimStart('#').Length == 6) v |= 0xFF000000;
        return Microsoft.UI.ColorHelper.FromArgb(
            (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF),
            (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }
}

/// <summary>
/// 标签贴纸，照搬 tag_chips.py：高 28、圆角 6、1px 边框、字号 13、文字左右 8；
/// 显示名优先中文别名（调用方传入）。
/// 传了 onRemove 时，贴纸左边多一个叉号：平时半透明，鼠标移上去亮起，点它取消这个标签。
/// </summary>
public sealed class TagChip : Grid
{
    public TagChip(string category, string displayName, Action? onRemove = null)
    {
        var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var (bg, border, fg) = TagColors.Get(category, dark);

        Height = 28;
        VerticalAlignment = VerticalAlignment.Top;
        var text = new TextBlock
        {
            Text = displayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(TagColors.FromHex(fg)),
        };
        UIElement content = text;
        var leftPadding = 8.0;
        if (onRemove is not null)
        {
            var remove = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
                Width = 18,
                Height = 18,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.55,
            };
            remove.PointerEntered += (_, _) => remove.Opacity = 1;
            remove.PointerExited += (_, _) => remove.Opacity = 0.55;
            remove.Click += (_, _) => onRemove();
            ToolTipService.SetToolTip(remove, "移除此标签");
            AutomationProperties.SetName(remove, $"移除标签 {displayName}");
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Children = { remove, text },
            };
            leftPadding = 2;
        }

        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(TagColors.FromHex(bg)),
            BorderBrush = new SolidColorBrush(TagColors.FromHex(border)),
            Padding = new Thickness(leftPadding, 0, 8, 0),
            Child = content,
        };
        Children.Add(frame);
        void ApplyTheme()
        {
            var colors = TagColors.Get(category, ActualTheme == ElementTheme.Dark);
            frame.Background = new SolidColorBrush(TagColors.FromHex(colors.Bg));
            frame.BorderBrush = new SolidColorBrush(TagColors.FromHex(colors.Border));
            text.Foreground = new SolidColorBrush(TagColors.FromHex(colors.Fg));
        }
        Loaded += (_, _) => ApplyTheme();
        ActualThemeChanged += (_, _) => ApplyTheme();
    }
}
