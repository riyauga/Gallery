using System.Text.Json;
using System.Security.Cryptography;

namespace Gallery.Services;

public sealed record UpdateAsset(string Name, long Size, string Sha256);
public sealed record UpdateManifest(string Version, UpdateAsset Installer, UpdateAsset Portable);
public sealed record UpdateRequest(int ParentId, string Target, string Package, string Kind,
    string Version, long Size, string Sha256, string DataRoot);

public static class UpdateProtocol
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string UpdateGateName(string dataRoot)
        => "Local\\Gallery.Update." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant())))[..20];
    public static string UpdatesRoot(string dataRoot) => Path.Combine(dataRoot, "updates");
    public static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Invalid package path");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package path escapes target");
        return path;
    }
    public static string[] ReadFiles(string directory)
    {
        var files = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, "application-files.json")), Json)
            ?? throw new InvalidDataException("Missing file list");
        if (files.Length == 0 || !files.Contains("Gallery.exe", StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Missing application");
        foreach (var file in files)
        {
            _ = Child(directory, file);
            var first = file.Replace('\\', '/').Split('/')[0];
            if (new[] { "archive", "models", "packs", "data", "updates" }.Contains(first, StringComparer.OrdinalIgnoreCase) ||
                new[] { "settings.json", "library.db", "folder_map.json", "personal_rules.json" }.Contains(first, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Package contains user data");
        }
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new InvalidDataException("Duplicate package path");
        return files;
    }
    public static void Verify(string path, long size, string sha256)
    {
        if (size <= 0 || sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) || new FileInfo(path).Length != size)
            throw new InvalidDataException("Package size mismatch");
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package checksum mismatch");
    }
}
