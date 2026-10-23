using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Gallery.Services;

/// <summary>七类标签编解码，移植 galleryold <c>app/taxonomy.py</c>。</summary>
public static class Taxonomy
{
    public static readonly string[] Categories =
        ["work", "character", "artist", "kind", "rating", "resolution", "custom"];

    public static readonly string[] CategoryTitles =
        ["作品", "角色", "作者", "类型", "分级", "分辨率", "自定义"];

    public static readonly IReadOnlyDictionary<string, string> SidecarPrefix =
        new Dictionary<string, string>
        {
            ["work"] = "作品", ["character"] = "角色", ["artist"] = "作者",
            ["kind"] = "类型", ["rating"] = "分级", ["resolution"] = "分辨率",
            ["custom"] = "",
        };

    public static readonly IReadOnlyDictionary<string, string> DanbooruCategoryMap =
        new Dictionary<string, string>
        {
            ["artist"] = "artist", ["copyright"] = "work", ["character"] = "character",
            ["general"] = "kind", ["meta"] = "kind",
        };


    // ------------------------------------------------------------------ //
    // 画面类型白名单（对齐 galleryold taxonomy.DEFAULT_KIND_WHITELIST）
    // 中位数帖子有 ~30 个 general 标签，全收进来「类型」就没法看，
    // 所以只挑「构图 / 画面性质」这一小组，映射成一个中文类型标签。
    // ------------------------------------------------------------------ //
    public static readonly IReadOnlyDictionary<string, string[]> DefaultKindWhitelist =
        new Dictionary<string, string[]>
        {
            ["单人"] = ["solo"],
            ["多人"] = ["2girls", "2boys", "multiple_girls", "multiple_boys", "6+girls"],
            ["风景"] = ["scenery", "landscape", "no_humans"],
            ["肖像"] = ["portrait"],
            ["全身"] = ["full_body"],
            ["半身"] = ["upper_body", "cowboy_shot"],
            ["黑白"] = ["monochrome", "greyscale"],
            ["线稿"] = ["sketch", "lineart"],
            ["AI生成"] = ["ai-generated", "ai-assisted"],
            ["有文字"] = ["english_text", "translated", "commentary"],
        };

    /// <summary>danbooru 标签（小写） → 中文类型标签。</summary>
    public static Dictionary<string, string> BuildKindLookup(
        IReadOnlyDictionary<string, string[]>? whitelist = null)
    {
        var table = whitelist ?? DefaultKindWhitelist;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (zh, tags) in table)
            foreach (var tag in tags)
            {
                var key = tag.Trim().ToLowerInvariant();
                if (key.Length > 0) map.TryAdd(key, zh);
            }
        return map;
    }

    /// <summary>从 general/meta 标签里挑出白名单命中的类型标签，顺序固定。</summary>
    public static List<string> KindTagsFrom(
        IEnumerable<string>? generalTags,
        IReadOnlyDictionary<string, string[]>? whitelist = null)
    {
        var map = BuildKindLookup(whitelist);
        var order = (whitelist ?? DefaultKindWhitelist).Keys.ToList();
        var hit = new List<string>();
        foreach (var raw in generalTags ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!map.TryGetValue(raw.Trim().ToLowerInvariant(), out var zh)) continue;
            if (!hit.Contains(zh)) hit.Add(zh);
        }
        return order.Where(hit.Contains).Concat(hit.Where(h => !order.Contains(h))).ToList();
    }

    /// <summary>读用户可编辑的白名单；文件缺失或损坏时退回默认值。</summary>
    public static IReadOnlyDictionary<string, string[]> LoadKindWhitelist(string? path)
    {
        var target = string.IsNullOrWhiteSpace(path) ? AppPaths.KindWhitelistPath : path;
        try
        {
            if (!File.Exists(target)) return DefaultKindWhitelist;
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(target));
            if (raw is null || raw.Count == 0) return DefaultKindWhitelist;
            var map = new Dictionary<string, string[]>();
            foreach (var (zh, value) in raw)
            {
                var tags = value.ValueKind switch
                {
                    JsonValueKind.String => [value.GetString() ?? ""],
                    JsonValueKind.Array => value.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString() ?? "")
                        .ToArray(),
                    _ => [],
                };
                var clean = tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
                if (clean.Length > 0) map[zh] = clean;
            }
            return map.Count > 0 ? map : DefaultKindWhitelist;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return DefaultKindWhitelist;
        }
    }

    /// <summary>把白名单写到磁盘（先写 .tmp 再替换，避免写一半）。</summary>
    public static void SaveKindWhitelist(
        string? path, IReadOnlyDictionary<string, string[]> whitelist)
    {
        var target = string.IsNullOrWhiteSpace(path) ? AppPaths.KindWhitelistPath : path;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var payload = whitelist.ToDictionary(kv => kv.Key, kv => kv.Value);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var temp = target + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, target, overwrite: true);
    }

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
        // 「画师」已改名为「作者」：旧导出文本里的老前缀仍按作者解析，避免落进自定义。
        if (head == "画师") return ("artist", s[(idx + 1)..].Trim());
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
