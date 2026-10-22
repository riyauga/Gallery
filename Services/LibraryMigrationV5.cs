using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// 结构 v4 → v5：视频接入。只做增量列，不改动既有图片行：
/// media_kind 默认 image，duration 默认 NULL。
/// 迁移前 VACUUM INTO 备份，结构变更与版本号同事务；可重复执行。
/// </summary>
internal static class LibraryMigrationV5
{
    public const int TargetVersion = 5;
    private const string ReportKey = "migration_v5";

    public static string Upgrade(SqliteConnection conn, string dbPath)
    {
        var backup = Backup(conn, dbPath);
        using var tx = conn.BeginTransaction();
        try
        {
            if (!ColumnExists(conn, tx, "images", "media_kind"))
                Exec(conn, tx, "ALTER TABLE images ADD COLUMN media_kind TEXT NOT NULL DEFAULT 'image'");
            if (!ColumnExists(conn, tx, "images", "duration"))
                Exec(conn, tx, "ALTER TABLE images ADD COLUMN duration REAL");

            var report = new Dictionary<string, object?>
            {
                ["schema_before"] = 4,
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
        var target = Path.Combine(dir, $"library-v4-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        if (File.Exists(target))
            target = Path.Combine(dir, $"library-v4-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p";
        cmd.Parameters.AddWithValue("$p", target);
        cmd.ExecuteNonQuery();
        return target;
    }

    private static bool ColumnExists(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void WriteMeta(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO meta(key,value) VALUES($k,$v) " +
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
