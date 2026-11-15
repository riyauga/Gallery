using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Services;

/// <summary>独立重设大小弹窗的解码、缩放及编码；编辑效果由原版网页编辑器处理。</summary>
public static class ImageEditService
{
    public static async Task<Image<Bgra32>?> LoadAsync(string path, int maxEdge)
    {
        try
        {
            return await Task.Run(() =>
            {
                var image = Image.Load<Bgra32>(path);
                image.Mutate(context => context.AutoOrient());
                if (maxEdge > 0 && Math.Max(image.Width, image.Height) > maxEdge)
                {
                    double scale = (double)maxEdge / Math.Max(image.Width, image.Height);
                    int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                    int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                    image.Mutate(context => context.Resize(width, height, KnownResamplers.Lanczos3));
                }
                return image;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            FaultLog.Write("edit-decode", ex);
            return null;
        }
    }

    public static Image<Bgra32> Resize(Image<Bgra32> source, int width, int height)
    {
        return source.Clone(context => context.Resize(width, height, KnownResamplers.Lanczos3));
    }

    public static async Task<bool> SaveAsync(string sourcePath, string targetPath, int width, int height, int quality)
    {
        try
        {
            await Task.Run(() =>
            {
                using var original = Image.Load<Bgra32>(sourcePath);
                original.Mutate(context => context.AutoOrient());
                using var resized = Resize(original, width, height);
                if (Path.GetExtension(targetPath).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif" or ".jpe")
                    resized.Save(targetPath, new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) });
                else resized.Save(targetPath);
            });
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            FaultLog.Write("image-resize", error);
            return false;
        }
    }
}
