using System.Text.Json;

namespace Gallery.Services;

/// <summary>
/// 待归档图片的基础标签：入队后补分辨率/横竖图，并用本地模型补空分级。
/// 只填空，不覆盖站点或人工分级；归档按钮不参与触发。
/// </summary>
public sealed class IngestBasicsService
{
    private readonly StoreService _store;
    private readonly string _modelDir;
    private Func<string, CancellationToken, NsfwProbs>? _score;
    private string _modelName = "";
    private string _modelHash = "";
    private bool _scorerTried;
    private string _scorerError = "";

    public IngestBasicsService(StoreService store, string modelDir)
    {
        _store = store;
        _modelDir = modelDir;
    }

    public bool ModelReady =>
        AppService.FakeNsfwScorer || NsfwScorer.LooksConfigured(_modelDir);

    public bool NeedsWork(StoreService.PendingIngestRow row)
    {
        if (row.State != "pending") return false;
        if (IngestReview.NeedsResolution(row.Payload)) return true;
        return ArchiveService.NormalizeRating(row.Rating).Length == 0
            && !IngestReview.RatingChecked(row.Payload)
            && ModelReady;
    }

    public BasicOutcome Enrich(int id, CancellationToken token)
    {
        try
        {
            var row = _store.IngestRow(id);
            if (row is null || row.State is "ignored" or "missing") return new BasicOutcome(false, "", "");
            var done = row.State == "done";
            var path = done
                ? IngestReview.ResolveCurrentPath(_store, row)
                : IngestReview.LocalPath(row);
            if (path.Length == 0 || !File.Exists(path))
                return new BasicOutcome(false, "", "文件不存在");

            var resolutionChanged = false;
            if (!done && IngestReview.NeedsResolution(row.Payload))
            {
                var meta = ImageInfo.ReadMeta(path);
                if (meta.Width is not > 0 || meta.Height is not > 0)
                    return new BasicOutcome(false, "", "读不到图片尺寸");
                resolutionChanged = _store.UpdateIngestBasics(id, meta.Width.Value, meta.Height.Value);
            }

            token.ThrowIfCancellationRequested();

            // 读尺寸或排队期间用户可能已经手填了分级，甚至这张图已经归档；
            // 写库前后都以最新记录为准，归档后的路径改用图库里的当前位置。
            var current = _store.IngestRow(id);
            if (current is null || current.State is "ignored" or "missing")
                return new BasicOutcome(resolutionChanged, "", "");
            if (!done && current.State == "done")
            {
                done = true;
                path = IngestReview.ResolveCurrentPath(_store, current);
                if (path.Length == 0 || !File.Exists(path))
                    return new BasicOutcome(resolutionChanged, "", "文件不存在");
            }

            var existing = ArchiveService.NormalizeRating(current.Rating);
            if (existing.Length == 0)
                existing = ArchiveService.NormalizeRating(IngestReview.RatingOf(current.Payload));
            if (existing.Length > 0) return new BasicOutcome(resolutionChanged, existing, "");
            if (IngestReview.RatingChecked(current.Payload))
                return new BasicOutcome(resolutionChanged, "", "");

            if (!TryScorer(out _)) return new BasicOutcome(resolutionChanged, "", "");

            var probs = _score!(path, token);
            token.ThrowIfCancellationRequested();
            var rating = probs.Rating;
            var applied = _store.ApplyIngestRating(id, rating, probs, _modelName, _modelHash);
            return new BasicOutcome(resolutionChanged, applied, "");
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
            return new BasicOutcome(false, "", ex.Message);
        }
    }

    private bool TryScorer(out string error)
    {
        error = "";
        if (_scorerTried)
        {
            error = _scorerError;
            return _score is not null;
        }

        _scorerTried = true;
        if (AppService.FakeNsfwScorer)
        {
            _score = (path, _) => Math.Abs(path.GetHashCode()) % 2 == 0
                ? new NsfwProbs(0.05, 0.10, 0.15, 0.70)
                : new NsfwProbs(0.86, 0.08, 0.04, 0.02);
            _modelName = "test";
            _modelHash = "test";
            return true;
        }

        var scorer = NsfwScorer.TryCreate(_modelDir, out _scorerError);
        if (scorer is null)
        {
            error = _scorerError;
            return false;
        }
        _modelName = scorer.ModelName;
        _modelHash = scorer.ModelHash;
        _score = (path, token) => scorer.Score(path, token);
        return true;
    }
}

public sealed record BasicOutcome(bool ResolutionChanged, string Rating, string Error);
