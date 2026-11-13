namespace Gallery.Services;

/// <summary>Bounded operational log. Never accepts exception messages, paths, keys or payloads.</summary>
public static class FaultLog
{
    private static readonly object Gate = new();
    public static void Write(string operation, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LocalDataDir);
                var path = Path.Combine(AppPaths.LocalDataDir, "fault.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
                    File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {operation} {error?.GetType().Name} {error?.HResult:X8}\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
