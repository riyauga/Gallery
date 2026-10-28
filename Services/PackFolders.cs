namespace Gallery.Services;

/// <summary>
/// 可热插拔的资料包目录：开发时在项目根目录，独立分发时在程序根目录。
///   <c>archive\&lt;包名&gt;\*.json</c> — 资料库包，可并存多个
///   <c>models\&lt;包名&gt;\</c> — 识别模型，需要 model.onnx + config.json，同时只保留一个
/// 用户在设置页拖入压缩包或文件夹即可导入，不需要重新打包程序。
/// </summary>
public static class PackFolders
{
    /// <summary>
    /// 资料包根目录。开发输出向上定位 Gallery.csproj；独立分发用程序目录。
    /// 带 <c>--dataroot</c> 跑沙箱时改到沙箱里，
    /// 免得自测把包写进真实安装目录。
    /// </summary>
    public static string Root => AppPaths.Sandbox is { Length: > 0 } sandbox
        ? Path.Combine(sandbox, "packs")
        : DefaultRoot;

    private static readonly string DefaultRoot = ResolveRoot();

    private static string ResolveRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gallery.csproj"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }

    public static string Archive => Path.Combine(Root, "archive");

    public static string Models => Path.Combine(Root, "models");

    /// <summary>旧版外部词库目录；仍参与读取，保证早先解压到这里的包不失效。</summary>
    public static string LegacyCatalog => Path.Combine(
        AppPaths.Sandbox is { Length: > 0 } ? Root : AppContext.BaseDirectory, "data", "tag_catalog");

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
