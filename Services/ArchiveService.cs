using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Gallery.Services;

/// <summary>
/// 归档流水线：把 inbox 里的文件搬进图库根目录下的目标子目录，成功后写 archive_jobs、
/// 更新 ingest_queue 状态并把新文件登记进 images。跨卷时先复制、校验长度后再删源，
/// 任何一步失败都保留源文件并记录原因（对齐 PLAN 的「成功后再清理源文件」）。
/// </summary>
public sealed class ArchiveService(StoreService store)
{
    public sealed record ArchiveResult(bool Ok, string Message, string Destination)
    {
        public static ArchiveResult Fail(string message) => new(false, message, "");
    }

    /// <summary>目标目录必须落在一个已登记的根目录里，返回 (rootId, 相对目录)。</summary>
    public (int RootId, string DirRel) ResolveRoot(string destinationDir)
    {
        var full = Path.GetFullPath(destinationDir);
        foreach (var root in store.Roots())
        {
            if (!root.Enabled) continue;
            var basePath = Path.GetFullPath(root.Path);
            var baseWithSep = basePath.EndsWith(Path.DirectorySeparatorChar)
                ? basePath
                : basePath + Path.DirectorySeparatorChar;

            // 目标就是根目录本身时 rel 为空，也应当算命中
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                    basePath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return (root.Id, "");

            if (!full.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase)) continue;

            var rel = full[baseWithSep.Length..].Replace('\\', '/').Trim('/');
            return (root.Id, rel);
        }

        return (-1, "");
    }

    /// <summary>归档一条记录到 destinationDir（绝对路径，必须在某个根目录下）。</summary>
    public ArchiveResult Archive(int ingestId, string destinationDir)
    {
        // 失败也留痕：设置页「最近错误」读 sync_errors，重启后还能看到
        ArchiveResult FailAndLog(string message)
        {
            store.RecordSyncError($"archive:{ingestId}:{destinationDir}", message);
            return ArchiveResult.Fail(message);
        }

        var row = store.IngestRow(ingestId);
        if (row is null) return ArchiveResult.Fail("找不到这条记录");
        if (row.State == "done") return ArchiveResult.Fail("这条记录已经归档过：" + row.Note);
        if (row.State == "ignored") return ArchiveResult.Fail("这条记录已被忽略，不会归档");

        var existingImageId = IngestReview.RearchiveImageId(row.Payload);
        if (existingImageId > 0) return Rearchive(row, existingImageId, destinationDir);

        var source = IngestReview.LocalPath(row);
        if (source.Length == 0) return FailAndLog("记录里没有本地路径");
        if (!File.Exists(source)) return FailAndLog("源文件已不在原位（可能之前已归档或已手动改名）");

        if (destinationDir.Length == 0) return FailAndLog("还没选目标文件夹");

        var (rootId, dirRel) = ResolveRoot(destinationDir);
        if (rootId < 0) return FailAndLog("目标文件夹不在已登记的图库目录里");

        try
        {
            Directory.CreateDirectory(destinationDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailAndLog("建不了目标文件夹：" + ex.Message);
        }

        var name = Path.GetFileName(source);
        var destination = UniquePath(Path.Combine(destinationDir, name));
        var jobId = Guid.NewGuid().ToString("n");
        var keepSource = KeepsOriginal(row.Payload);

        // 先复制并校验，再入库，最后才删源——任何一步失败都能回滚到「源文件还在原地」
        try
        {
            CopyVerified(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            store.SaveArchiveJob(jobId, source, destination, "failed", row.Payload, ex.Message);
            store.RecordSyncError($"archive:{ingestId}:{destination}", "搬文件失败：" + ex.Message);
            return new ArchiveResult(false, "搬文件失败：" + ex.Message, destination);
        }

        // 复制期间基础分级可能刚好写入；索引前重读一次，避免丢掉刚到的分级。
        var latest = store.IngestRow(row.Id);
        if (latest is { State: "pending" }) row = latest;

        var (imageId, indexed) = IndexFile(rootId, dirRel, destination, row.Payload);
        if (indexed.Length > 0)
        {
            // 索引没成功就当这次没归档：删掉刚复制的副本，保留源文件，状态仍是 pending
            try
            {
                File.Delete(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                store.SaveArchiveJob(jobId, source, destination, "failed", row.Payload,
                    "索引失败且副本删不掉：" + ex.Message);
                store.RecordSyncError($"archive:{ingestId}:{destination}",
                    "索引失败且副本删不掉：" + ex.Message);
                return new ArchiveResult(false, "索引失败：" + indexed, destination);
            }

            store.SaveArchiveJob(jobId, source, destination, "failed", row.Payload, "索引失败：" + indexed);
            store.RecordSyncError($"archive:{ingestId}:{destination}", "索引失败：" + indexed);
            return new ArchiveResult(false, "索引失败：" + indexed, destination);
        }

        store.SaveArchiveJob(jobId, source, destination, keepSource ? "copied" : "moved", row.Payload, "");

        if (!keepSource)
        {
            try
            {
                File.Delete(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 副本已就位、已入库，只是源文件删不掉（被占用等）；记一条错误但不判失败
                store.RecordSyncError($"archive:{ingestId}:{source}", "已归档但源文件删不掉：" + ex.Message);
            }
        }

        var finalPayload = store.SetIngestArchived(
            row.Id, destination, IngestReview.WithArchivedImage(row.Payload, imageId));
        // 归档完成前最后一刻才写入的分级：补到刚入库的图片上（已有分级时不会覆盖）。
        ApplyPayloadNsfw(imageId, finalPayload);
        return new ArchiveResult(true, $"已归档到 {Path.GetFileName(destination)}", destination);
    }

    private ArchiveResult Rearchive(StoreService.PendingIngestRow row, int imageId, string destinationDir)
    {
        var image = store.GetImage(imageId);
        if (image is null || image.Missing || !File.Exists(image.AbsPath))
            return ArchiveResult.Fail("原图库图片不存在，未重新归档");
        if (string.IsNullOrWhiteSpace(destinationDir) || ResolveRoot(destinationDir).RootId < 0)
            return ArchiveResult.Fail("目标文件夹不在已登记的图库目录里");
        try
        {
            Directory.CreateDirectory(destinationDir);
            if (FileIdentity.PathKey(Path.GetDirectoryName(image.AbsPath) ?? "") != FileIdentity.PathKey(destinationDir))
            {
                var (done, errors) = FileActions.MoveImages(store, [imageId], destinationDir);
                if (!done.Contains(imageId)) return ArchiveResult.Fail(errors.FirstOrDefault() ?? "图片移动失败");
            }
            var latest = store.IngestRow(row.Id) ?? row;
            var reviewed = IngestReview.Tags(latest.Payload, latest.Rating);
            var editable = IngestReview.Categories.Where(category => category != "rating").ToHashSet();
            var pairs = store.CategorizedTags(imageId).Where(pair => !editable.Contains(pair.Category)).ToList();
            var rating = NormalizeRating(latest.Rating);
            if (rating != NormalizeRating(image.Nsfw ?? "")) pairs.RemoveAll(pair => pair.Category == "rating");
            pairs.AddRange(reviewed.Where(pair => editable.Contains(pair.Key))
                .SelectMany(pair => pair.Value.Select(name => (pair.Key, name))));
            store.SetCategorizedTags([imageId], pairs);
            store.ApplyImageSkins(imageId, latest.Payload);
            if (rating != NormalizeRating(image.Nsfw ?? ""))
            {
                if (rating.Length == 0) store.ClearNsfwScore(imageId);
                else store.SetNsfwScore(imageId, IngestRatingOf(latest.Payload).Source, rating, tagIt: true);
            }
            store.Commit();
            store.SyncSidecars([imageId]);
            var current = store.GetImage(imageId)!;
            store.SetIngestArchived(row.Id, current.AbsPath, IngestReview.WithArchivedImage(latest.Payload, imageId));
            AppService.RequestCatalogAlign();
            return new ArchiveResult(true, "已重新归档", current.AbsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            return ArchiveResult.Fail("重新归档失败：" + ex.Message);
        }
    }

    /// <summary>本地导入时选择「归档后保留原文件」；浏览器下载的记录没有这个字段，按移动处理。</summary>
    public static bool KeepsOriginal(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("import_keep_source", out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>复制到目标后做长度 + SHA-256 双重校验；校验不过就删掉副本并抛错，源文件不动。</summary>
    private static void CopyVerified(string source, string destination)
    {
        var length = new FileInfo(source).Length;
        File.Copy(source, destination, false);
        var copied = new FileInfo(destination).Length;
        if (copied != length)
        {
            TryDelete(destination);
            throw new IOException($"复制后大小不一致（{length} → {copied}）");
        }

        var srcHash = Sha256Of(source);
        var dstHash = Sha256Of(destination);
        if (!string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(destination);
            throw new IOException("复制后内容校验不通过（SHA-256 不一致）");
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var next = Path.Combine(dir, $"{stem}-{i}{ext}");
            if (!File.Exists(next)) return next;
        }

        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():n}{ext}");
    }

    /// <summary>把新文件登记进 images（与扫描器同一套 UpsertImage 调用）。返回图片 id，失败时 id 为 0。</summary>
    private (int ImageId, string Error) IndexFile(int rootId, string dirRel, string absPath, string payload)
    {
        try
        {
            var stat = new FileInfo(absPath);
            var name = Path.GetFileName(absPath);
            var meta = ImageInfo.ReadMeta(absPath);
            var (imageId, _) = store.UpsertImage(
                rootId: rootId,
                relPath: dirRel.Length > 0 ? $"{dirRel}/{name}" : name,
                absPath: absPath,
                dirRel: dirRel,
                name: name,
                ext: Path.GetExtension(absPath).ToLowerInvariant(),
                size: stat.Length,
                mtime: stat.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds,
                ctime: stat.CreationTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds,
                width: meta?.Width,
                height: meta?.Height,
                takenAt: meta?.TakenAt);
            store.Commit();
            store.ApplyIngestOrigin(imageId, payload);
            store.ApplyIngestCommentary(imageId, payload);
            ApplyPayloadTags(imageId, payload);
            ApplyPayloadNsfw(imageId, payload);
            return (imageId, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            return (0, ex.Message);
        }
    }

    /// <summary>扩展带来的 danbooru 分级（g/s/q/e）直接落进 nsfw_scores + images.nsfw，
    /// 这样桥接进来的图不用等模型判定就能按「模糊 / 隐藏」处理。</summary>
    private void ApplyPayloadNsfw(int imageId, string payload)
    {
        var (ratingValue, source) = IngestRatingOf(payload);
        var rating = NormalizeRating(ratingValue);
        if (rating.Length == 0) return;
        try
        {
            var detail = source == "danbooru" ? "{\"from\":\"bridge\"}" : "{\"from\":\"review\"}";
            var settings = AppService.Settings.Settings;
            var fallback = !settings.auto_model_tags || settings.import_tag_rating;
            store.SetNsfwScore(
                imageId: imageId,
                source: source,
                rating: rating,
                detail: detail,
                tagIt: IngestReview.WantsRatingTag(payload, fallback));
            store.Commit();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            AppService.DebugLog("payload nsfw failed: " + ex.Message);
        }
    }

    /// <summary>把 general/sensitive/questionable/explicit 或 g/s/q/e 归一成一个字母；认不出就给空串。</summary>
    internal static string NormalizeRating(string? raw)
    {
        var v = (raw ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "g" or "general" or "safe" or "sfw" => "g",
            "s" or "sensitive" => "s",
            "q" or "questionable" => "q",
            "e" or "explicit" or "nsfw" => "e",
            _ => "",
        };
    }

    private static (string Rating, string Source) IngestRatingOf(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var rating = root.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? ""
                : "";
            var source = root.TryGetProperty("rating_source", out var s) && s.ValueKind == JsonValueKind.String
                ? (s.GetString() ?? "").Trim()
                : "";
            if (source is not ("model" or "manual")) source = "danbooru";
            return (rating, source);
        }
        catch (JsonException)
        {
            return ("", "danbooru");
        }
    }

    /// <summary>把扩展带来的标签挂到新文件上（artist/copyright/character/general/meta）。</summary>
    private void ApplyPayloadTags(int imageId, string payload)
    {
        try
        {
            payload = store.NormalizeSkinPayload(payload);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("review_tags", out var reviewed) && reviewed.ValueKind == JsonValueKind.Object)
            {
                var edited = IngestReview.Categories.Where(c => c != "rating")
                    .SelectMany(category => ReadNames(reviewed, category).Select(name => (Category: category, Name: name)))
                    .ToList();
                if (edited.Count > 0)
                {
                    using (var tx = store.Transaction())
                    {
                        store.SetCategorizedTags([imageId], edited, payload);
                        store.ApplyImageSkins(imageId, payload);
                        tx.Success = true;
                    }
                    store.SyncSidecars([imageId]);
                    store.Commit();
                    AppService.RequestCatalogAlign();
                }
                return;
            }
            if (!doc.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
                return;

            var pairs = new List<(string Category, string Name)>();
            var generalAndMeta = new List<string>();

            // artist/copyright/character 原样折类；general/meta 先收起来，
            // 之后按「类型白名单」挑构图/画面性质的那几个（galleryold 同款做法）
            foreach (var category in new[] { "artist", "copyright", "character" })
            {
                foreach (var tag in ReadNames(tags, category))
                    pairs.Add((Taxonomy.DanbooruCategoryMap.GetValueOrDefault(category, "custom"), tag));
            }

            generalAndMeta.AddRange(ReadNames(tags, "general"));
            generalAndMeta.AddRange(ReadNames(tags, "meta"));
            foreach (var category in new[] { "work", "kind", "custom" })
                pairs.AddRange(ReadNames(tags, category).Select(name => (category, name)));

            var whitelist = Taxonomy.LoadKindWhitelist(
                string.IsNullOrWhiteSpace(AppService.Settings?.Settings.kind_whitelist_path)
                    ? null
                    : AppService.Settings!.Settings.kind_whitelist_path);
            foreach (var kind in Taxonomy.KindTagsFrom(generalAndMeta, whitelist))
                pairs.Add(("kind", kind));

            if (pairs.Count == 0) return;

            using (var tx = store.Transaction())
            {
                store.SetCategorizedTags([imageId], pairs, payload);
                tx.Success = true;
            }
            store.SyncSidecars([imageId]);
            store.Commit();
            AppService.RequestCatalogAlign();
        }
        catch (JsonException)
        {
            // payload 坏了就只归档文件，标签留给后续手动补
        }
    }

    private static IEnumerable<string> ReadNames(JsonElement tags, string category)
    {
        if (!tags.TryGetProperty(category, out var array) || array.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var tag = item.GetString() ?? "";
            if (tag.Length > 0) yield return tag;
        }
    }
}
