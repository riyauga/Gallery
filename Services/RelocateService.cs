using System.Text;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// 图库根目录换位置（例如 C:\...\壁纸 → D:\Photos）。
///
/// 前提：文件已经在磁盘上搬好（robocopy 之类），这里只改"库与设置里的路径前缀"，不动文件。
/// 关键：保留 image id —— 标签、别名、NSFW 判定、收藏、来源都挂在 id 上，所以绝不能走
/// "重新扫描"来认这批文件（跨盘后 file_key 全变，content_md5 覆盖不到，重扫会造出一批新记录）。
/// </summary>
public static class RelocateService
{
    public sealed record Audit(
        int RootId,
        string OldRoot,
        string NewRoot,
        int Images,
        int OnDisk,
        int MissingRows,
        long Bytes,
        int Sidecars,
        List<string> Problems)
    {
        /// <summary>体检通过：找到根、无致命问题。</summary>
        public bool Ok => RootId > 0 && Problems.Count == 0;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"根 id      : {RootId}");
            sb.AppendLine($"旧路径     : {OldRoot}");
            sb.AppendLine($"新路径     : {NewRoot}");
            sb.AppendLine($"库记录     : {Images} 行（其中原本 missing {MissingRows} 行）");
            sb.AppendLine($"新位置文件 : {OnDisk} 个 / {Bytes / 1024.0 / 1024.0 / 1024.0:F2} GB");
            sb.AppendLine($"sidecar    : {Sidecars} 个");
            sb.AppendLine(Problems.Count == 0 ? "体检       : 通过" : $"体检       : {Problems.Count} 个问题");
            foreach (var p in Problems.Take(30)) sb.AppendLine("  ! " + p);
            if (Problems.Count > 30) sb.AppendLine($"  ... 另有 {Problems.Count - 30} 个");
            return sb.ToString();
        }
    }

    /// <summary>只读体检：不动库、不动文件。判断"能不能安全改路径"。</summary>
    public static Audit Inspect(StoreService store, string oldRoot, string newRoot)
    {
        var problems = new List<string>();
        string oldFull, newFull;
        try
        {
            oldFull = Path.GetFullPath(oldRoot);
            newFull = Path.GetFullPath(newRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new Audit(-1, oldRoot, newRoot, 0, 0, 0, 0, 0, ["路径无效：" + ex.Message]);
        }

        var root = store.Roots().FirstOrDefault(r => FileIdentity.PathKey(r.Path) == FileIdentity.PathKey(oldFull));
        if (root is null)
            return new Audit(-1, oldFull, newFull, 0, 0, 0, 0, 0, [$"旧路径不是已登记的图库根：{oldFull}"]);

        if (!Directory.Exists(newFull))
            return new Audit(root.Id, oldFull, newFull, 0, 0, 0, 0, 0, [$"新目录不存在：{newFull}"]);

        var others = store.Roots().Where(r => r.Id != root.Id).Select(r => r.Path).ToList();
        var (dup, parent, children) = StoreService.ImportRelation(newFull, others);
        if (dup is not null) problems.Add($"新目录已经是另一个图库根：{dup}");
        if (parent is not null) problems.Add($"新目录位于已有图库根内：{parent}");
        if (children.Count > 0) problems.Add($"新目录包含已有图库根：{children[0]}");

        var rows = store.QueryAll("SELECT * FROM images WHERE root_id=$r", ("$r", root.Id));
        var onDisk = 0;
        long bytes = 0;
        var missingRows = 0;
        foreach (var row in rows)
        {
            if (row.Missing) { missingRows++; continue; } // 库里本来就没文件的（135 行之类），换位置后仍然缺
            var abs = AbsOf(newFull, row.RelPath);
            if (!File.Exists(abs)) { problems.Add($"新位置缺文件：{row.RelPath}"); continue; }
            var length = new FileInfo(abs).Length;
            if (length != row.Size) problems.Add($"体积不符：{row.RelPath}（库 {row.Size} / 盘 {length}）");
            onDisk++;
            bytes += length;
        }

        var sidecars = Directory.EnumerateFiles(newFull, StoreService.SidecarName, SearchOption.AllDirectories).Count();
        return new Audit(root.Id, oldFull, newFull, rows.Count, onDisk, missingRows, bytes, sidecars, problems);
    }

    /// <summary>执行改路径：库（事务）+ 设置。返回给人看的报告。</summary>
    public static string Apply(StoreService store, SettingsStore settings, Audit audit, bool force = false)
    {
        var report = new StringBuilder();
        if (!audit.Ok && !force)
        {
            report.AppendLine("体检未通过，已中止（确认要用 --force）。");
            report.AppendLine(audit.Describe());
            return report.ToString();
        }

        var backup = FolderService.BackupBeforeChange();
        report.AppendLine("备份目录  : " + backup);
        report.AppendLine("旧路径    : " + audit.OldRoot);
        report.AppendLine("新路径    : " + audit.NewRoot);

        // 改写前先数一遍（用独立只读连接，事务外）
        var before = RewriteTargets(store.DbPath, audit.OldRoot);
        foreach (var (table, n) in before) report.AppendLine($"改写前  {table,-16}: 命中 {n} 行");

        var rows = store.QueryAll("SELECT * FROM images WHERE root_id=$r", ("$r", audit.RootId));
        var kept = 0;
        var stale = 0;
        using (var tx = store.Transaction())
        {
            store.Exec($"UPDATE roots SET path='{Esc(audit.NewRoot)}' WHERE id={audit.RootId}");
            foreach (var row in rows)
            {
                var abs = AbsOf(audit.NewRoot, row.RelPath);
                var exists = File.Exists(abs);
                // 只改路径，不改"这张图在不在"的结论：
                // 库里原本记 missing 的行一律保持 missing（历史上大小写重复、路径漂移那批），
                // 否则 Windows 不区分大小写会让它们凭空"复活"成一堆重复图。
                var missing = exists ? (row.Missing ? 1 : 0) : 1;
                if (!exists) kept++;
                else if (row.Missing) stale++;
                store.Exec($"UPDATE images SET abs_path='{Esc(abs)}', missing={missing} WHERE id={row.Id}");
            }
            Rewrite(store, "sidecar_state", "path", audit);
            Rewrite(store, "review_drafts", "path_key", audit);
            Rewrite(store, "ingest_queue", "note", audit);
            Rewrite(store, "ingest_queue", "inbox_path", audit);
            Rewrite(store, "archive_jobs", "source", audit);
            Rewrite(store, "archive_jobs", "destination", audit);
            tx.Success = true;
        }
        report.AppendLine($"images 改写: {rows.Count} 行（新位置有文件 {rows.Count - kept}；文件未到账按 missing 记账 {kept}；" +
                          $"原本就记 missing、换位置后仍保持 missing {stale}）");

        var s = settings.Settings;
        var rebased = s.roots.Where(p => Under(p, audit.OldRoot)).ToList();
        s.roots = s.roots.Select(p => Under(p, audit.OldRoot) ? Rebase(p, audit.OldRoot, audit.NewRoot) : p).ToList();
        s.created_folders = s.created_folders.Select(p => Under(p, audit.OldRoot) ? Rebase(p, audit.OldRoot, audit.NewRoot) : p).ToList();
        var archiveRoot = s.archive_root;
        if (Under(s.archive_root, audit.OldRoot))
        {
            s.archive_root = Rebase(s.archive_root, audit.OldRoot, audit.NewRoot);
            report.AppendLine($"设置改写  : archive_root {archiveRoot} → {s.archive_root}");
        }
        report.AppendLine($"设置改写  : roots {rebased.Count} 条、created_folders 已同步");
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.AppendLine("设置保存失败：" + ex.Message + "（备份里的 settings.json 可手工恢复）");
        }

        // 改写后再数一遍，应当全为 0
        var after = RewriteTargets(store.DbPath, audit.OldRoot);
        foreach (var (table, n) in after)
            report.AppendLine($"改写后  {table,-16}: 残留 {n} 行" + (n == 0 ? " ✓" : " ✗"));

        report.AppendLine("完成。请重启 Gallery（文件监听与缩略图缓存要换到新路径）。");
        return report.ToString();
    }

    private static readonly (string Table, string Column)[] PrefixTables =
    [
        ("sidecar_state", "path"),
        ("review_drafts", "path_key"),
        ("ingest_queue", "note"),
        ("ingest_queue", "inbox_path"),
        ("archive_jobs", "source"),
        ("archive_jobs", "destination"),
    ];

    private static void Rewrite(StoreService store, string table, string column, Audit audit)
    {
        var oldKey = Esc(audit.OldRoot);
        var newVal = Esc(audit.NewRoot);
        var len = audit.OldRoot.Length;
        // 大小写不敏感的前缀命中（Windows 路径），只改前缀本身，尾巴原样保留
        store.Exec($"UPDATE {table} SET {column} = '{newVal}' || substr({column}, {len + 1}) " +
                   $"WHERE {column} IS NOT NULL AND substr({column}, 1, {len}) = '{oldKey}' COLLATE NOCASE");
    }

    /// <summary>用独立连接统计各表还有多少行以旧前缀开头（事务外用，能看到已提交的数据）。</summary>
    private static List<(string Table, long Count)> RewriteTargets(string dbPath, string oldRoot)
    {
        var result = new List<(string, long)>();
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            conn.Open();
            foreach (var (table, column) in PrefixTables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} IS NOT NULL " +
                                  $"AND substr({column}, 1, {oldRoot.Length}) = $p COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$p", oldRoot);
                result.Add(($"{table}.{column}", Convert.ToInt64(cmd.ExecuteScalar() ?? 0L)));
            }
        }
        catch (SqliteException)
        {
            // 只读统计失败不影响搬迁本身
        }
        return result;
    }

    public static string AbsOf(string root, string relPath)
        => relPath.Length == 0
            ? root
            : Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));

    private static bool Under(string path, string prefix)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(prefix)) return false;
        var p = FileIdentity.PathKey(path);
        var q = FileIdentity.PathKey(prefix).TrimEnd('\\');
        return p == q || p.StartsWith(q + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Rebase(string path, string oldRoot, string newRoot)
    {
        try
        {
            var rel = Path.GetRelativePath(oldRoot, path);
            return rel == "." ? newRoot : Path.Combine(newRoot, rel);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
