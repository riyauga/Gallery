using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

public sealed partial class TagCatalogService
{
    public sealed record LibraryEntry(string Key, string EntityKey, string IdentityKey, string Kind, string Zh,
        string En, string[] Aliases, string Origin, bool Created, bool Edited)
    {
        public string Name => Zh.Length > 0 ? Zh : En;
        public bool Matches(string search) => SearchMatcher(search)(this);

        public static Func<LibraryEntry, bool> SearchMatcher(string search)
        {
            var term = Norm(search);
            return term.Length == 0 ? _ => true : entry =>
                Norm(entry.Zh).Contains(term, StringComparison.Ordinal) ||
                Norm(entry.En).Contains(term, StringComparison.Ordinal) ||
                entry.Aliases.Any(name => Norm(name).Contains(term, StringComparison.Ordinal));
        }
    }

    public sealed record LibraryTag(int Id, string Name, int Usage, string Identity, string AssociatedName,
        string Category, string TagKey)
    {
        /// <summary>标签实际关联的资料键；批量预览按它还原每条记录自己的标签。</summary>
        public string TargetKey { get; init; } = "";
    }

    public sealed record LibrarySnapshot(IReadOnlyList<LibraryEntry> Works, IReadOnlyList<LibraryEntry> Characters,
        ILookup<string, string> Appearances, IReadOnlySet<(string Kind, string Key)> Covers);

    private sealed record PersonalEntry(string Key, string Kind, bool Created, string? Zh, string? En,
        string[]? Aliases, string? Origin);
    private sealed record PersonalAppearance(string Identity, string Work, bool Included);
    private sealed record PersonalTag(string TagKey, string Category, string Name, string Target, string DisplayMode = "", string DisplayName = "", bool NameFallback = true);
    private sealed record PersonalCatalog(PersonalEntry[] Entries, PersonalAppearance[] Appearances, PersonalTag[] Tags);
    private static readonly JsonSerializerOptions CatalogJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public LibrarySnapshot ReadLibrary()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(deferred: true);
        var works = ReadEntries(conn, tx, "work");
        var characters = ReadEntries(conn, tx, "character");
        var memberships = ReadAppearances(conn, tx);
        var covers = new HashSet<(string Kind, string Key)>();
        using (var cmd = Query(conn, tx, "SELECT kind,key FROM catalog_covers"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) covers.Add((reader.GetString(0), reader.GetString(1)));
        return new(works, characters, memberships, covers);
    }

    private static ILookup<string, string> ReadAppearances(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = Query(conn, tx, "SELECT identity_key,game_key FROM catalog_effective_memberships ORDER BY game_key");
        using var reader = cmd.ExecuteReader();
        var memberships = new List<(string Identity, string Work)>();
        while (reader.Read()) memberships.Add((reader.GetString(0), reader.GetString(1)));
        return memberships.ToLookup(m => m.Identity, m => m.Work);
    }

    public byte[]? ReadLibraryCover(string kind, string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT image FROM catalog_covers WHERE kind=$kind AND key=$key";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as byte[];
    }

    public void SaveLibraryCover(string kind, string key, byte[]? image)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        RequireEntry(conn, tx, kind, key);
        WriteLibraryCover(conn, tx, kind, key, image);
        tx.Commit();
    }

    private static void WriteLibraryCover(SqliteConnection conn, SqliteTransaction tx, string kind, string key, byte[]? image)
    {
        if (image is null)
            Exec(conn, tx, "DELETE FROM catalog_covers WHERE kind=$kind AND key=$key", ("$kind", kind), ("$key", key));
        else
            Exec(conn, tx, """
                INSERT INTO catalog_covers(kind,key,image) VALUES($kind,$key,$image)
                ON CONFLICT(kind,key) DO UPDATE SET image=excluded.image
                """, ("$kind", kind), ("$key", key), ("$image", image));
    }

    private static List<LibraryEntry> ReadEntries(SqliteConnection conn, SqliteTransaction tx, string kind, string? key = null)
    {
        var work = kind == "work";
        var groupKey = work ? "e.game_key" : "e.identity_key";
        var fields = work ? "COALESCE(NULLIF(g.zh,''),e.zh),COALESCE(NULLIF(g.en,''),e.en),g.aliases,e.game_key" : "e.zh,e.en,e.aliases,COALESCE(o.game_key,'')";
        var joins = work ? "JOIN catalog_effective_games g ON g.game_key=e.game_key"
            : "LEFT JOIN catalog_character_origins o ON o.identity_key=e.identity_key";
        using var cmd = Query(conn, tx, $"""
            WITH selected AS (
                SELECT {groupKey} key,e.entity_key,e.identity_key,e.zh,e.en,e.aliases,e.game_key,e.source_kind,
                       ROW_NUMBER() OVER(PARTITION BY {groupKey}
                           ORDER BY (i.primary_entity_key=e.entity_key) DESC,(e.entity_key=e.identity_key) DESC,
                                    (e.zh<>'') DESC,e.entity_key) rank
                FROM catalog_effective_entities e LEFT JOIN identities i ON i.identity_key=e.identity_key
                WHERE e.kind=$kind AND ($key IS NULL OR {groupKey}=$key)
            )
            SELECT e.key,e.entity_key,e.identity_key,{fields},e.source_kind='local',
                   (p.key IS NOT NULL OR e.source_kind='manual' OR EXISTS(
                       SELECT 1 FROM catalog_personal_memberships m WHERE m.identity_key=e.key AND $kind='character'))
            FROM selected e {joins}
            LEFT JOIN catalog_personal_entries p ON p.kind=$kind AND p.key=e.key
            WHERE e.rank=1
            """, ("$kind", kind), ("$key", (object?)key ?? DBNull.Value));
        using var r = cmd.ExecuteReader();
        var entries = new List<LibraryEntry>();
        while (r.Read())
            entries.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), kind, r.GetString(3), r.GetString(4),
                DecodeAliases(r.GetString(5)), r.GetString(6), r.GetBoolean(7), r.GetBoolean(8)));
        return entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
    }

    private static string[] DecodeAliases(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value) ?? []; }
        catch (JsonException) { return []; }
    }

    private static SqliteCommand Query(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    public string SaveLibraryEntry(string kind, string? key, string zh, string en, IEnumerable<string> aliases, string? origin)
        => SaveLibraryEntryCore(kind, key, zh, en, aliases, origin).Key;

    public LibraryEntry CreateLibraryEntry(string kind, string zh, string en, IEnumerable<string> aliases, string? origin)
        => SaveLibraryEntryCore(kind, null, zh, en, aliases, origin);

    private LibraryEntry SaveLibraryEntryCore(string kind, string? key, string zh, string en, IEnumerable<string> aliases, string? origin)
    {
        if (kind is not ("work" or "character")) throw new ArgumentException("资料类别无效");
        zh = CanonicalName(zh);
        en = CanonicalName(en);
        if (zh.Length == 0 && en.Length == 0) throw new ArgumentException("请填写中文名或英文名");
        var names = CanonicalAliases(aliases, zh, en).ToArray();

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var old = key is null ? null : RequireEntry(conn, tx, kind, key);
        if (kind == "character" && (old is null || origin is not null))
        {
            if (string.IsNullOrEmpty(origin)) throw new ArgumentException("请选择原作");
            using var work = Query(conn, tx, "SELECT 1 FROM catalog_effective_games WHERE game_key=$k", ("$k", origin));
            if (work.ExecuteScalar() is null) throw new InvalidOperationException("原作已不可用");
            if (old is { Origin.Length: > 0 })
                SaveAppearance(conn, tx, new(old.Key, old.Origin, true));
            if (old is not null) SaveAppearance(conn, tx, new(old.Key, origin, true));
        }

        if (key is null)
        {
            if (kind == "character") key = "local:" + Guid.NewGuid().ToString("N");
            else
            {
                // The key is also the fallback work tag name. Keep it readable and stable.
                var stem = CanonicalGameKey(en.Length > 0 ? en : zh);
                stem = RegexForWorkKey.Replace(stem, "_").Trim('_');
                if (stem.Length == 0) stem = "work";
                key = stem;
                for (var i = 2; WorkKeyExists(conn, tx, key); i++) key = stem + "_" + i;
            }
        }
        var personalOrigin = origin ?? SavedOrigin(conn, tx, key);
        SavePersonalEntry(conn, tx, new(key, kind, old?.Created ?? true, zh, en, names, personalOrigin));
        var generatedKey = kind == "work" ? "work:" + key : key;
        var entry = new LibraryEntry(key, old?.EntityKey ?? generatedKey, old?.IdentityKey ?? generatedKey,
            kind, zh, en, names, kind == "work" ? key : personalOrigin ?? "", old?.Created ?? true, true);
        if (old is null) EnsureIdentity(conn, tx, entry);
        tx.Commit();
        return entry;
    }

    private static readonly System.Text.RegularExpressions.Regex RegexForWorkKey = new(@"[^\p{L}\p{N}_]+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool WorkKeyExists(SqliteConnection conn, SqliteTransaction tx, string key)
    {
        using var cmd = Query(conn, tx, """
            SELECT 1 FROM catalog_games WHERE game_key=$key
            UNION ALL SELECT 1 FROM catalog_personal_entries WHERE kind='work' AND key=$key
            UNION ALL SELECT 1 FROM catalog_deleted_entries WHERE kind='work' AND key=$key
            UNION ALL SELECT 1 FROM identities WHERE identity_key='work:'||$key LIMIT 1
            """, ("$key", key));
        return cmd.ExecuteScalar() is not null;
    }

    private static string? SavedOrigin(SqliteConnection conn, SqliteTransaction tx, string key)
    {
        using var cmd = Query(conn, tx, "SELECT origin FROM catalog_personal_entries WHERE kind='character' AND key=$k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    private static LibraryEntry RequireEntry(SqliteConnection conn, SqliteTransaction tx, string kind, string key)
        => ReadEntries(conn, tx, kind, key).SingleOrDefault() ?? throw new InvalidOperationException("这条资料已不可用");

    private static void SavePersonalEntry(SqliteConnection conn, SqliteTransaction tx, PersonalEntry entry)
    {
        var terms = entry.Aliases?.Append(entry.Zh ?? "").Append(entry.En ?? "")
            .Concat(entry.Kind == "work" ? [entry.Key] : [])
            .Select(Norm).Where(t => t.Length > 0).Distinct().ToArray();
        Exec(conn, tx, """
            INSERT INTO catalog_personal_entries(key,kind,created,zh,en,aliases,terms,origin)
            VALUES($k,$kind,$created,$zh,$en,$aliases,$terms,$origin)
            ON CONFLICT(kind,key) DO UPDATE SET created=excluded.created,zh=excluded.zh,en=excluded.en,
              aliases=excluded.aliases,terms=excluded.terms,origin=excluded.origin
            """, ("$k", entry.Key), ("$kind", entry.Kind), ("$created", entry.Created ? 1 : 0),
            ("$zh", (object?)entry.Zh ?? DBNull.Value), ("$en", (object?)entry.En ?? DBNull.Value),
            ("$aliases", entry.Aliases is null ? DBNull.Value : JsonSerializer.Serialize(entry.Aliases)),
            ("$terms", terms is null ? DBNull.Value : JsonSerializer.Serialize(terms)),
            ("$origin", (object?)entry.Origin ?? DBNull.Value));
    }

    private static void EnsureIdentity(SqliteConnection conn, SqliteTransaction tx, LibraryEntry entry)
        => Exec(conn, tx, """
            INSERT OR IGNORE INTO identities(identity_key,kind,zh,en,primary_entity_key,created_at,updated_at)
            VALUES($i,$kind,$zh,$en,$e,unixepoch(),unixepoch())
            """, ("$i", entry.IdentityKey), ("$kind", entry.Kind), ("$zh", entry.Zh), ("$en", entry.En), ("$e", entry.EntityKey));

    private static List<string> EffectiveWorks(SqliteConnection conn, string identity, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT game_key FROM catalog_effective_memberships WHERE identity_key=$i";
        cmd.Parameters.AddWithValue("$i", identity);
        using var reader = cmd.ExecuteReader();
        var works = new List<string>();
        while (reader.Read()) works.Add(reader.GetString(0));
        return works;
    }

    private static void SaveAppearance(SqliteConnection conn, SqliteTransaction tx, PersonalAppearance appearance)
        => Exec(conn, tx, """
            INSERT INTO catalog_personal_memberships(identity_key,game_key,included) VALUES($i,$g,$b)
            ON CONFLICT(identity_key,game_key) DO UPDATE SET included=excluded.included
            """, ("$i", appearance.Identity), ("$g", appearance.Work), ("$b", appearance.Included ? 1 : 0));

    public void SetAppearance(string identity, string work, bool included)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var person = RequireEntry(conn, tx, "character", identity);
        RequireEntry(conn, tx, "work", work);
        if (person.Origin.Length == 0) throw new InvalidOperationException("请先为角色指定原作");
        if (!included && person.Origin == work) throw new InvalidOperationException("可以更换原作，不能移除原作");
        SaveAppearance(conn, tx, new(identity, work, included));
        tx.Commit();
    }

    public void RestoreLibraryEntry(string kind, string key)
    {
        lock (PackGate)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var entry = RequireEntry(conn, tx, kind, key);
            if (entry.Created) throw new InvalidOperationException("自建资料没有资料包版本");
            var legacy = LegacyOriginal(conn, tx, entry);
            Exec(conn, tx, "DELETE FROM catalog_personal_entries WHERE kind=$kind AND key=$k", ("$kind", kind), ("$k", key));
            if (kind == "character")
                Exec(conn, tx, "DELETE FROM catalog_personal_memberships WHERE identity_key=$k", ("$k", key));
            if (legacy is { } original)
            {
                var (entity, record) = original;
                Exec(conn, tx, "UPDATE catalog_entities SET zh=$zh,en=$en,aliases=$a,source_kind='community' WHERE entity_key=$k",
                    ("$zh", record.Zh), ("$en", record.En), ("$a", JsonSerializer.Serialize(record.Aliases)), ("$k", entity));
                Exec(conn, tx, "DELETE FROM catalog_names WHERE entity_key=$k", ("$k", entity));
                foreach (var term in record.Aliases.Append(record.Zh).Append(record.En).Select(Norm)
                             .Where(t => t.Length > 0).Distinct())
                    Exec(conn, tx, "INSERT INTO catalog_names(term,entity_key) VALUES($t,$k)", ("$t", term), ("$k", entity));
            }
            tx.Commit();
        }
    }

    private static (string Entity, Rec Record)? LegacyOriginal(SqliteConnection conn, SqliteTransaction tx, LibraryEntry entry)
    {
        using var cmd = Query(conn, tx, "SELECT 1 FROM catalog_entities WHERE entity_key=$k AND source_kind='manual'", ("$k", entry.EntityKey));
        if (cmd.ExecuteScalar() is null) return null;
        foreach (var bundle in ReadBundles())
        {
            var prefix = Str(bundle.Root.GetProperty("source"), "id") + ":";
            if (!entry.EntityKey.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var record = Records(bundle).FirstOrDefault(r => prefix + r.Key == entry.EntityKey);
            if (record is not null) return (entry.EntityKey, record);
        }
        throw new InvalidOperationException("请先安装这条资料的原始资料包");
    }

    public List<LibraryTag> LibraryTags(string kind, string? entryKey = null)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(deferred: true);
        return ReadLibraryTags(conn, tx, kind, entryKey);
    }

    private static List<LibraryTag> ReadLibraryTags(SqliteConnection conn, SqliteTransaction tx, string kind, string? entryKey,
        IReadOnlyCollection<string>? targetKeys = null)
    {
        using var cmd = Query(conn, tx, """
            WITH entities AS MATERIALIZED (SELECT * FROM catalog_effective_entities WHERE kind=$kind)
            SELECT t.id,t.name,(SELECT COUNT(*) FROM image_tags it WHERE it.tag_id=t.id),
                   COALESCE(NULLIF(ti.identity_key,''),e.identity_key,''),COALESCE(NULLIF(g.zh,''),NULLIF(g.en,''),NULLIF(e.zh,''),NULLIF(e.en,''),''),
                   t.category,COALESCE(t.tag_key,''),
                   COALESCE(CASE WHEN t.category='work' THEN e.game_key
                       ELSE COALESCE(NULLIF(ti.identity_key,''),e.identity_key) END,'')
            FROM tags t LEFT JOIN tag_identities ti ON ti.tag_id=t.id
            LEFT JOIN tag_profiles f ON f.tag_id=t.id
            LEFT JOIN entities e ON COALESCE(ti.link_status,'')<>'independent' AND e.kind=t.category
                AND e.entity_key=COALESCE(
                (SELECT p.entity_key FROM entities p
                 WHERE p.entity_key=f.entity_key AND p.kind=t.category
                   AND (COALESCE(ti.identity_key,'')='' OR p.identity_key=ti.identity_key)),
                (SELECT a.entity_key FROM entities a
                 WHERE a.identity_key=ti.identity_key AND a.kind=t.category ORDER BY (a.entity_key=ti.identity_key) DESC,a.entity_key LIMIT 1))
            LEFT JOIN catalog_effective_games g ON t.category='work' AND g.game_key=e.game_key
            WHERE t.category=$kind AND ($key IS NULL OR
                CASE WHEN t.category='work' THEN e.game_key ELSE COALESCE(NULLIF(ti.identity_key,''),e.identity_key) END=$key)
              AND ($keys IS NULL OR (CASE WHEN t.category='work' THEN e.game_key
                   ELSE COALESCE(NULLIF(ti.identity_key,''),e.identity_key) END) IN (SELECT value FROM json_each($keys)))
            ORDER BY t.name COLLATE NOCASE,t.id
            """, ("$kind", kind), ("$key", (object?)entryKey ?? DBNull.Value),
                ("$keys", targetKeys is null ? DBNull.Value : JsonSerializer.Serialize(targetKeys)));
        using var r = cmd.ExecuteReader();
        var tags = new List<LibraryTag>();
        while (r.Read())
            tags.Add(new(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6))
            {
                TargetKey = r.GetString(7),
            });
        return tags;
    }

    public void SetLibraryTag(int tagId, string kind, string? entryKey, string expectedIdentity)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = Query(conn, tx, """
            SELECT t.name,t.category,t.tag_key,COALESCE(NULLIF(i.identity_key,''),e.identity_key,'') FROM tags t
            LEFT JOIN tag_identities i ON i.tag_id=t.id LEFT JOIN tag_profiles p ON p.tag_id=t.id
            LEFT JOIN catalog_effective_entities e ON e.entity_key=p.entity_key AND e.kind=t.category
                AND COALESCE(i.link_status,'')<>'independent'
            WHERE t.id=$id
            """, ("$id", tagId));
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.GetString(1) != kind) throw new InvalidOperationException("图库标签已不可用或分类不符");
        if (r.GetString(3) != expectedIdentity) throw new InvalidOperationException("标签关联已变化，请重新确认");
        var rule = new PersonalTag(r.GetString(2), kind, r.GetString(0), entryKey ?? "");
        r.Close();
        var entry = entryKey is null ? null : RequireEntry(conn, tx, kind, entryKey);
        SavePersonalTag(conn, tx, rule);
        WriteLibraryTag(conn, tx, tagId, entry);
        tx.Commit();
    }

    private static void SavePersonalTag(SqliteConnection conn, SqliteTransaction tx, PersonalTag tag)
        => Exec(conn, tx, """
            INSERT INTO catalog_personal_tags(tag_key,category,name,target,display_mode,display_name,name_fallback) VALUES($k,$kind,$n,$t,$m,$d,$fallback)
            ON CONFLICT(tag_key) DO UPDATE SET category=excluded.category,name=excluded.name,target=excluded.target,
                display_mode=excluded.display_mode,display_name=excluded.display_name,name_fallback=excluded.name_fallback
            """, ("$k", tag.TagKey), ("$kind", tag.Category), ("$n", tag.Name), ("$t", tag.Target), ("$m", tag.DisplayMode), ("$d", tag.DisplayName), ("$fallback", tag.NameFallback ? 1 : 0));

    private static void WriteLibraryTag(SqliteConnection conn, SqliteTransaction tx, int tagId, LibraryEntry? entry,
        PersonalTag? display = null, Dictionary<int, TagDisplayState.State>? stateBatch = null)
    {
        var states = stateBatch ?? TagDisplayState.Load(conn, tx);
        var st = states.GetValueOrDefault(tagId) ?? new TagDisplayState.State();
        var identity = entry?.IdentityKey ?? "";
        if (entry is null)
            Exec(conn, tx, "DELETE FROM tag_profiles WHERE tag_id=$id", ("$id", tagId));
        else
        {
            EnsureIdentity(conn, tx, entry);
            WriteProfile(conn, tx, tagId, entry.EntityKey, null);
        }
        states[tagId] = st with { Status = entry is null ? "original" : "manual", IdentityKey = identity,
            OriginGame = entry?.Origin ?? "", DisplayMode = display?.DisplayMode ?? st.DisplayMode,
            DisplayName = display?.DisplayName ?? st.DisplayName };
        if (stateBatch is null) TagDisplayState.Save(conn, states, tx);
        WriteTagIdentity(conn, tagId, identity, entry is null ? "independent" : "linked", true, "manual", tx: tx);
    }

    public sealed record LibraryDeletion(string Kind, string Key, string[] Characters, IReadOnlyList<LibraryTag> Tags);

    public LibraryDeletion GetLibraryDeletion(string kind, string key)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(deferred: true);
        return ReadLibraryDeletion(conn, tx, kind, key);
    }

    /// <summary>一次连接读取多条资料的删除影响；每条记录的 Tags 与单条 GetLibraryDeletion 一致，供提交时逐条校验。</summary>
    public LibraryDeletion[] GetLibraryDeletions(IReadOnlyList<(string Kind, string Key)> entries)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(deferred: true);
        var workKeys = entries.Where(e => e.Kind == "work").Select(e => e.Key).Distinct(StringComparer.Ordinal).ToArray();
        var characterKeys = entries.Where(e => e.Kind == "character").Select(e => e.Key).Distinct(StringComparer.Ordinal).ToArray();

        var origins = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (workKeys.Length > 0)
        {
            var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using var cmd = Query(conn, tx, """
                SELECT game_key,identity_key FROM catalog_character_origins
                WHERE game_key IN (SELECT value FROM json_each($keys)) ORDER BY game_key,identity_key
                """, ("$keys", JsonSerializer.Serialize(workKeys)));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var work = reader.GetString(0);
                if (!grouped.TryGetValue(work, out var list)) grouped[work] = list = [];
                list.Add(reader.GetString(1));
            }
            origins = grouped.ToDictionary(g => g.Key, g => g.Value.ToArray(), StringComparer.Ordinal);
        }

        var derived = origins.Values.SelectMany(keys => keys).Distinct(StringComparer.Ordinal).ToArray();
        var workTags = workKeys.Length > 0 ? ReadLibraryTags(conn, tx, "work", null, workKeys) : [];
        var characterTags = characterKeys.Length > 0 ? ReadLibraryTags(conn, tx, "character", null, characterKeys) : [];
        // 原单条预览对派生角色是一次按名称排序的整体查询；这里保留同一顺序，提交时 SequenceEqual 才不会误判关联变化。
        var derivedTags = derived.Length > 0 ? ReadLibraryTags(conn, tx, "character", null, derived) : [];
        var workByKey = workTags.GroupBy(tag => tag.TargetKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var characterByKey = characterTags.GroupBy(tag => tag.TargetKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        IReadOnlyList<LibraryTag> TagsOf(Dictionary<string, List<LibraryTag>> index, string key)
            => index.TryGetValue(key, out var list) ? list : [];

        return entries.Select(e =>
        {
            if (e.Kind == "character") return new LibraryDeletion(e.Kind, e.Key, [e.Key], TagsOf(characterByKey, e.Key));
            var characters = origins.GetValueOrDefault(e.Key, []);
            var affected = characters.ToHashSet(StringComparer.Ordinal);
            var own = TagsOf(workByKey, e.Key).ToList();
            own.AddRange(derivedTags.Where(tag => affected.Contains(tag.TargetKey)));
            return new LibraryDeletion(e.Kind, e.Key, characters, own);
        }).ToArray();
    }

    private static LibraryDeletion ReadLibraryDeletion(SqliteConnection conn, SqliteTransaction tx, string kind, string key)
    {
        var characters = new List<string>();
        if (kind == "work")
        {
            using var cmd = Query(conn, tx, "SELECT identity_key FROM catalog_character_origins WHERE game_key=$k ORDER BY identity_key", ("$k", key));
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) characters.Add(reader.GetString(0));
        }
        var tags = ReadLibraryTags(conn, tx, kind, key);
        if (characters.Count > 0) tags.AddRange(ReadLibraryTags(conn, tx, "character", null, characters));
        return new(kind, key, [.. characters], tags);
    }

    public bool DeleteLibraryEntry(LibraryDeletion preview) => DeleteLibraryEntries([preview]);

    public bool DeleteLibraryEntries(IReadOnlyList<LibraryDeletion> previews)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var latest = new List<LibraryDeletion>();
        foreach (var preview in previews)
        {
            RequireEntry(conn, tx, preview.Kind, preview.Key);
            // 删除一律按提交瞬间的关联执行。预览与提交是两次独立查询，逐条比对会把
            // 后台导入、标签状态刷新或视图排序差异误判为「关联已变化」而拒绝删除。
            latest.Add(ReadLibraryDeletion(conn, tx, preview.Kind, preview.Key));
        }
        var tags = latest.SelectMany(item => item.Tags).DistinctBy(tag => tag.Id).ToArray();
        if (tags.Length > 0)
        {
            var states = TagDisplayState.Load(conn, tx);
            foreach (var tag in tags)
            {
                SavePersonalTag(conn, tx, new(tag.TagKey, tag.Category, tag.Name, ""));
                WriteLibraryTag(conn, tx, tag.Id, null, stateBatch: states);
            }
            TagDisplayState.Save(conn, states, tx);
        }

        foreach (var item in latest)
        {
            var characterKeys = item.Kind == "character" ? [item.Key] : item.Characters;
            var workKey = item.Kind == "work" ? item.Key : "";
            var args = new (string Name, object Value)[] { ("$work", workKey), ("$characters", JsonSerializer.Serialize(characterKeys)) };
            Exec(conn, tx, """
                INSERT INTO catalog_deleted_entries(kind,key)
                    SELECT 'work',$work WHERE $work<>''
                    UNION ALL SELECT 'character',value FROM json_each($characters);
                DELETE FROM catalog_personal_tags WHERE (category='work' AND target=$work AND $work<>'')
                    OR (category='character' AND target IN (SELECT value FROM json_each($characters)));
                DELETE FROM catalog_personal_entries WHERE (kind='work' AND key=$work)
                    OR (kind='character' AND key IN (SELECT value FROM json_each($characters)));
                DELETE FROM catalog_personal_memberships WHERE game_key=$work
                    OR identity_key IN (SELECT value FROM json_each($characters));
                DELETE FROM catalog_covers WHERE (kind='work' AND key=$work)
                    OR (kind='character' AND key IN (SELECT value FROM json_each($characters)));
                """, args);
        }
        tx.Commit();
        return tags.Length > 0;
    }

    private static PersonalCatalog ReadPersonalCatalog(SqliteConnection conn, SqliteTransaction tx)
    {
        var entries = new List<PersonalEntry>();
        var appearances = new List<PersonalAppearance>();
        var tags = new List<PersonalTag>();
        var display = new Dictionary<string, TagDisplayState.State>();
        var states = TagDisplayState.Load(conn, tx);
        using (var cmd = Query(conn, tx, "SELECT t.tag_key,t.id FROM tags t JOIN catalog_personal_tags p ON p.tag_key=t.tag_key"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (states.TryGetValue(r.GetInt32(1), out var state)) display[r.GetString(0)] = state;
        using (var cmd = Query(conn, tx, "SELECT key,kind,created,zh,en,aliases,origin FROM catalog_personal_entries ORDER BY kind,key"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) entries.Add(new(r.GetString(0), r.GetString(1), r.GetBoolean(2), NullableText(r, 3),
                NullableText(r, 4), r.IsDBNull(5) ? null : DecodeAliases(r.GetString(5)), NullableText(r, 6)));
        using (var cmd = Query(conn, tx, "SELECT identity_key,game_key,included FROM catalog_personal_memberships ORDER BY identity_key,game_key"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) appearances.Add(new(r.GetString(0), r.GetString(1), r.GetBoolean(2)));
        using (var cmd = Query(conn, tx, """
            SELECT p.tag_key,p.category,COALESCE(t.name,p.name),p.target,p.display_mode,p.display_name,
                   p.name_fallback AND (t.id IS NULL OR 1=(SELECT COUNT(*) FROM tags x WHERE x.category=t.category AND x.name=t.name))
            FROM catalog_personal_tags p
            LEFT JOIN tags t ON t.tag_key=p.tag_key ORDER BY p.tag_key
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) {
                var state = display.GetValueOrDefault(r.GetString(0));
                tags.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    state?.DisplayMode ?? r.GetString(4), state?.DisplayName ?? r.GetString(5), r.GetBoolean(6)));
            }
        return new([.. entries], [.. appearances], [.. tags]);
    }

    private static string? NullableText(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static PersonalCatalog ParsePersonalCatalog(JsonElement element)
    {
        var value = element.Deserialize<PersonalCatalog>(CatalogJson);
        if (value?.Entries is null || value.Appearances is null || value.Tags is null ||
            value.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Key) || e.Kind is not ("work" or "character")
                || e.Created && (e.Zh is null || e.En is null || string.IsNullOrWhiteSpace(e.Zh) && string.IsNullOrWhiteSpace(e.En)
                    || e.Aliases is null)
                || e.Aliases?.Any(a => a is null) == true || e.Kind == "work" && e.Origin is not null) ||
            value.Appearances.Any(a => a is null || string.IsNullOrWhiteSpace(a.Identity) || string.IsNullOrWhiteSpace(a.Work)) ||
            value.Tags.Any(t => t is null || string.IsNullOrWhiteSpace(t.TagKey) || string.IsNullOrWhiteSpace(t.Name)
                || t.Category is not ("work" or "character") || t.Target is null || t.DisplayName is null
                || t.DisplayMode is not ("" or "zh_full" or "zh_short" or "alias" or "original" or "canonical")) ||
            value.Entries.DistinctBy(e => (e.Kind, e.Key)).Count() != value.Entries.Length ||
            value.Appearances.DistinctBy(a => (a.Identity, a.Work)).Count() != value.Appearances.Length ||
            value.Tags.DistinctBy(t => t.TagKey).Count() != value.Tags.Length)
            throw new InvalidDataException("个人资料库备份内容无效");
        return value;
    }

    private static int RestorePersonalCatalog(SqliteConnection conn, SqliteTransaction tx, PersonalCatalog incoming, bool overwrite)
    {
        var current = ReadPersonalCatalog(conn, tx);
        var existingEntries = current.Entries.ToDictionary(e => (e.Kind, e.Key));
        foreach (var entry in incoming.Entries)
        {
            if (existingEntries.TryGetValue((entry.Kind, entry.Key), out var old))
            {
                if (!overwrite) continue;
                if (old.Created != entry.Created) throw new InvalidDataException("备份中的作品键或角色键已被其他资料使用");
            }
            if (entry.Created && !existingEntries.ContainsKey((entry.Kind, entry.Key)) &&
                ReadEntries(conn, tx, entry.Kind, entry.Key).Count > 0)
                throw new InvalidDataException("备份中的作品键或角色键已被其他资料使用");
            SavePersonalEntry(conn, tx, entry);
        }

        var existingAppearances = current.Appearances.Select(a => (a.Identity, a.Work)).ToHashSet();
        foreach (var appearance in incoming.Appearances)
            if (overwrite || !existingAppearances.Contains((appearance.Identity, appearance.Work)))
                SaveAppearance(conn, tx, appearance);

        foreach (var entry in incoming.Entries.Where(e => e.Created))
            EnsureIdentity(conn, tx, RequireEntry(conn, tx, entry.Kind, entry.Key));
        var currentKeys = current.Tags.Select(t => t.TagKey).ToHashSet();
        var currentTargets = current.Tags.Select(t => FindPersonalTag(conn, tx, t)).Where(id => id > 0).ToHashSet();
        foreach (var tag in incoming.Tags)
        {
            var id = FindPersonalTag(conn, tx, tag);
            if (!overwrite && (currentKeys.Contains(tag.TagKey) || id > 0 &&
                (currentTargets.Contains(id) || HasManualTagDecision(conn, tx, id)))) continue;
            if (id > 0)
            {
                var key = GetTagKey(conn, tx, id);
                SavePersonalTag(conn, tx, tag with { TagKey = key });
                if (key != tag.TagKey)
                    Exec(conn, tx, "DELETE FROM catalog_personal_tags WHERE tag_key=$k", ("$k", tag.TagKey));
                var entry = tag.Target.Length == 0 ? null : ReadEntries(conn, tx, tag.Category, tag.Target).SingleOrDefault();
                if (entry is not null || tag.Target.Length == 0) WriteLibraryTag(conn, tx, id, entry, tag);
            }
            else SavePersonalTag(conn, tx, tag);
        }

        return ApplyPersonalCatalogTags(conn, tx) + current.Entries.Concat(incoming.Entries).DistinctBy(e => (e.Kind, e.Key))
            .Count(e => !e.Created && ReadEntries(conn, tx, e.Kind, e.Key).Count == 0)
            + incoming.Appearances.Count(a => ReadEntries(conn, tx, "character", a.Identity).Count == 0 ||
                ReadEntries(conn, tx, "work", a.Work).Count == 0)
            + incoming.Entries.Count(e => e.Origin is not null && ReadEntries(conn, tx, "work", e.Origin).Count == 0);
    }

    private static int FindPersonalTag(SqliteConnection conn, SqliteTransaction tx, PersonalTag tag)
    {
        using var cmd = Query(conn, tx, """
            SELECT COALESCE(
                (SELECT id FROM tags WHERE tag_key=$k AND category=$c),
                (SELECT CASE WHEN COUNT(*)=1 THEN MIN(id) ELSE 0 END FROM tags WHERE $fallback=1 AND category=$c AND name=$n COLLATE NOCASE))
            """, ("$k", tag.TagKey), ("$c", tag.Category), ("$n", tag.Name), ("$fallback", tag.NameFallback ? 1 : 0));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string GetTagKey(SqliteConnection conn, SqliteTransaction tx, int tagId)
    {
        using var cmd = Query(conn, tx, "SELECT tag_key FROM tags WHERE id=$id", ("$id", tagId));
        return (string)cmd.ExecuteScalar()!;
    }

    private static bool HasManualTagDecision(SqliteConnection conn, SqliteTransaction tx, int id)
    {
        using var cmd = Query(conn, tx, "SELECT 1 FROM tag_identities WHERE tag_id=$id AND (locked<>0 OR link_status='independent')",
            ("$id", id));
        return cmd.ExecuteScalar() is not null;
    }

    private static bool CatalogManagesTag(SqliteConnection conn, SqliteTransaction? tx, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM catalog_personal_tags p JOIN tags t ON t.tag_key=p.tag_key WHERE t.id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool HasLibraryTagBinding(SqliteConnection conn, SqliteTransaction tx, int id, LibraryEntry? entry)
    {
        using var cmd = Query(conn, tx, """
            SELECT 1 FROM tag_identities t LEFT JOIN tag_profiles p ON p.tag_id=t.tag_id
            WHERE t.tag_id=$id AND t.identity_key=$i AND t.link_status=$s AND t.locked=1
                AND COALESCE(p.entity_key,'')=$e
            """, ("$id", id), ("$i", entry?.IdentityKey ?? ""), ("$e", entry?.EntityKey ?? ""),
            ("$s", entry is null ? "independent" : "linked"));
        return cmd.ExecuteScalar() is not null;
    }

    private static int ApplyPersonalCatalogTags(SqliteConnection conn, SqliteTransaction tx)
    {
        var pending = 0;
        var rules = ReadPersonalCatalog(conn, tx).Tags;
        var targets = new Dictionary<string, Dictionary<string, LibraryEntry>>();
        foreach (var tag in rules)
        {
            var id = FindPersonalTag(conn, tx, tag);
            LibraryEntry? entry = null;
            if (tag.Target.Length > 0)
            {
                if (!targets.TryGetValue(tag.Category, out var entries))
                    targets[tag.Category] = entries = ReadEntries(conn, tx, tag.Category).ToDictionary(e => e.Key);
                entry = entries.GetValueOrDefault(tag.Target);
            }
            if (id == 0 || tag.Target.Length > 0 && entry is null) { pending++; continue; }

            var key = GetTagKey(conn, tx, id);
            if (key != tag.TagKey)
            {
                using var found = Query(conn, tx, "SELECT 1 FROM catalog_personal_tags WHERE tag_key=$k", ("$k", key));
                if (found.ExecuteScalar() is not null) { pending++; continue; }
                SavePersonalTag(conn, tx, tag with { TagKey = key });
                Exec(conn, tx, "DELETE FROM catalog_personal_tags WHERE tag_key=$k", ("$k", tag.TagKey));
            }
            if (!HasLibraryTagBinding(conn, tx, id, entry))
                WriteLibraryTag(conn, tx, id, entry, tag);
        }
        return pending;
    }
}
