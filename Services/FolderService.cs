using System.Text.Json;

namespace Gallery.Services;

/// <summary>
/// 根目录管理，移植 galleryold <c>app/root_changes.py</c>：
/// 添加前 import_relation 判定（重复/父含子/子含父 → 整笔拒绝）；
/// 保存前全量备份（库 + settings.json）到库旁 backups/。
/// 不移动任何图片文件。
/// </summary>
public sealed class FolderService(StoreService store, SettingsStore settings)
{
    public AppSettings Settings => settings.Settings;

    /// <summary>添加一个根目录并立即扫描。失败抛异常（消息可直接给用户）。</summary>
    public (RootInfo Root, ScanResult Scan) AddRootAndScan(
        string path, Action<int, string>? progress = null)
    {
        var candidate = RootCandidate(path);
        var (duplicate, parent, children) = StoreService.ImportRelation(
            candidate, Settings.roots);
        if (duplicate is not null)
            throw new InvalidOperationException("该目录已在图库中。");
        if (parent is not null)
            throw new InvalidOperationException(
                $"该目录位于已有图库目录 {parent} 内，请直接添加父目录。");
        if (children.Count > 0)
            throw new InvalidOperationException(
                $"该目录已包含图库目录（如 {children[0]}），请直接添加父目录。");

        // 备份（对齐 save_root_change：库全量快照 + settings 原样拷贝）
        var backupDir = BackupBeforeChange();

        var newRoots = new List<string>(Settings.roots) { candidate };
        try
        {
            var destinations = store.ReplaceRoots(newRoots);
            Settings.roots = destinations.Keys.ToList();
            settings.Save();
            var rootId = destinations[candidate];
            var scanner = new ScannerService(store, Settings);
            var scan = scanner.ScanRoot(rootId, progress);
            var root = store.Roots().First(r => r.Id == rootId);
            return (root, scan);
        }
        catch
        {
            // 配置回滚（对齐 root_changes.save_root_change 的失败分支）
            RestoreBackup(backupDir);
            throw;
        }
    }

    /// <summary>移除根目录：forget=true 连同索引记录一起移除（不删图）。</summary>
    public void RemoveRoot(int rootId, bool forget = false)
    {
        var newRoots = Settings.roots
            .Where(p => store.Roots().First(r =>
                FileIdentity.PathKey(p) == FileIdentity.PathKey(r.Path)).Id != rootId)
            .ToList();
        var backupDir = BackupBeforeChange();
        try
        {
            if (newRoots.Count > 0)
            {
                store.ReplaceRoots(newRoots);
            }
            else
            {
                store.Exec($"UPDATE roots SET enabled=0 WHERE id={rootId}");
            }
            if (forget)
            {
                store.Exec($"DELETE FROM roots WHERE id={rootId}"); // CASCADE 连带 images
                store.Commit();
            }
            Settings.roots = newRoots;
            settings.Save();
        }
        catch
        {
            RestoreBackup(backupDir);
            throw;
        }
    }

    private static string RootCandidate(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"目录不存在：{full}");
        return full;
    }

    private string BackupBeforeChange()
    {
        var dir = Path.GetDirectoryName(AppPaths.DbPath)!;
        var backup = Path.Combine(dir, "backups",
            "roots-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
            Environment.TickCount64);
        Directory.CreateDirectory(backup);

        // 库全量快照（sqlite backup API 语义：在线拷贝）
        var target = Path.Combine(backup, "library.db");
        using (var src = new Microsoft.Data.Sqlite.SqliteConnection(
                   new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                   {
                       DataSource = AppPaths.DbPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                       Pooling = false,
                   }.ToString()))
        {
            src.Open();
            using var dst = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = target, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString());
            dst.Open();
            src.BackupDatabase(dst);
        }

        if (File.Exists(AppPaths.SettingsPath))
            File.Copy(AppPaths.SettingsPath, Path.Combine(backup, "settings.json"), overwrite: true);
        return backup;
    }

    private static void RestoreBackup(string backupDir)
    {
        try
        {
            var configBackup = Path.Combine(backupDir, "settings.json");
            if (!File.Exists(configBackup))
            {
                if (File.Exists(AppPaths.SettingsPath)) File.Delete(AppPaths.SettingsPath);
                return;
            }
            var tmp = AppPaths.SettingsPath + ".restore.tmp";
            File.Copy(configBackup, tmp, overwrite: true);
            File.Move(tmp, AppPaths.SettingsPath, overwrite: true);
        }
        catch (IOException)
        {
            // 回滚失败只保留备份目录，不阻塞
        }
    }
}
