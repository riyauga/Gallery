using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gallery.Services;

/// <summary>待归档记录的路径与六类可手工编辑标签；顺序与 <see cref="Taxonomy.Categories"/> 一致（去掉自动识别的分辨率）。</summary>
public static class IngestReview
{
    public static readonly string[] Categories = ["work", "character", "artist", "kind", "rating", "custom"];
    public static readonly string[] Titles = ["作品", "角色", "作者", "类型", "分级", "自定义"];

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

    public static int ArchivedImageId(string payload)
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

    /// <summary>队列里是否已经有入队时生成的分辨率/横竖图；没有就需要补一次。</summary>
    public static bool NeedsResolution(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("width", out var widthValue) || !widthValue.TryGetInt32(out var width) || width <= 0)
                return true;
            if (!root.TryGetProperty("height", out var heightValue) || !heightValue.TryGetInt32(out var height) || height <= 0)
                return true;
            if (!root.TryGetProperty("basic_tags", out var basics) || basics.ValueKind != JsonValueKind.Object)
                return true;
            if (!basics.TryGetProperty("resolution", out var resolution) || resolution.ValueKind != JsonValueKind.Array)
                return true;
            return resolution.GetArrayLength() < 2;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>把入队时算出的分辨率/横竖图写进 payload；不改其他标签。</summary>
    public static string WithResolution(string payload, int width, int height)
    {
        if (width <= 0 || height <= 0) return payload;
        if (JsonNode.Parse(payload) is not JsonObject root) return payload;
        root["width"] = width;
        root["height"] = height;
        var basics = root["basic_tags"] as JsonObject ?? new JsonObject();
        basics["resolution"] = new JsonArray(Taxonomy.ResolutionTags(width, height)
            .Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray());
        root["basic_tags"] = basics;
        return root.ToJsonString();
    }

    /// <summary>写分级和来源；只在调用方确认当前分级为空时使用。</summary>
    public static string WithRating(string payload, string rating, string source)
    {
        if (JsonNode.Parse(payload) is not JsonObject root) return payload;
        root["rating"] = rating;
        root["rating_source"] = source;
        var basics = root["basic_tags"] as JsonObject ?? new JsonObject();
        basics["rating_checked"] = true;
        root["basic_tags"] = basics;
        return root.ToJsonString();
    }

    /// <summary>
    /// 「AI分析」和归档前补跑的写回：标签与分级按模型结果落盘，画师、自定义和来源字段不动。
    /// 调用方决定角色/作品是否顶替（模型没给结果就保留原值），这里只负责写入并打上已处理标记。
    /// </summary>
    public static string ApplyModelOverwrite(string payload, Dictionary<string, List<string>> tags,
        string rating, PixaiScore score)
    {
        var root = JsonNode.Parse(payload) as JsonObject
            ?? throw new JsonException("待归档记录不是 JSON 对象");
        var reviewed = new JsonObject();
        foreach (var category in Categories)
            reviewed[category] = new JsonArray(tags[category].Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        root["review_tags"] = reviewed;
        root["rating"] = rating;
        if (rating.Length > 0) root["rating_source"] = "model";
        var basics = root["basic_tags"] as JsonObject ?? new JsonObject();
        basics["model_tags_checked"] = true;
        basics["rating_checked"] = true;
        basics["model_tags_model"] = score.ModelName;
        basics["model_tags_hash"] = score.ModelHash;
        basics["model_character"] = score.Character;
        basics["model_character_score"] = Math.Round(score.CharacterScore, 3);
        basics["model_work"] = score.Work;
        basics["pixai_character_candidate"] = score.CharacterCandidate;
        basics["pixai_character_score"] = Math.Round(score.CharacterScore, 3);
        basics["pixai_work_candidate"] = score.WorkCandidate;
        basics["pixai_work_score"] = Math.Round(score.WorkScore, 3);
        root["basic_tags"] = basics;
        return root.ToJsonString();
    }

    /// <summary>标记「分级已处理过」，阻止后台模型在用户清空分级后又填回来。</summary>
    public static string WithRatingChecked(string payload)
    {
        if (JsonNode.Parse(payload) is not JsonObject root) return payload;
        var basics = root["basic_tags"] as JsonObject ?? new JsonObject();
        basics["rating_checked"] = true;
        root["basic_tags"] = basics;
        return root.ToJsonString();
    }

    /// <summary>模型已经尝试过这条记录的分级；用户之后清空分级也不再自动补回。</summary>
    public static bool RatingChecked(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("basic_tags", out var basics)
                && basics.ValueKind == JsonValueKind.Object
                && basics.TryGetProperty("rating_checked", out var checkedValue)
                && checkedValue.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>模型已经尝试过这条记录的角色/类型标签；用户之后手改也不会自动补回。</summary>
    public static bool ModelTagsChecked(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("basic_tags", out var basics)
                && basics.ValueKind == JsonValueKind.Object
                && basics.TryGetProperty("model_tags_checked", out var checkedValue)
                && checkedValue.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把模型结果补进 review_tags：角色/作品只在原分类为空时补，类型去重追加；
    /// 同时写入 model_tags_checked，保证同一条记录不会反复跑模型。
    /// </summary>
    public static string WithModelTags(string payload, string character, string work,
        IReadOnlyList<string> kinds, bool applyCharacter, bool applyWork, bool applyKind,
        bool applyRatingTag, string modelName, string modelHash, double characterScore, PixaiScore? pixai = null)
    {
        var root = JsonNode.Parse(payload) as JsonObject
            ?? throw new JsonException("待归档记录不是 JSON 对象");
        var tags = Tags(payload, RatingOf(payload));

        var addedCharacter = false;
        if (applyCharacter && character.Length > 0 && tags["character"].Count == 0)
        {
            tags["character"].Add(character);
            addedCharacter = true;
        }

        var addedWork = false;
        if (applyWork && work.Length > 0 && tags["work"].Count == 0)
        {
            tags["work"].Add(work);
            addedWork = true;
        }

        if (applyKind)
        {
            foreach (var kind in kinds)
            {
                if (kind.Length > 0 && !tags["kind"].Contains(kind, StringComparer.OrdinalIgnoreCase))
                    tags["kind"].Add(kind);
            }
        }

        var reviewed = new JsonObject();
        foreach (var category in Categories)
            reviewed[category] = new JsonArray(tags[category]
                .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        root["review_tags"] = reviewed;

        var basics = root["basic_tags"] as JsonObject ?? new JsonObject();
        basics["model_tags_checked"] = true;
        basics["rating_tag"] = applyRatingTag;
        if (modelName.Length > 0) basics["model_tags_model"] = modelName;
        if (modelHash.Length > 0) basics["model_tags_hash"] = modelHash;
        if (addedCharacter)
        {
            basics["model_character"] = character;
            basics["model_character_score"] = Math.Round(characterScore, 3);
        }
        if (addedWork) basics["model_work"] = work;
        if (applyKind)
        {
            basics["model_kind"] = new JsonArray(kinds
                .Where(kind => kind.Length > 0)
                .Select(kind => (JsonNode?)JsonValue.Create(kind)).ToArray());
        }
        if (pixai is not null)
        {
            basics["pixai_model"] = pixai.ModelName;
            basics["pixai_hash"] = pixai.ModelHash;
            basics["pixai_character_candidate"] = pixai.CharacterCandidate;
            basics["pixai_character_score"] = pixai.CharacterScore;
            basics["pixai_work_candidate"] = pixai.WorkCandidate;
            basics["pixai_work_score"] = pixai.WorkScore;
        }
        root["basic_tags"] = basics;

        return root.ToJsonString();
    }

    /// <summary>导入时是否要把模型分级写成 SFW/NSFW 标签；旧 payload 默认写，保持原行为。</summary>
    public static bool WantsRatingTag(string payload) => WantsRatingTag(payload, true);

    /// <summary>没有导入时快照时使用调用方给的当前设置兜底。</summary>
    public static bool WantsRatingTag(string payload, bool fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("basic_tags", out var basics)
                && basics.ValueKind == JsonValueKind.Object
                && basics.TryGetProperty("rating_tag", out var value))
            {
                if (value.ValueKind == JsonValueKind.False) return false;
                if (value.ValueKind == JsonValueKind.True) return true;
            }
        }
        catch (JsonException)
        {
        }
        return fallback;
    }

    public static string RatingOf(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("rating", out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "").Trim()
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public static string RatingSourceOf(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("rating_source", out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "").Trim()
                : "";
        }
        catch (JsonException)
        {
            return "";
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

    /// <summary>
    /// 人工保存时以库里最新的 payload 为底，只覆盖 review_tags 和分级；
    /// 后台刚写入的分辨率、宽高、rating_checked 不会被界面上的旧 payload 覆盖。
    /// </summary>
    public static string MergeReview(string currentPayload, string updatedPayload, string rating)
    {
        if (JsonNode.Parse(currentPayload) is not JsonObject current) return updatedPayload;
        if (JsonNode.Parse(updatedPayload) is not JsonObject updated) return updatedPayload;
        if (updated["review_tags"] is JsonNode review)
            current["review_tags"] = review.DeepClone();
        if (rating.Length > 0)
        {
            current["rating"] = rating;
            var source = updated["rating_source"] is JsonValue sourceValue
                && sourceValue.TryGetValue<string>(out var sourceText)
                && !string.IsNullOrEmpty(sourceText)
                ? sourceText
                : "manual";
            current["rating_source"] = source;
        }
        else
        {
            current.Remove("rating");
            current.Remove("rating_source");
        }
        if (RatingChecked(currentPayload) || RatingChecked(updatedPayload))
        {
            if (current["basic_tags"] is not JsonObject basics)
            {
                basics = new JsonObject();
                current["basic_tags"] = basics;
            }
            basics["rating_checked"] = true;
        }
        return current.ToJsonString();
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
        if (rating.Length > 0) root["rating_source"] = "manual";
        else root.Remove("rating_source");
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
