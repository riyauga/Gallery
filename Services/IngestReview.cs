using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gallery.Services;

/// <summary>待归档记录的路径与六类可手工编辑标签；顺序与 <see cref="Taxonomy.Categories"/> 一致（去掉自动识别的分辨率）。</summary>
public static class IngestReview
{
    public static readonly string[] Categories = ["work", "character", "artist", "kind", "rating", "custom"];
    public static readonly string[] Titles = ["作品", "角色", "画师", "类型", "分级", "自定义"];

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

    /// <summary>展示/加载预览用的路径：历史行优先用解析出的当前位置，其余沿用记录路径。</summary>
    public static string CurrentPath(StoreService.PendingIngestRow row)
        => row.ResolvedPath ?? LocalPath(row);

    /// <summary>
    /// 历史记录的文件当前位置：先按归档时记下的图片 id 找图库记录，再按原路径，最后按文件名唯一匹配。
    /// 找不到或同名不唯一时返回空串，不做猜测。
    /// </summary>
    public static string ResolveCurrentPath(StoreService store, StoreService.PendingIngestRow row)
    {
        var recorded = LocalPath(row);

        // 新记录优先信归档时的图片 id：即使原路径上又出现同名文件，也不会认错。
        var imageId = row.State == "done" ? ArchivedImageId(row.Payload) : 0;
        if (imageId > 0)
        {
            var byId = store.QueryOne("SELECT * FROM images WHERE id=$i", ("$i", imageId));
            if (byId is { Missing: false } && File.Exists(byId.AbsPath)) return byId.AbsPath;
        }

        if (recorded.Length > 0 && File.Exists(recorded)) return recorded;
        if (row.State != "done") return "";

        if (recorded.Length > 0)
        {
            var byPath = store.QueryOne(
                "SELECT * FROM images WHERE abs_path=$a COLLATE NOCASE AND missing=0",
                ("$a", recorded));
            if (byPath is not null && File.Exists(byPath.AbsPath)) return byPath.AbsPath;
        }

        var name = recorded.Length > 0 ? Path.GetFileName(recorded) : "";
        if (name.Length == 0) return "";
        var matches = store.QueryAll(
                "SELECT * FROM images WHERE name=$n COLLATE NOCASE AND missing=0",
                ("$n", name))
            .Where(candidate => File.Exists(candidate.AbsPath))
            .ToList();
        return matches.Count == 1 ? matches[0].AbsPath : "";
    }

    /// <summary>归档成功时把图片 id 补进 payload；解析不了原 payload 就原样返回。</summary>
    public static string WithArchivedImage(string payload, int imageId)
    {
        if (imageId <= 0) return payload;
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject root) return payload;
            root["archived_image_id"] = imageId;
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return payload;
        }
    }

    private static int ArchivedImageId(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("archived_image_id", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var id)
                ? id
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
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
