using Microsoft.Win32;

namespace Gallery.Services;

internal static class GalleryLaunchProtocol
{
    private const string ProtocolKey = @"Software\Classes\gallery-open";
    private const string CommandKey = @"shell\open\command";
    public const string Argument = "--open-library-uri";

    public static bool IsLibraryUri(string value) =>
        value is "gallery-open://library" or "gallery-open://library/";

    public static string? ConfiguredPath()
    {
        using var protocol = Registry.CurrentUser.OpenSubKey(ProtocolKey);
        return protocol?.GetValue("CustomPath") as string ?? protocol?.GetValue("InstalledPath") as string;
    }

    public static void SetTarget(string? customPath)
    {
        using var current = Registry.CurrentUser.OpenSubKey(ProtocolKey);
        var installedPath = current?.GetValue("InstalledPath") as string;
        var path = customPath ?? installedPath ?? Path.Combine(AppContext.BaseDirectory, "Gallery.exe");
        if (!Path.IsPathFullyQualified(path)
            || !Path.GetFileName(path).Equals("Gallery.exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
            throw new IOException("请选择存在的 Gallery.exe。");

        using var protocol = Registry.CurrentUser.CreateSubKey(ProtocolKey);
        protocol.SetValue("", "URL:Gallery");
        protocol.SetValue("URL Protocol", "");
        using var command = protocol.CreateSubKey(CommandKey);
        command.SetValue("", $"\"{path}\" {Argument} \"%1\"");
        if (customPath is null)
        {
            protocol.DeleteValue("CustomPath", false);
            if (installedPath is null) protocol.SetValue("InstalledPath", path);
        }
        else protocol.SetValue("CustomPath", path);
    }
}
