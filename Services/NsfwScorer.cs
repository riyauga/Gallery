namespace Gallery.Services;

/// <summary>分级模型四类分数；各模型按标签名称映射为 G/S/Q/E，不依赖输出顺序。</summary>
public sealed record NsfwProbs(double G, double S, double Q, double E)
{
    /// <summary>g/s 算安全，q/e 算敏感；敏感概率为 q+e。</summary>
    public double NsfwProb => Q + E;

    public (string Rating, double Confidence, double Margin) Verdict()
    {
        var classes = new[] { ("g", G), ("s", S), ("q", Q), ("e", E) };
        var ordered = classes.OrderByDescending(x => x.Item2).ToList();
        var top = ordered[0];
        var second = ordered[1];
        return (top.Item1, top.Item2, Math.Max(0, top.Item2 - second.Item2));
    }

    /// <summary>比分档更保险的读法：g 必须过半且领先，否则按三档里最高的一档算。</summary>
    public string Rating => G >= 0.5 && G > S && G > Q && G > E
        ? "g"
        : new[] { ("s", S), ("q", Q), ("e", E) }.OrderByDescending(x => x.Item2).First().Item1;
}

/// <summary>模型输出的一个 Danbooru 标签；Category 4 = 角色，0 = 可映射类型的 general/meta。</summary>
public sealed record ModelTag(string Name, int Category, double Score);

