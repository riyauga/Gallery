namespace Gallery.Services;

/// <summary>
/// 对账源下载路径已失效的待归档记录：只有同名图库文件的实际内容 MD5 一致时才自动完成。
/// </summary>
public static class IngestReconciler
{
    public readonly record struct Result(int Completed, int Missing);

    public static Result Reconcile(StoreService store, CancellationToken token = default)
    {
        var completed = 0;
        var missing = 0;
        foreach (var row in store.IngestRows("pending", int.MaxValue))
        {
            token.ThrowIfCancellationRequested();
            var source = IngestReview.LocalPath(row);
            if (source.Length > 0 && File.Exists(source)) continue;

            var name = row.InboxName.Length > 0 ? row.InboxName : Path.GetFileName(source);
            var match = row.Md5.Length == 0 ? null : FindMatch(store, name, row.Md5, token);
            var changed = match is not null
                ? store.TrySetIngestState(row.Id, "pending", "done", match.AbsPath)
                : store.TrySetIngestState(row.Id, "pending", "missing",
                    row.Md5.Length == 0
                        ? "源文件不可用；记录缺少 MD5，无法安全对账"
                        : "源文件不可用；未找到同内容图库文件");
            if (!changed) continue;
            if (match is not null) completed++;
            else missing++;
        }
        return new Result(completed, missing);
    }

    private static ImageRow? FindMatch(StoreService store, string name, string md5, CancellationToken token)
    {
        if (name.Length == 0) return null;
        foreach (var candidate in store.QueryAll(
                     "SELECT * FROM images WHERE name=$name COLLATE NOCASE",
                     ("$name", name)))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(candidate.AbsPath)) continue;
            string digest;
            try
            {
                digest = FileIdentity.ContentMd5(candidate.AbsPath);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                continue;
            }
            if (digest.Equals(md5, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }
}
