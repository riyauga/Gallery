using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Gallery.Services;

public sealed record DesktopCommand(string Action, string[] Paths);
public sealed record DesktopReply(bool Success, string Message = "", int Count = 0, int ProcessId = 0);

/// <summary>Current-user, local command transport. Library commands are serialized by their UI host.</summary>
public sealed class DesktopPipe : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<DesktopCommand, Task<DesktopReply>> _handle;
    public static string ViewerScope => AppPaths.LocalDataDir + "|viewer";
    private static string Name(bool viewer) => "Gallery.Commands." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(viewer ? ViewerScope : AppPaths.LocalDataDir)))[..20];

    public DesktopPipe(bool viewer, DispatcherQueue dispatcher, Func<DesktopCommand, Task<DesktopReply>> handle)
    {
        _dispatcher = dispatcher; _handle = handle;
        _ = ListenAsync(Name(viewer));
    }
    private async Task ListenAsync(string name)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch { pipe.Dispose(); throw; }
                _ = ServeAsync(pipe);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FaultLog.Write("desktop-listen", ex); }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
        {
            try
            {
                var line = await reader.ReadLineAsync(_stop.Token);
                var command = JsonSerializer.Deserialize<DesktopCommand>(line ?? "") ?? throw new JsonException();
                if (command.Paths is null) throw new JsonException();
                var done = new TaskCompletionSource<DesktopReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_dispatcher.TryEnqueue(async () =>
                {
                    try { done.TrySetResult(await _handle(command)); }
                    catch (Exception ex) { FaultLog.Write("desktop-command", ex); done.TrySetResult(new(false, "操作未完成，请重试。")); }
                })) return;
                var reply = await done.Task.WaitAsync(_stop.Token);
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException)
            { if (!_stop.IsCancellationRequested) FaultLog.Write("desktop-connection", ex); }
        }
    }
    public static async Task<DesktopReply?> SendAsync(bool viewer, DesktopCommand command, int connectMilliseconds = 2000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name(viewer), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(connectMilliseconds);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(command));
            var line = await reader.ReadLineAsync();
            return line is null ? null : JsonSerializer.Deserialize<DesktopReply>(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or JsonException) { return null; }
    }
    public void Dispose() { _stop.Cancel(); }
}
