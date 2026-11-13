using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using Windows.Storage;

namespace Gallery.Services;

/// <summary>设置页「识别模型 / 资料库」列表里的一行。</summary>
public sealed record PackEntry(string Path, string Name, string Detail, bool IsModel, bool IsActive);

/// <summary>
/// 资料包的落盘与清点：
///   资料库包拷进 <c>archive\&lt;包名&gt;\</c>（可并存多个，新导的追加）；
///   识别模型拷进 <c>models\&lt;包名&gt;\</c>（单一式，已有模型时拒绝第二个）。
/// 支持拖入 ZIP／7z／RAR、含 json/model.onnx 的文件夹、单个 json 文件。
/// </summary>
public static class PackLibrary
{
    public sealed record ImportReport(int Catalog, int Model, List<string> Errors)
    {
        public bool Any => Catalog > 0 || Model > 0;
    }

    /// <summary>列出已导入的条目：模型在前（单一），资料库按名称排。名称用作品中文名，不露内部 ID。</summary>
    public static List<PackEntry> List()
    {
        var list = new List<PackEntry>();
        var activeModel = AppService.Settings.Settings.nsfw_model_dir;
        foreach (var dir in PackFolders.ModelPacks())
        {
            var name = System.IO.Path.GetFileName(dir);
            var onnx = System.IO.Path.Combine(dir, "model.onnx");
            var size = File.Exists(onnx) ? FormatSize(new FileInfo(onnx).Length) : "";
            var active = string.Equals(dir, activeModel, StringComparison.OrdinalIgnoreCase);
            var detail = active ? (size.Length > 0 ? size + " · 使用中" : "使用中") : size;
            list.Add(new PackEntry(dir, name, detail, true, active));
        }
        foreach (var dir in PackFolders.ArchivePacks())
        {
            var (title, count) = DescribeCatalogPack(dir);
            list.Add(new PackEntry(dir, title, $"{count} 条", false, false));
        }
        return list;
    }

    private static readonly SemaphoreSlim MutationGate = new(1, 1);

    public static bool HasModel => PackFolders.ModelPacks().Count > 0;

    public static async Task RemoveAsync(PackEntry entry)
    {
        await MutationGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                var path = Path.GetFullPath(entry.Path).TrimEnd(Path.DirectorySeparatorChar);
                var expected = Path.GetFullPath(entry.IsModel ? PackFolders.Models : PackFolders.Archive)
                    .TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(path), expected, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("只能移除已导入的资料包目录");
                if (entry.IsModel)
                {
                    PixaiScorer.RefreshCache(null);
                    Directory.Delete(path, recursive: true);
                    if (string.Equals(AppService.Settings.Settings.nsfw_model_dir, path, StringComparison.OrdinalIgnoreCase))
                    {
                        AppService.Settings.Settings.nsfw_model_dir = "";
                        AppService.Settings.Save();
                    }
                }
                else
                {
                    var catalog = AppService.Catalog ?? throw new InvalidOperationException("资料库尚未就绪");
                    catalog.RemovePack(path);
                }
                AppService.NotifyPackRemoved();
            });
        }
        finally { MutationGate.Release(); }
    }

    /// <summary>导出报告：打进压缩包的包数、文件数、记录数，以及被跳过的项。</summary>
    public sealed record ExportReport(int Packs, int Files, int Records, List<string> Warnings);

    /// <summary>压缩包根目录的元信息文件名；不是资料库包，导入时按普通文件忽略。</summary>
    public const string ExportManifestFile = "gallery-export.json";

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 把整个资料库打包成一个 zip：每个已导入包一个同名子目录（放全部资料库 JSON 原件），
    /// 每包一份合并后的 catalog_relations.json，根目录一份元信息。
    /// 只含公共资料；个人规则与识别模型不在其中（导入侧也不接收模型）。
    /// </summary>
    public static async Task<ExportReport> ExportAsync(string destination)
    {
        await MutationGate.WaitAsync();
        try { return await Task.Run(() => Export(destination)); }
        finally { MutationGate.Release(); }
    }

    private static ExportReport Export(string destination)
    {
        var packs = PackFolders.ArchivePacks();
        if (packs.Count == 0) throw new InvalidOperationException("资料库里还没有可导出的内容");

        var warnings = new List<string>();
        var relations = MergedRelations(packs, warnings);

        // 先收集全部字节再落盘：中途发现没有有效文件时不留半个压缩包。
        var planned = new List<(string Dir, List<(string File, byte[] Bytes, int Records)> Files)>();
        foreach (var dir in packs)
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, ExportManifestFile, StringComparison.OrdinalIgnoreCase))
                name += "-pack";
            var files = new List<(string File, byte[] Bytes, int Records)>();
            foreach (var json in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                var file = Path.GetFileName(json);
                if (IsRelationsFile(file)) continue;
                try
                {
                    TagCatalogService.ValidatePackFile(json);
                    var bytes = File.ReadAllBytes(json);
                    files.Add((file, bytes, RecordCount(bytes)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                                               or KeyNotFoundException or InvalidOperationException)
                {
                    warnings.Add($"{name}/{file}：{ex.Message}");
                }
            }
            if (files.Count == 0)
            {
                warnings.Add($"{name}：没有可导出的资料文件，已跳过");
                continue;
            }
            planned.Add((name, files));
        }
        if (planned.Count == 0) throw new InvalidOperationException("资料库里没有可导出的有效资料文件");

        var records = planned.Sum(p => p.Files.Sum(f => f.Records));
        var fileCount = planned.Sum(p => p.Files.Count);
        using (var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, files) in planned)
            {
                foreach (var (file, bytes, _) in files) WriteEntry(zip, $"{name}/{file}", bytes);
                if (relations is not null)
                    WriteEntry(zip, $"{name}/{TagCatalogService.PublicRelationsFile}", relations);
            }
            WriteEntry(zip, ExportManifestFile, ManifestBytes(planned, warnings.Count));
        }
        return new ExportReport(planned.Count, fileCount, records, warnings);
    }

    private static bool IsRelationsFile(string file) =>
        string.Equals(file, TagCatalogService.PublicRelationsFile, StringComparison.OrdinalIgnoreCase)
        || string.Equals(file, "tag_identity.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>合并各包的实体关系：按来源实体键去重，先出现的优先（与 LoadIdentityPlan 口径一致）。</summary>
    private static byte[]? MergedRelations(IEnumerable<string> packDirs, List<string> warnings)
    {
        var links = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in packDirs)
        {
            var path = Path.Combine(dir, TagCatalogService.PublicRelationsFile);
            if (!File.Exists(path)) path = Path.Combine(dir, "tag_identity.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(TagCatalogService.PublicRelationsBytes(path));
                foreach (var link in doc.RootElement.GetProperty("identity_links").EnumerateArray())
                {
                    var member = link.TryGetProperty("member", out var key) ? key.GetString() ?? "" : "";
                    if (member.Length == 0 || !seen.Add(member)) continue;
                    links.Add(link.Clone());
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException)
            {
                warnings.Add($"{Path.GetFileName(dir)}：实体关系文件无效，已跳过（{ex.Message}）");
            }
        }
        if (links.Count == 0) return null;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "gallery-catalog-relations",
            version = 1,
            identity_links = links,
        }, ExportJson);
    }

    private static int RecordCount(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("records", out var records)
                && records.ValueKind == JsonValueKind.Array ? records.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }

    private static byte[] ManifestBytes(
        List<(string Dir, List<(string File, byte[] Bytes, int Records)> Files)> packs, int skipped)
    {
        var payload = new
        {
            format = "gallery-library-export",
            version = 1,
            exported_at = DateTimeOffset.Now.ToString("O"),
            packs = packs.Select(p => new
            {
                name = p.Dir,
                files = p.Files.Count,
                records = p.Files.Sum(f => f.Records),
            }).ToList(),
            skipped,
            note = "只含公共资料库；个人规则走「个人规则备份」，识别模型需单独导入。",
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, ExportJson);
    }

    private static void WriteEntry(ZipArchive zip, string key, byte[] bytes)
    {
        var entry = zip.CreateEntry(key, CompressionLevel.Optimal);
        using var target = entry.Open();
        target.Write(bytes);
    }

    // isModel 为 null 仅供已有无窗口验证入口自动识别；设置页必须指定目标类型。
    public static async Task<ImportReport> ImportAsync(IReadOnlyList<IStorageItem> items, bool? isModel = null)
    {
        var paths = items.Select(i => i.Path).ToArray();
        await MutationGate.WaitAsync();
        try { return await Task.Run(() => Import(paths, isModel)); }
        finally { MutationGate.Release(); }
    }

    private sealed record Candidate(string Source, string Name, bool IsModel, string[] Jsons, string? Relations = null);

    private static ImportReport Import(string[] paths, bool? isModel)
    {
        var errors = new List<string>();
        var candidates = new List<Candidate>();
        var catalog = 0;
        var model = 0;
        var staging = Path.Combine(PackFolders.Root, ".staging", "pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var path in paths)
            {
                try
                {
                    var name = Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
                    var source = path;
                    if (!Directory.Exists(path))
                    {
                        var extension = Path.GetExtension(path).ToLowerInvariant();
                        if (extension == ".json")
                        {
                            if (!IsBundle(path)) throw new InvalidDataException("不是有效的资料库 JSON");
                            candidates.Add(new Candidate(Path.GetDirectoryName(path)!, name, false, [path]));
                            continue;
                        }
                        if (extension is not (".zip" or ".7z" or ".rar"))
                            throw new InvalidDataException("请选择 ZIP、7z、RAR、资料库 JSON 或文件夹");
                        source = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                        Extract(path, source);
                    }
                    var found = Discover(source, name).ToList();
                    if (found.Count == 0) throw new InvalidDataException("未找到有效资料库或 PixAI 模型（model.onnx + config.json）");
                    candidates.AddRange(found);
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
            }

            // 整批预检，多模型一次也不接收，不能收下第一个再报错。
            if (candidates.Count(c => c.IsModel) > 1)
                return new(0, 0, [.. errors, "一次只能导入一个模型，请只保留一个模型包"]);
            if (isModel is not null && candidates.Any(c => c.IsModel != isModel))
                return new(0, 0, [.. errors, isModel.Value ? "模型区域只接受识别模型" : "资料库区域只接受资料库包"]);
            if (candidates.Any(c => c.IsModel) && HasModel)
                return new(0, 0, [.. errors, "识别模型只能保留一个，请先移除现有模型"]);
            if (isModel == true && (paths.Length != 1 || errors.Count > 0))
                return new(0, 0, [.. errors, "一次只能导入一个完整模型包"]);

            PackFolders.EnsureDirs();
            foreach (var candidate in candidates)
            {
                try
                {
                    if (!candidate.IsModel && IsAlreadyImported(candidate.Jsons, candidate.Relations)) continue;
                    var pending = Path.Combine(staging, "ready-" + Guid.NewGuid().ToString("N"));
                    if (candidate.IsModel) CopyDirectory(candidate.Source, pending);
                    else
                    {
                        Directory.CreateDirectory(pending);
                        foreach (var json in candidate.Jsons)
                            File.Copy(json, Path.Combine(pending, Path.GetFileName(json)));
                        if (candidate.Relations is not null)
                            File.WriteAllBytes(Path.Combine(pending, TagCatalogService.PublicRelationsFile),
                                TagCatalogService.PublicRelationsBytes(candidate.Relations));
                    }
                    // 先在同盘暂存完整副本，原子落位；监听器只能看见完整包。
                    var target = UniqueDir(candidate.IsModel ? PackFolders.Models : PackFolders.Archive, candidate.Name);
                    Directory.Move(pending, target);
                    if (candidate.IsModel) model++; else catalog++;
                }
                catch (Exception ex) { errors.Add($"{candidate.Name}：{ex.Message}"); }
            }
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        finally { TryDelete(staging); }
        return new(catalog, model, errors);
    }

    private static IEnumerable<Candidate> Discover(string directory, string name)
    {
        if ((File.GetAttributes(directory) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("资料包不能包含链接目录");
        if (File.Exists(Path.Combine(directory, "model.onnx")))
        {
            if (!PackFolders.LooksUsable(directory)) throw new InvalidDataException("PixAI 模型配置无效");
            yield return new Candidate(directory, name, true, []);
            yield break;
        }
        var jsons = Directory.GetFiles(directory, "*.json").Where(IsBundle).ToArray();
        if (jsons.Length > 0)
        {
            var relations = Path.Combine(directory, TagCatalogService.PublicRelationsFile);
            if (!File.Exists(relations)) relations = Path.Combine(directory, "tag_identity.json");
            if (!File.Exists(relations)) relations = null;
            if (relations is not null) _ = TagCatalogService.PublicRelationsBytes(relations);
            yield return new Candidate(directory, name, false, jsons, relations);
        }
        foreach (var sub in Directory.GetDirectories(directory))
        {
            if (Path.GetFileName(sub) is "__MACOSX" or ".staging") continue;
            foreach (var candidate in Discover(sub, Path.GetFileName(sub))) yield return candidate;
        }
    }

    private static void Extract(string path, string destination)
    {
        Directory.CreateDirectory(destination);
        var prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = SharpCompress.Archives.ArchiveFactory.Open(path);
        void WriteEntry(SharpCompress.Common.IEntry entry, Action<Stream> copy)
        {
            if (entry.IsEncrypted) throw new InvalidDataException("请先解密压缩包后再导入");
            if (entry.LinkTarget is not null) throw new InvalidDataException("压缩包不能包含链接");
            var key = entry.Key ?? throw new InvalidDataException("压缩包条目缺少路径");
            var target = Path.GetFullPath(Path.Combine(destination, key));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || key.Contains(':')) throw new InvalidDataException("压缩包含越界路径");
            if (entry.IsDirectory) { Directory.CreateDirectory(target); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var stream = new FileStream(target, FileMode.CreateNew);
            copy(stream);
        }
        if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry()) WriteEntry(reader.Entry, reader.WriteEntryTo);
        }
        else
        {
            foreach (var entry in archive.Entries)
                WriteEntry(entry, target => { using var input = entry.OpenEntryStream(); input.CopyTo(target); });
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if (Path.GetFullPath(target).StartsWith(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不能把模型目录复制进它自身，请选择独立的模型包");
        if ((File.GetAttributes(source) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("模型包不能包含链接目录");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & System.IO.FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("模型包不能包含链接文件");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static bool IsAlreadyImported(string[] jsons, string? relations)
    {
        var imported = PackFolders.ArchivePacks().SelectMany(d => Directory.GetFiles(d, "*.json"))
            .Select(Digest).ToHashSet(StringComparer.Ordinal);
        return jsons.All(j => imported.Contains(Digest(j))) && (relations is null || imported.Contains(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TagCatalogService.PublicRelationsBytes(relations)))));
    }

    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string UniqueDir(string root, string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0 || name is "." or "..") name = "pack";
        var dir = Path.Combine(root, name);
        var i = 2;
        while (Directory.Exists(dir) || File.Exists(dir)) dir = Path.Combine(root, $"{name} ({i++})");
        return dir;
    }

    private static bool IsBundle(string path)
    {
        try { TagCatalogService.ValidatePackFile(path); return true; }
        catch { return false; }
    }

    /// <summary>
    /// 资料库条目的显示名与记录数：优先用作品中文名，取不到才退回目录名。
    /// 一个包里塞了多份来源时（例如整包下载），显示成「首份 等 N 个来源」并汇总条数，
    /// 免得只有第一份的名字看起来像是整包的内容。
    /// </summary>
    private static (string Title, int Count) DescribeCatalogPack(string dir)
    {
        try
        {
            var bundles = Directory.GetFiles(dir, "*.json").Where(IsBundle).ToList();
            var total = 0;
            var firstTitle = "";
            foreach (var json in bundles)
            {
                using var stream = File.OpenRead(json);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                total += root.GetProperty("records").GetArrayLength();
                if (firstTitle.Length > 0) continue;
                if (root.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
                {
                    foreach (var game in games.EnumerateArray())
                    {
                        if (game.TryGetProperty("zh", out var zh) && (zh.GetString() ?? "").Length > 0)
                        {
                            firstTitle = zh.GetString()!;
                            break;
                        }
                    }
                }
                if (firstTitle.Length == 0 && root.GetProperty("source").TryGetProperty("id", out var idEl))
                    firstTitle = idEl.GetString() ?? "";
            }
            if (firstTitle.Length == 0) firstTitle = System.IO.Path.GetFileName(dir);
            var title = bundles.Count > 1 ? $"{firstTitle} 等 {bundles.Count} 个来源" : firstTitle;
            return (title, total);
        }
        catch
        {
            // 坏文件按目录名处理
        }
        return (System.IO.Path.GetFileName(dir), 0);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 临时目录清不掉不影响结果 */ }
    }
}
