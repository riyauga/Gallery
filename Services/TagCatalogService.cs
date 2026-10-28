// 角色词库导入：逐条照搬 Gallery_Old/app/tag_catalog.py 的 CharacterCatalog。
// 约定（与 galleryold 一致）：词库只写 catalog_* 与 tag_profiles 之外的东西，
// 绝不创建图片标签、绝不改写用户标签/别名/图片关联；整批导入是原子的，
// 失败回滚；内置词库更新不触碰人工资料（tag_profiles 持有 entity_key 的旧记录保留）。
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

public sealed class TagCatalogService
{
    private static readonly string[] ValidStatus = ["released", "announced", "unknown"];
    private static readonly Regex WorkSuffix = new(@"_\([^()]*\)$", RegexOptions.Compiled);
    private static readonly Regex NamePrefix = new(
        @"^(?:(?:角色|人物|干员|作品|游戏|敌人|宠物|character(?:s)?|work(?:s)?|game(?:s)?|npc|enem(?:y|ies)|pet(?:s)?|キャラクター|登場人物)\s*[/：:]\s*)+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly StoreService _store;

    public TagCatalogService(StoreService store) => _store = store;

    public sealed record ImportReport(
        string SourceId, int Records, int MissingNames, string SourceUrl,
        List<string> Gaps, string Digest);

    /// <summary>旧版外部词库目录；仍参与读取，保证早先解压到这里的包不失效。</summary>
    public static string ExternalDir => PackFolders.LegacyCatalog;

    /// <summary>archive 在前、旧版外部目录在后；按来源 ID 去重，最近导入的包优先。</summary>
    public static List<string> BundleDirs()
    {
        var dirs = new List<string>();
        var candidates = new List<string>();
        candidates.AddRange(PackFolders.ArchivePacks().OrderByDescending(Directory.GetCreationTimeUtc));
        candidates.Add(ExternalDir);
        foreach (var candidate in candidates)
        {
            string resolved;
            try { resolved = Path.GetFullPath(candidate); }
            catch { continue; }
            if (dirs.Contains(resolved) || !Directory.Exists(candidate)) continue;
            dirs.Add(resolved);
        }
        return dirs;
    }

    public static string Norm(string text)
    {
        var normalized = (text ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static string BaseName(string text)
    {
        var stripped = WorkSuffix.Replace(text ?? "", "");
        return stripped.Replace('_', ' ').Trim();
    }

    /// <summary>所有来源及人工词库编辑共用的入库名称格式；不改来源实体键和用户标签原名。</summary>
    public static string CanonicalName(string text)
    {
        var value = (text ?? "").Normalize(NormalizationForm.FormKC);
        value = string.Concat(value.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format));
        value = Regex.Replace(value.Replace('_', ' '), @"\s+", " ").Trim();
        value = NamePrefix.Replace(value, "").Trim();
        // 已确认的抓取字段错误：这是表格说明，不能作为任何来源的名字或别名。
        return value == "的攻击范围" ? "" : value;
    }

    private static List<string> CanonicalAliases(IEnumerable<string> aliases, params string[] names)
        => aliases.Select(CanonicalName).Where(name => name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string CanonicalGameKey(string key)
        => Regex.Replace((key ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(), @"[\s-]+", "_");

    private static string CanonicalKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "角色" or "人物" or "干员" or "characters" => "character",
        "作品" or "游戏" or "works" or "games" => "work",
        "敌人" or "enemies" => "enemy",
        "宠物" or "pets" => "pet",
        "character" or "work" or "enemy" or "pet" or "npc" or "artist" => kind.Trim().ToLowerInvariant(),
        _ => throw new InvalidOperationException("词库实体分类无效：" + kind),
    };

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>策略摘要：内容摘要再哈希一次，policy 变了可以强制重导。</summary>
    public static string PolicyDigest(string digest)
        => Sha256Hex(Encoding.UTF8.GetBytes(digest + ":canonical-names-v3"));

    public sealed record Bundle(string Path, string Digest, JsonElement Root);

    /// <summary>读取词库（directory 给定时只读那一个目录，否则合并当前外部包）。</summary>
    public static List<Bundle> ReadBundles(string? directory = null)
    {
        var dirs = directory is null ? BundleDirs() : [directory];
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var bundles = new List<Bundle>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var raw = File.ReadAllBytes(path);
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement.Clone();
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("source", out _) || !root.TryGetProperty("records", out _)) continue;
                    var sid = root.GetProperty("source").GetProperty("id").GetString() ?? "";
                    if (!seenSources.Add(sid)) continue;
                    bundles.Add(new Bundle(path, Sha256Hex(raw), root));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
                { throw new InvalidDataException("资料文件读取失败：" + path, ex); }
            }
        }
        return bundles;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _store.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=8000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static string? StoredDigest(SqliteConnection conn, string sourceId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT digest FROM catalog_imports WHERE source_id=$s";
        cmd.Parameters.AddWithValue("$s", sourceId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// 整批原子导入：任一来源校验失败就整体回滚，返回本次真正写入的来源报告。
    /// </summary>
    private static readonly object PackGate = new();

    public List<ImportReport> ImportAll(string? directory = null, Action<string>? log = null)
    {
        lock (PackGate) return ImportAllCore(directory, log);
    }

    private List<ImportReport> ImportAllCore(string? directory, Action<string>? log)
    {
        var bundles = ReadBundles(directory);

        // 先算哪些来源需要重导（摘要一致就跳过）
        using var conn = Open();
        var pending = new List<(Bundle Bundle, string SourceId, string SourceUrl, string Kind, string Policy)>();
        foreach (var bundle in bundles)
        {
            var root = bundle.Root;
            if (!root.TryGetProperty("source", out var source)) continue;
            var sid = source.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (sid.Length == 0) throw new InvalidOperationException("词库来源 ID 无效：" + bundle.Path);
            if (sid.Contains(':')) throw new InvalidOperationException("词库来源 ID 不能包含冒号：" + sid);
            var url = source.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var kind = source.TryGetProperty("kind", out var k) ? k.GetString() ?? "community" : "community";
            var policy = PolicyDigest(bundle.Digest);
            if (StoredDigest(conn, sid) == policy) continue;
            pending.Add((bundle, sid, url, kind, policy));
        }
        var activeIds = bundles.Select(b => b.Root.GetProperty("source").GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT source_id FROM catalog_imports";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (!activeIds.Contains(reader.GetString(0))) missing.Add(reader.GetString(0));
        }
        // 指定目录的增量导入不能撤下其它目录的来源。
        if (directory is not null) missing.Clear();
        if (pending.Count == 0 && missing.Count == 0)
        {
            log?.Invoke("词库已是最新，无需导入");
            return [];
        }

        Backup(conn);

        var reports = new List<ImportReport>();
        using (var tx = conn.BeginTransaction())
        {
            var write = conn.CreateCommand();
            write.Transaction = tx;
            try
            {
                foreach (var sid in missing)
                {
                    Exec(conn, tx, "DELETE FROM catalog_entities WHERE substr(entity_key,1,$n)=$p AND source_kind<>'manual'",
                        ("$n", sid.Length + 1), ("$p", sid + ":"));
                    Exec(conn, tx, "DELETE FROM catalog_imports WHERE source_id=$s", ("$s", sid));
                }
                foreach (var (bundle, sid, sourceUrl, kind, policy) in pending)
                    reports.Add(ImportOne(conn, tx, bundle, sid, sourceUrl, kind, policy, log));

                RebuildMemberships(conn, tx, directory is null ? bundles : ReadBundles().Concat(bundles));
                if (directory is null)
                {
                    Exec(conn, tx, "DELETE FROM catalog_games WHERE game_key NOT IN (SELECT game_key FROM catalog_entities) " +
                        "AND game_key NOT IN (SELECT game_key FROM catalog_memberships)");
                }

                // 新导入的来源条目也要有独立身份记录，否则标签关联会指向不存在的身份。
                var now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                Exec(conn, tx,
                    "INSERT OR IGNORE INTO identities(identity_key,kind,zh,en,primary_entity_key,created_at,updated_at) " +
                    "SELECT COALESCE(NULLIF(identity_key,''),entity_key)," +
                    "MIN(COALESCE(NULLIF(kind,''),'character')),MAX(COALESCE(zh,'')),MAX(COALESCE(en,''))," +
                    "MIN(entity_key)," + now + "," + now + " FROM catalog_entities " +
                    "WHERE COALESCE(NULLIF(identity_key,''),entity_key)<>'' " +
                    "GROUP BY COALESCE(NULLIF(identity_key,''),entity_key)");
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        return reports;
    }

    private static void RebuildMemberships(SqliteConnection conn, SqliteTransaction tx, IEnumerable<Bundle> bundles)
    {
        // 人工保留的实体同时保留已有作品关系。
        var manual = new List<(string Identity, string Game, string Url)>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT identity_key,game_key,source_url FROM catalog_memberships WHERE identity_key IN " +
                "(SELECT identity_key FROM catalog_entities WHERE source_kind='manual')";
            using var reader = read.ExecuteReader();
            while (reader.Read()) manual.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        Exec(conn, tx, "DELETE FROM catalog_memberships");
        foreach (var bundle in bundles)
        {
            var source = bundle.Root.GetProperty("source");
            var sid = source.GetProperty("id").GetString()!;
            var url = Str(source, "url");
            foreach (var rec in Records(bundle))
            {
                var identity = IdentityKey(conn, tx, sid + ":" + rec.Key);
                if (identity is null) continue;
                foreach (var game in new[] { rec.Game }.Concat(rec.Works).Distinct())
                    Exec(conn, tx, "INSERT OR IGNORE INTO catalog_memberships(identity_key,game_key,source_url) VALUES($i,$g,$s)",
                        ("$i", identity), ("$g", game), ("$s", rec.SourceUrl ?? url));
            }
        }
        foreach (var row in manual)
            Exec(conn, tx, "INSERT OR IGNORE INTO catalog_memberships(identity_key,game_key,source_url) VALUES($i,$g,$s)",
                ("$i", row.Identity), ("$g", row.Game), ("$s", row.Url));
    }

    /// <summary>复用正式导入的字段和记录校验，落盘前就拒绝坏包。</summary>
    public static void ValidatePackFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var sid = root.GetProperty("source").GetProperty("id").GetString() ?? "";
        if (sid.Length == 0 || sid.Contains(':') || root.GetProperty("records").ValueKind != JsonValueKind.Array
            || root.GetProperty("records").EnumerateArray().Any(r => r.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("资料库来源或记录格式无效");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rec in Records(new Bundle(path, "", root)))
            if (rec.Key.Length == 0 || rec.Game.Length == 0 || (rec.Zh.Length == 0 && rec.En.Length == 0)
                || !ValidStatus.Contains(rec.Status) || !seen.Add(rec.Key))
                throw new InvalidDataException("资料库记录字段无效或 key 重复：" + sid + ":" + rec.Key);
    }

    /// <summary>撤下来源资料；独立身份、图片标签、人工资料继续保留。</summary>
    public void RemovePack(string path)
    {
        lock (PackGate)
        {
            var removed = ReadBundles(path);
            var active = ReadBundles().ToDictionary(b => b.Root.GetProperty("source").GetProperty("id").GetString()!, StringComparer.Ordinal);
            var stagingRoot = Path.Combine(PackFolders.Root, ".staging");
            Directory.CreateDirectory(stagingRoot);
            var detached = Path.Combine(stagingRoot, "remove-" + Guid.NewGuid().ToString("N"));
            Directory.Move(path, detached);
            try
            {
                var remaining = ReadBundles();
                var remainingIds = remaining.Select(b => b.Root.GetProperty("source").GetProperty("id").GetString()!)
                    .ToHashSet(StringComparer.Ordinal);
                using var conn = Open();
                Backup(conn);
                using var tx = conn.BeginTransaction();
                foreach (var bundle in removed)
                {
                    var sid = bundle.Root.GetProperty("source").GetProperty("id").GetString()!;
                    // 同来源有其它包时，让它重新接管，不误删共享资料。
                    Exec(conn, tx, "DELETE FROM catalog_imports WHERE source_id=$s", ("$s", sid));
                    var originals = Records(active.GetValueOrDefault(sid) ?? bundle).ToDictionary(r => sid + ":" + r.Key, StringComparer.Ordinal);
                    var deletes = new List<string>();
                    var editedKeys = new List<string>();
                    var games = new HashSet<string>();
                    using (var read = conn.CreateCommand())
                    {
                        read.Transaction = tx;
                        read.CommandText = "SELECT entity_key,zh,en,aliases,game_key,source_kind FROM catalog_entities " +
                            "WHERE substr(entity_key,1,$n)=$p";
                        read.Parameters.AddWithValue("$n", sid.Length + 1);
                        read.Parameters.AddWithValue("$p", sid + ":");
                        using var reader = read.ExecuteReader();
                        while (reader.Read())
                        {
                            var key = reader.GetString(0);
                            // 旧版人工编辑没有独立标记，和原包不同的名称／别名也保留。
                            var edited = reader.GetString(5) == "manual"
                                || originals.TryGetValue(key, out var original) &&
                                   (reader.GetString(1) != original.Zh || reader.GetString(2) != original.En
                                    || reader.GetString(3) != JsonSerializer.Serialize(original.Aliases));
                            if (edited) { editedKeys.Add(key); continue; }
                            deletes.Add(key);
                            games.Add(reader.GetString(4));
                        }
                    }
                    foreach (var key in editedKeys)
                        Exec(conn, tx, "UPDATE catalog_entities SET source_kind='manual' WHERE entity_key=$k", ("$k", key));
                    if (remainingIds.Contains(sid)) continue;
                    foreach (var key in deletes)
                    {
                        Exec(conn, tx, "DELETE FROM catalog_names WHERE entity_key=$k", ("$k", key));
                        Exec(conn, tx, "DELETE FROM catalog_source_tags WHERE entity_key=$k", ("$k", key));
                        Exec(conn, tx, "DELETE FROM catalog_entities WHERE entity_key=$k", ("$k", key));
                    }
                    foreach (var game in games)
                        Exec(conn, tx, "DELETE FROM catalog_games WHERE game_key=$g " +
                            "AND NOT EXISTS(SELECT 1 FROM catalog_entities WHERE game_key=$g) " +
                            "AND NOT EXISTS(SELECT 1 FROM catalog_memberships m JOIN catalog_entities e ON e.identity_key=m.identity_key WHERE m.game_key=$g)", ("$g", game));
                }
                RebuildMemberships(conn, tx, remaining);
                tx.Commit();
            }
            catch
            {
                Directory.Move(detached, path);
                throw;
            }
            Directory.Delete(detached, recursive: true);
            ImportAllCore(null, null);
        }
    }

    private static void Backup(SqliteConnection conn)
    {
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM catalog_entities";
                if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return;
            }
            var dir = System.IO.Path.GetDirectoryName(conn.DataSource);
            if (string.IsNullOrEmpty(dir)) return;
            var backupDir = System.IO.Path.Combine(dir, "catalog-backups");
            Directory.CreateDirectory(backupDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var target = System.IO.Path.Combine(backupDir, "before-import-" + stamp + ".db");
            using var dest = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = target,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            dest.Open();
            conn.BackupDatabase(dest);
        }
        catch
        {
            // 备份失败不阻断导入（galleryold 只在有实体时备份）
        }
    }

    private sealed record Rec(
        string Key, string Game, string Kind, string Zh, string En,
        List<string> Aliases, List<string> SourceTags, List<string> Works,
        string Status, string? SourceUrl, string? IdentityKey);

    private static IEnumerable<Rec> Records(Bundle bundle)
    {
        if (!bundle.Root.TryGetProperty("records", out var records) ||
            records.ValueKind != JsonValueKind.Array) yield break;
        foreach (var r in records.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            var zh = CanonicalName(Str(r, "zh"));
            var en = CanonicalName(Str(r, "en"));
            yield return new Rec(
                Str(r, "key").Trim(), CanonicalGameKey(Str(r, "game")), CanonicalKind(Str(r, "kind", "character")),
                zh, en,
                CanonicalAliases(List(r, "aliases"), zh, en),
                List(r, "source_tags").Select(name => CanonicalName(name).ToLowerInvariant().Replace(' ', '_')).Where(name => name.Length > 0).Distinct().ToList(),
                List(r, "works").Select(CanonicalGameKey).Where(key => key.Length > 0).Distinct().ToList(),
                Str(r, "status").Trim().ToLowerInvariant(), r.TryGetProperty("source_url", out var su) ? su.GetString()?.Trim() : null,
                r.TryGetProperty("identity_key", out var ik) ? ik.GetString() : null);
        }
    }

    private static string Str(JsonElement el, string name, string fallback = "")
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    private static List<string> List(JsonElement el, string name)
    {
        var outList = new List<string>();
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return outList;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) outList.Add(s!);
        }
        return outList;
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

    private static string? IdentityKey(SqliteConnection conn, SqliteTransaction tx, string entityKey)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT identity_key FROM catalog_entities WHERE entity_key=$k";
        cmd.Parameters.AddWithValue("$k", entityKey);
        return cmd.ExecuteScalar() as string;
    }

    private static ImportReport ImportOne(SqliteConnection conn, SqliteTransaction tx, Bundle bundle,
        string sid, string sourceUrl, string kind, string policy, Action<string>? log)
    {
        // 先整体校验，任何一条不合法就抛错回滚（和 galleryold 一致）
        var records = Records(bundle).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.Key.Length == 0 || r.Game.Length == 0 || (r.Zh.Length == 0 && r.En.Length == 0) ||
                !ValidStatus.Contains(r.Status))
                throw new InvalidOperationException("词库记录字段无效：" + sid + ":" + r.Key);
            if (!seen.Add(r.Key))
                throw new InvalidOperationException("词库记录 key 重复：" + sid + ":" + r.Key);
        }

        // 旧记录里不在本次词库、且没人手工关联过的，删掉
        var stale = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT entity_key FROM catalog_entities WHERE substr(entity_key,1,$n)=$p";
            cmd.Parameters.AddWithValue("$n", sid.Length + 1);
            cmd.Parameters.AddWithValue("$p", sid + ":");
            using var r = cmd.ExecuteReader();
            while (r.Read()) stale.Add(r.GetString(0));
        }
        foreach (var oldKey in stale)
        {
            if (seen.Contains(oldKey[(sid.Length + 1)..])) continue;
            using var hasProfile = conn.CreateCommand();
            hasProfile.Transaction = tx;
            hasProfile.CommandText = "SELECT 1 FROM tag_profiles WHERE entity_key=$k UNION ALL " +
                "SELECT 1 FROM catalog_entities WHERE entity_key=$k AND source_kind='manual'";
            hasProfile.Parameters.AddWithValue("$k", oldKey);
            if (hasProfile.ExecuteScalar() is not null) continue;   // 人工身份保留
            Exec(conn, tx, "DELETE FROM catalog_entities WHERE entity_key=$k", ("$k", oldKey));
        }

        if (bundle.Root.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in games.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                var gk = CanonicalGameKey(Str(g, "key"));
                if (gk.Length == 0) continue;
                var zh = CanonicalName(Str(g, "zh"));
                var en = CanonicalName(Str(g, "en"));
                Exec(conn, tx,
                    "INSERT INTO catalog_games(game_key,zh,en,aliases,source_url) VALUES($k,$z,$e,$a,$u) " +
                    "ON CONFLICT(game_key) DO UPDATE SET zh=excluded.zh,en=excluded.en," +
                    "aliases=excluded.aliases,source_url=excluded.source_url",
                    ("$k", gk), ("$z", zh), ("$e", en),
                    ("$a", JsonSerializer.Serialize(CanonicalAliases(List(g, "aliases"), zh, en))),
                    ("$u", g.TryGetProperty("source_url", out var gsu) ? gsu.GetString() ?? sourceUrl : sourceUrl));
            }
        }

        var count = 0;
        var missing = 0;
        foreach (var r in records)
        {
            var key = sid + ":" + r.Key;
            using var manual = conn.CreateCommand();
            manual.Transaction = tx;
            manual.CommandText = "SELECT 1 FROM catalog_entities WHERE entity_key=$k AND source_kind='manual'";
            manual.Parameters.AddWithValue("$k", key);
            if (manual.ExecuteScalar() is not null) continue;
            var identity = string.IsNullOrEmpty(r.IdentityKey) ? key : r.IdentityKey!;
            var recordUrl = r.SourceUrl ?? sourceUrl;
            if (recordUrl.Length == 0) continue;    // 没出处的记录不进库
            Exec(conn, tx,
                "INSERT INTO catalog_entities(entity_key,identity_key,game_key,kind,zh,en,aliases,status," +
                "source_url,source_kind) VALUES($k,$i,$g,$kd,$z,$e,$a,$s,$u,$sk) " +
                "ON CONFLICT(entity_key) DO UPDATE SET identity_key=excluded.identity_key," +
                "game_key=excluded.game_key,kind=excluded.kind,zh=excluded.zh,en=excluded.en," +
                "aliases=excluded.aliases,status=excluded.status,source_url=excluded.source_url," +
                "source_kind=excluded.source_kind",
                ("$k", key), ("$i", identity), ("$g", r.Game), ("$kd", r.Kind),
                ("$z", r.Zh), ("$e", r.En), ("$a", JsonSerializer.Serialize(r.Aliases)),
                ("$s", r.Status), ("$u", recordUrl), ("$sk", kind));

            Exec(conn, tx, "DELETE FROM catalog_names WHERE entity_key=$k", ("$k", key));
            Exec(conn, tx, "DELETE FROM catalog_source_tags WHERE entity_key=$k", ("$k", key));

            var terms = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in new[] { r.Zh, r.En }.Concat(r.Aliases))
            {
                var norm = Norm(name);
                if (norm.Length > 0) terms.Add(norm);
            }
            foreach (var term in terms)
                Exec(conn, tx, "INSERT OR IGNORE INTO catalog_names(term,entity_key) VALUES($t,$k)",
                    ("$t", term), ("$k", key));
            foreach (var tag in r.SourceTags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct())
                Exec(conn, tx, "INSERT OR IGNORE INTO catalog_source_tags(tag,entity_key) VALUES($t,$k)",
                    ("$t", tag), ("$k", key));

            count++;
            if (r.Zh.Length == 0 || r.En.Length == 0) missing++;
        }

        var gaps = new List<string>();
        if (bundle.Root.TryGetProperty("gaps", out var gapsEl) && gapsEl.ValueKind == JsonValueKind.Array)
            foreach (var g in gapsEl.EnumerateArray())
                gaps.Add(g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : g.GetRawText());

        Exec(conn, tx,
            "INSERT INTO catalog_imports(source_id,digest,report) VALUES($s,$d,$r) " +
            "ON CONFLICT(source_id) DO UPDATE SET digest=excluded.digest,report=excluded.report",
            ("$s", sid), ("$d", policy),
            ("$r", JsonSerializer.Serialize(new { records = count, missing_names = missing, gaps })));

        log?.Invoke($"导入 {sid}：{count} 条（缺中/英名 {missing}）");
        return new ImportReport(sid, count, missing, sourceUrl, gaps, policy);
    }

    public (int Games, int Entities, int Names, int SourceTags, int Memberships, int Imports) Summary()
    {
        using var conn = Open();
        int Count(string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + table;
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        return (Count("catalog_games"), Count("catalog_entities"), Count("catalog_names"),
                Count("catalog_source_tags"), Count("catalog_memberships"), Count("catalog_imports"));
    }

    /// <summary>作品键到中/英文名的映射；搜索候选拼作品名时用。</summary>
    public Dictionary<string, (string Zh, string En)> GameNames()
    {
        var result = new Dictionary<string, (string Zh, string En)>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT game_key,COALESCE(zh,''),COALESCE(en,'') FROM catalog_games";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = (r.GetString(1), r.GetString(2));
        return result;
    }

    /// <summary>
    /// 身份级来源名与作品：同一身份可能由中/英文 wiki、多个形态的实体组成，
    /// 搜索候选缺某一语言时，可以先用同身份其他实体的来源名。
    /// </summary>
    public sealed record IdentityNames(
        IReadOnlyList<string> Zh, IReadOnlyList<string> En, IReadOnlyList<string> Games);

    /// <summary>
    /// 读取身份到来源名/作品的索引；只用于展示，不写回任何关联。
    /// 传入 identityKeys 时只读这些身份（搜索候选只需要当前命中的身份）。
    /// </summary>
    public Dictionary<string, IdentityNames> LoadIdentityNames(
        IEnumerable<string>? identityKeys = null)
    {
        var wanted = identityKeys?
            .Select(key => (key ?? "").Trim())
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (wanted is { Count: 0 }) return new Dictionary<string, IdentityNames>(StringComparer.Ordinal);

        // 参数太多时退回全量读取，避免触发 SQLite 变量数上限；正常搜索只有几百个身份。
        var useFilter = wanted is { Count: <= 800 };
        var filter = useFilter
            ? string.Join(",", wanted!.Select((_, i) => $"$k{i}"))
            : "";
        void BindKeys(SqliteCommand cmd)
        {
            if (!useFilter) return;
            for (var i = 0; i < wanted!.Count; i++) cmd.Parameters.AddWithValue($"$k{i}", wanted[i]);
        }

        var zh = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var en = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var games = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        static void Add(Dictionary<string, List<string>> map, string key, string value)
        {
            var clean = (value ?? "").Trim();
            if (key.Length == 0 || clean.Length == 0) return;
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            if (!list.Contains(clean, StringComparer.Ordinal)) list.Add(clean);
        }

        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COALESCE(NULLIF(identity_key,''),entity_key)," +
                "COALESCE(zh,''),COALESCE(en,''),COALESCE(game_key,'') FROM catalog_entities" +
                (useFilter ? $" WHERE identity_key IN ({filter})" : "");
            BindKeys(cmd);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                Add(zh, key, r.GetString(1));
                Add(en, key, r.GetString(2));
                Add(games, key, r.GetString(3));
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT identity_key,COALESCE(game_key,'') FROM catalog_memberships" +
                (useFilter ? $" WHERE identity_key IN ({filter})" : "");
            BindKeys(cmd);
            using var r = cmd.ExecuteReader();
            while (r.Read()) Add(games, r.GetString(0), r.GetString(1));
        }

        var keys = zh.Keys.Concat(en.Keys).Concat(games.Keys).Distinct(StringComparer.Ordinal);
        var result = new Dictionary<string, IdentityNames>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            result[key] = new IdentityNames(
                zh.TryGetValue(key, out var z) ? z : [],
                en.TryGetValue(key, out var e) ? e : [],
                games.TryGetValue(key, out var g) ? g : []);
        }
        return result;
    }

    /// <summary>写入人工资料（zh/en/entity_key），词库更新不会覆盖这些。</summary>
    public void SetProfile(int tagId, string zh = "", string en = "", string entityKey = "")
    {
        zh = CanonicalName(zh);
        en = CanonicalName(en);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tag_profiles(tag_id,zh,en,entity_key) VALUES($t,$z,$e,$k) " +
            "ON CONFLICT(tag_id) DO UPDATE SET zh=excluded.zh,en=excluded.en,entity_key=excluded.entity_key";
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.Parameters.AddWithValue("$z", zh);
        cmd.Parameters.AddWithValue("$e", en);
        cmd.Parameters.AddWithValue("$k", entityKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 按名称（标签名/别名，规范化后）在词库里找身份，取中文名/英文名。
    /// 名字相同不等于身份相同——这里只用于展示，不写回任何关联。
    /// </summary>
    public Dictionary<string, (string Zh, string En, string EntityKey)> Lookup(IEnumerable<string> names)
    {
        var result = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var norm = Norm(BaseName(name));
            if (norm.Length == 0) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COALESCE(e.zh,''),COALESCE(e.en,''),e.entity_key FROM catalog_names n " +
                "JOIN catalog_entities e ON e.entity_key=n.entity_key WHERE n.term=$t LIMIT 1";
            cmd.Parameters.AddWithValue("$t", norm);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) continue;
            result[name] = (r.GetString(0), r.GetString(1), r.GetString(2));
        }
        return result;
    }

    /// <summary>按分类和完整原名精确补全显示资料；歧义身份保留原名，不写回关联。</summary>
    public List<StoreService.TagInfo> ResolveDisplayNames(IEnumerable<StoreService.TagInfo> tags)
    {
        using var conn = Open();
        var games = GameNames();
        return tags.Select(tag =>
        {
            if (tag.Category is not ("character" or "work") || tag.DisplayName.Length > 0
                || tag.DisplayMode == "canonical") return tag;
            if (tag.Category == "work" && tag.EntityKey.Length == 0)
            {
                var name = Norm(tag.Name);
                var works = games.Where(game => Norm(game.Key) == name || Norm(game.Value.Zh) == name || Norm(game.Value.En) == name).ToArray();
                if (works.Length == 1)
                    return tag with { ZhName = tag.ZhName.Length > 0 ? tag.ZhName : works[0].Value.Zh,
                        EnName = tag.EnName.Length > 0 ? tag.EnName : works[0].Value.En };
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT e.entity_key,COALESCE(e.zh,''),COALESCE(e.en,''),COALESCE(e.aliases,'[]')," +
                "COALESCE(NULLIF(e.identity_key,''),e.entity_key),COALESCE(e.game_key,'') FROM catalog_entities e " +
                "WHERE e.kind=$category AND (( $key<>'' AND e.entity_key=$key) OR ($key='' AND EXISTS " +
                "(SELECT 1 FROM catalog_names n WHERE n.entity_key=e.entity_key AND n.term=$name)))";
            cmd.Parameters.AddWithValue("$category", tag.Category);
            cmd.Parameters.AddWithValue("$key", tag.EntityKey);
            cmd.Parameters.AddWithValue("$name", Norm(tag.Name));
            using var reader = cmd.ExecuteReader();
            var hits = new List<(string Key, string Zh, string En, string Aliases, string Identity, string Game)>();
            while (reader.Read()) hits.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            if (hits.Count == 0) return tag;
            var sameSource = hits.All(hit => hit.Game.Length > 0 && hit.En.Length > 0)
                && hits.Select(hit => (hit.Game, Name: Norm(hit.En))).Distinct().Count() == 1;
            if (hits.Select(hit => hit.Identity).Distinct(StringComparer.Ordinal).Count() != 1 && !sameSource) return tag;
            var own = hits.OrderByDescending(hit => hit.Zh.Length > 0).ThenByDescending(hit => hit.En.Length > 0)
                .ThenBy(hit => hit.Key, StringComparer.Ordinal).First();
            IReadOnlyList<string> aliases = tag.Aliases;
            if (aliases.Count == 0)
            {
                try { aliases = JsonSerializer.Deserialize<List<string>>(own.Aliases) ?? []; }
                catch (JsonException) { }
            }
            return tag with { EntityKey = own.Key, IdentityKey = own.Identity,
                ZhName = tag.ZhName.Length > 0 ? tag.ZhName : hits.Select(hit => hit.Zh).FirstOrDefault(name => name.Length > 0) ?? "",
                EnName = tag.EnName.Length > 0 ? tag.EnName : hits.Select(hit => hit.En).FirstOrDefault(name => name.Length > 0) ?? "",
                Aliases = aliases };
        }).ToList();
    }

    /// <summary>给标签补中文名/英文名/身份键（人工资料优先，词库只补空缺），对应 CharacterCatalog.enrich。</summary>
    public List<StoreService.TagInfo> Enrich(IEnumerable<StoreService.TagInfo> tags)
    {
        var list = tags.ToList();
        var pending = list.Where(t => t.ZhName.Length == 0).ToList();
        if (pending.Count == 0) return list;

        var terms = new Dictionary<string, (string Zh, string En, string Key)>(StringComparer.Ordinal);
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT n.term,COALESCE(e.zh,''),COALESCE(e.en,''),e.entity_key " +
                "FROM catalog_names n JOIN catalog_entities e ON e.entity_key=n.entity_key";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var term = r.GetString(0);
                if (term.Length == 0) continue;
                terms[term] = (r.GetString(1), r.GetString(2), r.GetString(3));
            }
        }
        if (terms.Count == 0) return list;

        var enriched = new List<StoreService.TagInfo>(list.Count);
        foreach (var tag in list)
        {
            if (tag.ZhName.Length > 0)
            {
                enriched.Add(tag);
                continue;
            }
            (string Zh, string En, string Key)? hit = null;
            foreach (var name in new[] { tag.Name }.Concat(tag.Aliases))
            {
                var norm = Norm(BaseName(name));
                if (norm.Length > 0 && terms.TryGetValue(norm, out var value))
                {
                    hit = (value.Zh, value.En, value.Key);
                    break;
                }
            }
            enriched.Add(hit is null
                ? tag
                : tag with { ZhName = hit.Value.Zh, EnName = hit.Value.En, EntityKey = hit.Value.Key });
        }
        return enriched;
    }

    public sealed record EntityHit(
        string EntityKey, string Zh, string En, string Kind, string GameKey, List<string> Aliases,
        string IdentityKey);

    /// <summary>在词库里搜角色/作品（标签编辑器用它把「刻晴」这类词加进标签）。</summary>
    public List<EntityHit> SearchEntities(string text, int limit = 30)
    {
        var needle = Norm(text ?? "");
        var exact = (text ?? "").Trim();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT e.entity_key,COALESCE(e.zh,''),COALESCE(e.en,''),COALESCE(e.kind,'character')," +
            "COALESCE(e.game_key,''),COALESCE(e.aliases,'[]')," +
            "COALESCE(NULLIF(e.identity_key,''),e.entity_key) FROM catalog_entities e";
        using var r = cmd.ExecuteReader();
        var all = new List<EntityHit>();
        while (r.Read())
        {
            List<string> aliases;
            try { aliases = JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? []; }
            catch { aliases = []; }
            all.Add(new EntityHit(r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4), aliases, r.GetString(6)));
        }

        // 同一身份可能来自多个 wiki（中/英）或同一角色的不同形态。
        // 只要命中任意一个成员，就只在候选里保留一个代表；优先精确命中，其次有中文名的成员。
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in all)
        {
            if (needle.Length == 0) continue;
            var haystack = new[] { hit.Zh, hit.En }.Concat(hit.Aliases)
                .Select(Norm).Where(s => s.Length > 0).ToList();
            if (haystack.Any(s => s.Contains(needle, StringComparison.Ordinal)))
                matched.Add(hit.IdentityKey.Length > 0 ? hit.IdentityKey : hit.EntityKey);
        }
        if (needle.Length == 0)
            foreach (var hit in all)
                matched.Add(hit.IdentityKey.Length > 0 ? hit.IdentityKey : hit.EntityKey);

        var hits = new List<EntityHit>();
        foreach (var group in all.GroupBy(h => h.IdentityKey.Length > 0 ? h.IdentityKey : h.EntityKey))
        {
            if (!matched.Contains(group.Key)) continue;
            hits.Add(group
                .OrderByDescending(h => new[] { h.Zh, h.En }.Concat(h.Aliases)
                    .Any(s => string.Equals(s.Trim(), exact, StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(h => h.Zh.Length > 0)
                .ThenBy(h => h.Zh.Length > 0 ? h.Zh : h.En, StringComparer.OrdinalIgnoreCase)
                .First());
        }

        return hits
            .OrderBy(h => new[] { h.Zh, h.En }.Concat(h.Aliases)
                .Any(s => string.Equals(s.Trim(), exact, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenBy(h => h.Zh.Length == 0 ? 1 : 0)
            .ThenBy(h => h.Zh.Length > 0 ? h.Zh : h.En, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>标签↔身份对齐结果（跳过歧义名，不猜）。</summary>
    public sealed record AlignReport(
        int Matched, int Ambiguous, int Unmatched, int Existing,
        int External = 0, int Manual = 0, int Skipped = 0,
        int Seeded = 0, int IdentityLinks = 0, int MergedEntities = 0, int MergedTags = 0,
        int Conflicting = 0);

    private sealed record PlanLink(string Member, string Identity, string Origin, string Note);
    private sealed record PlanTagState(string Tag, string Category, string Status, string Identity,
        string Origin, string DisplayMode, string DisplayName, string Note);
    private sealed record PlanTagMerge(string LoserCategory, string LoserName,
        string WinnerCategory, string WinnerName, string Note);
    private sealed record TagPlan(List<PlanLink> Links, List<PlanTagState> States,
        List<PlanTagMerge> Merges);

    public const string PublicRelationsFile = "catalog_relations.json";
    public static string PersonalRulesPath => Path.Combine(AppPaths.AppDataDir, "personal_rules.json");

    /// <summary>公共来源只加载实体关系；个人处理规则只从当前用户目录读取。</summary>
    private static TagPlan LoadIdentityPlan()
    {
        var links = new Dictionary<string, PlanLink>(StringComparer.Ordinal);
        foreach (var dir in BundleDirs())
        {
            var path = Path.Combine(dir, PublicRelationsFile);
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(PublicRelationsBytes(path));
            foreach (var link in ParsePlan(doc.RootElement, false).Links)
                links.TryAdd(link.Member, link);
        }
        var personal = LoadPersonalPlan();
        foreach (var link in personal.Links) links[link.Member] = link;
        return new TagPlan(links.Values.ToList(), personal.States, personal.Merges);
    }

    private static TagPlan LoadPersonalPlan()
        => File.Exists(PersonalRulesPath) ? ReadPersonalPlan(PersonalRulesPath) : new([], [], []);

    private static TagPlan ReadPersonalPlan(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        if (Str(root, "format") != "gallery-personal-rules" || root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("请选择 Gallery 个人规则备份，不要选择公共资料包");
        var plan = ParsePlan(root, true);
        if (root.GetProperty("tag_states").ValueKind != JsonValueKind.Array
            || root.GetProperty("tag_merges").ValueKind != JsonValueKind.Array
            || plan.States.Count != root.GetProperty("tag_states").GetArrayLength()
            || plan.Merges.Count != root.GetProperty("tag_merges").GetArrayLength()
            || plan.States.Any(s => s.Status is not ("" or "manual" or "resolved" or "external" or "original")
                || s.Category is not ("character" or "artist" or "work" or "kind" or "custom" or "rating")
                || s.DisplayMode is not ("" or "zh_full" or "zh_short" or "alias" or "original" or "canonical"))
            || plan.States.Select(s => (s.Category, s.Tag)).Distinct().Count() != plan.States.Count)
            throw new InvalidDataException("个人规则备份包含无效或重复的标签设置");
        return plan;
    }

    /// <summary>兼容旧资料包的混合文件，但仅提取实体关系，绝不带入个人状态和清理操作。</summary>
    public static byte[] PublicRelationsBytes(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        if (Path.GetFileName(path) != "tag_identity.json"
            && (Str(root, "format") != "gallery-catalog-relations" || root.GetProperty("version").GetInt32() != 1))
            throw new InvalidDataException("公共实体关系格式无效");
        var links = root.GetProperty("identity_links");
        if (links.ValueKind != JsonValueKind.Array || links.EnumerateArray().Any(l =>
                l.ValueKind != JsonValueKind.Object || Str(l, "member").Length == 0 || Str(l, "identity").Length == 0))
            throw new InvalidDataException("公共实体关系缺少来源实体键");
        return JsonSerializer.SerializeToUtf8Bytes(new { format = "gallery-catalog-relations", version = 1, identity_links = links });
    }

    private static TagPlan ParsePlan(JsonElement root, bool personal)
    {
        var links = new List<PlanLink>();
        if (root.TryGetProperty("identity_links", out var linkEl) &&
            linkEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in linkEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var member = Str(e, "member");
                var identity = Str(e, "identity");
                if (member.Length == 0 || identity.Length == 0) continue;
                links.Add(new PlanLink(member, identity, Str(e, "origin"), Str(e, "note")));
            }
        }

        var states = new List<PlanTagState>();
        if (personal && root.TryGetProperty("tag_states", out var stateEl) &&
            stateEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in stateEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var tag = Str(e, "tag");
                if (tag.Length == 0) continue;
                states.Add(new PlanTagState(
                    tag, Str(e, "category", "character"), Str(e, "status"),
                    Str(e, "identity"), Str(e, "origin"), Str(e, "display_mode"),
                    Str(e, "display_name"), Str(e, "note")));
            }
        }

        var merges = new List<PlanTagMerge>();
        if (personal && root.TryGetProperty("tag_merges", out var mergeEl) &&
            mergeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in mergeEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var loser = List(e, "loser");
                var winner = List(e, "winner");
                if (loser.Count < 2 || winner.Count < 2) continue;
                merges.Add(new PlanTagMerge(loser[0], loser[1], winner[0], winner[1],
                    Str(e, "note")));
            }
        }
        return new TagPlan(links, states, merges);
    }

    private TagPlan CurrentPersonalPlan(SqliteConnection conn)
    {
        var saved = LoadPersonalPlan();
        var states = saved.States.ToDictionary(s => (s.Category, s.Tag));
        var current = TagDisplayState.Load(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT t.id,t.name,t.category,COALESCE(i.identity_key,''),COALESCE(i.link_status,'')," +
            "COALESCE(i.note,''),COALESCE(i.locked,0) FROM tags t LEFT JOIN tag_identities i ON i.tag_id=t.id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            var tag = reader.GetString(1);
            var category = reader.GetString(2);
            if (current.TryGetValue(id, out var state))
                states[(category, tag)] = new(tag, category, state.Status, state.IdentityKey, state.OriginGame,
                    state.DisplayMode, state.DisplayName, state.Note);
            else if (reader.GetInt32(6) != 0 || reader.GetString(4) == "independent")
                states[(category, tag)] = new(tag, category, reader.GetString(4) == "independent" ? "original" : "manual",
                    reader.GetString(3), "", "", "", reader.GetString(5));
        }
        return new(saved.Links, states.Values.ToList(), saved.Merges);
    }

    private static byte[] PersonalPlanBytes(TagPlan plan, string mode, JsonElement directoryRules)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "gallery-personal-rules", version = 1, global_display_mode = mode, directory_rules = directoryRules,
            identity_links = plan.Links.Select(l => new { member = l.Member, identity = l.Identity, origin = l.Origin, note = l.Note }),
            tag_states = plan.States.Select(s => new { tag = s.Tag, category = s.Category, status = s.Status, identity = s.Identity,
                origin = s.Origin, display_mode = s.DisplayMode, display_name = s.DisplayName, note = s.Note }),
            tag_merges = plan.Merges.Select(m => new { loser = new[] { m.LoserCategory, m.LoserName },
                winner = new[] { m.WinnerCategory, m.WinnerName }, note = m.Note }),
        }, new JsonSerializerOptions { WriteIndented = true });

    public void ExportPersonalRules(string destination)
    {
        using var conn = Open();
        File.WriteAllBytes(destination, PersonalPlanBytes(CurrentPersonalPlan(conn), TagDisplayState.GlobalMode(conn), FolderMap.Load(AppPaths.FolderMapPath).ToJson()));
    }

    /// <summary>恢复时由用户现场选择保留当前设置或采用备份；标签按分类和名字匹配，不使用跨库数字 ID。</summary>
    public void ImportPersonalRules(string source, bool overwrite)
    {
        lock (PackGate)
        {
            var incoming = ReadPersonalPlan(source);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(source));
            var incomingMode = Str(doc.RootElement, "global_display_mode", TagDisplayState.DefaultGlobalMode);
            if (incomingMode is not ("zh_full" or "zh_short" or "alias" or "original" or "canonical"))
                throw new InvalidDataException("备份中的显示方式无效");
            using var conn = Open();
            var current = CurrentPersonalPlan(conn);
            var first = overwrite ? incoming : current;
            var second = overwrite ? current : incoming;
            var merged = new TagPlan(first.Links.Concat(second.Links).GroupBy(l => l.Member).Select(g => g.First()).ToList(),
                first.States.Concat(second.States).GroupBy(s => (s.Category, s.Tag)).Select(g => g.First()).ToList(),
                first.Merges.Concat(second.Merges).GroupBy(m => (m.LoserCategory, m.LoserName)).Select(g => g.First()).ToList());
            var existingKeys = current.States.Select(s => (s.Category, s.Tag)).ToHashSet();
            using var modeQuery = conn.CreateCommand();
            modeQuery.CommandText = "SELECT value FROM meta WHERE key=$k";
            modeQuery.Parameters.AddWithValue("$k", TagDisplayState.GlobalModeKey);
            var hasCurrentMode = modeQuery.ExecuteScalar() is string currentMode && currentMode.Length > 0;
            var mode = overwrite || !hasCurrentMode ? incomingMode : TagDisplayState.GlobalMode(conn);
            var oldDirectories = File.Exists(AppPaths.FolderMapPath) ? File.ReadAllBytes(AppPaths.FolderMapPath) : null;
            var directories = FolderMap.Load(AppPaths.FolderMapPath);
            var restoreDirectories = doc.RootElement.TryGetProperty("directory_rules", out var directoryElement);
            if (restoreDirectories)
            {
                var incomingDirectories = FolderMap.Parse(directoryElement);
                directories = oldDirectories is null ? incomingDirectories : FolderMap.Merge(directories, incomingDirectories, overwrite);
            }
            var directoryJson = directories.ToJson();
            var directoryPending = AppPaths.FolderMapPath + ".restore.tmp";
            var oldFile = File.Exists(PersonalRulesPath) ? File.ReadAllBytes(PersonalRulesPath) : null;
            var pending = PersonalRulesPath + ".tmp";
            Directory.CreateDirectory(AppPaths.AppDataDir);
            File.WriteAllBytes(pending, PersonalPlanBytes(merged, mode, directoryJson));
            if (restoreDirectories) File.WriteAllBytes(directoryPending, JsonSerializer.SerializeToUtf8Bytes(directoryJson, new JsonSerializerOptions { WriteIndented = true }));
            Backup(conn);
            var states = TagDisplayState.Load(conn);
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var rule in incoming.States)
                {
                    if (!overwrite && existingKeys.Contains((rule.Category, rule.Tag))) continue;
                    var id = TagId(conn, rule.Category, rule.Tag, tx);
                    if (id == 0) continue; // 规则仍保存在个人文件中，待对应标签出现后补齐。
                    states[id] = new(rule.Status, rule.Identity, rule.Origin, rule.DisplayMode, rule.DisplayName, rule.Note);
                    if (rule.Identity.Length > 0) WriteProfile(conn, tx, id, rule.Identity, null);
                    WriteTagIdentity(conn, id, rule.Identity, rule.Status == "original" ? "independent" :
                        rule.Identity.Length > 0 ? "linked" : "pending", locked: true, source: "manual", note: rule.Note, tx: tx);
                }
                TagDisplayState.Save(conn, states, tx);
                Exec(conn, tx, "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    ("$k", TagDisplayState.GlobalModeKey), ("$v", mode));
                File.Move(pending, PersonalRulesPath, overwrite: true);
                if (restoreDirectories) File.Move(directoryPending, AppPaths.FolderMapPath, overwrite: true);
                tx.Commit();
            }
            catch
            {
                if (oldFile is not null) File.WriteAllBytes(PersonalRulesPath, oldFile);
                else if (File.Exists(PersonalRulesPath)) File.Delete(PersonalRulesPath);
                if (restoreDirectories)
                {
                    if (oldDirectories is not null) File.WriteAllBytes(AppPaths.FolderMapPath, oldDirectories);
                    else if (File.Exists(AppPaths.FolderMapPath)) File.Delete(AppPaths.FolderMapPath);
                }
                throw;
            }
            finally
            {
                if (File.Exists(pending)) File.Delete(pending);
                if (File.Exists(directoryPending)) File.Delete(directoryPending);
            }
            if (restoreDirectories) FolderMap.NotifyChanged();
            AlignTags();
        }
    }

    private static int TagId(SqliteConnection conn, string category, string name, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT id FROM tags WHERE name=$n COLLATE NOCASE " +
            "AND COALESCE(category,'custom')=$c LIMIT 1";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", category);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void WriteProfile(SqliteConnection conn, SqliteTransaction tx, int tagId,
        string entityKey, Action<string>? log)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO tag_profiles(tag_id,zh,en,entity_key) " +
            "SELECT $t,COALESCE(zh,''),COALESCE(en,''),entity_key FROM catalog_entities " +
            "WHERE entity_key=$k " +
            "ON CONFLICT(tag_id) DO UPDATE SET zh=excluded.zh,en=excluded.en,entity_key=excluded.entity_key";
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.Parameters.AddWithValue("$k", entityKey);
        if (cmd.ExecuteNonQuery() == 0)
            log?.Invoke($"实体不存在，未写标签 #{tagId}: {entityKey}");
    }

    private sealed record NameHit(string Key, string Identity, string Zh, string En, string Game);

    private static Dictionary<string, List<NameHit>> LoadNameIndex(SqliteConnection conn)
    {
        var byTerm = new Dictionary<string, List<NameHit>>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT n.term,e.entity_key,COALESCE(NULLIF(e.identity_key,''),e.entity_key)," +
            "COALESCE(e.zh,''),COALESCE(e.en,''),COALESCE(e.game_key,'') " +
            "FROM catalog_names n JOIN catalog_entities e ON e.entity_key=n.entity_key";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var term = r.GetString(0);
            if (term.Length == 0) continue;
            if (!byTerm.TryGetValue(term, out var list))
            {
                list = [];
                byTerm[term] = list;
            }
            list.Add(new NameHit(r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5)));
        }
        return byTerm;
    }

    /// <summary>把同一身份下的 cn/wiki 两条记录折成一个候选；优先用身份锚点自身。</summary>
    private static List<NameHit> CollapseIdentities(IEnumerable<NameHit> hits)
    {
        var map = new Dictionary<string, NameHit>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            if (!map.TryGetValue(hit.Identity, out var old) ||
                (hit.Key == hit.Identity && old.Key != old.Identity))
                map[hit.Identity] = hit;
        }
        return map.Values.ToList();
    }

    /// <summary>
    /// 先按当前用户个人规则写入人工状态/身份映射，再把剩余 character 标签按唯一名称接到身份上。
    /// 已有人工/外部/原创/已解析状态、0 图标签、非 character 标签都不再自动猜。
    /// </summary>
    public AlignReport AlignTags(bool overwrite = false, bool dryRun = false, Action<string>? log = null)
    {
        var plan = LoadIdentityPlan();
        using var conn = Open();
        var states = TagDisplayState.Load(conn);

        // 1) 计划状态：缺的补上；已有状态只补空的显示方式/显示名，不覆盖人工修改。
        var changes = new Dictionary<int, TagDisplayState.State>();
        var newIds = new HashSet<int>();
        foreach (var ps in plan.States)
        {
            var id = TagId(conn, ps.Category, ps.Tag);
            if (id == 0)
            {
                log?.Invoke($"计划标签不存在：{ps.Category}/{ps.Tag}");
                continue;
            }
            if (!states.TryGetValue(id, out var existingState))
            {
                changes[id] = new TagDisplayState.State(
                    ps.Status, ps.Identity, ps.Origin, ps.DisplayMode, ps.DisplayName, ps.Note);
                newIds.Add(id);
                continue;
            }
            var next = existingState;
            if (existingState.DisplayMode.Length == 0 && ps.DisplayMode.Length > 0)
                next = next with { DisplayMode = ps.DisplayMode };
            if (existingState.DisplayName.Length == 0 && ps.DisplayName.Length > 0)
                next = next with { DisplayName = ps.DisplayName };
            if (next != existingState)
                changes[id] = next;
        }
        var seeded = changes.Count;
        if (!dryRun && changes.Count > 0)
        {
            using var tx = conn.BeginTransaction();
            foreach (var (id, st) in changes) states[id] = st;
            TagDisplayState.Save(conn, states, tx);
            foreach (var (id, st) in changes)
                if (newIds.Contains(id) && st.IdentityKey.Length > 0)
                    WriteProfile(conn, tx, id, st.IdentityKey, log);
            tx.Commit();
            log?.Invoke($"标签状态：写入/补齐 {changes.Count} 条");
        }
        else if (dryRun)
        {
            foreach (var (id, st) in changes) states[id] = st;
        }

        // 2) 身份映射（含跨作品起源/派生）和 0 图重复标签合并。
        var (identityLinks, mergedEntities) = ApplyIdentityLinks(conn, plan.Links, dryRun, log);
        var mergedTags = ApplyCustomTagMerges(conn, plan.Merges, dryRun, log);
        if (!dryRun && mergedTags > 0) states = TagDisplayState.Load(conn);

        // 旧库里的状态可能还指向已被合并的成员身份；统一到映射后的身份，
        // 避免搜索/显示按旧值再分出一份。
        var linkMap = plan.Links
            .Where(l => l.Member.Length > 0 && l.Identity.Length > 0 && l.Member != l.Identity)
            .GroupBy(l => l.Member, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Identity, StringComparer.Ordinal);
        var normalizedStates = 0;
        foreach (var id in states.Keys.ToList())
        {
            var st = states[id];
            if (st.IdentityKey.Length == 0 || !linkMap.TryGetValue(st.IdentityKey, out var canonical))
                continue;
            if (canonical == st.IdentityKey) continue;
            states[id] = st with { IdentityKey = canonical };
            normalizedStates++;
        }
        if (normalizedStates > 0 && !dryRun)
        {
            using var tx = conn.BeginTransaction();
            TagDisplayState.Save(conn, states, tx);
            tx.Commit();
            log?.Invoke($"标签状态身份归一：{normalizedStates} 条");
        }
        else if (normalizedStates > 0)
        {
            log?.Invoke($"预演标签状态身份归一：{normalizedStates} 条");
        }

        // 3) 对没有被状态挡住的 character 标签，按名称唯一命中身份。
        var byTerm = LoadNameIndex(conn);
        var tags = new List<(int Id, string Name, string Category, int Images)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT t.id,t.name,COALESCE(t.category,'custom')," +
                "(SELECT COUNT(*) FROM image_tags it WHERE it.tag_id=t.id) " +
                "FROM tags t ORDER BY t.name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                tags.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        }

        var existing = new HashSet<int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT tag_id FROM tag_profiles WHERE entity_key<>''";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetInt32(0));
        }

        // 新模型里的锁 / 独立状态：自动对齐一律不碰。
        var protectedTags = new HashSet<int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT tag_id FROM tag_identities WHERE locked<>0 OR link_status='independent'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) protectedTags.Add(r.GetInt32(0));
        }

        // 每张图实际带的 work 标签：候选身份必须覆盖这些作品，否则算冲突。
        var worksByTag = new Dictionary<int, HashSet<string>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT DISTINCT src.tag_id,w.name FROM image_tags src " +
                "JOIN image_tags wi ON wi.image_id=src.image_id " +
                "JOIN tags w ON w.id=wi.tag_id AND COALESCE(w.category,'custom')='work' " +
                "JOIN tags st ON st.id=src.tag_id AND COALESCE(st.category,'custom')='character'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var tagId = r.GetInt32(0);
                if (!worksByTag.TryGetValue(tagId, out var set))
                    worksByTag[tagId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(r.GetString(1));
            }
        }

        var gamesByIdentity = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT identity_key,game_key FROM catalog_memberships WHERE game_key<>''";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var identity = r.GetString(0);
                if (!gamesByIdentity.TryGetValue(identity, out var set))
                    gamesByIdentity[identity] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(r.GetString(1));
            }
        }

        int matched = 0, ambiguous = 0, unmatched = 0, kept = 0;
        int external = 0, manual = 0, skipped = 0, conflicting = 0;
        SqliteTransaction? writeTx = dryRun ? null : conn.BeginTransaction();
        try
        {
            foreach (var (id, name, category, images) in tags)
            {
                if (category != "character") { skipped++; continue; }
                if (images == 0) { skipped++; continue; }
                if (protectedTags.Contains(id))
                {
                    manual++;
                    continue;
                }
                if (states.TryGetValue(id, out var st) && st.BlocksAutoAlign)
                {
                    if (st.Status is "external" or "original") external++;
                    else manual++;
                    continue;
                }
                if (existing.Contains(id) && !overwrite) { kept++; continue; }

                var term = Norm(BaseName(name));
                if (term.Length == 0 || !byTerm.TryGetValue(term, out var raw) || raw.Count == 0)
                {
                    unmatched++;
                    continue;
                }
                var hits = CollapseIdentities(raw);
                if (hits.Count != 1)
                {
                    ambiguous++;
                    continue;
                }
                var hit = hits[0];
                // 名字唯一还不够：候选身份的作品必须覆盖图片实际的作品标签，否则不自动绑。
                if (!WorksAgree(worksByTag, gamesByIdentity, id, hit))
                {
                    conflicting++;
                    log?.Invoke($"身份冲突跳过：{category}/{name} -> {hit.Key} @ {hit.Game}");
                    continue;
                }
                if (dryRun)
                {
                    log?.Invoke($"[预演] {category}/{name} -> {hit.Key} | {hit.Zh}/{hit.En} @ {hit.Game}");
                    matched++;
                    continue;
                }
                WriteProfile(conn, writeTx!, id, hit.Key, log);
                WriteTagIdentity(conn, id, hit.Identity, "linked", locked: false,
                    source: "auto-align", note: $"作品一致：{hit.Game}", tx: writeTx);
                matched++;
            }
            writeTx?.Commit();
        }
        finally
        {
            writeTx?.Dispose();
        }

        return new AlignReport(matched, ambiguous, unmatched, kept, external, manual, skipped,
            seeded, identityLinks, mergedEntities, mergedTags, conflicting);
    }

    /// <summary>
    /// 候选身份覆盖图片实际作品标签才算一致；图片没有任何作品标签时无法判断，按允许处理。
    /// 同一身份跨作品（如鸣潮联动）时，成员表里任一作品匹配即可。
    /// </summary>
    private static bool WorksAgree(
        IReadOnlyDictionary<int, HashSet<string>> worksByTag,
        IReadOnlyDictionary<string, HashSet<string>> gamesByIdentity,
        int tagId, NameHit hit)
    {
        if (!worksByTag.TryGetValue(tagId, out var works) || works.Count == 0) return true;

        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hit.Game.Length > 0) games.Add(hit.Game);
        if (gamesByIdentity.TryGetValue(hit.Identity, out var members))
            foreach (var game in members) games.Add(game);
        if (games.Count == 0) return false;

        foreach (var work in works)
            if (!games.Any(game => GameMatchesWork(game, work)))
                return false;
        return true;
    }

    /// <summary>游戏键与作品标签名的容错匹配：忽略大小写/标点，允许 genshin ↔ genshin_impact 这类包含关系。</summary>
    private static bool GameMatchesWork(string game, string work)
    {
        static string Key(string text) =>
            new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        var g = Key(game);
        var w = Key(work);
        if (g.Length == 0 || w.Length == 0) return false;
        if (g == w) return true;
        if (g.Length >= 4 && w.Contains(g, StringComparison.Ordinal)) return true;
        if (w.Length >= 4 && g.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>应用人工身份映射；重建 memberships 以保留同身份的全部作品。</summary>
    private static (int Applied, int Merged) ApplyIdentityLinks(
        SqliteConnection conn, IReadOnlyList<PlanLink> links, bool dryRun, Action<string>? log)
    {
        if (links.Count == 0) return (0, 0);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var games = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT entity_key,COALESCE(NULLIF(identity_key,''),entity_key)," +
                "COALESCE(game_key,''),COALESCE(source_url,'') FROM catalog_entities";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                current[key] = r.GetString(1);
                games[key] = r.GetString(2);
                sources[key] = r.GetString(3);
            }
        }

        var next = new Dictionary<string, string>(current, StringComparer.Ordinal);
        var origins = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        int applied = 0;
        foreach (var link in links)
        {
            if (!current.ContainsKey(link.Member) || !current.ContainsKey(link.Identity))
            {
                log?.Invoke($"身份映射跳过（实体不存在）：{link.Member} -> {link.Identity}");
                continue;
            }
            next[link.Member] = link.Identity;
            next[link.Identity] = link.Identity;
            if (link.Origin.Length > 0)
            {
                if (!origins.TryGetValue(link.Identity, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    origins[link.Identity] = set;
                }
                set.Add(link.Origin);
            }
            applied++;
        }

        var changed = new List<(string Key, string Identity)>();
        foreach (var (key, identity) in next)
            if (!current.TryGetValue(key, out var old) || old != identity)
                changed.Add((key, identity));

        if (changed.Count == 0 && origins.Count == 0) return (applied, 0);
        if (dryRun)
        {
            log?.Invoke($"预演身份映射：{applied} 条，实体变更 {changed.Count} 条");
            return (applied, changed.Count);
        }

        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE catalog_entities SET identity_key=$i WHERE entity_key=$k";
            var pi = cmd.Parameters.Add("$i", SqliteType.Text);
            var pk = cmd.Parameters.Add("$k", SqliteType.Text);
            foreach (var (key, identity) in changed)
            {
                pi.Value = identity;
                pk.Value = key;
                cmd.ExecuteNonQuery();
            }
        }

        // 同一身份的不同写法（简称/译名）挂到已关联该身份的标签上，搜索和 sidecar 重导都能命中。
        foreach (var link in links)
        {
            if (link.Member == link.Identity) continue;
            var memberZh = "";
            var memberEn = "";
            var memberAliases = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COALESCE(zh,''),COALESCE(en,''),COALESCE(aliases,'[]') " +
                                  "FROM catalog_entities WHERE entity_key=$k";
                cmd.Parameters.AddWithValue("$k", link.Member);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) continue;
                memberZh = r.GetString(0);
                memberEn = r.GetString(1);
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<string>>(r.GetString(2));
                    if (parsed is not null) memberAliases.AddRange(parsed);
                }
                catch
                {
                    // 别名 JSON 坏了就只用中/英文名。
                }
            }

            var tagIds = new List<int>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "SELECT DISTINCT t.id FROM tags t JOIN tag_profiles p ON p.tag_id=t.id " +
                    "WHERE p.entity_key IN (SELECT entity_key FROM catalog_entities WHERE identity_key=$i)";
                cmd.Parameters.AddWithValue("$i", link.Identity);
                using var r = cmd.ExecuteReader();
                while (r.Read()) tagIds.Add(r.GetInt32(0));
            }
            if (tagIds.Count == 0) continue;

            var names = new[] { memberZh, memberEn }.Concat(memberAliases)
                .Select(n => (n ?? "").Trim()).Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO tag_aliases(tag_id,alias) VALUES($t,$a)";
            var pt = insert.Parameters.Add("$t", SqliteType.Integer);
            var pa = insert.Parameters.Add("$a", SqliteType.Text);
            foreach (var tagId in tagIds)
                foreach (var name in names)
                {
                    pt.Value = tagId;
                    pa.Value = name;
                    insert.ExecuteNonQuery();
                }
        }

        var memberships = new HashSet<(string Identity, string Game, string Source)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT identity_key,game_key,COALESCE(source_url,'') FROM catalog_memberships";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var identity = r.GetString(0);
                if (next.TryGetValue(identity, out var mapped)) identity = mapped;
                memberships.Add((identity, r.GetString(1), r.GetString(2)));
            }
        }
        foreach (var (key, identity) in next)
        {
            var game = games.GetValueOrDefault(key, "");
            if (game.Length > 0)
                memberships.Add((identity, game, sources.GetValueOrDefault(key, "")));
        }
        foreach (var (identity, set) in origins)
            foreach (var game in set)
                memberships.Add((identity, game, ""));

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM catalog_memberships";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR IGNORE INTO catalog_memberships(identity_key,game_key,source_url) " +
                "VALUES($i,$g,$s)";
            var pi = cmd.Parameters.Add("$i", SqliteType.Text);
            var pg = cmd.Parameters.Add("$g", SqliteType.Text);
            var ps = cmd.Parameters.Add("$s", SqliteType.Text);
            foreach (var (identity, game, source) in memberships)
            {
                pi.Value = identity;
                pg.Value = game;
                ps.Value = source;
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
        log?.Invoke($"身份映射：{applied} 条，实体变更 {changed.Count} 条");
        return (applied, changed.Count);
    }

    /// <summary>只合并计划里 0 图 custom→明确分类的重复标签；有图的留给 TagMergeService。</summary>
    private static int ApplyCustomTagMerges(
        SqliteConnection conn, IReadOnlyList<PlanTagMerge> merges, bool dryRun, Action<string>? log)
    {
        int done = 0;
        foreach (var merge in merges)
        {
            if (merge.LoserCategory != "custom" || merge.WinnerCategory is not ("character" or "artist" or "work" or "kind"))
            {
                log?.Invoke($"标签合并跳过（只处理计划内 0 图 custom→角色/画师/作品/类型）：{merge.LoserName}");
                continue;
            }
            var loserId = TagId(conn, merge.LoserCategory, merge.LoserName);
            var winnerId = TagId(conn, merge.WinnerCategory, merge.WinnerName);
            if (loserId == 0)
            {
                log?.Invoke(winnerId > 0
                    ? $"标签合并已处理过：{merge.LoserName} -> {merge.WinnerName}"
                    : $"标签合并跳过（两个标签都不存在）：{merge.LoserName} -> {merge.WinnerName}");
                continue;
            }
            if (winnerId == 0)
            {
                log?.Invoke($"标签合并跳过（winner 不存在）：{merge.LoserName} -> {merge.WinnerName}");
                continue;
            }
            int images;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM image_tags WHERE tag_id=$t";
                cmd.Parameters.AddWithValue("$t", loserId);
                images = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            if (images > 0)
            {
                log?.Invoke($"标签合并跳过（{merge.LoserName} 有 {images} 张图，请用 --merge-tags）");
                continue;
            }
            var currentState = TagDisplayState.Load(conn).GetValueOrDefault(loserId);
            using var protectedTag = conn.CreateCommand();
            protectedTag.CommandText = "SELECT 1 FROM tag_identities WHERE tag_id=$t AND (locked<>0 OR link_status='independent')";
            protectedTag.Parameters.AddWithValue("$t", loserId);
            if (currentState?.BlocksAutoAlign == true || !string.IsNullOrEmpty(currentState?.DisplayName)
                || !string.IsNullOrEmpty(currentState?.DisplayMode) || protectedTag.ExecuteScalar() is not null)
            {
                log?.Invoke($"标签合并跳过（{merge.LoserName} 已有个人设置）");
                continue;
            }
            if (dryRun)
            {
                log?.Invoke($"[预演] 合并标签 custom/{merge.LoserName} -> {merge.WinnerCategory}/{merge.WinnerName}");
                done++;
                continue;
            }

            var loserZh = ProfileValue(conn, loserId, "zh");
            var loserEn = ProfileValue(conn, loserId, "en");
            var loserEntity = ProfileValue(conn, loserId, "entity_key");
            var winnerZh = ProfileValue(conn, winnerId, "zh");
            var winnerEn = ProfileValue(conn, winnerId, "en");
            var winnerEntity = ProfileValue(conn, winnerId, "entity_key");
            var states = TagDisplayState.Load(conn);

            using var tx = conn.BeginTransaction();
            Exec(conn, tx, "INSERT OR IGNORE INTO tag_profiles(tag_id) VALUES($t)",
                ("$t", winnerId));
            if (loserZh.Length > 0 && winnerZh.Length == 0)
                Exec(conn, tx, "UPDATE tag_profiles SET zh=$v WHERE tag_id=$t",
                    ("$v", loserZh), ("$t", winnerId));
            if (loserEn.Length > 0 && winnerEn.Length == 0)
                Exec(conn, tx, "UPDATE tag_profiles SET en=$v WHERE tag_id=$t",
                    ("$v", loserEn), ("$t", winnerId));
            if (loserEntity.Length > 0 && winnerEntity.Length == 0)
                Exec(conn, tx, "UPDATE tag_profiles SET entity_key=$v WHERE tag_id=$t",
                    ("$v", loserEntity), ("$t", winnerId));
            Exec(conn, tx, "INSERT OR IGNORE INTO tag_aliases(tag_id,alias) SELECT $w,alias FROM tag_aliases WHERE tag_id=$l",
                ("$w", winnerId), ("$l", loserId));
            Exec(conn, tx,
                "INSERT OR IGNORE INTO tag_aliases(tag_id,alias) VALUES($t,$a)",
                ("$t", winnerId), ("$a", merge.LoserName));

            var moved = false;
            if (states.TryGetValue(loserId, out var loserState))
            {
                if (!states.ContainsKey(winnerId)) states[winnerId] = loserState;
                states.Remove(loserId);
                moved = true;
            }
            if (moved) TagDisplayState.Save(conn, states, tx);

            Exec(conn, tx, "DELETE FROM tags WHERE id=$t", ("$t", loserId));
            tx.Commit();
            log?.Invoke($"已合并标签 custom/{merge.LoserName} -> {merge.WinnerCategory}/{merge.WinnerName}（{merge.Note}）");
            done++;
        }
        return done;
    }

    private static string ProfileValue(SqliteConnection conn, int tagId, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE({column},'') FROM tag_profiles WHERE tag_id=$t";
        cmd.Parameters.AddWithValue("$t", tagId);
        return cmd.ExecuteScalar() as string ?? "";
    }

    /// <summary>同名多实体、需要人工消歧的标签：候选实体按作品排序，最多 6 个。</summary>
    public sealed record AmbiguousTag(
        int TagId, string TagName, List<(string Key, string Zh, string En, string Game)> Candidates);

    /// <summary>列出歧义标签（与 AlignTags 的判定口径一致）。</summary>
    public List<AmbiguousTag> AmbiguousTags(int maxCandidates = 6, int limit = 500)
    {
        var plan = LoadIdentityPlan();
        using var conn = Open();
        var states = TagDisplayState.Load(conn);
        foreach (var ps in plan.States)
        {
            var id = TagId(conn, ps.Category, ps.Tag);
            if (id > 0 && !states.ContainsKey(id))
                states[id] = new TagDisplayState.State(ps.Status, ps.Identity, ps.Origin,
                    ps.DisplayMode, ps.DisplayName, ps.Note);
        }

        var byTerm = LoadNameIndex(conn);
        var result = new List<AmbiguousTag>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT t.id,t.name," +
                "(SELECT COUNT(*) FROM image_tags it WHERE it.tag_id=t.id) " +
                "FROM tags t LEFT JOIN tag_profiles p ON p.tag_id=t.id AND p.entity_key<>'' " +
                "WHERE COALESCE(t.category,'custom')='character' AND p.tag_id IS NULL " +
                "ORDER BY t.name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                var name = r.GetString(1);
                if (r.GetInt32(2) == 0) continue;
                // manual 且还没选身份 = 待人工处理，仍要出现在歧义列表里；
                // 已经选了身份/明确外部/原创的才隐藏。
                if (states.TryGetValue(id, out var st) && st.BlocksAutoAlign &&
                    !(st.Status == "manual" && st.IdentityKey.Length == 0)) continue;
                var term = Norm(BaseName(name));
                if (term.Length == 0 || !byTerm.TryGetValue(term, out var raw)) continue;
                var hits = CollapseIdentities(raw);
                if (hits.Count < 2) continue;
                result.Add(new AmbiguousTag(id, name, hits
                    .OrderBy(h => h.Game, StringComparer.Ordinal)
                    .ThenBy(h => h.En, StringComparer.OrdinalIgnoreCase)
                    .Take(maxCandidates)
                    .Select(h => (h.Key, h.Zh, h.En, h.Game))
                    .ToList()));
                if (result.Count >= limit) break;
            }
        }
        return result;
    }

    /// <summary>人工消歧：把某个标签直接接到指定身份上，并记为 manual（以后自动对齐不再覆盖）。</summary>
    public void LinkTagToEntity(int tagId, string entityKey)
    {
        if (string.IsNullOrWhiteSpace(entityKey)) return;
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO tag_profiles(tag_id,zh,en,entity_key) " +
                "SELECT $t,COALESCE(zh,''),COALESCE(en,''),entity_key FROM catalog_entities " +
                "WHERE entity_key=$k " +
                "ON CONFLICT(tag_id) DO UPDATE SET zh=excluded.zh,en=excluded.en,entity_key=excluded.entity_key";
            cmd.Parameters.AddWithValue("$t", tagId);
            cmd.Parameters.AddWithValue("$k", entityKey);
            cmd.ExecuteNonQuery();
        }

        var identity = entityKey;
        var origin = "";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COALESCE(NULLIF(identity_key,''),entity_key),COALESCE(game_key,'') " +
                "FROM catalog_entities WHERE entity_key=$k";
            cmd.Parameters.AddWithValue("$k", entityKey);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                identity = r.GetString(0);
                origin = r.GetString(1);
            }
        }

        var states = TagDisplayState.Load(conn);
        var st = states.GetValueOrDefault(tagId) ?? new TagDisplayState.State();
        states[tagId] = st with
        {
            Status = "manual",
            IdentityKey = identity,
            OriginGame = origin,
        };
        TagDisplayState.Save(conn, states);
        WriteTagIdentity(conn, tagId, identity, "linked", locked: true, source: "manual");
    }

    /// <summary>身份关联的唯一落点；旧状态字段只留作显示与兼容读取。</summary>
    private static void WriteTagIdentity(SqliteConnection conn, int tagId, string identityKey,
        string linkStatus, bool locked, string source, string note = "", SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO tag_identities(tag_id,identity_key,link_status,source,locked,note,updated_at) " +
            "VALUES($t,$i,$s,$src,$l,$n,$now) " +
            "ON CONFLICT(tag_id) DO UPDATE SET identity_key=excluded.identity_key," +
            "link_status=excluded.link_status,source=excluded.source,locked=excluded.locked," +
            "note=excluded.note,updated_at=excluded.updated_at";
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.Parameters.AddWithValue("$i", identityKey ?? "");
        cmd.Parameters.AddWithValue("$s", linkStatus);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$l", locked ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", note);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>设置单个标签的状态/身份/显示方式；null 表示保持原值，空字符串表示清空。</summary>
    public void SetTagDisplay(int tagId, string? status = null, string? identityKey = null,
        string? originGame = null, string? displayMode = null, string? displayName = null)
    {
        using var conn = Open();
        var states = TagDisplayState.Load(conn);
        var st = states.GetValueOrDefault(tagId) ?? new TagDisplayState.State();
        var nextStatus = status ?? st.Status;
        var nextIdentity = identityKey ?? st.IdentityKey;
        states[tagId] = st with
        {
            Status = nextStatus,
            IdentityKey = nextIdentity,
            OriginGame = originGame ?? st.OriginGame,
            DisplayMode = displayMode ?? st.DisplayMode,
            DisplayName = displayName ?? st.DisplayName,
        };
        TagDisplayState.Save(conn, states);

        if (identityKey is not null || status is not null)
        {
            // 旧状态按实际关联迁移：original=独立；其余有身份即已关联，无身份保持待关联并保留保护。
            var linkStatus = nextStatus == "original"
                ? "independent"
                : nextIdentity.Length > 0 ? "linked" : "pending";
            WriteTagIdentity(conn, tagId, nextIdentity, linkStatus,
                locked: nextStatus is "manual" or "resolved" or "external" or "original",
                source: "manual", note: nextStatus.Length > 0 ? "legacy " + nextStatus : "");
        }
    }

    /// <summary>全局显示模式：zh_full / zh_short / alias / original / canonical。</summary>
    public void SetGlobalDisplayMode(string mode)
    {
        using var conn = Open();
        TagDisplayState.SetGlobalMode(conn, mode);
    }

    /// <summary>资料库浏览行：词库实体 + 关联标签的使用统计。</summary>
    public sealed record CatalogRow(
        string EntityKey, string GameKey, string Kind, string Zh, string En,
        string Aliases, int TagId, string TagName, int Usage);

    /// <summary>按关键词列出词库实体（含未关联标签的条目），并给出类型分布与总数。</summary>
    public List<CatalogRow> Browse(string? query, out List<(string Kind, int Count)> kinds,
        out int total, int limit = 2000)
    {
        var needle = Norm(query ?? "");
        var all = new List<CatalogRow>();
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT e.entity_key,COALESCE(e.game_key,''),COALESCE(e.kind,''),COALESCE(e.zh,'')," +
                "COALESCE(e.en,''),COALESCE(e.aliases,'[]')," +
                "COALESCE(p.tag_id,0),COALESCE(t.name,'')," +
                "(SELECT COUNT(*) FROM image_tags it WHERE it.tag_id=p.tag_id) " +
                "FROM catalog_entities e " +
                "LEFT JOIN tag_profiles p ON p.entity_key=e.entity_key " +
                "LEFT JOIN tags t ON t.id=p.tag_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var aliasesRaw = r.GetString(5);
                List<string> aliases;
                try { aliases = JsonSerializer.Deserialize<List<string>>(aliasesRaw) ?? []; }
                catch { aliases = []; }
                var zh = r.GetString(3);
                var en = r.GetString(4);
                if (needle.Length > 0)
                {
                    var hay = new[] { zh, en, r.GetString(1), r.GetString(0) }.Concat(aliases)
                        .Select(Norm).Where(x => x.Length > 0);
                    if (!hay.Any(x => x.Contains(needle, StringComparison.Ordinal))) continue;
                }
                all.Add(new CatalogRow(r.GetString(0), r.GetString(1), r.GetString(2), zh, en,
                    string.Join("、", aliases), r.GetInt32(6), r.GetString(7), r.GetInt32(8)));
            }
        }

        total = all.Count;
        kinds = all.GroupBy(x => x.Kind.Length == 0 ? "未分类" : x.Kind)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(g => g.Item2)
            .ToList();
        return all
            .OrderBy(x => x.TagId > 0 ? 0 : 1)
            .ThenByDescending(x => x.Usage)
            .ThenBy(x => x.Zh.Length == 0 ? 1 : 0)
            .ThenBy(x => x.Zh, StringComparer.Ordinal)
            .ThenBy(x => x.EntityKey, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// 保存人工资料：写 catalog_entities 的中/英名与别名，并把同名的标签补上 tag_profiles
    /// （人工资料优先于词库，词库更新不会覆盖）。返回被同步的标签数。
    /// </summary>
    public int UpdateEntity(string entityKey, string zh, string en, IEnumerable<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(entityKey)) return 0;
        zh = CanonicalName(zh);
        en = CanonicalName(en);
        if (zh.Length == 0 && en.Length == 0) throw new ArgumentException("词库名称不能为空");
        var aliasList = CanonicalAliases(aliases, zh, en);
        var aliasJson = JsonSerializer.Serialize(aliasList);

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        var oldNames = new List<string>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT COALESCE(zh,''),COALESCE(en,''),COALESCE(aliases,'[]') " +
                               "FROM catalog_entities WHERE entity_key=$k";
            read.Parameters.AddWithValue("$k", entityKey);
            using var r = read.ExecuteReader();
            if (r.Read())
            {
                oldNames.Add(r.GetString(0));
                oldNames.Add(r.GetString(1));
                try { oldNames.AddRange(JsonSerializer.Deserialize<List<string>>(r.GetString(2)) ?? []); }
                catch { /* 坏别名忽略 */ }
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE catalog_entities SET zh=$z,en=$e,aliases=$a,source_kind='manual' WHERE entity_key=$k";
            cmd.Parameters.AddWithValue("$z", zh.Trim());
            cmd.Parameters.AddWithValue("$e", en.Trim());
            cmd.Parameters.AddWithValue("$a", aliasJson);
            cmd.Parameters.AddWithValue("$k", entityKey);
            cmd.ExecuteNonQuery();
        }

        // 名称表：删旧加新（同时保留 entity_key 自身可检索）
        var names = new List<string> { zh, en };
        names.AddRange(aliasList);
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM catalog_names WHERE entity_key=$k";
            del.Parameters.AddWithValue("$k", entityKey);
            del.ExecuteNonQuery();
        }
        foreach (var name in names.Select(Norm).Where(n => n.Length > 0).Distinct())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO catalog_names(term,entity_key) VALUES($t,$k)";
            ins.Parameters.AddWithValue("$t", name);
            ins.Parameters.AddWithValue("$k", entityKey);
            ins.ExecuteNonQuery();
        }

        // 把同名（含旧名）的标签接到这个实体上
        var terms = names.Concat(oldNames).Select(Norm).Where(n => n.Length > 0).Distinct().ToList();
        var synced = 0;
        foreach (var term in terms)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO tag_profiles(tag_id,zh,en,entity_key) " +
                "SELECT id,$z,$e,$k FROM tags WHERE lower(name)=$t " +
                "ON CONFLICT(tag_id) DO UPDATE SET zh=excluded.zh,en=excluded.en,entity_key=excluded.entity_key";
            cmd.Parameters.AddWithValue("$z", zh.Trim());
            cmd.Parameters.AddWithValue("$e", en.Trim());
            cmd.Parameters.AddWithValue("$k", entityKey);
            cmd.Parameters.AddWithValue("$t", term);
            synced += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return synced;
    }
}
