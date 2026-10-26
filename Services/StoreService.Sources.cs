using System.Text.RegularExpressions;

namespace Gallery.Services;

/// <summary>
/// 按文件名里的 danbooru md5 反查来源：浏览器扩展查完 Danbooru 后经桥接回写。
/// 这里的 md5 只做「查找键」，永远不改写文件名或 orig_md5。
/// </summary>
public sealed partial class StoreService
{
    /// <summary>danbooru 下载器命名：<c>__标签_drawn_by_画师__&lt;32位md5&gt;.jpg</c>。</summary>
    private static readonly Regex DanbooruMd5 = new(@"^__.+__([0-9a-fA-F]{32})$", RegexOptions.Compiled);

    public sealed record SourceBackfillItem(string Md5, string Name, int Count);

    /// <summary>缺来源、且文件名带 danbooru md5 的图，按 md5 去重（同 md5 多张只查一次）。</summary>
    public List<SourceBackfillItem> PendingSourceBackfill()
    {
        var map = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM images WHERE COALESCE(TRIM(source_url),'')=''";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var stem = Path.GetFileNameWithoutExtension(name);
            var match = DanbooruMd5.Match(stem);
            if (!match.Success) continue;
            var md5 = match.Groups[1].Value.ToLowerInvariant();
            map[md5] = map.TryGetValue(md5, out var old) ? (old.Name, old.Count + 1) : (name, 1);
        }
        return map
            .Select(pair => new SourceBackfillItem(pair.Key, pair.Value.Name, pair.Value.Count))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 回写查到的出处。只填空值，已有来源一律不覆盖；返回真正改动的图片数。
    /// </summary>
    public int ApplySourceBackfill(IReadOnlyCollection<(string Md5, string PageUrl, string SourceUrl, string PostId)> rows)
    {
        if (rows.Count == 0) return 0;

        var wanted = rows
            .Where(row => row.Md5.Length > 0 && (row.PageUrl.Length > 0 || row.SourceUrl.Length > 0))
            .GroupBy(row => row.Md5, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return 0;

        var targets = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        using (var select = _conn.CreateCommand())
        {
            select.CommandText = "SELECT id,name FROM images WHERE COALESCE(TRIM(source_url),'')=''";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var match = DanbooruMd5.Match(Path.GetFileNameWithoutExtension(reader.GetString(1)));
                if (!match.Success) continue;
                var md5 = match.Groups[1].Value.ToLowerInvariant();
                if (!wanted.ContainsKey(md5)) continue;
                if (!targets.TryGetValue(md5, out var list)) targets[md5] = list = [];
                list.Add(reader.GetInt32(0));
            }
        }
        if (targets.Count == 0) return 0;

        var changed = new List<int>();
        using var tx = _conn.BeginTransaction();
        using var update = _conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText =
            "UPDATE images SET " +
            "page_url=COALESCE(NULLIF(TRIM(page_url),''),$page)," +
            "source_url=COALESCE(NULLIF(TRIM(source_url),''),$source)," +
            "post_id=COALESCE(NULLIF(TRIM(post_id),''),$post) WHERE id=$id";
        var pId = update.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pPage = update.Parameters.Add("$page", Microsoft.Data.Sqlite.SqliteType.Text);
        var pSource = update.Parameters.Add("$source", Microsoft.Data.Sqlite.SqliteType.Text);
        var pPost = update.Parameters.Add("$post", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var (md5, ids) in targets)
        {
            var row = wanted[md5];
            pPage.Value = (object?)NullIfEmpty(row.PageUrl) ?? DBNull.Value;
            pSource.Value = (object?)NullIfEmpty(row.SourceUrl) ?? DBNull.Value;
            pPost.Value = (object?)NullIfEmpty(row.PostId) ?? DBNull.Value;
            if (pPage.Value is DBNull && pSource.Value is DBNull) continue;
            foreach (var id in ids)
            {
                pId.Value = id;
                if (update.ExecuteNonQuery() > 0) changed.Add(id);
            }
        }
        tx.Commit();
        if (changed.Count > 0) SyncSidecars(changed);
        return changed.Count;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
