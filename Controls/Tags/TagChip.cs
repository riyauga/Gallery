using Microsoft.UI.Xaml;
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
            ["custom"] = [("#efedf3", "#c8c2d2", "#595064"), ("#38333f", "#797081", "#e0d9e8")],
        };

    public static (string Bg, string Border, string Fg) Get(string category, bool dark)
    {
        var pair = Table.GetValueOrDefault(category, Table["custom"]);
        return dark ? pair[1] : pair[0];
    }

    public static Windows.UI.Color FromHex(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Microsoft.UI.ColorHelper.FromArgb(
            (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF),
            (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }
}

/// <summary>
/// 标签贴纸，照搬 tag_chips.py：高 28、圆角 6、1px 边框、字号 13、文字左右 8；
/// 显示名优先中文别名（调用方传入）。
/// </summary>
public sealed class TagChip : Grid
{
    public TagChip(string category, string displayName)
    {
        var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var (bg, border, fg) = TagColors.Get(category, dark);

        Height = 28;
        VerticalAlignment = VerticalAlignment.Top;
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(TagColors.FromHex(bg)),
            BorderBrush = new SolidColorBrush(TagColors.FromHex(border)),
            Padding = new Thickness(8, 0, 8, 0),
            Child = new TextBlock
            {
                Text = displayName,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(TagColors.FromHex(fg)),
            },
        });
    }
}
