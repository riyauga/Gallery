using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Gallery.Services.Bridge;

public sealed partial class BridgeServer
{
    private async Task SubscribeReviewAsync(NetworkStream stream, string origin,
        Dictionary<string, string> headers, CancellationToken handshake, CancellationToken shutdown)
    {
        var key = headers.GetValueOrDefault("sec-websocket-key", "");
        if (!origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
            || !headers.GetValueOrDefault("upgrade", "").Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.GetValueOrDefault("connection", "").Split(',').Any(value =>
                value.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase))
            || headers.GetValueOrDefault("sec-websocket-version") != "13"
            || !Convert.TryFromBase64String(key, new byte[16], out var keySize) || keySize != 16)
        {
            await WriteAsync(stream, 400, JsonError("无法建立审核订阅"), handshake, origin).ConfigureAwait(false);
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
        await stream.WriteAsync(response, handshake).ConfigureAwait(false);

        using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
        var auth = new byte[256];
        var first = await socket.ReceiveAsync(auth.AsMemory(), handshake).ConfigureAwait(false);
        if (first.MessageType != WebSocketMessageType.Text || !first.EndOfMessage
            || !TokenMatches(Encoding.UTF8.GetString(auth, 0, first.Count)))
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Pairing required", handshake)
                .ConfigureAwait(false);
            return;
        }

        var updates = Channel.CreateBounded<bool>(1);
        void Signal() => updates.Writer.TryWrite(true);
        _store.ReviewableIngestChanged += Signal;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        // After authentication the client has nothing else to send. Any frame or close ends its subscription.
        var disconnected = socket.ReceiveAsync(auth.AsMemory(), lifetime.Token).AsTask();
        try
        {
            while (!disconnected.IsCompleted)
            {
                Touch();
                var snapshot = JsonSerializer.SerializeToUtf8Bytes(new { pending = _store.ReviewableIngestCount() });
                await socket.SendAsync(snapshot.AsMemory(), WebSocketMessageType.Text, true, lifetime.Token)
                    .ConfigureAwait(false);

                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                var next = updates.Reader.ReadAsync(heartbeat.Token).AsTask();
                if (await Task.WhenAny(disconnected, next).ConfigureAwait(false) == disconnected)
                {
                    heartbeat.Cancel();
                    break;
                }
                try { await next.ConfigureAwait(false); }
                catch (OperationCanceledException) when (!shutdown.IsCancellationRequested) { }
            }
        }
        catch (WebSocketException) { }
        finally
        {
            _store.ReviewableIngestChanged -= Signal;
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await disconnected.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (WebSocketException) { }
        }
    }
}
