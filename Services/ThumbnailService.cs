using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Services;

/// <summary>
/// 缩略图磁盘缓存，移植 galleryold <c>app/thumbs.py</c>。
/// 红线：key = sha1("{normcase路径}|{mtime:.9f}|{size}|{edge}")，两级散列目录，WebP q=82。
/// </summary>
public sealed class ThumbnailService
{
    public const int CacheEdge = 512;

    private readonly string _dir;
    private readonly int _quality;

    public ThumbnailService(string cacheDir, int quality = 82)
    {
        _dir = cacheDir;
        _quality = quality;
        Directory.CreateDirectory(_dir);
    }

    public string Key(string src, double mtime, long size)
    {
        // Python: f"{os.path.normcase(str(src))}|{mtime:.9f}|{size}|{edge}"
        var raw = $"{FileIdentity.PathKey(src)}|{StoreService.Fmt(mtime)}|{size}|{CacheEdge}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public string CachePath(string src, double mtime, long size)
    {
        var key = Key(src, mtime, size);
        return Path.Combine(_dir, key[..2], $"{key}.webp");
    }

    public string? Get(string src, double mtime, long size)
    {
        var p = CachePath(src, mtime, size);
        return File.Exists(p) ? p : null;
    }

    /// <summary>取缓存；缺失则生成。失败返回 null。</summary>
    public string? GetOrBuild(string src, double mtime, long size)
    {
        var dest = CachePath(src, mtime, size);
        if (File.Exists(dest)) return dest;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var data = BuildThumbBytes(src, CacheEdge, _quality);
        if (data is null) return null;
        var tmp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, dest, overwrite: true);
        }
        catch (IOException)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            return null;
        }
        return dest;
    }

    /// <summary>缓存超过上限时按修改时间删最旧。返回删除数。</summary>
    public int PruneOrphans(int maxFiles = 200_000)
    {
        var files = Directory.EnumerateFiles(_dir, "*.webp", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p)).ToList();
        if (files.Count <= maxFiles) return 0;
        var removed = 0;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc)
                     .Take(files.Count - maxFiles))
        {
            try { f.Delete(); removed++; } catch (IOException) { }
        }
        return removed;
    }

    /// <summary>
    /// 对应 build_thumb_bytes：EXIF 转正 → 等比缩到 edge 内 → WebP。
    /// </summary>
    public static byte[]? BuildThumbBytes(string src, int edge, int quality)
    {
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load(src);
            image.Mutate(ctx => ctx.AutoOrient());
            if (image.Width > edge || image.Height > edge)
            {
                var scale = Math.Min((double)edge / image.Width, (double)edge / image.Height);
                var w = Math.Max(1, (int)Math.Round(image.Width * scale));
                var h = Math.Max(1, (int)Math.Round(image.Height * scale));
                image.Mutate(ctx => ctx.Resize(w, h, KnownResamplers.Lanczos3));
            }
            using var ms = new MemoryStream();
            image.SaveAsWebp(ms, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = quality });
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or SixLabors.ImageSharp.UnknownImageFormatException
            or SixLabors.ImageSharp.InvalidImageContentException)
        {
            return null;
        }
    }
}
