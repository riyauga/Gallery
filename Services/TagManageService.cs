using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>标签管理页的数据服务：查看、改显示名、批量删除、生成合并计划。</summary>
public static class TagManageService
{
    public sealed record TagRow(int Id, string Category, string Name, string Zh, string En, string Identity, int Usage);

    public static List<TagRow> List(string keyword = "")
    {
        using var conn = new SqliteConnection("Data Source=" + AppPaths.DbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.id,t.category,t.name,COALESCE(p.zh,''),COALESCE(p.en,''),
                   COALESCE(i.identity_key,''),(SELECT COUNT(*) FROM image_tags it WHERE it.tag_id=t.id)
            FROM tags t LEFT JOIN tag_profiles p ON p.tag_id=t.id
            LEFT JOIN tag_identities i ON i.tag_id=t.id
            WHERE $q='' OR t.name LIKE '%'||$q||'%' OR COALESCE(p.zh,'') LIKE '%'||$q||'%'
               OR COALESCE(p.en,'') LIKE '%'||$q||'%' OR COALESCE(i.identity_key,'') LIKE '%'||$q||'%'
            ORDER BY t.category,t.name COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$q", keyword);
        using var r = cmd.ExecuteReader();
        var rows = new List<TagRow>();
        while (r.Read())
            rows.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetInt32(6)));
        return rows;
    }

    /// <summary>改显示名：只写 tag_profiles，不动 tags.name，避免破坏图片关联与身份对齐。</summary>
    public static void RenameDisplay(int tagId, string zh, string en)
    {
        using var conn = new SqliteConnection("Data Source=" + AppPaths.DbPath);
        conn.Open();
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "INSERT OR IGNORE INTO tag_profiles(tag_id) VALUES($t)", ("$t", tagId));
        Exec(conn, tx, "UPDATE tag_profiles SET zh=$z,en=$e WHERE tag_id=$t",
            ("$z", zh), ("$e", en), ("$t", tagId));
        tx.Commit();
    }

    /// <summary>批量删标签：移除所有图片上的该标签，再删标签行；不删图片。返回移除的关联数。</summary>
    public static int Delete(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return 0;
        using var conn = new SqliteConnection("Data Source=" + AppPaths.DbPath);
        conn.Open();
        using var tx = conn.BeginTransaction();
        var removed = 0;
        foreach (var id in ids)
        {
            using var count = conn.CreateCommand();
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM image_tags WHERE tag_id=$t";
            count.Parameters.AddWithValue("$t", id);
            removed += Convert.ToInt32(count.ExecuteScalar());
            Exec(conn, tx, "DELETE FROM image_tags WHERE tag_id=$t", ("$t", id));
            Exec(conn, tx, "DELETE FROM tags WHERE id=$t", ("$t", id));
        }
        tx.Commit();
        return removed;
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
