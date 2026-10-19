using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Gallery.Services;

/// <summary>一次批量判定的结果。</summary>
public sealed record NsfwScanResult(int Total, int Scored, int Failed, int Nsfw, string Error)
{
    public bool Ok => Error.Length == 0;
}

/// <summary>
/// 批量跑敏感判定：读图 → 模型 → 写 <c>nsfw_scores</c> / <c>images.nsfw</c>（可选打标签）。
/// 换模型后可以用 <paramref name="onlyStale"/> 只重跑旧模型（或没跑过）的图。
/// </summary>
public sealed class NsfwService
{
    private readonly StoreService _store;

    public NsfwService(StoreService store) => _store = store;

    /// <summary>给测试用的替身打分器：不给就用真模型。</summary>
    public Func<string, CancellationToken, NsfwProbs>? OverrideScorer { get; init; }

    public async Task<NsfwScanResult> RunAsync(
        IProgress<(int Done, int Total)>? progress = null,
        bool onlyStale = true,
        CancellationToken token = default)
    {
        var settings = AppService.Settings.Settings;
        NsfwScorer? scorer = null;
        if (OverrideScorer is null)
        {
            scorer = NsfwScorer.TryCreate(settings.nsfw_model_dir, out var error);
            if (scorer is null) return new NsfwScanResult(0, 0, 0, 0, error);
        }

        var modelHash = scorer?.ModelHash ?? "test";
        var modelName = scorer?.ModelName ?? "test";
        var files = _store.ImageFilesForScan(modelHash, onlyStale);
        var total = files.Count;
        if (total == 0) return new NsfwScanResult(0, 0, 0, 0, "");

        var done = 0;
        var scored = 0;
        var failed = 0;
        var nsfw = 0;
        var gate = new object();

        await Task.Run(() =>
        {
            Parallel.ForEach(
                files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                    CancellationToken = token,
                },
                item =>
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!File.Exists(item.Value))
                        {
                            lock (gate) failed++;
                            return;
                        }

                        var probs = OverrideScorer?.Invoke(item.Value, token)
                                    ?? scorer!.Score(item.Value, token);
                        var (rating, confidence, margin) = probs.Verdict();
                        var detail = JsonSerializer.Serialize(new
                        {
                            g = Math.Round(probs.G, 4),
                            s = Math.Round(probs.S, 4),
                            q = Math.Round(probs.Q, 4),
                            e = Math.Round(probs.E, 4),
                        });

                        _store.SetNsfwScore(item.Key, "model", probs.Rating, "",
                            probs.NsfwProb, confidence, margin, modelName, modelHash, detail,
                            settings.auto_nsfw_tag);

                        lock (gate)
                        {
                            scored++;
                            if (probs.Rating is "q" or "e") nsfw++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        lock (gate) failed++;
                    }
                    finally
                    {
                        lock (gate)
                        {
                            done++;
                            if (done % 4 == 0 || done == total) progress?.Report((done, total));
                        }
                    }
                });
        }, token).ConfigureAwait(false);

        progress?.Report((total, total));
        return new NsfwScanResult(total, scored, failed, nsfw, "");
    }

    /// <summary>格式化给 UI 的一句话（含失败数与模型名）。</summary>
    public static string Describe(NsfwScanResult result) =>
        !result.Ok
            ? result.Error
            : result.Total == 0
                ? "没有需要重新识别的图片"
                : $"识别了 {result.Total} 张 · 成功 {result.Scored} · 敏感 {result.Nsfw} · 失败 {result.Failed}";

    /// <summary>预览行：只算不写库，用来对比新旧分级。</summary>
    public sealed record NsfwPreviewRow(
        int ImageId, string Path, string OldRating, NsfwProbs Probs, string Rating,
        double Confidence, double Margin, string Detail, string ModelName, string ModelHash)
    {
        public bool IsNsfw => Rating is "q" or "e";

        public bool Changed => !string.Equals(OldRating, Rating, StringComparison.Ordinal);
    }

    /// <summary>跑一遍模型但不落库；<paramref name="limit"/> 限制预览张数（按 id 顺序）。</summary>
    public async Task<(string Error, List<NsfwPreviewRow> Rows)> PreviewAsync(
        int limit = 500,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken token = default)
    {
        var settings = AppService.Settings.Settings;
        NsfwScorer? scorer = null;
        if (OverrideScorer is null)
        {
            scorer = NsfwScorer.TryCreate(settings.nsfw_model_dir, out var error);
            if (scorer is null) return (error, new List<NsfwPreviewRow>());
        }

        var modelHash = scorer?.ModelHash ?? "test";
        var modelName = scorer?.ModelName ?? "test";
        var files = _store.ImageFilesForScan(modelHash, onlyStale: false)
            .Take(Math.Max(1, limit))
            .ToList();
        if (files.Count == 0) return ("", new List<NsfwPreviewRow>());

        var old = _store.NsfwRatings(files.Select(f => f.Key));
        var rows = new List<NsfwPreviewRow>(files.Count);
        var gate = new object();
        var done = 0;

        await Task.Run(() =>
        {
            Parallel.ForEach(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = token },
                item =>
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!File.Exists(item.Value)) return;
                        var probs = OverrideScorer?.Invoke(item.Value, token) ?? scorer!.Score(item.Value, token);
                        var (rating, confidence, margin) = probs.Verdict();
                        var detail = JsonSerializer.Serialize(new
                        {
                            g = Math.Round(probs.G, 4),
                            s = Math.Round(probs.S, 4),
                            q = Math.Round(probs.Q, 4),
                            e = Math.Round(probs.E, 4),
                        });

                        var row = new NsfwPreviewRow(
                            item.Key, item.Value, old.TryGetValue(item.Key, out var o) ? o : "",
                            probs, rating, confidence, margin, detail, modelName, modelHash);
                        lock (gate) rows.Add(row);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // 预览里单张失败就跳过，不打断整轮
                    }
                    finally
                    {
                        lock (gate)
                        {
                            done++;
                            if (done % 4 == 0 || done == files.Count) progress?.Report((done, files.Count));
                        }
                    }
                });
        }, token).ConfigureAwait(false);

        progress?.Report((files.Count, files.Count));
        rows.Sort((a, b) => a.ImageId.CompareTo(b.ImageId));
        return ("", rows);
    }

    /// <summary>把预览结果真正写进去（只写用户确认过的那一批）。</summary>
    public int ApplyRows(IReadOnlyList<NsfwPreviewRow> rows, bool tagIt)
    {
        var n = 0;
        foreach (var row in rows)
        {
            _store.SetNsfwScore(row.ImageId, "model", row.Rating, "", row.Probs.NsfwProb,
                row.Confidence, row.Margin, row.ModelName, row.ModelHash, row.Detail, tagIt);
            n++;
        }
        return n;
    }
}
