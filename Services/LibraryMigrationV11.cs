using Microsoft.Data.Sqlite;

namespace Gallery.Services;

internal static class LibraryMigrationV11
{
    public const int TargetVersion = 11;
    internal const string SchemaSql = """
        CREATE TABLE catalog_assets(hash TEXT PRIMARY KEY,image BLOB NOT NULL);
        CREATE TABLE catalog_entries(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            entity_id TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL CHECK(kind IN ('work','character')),
            legacy_key TEXT NOT NULL,
            zh TEXT NOT NULL,en TEXT NOT NULL,aliases TEXT NOT NULL,
            origin_entity_id TEXT REFERENCES catalog_entries(entity_id),
            cover_hash TEXT REFERENCES catalog_assets(hash),
            created INTEGER NOT NULL,edited INTEGER NOT NULL,
            UNIQUE(kind,legacy_key)
        );
        CREATE TABLE catalog_entity_ids(
            entity_id TEXT PRIMARY KEY,
            target_entity_id TEXT NOT NULL REFERENCES catalog_entries(entity_id) ON DELETE CASCADE
        );
        CREATE INDEX idx_catalog_entity_ids_target ON catalog_entity_ids(target_entity_id);
        CREATE TABLE catalog_appearances(
            character_entity_id TEXT NOT NULL REFERENCES catalog_entries(entity_id) ON DELETE CASCADE,
            work_entity_id TEXT NOT NULL REFERENCES catalog_entries(entity_id) ON DELETE CASCADE,
            PRIMARY KEY(character_entity_id,work_entity_id)
        );
        CREATE TABLE catalog_distinct_pairs(
            entity_id_a TEXT NOT NULL,entity_id_b TEXT NOT NULL,
            CHECK(entity_id_a<entity_id_b),PRIMARY KEY(entity_id_a,entity_id_b)
        );
        CREATE INDEX IF NOT EXISTS idx_catalog_entities_kind_game ON catalog_entities(kind,game_key);
        CREATE INDEX idx_catalog_entries_origin ON catalog_entries(origin_entity_id);
        """;

    public static void Initialize(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SchemaSql;
        cmd.ExecuteNonQuery();
        var names = new[] { "catalog_effective_names", "catalog_effective_entities", "catalog_character_origins",
            "catalog_effective_memberships", "catalog_effective_games" };
        var definitions = new List<string>();
        foreach (var name in names)
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='view' AND name=$name";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$name", name);
            var definition = (string)cmd.ExecuteScalar()!;
            foreach (var oldName in names) definition = definition.Replace(oldName, oldName.Replace("catalog_", "catalog_legacy_"));
            definitions.Add(definition);
        }
        cmd.Parameters.Clear();
        foreach (var name in names) { cmd.CommandText = "DROP VIEW " + name; cmd.ExecuteNonQuery(); }
        foreach (var definition in definitions) { cmd.CommandText = definition; cmd.ExecuteNonQuery(); }
        var deleted = new List<(string Kind, string Key)>();
        cmd.CommandText = "SELECT kind,key FROM catalog_deleted_entries ORDER BY kind";
        using (var r = cmd.ExecuteReader()) while (r.Read()) deleted.Add((r.GetString(0), r.GetString(1)));
        foreach (var row in deleted) TagCatalogService.EraseCatalogEntry(conn, tx, row.Kind, row.Key);
        TagCatalogService.SyncLegacyCatalog(conn, tx);
        cmd.CommandText = """
            CREATE VIEW catalog_effective_games AS
                SELECT legacy_key game_key,zh,en,aliases,'' source_url FROM catalog_entries WHERE kind='work';
            CREATE VIEW catalog_effective_memberships AS
                SELECT c.legacy_key identity_key,w.legacy_key game_key FROM catalog_appearances a
                JOIN catalog_entries c ON c.entity_id=a.character_entity_id
                JOIN catalog_entries w ON w.entity_id=a.work_entity_id;
            CREATE VIEW catalog_character_origins AS
                SELECT c.legacy_key identity_key,w.legacy_key game_key FROM catalog_entries c
                JOIN catalog_entries w ON w.entity_id=c.origin_entity_id WHERE c.kind='character';
            CREATE VIEW catalog_effective_entities AS
                SELECT COALESCE(l.entity_key,CASE WHEN c.kind='work' THEN 'work:'||c.legacy_key ELSE c.legacy_key END) entity_key,
                    CASE WHEN c.kind='work' THEN COALESCE(l.identity_key,'work:'||c.legacy_key) ELSE c.legacy_key END identity_key,
                    CASE WHEN c.kind='work' THEN c.legacy_key ELSE COALESCE(w.legacy_key,'') END game_key,
                    c.kind,c.zh,c.en,c.aliases,COALESCE(l.status,'unknown') status,
                    COALESCE(l.source_url,'') source_url,
                    CASE WHEN c.created=1 THEN 'local' ELSE COALESCE(l.source_kind,'community') END source_kind
                FROM catalog_entries c LEFT JOIN catalog_entries w ON w.entity_id=c.origin_entity_id
                LEFT JOIN catalog_entities l ON
                    (c.kind='work' AND l.kind='work' AND l.game_key=c.legacy_key)
                    OR (c.kind='character' AND l.kind='character' AND l.identity_key=c.legacy_key)
                UNION ALL SELECT * FROM catalog_entities WHERE kind NOT IN ('work','character');
            CREATE VIEW catalog_effective_names AS
                SELECT n.term,n.entity_key FROM catalog_names n
                JOIN catalog_effective_entities e ON e.entity_key=n.entity_key
                WHERE NOT EXISTS(SELECT 1 FROM catalog_personal_entries p WHERE p.kind=e.kind AND p.terms IS NOT NULL
                    AND p.key=CASE WHEN e.kind='work' THEN e.game_key ELSE e.identity_key END)
                UNION
                SELECT j.value,e.entity_key FROM catalog_personal_entries p JOIN catalog_effective_entities e
                    ON e.kind=p.kind AND p.key=CASE WHEN e.kind='work' THEN e.game_key ELSE e.identity_key END
                    JOIN json_each(p.terms) j;
            UPDATE meta SET value='11' WHERE key='schema_version';
            """;
        cmd.ExecuteNonQuery();
    }

    public static void Upgrade(SqliteConnection conn, string dbPath)
    {
        var backups = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
        Directory.CreateDirectory(backups);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", Path.Combine(backups, $"library-v10-{Guid.NewGuid():N}.db"));
        cmd.ExecuteNonQuery();
        using var tx = conn.BeginTransaction();
        Initialize(conn, tx);
        tx.Commit();
    }
}
