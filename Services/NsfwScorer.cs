using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace Gallery.Services;

/// <summary>WD tagger v3 的四类概率（顺序 = 标签表里 general / sensitive / questionable / explicit 四行）。</summary>
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

/// <summary>
/// ONNX 敏感判定模型。<c>model.onnx</c> + <c>selected_tags.csv</c> 由用户放进同一个文件夹，
/// 通过设置里的「模型文件夹」指定；缺文件、装不起来、跑不动都安全降级为「没有模型」。
/// <para>
/// 读图按 wd tagger v3（SmilingWolf）的约定：透明合白底 → 补成正方形 → 448×448（Lanczos3）
/// → <b>BGR</b>、0–255；输入形状照模型元数据（NHWC <c>[1,448,448,3]</c> 与 NCHW <c>[1,3,448,448]</c> 都支持）。
/// 四类概率按 <c>selected_tags.csv</c> 里评级标签所在行取输出下标；输出本来就是 4 维的模型直接取前 4 个。
/// </para>
/// </summary>
public sealed class NsfwScorer : IDisposable
{
    public const int ImageSize = 448;
    private const string ModelFile = "model.onnx";
    private const string LabelsFile = "selected_tags.csv";

    /// <summary>评级标签名，顺序即返回值 G/S/Q/E 的顺序。</summary>
    private static readonly string[] RatingTags = { "general", "sensitive", "questionable", "explicit" };
    private static readonly Rgba32 White = new(255, 255, 255, 255);

    private static readonly ConcurrentDictionary<string, NsfwScorer> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly bool _nhwc;
    private readonly int[] _ratingIndex;

    public string ModelHash { get; }
    public string ModelName { get; }
    public string ModelDir { get; }

    private NsfwScorer(string dir, InferenceSession session, string modelHash, bool nhwc, int[] ratingIndex)
    {
        ModelDir = dir;
        ModelHash = modelHash;
        ModelName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) + "/" + ModelFile;
        _session = session;
        _inputName = session.InputMetadata.Keys.First();
        _nhwc = nhwc;
        _ratingIndex = ratingIndex;
    }

    /// <summary>目录里有 <c>model.onnx</c> 才算配置了模型。</summary>
    public static bool LooksConfigured(string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && File.Exists(Path.Combine(dir!, ModelFile));

    /// <summary>按 (目录 + 模型 mtime) 复用 session；失败返回 null 并给出原因。</summary>
    public static NsfwScorer? TryCreate(string? dir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(dir))
        {
            error = "还没选模型文件夹";
            return null;
        }

        var modelPath = Path.Combine(dir!, ModelFile);
        if (!File.Exists(modelPath))
        {
            error = $"文件夹里没有 {ModelFile}";
            return null;
        }

        if (!File.Exists(Path.Combine(dir!, LabelsFile)))
        {
            error = $"文件夹里没有 {LabelsFile}";
            return null;
        }

        var key = dir! + "|" + File.GetLastWriteTimeUtc(modelPath).Ticks;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out cached)) return cached;
            try
            {
                var hash = Sha256Of(modelPath);
                var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
                var session = new InferenceSession(modelPath, options);
                var outputLength = session.OutputMetadata.Values.First().Dimensions is { Length: 2 } dims ? dims[1] : 0;
                string labelError;
                int[] ratingIndex;
                if (outputLength == 4)
                {
                    ratingIndex = new[] { 0, 1, 2, 3 };    // 输出本身就是四类的模型：前 4 个就是 g/s/q/e
                    labelError = "";
                }
                else
                {
                    ratingIndex = RatingIndices(dir!, out labelError);
                }

                if (ratingIndex.Length == 0)
                {
                    session.Dispose();
                    error = labelError;
                    return null;
                }

                var scorer = new NsfwScorer(dir!, session, hash, IsNhwc(session), ratingIndex);
                Cache[key] = scorer;
                return scorer;
            }
            catch (Exception ex)
            {
                error = "模型加载失败：" + ex.Message;
                return null;
            }
        }
    }

    /// <summary>逐张打分。</summary>
    public NsfwProbs Score(string imagePath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var input = Preprocess(imagePath);

        token.ThrowIfCancellationRequested();
        var shape = _nhwc ? new[] { 1, ImageSize, ImageSize, 3 } : new[] { 1, 3, ImageSize, ImageSize };
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<float>(input, shape)),
        });
        var output = results.First().AsEnumerable<float>().ToArray();

        foreach (var at in _ratingIndex)
        {
            if (at < 0 || at >= output.Length)
                throw new InvalidOperationException($"模型输出只有 {output.Length} 维，取不到第 {at} 个评级标签");
        }

        return new NsfwProbs(output[_ratingIndex[0]], output[_ratingIndex[1]],
            output[_ratingIndex[2]], output[_ratingIndex[3]]);
    }

    /// <summary>透明合白底 → 补成正方形 → 448×448（Lanczos3）→ BGR、0–255，按模型元数据排成 NHWC 或 NCHW。</summary>
    private float[] Preprocess(string imagePath)
    {
        var data = new float[3 * ImageSize * ImageSize];
        using var source = Image.Load<Rgba32>(imagePath);
        using var flat = new Image<Rgba32>(source.Width, source.Height, White);
        flat.Mutate(x => x.DrawImage(source, new Point(0, 0), 1f));

        var side = Math.Max(flat.Width, flat.Height);
        using var canvas = new Image<Rgba32>(side, side, White);
        canvas.Mutate(x => x.DrawImage(flat, new Point((side - flat.Width) / 2, (side - flat.Height) / 2), 1f));
        canvas.Mutate(x => x.Resize(ImageSize, ImageSize, KnownResamplers.Lanczos3));

        var plane = ImageSize * ImageSize;
        for (var y = 0; y < ImageSize; y++)
        {
            for (var x = 0; x < ImageSize; x++)
            {
                var p = canvas[x, y];
                var i = y * ImageSize + x;
                if (_nhwc)
                {
                    var at = i * 3;
                    data[at] = p.B;
                    data[at + 1] = p.G;
                    data[at + 2] = p.R;
                }
                else
                {
                    data[i] = p.B;
                    data[plane + i] = p.G;
                    data[2 * plane + i] = p.R;
                }
            }
        }

        return data;
    }

    /// <summary>输入是 <c>[1,448,448,3]</c> 就按 NHWC 喂，否则按 NCHW。</summary>
    private static bool IsNhwc(InferenceSession session)
    {
        var dims = session.InputMetadata.Values.First().Dimensions;
        return dims.Length == 4 && dims[3] == 3;
    }

    /// <summary>评级标签在标签表里的行号，也就是模型输出里的下标；缺任何一个都算没有可用标签表。</summary>
    private static int[] RatingIndices(string dir, out string error)
    {
        error = "";
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var index = -1;
        foreach (var line in File.ReadLines(Path.Combine(dir, LabelsFile)))
        {
            if (index < 0 && line.StartsWith("tag_id", StringComparison.OrdinalIgnoreCase)) continue;   // 表头
            index++;
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            var name = parts[1].Trim().Trim('"').Trim('\uFEFF');
            if (RatingTags.Contains(name, StringComparer.OrdinalIgnoreCase) && !found.ContainsKey(name))
                found[name] = index;
        }

        var indices = new int[RatingTags.Length];
        for (var i = 0; i < RatingTags.Length; i++)
        {
            if (!found.TryGetValue(RatingTags[i], out indices[i]))
            {
                error = $"{LabelsFile} 里找不到评级标签 {string.Join("/", RatingTags)}";
                return Array.Empty<int>();
            }
        }

        return indices;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose() => _session.Dispose();
}
