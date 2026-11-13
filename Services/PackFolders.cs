namespace Gallery.Services;

/// <summary>
/// 可热插拔的资料包目录：位于当前用户独立数据目录。
///   <c>archive\&lt;包名&gt;\*.json</c> — 资料库包，可并存多个
///   <c>models\&lt;包名&gt;\</c> — 识别模型，需要 model.onnx + config.json，同时只保留一个
/// 用户在设置页拖入压缩包或文件夹即可导入，不需要重新打包程序。
/// </summary>
public static class PackFolders
{
    public static string Root => Path.Combine(AppPaths.LocalDataDir, "packs");
    public static string Archive => Path.Combine(Root, "archive");
    public static string Models => Path.Combine(Root, "models");
    public static string LegacyCatalog => Path.Combine(Root, "data", "tag_catalog");
    public static string MigrationWarning { get; private set; } = "";

    public static void MigrateLegacy()
    {
        MigrationWarning = "";
#if GALLERY_DEVELOPMENT
        if (AppPaths.Sandbox is not null) return;
#endif
        var sources = new List<string> { AppContext.BaseDirectory };
#if GALLERY_DEVELOPMENT
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gallery.csproj"))) { sources.Add(dir.FullName); break; }
#endif
        var configuredModel = AppService.Settings.Settings.nsfw_model_dir;
        if (configuredModel.Length > 0 && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(configuredModel)) is { } modelParent &&
            string.Equals(Path.GetFileName(modelParent), "models", StringComparison.OrdinalIgnoreCase))
            sources.Add(Path.GetDirectoryName(modelParent)!);
        MigrateLegacyFrom(sources);
    }

    internal static void MigrateLegacyFrom(IEnumerable<string> sources)
    {
        foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) continue;
            var marker = Path.Combine(Root, "migration-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))) + ".done");
            if (File.Exists(marker)) continue;
            var conflicts = new List<string>();
            try
            {
                foreach (var kind in new[] { "archive", "models", Path.Combine("data", "tag_catalog") })
                {
                    var old = Path.Combine(source, kind);
                    if (!Directory.Exists(old)) continue;
                    var target = Path.Combine(Root, kind);
                    // Every package is staged separately. A conflicting package is never partially overwritten.
                    foreach (var entry in Directory.EnumerateFileSystemEntries(old))
                    {
                        var destination = Path.Combine(target, Path.GetFileName(entry));
                        var files = Directory.Exists(entry) ? Directory.GetFiles(entry, "*", SearchOption.AllDirectories) : [entry];
                        bool equal(string original, string copy) => File.Exists(copy) &&
                            new FileInfo(original).Length == new FileInfo(copy).Length &&
                            Hash(original).SequenceEqual(Hash(copy));
                        string mapped(string file, string root) => Directory.Exists(entry)
                            ? Path.Combine(root, Path.GetRelativePath(entry, file)) : root;
                        if (File.Exists(destination) || Directory.Exists(destination))
                        {
                            if (files.Any(f => !equal(f, mapped(f, destination))) ||
                                (Directory.Exists(destination) && Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length != files.Length))
                                conflicts.Add(Path.GetFileName(entry));
                            continue;
                        }
                        Directory.CreateDirectory(target);
                        var stage = destination + ".migration-" + Guid.NewGuid().ToString("N");
                        try
                        {
                            foreach (var file in files)
                            {
                                var copy = mapped(file, stage);
                                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                                File.Copy(file, copy);
                                if (!equal(file, copy)) throw new IOException("Migration verification failed");
                            }
                            if (Directory.Exists(entry))
                            {
                                Directory.CreateDirectory(stage);
                                Directory.Move(stage, destination);
                            }
                            else File.Move(stage, destination);
                        }
                        finally
                        {
                            if (Directory.Exists(stage)) Directory.Delete(stage, true);
                            else if (File.Exists(stage)) File.Delete(stage);
                        }
                    }
                }
                Directory.CreateDirectory(Root);
                if (conflicts.Count > 0) MigrationWarning = "部分旧资料与现有资料同名且内容不同，已保留双方内容。资料包自动加载已暂停，请确认后重新导入并重启。";
                var model = AppService.Settings.Settings.nsfw_model_dir;
                if (model.Length > 0 && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(model)) is { } parent &&
                    string.Equals(Path.GetFullPath(parent), Path.GetFullPath(Path.Combine(source, "models")), StringComparison.OrdinalIgnoreCase))
                {
                    var migrated = Path.Combine(Models, Path.GetFileName(Path.TrimEndingDirectorySeparator(model)));
                    if (Directory.Exists(migrated) && !conflicts.Contains(Path.GetFileName(migrated)))
                    { AppService.Settings.Settings.nsfw_model_dir = migrated; AppService.Settings.Save(); }
                }
                if (conflicts.Count == 0) File.WriteAllText(marker, "1");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MigrationWarning = "旧资料迁移未完成，原文件已保留。资料包自动加载已暂停，请检查存储空间和访问权限后重启。";
                FaultLog.Write("pack-migration", ex);
            }
        }
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return System.Security.Cryptography.SHA256.HashData(stream);
    }

    public static void EnsureDirs()
    {
        foreach (var dir in new[] { Archive, Models })
        {
            try { Directory.CreateDirectory(dir); }
            catch
            {
                // 装到只读目录（如 Program Files）时建不出来；不阻断启动，只是热插拔不可用。
            }
        }
    }

    /// <summary>资料库包目录：archive 下每个含 json 的子目录算一个包。</summary>
    public static List<string> ArchivePacks()
    {
        try
        {
            return Directory.EnumerateDirectories(Archive)
                .Where(dir => Directory.EnumerateFiles(dir, "*.json").Any())
                .OrderBy(dir => Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    /// <summary>models 下可用的模型目录。</summary>
    public static List<string> ModelPacks()
    {
        try
        {
            if (!Directory.Exists(Models)) return [];
            return Directory.EnumerateDirectories(Models)
                .Where(LooksUsable)
                .OrderBy(dir => Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>只有恰好一个模型包时才能自动启用；多包不擅自挑选。</summary>
    public static string? SingleModelDir()
    {
        var packs = ModelPacks();
        return packs.Count == 1 ? packs[0] : null;
    }

    /// <summary>模型包里有没有可用的本体文件（不加载 onnx，避免拖进来就吃满显存）。</summary>
    public static bool LooksUsable(string dir)
        => File.Exists(Path.Combine(dir, "model.onnx")) && IsReadableJson(Path.Combine(dir, "config.json"));

    private static bool IsReadableJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("tags", out var tags) || tags.ValueKind != System.Text.Json.JsonValueKind.Array
                || !root.TryGetProperty("tags_split", out var splits) || splits.ValueKind != System.Text.Json.JsonValueKind.Array)
                return false;
            var names = tags.EnumerateArray().Select(t => t.GetString()).ToArray();
            var total = 0;
            var character = false;
            var work = false;
            foreach (var split in splits.EnumerateArray())
            {
                var count = split[1].GetInt32();
                if (count < 0) return false;
                total += count;
                if (split[0].GetString() == "character" && count > 0) character = true;
                if (split[0].GetString() == "copyright" && count > 0) work = true;
            }
            return total == names.Length && character && work
                && new[] { "rating:g", "rating:s", "rating:q", "rating:e" }.All(names.Contains);
        }
        catch
        {
            return false;
        }
    }
}
