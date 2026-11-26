using System.Text.Json;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;

namespace Gallery.Services;

public sealed partial class TagCatalogService
{
    private const string LibraryArchiveFormat = "gallery-catalog";
    public sealed record CatalogRecord(string EntityId, string Kind, string Zh, string En, string[] Aliases,
        string? Origin, string[] Appearances, string? CoverHash)
    {
        public string Name => Zh.Length > 0 ? Zh : En;
    }
    public sealed record CatalogIdentity(string EntityId, string TargetEntityId);
    public sealed record CatalogDistinct(string EntityIdA, string EntityIdB);
    public sealed record CatalogAsset(string Hash, byte[] Image);
    public sealed record CatalogDocument(string Format, int Version, CatalogRecord[] Entries,
        CatalogIdentity[] EntityIds, CatalogDistinct[] DistinctPairs, CatalogAsset[] Assets);
    private sealed record ArchiveEntry(string Kind, string Key, string Zh, string En, string[] Aliases,
        string? Origin, string[] Appearances, byte[]? Cover);
    private sealed record LibraryArchive(string Format, int Version, ArchiveEntry[] Entries);

    public sealed class CatalogImportChoice(CatalogRecord incoming, CatalogRecord[] candidates, bool sameIdentity)
    {
        public CatalogRecord Incoming { get; } = incoming;
        public CatalogRecord[] Candidates { get; } = candidates;
        public bool SameIdentity { get; } = sameIdentity;
        // null means a separate new entity; a known identity cannot become a new entity.
        public string? TargetEntityId { get; set; } = sameIdentity ? candidates[0].EntityId : null;
        public bool PreferIncoming { get; set; }
    }
    public sealed record CatalogImportBatch(string Kind, CatalogImportChoice[] Choices,
        IReadOnlyDictionary<string, string> WorkNames, IReadOnlyDictionary<string, byte[]> Images);

    private static CatalogDocument ReadCatalogDocument(SqliteConnection conn, SqliteTransaction tx)
    {
        var appearances = new Dictionary<string, List<string>>();
        using (var cmd = Query(conn, tx, "SELECT character_entity_id,work_entity_id FROM catalog_appearances ORDER BY work_entity_id"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (!appearances.TryGetValue(r.GetString(0), out var works)) appearances[r.GetString(0)] = works = [];
                works.Add(r.GetString(1));
            }
        var entries = new List<CatalogRecord>();
        using (var cmd = Query(conn, tx, "SELECT entity_id,kind,zh,en,aliases,origin_entity_id,cover_hash FROM catalog_entries ORDER BY id"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) entries.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                DecodeAliases(r.GetString(4)), NullableText(r, 5), appearances.GetValueOrDefault(r.GetString(0), []).ToArray(), NullableText(r, 6)));
        var ids = new List<CatalogIdentity>();
        using (var cmd = Query(conn, tx, "SELECT entity_id,target_entity_id FROM catalog_entity_ids ORDER BY entity_id"))
        using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(new(r.GetString(0), r.GetString(1)));
        var pairs = new List<CatalogDistinct>();
        using (var cmd = Query(conn, tx, "SELECT entity_id_a,entity_id_b FROM catalog_distinct_pairs ORDER BY entity_id_a,entity_id_b"))
        using (var r = cmd.ExecuteReader()) while (r.Read()) pairs.Add(new(r.GetString(0), r.GetString(1)));
        var assets = new List<CatalogAsset>();
        using (var cmd = Query(conn, tx, "SELECT hash,image FROM catalog_assets ORDER BY hash"))
        using (var r = cmd.ExecuteReader()) while (r.Read()) assets.Add(new(r.GetString(0), (byte[])r.GetValue(1)));
        return new(LibraryArchiveFormat, 2, [.. entries], [.. ids], [.. pairs], [.. assets]);
    }

    internal CatalogDocument ExportCatalogDocument()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(deferred: true);
        return ReadCatalogDocument(conn, tx);
    }

    internal int WriteLibraryArchive(Stream output)
    {
        var document = ExportCatalogDocument();
        if (document.Entries.Length == 0) throw new InvalidOperationException("资料库里还没有可导出的内容");
        JsonSerializer.Serialize(output, document, CatalogJson);
        return document.Entries.Length;
    }

    internal static CatalogDocument ReadLibraryArchiveDocument(Stream input)
    {
        using var json = JsonDocument.Parse(input);
        CatalogDocument document;
        if (json.RootElement.TryGetProperty("version", out var version) && version.GetInt32() == 1)
        {
            var old = json.RootElement.Deserialize<LibraryArchive>(CatalogJson)
                ?? throw new InvalidDataException("资料库内容无效");
            if (old.Format != LibraryArchiveFormat || old.Entries is null || old.Entries.Any(e => e is null || e.Kind is not ("work" or "character") || string.IsNullOrWhiteSpace(e.Key))
                || old.Entries.DistinctBy(e => (e.Kind, e.Key)).Count() != old.Entries.Length)
                throw new InvalidDataException("旧版资料库内容无效");
            // Old readable work keys aren't global identities. Only the old local GUID is portable.
            var keys = old.Entries.ToDictionary(e => (e.Kind, e.Key), e => e.Kind == "character" && e.Key.StartsWith("local:") &&
                Guid.TryParse(e.Key[6..], out var id) ? id.ToString("D") : Guid.NewGuid().ToString("D"));
            string Work(string key) => keys.GetValueOrDefault(("work", key)) ?? throw new InvalidDataException("旧版资料库作品引用不存在");
            var assets = old.Entries.Where(e => e.Cover is not null).Select(e => new CatalogAsset(Sha256Hex(e.Cover!), e.Cover!)).DistinctBy(a => a.Hash).ToArray();
            document = new(LibraryArchiveFormat, 2, old.Entries.Select(e => new CatalogRecord(keys[(e.Kind, e.Key)], e.Kind,
                e.Zh, e.En, e.Aliases, e.Origin is null ? null : Work(e.Origin),
                e.Appearances?.Select(Work).ToArray()!, e.Cover is null ? null : Sha256Hex(e.Cover))).ToArray(), [], [], assets);
        }
        else document = json.RootElement.Deserialize<CatalogDocument>(CatalogJson) ?? throw new InvalidDataException("资料库内容无效");
        ValidateCatalogDocument(document);
        return document;
    }

    internal static void ValidateCatalogDocument(CatalogDocument d)
    {
        if (d.Format != LibraryArchiveFormat || d.Version != 2 || d.Entries is null || d.EntityIds is null || d.DistinctPairs is null || d.Assets is null
            || d.Entries.Any(e => e is null || !Guid.TryParseExact(e.EntityId, "D", out _) || e.EntityId != e.EntityId.ToLowerInvariant()
                || e.Kind is not ("work" or "character") || e.Zh is null || e.En is null || string.IsNullOrWhiteSpace(e.Zh) && string.IsNullOrWhiteSpace(e.En)
                || e.Aliases is null || e.Aliases.Any(a => a is null) || e.Appearances is null
                || e.Kind == "work" && (e.Origin is not null || e.Appearances.Length > 0))
            || d.Entries.DistinctBy(e => e.EntityId).Count() != d.Entries.Length
            || d.Assets.Any(a => a is null || a.Image is null || a.Hash != Sha256Hex(a.Image))
            || d.Assets.DistinctBy(a => a.Hash).Count() != d.Assets.Length)
            throw new InvalidDataException("资料库结构或实体编号无效");
        var works = d.Entries.Where(e => e.Kind == "work").Select(e => e.EntityId).ToHashSet();
        var entries = d.Entries.Select(e => e.EntityId).ToHashSet();
        var assets = d.Assets.Select(a => a.Hash).ToHashSet();
        if (d.Entries.Any(e => e.Origin is not null && !works.Contains(e.Origin) || e.Appearances.Any(w => !works.Contains(w))
                || e.CoverHash is not null && !assets.Contains(e.CoverHash))
            || d.EntityIds.Any(i => i is null || !Guid.TryParseExact(i.EntityId, "D", out _) || i.EntityId != i.EntityId.ToLowerInvariant()
                || !entries.Contains(i.TargetEntityId) || entries.Contains(i.EntityId) && i.EntityId != i.TargetEntityId)
            || d.EntityIds.DistinctBy(i => i.EntityId).Count() != d.EntityIds.Length)
            throw new InvalidDataException("资料库包含无效的身份或作品引用");
        var knownIds = entries.Concat(d.EntityIds.Select(i => i.EntityId)).ToHashSet();
        if (d.DistinctPairs.Any(p => p is null || !knownIds.Contains(p.EntityIdA) || !knownIds.Contains(p.EntityIdB)
            || string.CompareOrdinal(p.EntityIdA, p.EntityIdB) >= 0)) throw new InvalidDataException("资料库身份区分记录无效");
        foreach (var a in d.Assets)
            if (a.Image.Length is < 3 or > 8 * 1024 * 1024 || a.Image[0] != 0xFF || a.Image[1] != 0xD8 || a.Image[2] != 0xFF
                || Image.Identify(a.Image) is not { Width: > 0 and <= 1600, Height: > 0 and <= 1600 })
                throw new InvalidDataException("资料库背景图无效");
    }

    private static bool SameRecord(CatalogRecord a, CatalogRecord b)
        => a.Kind == b.Kind && a.Zh == b.Zh && a.En == b.En && a.Origin == b.Origin && a.CoverHash == b.CoverHash
            && a.Aliases.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Aliases)
            && a.Appearances.ToHashSet().SetEquals(b.Appearances);

    private static CatalogRecord MergeRecord(CatalogRecord current, CatalogRecord incoming, bool preferIncoming)
    {
        var first = preferIncoming ? incoming : current;
        var second = preferIncoming ? current : incoming;
        var zh = first.Zh.Length > 0 ? first.Zh : second.Zh;
        var en = first.En.Length > 0 ? first.En : second.En;
        var origin = first.Origin ?? second.Origin;
        return current with { Zh = zh, En = en, Origin = origin, CoverHash = first.CoverHash ?? second.CoverHash,
            Aliases = CanonicalAliases(first.Aliases.Concat(second.Aliases), zh, en).ToArray(),
            Appearances = current.Appearances.Concat(incoming.Appearances).Concat(origin is null ? [] : new[] { origin }).Distinct().ToArray() };
    }

    // Immutable assets are already identified by the content hashes referenced in entries.
    private static string CatalogStamp(CatalogDocument d) => Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(d with { Assets = [] }, CatalogJson));

    public async Task<int> ImportCatalogDocumentAsync(CatalogDocument incoming,
        Func<CatalogImportBatch, Task<bool>>? review = null)
    {
        ValidateCatalogDocument(incoming);
        var before = ExportCatalogDocument();
        var records = before.Entries.ToDictionary(e => e.EntityId);
        var aliases = before.EntityIds.ToDictionary(i => i.EntityId, i => i.TargetEntityId);
        foreach (var e in before.Entries) aliases[e.EntityId] = e.EntityId;
        var incomingAliases = incoming.Entries.ToDictionary(e => e.EntityId, e => incoming.EntityIds.Where(i => i.TargetEntityId == e.EntityId)
            .Select(i => i.EntityId).Append(e.EntityId).Distinct().ToArray());
        var mapping = new Dictionary<string, string>();
        var distinct = new HashSet<(string, string)>();
        var distinctById = new Dictionary<string, HashSet<string>>();
        void RememberDifferent(string a, string b)
        {
            distinct.Add(string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a));
            if (!distinctById.TryGetValue(a, out var left)) distinctById[a] = left = [];
            if (!distinctById.TryGetValue(b, out var right)) distinctById[b] = right = [];
            left.Add(b);
            right.Add(a);
        }
        foreach (var pair in before.DistinctPairs.Concat(incoming.DistinctPairs))
            RememberDifferent(pair.EntityIdA, pair.EntityIdB);
        bool Different(string incomingId, string candidateId) => incomingAliases[incomingId].Any(a =>
            distinctById.TryGetValue(a, out var others) && others.Any(b => aliases.GetValueOrDefault(b) == candidateId));
        var images = before.Assets.Concat(incoming.Assets).DistinctBy(a => a.Hash).ToDictionary(a => a.Hash, a => a.Image);
        var changed = new HashSet<string>();
        var touched = new HashSet<string>();
        foreach (var kind in new[] { "work", "character" })
        {
            var choices = new List<CatalogImportChoice>();
            foreach (var raw in incoming.Entries.Where(e => e.Kind == kind))
            {
                var e = raw with { Origin = raw.Origin is null ? null : mapping[raw.Origin], Appearances = raw.Appearances.Select(w => mapping[w]).Distinct().ToArray() };
                var known = incomingAliases[e.EntityId].Where(aliases.ContainsKey).Select(i => aliases[i]).Distinct().ToArray();
                if (known.Length > 1) throw new InvalidDataException("这份资料将本库多个对象标记为同一实体，当前无法安全合并，未写入资料");
                if (known.Length == 1)
                {
                    var old = records[known[0]];
                    if (old.Kind != kind) throw new InvalidDataException("实体编号对应的资料类型不一致");
                    if (SameRecord(old, e))
                    {
                        mapping[e.EntityId] = old.EntityId;
                        foreach (var id in incomingAliases[e.EntityId]) aliases[id] = old.EntityId;
                        continue;
                    }
                    choices.Add(new(e, [old], true));
                    continue;
                }
                var candidates = records.Values.Where(c => c.Kind == kind && (kind == "work" || c.Origin == e.Origin)
                    && !Different(e.EntityId, c.EntityId)).ToArray();
                var english = Norm(e.En);
                var matches = english.Length == 0 ? [] : candidates.Where(c => Norm(c.En) == english).ToArray();
                if (matches.Length == 0 && Norm(e.Zh) is { Length: > 0 } chinese) matches = candidates.Where(c => Norm(c.Zh) == chinese).ToArray();
                if (matches.Length > 0) choices.Add(new(e, matches, false));
                else
                {
                    records[e.EntityId] = e;
                    mapping[e.EntityId] = e.EntityId;
                    foreach (var id in incomingAliases[e.EntityId]) aliases[id] = e.EntityId;
                    changed.Add(e.EntityId); touched.Add(e.EntityId);
                }
            }
            if (choices.Count > 0)
            {
                var batch = new CatalogImportBatch(kind, [.. choices], records.Values.Where(e => e.Kind == "work")
                    .ToDictionary(e => e.EntityId, e => e.Name), images);
                if (review is null) throw new InvalidOperationException("资料存在重复候选或内容差异，需要确认合并方式");
                if (!await review(batch)) return 0;
                foreach (var choice in choices)
                {
                    var e = choice.Incoming;
                    var target = choice.TargetEntityId;
                    if (target is null && choice.SameIdentity || target is not null && !choice.Candidates.Any(c => c.EntityId == target))
                        throw new InvalidOperationException("无效的资料合并选择");
                    if (target is null)
                    {
                        foreach (var c in choice.Candidates) RememberDifferent(e.EntityId, c.EntityId);
                        target = e.EntityId;
                        records[target] = e;
                    }
                    else records[target] = MergeRecord(records[target], e, choice.PreferIncoming);
                    mapping[e.EntityId] = target;
                    foreach (var id in incomingAliases[e.EntityId]) aliases[id] = target;
                    changed.Add(target); touched.Add(target);
                }
            }
        }
        // A portable "different" decision must not contradict a confirmed identity merge.
        var finalPairs = distinct.Where(p => aliases.ContainsKey(p.Item1) && aliases.ContainsKey(p.Item2)
            && aliases[p.Item1] != aliases[p.Item2]).Select(p => new CatalogDistinct(p.Item1, p.Item2)).ToArray();
        var result = new CatalogDocument(LibraryArchiveFormat, 2, records.Values.ToArray(),
            aliases.Select(i => new CatalogIdentity(i.Key, i.Value)).ToArray(), finalPairs,
            images.Where(i => records.Values.Any(e => e.CoverHash == i.Key)).Select(i => new CatalogAsset(i.Key, i.Value)).ToArray());
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        if (CatalogStamp(ReadCatalogDocument(conn, tx)) != CatalogStamp(before))
            throw new InvalidOperationException("确认期间资料库已变化，请重新导入");
        foreach (var record in result.Entries.Where(e => changed.Contains(e.EntityId)).OrderBy(e => e.Kind == "work" ? 0 : 1))
            WriteCatalogRecord(conn, tx, record, images);
        foreach (var id in result.EntityIds)
            Exec(conn, tx, "INSERT INTO catalog_entity_ids(entity_id,target_entity_id) VALUES($id,$target) ON CONFLICT(entity_id) DO UPDATE SET target_entity_id=excluded.target_entity_id",
                ("$id", id.EntityId), ("$target", id.TargetEntityId));
        Exec(conn, tx, "DELETE FROM catalog_distinct_pairs");
        foreach (var p in result.DistinctPairs)
            Exec(conn, tx, "INSERT OR IGNORE INTO catalog_distinct_pairs VALUES($a,$b)", ("$a", p.EntityIdA), ("$b", p.EntityIdB));
        CollectCatalogAssets(conn, tx);
        ApplyPersonalCatalogTags(conn, tx);
        tx.Commit();
        return touched.Count;
    }

    private static void WriteCatalogRecord(SqliteConnection conn, SqliteTransaction tx, CatalogRecord record, IReadOnlyDictionary<string, byte[]> images)
    {
        using var find = Query(conn, tx, "SELECT legacy_key,created FROM catalog_entries WHERE entity_id=$id", ("$id", record.EntityId));
        using var row = find.ExecuteReader();
        var exists = row.Read();
        var key = exists ? row.GetString(0) : record.EntityId;
        var created = !exists || row.GetBoolean(1);
        row.Close();
        string? origin = null;
        if (record.Origin is { } originId)
        {
            using var work = Query(conn, tx, "SELECT legacy_key FROM catalog_entries WHERE entity_id=$id AND kind='work'", ("$id", originId));
            origin = work.ExecuteScalar() as string ?? throw new InvalidDataException("原作尚未导入");
        }
        // The accepted document already contains final values and UUID relationships. Mirror the old
        // personal adapter without rebuilding all legacy origin views for every imported character.
        SavePersonalEntry(conn, tx, new(key, record.Kind, created, record.Zh, record.En, record.Aliases, origin), synchronize: false);
        Exec(conn, tx, exists ? """
            UPDATE catalog_entries SET zh=$zh,en=$en,aliases=$aliases,origin_entity_id=$origin,edited=1 WHERE entity_id=$id
            """ : """
            INSERT INTO catalog_entries(entity_id,kind,legacy_key,zh,en,aliases,origin_entity_id,created,edited)
            VALUES($id,$kind,$key,$zh,$en,$aliases,$origin,1,1)
            """, ("$id", record.EntityId), ("$kind", record.Kind), ("$key", key), ("$zh", record.Zh), ("$en", record.En),
            ("$aliases", JsonSerializer.Serialize(record.Aliases)), ("$origin", (object?)record.Origin ?? DBNull.Value));
        Exec(conn, tx, "INSERT OR IGNORE INTO catalog_entity_ids VALUES($id,$id)", ("$id", record.EntityId));
        var entry = exists ? RequireEntry(conn, tx, record.Kind, key) : new LibraryEntry(key,
            record.Kind == "work" ? "work:" + key : key, record.Kind == "work" ? "work:" + key : key,
            record.Kind, record.Zh, record.En, record.Aliases, record.Kind == "work" ? key : origin ?? "", true, true);
        EnsureIdentity(conn, tx, entry);
        if (record.Kind == "character")
        {
            foreach (var workId in record.Appearances.Append(record.Origin ?? "").Where(w => w.Length > 0).Distinct())
            {
                using var work = Query(conn, tx, "SELECT legacy_key FROM catalog_entries WHERE entity_id=$id AND kind='work'", ("$id", workId));
                SaveAppearance(conn, tx, new(key, work.ExecuteScalar() as string ?? throw new InvalidDataException("登场作品尚未导入"), true));
            }
        }
        SetCatalogAsset(conn, tx, record.Kind, key, record.CoverHash is null ? null : images[record.CoverHash]);
    }
}
