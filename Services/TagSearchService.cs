using System.Text;

namespace Gallery.Services;

/// <summary>
/// 标签候选：同一个人物只留一条。
/// Display 是标签贴纸显示名；角色、作品的 SearchDisplay 跟随输入语言，角色附带作品名。
/// </summary>
public sealed record TagSearchSuggestion(
    string Name, string Display, string SearchDisplay, string EntityKey, int Count, bool IsEntity);

/// <summary>
/// 标签编辑器和评审页共用的搜索结果。
/// 先按身份键折叠标签（同一角色不同写法只留使用最多的那条），
/// 词库里已有同身份的候选时不再重复追加；来源名先查身份内其他实体，
/// 再退回另一种语言，两种都缺失或作品未绑定时显式标记。
/// </summary>
public static class TagSearchService
{
    public static List<TagSearchSuggestion> Search(
        StoreService store, string category, string query, int limit = 12)
    {
        var displayMode = store.GlobalTagDisplayMode();
        var searchNameMode = HasHan(query) ? "zh_source" : "en_source";
        var catalog = new TagCatalogService(store);
        var games = catalog.GameNames();
        var tags = store.SearchTags(query, 200)
            .Where(t => t.Category == category)
            .ToList();
        var entities = catalog.SearchEntities(query, 60)
            .Where(e => e.Kind == category)
            .ToList();
        Dictionary<string, TagCatalogService.IdentityNames>? identities = null;
        if (category == "character")
        {
            var identityKeys = tags.Select(t => t.IdentityKey)
                .Concat(entities.Select(e => e.IdentityKey))
                .Where(key => key.Length > 0)
                .Distinct(StringComparer.Ordinal);
            identities = catalog.LoadIdentityNames(identityKeys);
        }

        var result = new List<TagSearchSuggestion>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var searchDisplays = new HashSet<string>(StringComparer.Ordinal);
        var dedupeBySearchDisplay = category == "character";
        var tagNamesByGame = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (tag.GameKey.Length == 0) continue;
            if (!tagNamesByGame.TryGetValue(tag.GameKey, out var names))
                tagNamesByGame[tag.GameKey] = names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var term in tag.SearchTerms.Append(tag.Display))
            {
                var norm = Norm(term);
                if (norm.Length >= 2) names.Add(norm);
            }
        }

        // 标签优先：同一身份保留使用数最多的标签名，但保留搜索结果原本的排序位置。
        foreach (var tag in tags)
        {
            var key = tag.IdentityKey.Length > 0
                ? "id:" + tag.IdentityKey
                : "tag:" + tag.Id;
            var searchDisplay = TagSearchDisplay(tag, category, searchNameMode, games, identities);
            var suggestion = new TagSearchSuggestion(
                tag.Name,
                tag.Display,
                searchDisplay,
                tag.EntityKey,
                tag.Count,
                false);
            if (indexes.TryGetValue(key, out var at))
            {
                if (suggestion.Count > result[at].Count) result[at] = suggestion;
                continue;
            }
            if (dedupeBySearchDisplay && !searchDisplays.Add(searchDisplay)) continue;
            indexes[key] = result.Count;
            result.Add(suggestion);
        }

        // 只有这个身份还没有任何标签时，才把词库实体作为候选。
        // 同作品、同英文来源名的中/英文 wiki 条目也只留一个代表。
        var distinctEntities = entities
            .GroupBy(e => category == "character" && e.En.Length > 0 && e.GameKey.Length > 0
                ? "name:" + e.GameKey + "\u001f" + Norm(e.En)
                : "entity:" + e.EntityKey)
            .Select(g => g.OrderByDescending(e => e.Zh.Length > 0)
                .ThenByDescending(e => e.En.Length > 0)
                .First());
        foreach (var entity in distinctEntities)
        {
            // 中文/英文 wiki 经常把同一角色拆成两个实体；同作品且来源名完全相同时，
            // 已有标签已经代表这个角色，不再追加第二个候选。
            if (HasExactTagName(tagNamesByGame, entity)) continue;
            var identity = entity.IdentityKey.Length > 0 ? entity.IdentityKey : entity.EntityKey;
            var key = "id:" + identity;
            if (indexes.ContainsKey(key)) continue;
            var name = entity.Zh.Length > 0 ? entity.Zh : entity.En;
            var display = EntityDisplay(entity, category is "character" or "work" ? displayMode : TagDisplayState.DefaultGlobalMode);
            var searchDisplay = EntitySearchDisplay(entity, category, searchNameMode, games, identities);
            if (name.Length == 0 || display.Length == 0 || searchDisplay.Length == 0) continue;
            if (dedupeBySearchDisplay && !searchDisplays.Add(searchDisplay)) continue;
            indexes[key] = result.Count;
            result.Add(new TagSearchSuggestion(name, display, searchDisplay, entity.EntityKey, 0, true));
        }

        return result.Take(limit).ToList();
    }

    private static bool HasExactTagName(
        Dictionary<string, HashSet<string>> namesByGame, TagCatalogService.EntityHit entity)
    {
        if (entity.GameKey.Length == 0 ||
            !namesByGame.TryGetValue(entity.GameKey, out var names) ||
            names.Count == 0)
            return false;
        foreach (var term in new[] { entity.Zh, entity.En }.Concat(entity.Aliases))
        {
            var norm = Norm(term);
            if (norm.Length >= 2 && names.Contains(norm)) return true;
        }
        return false;
    }

    private static string Norm(string? text) => TagCatalogService.Norm(text ?? "");

    /// <summary>
    /// 角色候选使用来源原名并带作品名，作品候选使用作品名。
    /// 英文风格与示例一致：kafka_(honkai:_star_rail)；中文：卡芙卡_(崩坏：星穹铁道)。
    /// 输入语言对应的名称缺失时退回另一种来源名；两种都缺失或没有绑定作品时显式标记，不伪造。
    /// </summary>
    private static string TagSearchDisplay(
        StoreService.TagInfo tag, string category, string mode,
        IReadOnlyDictionary<string, (string Zh, string En)> games,
        IReadOnlyDictionary<string, TagCatalogService.IdentityNames>? identities)
    {
        if (category is not ("character" or "work")) return tag.Display;
        if (category == "work")
        {
            var workZh = FirstNonEmpty(tag.ZhName, HasHan(tag.Name) ? tag.Name : "", tag.Aliases.FirstOrDefault(HasHan));
            var workEn = FirstNonEmpty(tag.EnName, HasAsciiLetter(tag.Name) && !HasHan(tag.Name) ? tag.Name : "",
                tag.Aliases.FirstOrDefault(name => HasAsciiLetter(name) && !HasHan(name)));
            return PreferredName(workZh, workEn, mode, tag.Display);
        }
        // 标签名本身就是来源原名（通常是英文小写下划线）；中文标签名只在没有中文名时作为中文来源。
        var asciiName = HasAsciiLetter(tag.Name);
        var zh = tag.ZhName;
        var en = asciiName ? tag.Name : tag.EnName;
        if (!asciiName && zh.Length == 0 && en.Length == 0) zh = tag.Name;
        var identity = FindIdentity(identities, tag.IdentityKey);
        return BuildSourceLabel(zh, en, tag.GameKey, tag.OriginGame, mode,
            tag.Display, games, identity);
    }

    // 中英混输含汉字时优先中文；纯英文、数字和符号输入优先英文，缺失时回退。
    private static bool HasHan(string text) => text.EnumerateRunes().Any(rune =>
        rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FA1F or >= 0x30000 and <= 0x323AF);

    private static string PreferredName(string zh, string en, string mode, string fallback) =>
        mode == "zh_source" ? FirstNonEmpty(zh, en, fallback) : FirstNonEmpty(en, zh, fallback);

    private static bool HasAsciiLetter(string text) =>
        text.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static string EntitySearchDisplay(
        TagCatalogService.EntityHit entity, string category, string mode,
        IReadOnlyDictionary<string, (string Zh, string En)> games,
        IReadOnlyDictionary<string, TagCatalogService.IdentityNames>? identities)
    {
        var display = EntityDisplay(entity, TagDisplayState.DefaultGlobalMode);
        if (category == "work") return PreferredName(FirstNonEmpty(entity.Zh, entity.Aliases.FirstOrDefault(HasHan)),
            FirstNonEmpty(entity.En, entity.Aliases.FirstOrDefault(name => HasAsciiLetter(name) && !HasHan(name))), mode, display);
        if (category != "character") return display;
        var identity = FindIdentity(identities, entity.IdentityKey);
        return BuildSourceLabel(entity.Zh, entity.En, entity.GameKey, "", mode,
            display, games, identity);
    }

    private static TagCatalogService.IdentityNames? FindIdentity(
        IReadOnlyDictionary<string, TagCatalogService.IdentityNames>? identities, string key)
    {
        if (identities is null || key.Length == 0) return null;
        return identities.TryGetValue(key, out var identity) ? identity : null;
    }

    /// <summary>优先实体/标签自己的来源名，缺失时从同身份其他实体取最短的名字。</summary>
    private static string PickName(string own, IReadOnlyList<string>? identityNames)
    {
        var clean = (own ?? "").Trim();
        if (clean.Length > 0) return clean;
        if (identityNames is null) return "";
        return identityNames
            .Select(name => (name ?? "").Trim())
            .Where(name => name.Length > 0)
            .OrderBy(name => name.Length)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "";
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.Select(value => (value ?? "").Trim())
            .FirstOrDefault(value => value.Length > 0) ?? "";

    private static string BuildSourceLabel(
        string zhName, string enName, string gameKey, string originGame, string mode,
        string fallback, IReadOnlyDictionary<string, (string Zh, string En)> games,
        TagCatalogService.IdentityNames? identity)
    {
        var zh = PickName(zhName, identity?.Zh);
        var en = PickName(enName, identity?.En);
        var workKey = FirstNonEmpty(gameKey, originGame, identity?.Games.FirstOrDefault());
        var (workZh, workEn) = WorkNames(workKey, games);

        if (zh.Length == 0 && en.Length == 0)
            return WithMarkers(fallback, missingSource: true,
                missingWork: workZh.Length == 0 && workEn.Length == 0);

        var useZh = mode == "zh_source" ? zh.Length > 0 : en.Length == 0;
        var name = useZh ? zh : EnglishSlug(en);
        var work = useZh ? workZh : workEn;
        if (work.Length == 0) return WithMarkers(name, missingSource: false, missingWork: true);
        return AppendWork(name, work);
    }

    private static string WithMarkers(string name, bool missingSource, bool missingWork)
    {
        var clean = (name ?? "").Trim();
        var markers = new List<string>(2);
        if (missingSource) markers.Add("来源缺失");
        if (missingWork) markers.Add("未绑定作品");
        if (markers.Count == 0) return clean;
        var suffix = "（" + string.Join("，", markers) + "）";
        return clean.EndsWith(suffix, StringComparison.Ordinal) ? clean : clean + suffix;
    }

    private static (string Zh, string En) WorkNames(
        string gameKey, IReadOnlyDictionary<string, (string Zh, string En)> games)
    {
        if (gameKey.Length == 0) return ("", "");
        if (games.TryGetValue(gameKey, out var names))
            return (names.Zh.Trim(), EnglishSlug(names.En));
        return (gameKey, gameKey);
    }

    private static string AppendWork(string name, string work)
    {
        var clean = (name ?? "").Trim();
        var suffix = (work ?? "").Trim();
        if (clean.Length == 0 || suffix.Length == 0) return clean;
        var tail = "_(" + suffix + ")";
        return clean.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
            ? clean
            : clean + tail;
    }

    private static string EnglishSlug(string text)
    {
        var parts = (text ?? "").Trim().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join("_", parts).ToLowerInvariant();
    }

    /// <summary>实体候选没有标签的显示覆盖，只能按全局模式算；默认中文，没有中文用英文来源名。</summary>
    private static string EntityDisplay(TagCatalogService.EntityHit entity, string mode)
    {
        var zh = entity.Zh.Trim();
        var en = entity.En.Trim();
        return mode switch
        {
            "original" => en.Length > 0 ? en : zh,
            "canonical" => en.Length > 0 ? en : zh,
            "alias" => entity.Aliases
                .Select(a => (a ?? "").Trim())
                .FirstOrDefault(a => a.Length > 0) is { } alias
                ? alias
                : zh.Length > 0 ? zh : en,
            _ => zh.Length > 0 ? zh : en,
        };
    }
}
