namespace Gallery.Controls.Tags;

/// <summary>标签工作台和菜单共用的分类图标。</summary>
public static class TagCategoryIcons
{
    public static Microsoft.UI.Xaml.Controls.IconElement Create(string category, double size = 20)
    {
        if (category == "rating")
        {
            // 外圈与同组轮廓图标对齐，圈内 R 在菜单和工作台共用同一形状。
            var icon = (Microsoft.UI.Xaml.Controls.PathIcon)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                <PathIcon xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="20" Height="20"
                    Data="F0 M19,10 A9,9 0 1 1 1,10 A9,9 0 1 1 19,10 Z M17.5,10 A7.5,7.5 0 1 1 2.5,10 A7.5,7.5 0 1 1 17.5,10 Z M6.2,5 L10,5 C12.8,5 14,6.2 14,8.2 C14,9.7 13.2,10.7 11.9,11 L14,15 L12.4,15 L10.4,11.3 L7.6,11.3 L7.6,15 L6.2,15 Z M7.6,6.4 L7.6,9.9 L10,9.9 C11.8,9.9 12.6,9.4 12.6,8.2 C12.6,6.9 11.8,6.4 10,6.4 Z" />
                """);
            icon.Width = icon.Height = size;
            if (size != 20)
                icon.Data.Transform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = size / 20, ScaleY = size / 20 };
            return icon;
        }
        return new Microsoft.UI.Xaml.Controls.FontIcon
        {
            Glyph = Glyph(category), FontSize = size,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
            Width = size, Height = size, IsTextScaleFactorEnabled = false,
        };
    }

    private static string Glyph(string category) => category switch
    {
        "work" => "\uE91B",
        "character" => "\uE77B",
        "artist" => "\uE76D",
        "kind" => "\uF0E2",
        "custom" => "\uE710",
        _ => "\uE8EC",
    };
}
