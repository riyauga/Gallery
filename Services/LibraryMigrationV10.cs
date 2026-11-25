using Microsoft.Data.Sqlite;

namespace Gallery.Services;

internal static class LibraryMigrationV10
{
    public const int TargetVersion = 10;

    // catalog_character_origins 的相关子查询按 (kind, identity_key, game_key) 查 catalog_entities；
    // 缺这条复合索引时，按作品键查派生角色约需 2 秒，补上后约 95 毫秒。
    internal const string SchemaSql = """
        CREATE INDEX IF NOT EXISTS idx_catalog_entities_kind_identity_game
            ON catalog_entities(kind,identity_key,game_key);
        """;

    public static void Upgrade(SqliteConnection conn, string dbPath)
    {
        var backups = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
        Directory.CreateDirectory(backups);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", Path.Combine(backups, $"library-v9-{Guid.NewGuid():N}.db"));
        cmd.ExecuteNonQuery();
        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;
        cmd.CommandText = SchemaSql + "\nUPDATE meta SET value='10' WHERE key='schema_version'";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
