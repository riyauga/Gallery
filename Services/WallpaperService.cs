using System.Runtime.InteropServices;

namespace Gallery.Services;

/// <summary>
/// 桌面壁纸：走系统桌面接口 SystemParametersInfo(SPI_SETDESKWALLPAPER)。
/// 超过 20 MiB 的图片不提交（与界面入口的提示保持一致），文件是否真的可用由系统判定。
/// </summary>
public static class WallpaperService
{
    public const long MaxBytes = 20L * 1024 * 1024;

    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);

    /// <summary>设置失败时返回 false，error 可直接展示给用户。</summary>
    public static bool TrySet(string path, out string error)
    {
        error = "";
        long size;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "文件已不在原位"; return false; }
            size = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
        if (size > MaxBytes) { error = "图片超过 20 MB"; return false; }

        if (SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE)) return true;

        int code = Marshal.GetLastWin32Error();
        error = code == 0 ? "系统拒绝了这个文件" : $"系统拒绝了这个文件（错误 {code}）";
        return false;
    }
}
