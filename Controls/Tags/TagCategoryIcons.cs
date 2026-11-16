namespace Gallery.Controls.Tags;

/// <summary>标签工作台和菜单共用的分类图标。</summary>
public static class TagCategoryIcons
{
    public static string Glyph(string category) => category switch
    {
        "work" => "\uE91B",
        "character" => "\uE77B",
        "artist" => "\uE76D",
        "kind" => "\uF0E2",
        "rating" => "R",
        "custom" => "\uE710",
        _ => "\uE8EC",
    };
}
