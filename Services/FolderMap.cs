using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gallery.Services;

/// <summary>
/// 目录分层规则，读 <c>%APPDATA%\Gallery\folder_map.json</c>（v4）：
/// <c>managed_dirs</c> = 由标签接管的一级目录；<c>skip_dirs</c> = 受管目录里不再接管的相对路径；
/// <c>rules</c> = 例外覆盖（把某个标签映射到指定目录名，原样使用、参与多标签择优）。
/// 文件缺失时没有目录规则；配置损坏时停止自动计算并报告错误，不搬动图片。
/// </summary>
public sealed class FolderMap
{
    public const string NoSource = "no_source";      // 没有作品标签时的兜底（original 是真正的「原创作品」标签，走 Sanitize 自然得到 original/）
    public const string NoArtist = "_无作者";        // 没有作者标签时的兜底

    public sealed record Rule(string Category, string Name, string Folder);

    public List<string> ManagedDirs { get; } = [];
    public List<string> SkipDirs { get; } = [];
    public List<Rule> Rules { get; } = [];

    public sealed record RoutingRule(bool Enabled, string Category, string Name, string Folder,
        bool SplitRating, string SfwFolder, string NsfwFolder, bool AppendArtist);

    public List<RoutingRule> RoutingRules { get; } = [];
    public string LoadError { get; private set; } = "";
    public static event Action? Changed;
    internal static void NotifyChanged() => Changed?.Invoke();

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
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema is not (3 or 4))
            throw new InvalidDataException("目录配置格式无效，只支持 folder_map 版本 3 或 4");
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
        map.Validate();
        return map;
    }

    public void Validate()
    {
        if (LoadError.Length > 0) throw new InvalidDataException(LoadError);
        foreach (var dir in ManagedDirs)
            if (RelativeFolder(dir).Contains('/')) throw new InvalidDataException("受管目录只能填写一级目录名");
        foreach (var dir in SkipDirs) _ = RelativeFolder(dir);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in RoutingRules)
        {
            if (rule.Category is not ("work" or "character" or "artist" or "kind" or "custom" or "rating")
                || string.IsNullOrWhiteSpace(rule.Name)) throw new InvalidDataException("请选择标签类型并填写标签名");
            if (!seen.Add(rule.Category + "\0" + rule.Name.Trim())) throw new InvalidDataException("同一类型的同名标签只能配置一条规则");
            _ = RelativeFolder(rule.Folder);
            if (rule.SplitRating)
            {
                if (string.IsNullOrWhiteSpace(rule.SfwFolder) || string.IsNullOrWhiteSpace(rule.NsfwFolder))
                    throw new InvalidDataException("启用分级细分后，请填写 SFW 和 NSFW 子目录名");
                _ = RelativeFolder(rule.SfwFolder); _ = RelativeFolder(rule.NsfwFolder);
            }
        }
    }

    public JsonElement ToJson()
    {
        Validate();
        return JsonSerializer.SerializeToElement(new
        {
            version = 4, managed_dirs = ManagedDirs, skip_dirs = SkipDirs,
            rules = Rules.Select(r => new { category = r.Category, name = r.Name, folder = r.Folder }),
            routing_rules = RoutingRules.Select(r => new { enabled = r.Enabled, category = r.Category, name = r.Name,
                folder = r.Folder, split_rating = r.SplitRating, sfw_folder = r.SfwFolder,
                nsfw_folder = r.NsfwFolder, append_artist = r.AppendArtist }),
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
        var result = Parse((overwrite ? incoming : current).ToJson());
        if (overwrite) return result;
        foreach (var rule in incoming.RoutingRules)
            if (!result.RoutingRules.Any(r => r.Category.Equals(rule.Category, StringComparison.OrdinalIgnoreCase)
                && r.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))) result.RoutingRules.Add(rule);
        foreach (var rule in incoming.Rules)
            if (!result.Rules.Any(r => r.Category.Equals(rule.Category, StringComparison.OrdinalIgnoreCase)
                && r.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))) result.Rules.Add(rule);
        return result;
    }

    public string Resolve(IReadOnlyDictionary<string, List<string>> tags, string? managedDir = null)
    {
        if (LoadError.Length > 0) return "";
        var rating = RatingGroup(tags);
        foreach (var rule in RoutingRules.Where(r => r.Enabled))
        {
            var matches = tags.TryGetValue(rule.Category, out var names)
                && names.Contains(rule.Name, StringComparer.OrdinalIgnoreCase);
            if (rule.Category == "rating" && (rule.Name.Equals("SFW", StringComparison.OrdinalIgnoreCase)
                || rule.Name.Equals("NSFW", StringComparison.OrdinalIgnoreCase)))
                matches = rating.Equals(rule.Name, StringComparison.OrdinalIgnoreCase);
            if (!matches) continue;
            var target = RelativeFolder(rule.Folder);
            if (rule.SplitRating)
            {
                if (rating.Length == 0) return target;
                target += "/" + RelativeFolder(rating == "NSFW" ? rule.NsfwFolder : rule.SfwFolder);
            }
            if (rule.AppendArtist && tags.TryGetValue("artist", out var authors) && authors.Count > 0)
            {
                var author = ArtistFolder(authors);
                if (author.Length > 0) target += "/" + author;
            }
            return target;
        }
        return TargetDirRel(tags.GetValueOrDefault("work") ?? [], tags.GetValueOrDefault("artist") ?? [], managedDir);
    }

    public string? ResolveForExisting(string dirRel, IReadOnlyDictionary<string, List<string>> tags)
    {
        if (LoadError.Length > 0 || !IsManaged(dirRel)) return null;
        var target = Resolve(tags, FirstSegment(dirRel));
        return target.Length == 0 || SkipDirs.Any(skip => Matches(target, skip)) ? null : target;
    }

    private static string RatingGroup(IReadOnlyDictionary<string, List<string>> tags)
    {
        var names = tags.GetValueOrDefault("rating") ?? [];
        var raw = names.FirstOrDefault(n => n is "g" or "s" or "q" or "e");
        if (raw is not null) return raw is "q" or "e" ? "NSFW" : "SFW";
        if (names.Contains("NSFW", StringComparer.OrdinalIgnoreCase)) return "NSFW";
        return names.Contains("SFW", StringComparer.OrdinalIgnoreCase) ? "SFW" : "";
    }

    private static string RelativeFolder(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) throw new InvalidDataException("目标目录必须相对图库根目录，不能填写绝对路径");
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

    /// <summary>该目录是否由标签接管（一级目录命中且不在 skip_dirs 里）。</summary>
    public bool IsManaged(string dirRel)
    {
        var top = FirstSegment(dirRel);
        if (!ManagedDirs.Any(d => string.Equals(d, top, StringComparison.OrdinalIgnoreCase))) return false;
        return !SkipDirs.Any(skip => Matches(dirRel, skip));
    }

    /// <summary>按图片当前所在目录算目标目录（相对根、正斜杠）；不在受管范围内返回 null。</summary>
    public string? TargetDirRel(string dirRel, IReadOnlyList<string> works, IReadOnlyList<string> artists)
        => IsManaged(dirRel) ? TargetDirRel(works, artists, FirstSegment(dirRel)) : null;

    /// <summary>新图归档用：没有现成目录时用首选受管目录拼目标目录。</summary>
    public string TargetDirRel(IReadOnlyList<string> works, IReadOnlyList<string> artists, string? managedDir = null)
    {
        var top = managedDir is { Length: > 0 } ? managedDir
            : ManagedDirs.Count > 0 ? ManagedDirs[0] : "";
        if (top.Length == 0) return "";
        return string.Join('/', new[] { top, WorkFolder(works), ArtistFolder(artists) }
            .Where(part => part.Length > 0));
    }

    /// <summary>作品目录名：有 rules 覆盖就按覆盖（原样），否则取字典序第一个标签名；没有作品标签时用 <see cref="NoSource"/>。</summary>
    public string WorkFolder(IReadOnlyList<string> works)
        => FolderFor("work", works) ?? NoSource;

    /// <summary>画师目录名：同上，没有画师标签时用 <see cref="NoArtist"/>。</summary>
    public string ArtistFolder(IReadOnlyList<string> artists)
        => FolderFor("artist", artists) ?? NoArtist;

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

    /// <summary>覆盖表里的目录名原样保留（只清掉不能进路径的字符与首尾空白点）。</summary>
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
