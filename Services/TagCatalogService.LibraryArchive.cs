using System.Text.Json;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;

namespace Gallery.Services;

public sealed partial class TagCatalogService
{
    private const string LibraryArchiveFormat = "gallery-catalog";
    private sealed record ArchiveEntry(string Kind, string Key, string Zh, string En, string[] Aliases,
        string? Origin, string[] Appearances, byte[]? Cover);
    private sealed record LibraryArchive(string Format, int Version, ArchiveEntry[] Entries);

    internal int WriteLibraryArchive(Stream output)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(deferred: true);
        var appearances = ReadAppearances(conn, tx);
        var covers = new Dictionary<(string, string), byte[]>();
        using (var cmd = Query(conn, tx, "SELECT kind,key,image FROM catalog_covers"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) covers[(reader.GetString(0), reader.GetString(1))] = (byte[])reader.GetValue(2);
        var records = ReadEntries(conn, tx, "work").Concat(ReadEntries(conn, tx, "character"))
            .Select(e => new ArchiveEntry(e.Kind, e.Key, e.Zh, e.En, e.Aliases,
                e.Origin.Length == 0 || e.Kind == "work" ? null : e.Origin,
                e.Kind == "character" ? appearances[e.Key].ToArray() : [],
                covers.GetValueOrDefault((e.Kind, e.Key)))).ToArray();
        if (records.Length == 0) throw new InvalidOperationException("资料库里还没有可导出的内容");
        JsonSerializer.Serialize(output, new LibraryArchive(LibraryArchiveFormat, 1, records), CatalogJson);
        return records.Length;
    }

    internal int ReadLibraryArchive(Stream input, bool? overwrite)
    {
        var archive = JsonSerializer.Deserialize<LibraryArchive>(input, CatalogJson);
        if (archive is not { Format: LibraryArchiveFormat, Version: 1, Entries: { } entries } ||
            entries.Any(e => e is null || e.Kind is not ("work" or "character") || string.IsNullOrWhiteSpace(e.Key) ||
                e.Zh is null || e.En is null || (string.IsNullOrWhiteSpace(e.Zh) && string.IsNullOrWhiteSpace(e.En)) ||
                e.Aliases is null || e.Aliases.Any(a => a is null) || e.Appearances is null ||
                e.Kind == "work" && (e.Origin is not null || e.Appearances.Length > 0)) ||
            entries.DistinctBy(e => (e.Kind, e.Key)).Count() != entries.Length)
            throw new InvalidDataException("资料库压缩包内容无效");

        var works = entries.Where(e => e.Kind == "work").Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Origin is { } origin && !works.Contains(origin) || entry.Appearances.Any(w => !works.Contains(w)))
                throw new InvalidDataException("资料库压缩包包含无效的作品关系");
            if (entry.Cover is { } cover && (cover.Length > 8 * 1024 * 1024 || cover.Length < 3 ||
                cover[0] != 0xFF || cover[1] != 0xD8 || cover[2] != 0xFF ||
                Image.Identify(cover) is not { Width: > 0 and <= 1600, Height: > 0 and <= 1600 }))
                throw new InvalidDataException("资料库压缩包包含无效的背景图片");
        }

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var existing = ReadEntries(conn, tx, "work").Concat(ReadEntries(conn, tx, "character"))
            .ToDictionary(e => (e.Kind, e.Key));
        var deleted = new HashSet<(string Kind, string Key)>();
        using (var cmd = Query(conn, tx, "SELECT kind,key FROM catalog_deleted_entries"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) deleted.Add((reader.GetString(0), reader.GetString(1)));

        if (overwrite is null && entries.Any(e => existing.ContainsKey((e.Kind, e.Key)) || deleted.Contains((e.Kind, e.Key))))
            throw new InvalidOperationException("压缩包包含已有或已删除的同编号资料，重导处理方式尚待确认");

        int imported = 0;
        foreach (var entry in entries.OrderBy(e => e.Kind == "work" ? 0 : 1))
        {
            var id = (entry.Kind, entry.Key);
            var old = existing.GetValueOrDefault(id);
            var wasDeleted = deleted.Contains(id);
            if ((old is not null || wasDeleted) && overwrite != true) continue;
            if (wasDeleted)
                Exec(conn, tx, "DELETE FROM catalog_deleted_entries WHERE kind=$kind AND key=$k",
                    ("$kind", entry.Kind), ("$k", entry.Key));
            SavePersonalEntry(conn, tx, new(entry.Key, entry.Kind, old?.Created ?? true, entry.Zh, entry.En,
                entry.Aliases, entry.Origin));
            EnsureIdentity(conn, tx, RequireEntry(conn, tx, entry.Kind, entry.Key));
            if (entry.Kind == "character")
            {
                var incomingWorks = entry.Appearances.Append(entry.Origin ?? "").Where(works.Contains).ToHashSet();
                foreach (var work in EffectiveWorks(conn, entry.Key, tx))
                    if (!incomingWorks.Contains(work)) SaveAppearance(conn, tx, new(entry.Key, work, false));
                foreach (var work in incomingWorks) SaveAppearance(conn, tx, new(entry.Key, work, true));
            }
            WriteLibraryCover(conn, tx, entry.Kind, entry.Key, entry.Cover);
            imported++;
        }
        ApplyPersonalCatalogTags(conn, tx);
        tx.Commit();
        return imported;
    }
}
