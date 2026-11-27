using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gallery.Services;

/// <summary>
/// 归档目录配置，读 <c>%APPDATA%\Gallery\folder_map.json</c>（v7）。
/// v7：默认逐级路径 + 按标签条件覆盖；兼容 v5 共用及 v6 每级标签选取方式。
/// 无默认级且未命中条件时直接进根目录。v3/v4 只读入兼容个人备份，不参与目录计算。
/// 文件缺失时没有目录级；配置损坏时停止自动计算并报告错误，不搬动图片。
/// </summary>
public sealed class FolderMap
{
    public const string NoSource = "no_source";      // 旧整理工具兜底
    public const string NoArtist = "_无作者";        // 旧整理工具兜底

    public sealed record Rule(string Category, string Name, string Folder);
    public sealed record RoutingRule(bool Enabled, string Category, string Name, string Folder,
        bool SplitRating, string SfwFolder, string NsfwFolder, bool AppendArtist);

    /// <summary>一级目录的数据来源。Kind 为 fixed / tag / source / date。</summary>
    public sealed record FolderLevel(string Kind, string Category = "", bool UseTagName = true,
        string Fixed = "", string Fallback = "", string DatePart = "", string TagOrder = "input");

    /// <summary>归档时一张图片的附加上下文：来源页网址与导入时间（Unix 秒）。</summary>
    public sealed record FolderContext(string PageUrl = "", double ImportedAt = 0);

    public const int MaxLevels = 8;
    public const string DefaultFallback = "_未分类";

    /// <summary>已登记图库目录的绝对路径；为空表示沿用审核页记忆的目录。</summary>
    public string Root { get; private set; } = "";
    public List<FolderLevel> Levels { get; } = [];
    public sealed record PathOverride(string Category, List<string> Tags, int Position,
        FolderLevel Directory, bool ReplaceTail, List<FolderLevel> Children);
    public List<PathOverride> Overrides { get; } = [];

    // v3/v4 旧字段：只为个人备份的读写兼容，不参与 v5 目录计算。
    public List<string> ManagedDirs { get; } = [];
    public List<string> SkipDirs { get; } = [];
    public List<Rule> Rules { get; } = [];
    public List<RoutingRule> RoutingRules { get; } = [];

    public string LoadError { get; private set; } = "";
    public static event Action? Changed;
    internal static void NotifyChanged() => Changed?.Invoke();

    public void SetRoot(string value) => Root = value.Trim();

    public static FolderMap Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new FolderMap();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return new FolderMap { LoadError = ex.Message };
        }
    }

    public static FolderMap Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema is not (3 or 4 or 5 or 6 or 7))
            throw new InvalidDataException("目录配置格式无效，只支持 folder_map 版本 3、4、5、6 或 7");
        var map = new FolderMap();
        ReadStrings(root, "managed_dirs", map.ManagedDirs);
        ReadStrings(root, "skip_dirs", map.SkipDirs);
        if (root.TryGetProperty("rules", out var rules))
            foreach (var rule in rules.EnumerateArray())
            {
                var folder = rule.TryGetProperty("folder", out var value) ? value.GetString() ?? "" : "";
                if (folder.Length == 0) continue;
                map.Rules.Add(new Rule(rule.TryGetProperty("category", out var category) ? category.GetString() ?? "custom" : "custom",
                    rule.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "", folder));
            }
        if (root.TryGetProperty("routing_rules", out var routes))
            foreach (var rule in routes.EnumerateArray())
                map.RoutingRules.Add(new RoutingRule(rule.GetProperty("enabled").GetBoolean(),
                    (rule.GetProperty("category").GetString() ?? "").ToLowerInvariant(), rule.GetProperty("name").GetString() ?? "",
                    rule.GetProperty("folder").GetString() ?? "", rule.GetProperty("split_rating").GetBoolean(),
                    rule.GetProperty("sfw_folder").GetString() ?? "", rule.GetProperty("nsfw_folder").GetString() ?? "",
                    rule.GetProperty("append_artist").GetBoolean()));
        if (root.TryGetProperty("root", out var rootElement) && rootElement.ValueKind == JsonValueKind.String)
            map.Root = rootElement.GetString() ?? "";
        var legacyTagOrder = root.TryGetProperty("tag_order", out var orderElement)
            && orderElement.ValueKind == JsonValueKind.String && orderElement.GetString() == "text" ? "text" : "input";
        if (root.TryGetProperty("levels", out var levels))
            map.Levels.AddRange(levels.EnumerateArray().Select(l => ReadLevel(l, schema < 6 ? legacyTagOrder : "input")));
        if (root.TryGetProperty("overrides", out var overrides))
            foreach (var rule in overrides.EnumerateArray())
                map.Overrides.Add(new PathOverride(rule.GetProperty("category").GetString() ?? "",
                    rule.GetProperty("tags").EnumerateArray().Select(t => t.GetString() ?? "").ToList(),
                    rule.GetProperty("position").GetInt32(), ReadLevel(rule.GetProperty("directory")),
                    rule.GetProperty("replace_tail").GetBoolean(),
                    rule.GetProperty("children").EnumerateArray().Select(l => ReadLevel(l)).ToList()));
        map.Validate();
        return map;
    }

    private static FolderLevel ReadLevel(JsonElement level, string defaultOrder = "input")
        => new(level.TryGetProperty("kind", out var k) ? (k.GetString() ?? "").ToLowerInvariant() : "tag",
            level.TryGetProperty("category", out var c) ? (c.GetString() ?? "").ToLowerInvariant() : "",
            !level.TryGetProperty("use_tag_name", out var u) || u.GetBoolean(),
            level.TryGetProperty("fixed_name", out var f) ? f.GetString() ?? "" : "",
            level.TryGetProperty("fallback", out var b) ? b.GetString() ?? "" : "",
            level.TryGetProperty("date_part", out var d) ? (d.GetString() ?? "").ToLowerInvariant() : "",
            level.TryGetProperty("tag_order", out var o) ? o.GetString() ?? defaultOrder : defaultOrder);

    public void Validate()
    {
        if (LoadError.Length > 0) throw new InvalidDataException(LoadError);
        if (Levels.Count > MaxLevels) throw new InvalidDataException($"目录最多配置 {MaxLevels} 级");
        foreach (var level in Levels) ValidateLevel(level);
        foreach (var rule in Overrides)
        {
            if (rule.Category is not ("work" or "character" or "artist" or "kind" or "rating" or "custom")
                || rule.Tags.Count == 0 || rule.Tags.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("请选择条件的标签类型和至少一个标签");
            if (rule.Position < 0 || rule.Position >= MaxLevels || rule.Position + 1 + rule.Children.Count > MaxLevels)
                throw new InvalidDataException($"路径最多 {MaxLevels} 级");
            ValidateLevel(rule.Directory);
            foreach (var level in rule.Children) ValidateLevel(level);
        }
    }

    private static void ValidateLevel(FolderLevel level)
    {
        if (level.TagOrder is not ("input" or "text")) throw new InvalidDataException("不支持的标签选取方式");
        switch (level.Kind)
        {
            case "fixed": _ = RelativeFolder(level.Fixed); break;
            case "tag":
                if (level.Category is not ("work" or "character" or "artist" or "kind" or "rating" or "custom"))
                    throw new InvalidDataException("请选择标签类型");
                if (!level.UseTagName) _ = RelativeFolder(level.Fixed);
                break;
            case "source": break;
            case "date":
                if (level.DatePart is not ("year" or "month" or "day")) throw new InvalidDataException("请选择导入时间的精度");
                break;
            default: throw new InvalidDataException("不支持的目录命名方式");
        }
        if (level.Fallback.Length > 0) _ = RelativeFolder(level.Fallback);
    }

    private static object WriteLevel(FolderLevel l) => new { kind = l.Kind, category = l.Category,
        use_tag_name = l.UseTagName, fixed_name = l.Fixed, fallback = l.Fallback, date_part = l.DatePart, tag_order = l.TagOrder };

    public JsonElement ToJson()
    {
        Validate();
        return JsonSerializer.SerializeToElement(new
        {
            version = 7, root = Root, levels = Levels.Select(WriteLevel),
            overrides = Overrides.Select(r => new { category = r.Category, tags = r.Tags, position = r.Position,
                directory = WriteLevel(r.Directory), replace_tail = r.ReplaceTail, children = r.Children.Select(WriteLevel) }),
        });
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(ToJson(), new JsonSerializerOptions { WriteIndented = true });
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
        var pending = path + ".tmp";
        try { File.WriteAllText(pending, json); File.Move(pending, path, overwrite: true); }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        NotifyChanged();
    }

    public static FolderMap Merge(FolderMap current, FolderMap incoming, bool overwrite)
    {
        if (overwrite) return Parse(incoming.ToJson());
        var result = Parse(current.ToJson());
        if (result.Levels.Count == 0 && result.Overrides.Count == 0 && (incoming.Levels.Count > 0 || incoming.Overrides.Count > 0))
        {
            result.SetRoot(incoming.Root);
            foreach (var level in incoming.Levels) result.Levels.Add(level);
            result.Overrides.AddRange(Parse(incoming.ToJson()).Overrides);
        }
        return result;
    }

    /// <summary>按逐级模板算相对目录（正斜杠）；没有目录级时返回空字符串（=进根目录）。</summary>
    public string Resolve(IReadOnlyDictionary<string, List<string>> tags, FolderContext? context = null)
    {
        if (LoadError.Length > 0) return "";
        var ctx = context ?? new FolderContext();
        var parts = new List<string>();
        var path = Levels.ToList();
        // 无默认级时也允许条件在一级目录建立路径；后续级只在实际路径中应用。
        if (path.Count == 0 && Overrides.Any(r => r.Position == 0 && Matches(r, tags)))
            path.Add(new FolderLevel("fixed", Fixed: DefaultFallback));
        for (var i = 0; i < path.Count; i++)
        {
            var rule = Overrides.FirstOrDefault(r => r.Position == i && Matches(r, tags));
            var level = rule?.Directory ?? path[i];
            if (rule is { ReplaceTail: true })
            {
                path.RemoveRange(i + 1, path.Count - i - 1);
                path.AddRange(rule.Children);
            }
            var value = level.Kind switch
            {
                "fixed" => level.Fixed,
                "tag" => TagFolder(level, tags),
                "source" => SourceFolder(ctx.PageUrl, level.Fallback),
                "date" => DateFolder(ctx.ImportedAt, level.DatePart, level.Fallback),
                _ => "",
            };
            if (value.Length > 0) parts.Add(RelativeFolder(value));
        }
        return string.Join('/', parts);
    }

    private static bool Matches(PathOverride rule, IReadOnlyDictionary<string, List<string>> tags)
    {
        var names = tags.GetValueOrDefault(rule.Category) ?? [];
        return rule.Tags.Any(t => names.Contains(t, StringComparer.OrdinalIgnoreCase)
            || rule.Category == "rating" && string.Equals(t, RatingGroup(tags), StringComparison.OrdinalIgnoreCase));
    }

    private string TagFolder(FolderLevel level, IReadOnlyDictionary<string, List<string>> tags)
    {
        if (level.Category == "rating")
        {
            var group = RatingGroup(tags);
            return group.Length > 0 ? group : Fallback(level.Fallback);
        }
        var names = tags.GetValueOrDefault(level.Category) ?? [];
        if (names.Count == 0) return Fallback(level.Fallback);
        if (!level.UseTagName) return level.Fixed;
        var ordered = level.TagOrder == "text" ? names.OrderBy(n => n, StringComparer.Ordinal).ToList() : names;
        return Sanitize(ordered.First());
    }

    private static string Fallback(string configured)
        => configured.Length > 0 ? configured : DefaultFallback;

    private static string RatingGroup(IReadOnlyDictionary<string, List<string>> tags)
    {
        var names = tags.GetValueOrDefault("rating") ?? [];
        var raw = names.FirstOrDefault(n => n is "g" or "s" or "q" or "e");
        if (raw is not null) return raw is "q" or "e" ? "NSFW" : "SFW";
        if (names.Contains("NSFW", StringComparer.OrdinalIgnoreCase)) return "NSFW";
        return names.Contains("SFW", StringComparer.OrdinalIgnoreCase) ? "SFW" : "";
    }

    /// <summary>从来源页网址取站点前缀；识别不了就用域名，没有网址走兜底。</summary>
    private static string SourceFolder(string pageUrl, string fallback)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)) return Fallback(fallback);
        var host = uri.Host.Trim().ToLowerInvariant();
        if (string.Equals(host, "x.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".x.com", StringComparison.OrdinalIgnoreCase)
            || host.Contains("twitter.com", StringComparison.OrdinalIgnoreCase)) return "x";
        if (host.Contains("danbooru", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".donmai.us", StringComparison.OrdinalIgnoreCase)) return "danbooru";
        if (host.Contains("pixiv", StringComparison.OrdinalIgnoreCase)) return "pixiv";
        if (host.Contains("pinterest", StringComparison.OrdinalIgnoreCase)) return "pinterest";
        var bare = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        // 域名作为目录名保留点号（如 example.com），不套标签名的清洗规则。
        return bare.Length == 0 ? Fallback(fallback) : bare;
    }

    private static string DateFolder(double importedAt, string part, string fallback)
    {
        if (importedAt <= 0) return Fallback(fallback);
        DateTimeOffset time;
        try { time = DateTimeOffset.FromUnixTimeSeconds((long)importedAt).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return Fallback(fallback); }
        return part switch
        {
            "year" => time.ToString("yyyy"),
            "month" => time.ToString("yyyy-MM"),
            "day" => time.ToString("yyyy-MM-dd"),
            _ => Fallback(fallback),
        };
    }

    private static string RelativeFolder(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) throw new InvalidDataException("目录名必须相对图库根目录，不能是绝对路径");
        var parts = value.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            var device = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part != part.TrimEnd(' ', '.')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(device, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new InvalidDataException("目录名无效，不能包含越界段、非法字符或 Windows 保留名称");
        }
        return string.Join('/', parts);
    }

    // 以下供命令行「手动整理」工具使用；v5 下没有受管目录概念，整理不再自动搬动图片。
    public bool IsManaged(string dirRel)
    {
        var top = FirstSegment(dirRel);
        if (!ManagedDirs.Any(d => string.Equals(d, top, StringComparison.OrdinalIgnoreCase))) return false;
        return !SkipDirs.Any(skip => Matches(dirRel, skip));
    }

    public string? ResolveForExisting(string dirRel, IReadOnlyDictionary<string, List<string>> tags)
    {
        if (LoadError.Length > 0 || !IsManaged(dirRel)) return null;
        var target = TargetDirRel(tags.GetValueOrDefault("work") ?? [], tags.GetValueOrDefault("artist") ?? [], FirstSegment(dirRel));
        return target.Length == 0 || SkipDirs.Any(skip => Matches(target, skip)) ? null : target;
    }

    public string? TargetDirRel(string dirRel, IReadOnlyList<string> works, IReadOnlyList<string> artists)
        => IsManaged(dirRel) ? TargetDirRel(works, artists, FirstSegment(dirRel)) : null;

    public string TargetDirRel(IReadOnlyList<string> works, IReadOnlyList<string> artists, string? managedDir = null)
    {
        var top = managedDir is { Length: > 0 } ? managedDir
            : ManagedDirs.Count > 0 ? ManagedDirs[0] : "";
        if (top.Length == 0) return "";
        return string.Join('/', new[] { top, WorkFolder(works), ArtistFolder(artists) }
            .Where(part => part.Length > 0));
    }

    public string WorkFolder(IReadOnlyList<string> works) => FolderFor("work", works) ?? NoSource;
    public string ArtistFolder(IReadOnlyList<string> artists) => FolderFor("artist", artists) ?? NoArtist;

    private string? FolderFor(string category, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return null;
        var ordered = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        foreach (var name in ordered)
        {
            var rule = Rules.FirstOrDefault(r =>
                string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (rule is not null) return CleanRuleFolder(rule.Folder);
        }
        return Sanitize(ordered[0]);
    }

    /// <summary>标签名 → 目录名：去掉 Windows 非法字符与前缀 @，空白/点/下划线压成一个，统一小写。</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (ch == '@' && sb.Length == 0) continue;
            sb.Append(IsIllegal(ch) ? ' ' : ch);
        }
        var collapsed = Regex.Replace(sb.ToString().Trim().Trim('.'), @"[\s_.]+", "_").Trim('_');
        return collapsed.ToLowerInvariant();
    }

    private static string CleanRuleFolder(string folder)
    {
        var sb = new StringBuilder(folder.Length);
        foreach (var ch in folder.Trim()) sb.Append(IsIllegal(ch) ? '_' : ch);
        return sb.ToString().Trim().Trim('.').Replace('/', '_').Replace('\\', '_');
    }

    private static bool IsIllegal(char ch) =>
        ch < ' ' || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    private static string FirstSegment(string dirRel)
    {
        var trimmed = dirRel.Trim('/');
        var index = trimmed.IndexOf('/');
        return index < 0 ? trimmed : trimmed[..index];
    }

    private static bool Matches(string dirRel, string skip)
    {
        var a = dirRel.Trim('/');
        var b = skip.Trim('/');
        if (b.Length == 0) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadStrings(JsonElement root, string key, List<string> target)
    {
        if (!root.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            var value = (entry.GetString() ?? "").Trim().Replace('\\', '/').Trim('/');
            if (value.Length > 0) target.Add(value);
        }
    }
}
