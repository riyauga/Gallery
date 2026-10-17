using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Services;

public sealed record ImageMeta(int? Width, int? Height, double? TakenAt);

/// <summary>
/// 图像头信息读取，移植 galleryold <c>app/imageinfo.py</c>：
/// 尺寸 + EXIF 拍摄时间（DateTimeOriginal → Digitized → DateTime），
/// Orientation 5/6/7/8 时宽高互换。任何失败返回空 meta。
/// </summary>
public static class ImageInfo
{
    private static readonly int[] SwapOrientations = [5, 6, 7, 8];

    public static ImageMeta ReadMeta(string path)
    {
        try
        {
            using var image = Image.Load(path); // 不解码像素到托管内存也行：仅元数据
            int w = image.Width, h = image.Height;
            double? takenAt = null;
            int orientation = 1;
            var exif = image.Metadata.ExifProfile;
            if (exif is not null)
            {
                var orient = exif.TryGetValue(ExifTag.Orientation, out var o) ? o : null;
                if (orient is not null) orientation = (int)orient.Value;

                // 优先 Exif IFD 的 DateTimeOriginal(36867) → Digitized(36868)，再 0th 的 DateTime(306)
                takenAt = ParseExifDt(
                    exif.TryGetValue(ExifTag.DateTimeOriginal, out var d1) ? d1?.Value : null)
                    ?? ParseExifDt(exif.TryGetValue(ExifTag.DateTimeDigitized, out var d2) ? d2?.Value : null)
                    ?? ParseExifDt(exif.TryGetValue(ExifTag.DateTime, out var d3) ? d3?.Value : null);
            }
            if (SwapOrientations.Contains(orientation)) (w, h) = (h, w);
            return new ImageMeta(w, h, takenAt);
        }
        catch (Exception ex) when (ex is IOException
            or SixLabors.ImageSharp.UnknownImageFormatException
            or SixLabors.ImageSharp.InvalidImageContentException)
        {
            return new ImageMeta(null, null, null);
        }
    }

    /// <summary>对应 _parse_exif_dt："2024:05:01 12:34:56" 与 ISO 变体；解析为本地时间的 Unix 秒。</summary>
    private static double? ParseExifDt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim().Trim('\0');
        string[] formats =
        [
            "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss",
            "yyyy:MM:dd HH:mm", "yyyy-MM-ddTHH:mm:ss",
        ];
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(text, format,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
            {
                var local = DateTime.SpecifyKind(dt, DateTimeKind.Local);
                return local.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds;
            }
        }
        return null;
    }
}
