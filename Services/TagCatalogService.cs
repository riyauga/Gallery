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

    private readonly StoreService _store;

    public TagCatalogService(StoreService store) => _store = store;

    public sealed record ImportReport(
        string SourceId, int Records, int MissingNames, string SourceUrl,
        List<string> Gaps, string Digest);

    /// <summary>程序目录旁的词库目录（用户改这里不用重新打包）。</summary>
    public static string ExternalDir =>
        Path.Combine(AppContext.BaseDirectory, "data", "tag_catalog");

    /// <summary>随程序分发的内置词库。</summary>
    public static string BundleDir =>
        Path.Combine(AppContext.BaseDirectory, "assets", "tag_catalog");

    /// <summary>外部在前、内置在后；同名文件外部优先。</summary>
    public static List<string> BundleDirs()
    {
        var dirs = new List<string>();
        foreach (var candidate in new[] { ExternalDir, BundleDir })
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

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>策略摘要：内容摘要再哈希一次，policy 变了可以强制重导。</summary>
    public static string PolicyDigest(string digest)
        => Sha256Hex(Encoding.UTF8.GetBytes(digest + ":explicit-identity-v2"));

    public sealed record Bundle(string Path, string Digest, JsonElement Root);

    /// <summary>读取词库（directory 给定时只读那一个目录，否则外部 + 内置合并）。</summary>
    public static List<Bundle> ReadBundles(string? directory = null)
    {
        var dirs = directory is null ? BundleDirs() : [directory];
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundles = new List<Bundle>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = System.IO.Path.GetFileName(path);
                if (!seenNames.Add(name)) continue;
                try
                {
                    var raw = File.ReadAllBytes(path);
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement.Clone();
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("source", out _) || !root.TryGetProperty("records", out _)) continue;
                    bundles.Add(new Bundle(path, Sha256Hex(raw), root));
                }
                catch (Exception) { }
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
    public List<ImportReport> ImportAll(string? directory = null, Action<string>? log = null)
    {
        var bundles = ReadBundles(directory);
        if (bundles.Count == 0) return [];

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
        if (pending.Count == 0)
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
                foreach (var (bundle, sid, sourceUrl, kind, policy) in pending)
                    reports.Add(ImportOne(conn, tx, bundle, sid, sourceUrl, kind, policy, log));

                // 成员关系整体重建：身份只认记录里显式写出的 identity_key
                Exec(conn, tx, "DELETE FROM catalog_memberships");
                foreach (var (bundle, sid, sourceUrl, _, _) in pending)
                {
                    foreach (var rec in Records(bundle))
                    {
                        var key = sid + ":" + rec.Key;
                        var identity = IdentityKey(conn, tx, key) ?? key;
                        var games = new List<string> { rec.Game };
                        games.AddRange(rec.Works);
                        foreach (var game in games.Distinct())
                            Exec(conn, tx,
                                "INSERT OR IGNORE INTO catalog_memberships(identity_key,game_key,source_url) VALUES($i,$g,$s)",
                                ("$i", identity), ("$g", game), ("$s", rec.SourceUrl ?? sourceUrl));
                    }
                }
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
            yield return new Rec(
                Str(r, "key"), Str(r, "game"), Str(r, "kind", "character"),
                Str(r, "zh"), Str(r, "en"),
                List(r, "aliases"), List(r, "source_tags"), List(r, "works"),
                Str(r, "status"), r.TryGetProperty("source_url", out var su) ? su.GetString() : null,
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
            hasProfile.CommandText = "SELECT 1 FROM tag_profiles WHERE entity_key=$k";
            hasProfile.Parameters.AddWithValue("$k", oldKey);
            if (hasProfile.ExecuteScalar() is not null) continue;   // 人工身份保留
            Exec(conn, tx, "DELETE FROM catalog_entities WHERE entity_key=$k", ("$k", oldKey));
        }

        if (bundle.Root.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in games.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                var gk = Str(g, "key");
                if (gk.Length == 0) continue;
                Exec(conn, tx,
                    "INSERT INTO catalog_games(game_key,zh,en,aliases,source_url) VALUES($k,$z,$e,$a,$u) " +
                    "ON CONFLICT(game_key) DO UPDATE SET zh=excluded.zh,en=excluded.en," +
                    "aliases=excluded.aliases,source_url=excluded.source_url",
                    ("$k", gk), ("$z", Str(g, "zh")), ("$e", Str(g, "en")),
                    ("$a", JsonSerializer.Serialize(List(g, "aliases"))),
                    ("$u", g.TryGetProperty("source_url", out var gsu) ? gsu.GetString() ?? sourceUrl : sourceUrl));
            }
        }

        var count = 0;
        var missing = 0;
        foreach (var r in records)
        {
            var key = sid + ":" + r.Key;
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

    /// <summary>写入人工资料（zh/en/entity_key），词库更新不会覆盖这些。</summary>
    public void SetProfile(int tagId, string zh = "", string en = "", string entityKey = "")
    {
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
        string EntityKey, string Zh, string En, string Kind, string GameKey, List<string> Aliases);

    /// <summary>在词库里搜角色/作品（标签编辑器用它把「刻晴」这类词加进标签）。</summary>
    public List<EntityHit> SearchEntities(string text, int limit = 30)
    {
        var needle = Norm(text ?? "");
        var exact = (text ?? "").Trim();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT e.entity_key,COALESCE(e.zh,''),COALESCE(e.en,''),COALESCE(e.kind,'character')," +
            "COALESCE(e.game_key,''),COALESCE(e.aliases,'[]') FROM catalog_entities e";
        using var r = cmd.ExecuteReader();
        var hits = new List<EntityHit>();
        while (r.Read())
        {
            List<string> aliases;
            try { aliases = JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? []; }
            catch { aliases = []; }
            var hit = new EntityHit(r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4), aliases);
            if (needle.Length > 0)
            {
                var haystack = new[] { hit.Zh, hit.En }.Concat(hit.Aliases)
                    .Select(Norm).Where(s => s.Length > 0).ToList();
                if (!haystack.Any(s => s.Contains(needle, StringComparison.Ordinal))) continue;
            }
            hits.Add(hit);
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
    public sealed record AlignReport(int Matched, int Ambiguous, int Unmatched, int Existing);

    /// <summary>
    /// 把现有标签按「规范化名称」接到词库实体上：term 唯一命中才写 tag_profiles（人工资料优先，
    /// 已有 entity_key 的不覆盖）；同名多实体记为 ambiguous，不猜。
    /// </summary>
    public AlignReport AlignTags(bool overwrite = false)
    {
        var byTerm = new Dictionary<string, List<(string Key, string Zh, string En)>>(StringComparer.Ordinal);
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
                if (!byTerm.TryGetValue(term, out var list))
                {
                    list = new List<(string, string, string)>();
                    byTerm[term] = list;
                }
                list.Add((r.GetString(3), r.GetString(1), r.GetString(2)));
            }
        }

        var tags = new List<(int Id, string Name)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id,name FROM tags";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tags.Add((r.GetInt32(0), r.GetString(1)));
        }

        var existing = new HashSet<int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT tag_id FROM tag_profiles WHERE entity_key<>''";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetInt32(0));
        }

        int matched = 0, ambiguous = 0, unmatched = 0, kept = 0;
        using var tx = conn.BeginTransaction();
        foreach (var (id, name) in tags)
        {
            var term = Norm(BaseName(name));
            if (term.Length == 0 || !byTerm.TryGetValue(term, out var hits))
            {
                unmatched++;
                continue;
            }
            if (hits.Count > 1)
            {
                ambiguous++;
                continue;
            }
            if (existing.Contains(id) && !overwrite)
            {
                kept++;
                continue;
            }
            var hit = hits[0];
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO tag_profiles(tag_id,zh,en,entity_key) VALUES($t,$z,$e,$k) " +
                "ON CONFLICT(tag_id) DO UPDATE SET zh=excluded.zh,en=excluded.en,entity_key=excluded.entity_key";
            cmd.Parameters.AddWithValue("$t", id);
            cmd.Parameters.AddWithValue("$z", hit.Zh);
            cmd.Parameters.AddWithValue("$e", hit.En);
            cmd.Parameters.AddWithValue("$k", hit.Key);
            cmd.ExecuteNonQuery();
            matched++;
        }
        tx.Commit();
        return new AlignReport(matched, ambiguous, unmatched, kept);
    }

    /// <summary>同名多实体、需要人工消歧的标签：候选实体按作品排序，最多 6 个。</summary>
    public sealed record AmbiguousTag(
        int TagId, string TagName, List<(string Key, string Zh, string En, string Game)> Candidates);

    /// <summary>列出歧义标签（与 AlignTags 的判定口径一致）。</summary>
    public List<AmbiguousTag> AmbiguousTags(int maxCandidates = 6, int limit = 500)
    {
        var byTerm = new Dictionary<string, List<(string Key, string Zh, string En, string Game)>>(StringComparer.Ordinal);
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT n.term,COALESCE(e.zh,''),COALESCE(e.en,''),e.entity_key,COALESCE(e.game_key,'') " +
                "FROM catalog_names n JOIN catalog_entities e ON e.entity_key=n.entity_key";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var term = r.GetString(0);
                if (term.Length == 0) continue;
                if (!byTerm.TryGetValue(term, out var list))
                {
                    list = new List<(string, string, string, string)>();
                    byTerm[term] = list;
                }
                list.Add((r.GetString(3), r.GetString(1), r.GetString(2), r.GetString(4)));
            }
        }

        var result = new List<AmbiguousTag>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT t.id,t.name FROM tags t " +
                "LEFT JOIN tag_profiles p ON p.tag_id=t.id AND p.entity_key<>'' " +
                "WHERE p.tag_id IS NULL ORDER BY t.name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                var name = r.GetString(1);
                var term = Norm(BaseName(name));
                if (term.Length == 0 || !byTerm.TryGetValue(term, out var hits) || hits.Count < 2) continue;
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

    /// <summary>人工消歧：把某个标签直接接到指定身份上。</summary>
    public void LinkTagToEntity(int tagId, string entityKey)
    {
        if (string.IsNullOrWhiteSpace(entityKey)) return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tag_profiles(tag_id,zh,en,entity_key) " +
            "SELECT $t,COALESCE(zh,''),COALESCE(en,''),entity_key FROM catalog_entities WHERE entity_key=$k " +
            "ON CONFLICT(tag_id) DO UPDATE SET zh=excluded.zh,en=excluded.en,entity_key=excluded.entity_key";
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.Parameters.AddWithValue("$k", entityKey);
        cmd.ExecuteNonQuery();
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
        var aliasList = aliases.Select(a => a.Trim()).Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
                "UPDATE catalog_entities SET zh=$z,en=$e,aliases=$a WHERE entity_key=$k";
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
