namespace Gallery.Services;

public sealed record ScanResult
{
    public HashSet<string> Directories { get; } = [];
    public bool DirectoriesComplete { get; set; }
    public int Scanned { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Missing { get; set; }
    public int SidecarsImported { get; set; }
    public int SkippedDirs { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = [];

    public string Summary()
    {
        var bits = new List<string> { $"扫描 {Scanned} 个文件" };
        if (Added > 0) bits.Add($"新增 {Added}");
        if (Updated > 0) bits.Add($"更新 {Updated}");
        if (Missing > 0) bits.Add($"缺失 {Missing}");
        if (SidecarsImported > 0) bits.Add($"导入 sidecar {SidecarsImported} 项");
        if (Cancelled) bits.Add("（已中断）");
        return string.Join("，", bits);
    }
}

/// <summary>
/// 目录增量扫描，移植 galleryold <c>app/scanner.py</c>：
/// 跳过隐藏/系统/.开头/临时目录；每个目录先导 sidecar 再索引文件；
/// 完整走完后 mark_missing + sync_managed。不动任何文件。
/// </summary>
public sealed class ScannerService(StoreService store, AppSettings settings)
{
    private static readonly HashSet<string> SkipDirNames =
        ["$RECYCLE.BIN", "System Volume Information", ".git", ".svn",
         "node_modules", "__pycache__"];

    public static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".gif", ".webp", ".bmp",
        ".tif", ".tiff", ".avif", ".heic", ".heif", ".ico", ".jxl", ".psd",
    };

    private readonly ThumbnailService _thumbs =
        new(AppPaths.ThumbCacheDir, settings.thumb_quality);

    public ThumbnailService Thumbs => _thumbs;

    public static bool ShouldSkipDir(string name, string full)
    {
        if (SkipDirNames.Contains(name)) return true;
        if (name.StartsWith('.')) return true;
        try
        {
            var attrs = File.GetAttributes(full);
            return attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System);
        }
        catch (IOException) { return false; }
    }

    private static IEnumerable<string> IterImageFiles(string folder)
    {
        List<string> files = [];
        try
        {
            foreach (var entry in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(entry);
                if (name == StoreService.SidecarName || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ImageExts.Contains(Path.GetExtension(entry))) files.Add(entry);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return files;
    }

    public ScanResult ScanRoot(int rootId, Action<int, string>? progress = null,
        Func<bool>? cancel = null, IEnumerable<string>? exclude = null)
    {
        var res = new ScanResult();
        var root = store.RootPath(rootId);
        if (root is null || !Directory.Exists(root))
        {
            res.Errors.Add($"根目录不存在：{root}");
            return res;
        }

        var excludeNorm = (exclude ?? []).Select(FileIdentity.PathKey).ToHashSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var n = 0;

        foreach (var dirpath in EnumerateDirs(root))
        {
            if (cancel?.Invoke() == true) { res.Cancelled = true; break; }

            string dirRel = Path.GetRelativePath(root, dirpath).Replace('\\', '/');
            if (dirRel == ".") dirRel = "";
            res.Directories.Add(dirRel);

            // 1) 外部改过的 sidecar 优先导入
            try { res.SidecarsImported += store.ImportSidecar(rootId, dirRel); }
            catch (Exception ex) { res.Errors.Add($"{dirRel}: sidecar 导入失败 {ex.Message}"); }

            // 2) 索引文件
            var newIds = new List<int>();
            foreach (var entry in IterImageFiles(dirpath))
            {
                if (cancel?.Invoke() == true) { res.Cancelled = true; break; }
                var name = Path.GetFileName(entry);
                var rel = dirRel.Length > 0 ? $"{dirRel}/{name}" : name;
                seen.Add(rel);
                res.Scanned++;
                n++;
                if (progress is not null && n % 40 == 0) progress(n, name);

                FileInfo stat;
                try { stat = new FileInfo(entry); } catch (IOException) { continue; }

                var known = store.QueryOne(
                    "SELECT id,size,mtime FROM images WHERE root_id=$r AND rel_path=$p",
                    ("$r", rootId), ("$p", rel));
                ImageMeta? meta = null;
                var mtime = ToUnix(stat.LastWriteTimeUtc);
                if (known is null || known.Size != stat.Length ||
                    Math.Abs(known.Mtime - mtime) > 1e-6)
                    meta = ImageInfo.ReadMeta(entry);

                try
                {
                    var (imageId, created) = store.UpsertImage(
                        rootId: rootId, relPath: rel, absPath: entry, dirRel: dirRel,
                        name: name, ext: Path.GetExtension(entry).ToLowerInvariant(),
                        size: stat.Length, mtime: mtime,
                        ctime: ToUnix(stat.CreationTimeUtc),
                        width: meta?.Width, height: meta?.Height, takenAt: meta?.TakenAt);
                    if (created) { res.Added++; newIds.Add(imageId); }
                    else res.Updated++;
                }
                catch (Exception ex)
                {
                    res.Errors.Add($"{rel}: {ex.Message}");
                }
            }

            store.Commit();
            try { res.SidecarsImported += store.ImportSidecar(rootId, dirRel); }
            catch (Exception ex) { res.Errors.Add($"{dirRel}: 标签恢复失败 {ex.Message}"); }

            // 3) 新文件按目录自动打标签
            if (settings.auto_tag_from_folder && newIds.Count > 0)
            {
                var tags = AutoTagsFor(dirRel, settings.auto_tag_depth);
                if (tags.Count > 0)
                {
                    try { store.AddTags(newIds, tags); }
                    catch (Exception ex) { res.Errors.Add($"{dirRel}: 自动标签失败 {ex.Message}"); }
                }
            }
        }

        res.DirectoriesComplete = !res.Cancelled;
        if (res.DirectoriesComplete)
        {
            res.Missing = store.MarkMissing(rootId, seen);
            store.Commit();
            SyncManaged(rootId);
        }
        progress?.Invoke(n, "完成");
        if (!res.Cancelled) AppService.RequestCatalogAlign();
        return res;
    }

    private static IEnumerable<string> EnumerateDirs(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            yield return dir;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (ShouldSkipDir(name, sub)) continue;
                queue.Enqueue(sub);
            }
        }
    }

    /// <summary>把相对目录前 depth 段当标签："微博/2026-01" + 1 → ["微博"]。</summary>
    private static List<string> AutoTagsFor(string dirRel, int depth)
    {
        if (depth <= 0 || dirRel.Length == 0) return [];
        return dirRel.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(depth).ToList();
    }

    /// <summary>对应 sync_managed：只更新主动管理过的图片（含分辨率标签），再写回 sidecar。</summary>
    private void SyncManaged(int rootId)
    {
        var rows = store.QueryAll(
            "SELECT id,dir_rel,width,height FROM images WHERE root_id=$r AND managed=1 AND missing=0",
            ("$r", rootId));
        var directories = new HashSet<string>();
        foreach (var row in rows)
        {
            var pairs = store.CategorizedTags(row.Id)
                .Where(p => p.Category != "resolution")
                .ToList();
            pairs.AddRange(Taxonomy.ResolutionTags(row.Width, row.Height)
                .Select(tag => ("resolution", tag)));
            using var tx = store.Transaction();
            store.SetCategorizedTags([row.Id], pairs);
            tx.Success = true;
            directories.Add(row.DirRel);
        }
        if (store.SidecarEnabled)
        {
            foreach (var directory in directories)
                store.WriteSidecar(rootId, directory);
            foreach (var (oldRoot, directory) in store.TrackingDirty)
                store.WriteSidecar(oldRoot, directory);
            store.TrackingDirty.Clear();
        }
    }

    private static double ToUnix(DateTime utc) => StoreService.ToUnixSeconds(utc);
}
