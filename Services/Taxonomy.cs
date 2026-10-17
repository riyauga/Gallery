namespace Gallery.Services;

/// <summary>七类标签编解码，移植 galleryold <c>app/taxonomy.py</c>。</summary>
public static class Taxonomy
{
    public static readonly string[] Categories =
        ["work", "character", "artist", "kind", "rating", "resolution", "custom"];

    public static readonly string[] CategoryTitles =
        ["作品", "角色", "画师", "类型", "分级", "分辨率", "自定义"];

    public static readonly IReadOnlyDictionary<string, string> SidecarPrefix =
        new Dictionary<string, string>
        {
            ["work"] = "作品", ["character"] = "角色", ["artist"] = "画师",
            ["kind"] = "类型", ["rating"] = "分级", ["resolution"] = "分辨率",
            ["custom"] = "",
        };

    public static readonly IReadOnlyDictionary<string, string> DanbooruCategoryMap =
        new Dictionary<string, string>
        {
            ["artist"] = "artist", ["copyright"] = "work", ["character"] = "character",
            ["general"] = "kind", ["meta"] = "kind",
        };

    public static bool IsValidCategory(string c) => SidecarPrefix.ContainsKey(c);

    /// <summary>('work','x') → '作品/x'；custom 或名字含分隔符 → 原样。</summary>
    public static string FullName(string category, string name)
    {
        var prefix = SidecarPrefix.GetValueOrDefault(category ?? "custom", "");
        var clean = (name ?? "").Trim();
        if (prefix.Length == 0 || clean.IndexOfAny(['/', '\\']) >= 0) return clean;
        return $"{prefix}/{clean}";
    }

    /// <summary>'作品/x' → ('work','x')；无前缀 → custom。</summary>
    public static (string Category, string Name) SplitFullName(string text)
    {
        var s = (text ?? "").Trim();
        var idx = s.IndexOf('/');
        if (idx <= 0) return ("custom", s);
        var head = s[..idx];
        foreach (var (cat, prefix) in SidecarPrefix)
        {
            if (prefix.Length > 0 && head == prefix)
                return (cat, s[(idx + 1)..].Trim());
        }
        return ("custom", s);
    }

    public static string OrientationTag(int? width, int? height)
    {
        if (width is null || height is null || width <= 0 || height <= 0) return "";
        var ratio = (double)width.Value / height.Value;
        if (ratio > 1.15) return "横图";
        if (ratio < 0.87) return "竖图";
        return "方图";
    }

    public static string TierTag(int? width, int? height)
    {
        if (width is null || height is null || width <= 0 || height <= 0) return "";
        return $"{width}x{height}";
    }

    public static List<string> ResolutionTags(int? width, int? height)
    {
        var o = OrientationTag(width, height);
        var t = TierTag(width, height);
        var list = new List<string>(2);
        if (o.Length > 0) list.Add(o);
        if (t.Length > 0) list.Add(t);
        return list;
    }
}
