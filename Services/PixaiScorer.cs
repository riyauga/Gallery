using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Services;

/// <summary>保留未过阈值的最高候选，便于追溯；Character/Work 只含过线结果。</summary>
public sealed record PixaiScore(string Character, string Work, string CharacterCandidate,
    double CharacterScore, string WorkCandidate, double WorkScore, string ModelName, string ModelHash,
    NsfwProbs Probs, IReadOnlyList<ModelTag> Tags)
{
    public string Rating => Probs.Rating;
}

/// <summary>PixAI v1.0 ONNX 概率输出版：独立识别最高分角色和作品，一次推理返回四类标签。</summary>
public sealed class PixaiScorer
{
    /// <summary>默认尺寸；实际尺寸从模型 config.json 的 img_size 读取。</summary>
    public const int ImageSize = 1008;
    /// <summary>模型目录里可识别的 ONNX 文件名，按此顺序取第一个存在的。</summary>
    private static readonly string[] ModelFiles = ["model.onnx", "fp32.onnx", "fp16.onnx"];
    public const double CharacterThreshold = 0.27;
    public const double WorkThreshold = 0.24;
    private static readonly ConcurrentDictionary<string, PixaiScorer> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LoadGate = new();
    /// <summary>最后一张推理结束后闲置多久释放会话；下次识别重新加载。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(1);
    private static Timer? _idleTimer;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly InferenceSession _session;
    private readonly int _imageSize;
    private volatile bool _retired;
    private readonly string _input;
    private readonly string[] _tags;
    private readonly (int Start, int Count) _characters, _works;
    private readonly int[] _ratingIndices;
    private readonly List<int> _kindIndices = new();
    public string ModelName { get; }
    public string ModelHash { get; }

    /// <summary>实际生效的执行设备：<c>gpu</c> 或 <c>cpu</c>。请求 GPU 但初始化失败时是 <c>cpu</c>。</summary>
    public string EffectiveDevice { get; }

    /// <summary>回退原因；正常用上 GPU 或强制 CPU 时为空。</summary>
    public string DeviceNote { get; }

    private PixaiScorer(string dir, string device)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
        _tags = config.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        var offset = 0;
        foreach (var split in config.RootElement.GetProperty("tags_split").EnumerateArray())
        {
            var count = split[1].GetInt32();
            if (count < 0) throw new InvalidOperationException("PixAI 标签分类长度无效");
            if (split[0].GetString() == "character") _characters = (offset, count);
            if (split[0].GetString() == "copyright") _works = (offset, count);
            if (split[0].GetString() is "general" or "meta")
                _kindIndices.AddRange(Enumerable.Range(offset, count));
            offset += count;
        }
        if (offset != _tags.Length || _characters.Count == 0 || _works.Count == 0)
            throw new InvalidOperationException("PixAI config.json 缺少角色／作品标签，或标签长度不一致");
        _ratingIndices = new[] { "rating:g", "rating:s", "rating:q", "rating:e" }
            .Select(name => Array.IndexOf(_tags, name)).ToArray();
        if (_ratingIndices.Any(i => i < 0)) throw new InvalidOperationException("PixAI 配置缺少 rating:g/s/q/e 四级分级标签，视为不支持的模型");
        var size = config.RootElement.TryGetProperty("img_size", out var sizeElement) ? sizeElement.GetInt32() : ImageSize;
        if (size < 32 || size > 4096) throw new InvalidOperationException("PixAI 配置的 img_size 不在 32–4096 范围内");
        _imageSize = size;
        var path = FindModelFile(dir) ?? throw new InvalidOperationException("模型目录缺少 model.onnx / fp32.onnx / fp16.onnx");
        using (var stream = File.OpenRead(path)) ModelHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        ModelName = "PixAI v1.0/" + Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        _session = CreateSession(path, device, out var effective, out var note);
        EffectiveDevice = effective;
        DeviceNote = note;
        var input = _session.InputMetadata.First();
        if (_session.InputMetadata.Count != 1 || input.Value.Dimensions is not [1, 3, var h, var w] || h != _imageSize || w != _imageSize
            || _session.OutputMetadata.Values.First().Dimensions is not [1, var outputs] || outputs != _tags.Length)
        {
            _session.Dispose();
            throw new InvalidOperationException($"需要 PixAI v1.0 ONNX 概率模型：单输入 [1,3,{_imageSize},{_imageSize}]、输出维度 {_tags.Length}，并附带匹配 config.json");
        }
        _input = input.Key;
    }

    /// <summary>按约定顺序找模型本体；找不到返回 null。</summary>
    public static string? FindModelFile(string dir)
        => ModelFiles.Select(name => Path.Combine(dir, name)).FirstOrDefault(File.Exists);

    /// <summary>CPU 会话选项：线程数按机器核数限流。</summary>
    private static SessionOptions CpuOptions()
        => new() { IntraOpNumThreads = Math.Max(1, Math.Min(12, Environment.ProcessorCount / 2)) };

    /// <summary>
    /// 建会话。<c>cpu</c> 只用 CPU；<c>auto</c> / <c>gpu</c> 先试 DirectML。
    /// DirectML 必须显式保留 CPU 执行器：模型含只有 CPU 侧实现的节点（如 fp16 常量的 Tile），
    /// 只挂 DML 时初始化直接抛异常——这是本机实测结论，不要删掉那行 AppendExecutionProvider_CPU。
    /// </summary>
    private static InferenceSession CreateSession(string path, string device, out string effective, out string note)
    {
        effective = "cpu";
        note = "";
        if (!string.Equals(device, "gpu", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(device, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return new InferenceSession(path, CpuOptions());
        }
        try
        {
            using var options = new SessionOptions();
            options.AppendExecutionProvider_CPU(1);
            options.AppendExecutionProvider_DML(0);
            var session = new InferenceSession(path, options);
            effective = "gpu";
            return session;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or TypeInitializationException or ArgumentException)
        {
            note = "GPU 不可用，已回退 CPU：" + ex.Message;
            AppService.DebugLog("pixai gpu fallback: " + ex.Message);
            return new InferenceSession(path, CpuOptions());
        }
    }

    public static bool LooksConfigured(string? dir) => !string.IsNullOrWhiteSpace(dir)
        && FindModelFile(dir) is not null && File.Exists(Path.Combine(dir, "config.json"));

    /// <summary>执行设备设置值归一化：只认 cpu / gpu，其余（含空）按 auto。</summary>
    public static string NormalizeDevice(string? device) => device?.Trim().ToLowerInvariant() switch
    {
        "cpu" => "cpu",
        "gpu" => "gpu",
        _ => "auto",
    };

    /// <summary><paramref name="device"/> 为空时读设置页的「识别设备」。</summary>
    public static PixaiScorer? TryCreate(string? dir, out string error, string? device = null)
    {
        error = "";
        if (!LooksConfigured(dir)) { error = "识别模型未配置：需要 PixAI ONNX 模型文件（model.onnx / fp32.onnx / fp16.onnx）和 config.json"; return null; }
        var mode = NormalizeDevice(device ?? AppService.Settings.Settings.nsfw_device);
        var key = CacheKey(dir!, mode);
        lock (LoadGate)
        {
            RetainCache(key);
            if (Cache.TryGetValue(key, out var cached)) { ResetIdleLocked(); return cached; }
            try { var scorer = new PixaiScorer(dir!, mode); Cache[key] = scorer; ResetIdleLocked(); return scorer; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or KeyNotFoundException or ArgumentException or OnnxRuntimeException)
            { error = "PixAI 模型加载失败：" + ex.Message; return null; }
        }
    }

    private static string CacheKey(string dir, string mode)
        => Path.GetFullPath(dir) + "|" + (FindModelFile(dir) is { } file ? File.GetLastWriteTimeUtc(file).Ticks : 0)
            + "|" + File.GetLastWriteTimeUtc(Path.Combine(dir, "config.json")).Ticks + "|" + mode;

    /// <summary>配置变更、包移除、空闲超时或退出时释放会话；等待当前单张推理结束，不释放正在使用的原生资源。</summary>
    public static void RefreshCache(string? dir)
    {
        lock (LoadGate)
        {
            var configured = LooksConfigured(dir);
            RetainCache(configured ? CacheKey(dir!, NormalizeDevice(AppService.Settings.Settings.nsfw_device)) : null);
            if (configured && !Cache.IsEmpty) ResetIdleLocked();
            else StopIdleLocked();
        }
    }

    /// <summary>重置空闲计时；调用方需持有 <see cref="LoadGate"/>。</summary>
    private static void ResetIdleLocked()
    {
        if (Cache.IsEmpty) { StopIdleLocked(); return; }
        if (_idleTimer is not null) _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
        else _idleTimer = new Timer(static _ => RefreshCache(null), null, IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    private static void StopIdleLocked()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    private static void RetainCache(string? key)
    {
        foreach (var entry in Cache)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase) || !Cache.TryRemove(entry.Key, out var scorer)) continue;
            scorer._retired = true;
            void ReleaseSession()
            {
                try { scorer._session.Dispose(); }
                finally { scorer._inferenceGate.Release(); }
            }
            if (scorer._inferenceGate.Wait(0)) ReleaseSession();
            else _ = Task.Run(() => { scorer._inferenceGate.Wait(); ReleaseSession(); });
        }
    }

    public PixaiScore Score(string path, CancellationToken token = default)
    {
        _inferenceGate.Wait(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_retired) throw new InvalidOperationException("识别模型已移除或更换，请重新开始识别");
            var input = Preprocess(path);
            token.ThrowIfCancellationRequested();
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input,
                new DenseTensor<float>(input, new[] { 1, 3, _imageSize, _imageSize })) });
            var scores = results.First().AsEnumerable<float>().ToArray();
            token.ThrowIfCancellationRequested();
            var character = Top(scores, _characters);
            var work = Top(scores, _works);
            return new(character.Score >= CharacterThreshold ? character.Name : "",
                work.Score >= WorkThreshold ? work.Name : "", character.Name, character.Score,
                work.Name, work.Score, ModelName, ModelHash,
                new NsfwProbs(scores[_ratingIndices[0]], scores[_ratingIndices[1]],
                    scores[_ratingIndices[2]], scores[_ratingIndices[3]]),
                _kindIndices.Where(i => scores[i] >= 0.17f)
                    .Select(i => new ModelTag(_tags[i], 0, scores[i])).ToArray());
        }
        finally
        {
            _inferenceGate.Release();
            lock (LoadGate) ResetIdleLocked();
        }
    }

    private (string Name, double Score) Top(float[] scores, (int Start, int Count) range)
    {
        var at = range.Start;
        for (var i = at + 1; i < range.Start + range.Count; i++)
            if (scores[i] > scores[at]) at = i;
        return (_tags[at], scores[at]);
    }

    private float[] Preprocess(string path)
    {
        using var source = Image.Load<Rgba32>(path);
        using var flat = new Image<RgbaVector>(source.Width, source.Height, new RgbaVector(1, 1, 1, 1));
        flat.Mutate(x => x.DrawImage(source, new Point(0, 0), 1f));
        var ratio = Math.Min((double)_imageSize / source.Width, (double)_imageSize / source.Height);
        var width = Math.Max(1, (int)(source.Width * ratio));
        var height = Math.Max(1, (int)(source.Height * ratio));
        // 浮点 RGB 缩放，避免 uint8 中间结果舍入；保比例缩放、黑色补边后再归一化。
        flat.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(width, height), Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Triangle }));
        var plane = _imageSize * _imageSize;
        var data = new float[3 * plane];
        Array.Fill(data, -1f);
        var left = (_imageSize - width) / 2;
        var top = (_imageSize - height) / 2;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var pixel = flat[x, y];
                var at = (y + top) * _imageSize + x + left;
                data[at] = pixel.R * 2 - 1;
                data[plane + at] = pixel.G * 2 - 1;
                data[2 * plane + at] = pixel.B * 2 - 1;
            }
        return data;
    }
}
