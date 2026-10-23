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

/// <summary>重新识别时写入哪些标签；Rating 对应 SFW/NSFW 标签。</summary>
public sealed record NsfwTagOptions(bool Character, bool Work, bool Kind, bool Rating, bool Overwrite)
{
    public bool AnyTags => Character || Work || Kind;

    public static NsfwTagOptions FromSettings()
    {
        var settings = AppService.Settings.Settings;
        return new(settings.rescan_tag_character, settings.rescan_tag_work,
            settings.rescan_tag_kind, settings.auto_nsfw_tag, settings.rescan_overwrite_tags);
    }
}

/// <summary>
/// 统一 PixAI 批量识别：读图 → 一次推理 → 按勾选写 <c>nsfw_scores</c> / <c>images.nsfw</c>（可选打标签）。
/// 换模型后可以用 <paramref name="onlyStale"/> 只重跑旧模型（或没跑过）的图。
/// </summary>
public sealed class NsfwService
{
    private readonly StoreService _store;

    public NsfwService(StoreService store) => _store = store;

    /// <summary>给测试用的替身打分器：不给就用真模型。</summary>
    public Func<string, CancellationToken, NsfwProbs>? OverrideScorer { get; init; }

    /// <summary>主动重新识别的标签写入选项；默认按设置页当前勾选。</summary>
    public NsfwTagOptions TagOptions { get; init; } = NsfwTagOptions.FromSettings();

    /// <summary>最近一次实际使用的执行设备：<c>gpu</c> / <c>cpu</c>；没加载过模型时为空。</summary>
    public string LastDevice { get; private set; } = "";

    /// <summary>请求 GPU 但回退 CPU 时的原因；正常为空。</summary>
    public string LastDeviceNote { get; private set; } = "";

    /// <summary>一句话说明本次用的是显卡还是 CPU，供界面反馈。</summary>
    public string DeviceSummary => LastDevice switch
    {
        "gpu" => " · 显卡",
        "cpu" => LastDeviceNote.Length > 0 ? " · CPU（" + LastDeviceNote + "）" : " · CPU",
        _ => "",
    };

    public async Task<NsfwScanResult> RunAsync(
        IProgress<(int Done, int Total)>? progress = null,
        bool onlyStale = true,
        CancellationToken token = default)
    {
        var settings = AppService.Settings.Settings;
        var options = TagOptions;
        var whitelist = options.Kind
            ? Taxonomy.LoadKindWhitelist(settings.kind_whitelist_path)
            : null;
        PixaiScorer? scorer = null;
        if (!options.AnyTags && !options.Rating)
            return new NsfwScanResult(0, 0, 0, 0, "请至少勾选一种识别标签");
        if (OverrideScorer is null)
        {
            var loaded = await LoadScorerAsync(settings.nsfw_model_dir, token).ConfigureAwait(false);
            scorer = loaded.Scorer;
            if (scorer is null) return new NsfwScanResult(0, 0, 0, 0, loaded.Error);
            LastDevice = scorer.EffectiveDevice;
            LastDeviceNote = scorer.DeviceNote;
        }

        var modelHash = scorer?.ModelHash ?? "test";
        var modelName = scorer?.ModelName ?? "test";
        var files = _store.ImageFilesForScan(modelHash, onlyStale && !options.AnyTags);
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
                    MaxDegreeOfParallelism = 1,
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

                        var prediction = scorer?.Score(item.Value, token);
                        var probs = prediction?.Probs ?? OverrideScorer!(item.Value, token);
                        var tags = prediction?.Tags ?? Array.Empty<ModelTag>();
                        var (rating, confidence, margin) = probs.Verdict();
                        var detail = JsonSerializer.Serialize(new
                        {
                            g = Math.Round(probs.G, 4),
                            s = Math.Round(probs.S, 4),
                            q = Math.Round(probs.Q, 4),
                            e = Math.Round(probs.E, 4),
                            character_candidate = prediction?.CharacterCandidate,
                            character_score = prediction?.CharacterScore,
                            work_candidate = prediction?.WorkCandidate,
                            work_score = prediction?.WorkScore,
                        });

                        if (options.Rating)
                            _store.SetNsfwScore(item.Key, "model", probs.Rating, "",
                                probs.NsfwProb, confidence, margin, modelName, modelHash, detail, true);

                        if (options.AnyTags)
                        {
                            var character = prediction?.Character ?? "";
                            var work = prediction?.Work ?? "";
                            IReadOnlyList<string> kinds = options.Kind && whitelist is not null
                                ? ModelTagMapper.PickKinds(tags, whitelist)
                                : Array.Empty<string>();
                            _store.ApplyModelTagsToImage(item.Key, character, work, kinds,
                                new ModelTagOptions(options.Character, options.Work,
                                    options.Kind, options.Overwrite));
                        }

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

    /// <summary>预览行：只算不写库，用来对比新旧分级和将要写入的标签。</summary>
    public sealed record NsfwPreviewRow(
        int ImageId, string Path, string OldRating, NsfwProbs Probs, string Rating,
        double Confidence, double Margin, string Detail, string ModelName, string ModelHash,
        string Character, string Work, IReadOnlyList<string> Kinds, string TagSummary)
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
        var options = TagOptions;
        var whitelist = options.Kind
            ? Taxonomy.LoadKindWhitelist(settings.kind_whitelist_path)
            : null;
        PixaiScorer? scorer = null;
        if (!options.AnyTags && !options.Rating)
            return ("请至少勾选一种识别标签", new List<NsfwPreviewRow>());
        if (OverrideScorer is null)
        {
            var loaded = await LoadScorerAsync(settings.nsfw_model_dir, token).ConfigureAwait(false);
            scorer = loaded.Scorer;
            if (scorer is null) return (loaded.Error, new List<NsfwPreviewRow>());
            LastDevice = scorer.EffectiveDevice;
            LastDeviceNote = scorer.DeviceNote;
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
                new ParallelOptions { MaxDegreeOfParallelism = 1, CancellationToken = token },
                item =>
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!File.Exists(item.Value)) return;
                        var prediction = scorer?.Score(item.Value, token);
                        var probs = prediction?.Probs ?? OverrideScorer!(item.Value, token);
                        var tags = prediction?.Tags ?? Array.Empty<ModelTag>();
                        var (rating, confidence, margin) = probs.Verdict();
                        var detail = JsonSerializer.Serialize(new
                        {
                            g = Math.Round(probs.G, 4),
                            s = Math.Round(probs.S, 4),
                            q = Math.Round(probs.Q, 4),
                            e = Math.Round(probs.E, 4),
                            character_candidate = prediction?.CharacterCandidate,
                            character_score = prediction?.CharacterScore,
                            work_candidate = prediction?.WorkCandidate,
                            work_score = prediction?.WorkScore,
                        });

                        var character = prediction?.Character ?? "";
                        var work = prediction?.Work ?? "";
                        IReadOnlyList<string> kinds = options.Kind && whitelist is not null
                            ? ModelTagMapper.PickKinds(tags, whitelist)
                            : Array.Empty<string>();
                        var summary = BuildTagSummary(options, character, work, kinds);
                        var row = new NsfwPreviewRow(
                            item.Key, item.Value, old.TryGetValue(item.Key, out var o) ? o : "",
                            probs, options.Rating ? probs.Rating : old.GetValueOrDefault(item.Key, ""),
                            confidence, margin, detail, modelName, modelHash,
                            character, work, kinds, summary);
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

    /// <summary>把预览结果真正写进去（只写用户确认过的那一批）；tagIt=false 时连标签也跳过。</summary>
    public int ApplyRows(IReadOnlyList<NsfwPreviewRow> rows, bool? tagIt = null)
    {
        var options = TagOptions;
        var writeTags = tagIt != false && options.AnyTags;
        var ratingTag = tagIt ?? options.Rating;
        var n = 0;
        foreach (var row in rows)
        {
            if (options.Rating)
                _store.SetNsfwScore(row.ImageId, "model", row.Rating, "", row.Probs.NsfwProb,
                    row.Confidence, row.Margin, row.ModelName, row.ModelHash, row.Detail, ratingTag);
            if (writeTags)
            {
                _store.ApplyModelTagsToImage(row.ImageId, row.Character, row.Work, row.Kinds,
                    new ModelTagOptions(options.Character, options.Work,
                        options.Kind, options.Overwrite));
            }
            n++;
        }
        return n;
    }

    private static Task<(PixaiScorer? Scorer, string Error)> LoadScorerAsync(string dir, CancellationToken token)
        => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var scorer = PixaiScorer.TryCreate(dir, out var error);
            token.ThrowIfCancellationRequested();
            return (scorer, error);
        }, token);

    private static string BuildTagSummary(NsfwTagOptions options, string character,
        string work, IReadOnlyList<string> kinds)
    {
        var labels = new List<string>();
        if (options.Character && character.Length > 0) labels.Add(character);
        if (options.Work && work.Length > 0) labels.Add(work);
        if (options.Kind) labels.AddRange(kinds);
        return string.Join(" · ", labels);
    }
}
