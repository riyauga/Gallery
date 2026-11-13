using System.Runtime.InteropServices;
using Windows.Storage;
using Windows.System.UserProfile;

namespace Gallery.Services;

public enum WallpaperTarget { Desktop, LockScreen }

/// <summary>System personalization. Never modifies the source image or machine policy.</summary>
public static class WallpaperService
{
    private static readonly SemaphoreSlim Setting = new(1, 1);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);

    public static async Task<(bool Success, string Error)> SetAsync(string path, WallpaperTarget target)
    {
        if (!Setting.Wait(0)) return (false, "正在设置图片，请稍候");
        string? pending = null;
        try
        {
            if (!File.Exists(path)) return (false, "文件已不在原位");
            if (target == WallpaperTarget.Desktop)
            {
                return await Task.Run(() =>
                {
                    if (SystemParametersInfo(0x0014, 0, path, 0x01 | 0x02)) return (true, "");
                    var code = Marshal.GetLastWin32Error();
                    return (false, code == 0 ? "系统拒绝了这个文件" : $"系统拒绝了这个文件（错误 {code}）");
                });
            }
            if (!UserProfilePersonalizationSettings.IsSupported()) return (false, "当前系统不支持在应用中设置锁屏图片");
            var directory = Path.Combine(AppPaths.LocalDataDir, "personalization");
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            pending = copy;
            await Task.Run(() => File.Copy(path, copy));
            var file = await StorageFile.GetFileFromPathAsync(copy);
            var success = await UserProfilePersonalizationSettings.Current.TrySetLockScreenImageAsync(file);
            // Windows may continue reading the successful file after the asynchronous call returns.
            if (success)
            {
                pending = null;
                try
                {
                    foreach (var old in Directory.GetFiles(directory).Where(p => p != copy)) File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FaultLog.Write("personalization-cleanup", ex); }
            }
            return success ? (true, "") : (false, "系统未能设置锁屏图片，请检查系统个性化设置或设备策略");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            FaultLog.Write("personalization", ex);
            return (false, "无法设置图片，请检查文件格式、访问权限或系统个性化设置");
        }
        finally
        {
            if (pending is not null)
            {
                try { File.Delete(pending); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FaultLog.Write("personalization-cleanup", ex); }
            }
            Setting.Release();
        }
    }
}
