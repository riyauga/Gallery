using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// 结构 v3 → v4：身份记录独立存续、标签稳定键、同名允许（仅 character/work）、
/// 关联状态与显示模式分离。迁移前用 VACUUM INTO 备份，结构迁移与数据修复同事务，失败回滚。
///
/// 迁移只调整通用数据结构并保留原关联；具体作品／角色资料由外部资料包维护。
/// 重复执行由 meta.schema_version 拦截，修复语句本身也带幂等保护。
/// </summary>
internal static class LibraryMigrationV4
{
    public const int TargetVersion = 4;
    private const string ReportKey = "migration_v4";

    public static string Upgrade(SqliteConnection conn, string dbPath)
    {
        var backup = Backup(conn, dbPath);
        var notes = new List<string>();
        var report = new Dictionary<string, object?>
        {
            ["schema_before"] = 3,
            ["schema_after"] = TargetVersion,
            ["backup"] = backup,
            ["checked_at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["notes"] = notes,
        };

        var before = Counts(conn);
        // Microsoft.Data.Sqlite 默认开启 foreign_keys；重建 tags 时若开启会按 ON DELETE CASCADE 清空 image_tags。
        // 必须在事务外关闭，事务内是空操作。
        Exec(conn, null, "PRAGMA foreign_keys=OFF");
        using var tx = conn.BeginTransaction();
        try
        {
            CreateNewTables(conn, tx);
            RebuildTags(conn, tx, notes);
            SeedIdentities(conn, tx);
            MigrateLinkState(conn, tx, notes);

            var afterCounts = Counts(conn, tx);
            if (Convert.ToInt64(afterCounts["image_tags"]) != Convert.ToInt64(before["image_tags"]) ||
                Convert.ToInt64(afterCounts["images"]) != Convert.ToInt64(before["images"]) ||
                Convert.ToInt64(afterCounts["tags"]) != Convert.ToInt64(before["tags"]))
                throw new InvalidDataException("迁移过程中图片/标签数量发生变化，已回滚。");
            report["counts_before"] = before;
            report["counts_after"] = afterCounts;
            report["repairs"] = Array.Empty<object>();
            report["pending_review"] = Array.Empty<object>();
            report["verification"] = Verify(conn, tx);

            WriteMeta(conn, tx, "schema_version", TargetVersion.ToString(CultureInfo.InvariantCulture));
            WriteMeta(conn, tx, "identity_model", "v1");
            WriteMeta(conn, tx, ReportKey, JsonSerializer.Serialize(report));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            // PRAGMA 在事务内是空操作：先结束事务再恢复外键检查。
            tx.Dispose();
            Exec(conn, null, "PRAGMA foreign_keys=ON");
        }

        var after = Counts(conn);
        if (Convert.ToInt64(after["image_tags"]) != Convert.ToInt64(before["image_tags"]) ||
            Convert.ToInt64(after["images"]) != Convert.ToInt64(before["images"]) ||
            Convert.ToInt64(after["tags"]) != Convert.ToInt64(before["tags"]))
            throw new InvalidDataException("迁移后图片/标签数量与迁移前不一致，已备份但需人工核对；应用将拒绝启动。");

        return backup;
    }

    private static string Backup(SqliteConnection conn, string dbPath)
    {
        var dir = Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "backups");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir,
            $"library-v3-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        if (File.Exists(target)) target = Path.Combine(dir,
            $"library-v3-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p";
        cmd.Parameters.AddWithValue("$p", target);
        cmd.ExecuteNonQuery();
        return target;
    }

    private static void CreateNewTables(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS identities(
                identity_key       TEXT PRIMARY KEY,
                kind               TEXT NOT NULL DEFAULT 'character',
                zh                 TEXT NOT NULL DEFAULT '',
                en                 TEXT NOT NULL DEFAULT '',
                primary_entity_key TEXT NOT NULL DEFAULT '',
                created_at         REAL NOT NULL DEFAULT 0,
                updated_at         REAL NOT NULL DEFAULT 0
            );
            """);
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS identity_redirects(
                old_key    TEXT PRIMARY KEY,
                identity_key TEXT NOT NULL,
                merged_at  REAL NOT NULL DEFAULT 0,
                note       TEXT NOT NULL DEFAULT ''
            );
            """);
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS tag_identities(
                tag_id       INTEGER PRIMARY KEY REFERENCES tags(id) ON DELETE CASCADE,
                identity_key TEXT NOT NULL DEFAULT '',
                link_status  TEXT NOT NULL DEFAULT 'pending',
                source       TEXT NOT NULL DEFAULT '',
                locked       INTEGER NOT NULL DEFAULT 0,
                note         TEXT NOT NULL DEFAULT '',
                updated_at   REAL NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_tag_identities_identity ON tag_identities(identity_key);
            """);
        Exec(conn, tx, """
            CREATE INDEX IF NOT EXISTS idx_identity_redirects_target ON identity_redirects(identity_key);
            """);
    }

    /// <summary>
    /// 重建 tags：去掉 UNIQUE(category,name)，允许 character/work 同名；
    /// 其余分类的唯一性由应用层保持（见 TagIdentityService）。id 与图片关联不变。
    /// </summary>
    private static void RebuildTags(SqliteConnection conn, SqliteTransaction tx, List<string> notes)
    {
        var hasKey = ColumnExists(conn, tx, "tags", "tag_key");
        if (hasKey)
        {
            notes.Add("tags.tag_key 已存在，跳过表重建");
            return;
        }

        var before = Scalar(conn, tx, "SELECT COUNT(*) FROM tags");
        Exec(conn, tx, """
            CREATE TABLE tags_v4(
                id       INTEGER PRIMARY KEY,
                name     TEXT NOT NULL COLLATE NOCASE,
                color    TEXT,
                category TEXT NOT NULL DEFAULT 'custom',
                tag_key  TEXT
            );
            """);
        Exec(conn, tx, """
            INSERT INTO tags_v4(id,name,color,category,tag_key)
            SELECT id,name,color,COALESCE(NULLIF(category,''),'custom'),
                   'tk_' || lower(hex(randomblob(8)))
            FROM tags;
            """);
        Exec(conn, tx, "DROP TABLE tags;");
        Exec(conn, tx, "ALTER TABLE tags_v4 RENAME TO tags;");
        Exec(conn, tx, """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_tags_tag_key ON tags(tag_key)
                WHERE tag_key IS NOT NULL AND tag_key<>'';
            CREATE INDEX IF NOT EXISTS idx_tags_name ON tags(category,name COLLATE NOCASE);
            """);
        var after = Scalar(conn, tx, "SELECT COUNT(*) FROM tags");
        if (before != after)
            throw new InvalidDataException($"标签表重建数量不一致：{before} → {after}");
        notes.Add($"重建 tags：{after} 个标签，取消分类内名称唯一约束，补齐 tag_key");
    }

    private static void SeedIdentities(SqliteConnection conn, SqliteTransaction tx)
    {
        var now = Now();
        Exec(conn, tx, $"""
            INSERT OR IGNORE INTO identities(identity_key,kind,zh,en,primary_entity_key,created_at,updated_at)
            SELECT key, kind, zh, en, entity_key, {now.ToString(CultureInfo.InvariantCulture)}, {now.ToString(CultureInfo.InvariantCulture)}
            FROM (
                SELECT COALESCE(NULLIF(e.identity_key,''),e.entity_key) AS key,
                       MIN(COALESCE(NULLIF(e.kind,''),'character')) AS kind,
                       MAX(COALESCE(e.zh,'')) AS zh,
                       MAX(COALESCE(e.en,'')) AS en,
                       MIN(e.entity_key) AS entity_key
                FROM catalog_entities e
                WHERE COALESCE(NULLIF(e.identity_key,''),e.entity_key)<>''
                GROUP BY COALESCE(NULLIF(e.identity_key,''),e.entity_key)
            );
            """);

        // 每个标签一条关联记录：有来源实体就按实体身份落“已关联”，否则“待关联”。
        Exec(conn, tx, $"""
            INSERT OR IGNORE INTO tag_identities(tag_id,identity_key,link_status,source,locked,note,updated_at)
            SELECT t.id,
                   COALESCE(NULLIF(COALESCE(e.identity_key,''),''),''),
                   CASE WHEN COALESCE(NULLIF(COALESCE(e.identity_key,''),''),'')<>'' THEN 'linked' ELSE 'pending' END,
                   'migration',0,'',{now.ToString(CultureInfo.InvariantCulture)}
            FROM tags t
            LEFT JOIN tag_profiles p ON p.tag_id=t.id
            LEFT JOIN catalog_entities e ON e.entity_key=p.entity_key;
            """);
    }

    /// <summary>
    /// manual/resolved/external/original 按实际关联与记录说明迁移；
    /// 判断不了的保留保护（locked=1）并记入待核实，不解释成用户意图。
    /// </summary>
    private static void MigrateLinkState(SqliteConnection conn, SqliteTransaction tx, List<string> notes)
    {
        var states = TagDisplayState.Load(conn);
        if (states.Count == 0)
        {
            notes.Add("没有历史人工状态，链接状态全部按来源实体推导");
            return;
        }

        var migrated = 0;
        var guarded = 0;
        foreach (var (tagId, state) in states)
        {
            var identity = Scalar(conn, tx,
                "SELECT COALESCE(identity_key,'') FROM tag_identities WHERE tag_id=$t", ("$t", tagId));
            var status = state.Status;
            string linkStatus;
            var locked = 0;
            var note = "";

            switch (status)
            {
                case "original":
                    linkStatus = "independent";
                    locked = 1;
                    note = "legacy original";
                    break;
                case "manual":
                case "resolved":
                case "external":
                    if (identity.Length > 0)
                    {
                        linkStatus = "linked";
                        locked = 1;
                    }
                    else
                    {
                        linkStatus = "pending";
                        locked = 1;
                        guarded++;
                    }
                    note = "legacy " + status;
                    break;
                default:
                    continue;
            }

            Exec(conn, tx,
                "UPDATE tag_identities SET link_status=$s,locked=$l,note=$n,source='migration' WHERE tag_id=$t",
                ("$s", linkStatus), ("$l", locked), ("$n", note), ("$t", tagId));
            migrated++;
        }
        notes.Add($"人工状态迁移：{migrated} 条，其中无身份依据保留保护并进入核查 {guarded} 条");
    }

    private static Dictionary<string, object?> Verify(SqliteConnection conn, SqliteTransaction tx)
    {
        var dupKeys = Scalar(conn, tx,
            "SELECT COUNT(*) FROM (SELECT tag_key FROM tags GROUP BY tag_key HAVING COUNT(*)>1)");
        var orphanLinks = Scalar(conn, tx,
            "SELECT COUNT(*) FROM tag_identities ti LEFT JOIN tags t ON t.id=ti.tag_id WHERE t.id IS NULL");
        var danglingIdentity = Scalar(conn, tx,
            "SELECT COUNT(*) FROM tag_identities ti WHERE ti.identity_key<>'' AND ti.identity_key NOT IN (SELECT identity_key FROM identities)");
        var fk = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "PRAGMA foreign_key_check";
            using var r = cmd.ExecuteReader();
            while (r.Read()) fk.Add($"{r.GetString(0)}:{r.GetInt64(1)}");
        }
        return new Dictionary<string, object?>
        {
            ["duplicate_tag_keys"] = dupKeys,
            ["orphan_tag_links"] = orphanLinks,
            ["dangling_identity_links"] = danglingIdentity,
            ["foreign_key_violations"] = fk.Count,
        };
    }

    private static Dictionary<string, object?> Counts(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT (SELECT COUNT(*) FROM tags),(SELECT COUNT(*) FROM image_tags)," +
            "(SELECT COUNT(*) FROM images),(SELECT COUNT(*) FROM catalog_entities)," +
            "(SELECT COUNT(*) FROM catalog_memberships)";
        using var r = cmd.ExecuteReader();
        r.Read();
        return new Dictionary<string, object?>
        {
            ["tags"] = r.GetInt64(0),
            ["image_tags"] = r.GetInt64(1),
            ["images"] = r.GetInt64(2),
            ["catalog_entities"] = r.GetInt64(3),
            ["catalog_memberships"] = r.GetInt64(4),
        };
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

    private static string Scalar(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? "" : Convert.ToString(result, CultureInfo.InvariantCulture) ?? "";
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

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static double Now() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
}
