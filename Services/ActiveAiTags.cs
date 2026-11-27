namespace Gallery.Services;

/// <summary>用户主动分析的合并规则；导入补全与图库重识别仍由各自选项决定。</summary>
internal static class ActiveAiTags
{
    public static void Apply(Dictionary<string, List<string>> tags, PixaiScore score,
        IReadOnlyDictionary<string, string[]> whitelist)
    {
        if (score.Character.Length > 0) tags["character"] = [score.Character];
        if (score.Work.Length > 0) tags["work"] = [score.Work];
        if (score.Rating.Length > 0) tags["rating"] = [score.Rating];
        foreach (var kind in ModelTagMapper.PickKinds(score.Tags, whitelist))
            if (kind.Length > 0 && !tags["kind"].Contains(kind, StringComparer.OrdinalIgnoreCase))
                tags["kind"].Add(kind);
    }
}
