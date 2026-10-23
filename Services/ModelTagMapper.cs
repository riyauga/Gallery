namespace Gallery.Services;

/// <summary>模型标签要写入哪几类；Overwrite 仅用于主动重新识别。</summary>
public sealed record ModelTagOptions(bool Character, bool Work, bool Kind, bool Overwrite = false)
{
    public bool AnyTags => Character || Work || Kind;
}

/// <summary>把 PixAI 的 general 输出按白名单映射成类型标签。</summary>
public static class ModelTagMapper
{
    public const double KindThreshold = 0.17;

    /// <summary>把模型命中的 general 标签按白名单转成类型标签；单人/多人冲突时只留分高的一个。</summary>
    public static List<string> PickKinds(IReadOnlyList<ModelTag> tags,
        IReadOnlyDictionary<string, string[]> whitelist)
    {
        var map = Taxonomy.BuildKindLookup(whitelist);
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (tag.Category != 0 || tag.Score < KindThreshold) continue;
            if (!map.TryGetValue(tag.Name.Trim().ToLowerInvariant(), out var kind)) continue;
            if (!scores.TryGetValue(kind, out var old) || tag.Score > old) scores[kind] = tag.Score;
        }

        if (scores.TryGetValue("单人", out var solo) && scores.TryGetValue("多人", out var multiple))
        {
            if (solo >= multiple) scores.Remove("多人");
            else scores.Remove("单人");
        }

        var order = whitelist.Keys.ToList();
        return order.Where(scores.ContainsKey)
            .Concat(scores.Keys.Where(kind => !order.Contains(kind)))
            .ToList();
    }
}
