using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Gallery.Services;

namespace Gallery.Updater;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var work = Path.GetFullPath(args[0]);
        UpdateRequest? request = null;
        var oldFiles = Array.Empty<string>();
        var newFiles = Array.Empty<string>();
        var replacing = false;
        Process? launched = null;
        try
        {
            request = JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(Path.Combine(work, "request.json")), UpdateProtocol.Json)
                ?? throw new InvalidDataException("Missing request");
            if (!Guid.TryParseExact(Path.GetFileName(work), "N", out _) ||
                !string.Equals(Path.GetDirectoryName(work), UpdateProtocol.UpdatesRoot(request.DataRoot), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(request.Package), work, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update workspace");
            try
            {
                using var parent = Process.GetProcessById(request.ParentId);
                await parent.WaitForExitAsync();
            }
            catch (ArgumentException) { }
            // No replacement after a failed/cancelled shutdown, even if the user later exits normally.
            if (!File.Exists(Path.Combine(work, "install"))) return 3;
            UpdateProtocol.Verify(request.Package, request.Size, request.Sha256);
            var target = Path.GetFullPath(request.Target);
            if (Directory.GetParent(target) is null || !File.Exists(Path.Combine(target, "Gallery.exe"))) throw new InvalidDataException("Invalid installation");
            oldFiles = UpdateProtocol.ReadFiles(target);
            oldFiles = oldFiles.Concat(Directory.GetFiles(target, "unins*.*").Select(Path.GetFileName).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var backup = Path.Combine(work, "backup");
            foreach (var file in oldFiles)
            {
                var source = UpdateProtocol.Child(target, file);
                if (!File.Exists(source)) continue;
                NoLinks(target, source);
                var copy = UpdateProtocol.Child(backup, file);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(source, copy);
            }
            var payload = Path.Combine(work, "payload");
            if (request.Kind == "portable")
            {
                Directory.CreateDirectory(payload);
                using var archive = ZipFile.OpenRead(request.Package);
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    var prefix = "Gallery-" + request.Version + "/";
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected archive root");
                    var relative = name[prefix.Length..];
                    if (relative.Length == 0) continue;
                    var output = UpdateProtocol.Child(payload, relative);
                    if (name.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    entry.ExtractToFile(output, false);
                }
                newFiles = UpdateProtocol.ReadFiles(payload);
                var actual = Directory.GetFiles(payload, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(payload, p)).ToArray();
                if (actual.Length != newFiles.Length || actual.Except(newFiles, StringComparer.OrdinalIgnoreCase).Any())
                    throw new InvalidDataException("Unexpected payload files");
                if (File.ReadAllText(Path.Combine(payload, "distribution-kind.txt")).Trim() != "portable") throw new InvalidDataException("Wrong distribution");
                foreach (var file in newFiles)
                {
                    var destination = UpdateProtocol.Child(target, file);
                    NoLinks(target, destination);
                    if (File.Exists(destination) && !oldFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                        throw new IOException("Update would overwrite an unowned file");
                }
                replacing = true;
                File.WriteAllText(Path.Combine(work, "replacing"), "1");
                foreach (var file in newFiles)
                {
                    var destination = UpdateProtocol.Child(target, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(UpdateProtocol.Child(payload, file), destination, true);
                }
            }
            else if (request.Kind == "installed")
            {
                replacing = true;
                File.WriteAllText(Path.Combine(work, "replacing"), "1");
                var setup = new ProcessStartInfo(request.Package) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/DIR=" + target }) setup.ArgumentList.Add(argument);
                using var installer = Process.Start(setup) ?? throw new IOException("Cannot start installer");
                await installer.WaitForExitAsync();
                if (installer.ExitCode != 0) throw new IOException("Installer failed");
                newFiles = UpdateProtocol.ReadFiles(target);
                if (File.ReadAllText(Path.Combine(target, "distribution-kind.txt")).Trim() != "installed") throw new InvalidDataException("Wrong installation marker");
            }
            else throw new InvalidDataException("Unknown distribution");
            if (FileVersionInfo.GetVersionInfo(Path.Combine(target, "Gallery.exe")).ProductVersion?.Split('+')[0] != request.Version)
                throw new InvalidDataException("Installed version mismatch");
            foreach (var obsolete in oldFiles.Except(newFiles, StringComparer.OrdinalIgnoreCase).Where(f => !f.StartsWith("unins", StringComparison.OrdinalIgnoreCase)))
            {
                var path = UpdateProtocol.Child(target, obsolete);
                if (File.Exists(path)) File.Delete(path);
            }
            var start = new ProcessStartInfo(Path.Combine(target, "Gallery.exe")) { UseShellExecute = false, WorkingDirectory = target };
            start.ArgumentList.Add("--update-token");
            start.ArgumentList.Add(Path.GetFileName(work));
            launched = Process.Start(start) ?? throw new IOException("Cannot restart application");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !launched.HasExited && !File.Exists(Path.Combine(work, "started"))) await Task.Delay(250);
            if (!File.Exists(Path.Combine(work, "started")) || File.ReadAllText(Path.Combine(work, "started")) != request.Version)
                throw new IOException("New application did not confirm startup");
            File.WriteAllText(Path.Combine(work, "succeeded"), request.Version);
            try
            {
                Directory.Delete(backup, true);
                if (Directory.Exists(payload)) Directory.Delete(payload, true);
                File.Delete(request.Package);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Installation is committed. A locked cleanup file must not roll back a running new version.
            }
            return 0;
        }
        catch (Exception error)
        {
            if (request is not null && replacing)
            {
                try
                {
                    if (launched is { HasExited: false }) { launched.Kill(); await launched.WaitForExitAsync(); }
                    if (newFiles.Length == 0 && File.Exists(Path.Combine(request.Target, "application-files.json")))
                        newFiles = UpdateProtocol.ReadFiles(request.Target);
                    foreach (var file in newFiles.Except(oldFiles, StringComparer.OrdinalIgnoreCase))
                    {
                        var path = UpdateProtocol.Child(request.Target, file);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    foreach (var file in oldFiles)
                    {
                        var copy = UpdateProtocol.Child(Path.Combine(work, "backup"), file);
                        if (!File.Exists(copy)) continue;
                        var target = UpdateProtocol.Child(request.Target, file);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(copy, target, true);
                    }
                    File.WriteAllText(Path.Combine(work, "failed"), "更新未完成，已恢复旧程序。请重试。新版已启动过时，数据库不会自动回退。");
                    Process.Start(new ProcessStartInfo(Path.Combine(request.Target, "Gallery.exe")) { UseShellExecute = false, WorkingDirectory = request.Target });
                }
                catch (Exception recovery)
                {
                    File.WriteAllText(Path.Combine(work, "failed"), "更新恢复未完成。请重新安装程序；个人数据已保留。 " + recovery.GetType().Name);
                }
            }
            else if (Directory.Exists(work)) File.WriteAllText(Path.Combine(work, "failed"), "更新未开始，原程序未修改。请重新启动程序后重试。");
            try
            {
                if (request is not null)
                {
                    var log = Path.Combine(request.DataRoot, "fault.log");
                    if (File.Exists(log) && new FileInfo(log).Length >= 2 * 1024 * 1024) File.Move(log, log + ".1", true);
                    File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} update {error.GetType().Name} {error.HResult:X8}\n");
                }
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            return 1;
        }
        finally { launched?.Dispose(); }
    }

    private static void NoLinks(string root, string path)
    {
        for (var current = path; current.Length >= Path.TrimEndingDirectorySeparator(root).Length; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation contains a linked path");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) break;
        }
    }
}
