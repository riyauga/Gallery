using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gallery.Services;

/// <summary>待归档记录的路径与六类标签；保留扩展原始字段，人工修订单独保存并供归档使用。</summary>
public static class IngestReview
{
    public static readonly string[] Categories = ["artist", "work", "character", "kind", "rating", "custom"];
    public static readonly string[] Titles = ["画师", "作品", "角色", "类型", "分级", "自定义"];

    /// <summary>兼容扩展 filename 与旧协议 inbox_path，不猜测文件的移动位置。</summary>
    public static string LocalPath(StoreService.PendingIngestRow row)
    {
        if (row.State == "done") return CanonicalPath(row.Note);
        if (!string.IsNullOrWhiteSpace(row.InboxPath)) return CanonicalPath(row.InboxPath);
        try
        {
            using var doc = JsonDocument.Parse(row.Payload);
            if (doc.RootElement.TryGetProperty("filename", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var path = value.GetString() ?? "";
                return CanonicalPath(path);
            }
        }
        catch (JsonException) { }
        return "";
    }

    private static string CanonicalPath(string path)
    {
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : ""; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    public static Dictionary<string, List<string>> Tags(string payload, string rating)
    {
        var result = Categories.ToDictionary(c => c, _ => new List<string>());
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("review_tags", out var reviewed) && reviewed.ValueKind == JsonValueKind.Object)
            {
                foreach (var category in Categories)
                    result[category].AddRange(ReadNames(reviewed, category));
            }
            else if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                result["artist"].AddRange(ReadNames(tags, "artist"));
                result["work"].AddRange(ReadNames(tags, "copyright").Concat(ReadNames(tags, "work")));
                result["character"].AddRange(ReadNames(tags, "character"));
                result["custom"].AddRange(ReadNames(tags, "custom"));
                result["kind"].AddRange(ReadNames(tags, "kind"));
                result["kind"].AddRange(Taxonomy.KindTagsFrom(ReadNames(tags, "general").Concat(ReadNames(tags, "meta")),
                    Taxonomy.LoadKindWhitelist(AppService.Settings.Settings.kind_whitelist_path)));
            }
        }
        catch (JsonException) { }
        result["rating"].Clear();
        var normalized = ArchiveService.NormalizeRating(rating);
        if (normalized.Length > 0) result["rating"].Add(normalized);
        return result.ToDictionary(p => p.Key, p => p.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public static string UpdatePayload(string payload, Dictionary<string, List<string>> tags, string rating)
    {
        var root = JsonNode.Parse(payload) as JsonObject
            ?? throw new JsonException("待归档记录不是 JSON 对象");
        var reviewed = new JsonObject();
        foreach (var category in Categories)
            reviewed[category] = new JsonArray(tags[category].Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        root["review_tags"] = reviewed;
        root["rating"] = rating;
        return root.ToJsonString();
    }

    private static IEnumerable<string> ReadNames(JsonElement tags, string key)
    {
        if (!tags.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in array.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } name)
                yield return name;
    }
}
