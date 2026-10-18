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

namespace Gallery.Services;

/// <summary>WD tagger v3 的四类概率（顺序对齐 selected_tags.csv 里 category==9 的四行）。</summary>
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
/// </summary>
public sealed class NsfwScorer : IDisposable
{
    public const int ImageSize = 448;
    private const string ModelFile = "model.onnx";
    private const string LabelsFile = "selected_tags.csv";

    private static readonly ConcurrentDictionary<string, NsfwScorer> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private readonly InferenceSession _session;
    private readonly string _inputName;

    public string ModelHash { get; }
    public string ModelName { get; }
    public string ModelDir { get; }

    private NsfwScorer(string dir, string modelPath, InferenceSession session, string modelHash)
    {
        ModelDir = dir;
        ModelHash = modelHash;
        ModelName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) + "/" + ModelFile;
        _session = session;
        _inputName = session.InputMetadata.Keys.First();
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
                var scorer = new NsfwScorer(dir!, modelPath, session, hash);
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

    /// <summary>逐张打分：短边缩到 448 后中心裁 448×448，RGB 归一化到 0–1。</summary>
    public NsfwProbs Score(string imagePath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        float[] input;
        using (var image = Image.Load<Rgb24>(imagePath))
        {
            image.Mutate(x => x
                .Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Min,
                    Size = new SixLabors.ImageSharp.Size(ImageSize, ImageSize),
                })
                .Crop(new Rectangle(
                    Math.Max(0, (image.Width - ImageSize) / 2),
                    Math.Max(0, (image.Height - ImageSize) / 2),
                    Math.Min(ImageSize, image.Width),
                    Math.Min(ImageSize, image.Height))));

            input = new float[3 * ImageSize * ImageSize];
            var plane = ImageSize * ImageSize;
            for (var y = 0; y < ImageSize; y++)
            {
                for (var x = 0; x < ImageSize; x++)
                {
                    var p = image[x, y];
                    var i = y * ImageSize + x;
                    input[i] = p.R / 255f;
                    input[plane + i] = p.G / 255f;
                    input[2 * plane + i] = p.B / 255f;
                }
            }
        }

        token.ThrowIfCancellationRequested();
        var tensor = new DenseTensor<float>(input, new[] { 1, 3, ImageSize, ImageSize });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var output = results.First().AsEnumerable<float>().ToArray();

        // 六种可能：4 类 / 优先级 4 类 + 2 / 只有 4 类但多一维 batch
        if (output.Length < 4) throw new InvalidOperationException("模型输出维度不是 4 类");
        return new NsfwProbs(output[0], output[1], output[2], output[3]);
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose() => _session.Dispose();
}
