using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gallery.Services;

/// <summary>
/// 目录分层规则，读 <c>%APPDATA%\Gallery\folder_map.json</c>（v3）：
/// <c>managed_dirs</c> = 由标签接管的一级目录；<c>skip_dirs</c> = 受管目录里不再接管的相对路径；
/// <c>rules</c> = 例外覆盖（把某个标签映射到指定目录名，原样使用、参与多标签择优）。
/// 文件缺失或读坏时按默认（只接管 cartoon）工作，不影响启动，也不改任何数据。
/// </summary>
public sealed class FolderMap
{
    public const string DefaultManagedDir = "cartoon";
    public const string NoSource = "no_source";      // 没有作品标签时的兜底（original 是真正的「原创作品」标签，走 Sanitize 自然得到 original/）
    public const string NoArtist = "_无画师";        // 没有画师标签时的兜底

    public sealed record Rule(string Category, string Name, string Folder);

    public List<string> ManagedDirs { get; } = [];
    public List<string> SkipDirs { get; } = [];
    public List<Rule> Rules { get; } = [];

    public static FolderMap Load(string path)
    {
        var map = new FolderMap();
        try
        {
            if (!File.Exists(path))
            {
                map.ManagedDirs.Add(DefaultManagedDir);
                return map;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            ReadStrings(doc.RootElement, "managed_dirs", map.ManagedDirs);
            ReadStrings(doc.RootElement, "skip_dirs", map.SkipDirs);
            if (doc.RootElement.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in rules.EnumerateArray())
                {
                    var folder = entry.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
                    if (folder.Length == 0) continue;
                    map.Rules.Add(new Rule(
                        entry.TryGetProperty("category", out var c) ? c.GetString() ?? "custom" : "custom",
                        entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        folder));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            map.ManagedDirs.Clear();
            map.SkipDirs.Clear();
            map.Rules.Clear();
        }
        if (map.ManagedDirs.Count == 0) map.ManagedDirs.Add(DefaultManagedDir);
        return map;
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
            : ManagedDirs.Count > 0 ? ManagedDirs[0] : DefaultManagedDir;
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
            var value = (entry.GetString() ?? "").Trim().Trim('/');
            if (value.Length > 0) target.Add(value);
        }
    }
}
