using Gallery.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Gallery.Services;

public enum ImagePriority { Preview, Visible, Prefetch }

/// <summary>UI 线程使用的统一加载入口：两条后台工作槽、可见优先、分池缓存、192MiB 解码准入预算；毛玻璃只使用256像素底图。</summary>
public static class ImageLoader
{
    private enum Purpose { Thumbnail, ProtectedBase, ProtectedThumbnail, Preview }
    private readonly record struct Key(string Path, double Mtime, long Size, int Edge, int Blur, Purpose Purpose);
    private sealed record Pixels(byte[] Bytes, int Width, int Height);
    private sealed record Entry(Key Key, BitmapSource? Bitmap, Pixels? Pixels, long Bytes);
    private sealed class Request(Key key, PhotoItem item, ImagePriority priority)
    {
        public readonly Key Key = key;
        public readonly PhotoItem Item = item;
        public ImagePriority Priority = priority;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<BitmapSource> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Users;
        public bool Running;
        public long Weight;
    }
    /// <summary>验证实际工作量，不记录原图路径或内容。</summary>
    public sealed record Statistics(long CacheHits, long Decodes, long Builds, long BaseReads, int Queued,
        int Active, int PeakActive, int PeakThumbnailRequests, long ScratchBytes, long ThumbnailBytes,
        long PreviewBytes, long DecodeMilliseconds, long UploadMilliseconds);
    private const long PoolBytes = 64L * 1024 * 1024, ScratchLimit = 192L * 1024 * 1024;
    private static readonly Dictionary<Key, Request> Requests = new();
    private static readonly Dictionary<Key, LinkedListNode<Entry>> Cache = new();
    private static readonly LinkedList<Entry> Thumbnails = new(), Previews = new();
    private static readonly Dictionary<Key, Task<Pixels>> BaseLoads = new();
    private static HashSet<string> _visible = new(StringComparer.Ordinal), _desired = new(StringComparer.Ordinal);
    private static DispatcherQueue? _dispatcher;
    private static bool _pumpQueued, _galleryActive = true, _previewOpen, _resizing;
    private static int _active, _previewActive, _peakActive, _peakRequests;
    private static long _thumbnailBytes, _previewBytes, _scratch, _hits, _decodes, _builds, _baseReads, _decodeMs, _uploadMs;
    public static event Action? WorkCompleted;
    public static Statistics Snapshot() => new(_hits, _decodes, _builds, _baseReads,
        Requests.Values.Count(r => !r.Running), _active, _peakActive, _peakRequests, _scratch,
        _thumbnailBytes, _previewBytes, _decodeMs, _uploadMs);

    public static int ThumbnailEdge(PhotoItem item, double pixels)
    {
        if (IsHidden(item)) return 0;
        if (BlurStrength(item) > 0) return 256;
        int edge = new[] { 512, 1024, 2048, 4096 }.FirstOrDefault(n => n >= pixels, 4096);
        int native = Math.Max(item.PixelWidth, item.PixelHeight);
        return native > 0 ? Math.Min(native, edge) : edge;
    }
    public static int BlurStrength(PhotoItem item) => item.NeedsProtection && AppService.Settings.Settings.nsfw_display == "blur"
        ? AppService.Settings.Settings.nsfw_blur_strength : 0;
    public static bool IsHidden(PhotoItem item) => item.NeedsProtection && AppService.Settings.Settings.nsfw_display == "hide";
    public static string Identity(PhotoItem item) => FileIdentity.PathKey(item.FullPath);
    private static Key MakeKey(PhotoItem item, int edge, int blur, Purpose purpose)
        => new(Identity(item), item.ModifiedAtUnix ?? 0, item.FileSize, edge, blur, purpose);

    public static void SetViewport(IEnumerable<PhotoItem> visible, IEnumerable<PhotoItem> nearby)
    {
        _visible = visible.Select(Identity).ToHashSet(StringComparer.Ordinal);
        _desired = nearby.Select(Identity).ToHashSet(StringComparer.Ordinal);
        _desired.UnionWith(_visible);
        foreach (var request in Requests.Values.ToArray())
        {
            if (request.Key.Purpose == Purpose.Preview) continue;
            if (!request.Running && (!_galleryActive || !_desired.Contains(request.Key.Path))) CancelQueued(request);
            else request.Priority = _visible.Contains(request.Key.Path) ? ImagePriority.Visible : ImagePriority.Prefetch;
        }
        SchedulePump();
    }
    public static void SetGalleryActive(bool active) { _galleryActive = active; if (!active) SetViewport([], []); SchedulePump(); }
    public static void SetPreviewOpen(bool open) { _previewOpen = open; SchedulePump(); }
    public static void SetResizing(bool resizing) { _resizing = resizing; SchedulePump(); }

    public static BitmapSource? FindThumbnail(PhotoItem item, int target, int blur)
    {
        var purpose = blur > 0 ? Purpose.ProtectedThumbnail : Purpose.Thumbnail;
        var key = MakeKey(item, target, blur, purpose);
        var found = Cache.Values.Where(n => n.Value.Key.Path == key.Path && n.Value.Key.Mtime == key.Mtime
            && n.Value.Key.Size == key.Size && n.Value.Key.Purpose == purpose && n.Value.Key.Blur == blur)
            .OrderBy(n => n.Value.Key.Edge >= target ? 0 : 1).ThenBy(n => Math.Abs(n.Value.Key.Edge - target)).FirstOrDefault();
        if (found is null) return null;
        Touch(found); _hits++;
        return found.Value.Bitmap;
    }
    public static Task<BitmapSource> LoadThumbnailAsync(PhotoItem item, int edge, int blur, CancellationToken token, ImagePriority priority = ImagePriority.Visible)
    {
        if (IsHidden(item)) return Task.FromCanceled<BitmapSource>(new CancellationToken(true));
        if (blur > 0) edge = 256;
        return LoadAsync(item, edge, blur, blur > 0 ? Purpose.ProtectedThumbnail : Purpose.Thumbnail, priority, token);
    }
    public static Task<BitmapSource> LoadPreviewAsync(PhotoItem item, int edge, CancellationToken token)
        => LoadAsync(item, edge > 0 ? edge : 2048, 0, Purpose.Preview, ImagePriority.Preview, token);

    private static async Task<BitmapSource> LoadAsync(PhotoItem item, int edge, int blur, Purpose purpose, ImagePriority priority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _dispatcher ??= DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("图片请求必须由 UI 线程提交");
        var key = MakeKey(item, edge, blur, purpose);
        if (Cache.TryGetValue(key, out var cached)) { Touch(cached); _hits++; return cached.Value.Bitmap!; }
        if (purpose == Purpose.Preview && Requests.TryGetValue(key, out var previous) && previous.Cancellation.IsCancellationRequested)
        {
            // 快速关闭再打开同一张图时，不能加入已经取消的旧解码。
            try { await previous.Completion.Task.WaitAsync(token); }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
            return await LoadAsync(item, edge, blur, purpose, priority, token);
        }
        if (!Requests.TryGetValue(key, out var request))
        {
            if (purpose != Purpose.Preview && Requests.Values.Count(r => r.Key.Purpose != Purpose.Preview) >= 96)
            {
                var victim = Requests.Values.LastOrDefault(r => !r.Running && r.Priority == ImagePriority.Prefetch);
                if (victim is null || priority == ImagePriority.Prefetch) throw new OperationCanceledException("图片队列已满");
                CancelQueued(victim);
            }
            request = new Request(key, item, priority); Requests.Add(key, request);
            _peakRequests = Math.Max(_peakRequests, Requests.Values.Count(r => r.Key.Purpose != Purpose.Preview));
        }
        if (priority < request.Priority) request.Priority = priority;
        request.Users++; SchedulePump();
        try { return await request.Completion.Task.WaitAsync(token); }
        finally
        {
            if (--request.Users == 0 && !request.Completion.Task.IsCompleted)
            {
                if (!request.Running) CancelQueued(request);
                else if (purpose == Purpose.Preview) request.Cancellation.Cancel();
            }
        }
    }
    private static void CancelQueued(Request request)
    {
        if (request.Running || !Requests.TryGetValue(request.Key, out var current) || !ReferenceEquals(current, request)) return;
        Requests.Remove(request.Key); request.Completion.TrySetCanceled(); request.Cancellation.Dispose();
    }
    private static void SchedulePump()
    {
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();
        if (_pumpQueued || _dispatcher is null) return;
        _pumpQueued = true;
        _dispatcher.TryEnqueue(() => { _pumpQueued = false; Pump(); });
    }
    private static void Pump()
    {
        while (_active < 2)
        {
            var next = Requests.Values.Where(r => !r.Running && (r.Key.Purpose != Purpose.Preview || _previewActive == 0)
                && (r.Priority != ImagePriority.Prefetch || (_galleryActive && !_previewOpen && _previewActive == 0 && !_resizing)))
                .OrderBy(r => r.Priority).FirstOrDefault(r => _scratch + Estimate(r) <= ScratchLimit);
            if (next is null) break;
            next.Running = true; next.Weight = Estimate(next); _scratch += next.Weight;
            _active++; _peakActive = Math.Max(_peakActive, _active);
            if (next.Key.Purpose == Purpose.Preview) _previewActive++;
            _ = ExecuteAsync(next);
        }
    }
    private static long Estimate(Request request)
    {
        if (Cache.ContainsKey(request.Key with { Edge = 256, Blur = 0, Purpose = Purpose.ProtectedBase })) return 256L * 256 * 8;
        // 视频这里只解系统封面（<= edge），不按原始分辨率占解码预算。
        if (request.Item.IsVideo) return 512L * 512 * 8;
        long width = request.Item.PixelWidth, height = request.Item.PixelHeight;
        return width <= 0 || height <= 0 ? ScratchLimit : Math.Clamp(width * height * 8, 1, ScratchLimit);
    }
    private static async Task ExecuteAsync(Request request)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var key = request.Key;
            Pixels pixels;
            if (key.Purpose == Purpose.ProtectedThumbnail)
            {
                var baseKey = key with { Edge = 256, Blur = 0, Purpose = Purpose.ProtectedBase };
                if (Cache.TryGetValue(baseKey, out var entry)) { Touch(entry); _hits++; pixels = entry.Value.Pixels!; }
                else
                {
                    if (!BaseLoads.TryGetValue(baseKey, out var loading))
                    {
                        _baseReads++; loading = Task.Run(() => ReadPixelsAsync(request.Item, 256, true, CancellationToken.None));
                        BaseLoads.Add(baseKey, loading);
                    }
                    try { pixels = await loading; Store(new Entry(baseKey, null, pixels, pixels.Bytes.LongLength)); }
                    finally { if (BaseLoads.TryGetValue(baseKey, out var current) && ReferenceEquals(current, loading)) BaseLoads.Remove(baseKey); }
                }
                var source = pixels; pixels = await Task.Run(() => Blur(source, key.Blur));
            }
            else pixels = await Task.Run(() => ReadPixelsAsync(request.Item, key.Edge, key.Purpose == Purpose.Thumbnail, request.Cancellation.Token));
            _decodeMs += watch.ElapsedMilliseconds;
            request.Cancellation.Token.ThrowIfCancellationRequested(); watch.Restart();
            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
            using (var stream = bitmap.PixelBuffer.AsStream()) await stream.WriteAsync(pixels.Bytes);
            bitmap.Invalidate(); _uploadMs += watch.ElapsedMilliseconds;
            Store(new Entry(key, bitmap, null, pixels.Bytes.LongLength)); request.Completion.TrySetResult(bitmap);
        }
        catch (OperationCanceledException) { request.Completion.TrySetCanceled(); }
        catch (Exception ex) { request.Completion.TrySetException(ex); }
        finally
        {
            Requests.Remove(request.Key); _active--; _scratch -= request.Weight;
            if (request.Key.Purpose == Purpose.Preview) _previewActive--;
            request.Cancellation.Dispose(); SchedulePump(); WorkCompleted?.Invoke();
        }
    }
    private static async Task<Pixels> ReadPixelsAsync(PhotoItem item, int edge, bool thumbnail, CancellationToken token)
    {
        string path = item.FullPath;
        var scanner = AppService.Scanner;
        if (item.IsVideo && scanner is not null)
        {
            // 视频本身没有可交给 BitmapDecoder 的静态帧；统一走系统封面缓存。
            // 正常预览由 VideoViewer 接管，这里主要服务卡片缩略图。
            double mtime = item.ModifiedAtUnix ?? StoreService.ToUnixSeconds(File.GetLastWriteTimeUtc(path));
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _builds);
            path = scanner.Thumbs.GetOrBuildVideo(path, mtime, item.FileSize, edge)
                ?? throw new IOException("视频封面不可用");
        }
        else if (thumbnail && scanner is not null)
        {
            double mtime = item.ModifiedAtUnix ?? StoreService.ToUnixSeconds(File.GetLastWriteTimeUtc(path));
            var destination = scanner.Thumbs.CachePath(path, mtime, item.FileSize, edge);
            var smaller = edge == 256 ? scanner.Thumbs.Get(path, mtime, item.FileSize) : null;
            if (File.Exists(destination)) path = destination;
            else if (smaller is not null) path = smaller;
            else
            {
                token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _builds);
                path = scanner.Thumbs.GetOrBuild(path, mtime, item.FileSize, edge) ?? path;
            }
        }
        token.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var input = await file.OpenReadAsync(); var decoder = await BitmapDecoder.CreateAsync(input);
        double scale = Math.Min(1, (double)edge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)), height = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var operation = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var registration = token.Register(operation.Cancel);
        var bytes = (await operation).DetachPixelData(); token.ThrowIfCancellationRequested();
        if (decoder.OrientedPixelWidth != decoder.PixelWidth) (width, height) = (height, width);
        Interlocked.Increment(ref _decodes);
        return new Pixels(bytes, (int)width, (int)height);
    }
    private static Pixels Blur(Pixels source, int strength)
    {
        var configuration = Configuration.Default.Clone(); configuration.MaxDegreeOfParallelism = 1;
        using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(configuration, source.Bytes, source.Width, source.Height);
        image.Mutate(c => c.GaussianBlur(Math.Min(source.Width, source.Height) * .12f * Math.Clamp(strength, 0, 100) / 100f));
        var output = new byte[source.Bytes.Length]; image.CopyPixelDataTo(output);
        return new Pixels(output, source.Width, source.Height);
    }
    private static void Touch(LinkedListNode<Entry> node)
    {
        var list = node.Value.Key.Purpose == Purpose.Preview ? Previews : Thumbnails;
        list.Remove(node); list.AddLast(node);
    }
    private static void Store(Entry entry)
    {
        if (Cache.TryGetValue(entry.Key, out var existing)) { Touch(existing); return; }
        if (entry.Bytes > PoolBytes) return;
        bool preview = entry.Key.Purpose == Purpose.Preview;
        var list = preview ? Previews : Thumbnails;
        Cache.Add(entry.Key, list.AddLast(entry));
        if (preview) _previewBytes += entry.Bytes; else _thumbnailBytes += entry.Bytes;
        while ((preview ? _previewBytes : _thumbnailBytes) > PoolBytes || list.Count > (preview ? 3 : 256))
        {
            var victim = list.First!;
            if (!preview)
                for (var node = list.First; node is not null; node = node.Next)
                    if (!_visible.Contains(node.Value.Key.Path)) { victim = node; break; }
            if (preview) _previewBytes -= victim.Value.Bytes; else _thumbnailBytes -= victim.Value.Bytes;
            Cache.Remove(victim.Value.Key); list.Remove(victim);
        }
    }
}
