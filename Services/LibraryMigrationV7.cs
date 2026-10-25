using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>v6 → v7：独立的角色皮肤、来源标签和图片关联。备份后同事务建表，不改历史标签关系。</summary>
internal static class LibraryMigrationV7
{
    public const int TargetVersion = 7;
    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS character_skins(
            skin_key TEXT PRIMARY KEY,
            identity_key TEXT NOT NULL REFERENCES identities(identity_key),
            game_key TEXT NOT NULL,
            character_tag TEXT NOT NULL,
            zh TEXT NOT NULL DEFAULT '',
            en TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_character_skins_owner ON character_skins(identity_key);
        CREATE TABLE IF NOT EXISTS skin_source_tags(
            source TEXT NOT NULL,
            tag TEXT NOT NULL,
            skin_key TEXT NOT NULL REFERENCES character_skins(skin_key) ON DELETE CASCADE,
            PRIMARY KEY(source,tag)
        );
        CREATE TABLE IF NOT EXISTS image_skins(
            image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
            skin_key TEXT NOT NULL REFERENCES character_skins(skin_key) ON DELETE CASCADE,
            PRIMARY KEY(image_id,skin_key)
        );
        CREATE INDEX IF NOT EXISTS idx_image_skins_skin ON image_skins(skin_key);
        """;
    private const string ReportKey = "migration_v7";

    public static string Upgrade(SqliteConnection conn, string dbPath)
    {
        var backup = Backup(conn, dbPath);
        using var tx = conn.BeginTransaction();
        try
        {
            Exec(conn, tx, SchemaSql);

            var report = new Dictionary<string, object?>
            {
                ["schema_before"] = 6,
                ["schema_after"] = TargetVersion,
                ["backup"] = backup,
                ["checked_at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            WriteMeta(conn, tx, "schema_version", TargetVersion.ToString(CultureInfo.InvariantCulture));
            WriteMeta(conn, tx, ReportKey, JsonSerializer.Serialize(report));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return backup;
    }

    private static string Backup(SqliteConnection conn, string dbPath)
    {
        var dir = Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "backups");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"library-v6-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        if (File.Exists(target))
            target = Path.Combine(dir, $"library-v6-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p";
        cmd.Parameters.AddWithValue("$p", target);
        cmd.ExecuteNonQuery();
        return target;
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void WriteMeta(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) " +
                          "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
