using Gallery.Services;
using Microsoft.UI.Xaml;

namespace Gallery.Controls.Review;

public sealed partial class ReviewPage
{
    private sealed record ArchiveBatch(int[] Ids, bool FixedDestination, string Destination, bool Auto, string Root);

    private async Task RunArchiveQueueAsync()
    {
        // 先让提交操作返回，队列任务由页面持有；正常退出由 DrainWorkAsync 等待。
        await Task.Yield();
        var store = AppService.Store!;
        while (_archiveQueue.TryDequeue(out var batch))
        {
            var succeeded = 0;
            var galleryChanged = false;
            var failures = new List<string>();
            foreach (var id in batch.Ids)
            {
                var failedBefore = failures.Count;
                _archiveProgress.Text = $"后台归档：本批 {succeeded + failures.Count + 1} / {batch.Ids.Length}，队列剩余 {store.ArchivingIngestCount} 张…";
                try
                {
                    var result = await ArchiveOneAsync(id, batch);
                    if (result.Ok)
                    {
                        succeeded++;
                        if (AppService.Store?.IngestRow(id) is { } completed
                            && IngestReview.RearchiveImageId(completed.Payload) > 0) galleryChanged = true;
                        try { QueueBasics(id); }
                        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
                        { AppService.DebugLog($"archive basics #{id}: {ex.Message}"); }
                    }
                    else failures.Add($"#{id}：{result.Message}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
                    or ArgumentException or NotSupportedException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    failures.Add($"#{id}：{ex.Message}");
                    AppService.DebugLog($"background archive #{id}: {ex.Message}");
                }
                finally
                {
                    store.EndIngestArchiving(id);
                    if (failures.Count != failedBefore)
                    {
                        Reload();
                        Changed?.Invoke();
                    }
                }
            }
            var settingsError = "";
            try
            {
                if (succeeded > 0 && !batch.Auto && AppService.Archiver is { } archiver)
                {
                    var root = await Task.Run(() =>
                    {
                        var resolved = archiver.ResolveRoot(batch.Destination);
                        return (Root: store.Roots().First(item => item.Id == resolved.RootId).Path, resolved.DirRel);
                    });
                    AppService.Settings.Settings.archive_root = root.Root;
                    AppService.Settings.Settings.archive_sub = root.DirRel.Replace('/', Path.DirectorySeparatorChar);
                    AppService.Settings.Save();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            { settingsError = "\n归档已完成，但保存默认目录失败：" + ex.Message; }
            _archiveProgress.Text = failures.Count == 0 ? $"已归档 {succeeded} 张" :
                $"已归档 {succeeded} 张，失败 {failures.Count} 条，失败项已返回待审核。\n" + string.Join("\n", failures.Take(3));
            _archiveProgress.Text += settingsError;
            Reload();
            if (galleryChanged) GalleryImagesChanged?.Invoke();
            Changed?.Invoke();
        }
    }

    private async Task<ArchiveService.ArchiveResult> ArchiveOneAsync(int id, ArchiveBatch batch)
    {
        var store = AppService.Store!;
        var archiver = AppService.Archiver!;
        if (!batch.FixedDestination && await Task.Run(() => NeedsModelBeforeArchive(store, id)))
        {
            var model = await Task.Run(() => ApplyModelToRow(store, id));
            if (model.Error.Length > 0) return ArchiveService.ArchiveResult.Fail("识别失败（" + model.Error + "）");
        }
        var item = await Task.Run(() =>
        {
            var row = store.IngestRow(id);
            var context = row is null ? new FolderMap.FolderContext()
                : new FolderMap.FolderContext(row.PageUrl, row.ReceivedAt);
            var relative = "";
            if (!batch.FixedDestination && row is not null)
            {
                var tags = IngestReview.Tags(row.Payload, row.Rating);
                var folderNames = store.ResolveTagFolderNames(tags.SelectMany(pair =>
                    pair.Value.Select(name => (Category: pair.Key, Name: name))));
                relative = FolderMap.Load(AppPaths.FolderMapPath).Resolve(tags, context, folderNames);
            }
            var target = batch.FixedDestination ? batch.Destination : batch.Root.Length == 0
                ? "" : Path.Combine(batch.Root, relative.Length == 0 ? "" : relative.Replace('/', Path.DirectorySeparatorChar));
            return (Target: target, Moving: row is not null && !ArchiveService.KeepsOriginal(row.Payload),
                Path: row is null ? "" : IngestReview.LocalPath(row));
        });
        if (item.Target.Length == 0) return ArchiveService.ArchiveResult.Fail("没有可用的归档目录");
        if (item.Moving && !await ViewerCoordinator.PrepareMoveAsync(item.Path))
            return ArchiveService.ArchiveResult.Fail("请先完成查看窗口的文件操作，再重试归档。");
        try { return await Task.Run(() => archiver.Archive(id, item.Target)); }
        finally
        {
            if (item.Moving)
            {
                var moved = await Task.Run(() => store.IngestRow(id) is { State: "done" } && !File.Exists(item.Path));
                await ViewerCoordinator.FinishMoveAsync(item.Path, moved);
            }
        }
    }
}
