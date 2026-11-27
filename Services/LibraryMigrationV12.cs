using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>移除自动分辨率标签；数据库和现行侧车分别备份、可独立重试。</summary>
internal static class LibraryMigrationV12
{
    public const int TargetVersion = 12;

    public static void Initialize(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE retired_tag_sidecars(root_path TEXT PRIMARY KEY);
            INSERT INTO retired_tag_sidecars SELECT path FROM roots;
            """;
        cmd.ExecuteNonQuery();
        var ids = new List<int>();
        cmd.CommandText = "SELECT id FROM tags WHERE category='resolution'";
        using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt32(0));
        var states = TagDisplayState.Load(conn, tx);
        foreach (var id in ids) states.Remove(id);
        TagDisplayState.Save(conn, states, tx);
        cmd.CommandText = """
            DELETE FROM image_tags WHERE tag_id IN (SELECT id FROM tags WHERE category='resolution');
            DELETE FROM tag_aliases WHERE tag_id IN (SELECT id FROM tags WHERE category='resolution');
            DELETE FROM tag_profiles WHERE tag_id IN (SELECT id FROM tags WHERE category='resolution');
            DELETE FROM tag_identities WHERE tag_id IN (SELECT id FROM tags WHERE category='resolution');
            DELETE FROM catalog_personal_tags WHERE category='resolution'
                OR tag_key IN (SELECT tag_key FROM tags WHERE category='resolution');
            DELETE FROM image_manual_categories WHERE category='resolution';
            DELETE FROM tags WHERE category='resolution';
            """;
        cmd.ExecuteNonQuery();
        foreach (var (table, key) in new[] { ("ingest_queue", "id"), ("review_drafts", "path_key"), ("archive_jobs", "id") })
        {
            var rows = new List<(object Key, string Payload)>();
            cmd.CommandText = $"SELECT {key},payload FROM {table}";
            using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add((r.GetValue(0), r.GetString(1)));
            foreach (var row in rows)
            {
                var clean = RetiredResolutionTags.CleanPayload(row.Payload);
                if (clean == row.Payload) continue;
                cmd.CommandText = $"UPDATE {table} SET payload=$p WHERE {key}=$id";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$p", clean);
                cmd.Parameters.AddWithValue("$id", row.Key);
                cmd.ExecuteNonQuery();
            }
            cmd.Parameters.Clear();
        }
        cmd.CommandText = "UPDATE meta SET value='12' WHERE key='schema_version'";
        cmd.ExecuteNonQuery();
    }

    public static void Upgrade(SqliteConnection conn, string dbPath)
    {
        var backups = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
        Directory.CreateDirectory(backups);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", Path.Combine(backups, $"library-v11-{Guid.NewGuid():N}.db"));
        cmd.ExecuteNonQuery();
        using var tx = conn.BeginTransaction();
        Initialize(conn, tx);
        tx.Commit();
    }
}
