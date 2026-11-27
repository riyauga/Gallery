using System.Text.Json;

namespace Gallery.Services;

/// <summary>
/// 待归档图片的基础信息：入队后补真实像素尺寸，并用本地模型补空分级；
/// 开启「导入时自动识别标签」后，PixAI 同一轮补角色/作品/类型。
/// 只处理待审核记录，只补空缺，不覆盖站点或人工标签；归档按钮不参与触发。
/// </summary>
public sealed class IngestBasicsService
{
    private readonly StoreService _store;
    private readonly string _modelDir;
    private Func<string, CancellationToken, PixaiScore>? _score;
    private string _modelName = "";
    private string _modelHash = "";

    public IngestBasicsService(StoreService store, string modelDir)
    {
        _store = store;
        _modelDir = modelDir;
    }

    public bool ModelReady =>
        AppService.ModelAvailable(_modelDir);

    public bool NeedsWork(StoreService.PendingIngestRow row)
    {
        if (row.State != "pending") return false;
        if (IngestReview.NeedsDimensions(row.Payload)) return true;
        if (ModelReady && ArchiveService.NormalizeRating(row.Rating).Length == 0
            && !IngestReview.RatingChecked(row.Payload)) return true;
        return ModelReady && AutoTags && !IngestReview.ModelTagsChecked(row.Payload);
    }

    public BasicOutcome Enrich(int id, CancellationToken token)
    {
        try
        {
            var row = _store.IngestRow(id);
            if (row is null || row.State is "ignored" or "missing")
                return new BasicOutcome(false, "", "", false);
            var done = row.State == "done";
            var path = done
                ? IngestReview.ResolveCurrentPath(_store, row)
                : IngestReview.LocalPath(row);
            if (path.Length == 0 || !File.Exists(path))
                return new BasicOutcome(false, "", "文件不存在", false);

            var dimensionsChanged = false;
            if (!done && IngestReview.NeedsDimensions(row.Payload))
            {
                var meta = ImageInfo.ReadMeta(path);
                if (meta.Width is not > 0 || meta.Height is not > 0)
                    return new BasicOutcome(false, "", "读不到图片尺寸", false);
                dimensionsChanged = _store.UpdateIngestBasics(id, meta.Width.Value, meta.Height.Value);
            }

            token.ThrowIfCancellationRequested();

            // 读尺寸或排队期间用户可能已经手填了分级，甚至这张图已经归档；
            // 写库前后都以最新记录为准，归档后的路径改用图库里的当前位置。
            var current = _store.IngestRow(id);
            if (current is null || current.State is "ignored" or "missing")
                return new BasicOutcome(dimensionsChanged, "", "", false);
            if (!done && current.State == "done")
            {
                done = true;
                path = IngestReview.ResolveCurrentPath(_store, current);
                if (path.Length == 0 || !File.Exists(path))
                    return new BasicOutcome(dimensionsChanged, "", "文件不存在", false);
            }

            var existing = ArchiveService.NormalizeRating(current.Rating);
            if (existing.Length == 0)
                existing = ArchiveService.NormalizeRating(IngestReview.RatingOf(current.Payload));
            var settings = AppService.Settings.Settings;
            var tagOptions = new ModelTagOptions(settings.import_tag_character,
                settings.import_tag_work, settings.import_tag_kind);
            var applyRatingTag = settings.import_tag_rating;
            var needRating = ModelReady && existing.Length == 0 && !IngestReview.RatingChecked(current.Payload);
            var needTags = !done && AutoTags && !IngestReview.ModelTagsChecked(current.Payload);
            if (!needRating && !needTags)
                return new BasicOutcome(dimensionsChanged, existing, "", false);

            // 四种标签都没选、也不需要分级：只记已处理，不必加载模型。
            var tagsChanged = false;
            if (needTags && !tagOptions.AnyTags && !needRating)
            {
                tagsChanged = _store.ApplyIngestModelTags(id, "", "", Array.Empty<string>(),
                    false, false, false, applyRatingTag, _modelName, _modelHash, 0);
                return new BasicOutcome(dimensionsChanged, existing, "", tagsChanged);
            }

            if (!TryScorer(out var error))
                return new BasicOutcome(dimensionsChanged, existing, error, false);
            var pixai = _score!(path, token);
            var probs = pixai.Probs;
            var tags = pixai.Tags;

            token.ThrowIfCancellationRequested();
            var applied = "";
            if (needRating)
                applied = _store.ApplyIngestRating(id, probs.Rating, probs, _modelName, _modelHash);

            if (needTags)
            {
                // 推理期间可能已经归档；自动标签只写仍在待审核队列里的记录。
                var after = _store.IngestRow(id);
                if (after is { State: "pending" })
                {
                    var character = pixai.Character;
                    var work = pixai.Work;
                    IReadOnlyList<string> kinds = tagOptions.Kind
                        ? ModelTagMapper.PickKinds(tags,
                            Taxonomy.LoadKindWhitelist(settings.kind_whitelist_path))
                        : Array.Empty<string>();
                    tagsChanged = _store.ApplyIngestModelTags(id, character, work, kinds,
                        tagOptions.Character, tagOptions.Work, tagOptions.Kind, applyRatingTag,
                        _modelName, _modelHash, pixai.CharacterScore, pixai);
                }
            }

            return new BasicOutcome(dimensionsChanged, applied.Length > 0 ? applied : existing,
                "", tagsChanged);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or ArgumentException
            or Microsoft.Data.Sqlite.SqliteException
            or Microsoft.ML.OnnxRuntime.OnnxRuntimeException
            or SixLabors.ImageSharp.UnknownImageFormatException
            or SixLabors.ImageSharp.InvalidImageContentException)
        {
            return new BasicOutcome(false, "", ex.Message, false);
        }
    }

    private static bool AutoTags => AppService.Settings.Settings.auto_model_tags;

    private bool TryScorer(out string error)
    {
        error = "";
#if GALLERY_DEVELOPMENT
        if (AppService.FakeNsfwScorer)
        {
            _score = (path, _) => new PixaiScore("", "", "", 0, "", 0, "test", "test",
                Math.Abs(path.GetHashCode()) % 2 == 0
                    ? new NsfwProbs(0.05, 0.10, 0.15, 0.70)
                    : new NsfwProbs(0.86, 0.08, 0.04, 0.02), Array.Empty<ModelTag>());
            _modelName = "test";
            _modelHash = "test";
            return true;
        }

#endif
        var scorer = PixaiScorer.TryCreate(_modelDir, out error);
        if (scorer is null)
        {
            return false;
        }
        _modelName = scorer.ModelName;
        _modelHash = scorer.ModelHash;
        _score = (path, token) => scorer.Score(path, token);
        return true;
    }
}

public sealed record BasicOutcome(bool DimensionsChanged, string Rating, string Error,
    bool TagsChanged);
