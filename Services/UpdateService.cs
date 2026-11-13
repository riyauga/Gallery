using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Gallery.Services;

public sealed record AvailableUpdate(string Version, string Notes, UpdateManifest Manifest, Uri PackageUrl, string Kind);
public sealed record PreparedUpdate(string Directory, string Helper, UpdateRequest Request);

/// <summary>GitHub stable releases only. Checks never download or install without user action.</summary>
public sealed class UpdateService
{
    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version!.ToString(3);
    private readonly HttpClient _http;
    public UpdateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("Gallery/" + CurrentVersion);
    }
    public static string DistributionKind
    {
        get
        {
            var kind = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "distribution-kind.txt")).Trim();
            return kind is "installed" or "portable" ? kind : throw new InvalidDataException("Unknown distribution");
        }
    }
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync("https://api.github.com/repos/riyauga/Gallery/releases/latest", timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = release.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = (root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Missing release version")).TrimStart('v');
        if (!Version.TryParse(tag, out var version) || version <= Version.Parse(CurrentVersion)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        Uri assetUrl(string name)
        {
            var matches = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Missing update asset");
            var uri = new Uri(matches[0].GetProperty("browser_download_url").GetString() ?? throw new InvalidDataException("Missing download URL"));
            if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/riyauga/Gallery/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected download origin");
            return uri;
        }
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(await _http.GetStringAsync(assetUrl("update-manifest.json"), timeout.Token), UpdateProtocol.Json)
            ?? throw new InvalidDataException("Invalid update manifest");
        if (manifest.Version != tag) throw new InvalidDataException("Version mismatch");
        var kind = DistributionKind;
        var asset = kind == "installed" ? manifest.Installer : manifest.Portable;
        if (asset is null || string.IsNullOrWhiteSpace(asset.Name) || Path.GetFileName(asset.Name) != asset.Name || !asset.Name.EndsWith(kind == "installed" ? "-setup.exe" : "-win-x64.zip", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid update asset");
        return new(tag, root.GetProperty("body").GetString() ?? "此版本未提供更新说明。", manifest, assetUrl(asset.Name), kind);
    }
    public async Task<PreparedUpdate> DownloadAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken token)
    {
        var asset = update.Kind == "installed" ? update.Manifest.Installer : update.Manifest.Portable;
        var directory = Path.Combine(UpdateProtocol.UpdatesRoot(AppPaths.LocalDataDir), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "downloading"), "1");
        var package = Path.Combine(directory, asset.Name);
        try
        {
            using var response = await _http.GetAsync(update.PackageUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(package))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > asset.Size) throw new InvalidDataException("Package too large");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report((double)total / asset.Size);
                }
            }
            UpdateProtocol.Verify(package, asset.Size, asset.Sha256);
            var helper = Path.Combine(directory, "Gallery.Updater.exe");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Gallery.Updater.exe"), helper);
            var request = new UpdateRequest(Environment.ProcessId, AppContext.BaseDirectory, package, update.Kind,
                update.Version, asset.Size, asset.Sha256, AppPaths.LocalDataDir);
            await File.WriteAllTextAsync(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(request, UpdateProtocol.Json), token);
            return new(directory, helper, request);
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }
    public static Process StartHelper(PreparedUpdate update)
    {
        var start = new ProcessStartInfo(update.Helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = update.Directory };
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(update.Directory, "runtime");
        start.ArgumentList.Add(update.Directory);
        return Process.Start(start) ?? throw new IOException("Cannot start updater");
    }
    public static void CleanupCompleted()
    {
        try { CleanupCompletedCore(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FaultLog.Write("update-cleanup", ex); }
    }

    private static void CleanupCompletedCore()
    {
        var root = UpdateProtocol.UpdatesRoot(AppPaths.LocalDataDir);
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.GetDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                (!File.Exists(Path.Combine(directory, "request.json")) && !File.Exists(Path.Combine(directory, "downloading")))) continue;
            if (!File.Exists(Path.Combine(directory, "succeeded")) &&
                (File.Exists(Path.Combine(directory, "install")) || File.Exists(Path.Combine(directory, "failed")))) continue;
            try
            {
                // A running helper locks its executable. Leave its workspace intact until the next launch.
                var helper = Path.Combine(directory, "Gallery.Updater.exe");
                if (File.Exists(helper)) File.Delete(helper);
                Directory.Delete(directory, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FaultLog.Write("update-cleanup", ex); }
        }
    }

    public static void ConfirmStartup(string[] args)
    {
        var index = Array.IndexOf(args, "--update-token");
        if (index < 0 || index + 1 >= args.Length || !Guid.TryParseExact(args[index + 1], "N", out _)) return;
        var directory = UpdateProtocol.Child(UpdateProtocol.UpdatesRoot(AppPaths.LocalDataDir), args[index + 1]);
        if (!File.Exists(Path.Combine(directory, "request.json"))) return;
        File.WriteAllText(Path.Combine(directory, "started"), CurrentVersion);
    }
}
