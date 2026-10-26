using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gallery.Services.Bridge;

/// <summary>
/// 浏览器扩展的本地桥接（对齐 galleryold 的 app/bridge.py 协议）：
/// 只绑 127.0.0.1，校验 Host（防 DNS rebinding）与 Origin（只接受扩展来源），
/// /ingest 与 /ui/open 必须带 X-Gallery-Token；ingest_id 幂等；下载未完成直接拒绝。
/// </summary>
public sealed partial class BridgeServer : IDisposable
{
    public const int DefaultPort = 47821;
    public const int PortTries = 6;
    private const int MaxBody = 512 * 1024;
    private const string ProtocolVersion = "2";

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly StoreService _store;
    private readonly string _version;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private Task? _acceptTask;
    private readonly HashSet<Task> _clients = [];

    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public int Received { get; private set; }
    public int Probes { get; private set; }
    public DateTimeOffset LastContact { get; private set; } = DateTimeOffset.MinValue;
    public bool Running => _listener is not null;

    /// <summary>收到一条新记录（已落库）。</summary>
    public event Action<int, string>? Ingested;
    /// <summary>扩展请求打开界面：view = library | pending，path 可空。</summary>
    public event Action<string, string>? UiRequested;

    public BridgeServer(StoreService store, string version)
    {
        _store = store;
        _version = version;
    }

    public bool BrowserConnected =>
        Running && DateTimeOffset.UtcNow - LastContact < TimeSpan.FromSeconds(90);

    public bool Start(int preferredPort, string token)
    {
        lock (_gate)
        {
            if (Running) return true;
            Token = token;
            for (var i = 0; i < PortTries; i++)
            {
                var port = preferredPort + i;
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    break;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }

            if (_listener is null) return false;

            _cts = new CancellationTokenSource();
            var activeListener = _listener;
            var shutdownToken = _cts.Token;
            _acceptTask = Task.Run(() => AcceptLoopAsync(activeListener, shutdownToken));
            return true;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                continue;
            }

            var task = Task.Run(() => HandleAsync(client, token), CancellationToken.None);
            lock (_gate) _clients.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_gate) _clients.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        token = timeout.Token;
        using (client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            try
            {
                using var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, token).ConfigureAwait(false);
                if (head is null) return;

                var (method, path, headers) = head.Value;
                var origin = headers.GetValueOrDefault("origin", "");
                var host = headers.GetValueOrDefault("host", "");

                if (!IsLocalHost(host))
                {
                    await DrainAsync(stream, headers, token).ConfigureAwait(false);
                    await WriteAsync(stream, 403, JsonError("只接受本机访问"), token).ConfigureAwait(false);
                    return;
                }

                if (origin.Length > 0 && !IsExtensionOrigin(origin))
                {
                    await DrainAsync(stream, headers, token).ConfigureAwait(false);
                    await WriteAsync(stream, 403, JsonError("只接受浏览器扩展来源"), token).ConfigureAwait(false);
                    return;
                }

                if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, 204, "", token, origin).ConfigureAwait(false);
                    return;
                }

                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/ping")
                {
                    Touch();
                    var pending = _store.PendingIngestCount();
                    var body = "{" +
                               "\"ok\":true,\"app\":\"gallery\"," +
                               $"\"version\":{JsonSerializer.Serialize(_version, JsonOut)}," +
                               $"\"port\":{Port},\"tokenRequired\":true," +
                               $"\"received\":{Received},\"pending\":{pending}," +
                               $"\"protocol\":{ProtocolVersion}}}";
                    await WriteAsync(stream, 200, body, token, origin).ConfigureAwait(false);
                    return;
                }

                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/tags")
                {
                    Touch();
                    await WriteAsync(stream, 200, BuildTagIndex(), token, origin).ConfigureAwait(false);
                    return;
                }

                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/sources")
                {
                    Touch();
                    await WriteAsync(stream, 200, BuildSourceBackfillQueue(), token, origin).ConfigureAwait(false);
                    return;
                }

                if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                    (path != "/ingest" && path != "/ui/open" && path != "/sources"))
                {
                    await DrainAsync(stream, headers, token).ConfigureAwait(false);
                    await WriteAsync(stream, 404, JsonError("未知路径"), token, origin).ConfigureAwait(false);
                    return;
                }

                var provided = headers.GetValueOrDefault("x-gallery-token", "");
                if (!TokenMatches(provided))
                {
                    await DrainAsync(stream, headers, token).ConfigureAwait(false);
                    await WriteAsync(stream, 401,
                        JsonError("令牌无效，请在插件设置页重新粘贴"), token, origin).ConfigureAwait(false);
                    return;
                }

                var length = ParseLength(headers);
                if (length < 0 || length > MaxBody)
                {
                    await WriteAsync(stream, 413, JsonError("请求体过大"), token, origin).ConfigureAwait(false);
                    return;
                }

                var raw = await ReadBodyAsync(stream, length, token).ConfigureAwait(false);
                Touch();

                if (path == "/ui/open")
                {
                    HandleUiOpen(raw);
                    await WriteAsync(stream, 200, "{\"ok\":true}", token, origin).ConfigureAwait(false);
                    return;
                }

                if (path == "/sources")
                {
                    await WriteAsync(stream, 200, HandleSourceBackfill(raw), token, origin).ConfigureAwait(false);
                    return;
                }

                var (status, ingestBody) = HandleIngest(raw);
                await WriteAsync(stream, status, ingestBody, token, origin).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    private void HandleUiOpen(string raw)
    {
        var view = "library";
        var path = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("view", out var v) && v.ValueKind == JsonValueKind.String)
                    view = v.GetString() is { Length: > 0 } s ? s : view;
                if (doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                    path = p.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            return;
        }

        UiRequested?.Invoke(view, path);
    }

    /// <summary>扩展要查的 md5 清单：只列缺来源、且文件名带 danbooru md5 的图（按 md5 去重）。</summary>
    private string BuildSourceBackfillQueue()
    {
        var items = _store.PendingSourceBackfill();
        var total = items.Sum(item => item.Count);
        var sb = new StringBuilder();
        sb.Append("{\"ok\":true,\"total\":").Append(total).Append(",\"items\":[");
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"md5\":").Append(JsonSerializer.Serialize(items[i].Md5, JsonOut))
                .Append(",\"name\":").Append(JsonSerializer.Serialize(items[i].Name, JsonOut))
                .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>扩展查到出处后回写：只填空值，已有来源不覆盖。</summary>
    private string HandleSourceBackfill(string raw)
    {
        var rows = new List<(string Md5, string PageUrl, string SourceUrl, string PostId)>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    rows.Add((Prop(item, "md5"), Prop(item, "page_url"), Prop(item, "source_url"), Prop(item, "post_id")));
                }
            }
        }
        catch (JsonException)
        {
            return JsonError("请求体不是合法 JSON");
        }

        var updated = _store.ApplySourceBackfill(rows);
        Touch();
        return "{\"ok\":true,\"received\":" + rows.Count + ",\"updated\":" + updated + "}";
    }

    private static string Prop(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    private (int Status, string Body) HandleIngest(string raw)
    {        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return (400, JsonError("请求体不是合法 JSON"));
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (400, JsonError("请求体不是合法 JSON"));

            var payload = Normalize(doc.RootElement);

            if (payload.Probe)
            {
                Probes++;
                return (200, "{\"ok\":true,\"probe\":true}");
            }

            if (!payload.DownloadComplete)
                return (400, JsonError("下载尚未完成，不会归档"));

            var result = _store.IngestPush(payload.Raw, payload.InboxName, payload.InboxPath,
                payload.PageUrl, payload.ImageUrl, payload.PostId, payload.Md5, payload.Rating,
                payload.Source, payload.IngestId);

            if (!result.Duplicate) Received++;

            var body = "{" +
                       "\"ok\":true,\"persisted\":true," +
                       $"\"queued\":{result.QueueId}," +
                       $"\"duplicate\":{(result.Duplicate ? "true" : "false")}," +
                       $"\"ingest_id\":{JsonSerializer.Serialize(payload.IngestId, JsonOut)}," +
                       $"\"name\":{JsonSerializer.Serialize(payload.InboxName, JsonOut)}}}";

            if (!result.Duplicate) Ingested?.Invoke(result.QueueId, payload.InboxName);
            return (200, body);
        }
    }

    private sealed record NormalizedPayload(
        bool Probe,
        bool DownloadComplete,
        string InboxName,
        string InboxPath,
        string PageUrl,
        string ImageUrl,
        string PostId,
        string Md5,
        string Rating,
        string Source,
        string IngestId,
        string Raw);

    private static NormalizedPayload Normalize(JsonElement root)
    {
        string Str(string name, int limit = 0)
        {
            if (!root.TryGetProperty(name, out var v)) return "";
            var s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.ToString(),
                JsonValueKind.True => "true",
                _ => ""
            };
            s = s.Trim();
            if (limit > 0 && s.Length > limit) s = s[..limit];
            return s;
        }

        var probe = root.TryGetProperty("probe", out var pv) && pv.ValueKind == JsonValueKind.True;

        var inboxName = Str("inbox_name", 260);
        if (inboxName.Length == 0)
        {
            var filename = Str("filename", 260);
            inboxName = filename.Length > 0 ? Path.GetFileName(filename) : "";
        }

        var md5 = Str("md5", 64).ToLowerInvariant();
        if (!Md5Pattern().IsMatch(md5)) md5 = "";

        var downloadState = Str("download_state", 32);
        if (downloadState.Length == 0) downloadState = "complete";
        var inboxPath = Str("inbox_path", 1024);
        var downloadFilename = Str("filename", 1024);
        if (inboxPath.Length == 0 && Path.IsPathFullyQualified(downloadFilename))
            inboxPath = downloadFilename;

        return new NormalizedPayload(
            probe,
            downloadState.Equals("complete", StringComparison.OrdinalIgnoreCase),
            inboxName,
            inboxPath,
            Str("page_url", 2048),
            Str("image_url", 2048),
            Str("post_id", 64),
            md5,
            Str("rating", 32),
            Str("source", 120),
            Str("ingest_id", 120),
            root.GetRawText());
    }

    private bool TokenMatches(string provided)
    {
        if (Token.Length == 0) return provided.Length == 0;
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(Token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool IsLocalHost(string host)
    {
        if (host.Length == 0) return false;
        var name = host;
        var colon = name.LastIndexOf(':');
        if (colon > 0) name = name[..colon];
        name = name.Trim('[', ']').Trim().ToLowerInvariant();
        return name is "127.0.0.1" or "localhost" or "::1";
    }

    private static bool IsExtensionOrigin(string origin)
    {
        var o = origin.ToLowerInvariant();
        return o.StartsWith("chrome-extension://", StringComparison.Ordinal)
               || o.StartsWith("moz-extension://", StringComparison.Ordinal)
               || o.StartsWith("safari-web-extension://", StringComparison.Ordinal);
    }

    private static int ParseLength(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("content-length", out var raw)) return 0;
        return int.TryParse(raw, out var n) && n >= 0 ? n : -1;
    }

    private void Touch() => LastContact = DateTimeOffset.UtcNow;

    private static string JsonError(string message) =>
        "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(message, JsonOut) + "}";

    /// <summary>
    /// 给扩展用的标签词表：作品与角色的规范名 + 别名（含中文名、英文名和繁体写法）。
    /// 扩展据此过滤 Pixiv 标签，命中才写进记录；繁简差异靠这里补齐，扩展侧不带字表。
    /// </summary>
    private string BuildTagIndex()
    {
        List<StoreService.TagInfo> tags;
        try
        {
            lock (AppService.DbLock) tags = _store.AllTags();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return JsonError("读取标签失败");
        }

        var works = new List<string>();
        var characters = new List<string>();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (tag.Category == "work") works.Add(tag.Name);
            else if (tag.Category == "character") characters.Add(tag.Name);
            else continue;

            void AddAlias(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var trimmed = value.Trim();
                aliases.TryAdd(trimmed, tag.Name);
                var traditional = ToTraditional(trimmed);
                if (traditional.Length > 0 && !string.Equals(traditional, trimmed, StringComparison.Ordinal))
                    aliases.TryAdd(traditional, tag.Name);
            }

            foreach (var alias in tag.Aliases) AddAlias(alias);
            AddAlias(tag.ZhName);
            AddAlias(tag.EnName);
        }

        return JsonSerializer.Serialize(new { ok = true, works, characters, aliases }, JsonOut);
    }

    /// <summary>简体 → 繁体（Windows NLS）。取不到就返回空串，调用方跳过。</summary>
    private static string ToTraditional(string text)
    {
        if (text.Length == 0) return "";
        try
        {
            var buffer = new char[text.Length * 2 + 8];
            var written = LCMapStringEx(null, LcmapTraditionalChinese, text, text.Length,
                buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return written > 0 ? new string(buffer, 0, written) : "";
        }
        catch { return ""; }
    }

    private const uint LcmapTraditionalChinese = 0x0400_0000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(string? localeName, uint mapFlags, string src, int srcLen,
        [Out] char[] dest, int destLen, IntPtr versionInfo, IntPtr reserved, IntPtr sortHandle);

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers)?> ReadHeadAsync(
        NetworkStream stream, CancellationToken token)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(one, token).ConfigureAwait(false);
            if (read == 0) return null;
            buffer.Add(one[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
                break;
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var lines = text.Split("\r\n");
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;
        var method = parts[0];
        var path = parts[1];
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return (method, path, headers);
    }

    /// <summary>
    /// 拒绝请求前把请求体读掉：不读就关连接会让客户端收到 RST，
    /// 而不是我们刚写的 401/403 响应（浏览器插件就看不到错误原因）。
    /// </summary>
    private static async Task DrainAsync(NetworkStream stream,
        Dictionary<string, string> headers, CancellationToken token)
    {
        var length = ParseLength(headers);
        if (length <= 0) return;

        var remaining = Math.Min(length, MaxBody + 4096);
        var buffer = new byte[8192];
        try
        {
            while (remaining > 0)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                remaining -= read;
            }
        }
        catch (IOException)
        {
            // 对端已经半关闭，忽略即可
        }
    }

    private static async Task<string> ReadBodyAsync(NetworkStream stream, int length, CancellationToken token)
    {
        if (length <= 0) return "";
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), token).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, offset);
    }

    private async Task WriteAsync(NetworkStream stream, int status, string body, CancellationToken token,
        string origin = "")
    {
        var reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            413 => "Payload Too Large",
            _ => "OK"
        };

        var bytes = Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        if (origin.Length > 0)
        {
            sb.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n");
            sb.Append("Access-Control-Allow-Headers: content-type, x-gallery-token\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Private-Network: true\r\n");
        }

        sb.Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head, token).ConfigureAwait(false);
        if (bytes.Length > 0) await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public async Task StopAsync()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
        }
        if (_acceptTask is not null) await _acceptTask;
        Task[] clients;
        lock (_gate) clients = _clients.ToArray();
        try { await Task.WhenAll(clients); }
        catch (OperationCanceledException) { }
        Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch (Exception ex) { Debug.WriteLine(ex); }
            try { _listener?.Stop(); } catch (Exception ex) { Debug.WriteLine(ex); }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Md5Pattern();
}
