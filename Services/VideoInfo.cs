using System.Runtime.InteropServices;
using Windows.Storage;

namespace Gallery.Services;

public sealed record VideoMeta(int? Width, int? Height, double? Duration);

/// <summary>
/// 视频头信息：宽高 + 时长，走 Windows 视频属性（Shell/Media Foundation 元数据）。
/// 与 <see cref="ImageInfo"/> 一样，任何失败都返回空 meta，不阻断入库；
/// 调用方可能处在 UI 线程，内部用线程池等待，避免 WinRT 回调回到 UI 线程造成死锁。
/// </summary>
public static class VideoInfo
{
    public static VideoMeta ReadMeta(string path)
    {
        try
        {
            return Task.Run(async () =>
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var props = await file.Properties.GetVideoPropertiesAsync();
                int width = props.Width > 0 ? (int)props.Width : 0;
                int height = props.Height > 0 ? (int)props.Height : 0;
                double? duration = props.Duration > TimeSpan.Zero && props.Duration != TimeSpan.MaxValue
                    ? props.Duration.TotalSeconds
                    : null;
                return new VideoMeta(width > 0 ? width : null, height > 0 ? height : null, duration);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or COMException or InvalidOperationException)
        {
            return new VideoMeta(null, null, null);
        }
    }
}
