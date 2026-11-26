using System.Text;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// 按标签把受管目录里的图片整理成 作品/作者 两级（受管范围与例外见 <see cref="FolderMap"/>）。
/// 只搬盘上真实存在的活图：先算目标目录，再复用 <see cref="FileActions.MoveImages"/>（同盘改名、
/// 双目录 sidecar），最后清掉腾空的旧目录。预演完全不写数据、不建目录、不搬文件。
/// </summary>
public static class OrganizeService
{
    private sealed record Item(int Id, int RootId, string DirRel, string Name,
        List<string> Works, List<string> Artists, string Target);

    /// <param name="logPath">执行时把完整搬迁清单写到这个文件（新路径 ← 旧路径），供需要时搬回去。</param>
    public static string Run(StoreService store, FolderMap map, bool apply, string logPath = "")
    {
        var db = store.DbPath;
        var report = new StringBuilder();
        report.AppendLine($"库    : {db}");
        report.AppendLine($"规则  : 受管 {string.Join("、", map.ManagedDirs)} · " +
                          $"跳过 {map.SkipDirs.Count} 个子目录 · 目录名覆盖 {map.Rules.Count} 条 · 标签路由 {map.RoutingRules.Count} 条");
        report.AppendLine($"模式  : {(apply ? "执行" : "预演（不改盘上文件、不改库）")}");
        report.AppendLine();

        if (map.LoadError.Length > 0) return "目录配置无效，整理已停止：" + map.LoadError;
        var all = ReadImages(db);
        var known = all.Where(r => r.Missing == 0).ToList();
        var items = new List<Item>();
        var unmanaged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in known)
        {
            var target = map.ResolveForExisting(row.DirRel, row.Tags);
            if (target is null)
            {
                // 一级目录受管但被 skip_dirs 排除的，按相对目录记账，免得混进「不受管的顶层目录」里
                var top = row.DirRel.Split('/')[0];
                var label = map.ManagedDirs.Any(d => string.Equals(d, top, StringComparison.OrdinalIgnoreCase))
                    ? row.DirRel
                    : top;
                unmanaged[label] = unmanaged.GetValueOrDefault(label) + 1;
                continue;
            }
            items.Add(new Item(row.Id, row.RootId, row.DirRel, row.Name, row.Works, row.Artists, target));
        }

        var moves = items.Where(i => !string.Equals(i.DirRel, i.Target, StringComparison.Ordinal)).ToList();
        report.AppendLine("== 盘点 ==");
        report.AppendLine($"活图 {known.Count} 张：受管 {items.Count} 张（不动 {items.Count - moves.Count}、要搬 {moves.Count}）");
        if (unmanaged.Count > 0)
            report.AppendLine($"不受管 {unmanaged.Values.Sum()} 张：" +
                              string.Join("、", unmanaged.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")));
        if (items.Count == 0)
        {
            report.AppendLine("受管范围内没有图片，无需处理。");
            return report.ToString();
        }

        var targets = items.Select(i => i.Target).Distinct().ToList();
        var missingDirs = targets.Where(target => !Directory.Exists(Abs(store, items.First(i => i.Target == target), target)))
            .ToList();
        var byWork = items.GroupBy(i => i.Target.Split('/').ElementAtOrDefault(1) ?? "")
            .OrderByDescending(g => g.Count()).ToList();
        report.AppendLine($"目标目录 {targets.Count} 个（还不存在的 {missingDirs.Count} 个）· 作品层 {byWork.Count} 个");
        report.AppendLine($"落到 {FolderMap.NoSource}/ 的 {items.Count(i => i.Target.Contains($"/{FolderMap.NoSource}/"))} 张 · " +
                          $"落到 {FolderMap.NoArtist} 的 {items.Count(i => i.Target.EndsWith("/" + FolderMap.NoArtist, StringComparison.Ordinal))} 张");
        report.AppendLine();

        report.AppendLine("== 作品层 ==");
        foreach (var group in byWork)
            report.AppendLine($"    {group.Key,-34} {group.Count(),4} 张  → {group.Select(i => i.Target.Split('/').Last()).Distinct().Count()} 个作者目录");
        report.AppendLine();

        // 目标已存在同名文件 / 两个来源撞同一个目标名：MoveImages 会拒绝，先说清楚
        var conflicts = new List<string>();
        foreach (var item in moves)
        {
            var row = items.First(i => i.Id == item.Id);
            var full = Path.Combine(Abs(store, row, item.Target), item.Name);
            if (File.Exists(full)) conflicts.Add($"{item.Target}/{item.Name}：目标已有同名文件");
        }
        foreach (var clash in moves.GroupBy(m => (m.RootId, m.Target, m.Name.ToLowerInvariant())).Where(g => g.Count() > 1))
            conflicts.Add($"{clash.Key.Target}/{clash.Key.Item3}：有 {clash.Count()} 张图要搬到同一处同名");
        report.AppendLine("== 冲突预检 ==");
        report.AppendLine(conflicts.Count == 0 ? "    无" : string.Join(Environment.NewLine, conflicts.Select(c => "    ! " + c)));
        report.AppendLine();

        report.AppendLine("== 要搬的（前 25 条）==");
        foreach (var item in moves.Take(25))
            report.AppendLine($"    {Show(item.DirRel, item.Name),-64} → {item.Target}/");
        if (moves.Count > 25) report.AppendLine($"    …… 另 {moves.Count - 25} 张");
        report.AppendLine();

        if (!apply)
        {
            report.AppendLine("（预演，未写入）");
            return report.ToString();
        }

        report.AppendLine("备份：" + FolderService.BackupBeforeChange());

        // 先落盘完整搬迁清单：万一中途出问题，照着它就能搬回原位
        if (logPath.Length > 0)
        {
            var log = new StringBuilder();
            log.AppendLine($"# 整理搬迁清单 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.AppendLine($"# 库 {db}");
            log.AppendLine($"# 每行：新路径 <TAB> 旧路径 <TAB> 图片 id（按旧路径搬回即可还原）");
            log.AppendLine($"# 计划 {moves.Count} 张；实际结果见报告");
            foreach (var item in moves)
            {
                var oldFull = Path.Combine(Abs(store, item, item.DirRel), item.Name);
                var newFull = Path.Combine(Abs(store, item, item.Target), item.Name);
                log.AppendLine($"{newFull}\t{oldFull}\t{item.Id}");
            }
            File.WriteAllText(logPath, log.ToString());
            report.AppendLine($"搬迁清单：{logPath}（{moves.Count} 行）");
        }

        var created = 0;
        foreach (var target in targets)
        {
            var item = items.First(i => i.Target == target);
            var dir = Abs(store, item, target);
            if (Directory.Exists(dir)) continue;
            Directory.CreateDirectory(dir);
            created++;
        }

        var done = 0;
        var errors = new List<string>();
        foreach (var group in moves.GroupBy(m => (m.RootId, m.Target)))
        {
            var item = items.First(i => i.RootId == group.Key.RootId && i.Target == group.Key.Target);
            var (ok, failed) = FileActions.MoveImages(store, group.Select(m => m.Id).ToList(), Abs(store, item, group.Key.Target));
            done += ok.Count;
            errors.AddRange(failed);
        }

        // 腾空的旧目录：库里没有记录、盘上只剩 sidecar（或本来就空）→ 收尾
        var emptiedDirs = new List<string>();
        var keptDirs = new List<string>();
        foreach (var (rootId, dirRel) in moves.Select(m => (m.RootId, m.DirRel))
                     .DistinctBy(p => (p.RootId, p.DirRel.ToLowerInvariant())))
        {
            if (Scalar(db, $"SELECT COUNT(*) FROM images WHERE root_id={rootId} AND dir_rel='{Esc(dirRel)}'") != 0) continue;
            var root = store.RootPath(rootId);
            if (root is null) continue;
            var folder = dirRel.Length > 0 ? Path.Combine(root, dirRel.Replace('/', '\\')) : root;
            if (!Directory.Exists(folder)) continue;
            var leftovers = Directory.GetFiles(folder)
                .Where(p => !string.Equals(Path.GetFileName(p), StoreService.SidecarName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var subDirs = Directory.GetDirectories(folder);
            if (leftovers.Count > 0 || subDirs.Length > 0)
            {
                keptDirs.Add($"{dirRel}（还有 {leftovers.Count} 个未索引文件 / {subDirs.Length} 个子目录）");
                continue;
            }
            var sidecar = Path.Combine(folder, StoreService.SidecarName);
            if (File.Exists(sidecar))
                try { File.Delete(sidecar); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            store.Exec($"DELETE FROM sidecar_state WHERE path='{Esc(sidecar)}' COLLATE NOCASE");
            try { Directory.Delete(folder); emptiedDirs.Add(dirRel); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { keptDirs.Add($"{dirRel}（{ex.Message}）"); }
        }

        // 对账：库里的位置与目标一致、文件确实在新位置
        var mismatched = 0;
        var gone = 0;
        foreach (var item in moves)
        {
            var row = store.GetImage(item.Id);
            if (row is null || !string.Equals(row.DirRel, item.Target, StringComparison.Ordinal)) mismatched++;
            else if (!File.Exists(row.AbsPath)) gone++;
        }

        report.AppendLine("== 结果 ==");
        report.AppendLine($"新建目录 {created} 个 · 已搬 {done}/{moves.Count} 张" +
                          (errors.Count == 0 ? " ✓" : $"（失败 {errors.Count} 条）"));
        foreach (var error in errors.Take(10)) report.AppendLine("    ! " + error);
        report.AppendLine($"腾空并清理的旧目录 {emptiedDirs.Count} 个" +
                          (emptiedDirs.Count == 0 ? "" : "：" + string.Join("、", emptiedDirs.Take(12))));
        if (keptDirs.Count > 0)
        {
            report.AppendLine($"保留未删的旧目录 {keptDirs.Count} 个：");
            foreach (var kept in keptDirs.Take(8)) report.AppendLine("    · " + kept);
        }
        report.AppendLine($"对账：位置不符 {mismatched} 张、新位置找不到文件 {gone} 张" +
                          (mismatched == 0 && gone == 0 ? " ✓" : " ✗"));
        report.AppendLine("完成。重启 Gallery 后重新扫描即可看到新目录。");
        return report.ToString();
    }

    // ------------------------------------------------------------------ 读盘工具

    private sealed record Row(int Id, int RootId, string DirRel, string Name, long Missing,
        List<string> Works, List<string> Artists, Dictionary<string, List<string>> Tags);

    private static List<Row> ReadImages(string dbPath)
    {
        var tags = new Dictionary<long, Dictionary<string, List<string>>>();
        using var conn = Open(dbPath);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT it.image_id,t.category,t.name FROM image_tags it " +
                "JOIN tags t ON t.id=it.tag_id JOIN images i ON i.id=it.image_id WHERE i.missing=0";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!tags.TryGetValue(id, out var categories)) tags[id] = categories = new();
                var category = reader.IsDBNull(1) ? "custom" : reader.GetString(1);
                if (!categories.TryGetValue(category, out var names)) categories[category] = names = [];
                names.Add(reader.GetString(2));
            }
        }
        var list = new List<Row>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id,root_id,dir_rel,name,missing,COALESCE(nsfw,'') FROM images";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var categories = tags.GetValueOrDefault(id) ?? new();
                var rating = reader.GetString(5);
                if (rating.Length > 0)
                {
                    if (!categories.TryGetValue("rating", out var names)) categories["rating"] = names = [];
                    names.Add(rating);
                }
                list.Add(new Row((int)id, (int)reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                    categories.GetValueOrDefault("work", []), categories.GetValueOrDefault("artist", []), categories));
            }
        }
        return list;
    }

    private static string Show(string dirRel, string name) => dirRel + "/" + name;

    private static string Abs(StoreService store, Item item, string targetDir)
    {
        var root = store.RootPath(item.RootId) ?? "";
        return targetDir.Length > 0 ? Path.Combine(root, targetDir.Replace('/', '\\')) : root;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static long Scalar(string dbPath, string sql)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0L : Convert.ToInt64(value);
    }

    private static string Esc(string value) => value.Replace("'", "''");
}
