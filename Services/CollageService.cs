using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Services;

/// <summary>拼图导出：按布局结果逐张解码、缩放、贴到黑底画布。</summary>
public static class CollageService
{
    /// <summary>画布底色。留黑是产品决定，不是占位。</summary>
    public static readonly Rgba32 Background = new(0, 0, 0, 255);

    /// <summary>读源图显示尺寸（已按 EXIF 方向交换宽高）。读不到返回 0。</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        var meta = ImageInfo.ReadMeta(path);
        return (meta.Width ?? 0, meta.Height ?? 0);
    }

    /// <summary>全分辨率合成。任何一张读不到就整体失败，返回 null 并写故障日志。</summary>
    public static async Task<Image<Rgba32>?> RenderAsync(
        IReadOnlyList<CollageEntry> entries, double width, double height)
    {
        int canvasWidth = Math.Max(1, (int)Math.Round(width));
        int canvasHeight = Math.Max(1, (int)Math.Round(height));
        try
        {
            return await Task.Run(() =>
            {
                var canvas = new Image<Rgba32>(canvasWidth, canvasHeight, Background);
                try
                {
                    foreach (var entry in entries)
                    {
                        using var source = Image.Load<Rgba32>(entry.Path);
                        source.Mutate(context => context.AutoOrient());
                        int targetWidth = Math.Max(1, (int)Math.Round(entry.Width));
                        int targetHeight = Math.Max(1, (int)Math.Round(entry.Height));
                        using var scaled = source.Clone(context => context.Resize(targetWidth, targetHeight, KnownResamplers.Lanczos3));
                        int left = (int)Math.Round(entry.X), top = (int)Math.Round(entry.Y);
                        canvas.Mutate(context => context.DrawImage(scaled, new Point(left, top), 1f));
                    }
                    return canvas;
                }
                catch
                {
                    canvas.Dispose();
                    throw;
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or UnknownImageFormatException or InvalidImageContentException or NotSupportedException
            or OutOfMemoryException)
        {
            FaultLog.Write("collage-render", error);
            return null;
        }
    }
}
