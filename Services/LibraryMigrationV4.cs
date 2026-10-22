using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// 结构 v3 → v4：身份记录独立存续、标签稳定键、同名允许（仅 character/work）、
/// 关联状态与显示模式分离。迁移前用 VACUUM INTO 备份，结构迁移与数据修复同事务，失败回滚。
///
/// 迁移只做「有证据」的修复；证据不足的一律保持原关联并留给 <see cref="Report"/> 里的待核实项。
/// 重复执行由 meta.schema_version 拦截，修复语句本身也带幂等保护。
/// </summary>
internal static class LibraryMigrationV4
{
    public const int TargetVersion = 4;
    private const string ReportKey = "migration_v4";

    // 绝区零露西：fandom 全名条目与 biligame 短名条目是同一个人，复用已有有效身份键。
    private const string ZzzLucyEntity = "zenless-zone-zero-wiki:zenless-zone-zero:page:2869";
    private const string ZzzLucyIdentity = "cn-zenless_zone_zero:wiki.biligame.com:page:769";
    private const string ZzzLucySource = "https://wiki.biligame.com/zzz/%E9%9C%B2%E8%A5%BF";

    // 鸣潮 3.4 与《赛博朋克：边缘行者》官方联动，露西是同一人物的跨作品登场：
    // 复用鸣潮已有的身份键，再补赛博朋克的作品与角色来源条目。
    private const string LucyIdentity = "wutheringwaves-wiki:wutheringwaves:page:40855";
    private const string CyberpunkGame = "cyberpunk_edgerunners";
    private const string CyberpunkWorkEntity = "cyberpunk-edgerunners:official:work:edgerunners";
    private const string CyberpunkLucyEntity = "cyberpunk-edgerunners:official:character:lucy";
    private const string CyberpunkSource = "https://www.cyberpunk.net/zh-cn/edgerunners";
    private const string WwLucySource = "https://wiki.biligame.com/wutheringwaves/%E5%85%B1%E9%B8%A3%E8%80%85/%E9%9C%B2%E8%A5%BF";

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
            ApplyLucyRepairs(conn, tx, notes);

            var afterCounts = Counts(conn, tx);
            if (Convert.ToInt64(afterCounts["image_tags"]) != Convert.ToInt64(before["image_tags"]) ||
                Convert.ToInt64(afterCounts["images"]) != Convert.ToInt64(before["images"]) ||
                Convert.ToInt64(afterCounts["tags"]) != Convert.ToInt64(before["tags"]))
                throw new InvalidDataException("迁移过程中图片/标签数量发生变化，已回滚。");
            report["counts_before"] = before;
            report["counts_after"] = afterCounts;
            report["repairs"] = new[]
            {
                new
                {
                    item = "zzz-lucy-identity",
                    status = "confirmed",
                    summary = "绝区零露西的 fandom 全名条目并入 biligame 露西身份，标签 luciana_de_montefio 关联到该身份",
                    identity = ZzzLucyIdentity,
                    sources = new[] { ZzzLucySource, "https://zenless-zone-zero.fandom.com/wiki/Luciana_de_Montefio" },
                },
                new
                {
                    item = "lucy-cross-work",
                    status = "confirmed",
                    summary = "鸣潮 3.4 联动角色露西与《赛博朋克：边缘行者》露西为同一人物；补作品/角色来源条目，身份复用鸣潮已有键",
                    identity = LucyIdentity,
                    sources = new[]
                    {
                        CyberpunkSource,
                        WwLucySource,
                        "https://wutheringwaves.kurogames.com/zh-tw/main/news/detail/4788",
                    },
                },
            };
            report["pending_review"] = new[]
            {
                new
                {
                    item = "lucy-mixed-tag-reviewed",
                    status = "no-split",
                    summary = "标签 lucy 同时挂赛博朋克与鸣潮图片，经核实是同一人物跨作品（鸣潮 3.4 官方联动），不按不同人物拆分；作品差异由图片上的 work 标签区分",
                },
            };
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

    private static void ApplyLucyRepairs(SqliteConnection conn, SqliteTransaction tx, List<string> notes)
    {
        var now = Now();

        // --- 绝区零：全名条目并入已有露西身份 ---
        Exec(conn, tx,
            "UPDATE catalog_entities SET identity_key=$i WHERE entity_key=$e",
            ("$i", ZzzLucyIdentity), ("$e", ZzzLucyEntity));
        Exec(conn, tx, $"""
            INSERT OR IGNORE INTO catalog_memberships(identity_key,game_key,source_url)
            VALUES($i,'zenless_zone_zero',$s)
            """, ("$i", ZzzLucyIdentity), ("$s", ZzzLucySource));
        Exec(conn, tx,
            "UPDATE tag_identities SET identity_key=$i,link_status='linked',locked=1,source='migration'," +
            "note='绝区零露西：短名与全名统一' WHERE tag_id IN " +
            "(SELECT id FROM tags WHERE name='luciana_de_montefio' AND category='character')",
            ("$i", ZzzLucyIdentity));
        // 旧条目键留重定向，历史草稿/侧车即便只带旧键也能落到有效身份。
        Exec(conn, tx, $"""
            INSERT OR IGNORE INTO identity_redirects(old_key,identity_key,merged_at,note)
            VALUES($old,$new,{F(now)},'绝区零露西 fandom 条目并入 biligame 身份')
            """, ("$old", ZzzLucyEntity), ("$new", ZzzLucyIdentity));
        Exec(conn, tx,
            "DELETE FROM identities WHERE identity_key=$k AND identity_key NOT IN " +
            "(SELECT identity_key FROM tag_identities WHERE identity_key<>'') " +
            "AND identity_key NOT IN (SELECT identity_key FROM catalog_entities WHERE identity_key<>'')",
            ("$k", ZzzLucyEntity));

        // --- 赛博朋克：补作品/角色来源条目，身份复用鸣潮已核实键 ---
        Exec(conn, tx, """
            INSERT OR IGNORE INTO catalog_games(game_key,zh,en,aliases,source_url)
            VALUES('cyberpunk_edgerunners','赛博朋克：边缘行者','Cyberpunk: Edgerunners',
                   '["赛博浪客","Cyberpunk 2077"]','https://www.cyberpunk.net/zh-cn/edgerunners');
            """);
        Exec(conn, tx, $"""
            INSERT OR IGNORE INTO catalog_entities(entity_key,identity_key,game_key,kind,zh,en,aliases,status,source_url,source_kind)
            VALUES($w,$w,'cyberpunk_edgerunners','work','赛博朋克：边缘行者','Cyberpunk: Edgerunners',
                   '["赛博浪客"]','released',{Q(CyberpunkSource)},'official');
            """, ("$w", CyberpunkWorkEntity));
        Exec(conn, tx, $"""
            INSERT OR IGNORE INTO catalog_entities(entity_key,identity_key,game_key,kind,zh,en,aliases,status,source_url,source_kind)
            VALUES($c,$i,'cyberpunk_edgerunners','character','露西','Lucy',
                   '["Lucyna Kushinada","露西·栉名田"]','released',{Q(CyberpunkSource)},'official');
            """, ("$c", CyberpunkLucyEntity), ("$i", LucyIdentity));

        foreach (var term in new[] { "lucy", "露西", "lucy kushinada", "lucyna kushinada" })
            Exec(conn, tx,
                "INSERT OR IGNORE INTO catalog_names(term,entity_key) VALUES($t,$e)",
                ("$t", TagCatalogService.Norm(term)), ("$e", CyberpunkLucyEntity));
        foreach (var term in new[] { "cyberpunk", "赛博朋克", "cyberpunk: edgerunners", "赛博朋克：边缘行者" })
            Exec(conn, tx,
                "INSERT OR IGNORE INTO catalog_names(term,entity_key) VALUES($t,$e)",
                ("$t", TagCatalogService.Norm(term)), ("$e", CyberpunkWorkEntity));

        Exec(conn, tx, $"""
            INSERT OR IGNORE INTO catalog_memberships(identity_key,game_key,source_url)
            VALUES($i,'cyberpunk_edgerunners',$s)
            """, ("$i", LucyIdentity), ("$s", CyberpunkSource));
        Exec(conn, tx, $"""
            INSERT OR IGNORE INTO catalog_memberships(identity_key,game_key,source_url)
            VALUES($w,'cyberpunk_edgerunners',$s)
            """, ("$w", CyberpunkWorkEntity), ("$s", CyberpunkSource));

        Exec(conn, tx, $"""
            INSERT OR IGNORE INTO identities(identity_key,kind,zh,en,primary_entity_key,created_at,updated_at)
            VALUES($w,'work','赛博朋克：边缘行者','Cyberpunk: Edgerunners',$w,{F(now)},{F(now)})
            """, ("$w", CyberpunkWorkEntity));

        // 已有标签接上身份；标签 lucy 暂不拆分，等按 tag_key 落库的入口上线后按图拆分。
        Exec(conn, tx,
            "UPDATE tag_identities SET identity_key=$i,link_status='linked',source='migration'," +
            "note='鸣潮 3.4 联动＝同一人物' WHERE tag_id IN " +
            "(SELECT id FROM tags WHERE name='lucy' AND category='character')",
            ("$i", LucyIdentity));
        // 露西标签补齐来源资料：中文名 + 已核实的赛博朋克角色条目（与鸣潮同身份）。
        Exec(conn, tx,
            "INSERT INTO tag_profiles(tag_id,zh,en,entity_key) " +
            "SELECT id,'露西','Lucy',$c FROM tags WHERE name='lucy' AND category='character' " +
            "ON CONFLICT(tag_id) DO UPDATE SET zh=excluded.zh,en=excluded.en,entity_key=excluded.entity_key",
            ("$c", CyberpunkLucyEntity));
        Exec(conn, tx,
            "UPDATE tag_identities SET identity_key=$i,link_status='linked',source='migration'," +
            "note='赛博朋克：边缘行者的作品标签' WHERE tag_id IN " +
            "(SELECT id FROM tags WHERE name='cyberpunk' AND category='work')",
            ("$i", CyberpunkWorkEntity));

        // 来源条目：让标签在候选里能显示中文名与作品。
        Exec(conn, tx,
            "INSERT OR IGNORE INTO tag_profiles(tag_id,zh,en,entity_key) " +
            "SELECT id,'赛博朋克：边缘行者','Cyberpunk: Edgerunners',$w FROM tags " +
            "WHERE name='cyberpunk' AND category='work'", ("$w", CyberpunkWorkEntity));

        notes.Add("已补：绝区零露西身份统一；赛博朋克作品/角色条目；鸣潮与赛博朋克露西合并为同一身份");
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

    private static string Q(string value) => "'" + value.Replace("'", "''") + "'";
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static double Now() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
}
