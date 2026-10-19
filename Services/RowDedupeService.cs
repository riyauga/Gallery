using System.Text;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// 合并「同一份文件在库里存了两行」的历史遗留记录：早年大小写不同的目录各扫出一份，
/// 于是同一张图有两行（老行 missing=1、新行是活行），而标签常常只落在老行上。
/// 这里让活行继承另一行的全部标签与附属数据（收藏 / nsfw 分 / 手动分类），再删掉多余那行
/// （外键 CASCADE 连带它的关联行）；只处理这类重复组，其它记录一律不碰。
/// </summary>
public static class RowDedupeService
{
    private sealed record Row(long Id, long RootId, string DirRel, string Name, long Missing);

    public static string Run(StoreService store, bool apply)
    {
        var db = store.DbPath;
        var report = new StringBuilder();
        report.AppendLine($"库    : {db}");
        report.AppendLine($"模式  : {(apply ? "执行" : "预演（不改任何数据）")}");
        report.AppendLine();

        var rows = ReadRows(db);
        var groups = new List<(Row Live, List<Row> Dupes)>();
        foreach (var group in rows.GroupBy(r => (r.RootId, PathKey(r.DirRel, r.Name))))
        {
            if (group.Count() < 2) continue;
            var list = group.OrderBy(r => r.Id).ToList();
            var live = list.FirstOrDefault(r => r.Missing == 0) ?? list[0];
            groups.Add((live, list.Where(r => r.Id != live.Id).ToList()));
        }

        var liveCount = rows.Count(r => r.Missing == 0);
        report.AppendLine("== 扫描 ==");
        report.AppendLine($"images 行数 {rows.Count}（活 {liveCount} / missing {rows.Count - liveCount}）");
        if (groups.Count == 0)
        {
            report.AppendLine("没有「同一路径多行」的重复记录，无需处理。");
            return report.ToString();
        }

        // 逐组分析（读自己的连接，看已提交数据）
        long gainedLinks = 0, favMoves = 0, nsfwMoves = 0, manualMoves = 0;
        var noTagBefore = 0;
        var tagBearing = 0;
        var dupeDirs = new Dictionary<(long RootId, string DirRel), int>();
        var examples = new List<string>();
        var liveIds = new List<int>();
        foreach (var (live, dupes) in groups)
        {
            var liveTags = TagIds(db, live.Id);
            var liveManual = ManualCategories(db, live.Id);
            var union = new HashSet<long>(liveTags);
            var gained = 0;
            foreach (var dupe in dupes)
            {
                var dupeTags = TagIds(db, dupe.Id);
                if (dupeTags.Count > 0) tagBearing++;
                foreach (var tag in dupeTags)
                    if (union.Add(tag)) gained++;

                if (Exists(db, "favorites", dupe.Id) && !Exists(db, "favorites", live.Id)) favMoves++;
                if (Exists(db, "nsfw_scores", dupe.Id) && !Exists(db, "nsfw_scores", live.Id)) nsfwMoves++;
                foreach (var category in ManualCategories(db, dupe.Id))
                    if (!liveManual.Contains(category)) manualMoves++;

                dupeDirs[(dupe.RootId, dupe.DirRel)] = dupeDirs.GetValueOrDefault((dupe.RootId, dupe.DirRel)) + 1;
            }
            gainedLinks += gained;
            if (liveTags.Count == 0) noTagBefore++;
            liveIds.Add((int)live.Id);
            if (examples.Count < 8)
                examples.Add($"#{live.Id} {live.DirRel}/{live.Name}  ←  多余行 #{string.Join("、#", dupes.Select(d => d.Id))}" +
                             $"（{string.Join(" / ", dupes.Select(d => d.DirRel))}）" +
                             (gained > 0 ? $"：继承 {gained} 个标签关联" : "：没有标签可继承"));
        }

        var leftovers = groups.Count(g => g.Dupes.Any(d => d.Missing != 1));
        report.AppendLine();
        report.AppendLine("== 结论 ==");
        report.AppendLine($"重复组 {groups.Count} 组：活行 {groups.Count} 行、多余行 {groups.Sum(g => g.Dupes.Count)} 行" +
                          (leftovers > 0 ? $"（其中 {leftovers} 组的多余行自身也是活行，已按 id 最小的一行保留）" : ""));
        report.AppendLine($"多余行里带标签的 {tagBearing} 行；活行原本一个标签都没有的 {noTagBefore} 行");
        report.AppendLine($"可继承的标签关联 {gainedLinks} 行；收藏 {favMoves}、nsfw 分 {nsfwMoves}、手动分类 {manualMoves}");
        report.AppendLine("多余行所在目录（这些目录名在库里将不再有记录）：");
        foreach (var ((rootId, dirRel), count) in dupeDirs.OrderByDescending(p => p.Value))
            report.AppendLine($"    [{rootId}] {dirRel}  {count} 行");
        report.AppendLine();
        report.AppendLine("例：");
        foreach (var example in examples) report.AppendLine("    " + example);
        report.AppendLine();

        if (!apply)
        {
            report.AppendLine("（预演，未写入）");
            return report.ToString();
        }

        var imagesBefore = Scalar(db, "SELECT COUNT(*) FROM images");
        var linksBefore = Scalar(db, "SELECT COUNT(*) FROM image_tags");
        var missingBefore = Scalar(db, "SELECT COUNT(*) FROM images WHERE missing!=0");
        var withTagBefore = Scalar(db,
            "SELECT COUNT(*) FROM images i WHERE i.missing=0 AND EXISTS(SELECT 1 FROM image_tags t WHERE t.image_id=i.id)");
        report.AppendLine("备份：" + FolderService.BackupBeforeChange());

        long deleted = 0;
        using (var tx = store.Transaction())
        {
            foreach (var (live, dupes) in groups)
            {
                foreach (var dupe in dupes)
                {
                    store.Exec($"INSERT OR IGNORE INTO image_tags(image_id,tag_id) " +
                               $"SELECT {live.Id},tag_id FROM image_tags WHERE image_id={dupe.Id}");
                    store.Exec($"INSERT OR IGNORE INTO favorites(image_id,added_at) " +
                               $"SELECT {live.Id},added_at FROM favorites WHERE image_id={dupe.Id}");
                    store.Exec("INSERT OR IGNORE INTO nsfw_scores(image_id,source,rating,value,nsfw_prob," +
                               "confidence,margin,model_name,model_hash,detail,checked_at) " +
                               $"SELECT {live.Id},source,rating,value,nsfw_prob,confidence,margin,model_name," +
                               $"model_hash,detail,checked_at FROM nsfw_scores WHERE image_id={dupe.Id}");
                    store.Exec($"INSERT OR IGNORE INTO image_manual_categories(image_id,category) " +
                               $"SELECT {live.Id},category FROM image_manual_categories WHERE image_id={dupe.Id}");
                    // 标签是从别处继承来的，等同于「这张图的标签已被人管过」，与 SetCategorizedTags 口径一致
                    store.Exec($"UPDATE images SET managed=1 WHERE id={live.Id} AND NOT managed=1 " +
                               $"AND EXISTS(SELECT 1 FROM image_tags WHERE image_id={live.Id})");
                    store.Exec($"DELETE FROM images WHERE id={dupe.Id}");
                    deleted++;
                }
            }
            tx.Success = true;
        }

        // 多余行所在的目录在库里已经没有任何记录了，它的 sidecar 状态键就是死键，顺手清掉
        var cleanedStates = 0;
        foreach (var ((rootId, dirRel), _) in dupeDirs)
        {
            if (Scalar(db, $"SELECT COUNT(*) FROM images WHERE root_id={rootId} AND dir_rel='{Esc(dirRel)}'") != 0) continue;
            var root = store.RootPath((int)rootId);
            if (root is null) continue;
            var folder = dirRel.Length > 0 ? Path.Combine(root, dirRel.Replace('/', '\\')) : root;
            var statePath = Path.Combine(folder, StoreService.SidecarName);
            cleanedStates += (int)Scalar(db,
                $"SELECT COUNT(*) FROM sidecar_state WHERE path='{Esc(statePath)}' COLLATE NOCASE");
            store.Exec($"DELETE FROM sidecar_state WHERE path='{Esc(statePath)}' COLLATE NOCASE");
        }

        store.SyncSidecars(liveIds); // 活行所在目录的 sidecar 跟着重写

        var imagesAfter = Scalar(db, "SELECT COUNT(*) FROM images");
        var linksAfter = Scalar(db, "SELECT COUNT(*) FROM image_tags");
        var missingAfter = Scalar(db, "SELECT COUNT(*) FROM images WHERE missing!=0");
        var withTagAfter = Scalar(db,
            "SELECT COUNT(*) FROM images i WHERE i.missing=0 AND EXISTS(SELECT 1 FROM image_tags t WHERE t.image_id=i.id)");

        report.AppendLine("== 对账 ==");
        report.AppendLine($"images   ：{imagesBefore} → {imagesAfter}（删除多余行 {deleted}）" +
                          (imagesBefore - imagesAfter == deleted ? " ✓" : " ✗"));
        report.AppendLine($"missing  ：{missingBefore} → {missingAfter}");
        report.AppendLine($"image_tags：{linksBefore} → {linksAfter}" +
                          (linksBefore == linksAfter ? "（标签只是在两行间换了归属）✓" : " ✗"));
        report.AppendLine($"有标签的活行：{withTagBefore} → {withTagAfter}");
        report.AppendLine($"sidecar 状态键清理：{cleanedStates} 行");
        report.AppendLine("完成。重启 Gallery 后生效。");
        return report.ToString();
    }

    // ------------------------------------------------------------------ 只读小工具

    private static string PathKey(string dirRel, string name) =>
        (dirRel.TrimEnd('/') + "/" + name).ToLowerInvariant();

    private static List<Row> ReadRows(string dbPath)
    {
        var list = new List<Row>();
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,root_id,dir_rel,name,missing FROM images ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new Row(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4)));
        return list;
    }

    private static HashSet<long> TagIds(string dbPath, long imageId)
    {
        var set = new HashSet<long>();
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag_id FROM image_tags WHERE image_id=$i";
        cmd.Parameters.AddWithValue("$i", imageId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetInt64(0));
        return set;
    }

    private static List<string> ManualCategories(string dbPath, long imageId)
    {
        var list = new List<string>();
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT category FROM image_manual_categories WHERE image_id=$i";
        cmd.Parameters.AddWithValue("$i", imageId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    private static bool Exists(string dbPath, string table, long imageId)
        => Scalar(dbPath, $"SELECT COUNT(*) FROM {table} WHERE image_id={imageId}") > 0;

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
