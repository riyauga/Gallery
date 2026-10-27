using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Gallery.Services;

/// <summary>开机自启：HKCU\...\Run 下写一条指向本 exe 的值（和 galleryold 的 start_at_login 对应）。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Gallery";

    private static string ExePath => Process.GetCurrentProcess().MainModule?.FileName
                                     ?? Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void Apply(bool enabled)
    {
        if (AppPaths.Sandbox is not null) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        if (enabled)
        {
            var exe = ExePath;
            if (exe.Length == 0) return;
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>启动时把「设置里的意图」落到注册表（用户可能在别处删掉了）。</summary>
    public static void SyncFromSettings()
    {
        try
        {
            Apply(AppService.Settings.Settings.start_at_login);
        }
        catch
        {
            // 注册表被策略挡住不该影响启动
        }
    }
}
