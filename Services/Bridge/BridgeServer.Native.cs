using Microsoft.Win32;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gallery.Services.Bridge;

public sealed partial class BridgeServer
{
    private async Task AnnounceAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream("Gallery.BrowserBridge", PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                var bytes = Encoding.UTF8.GetBytes(Port.ToString() + "\n");
                await pipe.WriteAsync(bytes, cancellation);
                await pipe.FlushAsync(cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static readonly object NativeRegistrationLock = new();
    private static readonly HashSet<string> RegisteredOrigins = new(StringComparer.Ordinal);
    private void RegisterNativeHost(string origin)
    {
        if (!Regex.IsMatch(origin, @"^chrome-extension://[a-p]{32}$")) return;
#if GALLERY_DEVELOPMENT
        if (AppPaths.Sandbox is not null) return;
#endif
        var executable = Path.Combine(AppContext.BaseDirectory, "Gallery.NativeHost.exe");
        if (!File.Exists(executable)) return;
        lock (NativeRegistrationLock)
        {
            try
            {
                if (RegisteredOrigins.Contains(origin)) return;
                var directory = Path.Combine(AppPaths.LocalDataDir, "browser-host");
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "manifest.json");
                var origins = new HashSet<string>(StringComparer.Ordinal);
                if (File.Exists(file))
                {
                    using var old = JsonDocument.Parse(File.ReadAllText(file));
                    if (old.RootElement.TryGetProperty("allowed_origins", out var values))
                        foreach (var value in values.EnumerateArray())
                            if (value.GetString() is { } item) origins.Add(item);
                }
                origins.Add(origin + "/");
                File.WriteAllText(file, JsonSerializer.Serialize(new
                {
                    name = "gallery.bridge",
                    description = "Gallery browser connection",
                    path = executable,
                    type = "stdio",
                    allowed_origins = origins,
                }));
                foreach (var browser in new[] { @"Google\Chrome", @"Microsoft\Edge" })
                {
                    using var key = Registry.CurrentUser.CreateSubKey($@"Software\{browser}\NativeMessagingHosts\gallery.bridge");
                    key.SetValue("", file);
                }
                RegisteredOrigins.Add(origin);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or JsonException)
            {
                LastError = "浏览器连接组件注册失败：" + error.Message;
            }
        }
    }
}
