using System.IO;

namespace Gallery.Services;

/// <summary>把外部文件夹里的图片找出来送进待审核队列；只枚举，不复制也不移动文件。</summary>
public static class LocalImageImport
{
    public static List<string> Enumerate(string folder, bool recursive)
    {
        var files = new List<string>();
        if (!Directory.Exists(folder)) return files;

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(folder));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    if (name == StoreService.SidecarName ||
                        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ScannerService.ImageExts.Contains(Path.GetExtension(file))) files.Add(file);
                }

                if (!recursive) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!ScannerService.ShouldSkipDir(Path.GetFileName(sub), sub))
                        pending.Push(sub);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>合并多个来源；拖进来的单个图片文件也直接收下。</summary>
    public static List<string> EnumerateAll(IEnumerable<string> folders, IEnumerable<string> files, bool recursive)
    {
        var result = new List<string>();
        foreach (var folder in folders) result.AddRange(Enumerate(folder, recursive));
        foreach (var file in files)
        {
            if (File.Exists(file) && ScannerService.ImageExts.Contains(Path.GetExtension(file)))
                result.Add(file);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
