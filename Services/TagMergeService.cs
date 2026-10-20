using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// 合并重复标签（作品 / 画师）：保留 winner，把 loser 的图片、别名、中文名并过去，再删掉 loser。
///
/// 图片标签走 <see cref="StoreService.SetCategorizedTags"/>，所以该目录的 .gallery.json 会跟着重写，
/// 库与 sidecar 保持一致；loser 的名字会作为 winner 的别名留下，
/// 配合 EnsureTag 的同分类别名重定向，将来同一个旧名再出现也不会分裂成新标签。
///
/// 计划文件（JSON 数组）：
///   [{"loser":["work","honkai"],"winner":["work","honkai:_star_rail"],"zh":"崩坏星穹铁道"}]
/// zh 可选：写进 winner 的中文显示名（tag_profiles.zh）；winner 已有中文名时不覆盖。
/// </summary>
public static class TagMergeService
{
    public sealed record PlanItem(
        string LoserCategory, string LoserName, string WinnerCategory, string WinnerName, string Zh);

    public static List<PlanItem> LoadPlan(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("计划文件必须是 JSON 数组");
        var list = new List<PlanItem>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var loser = e.GetProperty("loser");
            var winner = e.GetProperty("winner");
            list.Add(new PlanItem(
                loser[0].GetString() ?? "", loser[1].GetString() ?? "",
                winner[0].GetString() ?? "", winner[1].GetString() ?? "",
                e.TryGetProperty("zh", out var zh) ? zh.GetString() ?? "" : ""));
        }
        return list;
    }

    /// <summary>执行（或预演）合并。任何一条计划对不上就整体中止，不做半截改动。</summary>
    public static string Run(StoreService store, string planPath, bool apply)
    {
        var report = new StringBuilder();
        var db = store.DbPath;
        var plan = LoadPlan(planPath);
        report.AppendLine($"库    : {db}");
        report.AppendLine($"计划  : {plan.Count} 条 · 模式 = {(apply ? "执行" : "预演（不改任何数据）")}");
        report.AppendLine();

        // ① 先把整份计划解析成 id，有问题就整体退出
        var resolved = new List<(PlanItem Item, long LoserId, long WinnerId)>();
        var problems = new List<string>();
        foreach (var item in plan)
        {
            var label = $"{item.LoserCategory}/{item.LoserName} → {item.WinnerCategory}/{item.WinnerName}";
            var sameCategory = string.Equals(item.LoserCategory, item.WinnerCategory, StringComparison.Ordinal);
            var customToCharacter = item.LoserCategory == "custom" && item.WinnerCategory == "character";
            if (!sameCategory && !customToCharacter)
                problems.Add($"{label}：分类不一致（只允许同分类，或 custom→character）");
            if (item.LoserCategory.Length == 0 || item.LoserName.Length == 0 ||
                item.WinnerName.Length == 0)
                problems.Add($"{label}：分类或名字为空");

            var loserId = Scalar(db, "SELECT id FROM tags WHERE category=$c AND name=$n",
                ("$c", item.LoserCategory), ("$n", item.LoserName));
            var winnerId = Scalar(db, "SELECT id FROM tags WHERE category=$c AND name=$n",
                ("$c", item.WinnerCategory), ("$n", item.WinnerName));
            if (loserId == 0) problems.Add($"{label}：找不到要合并掉的标签");
            if (winnerId == 0) problems.Add($"{label}：找不到要保留的标签");
            if (loserId != 0 && loserId == winnerId) problems.Add($"{label}：两边是同一个标签");
            resolved.Add((item, loserId, winnerId));
        }

        var loserIds = resolved.Where(r => r.LoserId != 0).Select(r => r.LoserId).ToList();
        if (loserIds.Count != loserIds.Distinct().Count())
            problems.Add("计划里有重复的 loser");
        var winnerIds = resolved.Where(r => r.WinnerId != 0).Select(r => r.WinnerId).ToHashSet();
        if (loserIds.Any(winnerIds.Contains))
            problems.Add("计划里有链式合并（某个 loser 同时是别人的 winner），请拆成两轮");

        if (problems.Count > 0)
        {
            report.AppendLine("计划有问题，未做任何改动：");
            foreach (var p in problems) report.AppendLine("  ! " + p);
            return report.ToString();
        }

        var imagesBefore = Scalar(db, "SELECT COUNT(*) FROM image_tags");
        var tagsBefore = Scalar(db, "SELECT COUNT(*) FROM tags");
        long deduped = 0, moved = 0;
        var backedUp = false;

        foreach (var (item, loserId, winnerId) in resolved)
        {
            var images = Rows(db, "SELECT image_id FROM image_tags WHERE tag_id=$t ORDER BY image_id",
                ("$t", loserId)).Select(long.Parse).ToList();
            var winnerImages = Rows(db, "SELECT image_id FROM image_tags WHERE tag_id=$t", ("$t", winnerId))
                .Select(long.Parse).ToHashSet();
            var overlap = images.Count(winnerImages.Contains);
            var loserZh = Text(db, "SELECT zh FROM tag_profiles WHERE tag_id=$t", ("$t", loserId));
            var winnerZh = Text(db, "SELECT zh FROM tag_profiles WHERE tag_id=$t", ("$t", winnerId));
            var loserEn = Text(db, "SELECT en FROM tag_profiles WHERE tag_id=$t", ("$t", loserId));
            var winnerEn = Text(db, "SELECT en FROM tag_profiles WHERE tag_id=$t", ("$t", winnerId));
            var loserEntity = Text(db, "SELECT entity_key FROM tag_profiles WHERE tag_id=$t", ("$t", loserId));
            var winnerEntity = Text(db, "SELECT entity_key FROM tag_profiles WHERE tag_id=$t", ("$t", winnerId));
            var aliases = Rows(db, "SELECT alias FROM tag_aliases WHERE tag_id=$t ORDER BY alias",
                ("$t", loserId)).Where(a => a.Length > 0).ToList();

            report.AppendLine($"[{item.LoserCategory}] {item.LoserName} (#{loserId}) → {item.WinnerName} (#{winnerId})");
            report.AppendLine($"    图片 {images.Count} 张：其中 {images.Count - overlap} 张需要搬到 winner，" +
                              $"{overlap} 张本来就有 winner（只去掉重复关联）");
            report.AppendLine($"    中文名：winner = '{winnerZh}'" +
                              (loserZh.Length == 0 ? "" : $" · loser = '{loserZh}'") +
                              (item.Zh.Length == 0 ? "" : $" · 计划 = '{item.Zh}'"));
            if (aliases.Count > 0) report.AppendLine("    一并带走的别名：" + string.Join("、", aliases));
            if (!apply)
            {
                report.AppendLine("    （预演，未写入）");
                report.AppendLine();
                continue;
            }

            if (!backedUp)
            {
                report.AppendLine("    备份：" + FolderService.BackupBeforeChange());
                backedUp = true;
            }

            using (var tx = store.Transaction())
            {
                foreach (var imageId in images)
                {
                    var pairs = store.CategorizedTags((int)imageId);
                    var next = pairs
                        .Where(p => !(p.Category == item.LoserCategory && p.Name == item.LoserName))
                        .ToList();
                    if (!next.Any(p => p.Category == item.WinnerCategory && p.Name == item.WinnerName))
                        next.Add((item.WinnerCategory, item.WinnerName));
                    store.SetCategorizedTags([(int)imageId], next);
                }

                // 别名与中文名先搬，再删 loser（tag_aliases / tag_profiles 都是级联删除）
                store.Exec($"INSERT OR IGNORE INTO tag_aliases(tag_id,alias) " +
                           $"SELECT {winnerId},alias FROM tag_aliases WHERE tag_id={loserId}");
                store.Exec($"INSERT OR IGNORE INTO tag_aliases(tag_id,alias) " +
                           $"VALUES({winnerId},'{Esc(item.LoserName)}')");
                var zh = !string.IsNullOrWhiteSpace(item.Zh) && string.IsNullOrWhiteSpace(winnerZh) ? item.Zh
                    : string.IsNullOrWhiteSpace(winnerZh) ? loserZh : "";
                if (zh.Length > 0)
                    store.Exec($"INSERT INTO tag_profiles(tag_id,zh) VALUES({winnerId},'{Esc(zh)}') " +
                               "ON CONFLICT(tag_id) DO UPDATE SET zh=excluded.zh");
                store.Exec($"INSERT OR IGNORE INTO tag_profiles(tag_id) VALUES({winnerId})");
                if (loserEn.Length > 0 && winnerEn.Length == 0)
                    store.Exec($"UPDATE tag_profiles SET en='{Esc(loserEn)}' WHERE tag_id={winnerId}");
                if (loserEntity.Length > 0 && winnerEntity.Length == 0)
                    store.Exec($"UPDATE tag_profiles SET entity_key='{Esc(loserEntity)}' WHERE tag_id={winnerId}");
                store.Exec($"DELETE FROM tags WHERE id={loserId}");
                tx.Success = true;
            }

            store.SyncSidecars(images.Select(i => (int)i).ToList()); // 事务内不会自动写 sidecar
            deduped += overlap;
            moved += images.Count - overlap;
            report.AppendLine("    已合并，sidecar 已重写");
            report.AppendLine();
        }

        if (!apply) return report.ToString();

        var imagesAfter = Scalar(db, "SELECT COUNT(*) FROM image_tags");
        var tagsAfter = Scalar(db, "SELECT COUNT(*) FROM tags");
        var leftovers = resolved.Sum(r => Scalar(db,
            "SELECT COUNT(*) FROM image_tags WHERE tag_id=$t", ("$t", r.LoserId)));
        var dangling = resolved.Sum(r =>
            Scalar(db, "SELECT COUNT(*) FROM tags WHERE id=$t", ("$t", r.LoserId)));

        report.AppendLine("== 对账 ==");
        report.AppendLine($"image_tags：{imagesBefore} → {imagesAfter}（-{imagesBefore - imagesAfter}；" +
                          $"重复关联 {deduped}、转移 {moved}）");
        report.AppendLine($"标签总数  ：{tagsBefore} → {tagsAfter}（应减少 {resolved.Count}）" +
                          (tagsBefore - tagsAfter == resolved.Count ? " ✓" : " ✗"));
        report.AppendLine($"loser 残留：关联 {leftovers} 行、标签 {dangling} 行" +
                          (leftovers == 0 && dangling == 0 ? " ✓" : " ✗"));
        report.AppendLine("完成。重启 Gallery 后生效（标签列表在内存里有缓存）。");
        return report.ToString();
    }

    // ------------------------------------------------------------------ 只读小工具（独立连接，事务外看已提交数据）

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static long Scalar(string dbPath, string sql, params (string Key, object Value)[] args)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private static string Text(string dbPath, string sql, params (string Key, object Value)[] args)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? "" : Convert.ToString(result) ?? "";
    }

    private static List<string> Rows(string dbPath, string sql, params (string Key, object Value)[] args)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0)) ?? "");
        return list;
    }

    private static string Esc(string value) => value.Replace("'", "''");
}
