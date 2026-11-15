using Microsoft.Win32;

namespace Gallery.Services;

/// <summary>
/// 把本程序登记为“打开方式”候选。写入的键与安装器 <c>tools/installer.iss</c> 一致：
/// 只写 OpenWithProgids、ProgId 与 Capabilities，不碰 UserChoice，也不改现有默认程序。
/// 免安装版靠这里补注册；安装版已由安装器写过，重复写入是幂等的。
/// </summary>
public static class FileAssociationService
{
    private const string ProgId = "Gallery.Media";
    private const string CapabilitiesPath = @"Software\Gallery\Capabilities";
    private const string Description = "Gallery 图片与视频";

    /// <summary>与安装器注册的扩展名保持一致。</summary>
    private static readonly string[] Extensions =
    [
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".webp", ".bmp",
        ".tif", ".tiff", ".avif", ".heic", ".heif", ".ico", ".jxl", ".psd", ".mp4",
    ];

    private static string ExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Gallery.exe");

    private static string OpenCommand => "\"" + ExePath + "\" --view \"%1\"";

    /// <summary>当前这份 exe 是否已经登记为候选（路径不匹配视为没登记）。</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command");
            return key?.GetValue(null) is string command
                && command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>写入候选登记。返回 false 表示注册表写入失败，界面据此提示。</summary>
    public static bool EnsureRegistered()
    {
#if GALLERY_DEVELOPMENT
        if (AppPaths.Sandbox is not null) return true;
#endif
        try
        {
            using (var application = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Gallery.exe"))
            {
                if (application is null) return false;
                application.SetValue("FriendlyAppName", "Gallery");
                using var command = application.CreateSubKey(@"shell\open\command");
                command?.SetValue(null, OpenCommand);
                using var supported = application.CreateSubKey("SupportedTypes");
                foreach (var extension in Extensions) supported?.SetValue(extension, "");
            }

            using (var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                if (progId is null) return false;
                progId.SetValue(null, Description);
                using var icon = progId.CreateSubKey("DefaultIcon");
                icon?.SetValue(null, ExePath + ",0");
                using var command = progId.CreateSubKey(@"shell\open\command");
                command?.SetValue(null, OpenCommand);
            }

            using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
            {
                if (capabilities is null) return false;
                capabilities.SetValue("ApplicationName", "Gallery");
                capabilities.SetValue("ApplicationDescription", "查看图片和播放视频");
                using var associations = capabilities.CreateSubKey("FileAssociations");
                foreach (var extension in Extensions) associations?.SetValue(extension, ProgId);
            }

            using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            {
                registered?.SetValue("Gallery", CapabilitiesPath);
            }

            foreach (var extension in Extensions)
            {
                using var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
                openWith?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            FaultLog.Write("associations", ex);
            return false;
        }
    }

    /// <summary>
    /// 打开系统“默认应用”。Windows 11 22H2 起可直达本程序的设置页；
    /// 老版本退回到默认应用总表，由用户自己选扩展名。
    /// </summary>
    public static async Task<bool> OpenDefaultAppsAsync()
    {
        if (await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps?registeredAppUser=Gallery")))
            return true;
        return await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }
}
