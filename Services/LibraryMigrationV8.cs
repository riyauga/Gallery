using Microsoft.Data.Sqlite;

namespace Gallery.Services;

internal static class LibraryMigrationV8
{
    public const int TargetVersion = 8;

    // Public rows remain the baseline. The views apply personal edits without
    // copying public data into backups or losing removals on the next pack import.
    internal const string SchemaSql = """
        CREATE TABLE catalog_personal_entries(
            key TEXT NOT NULL, kind TEXT NOT NULL, created INTEGER NOT NULL,
            zh TEXT, en TEXT, aliases TEXT, terms TEXT, origin TEXT,
            PRIMARY KEY(kind,key)
        );
        CREATE TABLE catalog_personal_memberships(
            identity_key TEXT NOT NULL, game_key TEXT NOT NULL, included INTEGER NOT NULL,
            PRIMARY KEY(identity_key,game_key)
        );
        CREATE TABLE catalog_personal_tags(
            tag_key TEXT PRIMARY KEY, category TEXT NOT NULL, name TEXT NOT NULL,
            target TEXT NOT NULL, display_mode TEXT NOT NULL, display_name TEXT NOT NULL,
            name_fallback INTEGER NOT NULL
        );
        INSERT INTO catalog_personal_entries(key,kind,created,zh,en,aliases,terms,origin)
            SELECT key,kind,0,zh,en,aliases,COALESCE(
                (SELECT json_group_array(n.term) FROM catalog_names n WHERE n.entity_key=e.entity_key),'[]'),NULL
            FROM (
                SELECT CASE WHEN kind='work' THEN game_key ELSE identity_key END key,
                    kind,zh,en,aliases,entity_key,
                    ROW_NUMBER() OVER(PARTITION BY kind,CASE WHEN kind='work' THEN game_key ELSE identity_key END
                        ORDER BY entity_key) rank
                FROM catalog_entities WHERE source_kind='manual' AND kind IN ('work','character')
            ) e WHERE rank=1 AND key<>'';
        CREATE INDEX IF NOT EXISTS idx_catalog_entities_identity ON catalog_entities(identity_key);
        CREATE INDEX IF NOT EXISTS idx_catalog_entities_game_kind ON catalog_entities(game_key,kind);
        CREATE VIEW catalog_effective_games AS
            SELECT g.game_key,COALESCE(p.zh,m.zh,g.zh) zh,COALESCE(p.en,m.en,g.en) en,
                   COALESCE(p.aliases,m.aliases,g.aliases) aliases,g.source_url
            FROM catalog_games g LEFT JOIN catalog_personal_entries p ON p.kind='work' AND p.key=g.game_key
            LEFT JOIN catalog_entities m ON m.entity_key=(SELECT e.entity_key FROM catalog_entities e
                WHERE e.game_key=g.game_key AND e.kind='work' AND e.source_kind='manual' ORDER BY e.entity_key LIMIT 1)
            UNION ALL
            SELECT key,zh,en,aliases,'' FROM catalog_personal_entries WHERE kind='work' AND created=1
              AND NOT EXISTS(SELECT 1 FROM catalog_games g WHERE g.game_key=key);
        CREATE VIEW catalog_effective_memberships AS
            SELECT DISTINCT b.identity_key,b.game_key FROM (
                SELECT identity_key,game_key FROM catalog_memberships
                UNION ALL
                SELECT identity_key,game_key FROM catalog_entities WHERE kind='character' AND game_key<>''
                UNION ALL
                SELECT identity_key,game_key FROM catalog_personal_memberships WHERE included=1
                UNION ALL
                SELECT key,origin FROM catalog_personal_entries WHERE kind='character' AND origin IS NOT NULL
            ) b JOIN catalog_effective_games g ON g.game_key=b.game_key
            WHERE NOT EXISTS (SELECT 1 FROM catalog_personal_memberships p
                WHERE p.identity_key=b.identity_key AND p.game_key=b.game_key AND p.included=0)
              AND (EXISTS(SELECT 1 FROM catalog_entities e WHERE e.identity_key=b.identity_key AND e.kind='character')
                   OR EXISTS(SELECT 1 FROM catalog_personal_entries p WHERE p.kind='character' AND p.key=b.identity_key AND p.created=1));
        CREATE VIEW catalog_character_origins AS
            WITH members AS MATERIALIZED (SELECT identity_key,game_key FROM catalog_effective_memberships),
            choices AS (
                SELECT m.identity_key,m.game_key,ROW_NUMBER() OVER(PARTITION BY m.identity_key
                    ORDER BY (m.game_key=p.origin) DESC,
                        (m.game_key=e.game_key) DESC,
                        (SELECT MIN(r.entity_key) FROM catalog_entities r WHERE r.kind='character'
                            AND r.identity_key=m.identity_key AND r.game_key=m.game_key) IS NULL,
                        (SELECT MIN(r.entity_key) FROM catalog_entities r WHERE r.kind='character'
                            AND r.identity_key=m.identity_key AND r.game_key=m.game_key),
                        m.game_key) rank
                FROM members m LEFT JOIN catalog_personal_entries p ON p.kind='character' AND p.key=m.identity_key
                LEFT JOIN identities i ON i.identity_key=m.identity_key
                LEFT JOIN catalog_entities e ON e.entity_key=i.primary_entity_key AND e.identity_key=m.identity_key
            )
            SELECT identity_key,game_key FROM choices WHERE rank=1;
        CREATE VIEW catalog_effective_entities AS
            SELECT e.entity_key,e.identity_key,
                   CASE WHEN e.kind='character' AND e.game_key<>''
                          AND (NOT EXISTS(SELECT 1 FROM catalog_effective_games g WHERE g.game_key=e.game_key)
                               OR EXISTS(SELECT 1 FROM catalog_personal_memberships m WHERE m.identity_key=e.identity_key AND m.game_key=e.game_key AND m.included=0))
                        THEN '' ELSE e.game_key END game_key,
                   e.kind,COALESCE(p.zh,e.zh) zh,COALESCE(p.en,e.en) en,COALESCE(p.aliases,e.aliases) aliases,
                   e.status,e.source_url,e.source_kind
            FROM catalog_entities e LEFT JOIN catalog_personal_entries p ON p.kind=e.kind
                AND p.key=CASE WHEN e.kind='work' THEN e.game_key ELSE e.identity_key END
            UNION ALL
            SELECT p.key,p.key,COALESCE(o.game_key,''),'character',p.zh,p.en,p.aliases,'unknown','','local'
            FROM catalog_personal_entries p LEFT JOIN catalog_character_origins o ON o.identity_key=p.key
            WHERE p.kind='character' AND p.created=1
            UNION ALL
            SELECT 'work:'||g.game_key,'work:'||g.game_key,g.game_key,'work',g.zh,g.en,g.aliases,'unknown',g.source_url,
                   CASE WHEN p.created=1 THEN 'local' ELSE 'community' END
            FROM catalog_effective_games g LEFT JOIN catalog_personal_entries p ON p.kind='work' AND p.key=g.game_key
            WHERE NOT EXISTS(SELECT 1 FROM catalog_entities e WHERE e.game_key=g.game_key AND e.kind='work');
        CREATE VIEW catalog_effective_names AS
            SELECT n.term,n.entity_key FROM catalog_names n JOIN catalog_entities e ON e.entity_key=n.entity_key
            WHERE NOT EXISTS(SELECT 1 FROM catalog_personal_entries p WHERE p.kind=e.kind AND p.terms IS NOT NULL
                AND p.key=CASE WHEN e.kind='work' THEN e.game_key ELSE e.identity_key END)
            UNION
            SELECT j.value,e.entity_key FROM catalog_personal_entries p JOIN catalog_effective_entities e
                ON e.kind=p.kind AND p.key=CASE WHEN e.kind='work' THEN e.game_key ELSE e.identity_key END
                JOIN json_each(p.terms) j;
        """;

    public static void Upgrade(SqliteConnection conn, string dbPath)
    {
        var backups = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
        Directory.CreateDirectory(backups);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", Path.Combine(backups, $"library-v7-{Guid.NewGuid():N}.db"));
        cmd.ExecuteNonQuery();
        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;
        cmd.CommandText = SchemaSql + "\nUPDATE meta SET value='8' WHERE key='schema_version'";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
