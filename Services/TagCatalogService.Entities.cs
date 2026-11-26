using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Gallery.Services;

public sealed partial class TagCatalogService
{
    // Old keys remain an adapter for image tags and source packages; all portable relationships use UUIDs.
    internal static void SyncLegacyCatalog(SqliteConnection conn, SqliteTransaction tx)
    {
        var entries = ReadEntries(conn, tx, "work", legacy: true).Concat(ReadEntries(conn, tx, "character", legacy: true)).ToArray();
        foreach (var entry in entries) SyncLegacyEntry(conn, tx, entry);
        var active = entries.Select(e => (e.Kind, e.Key)).ToHashSet();
        var stale = new List<(string Kind, string Key)>();
        using (var read = Query(conn, tx, "SELECT kind,legacy_key FROM catalog_entries ORDER BY kind"))
        using (var r = read.ExecuteReader())
            while (r.Read()) if (!active.Contains((r.GetString(0), r.GetString(1)))) stale.Add((r.GetString(0), r.GetString(1)));
        foreach (var row in stale) EraseCatalogEntry(conn, tx, row.Kind, row.Key);
        Exec(conn, tx, """
            DELETE FROM catalog_appearances;
            INSERT OR IGNORE INTO catalog_appearances(character_entity_id,work_entity_id)
                SELECT c.entity_id,w.entity_id FROM catalog_legacy_effective_memberships m
                JOIN catalog_entries c ON c.kind='character' AND c.legacy_key=m.identity_key
                JOIN catalog_entries w ON w.kind='work' AND w.legacy_key=m.game_key;
            """);
    }

    private static void SyncLegacyEntry(SqliteConnection conn, SqliteTransaction tx, LibraryEntry entry)
    {
        var uuid = entry.Kind == "character" && entry.Key.StartsWith("local:") && Guid.TryParse(entry.Key[6..], out var localId)
            ? localId.ToString("D") : Guid.NewGuid().ToString("D");
        Exec(conn, tx, """
            UPDATE catalog_entries SET zh=$zh,en=$en,aliases=$aliases,
                origin_entity_id=(SELECT entity_id FROM catalog_entries WHERE kind='work' AND legacy_key=$origin),
                created=$created,edited=$edited WHERE kind=$kind AND legacy_key=$key;
            INSERT INTO catalog_entries(entity_id,kind,legacy_key,zh,en,aliases,origin_entity_id,created,edited)
            SELECT $uuid,$kind,$key,$zh,$en,$aliases,
                (SELECT entity_id FROM catalog_entries WHERE kind='work' AND legacy_key=$origin),$created,$edited
            WHERE NOT EXISTS(SELECT 1 FROM catalog_entries WHERE kind=$kind AND legacy_key=$key);
            INSERT OR IGNORE INTO catalog_entity_ids(entity_id,target_entity_id)
                SELECT entity_id,entity_id FROM catalog_entries WHERE kind=$kind AND legacy_key=$key;
            """, ("$uuid", uuid), ("$kind", entry.Kind), ("$key", entry.Key), ("$zh", entry.Zh), ("$en", entry.En),
            ("$aliases", JsonSerializer.Serialize(entry.Aliases)), ("$origin", entry.Kind == "character" ? entry.Origin : ""),
            ("$created", entry.Created ? 1 : 0), ("$edited", entry.Edited ? 1 : 0));
        using var cover = Query(conn, tx, "SELECT image FROM catalog_covers WHERE kind=$kind AND key=$key",
            ("$kind", entry.Kind), ("$key", entry.Key));
        var image = cover.ExecuteScalar() as byte[];
        if (image is not null)
        {
            SetCatalogAsset(conn, tx, entry.Kind, entry.Key, image);
            Exec(conn, tx, "DELETE FROM catalog_covers WHERE kind=$kind AND key=$key", ("$kind", entry.Kind), ("$key", entry.Key));
        }
    }

    private static void SetCatalogAsset(SqliteConnection conn, SqliteTransaction tx, string kind, string key, byte[]? image)
    {
        string? hash = image is null ? null : Sha256Hex(image);
        if (image is not null)
            Exec(conn, tx, "INSERT OR IGNORE INTO catalog_assets(hash,image) VALUES($h,$image)", ("$h", hash!), ("$image", image));
        Exec(conn, tx, "UPDATE catalog_entries SET cover_hash=$h WHERE kind=$kind AND legacy_key=$key",
            ("$h", (object?)hash ?? DBNull.Value), ("$kind", kind), ("$key", key));
    }

    private static void CollectCatalogAssets(SqliteConnection conn, SqliteTransaction tx)
        => Exec(conn, tx, "DELETE FROM catalog_assets WHERE hash NOT IN (SELECT cover_hash FROM catalog_entries WHERE cover_hash IS NOT NULL)");
    internal static void EraseCatalogEntry(SqliteConnection conn, SqliteTransaction tx, string kind, string key)
    {
        Exec(conn, tx, """
            DELETE FROM catalog_distinct_pairs WHERE entity_id_a IN
                (SELECT entity_id FROM catalog_entity_ids WHERE target_entity_id=(SELECT entity_id FROM catalog_entries WHERE kind=$kind AND legacy_key=$key))
                OR entity_id_b IN (SELECT entity_id FROM catalog_entity_ids WHERE target_entity_id=(SELECT entity_id FROM catalog_entries WHERE kind=$kind AND legacy_key=$key));
            DELETE FROM catalog_entity_ids WHERE target_entity_id=(SELECT entity_id FROM catalog_entries WHERE kind=$kind AND legacy_key=$key);
            DELETE FROM catalog_appearances WHERE character_entity_id=(SELECT entity_id FROM catalog_entries WHERE kind=$kind AND legacy_key=$key)
                OR work_entity_id=(SELECT entity_id FROM catalog_entries WHERE kind=$kind AND legacy_key=$key);
            UPDATE catalog_entries SET origin_entity_id=NULL WHERE origin_entity_id=(SELECT entity_id FROM catalog_entries WHERE kind=$kind AND legacy_key=$key);
            DELETE FROM catalog_entries WHERE kind=$kind AND legacy_key=$key;
            DELETE FROM catalog_names WHERE entity_key IN
                (SELECT entity_key FROM catalog_entities WHERE kind=$kind AND CASE WHEN kind='work' THEN game_key ELSE identity_key END=$key);
            DELETE FROM catalog_source_tags WHERE entity_key IN
                (SELECT entity_key FROM catalog_entities WHERE kind=$kind AND CASE WHEN kind='work' THEN game_key ELSE identity_key END=$key);
            DELETE FROM catalog_entities WHERE kind=$kind AND CASE WHEN kind='work' THEN game_key ELSE identity_key END=$key;
            DELETE FROM catalog_games WHERE $kind='work' AND game_key=$key;
            DELETE FROM catalog_memberships WHERE ($kind='work' AND game_key=$key) OR ($kind='character' AND identity_key=$key);
            DELETE FROM catalog_personal_entries WHERE kind=$kind AND key=$key;
            DELETE FROM catalog_personal_memberships WHERE ($kind='work' AND game_key=$key) OR ($kind='character' AND identity_key=$key);
            DELETE FROM catalog_covers WHERE kind=$kind AND key=$key;
            DELETE FROM catalog_deleted_entries WHERE kind=$kind AND key=$key;
            """, ("$kind", kind), ("$key", key));
    }

}
