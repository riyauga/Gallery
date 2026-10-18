using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Gallery.Models;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gallery.Controls.Review;

/// <summary>以草图原有的本地 CSS 圆环渲染预览，原生页保留选中、保护、右键与归档契约。</summary>
internal sealed class ReviewCarousel : Grid
{
    private readonly WebView2 _browser = new() { DefaultBackgroundColor = Microsoft.UI.Colors.Transparent };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Dictionary<int, Preview> _previews = [];
    private readonly Dictionary<int, string> _sentSources = [];
    private readonly HashSet<int> _revealed = [];
    private IReadOnlyList<StoreService.PendingIngestRow> _rows = [];
    private CancellationTokenSource? _loading;
    private int _index;
    private bool _active = true, _initializing, _ready;
    private double _angle = 10, _overlap = 67, _size = 95;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public event Action<int>? SelectionRequested;
    public event Action<PhotoItem>? SelectedImageLoaded;
    public event Action<StoreService.PendingIngestRow, bool>? OpenRequested;

    public ReviewCarousel()
    {
        MinHeight = 320;
        Children.Add(_error);
        if (AppService.Settings.Settings.layout_options.TryGetValue("review_carousel", out var saved) && saved.ValueKind == JsonValueKind.Object)
        {
            _angle = ReadNumber(saved, "angle", 10, 8, 17);
            _overlap = ReadNumber(saved, "overlap", 67, 0, 95);
            _size = ReadNumber(saved, "size", 95, 50, 95);
        }
        Loaded += async (_, _) => await InitializeAsync();
        Unloaded += (_, _) => SetActive(false);
        ActualThemeChanged += (_, _) => PublishState();
    }

    private async Task InitializeAsync()
    {
        if (_initializing || _ready) return;
        _initializing = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", Path.Combine(AppPaths.LocalDataDir, "review-webview"), null);
            // 加入可视树后 WebView2 会自动初始化；在同一 UI 回合内先绑定指定环境，避免默认环境抢先创建。
            if (_browser.Parent is null) Children.Insert(0, _browser);
            await _browser.EnsureCoreWebView2Async(environment);
            var core = _browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                // 仅初始化期间允许 NavigateToString 的内部导航；页面就绪后不再接受任何导航。
                args.Cancel = _ready;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += OnMessage;
            using var resource = typeof(ReviewCarousel).Assembly.GetManifestResourceStream("Gallery.Controls.Review.ReviewCarousel.html")!;
            using var reader = new StreamReader(resource);
            core.NavigateToString(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            _error.Text = "无法初始化预览：" + ex.Message;
            _error.Visibility = Visibility.Visible;
            AppService.DebugLog("review renderer: " + ex);
        }
        finally { _initializing = false; }
    }

    public void SetItems(IReadOnlyList<StoreService.PendingIngestRow> rows, int index)
    {
        _rows = rows;
        _index = rows.Count == 0 ? 0 : Math.Clamp(index, 0, rows.Count - 1);
        PublishState();
        StartLoads();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (!active) { _loading?.Cancel(); _previews.Clear(); _revealed.Clear(); }
        PublishState();
        if (active) StartLoads();
    }

    public void RefreshPreview()
    {
        _revealed.Clear(); _previews.Clear();
        PublishState(); StartLoads();
    }

    private IEnumerable<(StoreService.PendingIngestRow Row, int Index)> Nearby()
        => Enumerable.Range(Math.Max(0, _index - 4), Math.Max(0, Math.Min(_rows.Count, _index + 5) - Math.Max(0, _index - 4)))
            .Select(index => (_rows[index], index));

    private bool Protected(StoreService.PendingIngestRow row)
        => AppService.Settings.Settings.blur_nsfw && ArchiveService.NormalizeRating(row.Rating) is not ("g" or "s") && !_revealed.Contains(row.Id);

    private string PreviewKey(StoreService.PendingIngestRow row, int index)
        => $"{IngestReview.LocalPath(row)}|{Protected(row)}|{(index == _index ? 2048 : 768)}";

    private void PublishState()
    {
        if (!_ready) return;
        var visibleIds = !_active ? [] : Nearby().Select(entry => entry.Row.Id).ToHashSet();
        foreach (var id in _sentSources.Keys.Where(id => !visibleIds.Contains(id)).ToArray()) _sentSources.Remove(id);
        var cards = !_active ? [] : Nearby().Select(entry =>
        {
            var (row, index) = entry;
            var cached = _previews.GetValueOrDefault(row.Id);
            // 分级保护切换后不能显示旧的清晰缓存，尺寸变化则可先沿用同一保护等级的像素。
            var preview = cached is not null && cached.Protected == Protected(row) ? cached : null;
            var source = preview?.Source ?? "";
            var changed = !_sentSources.TryGetValue(row.Id, out var sent) || !ReferenceEquals(source, sent);
            _sentSources[row.Id] = source;
            return new { row.Id, Index = index, row.InboxName, Src = changed ? source : null, Ratio = preview?.Ratio ?? 1,
                Blocked = Protected(row), Status = preview?.Status ?? "正在加载…" };
        }).ToArray();
        _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "state", cards, index = _index, count = _rows.Count,
            design = new { angle = _angle, overlap = _overlap, size = _size, aspect = 16.0 / 9 },
            theme = ActualTheme == ElementTheme.Dark ? "dark" : "light",
            background = SurfaceColor(),
            motion = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
        }, Json));
    }

    /// <summary>读取实际承载层的填充色，补上 WebView 透明表面绕过的 NavigationView 内容底色。</summary>
    private string SurfaceColor()
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            var brush = parent switch
            {
                Panel panel => panel.Background,
                Border border => border.Background,
                ContentPresenter presenter => presenter.Background,
                Control control => control.Background,
                _ => null,
            };
            if (brush is not SolidColorBrush { Color.A: > 0 } solid) continue;
            var color = solid.Color;
            return FormattableString.Invariant($"rgba({color.R},{color.G},{color.B},{color.A / 255.0 * solid.Opacity:0.######})");
        }
        return "transparent";
    }

    private void StartLoads()
    {
        _loading?.Cancel(); _loading?.Dispose();
        _loading = new CancellationTokenSource();
        if (!_active || !_ready) return;
        var entries = Nearby().OrderBy(entry => Math.Abs(entry.Index - _index)).ToArray();
        var ids = entries.Select(entry => entry.Row.Id).ToHashSet();
        foreach (var id in _previews.Keys.Where(id => !ids.Contains(id)).ToArray()) _previews.Remove(id);
        foreach (var (row, index) in entries)
        {
            var key = PreviewKey(row, index);
            if (_previews.TryGetValue(row.Id, out var cached) && cached.Key == key)
            {
                if (index == _index && cached.Item is not null) SelectedImageLoaded?.Invoke(cached.Item);
                continue;
            }
            _ = LoadAsync(row, index, key, _loading.Token);
        }
    }

    private async Task LoadAsync(StoreService.PendingIngestRow row, int index, string key, CancellationToken token)
    {
        var blocked = Protected(row);
        try
        {
            var path = IngestReview.LocalPath(row);
            if (path.Length == 0 || !File.Exists(path))
            {
                _previews[row.Id] = new Preview(key, "", 1, blocked, "本地文件不可用", null);
                PublishState(); return;
            }
            var item = await Task.Run(() =>
            {
                var meta = ImageInfo.ReadMeta(path);
                var file = new FileInfo(path);
                return new PhotoItem { File = file.Name, FullPath = path, PixelWidth = meta.Width ?? 0, PixelHeight = meta.Height ?? 0,
                    Date = file.LastWriteTime, FileSize = file.Length, ModifiedAtUnix = StoreService.ToUnixSeconds(file.LastWriteTimeUtc), Tags = [("rating", "SFW")] };
            }, token);
            // 敏感／未知图片在揭示前只交付已模糊像素；本地网页不接触原文件路径。
            var bitmap = (WriteableBitmap)(blocked ? await ImageLoader.LoadThumbnailAsync(item, 256, 100, token)
                : index == _index ? await ImageLoader.LoadPreviewAsync(item, 2048, token)
                : await ImageLoader.LoadThumbnailAsync(item, 768, 0, token));
            token.ThrowIfCancellationRequested();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bitmap.PixelBuffer.ToArray());
            await encoder.FlushAsync();
            token.ThrowIfCancellationRequested();
            stream.Seek(0);
            using var bytes = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(bytes, token);
            token.ThrowIfCancellationRequested();
            _previews[row.Id] = new Preview(key, "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray()), (double)bitmap.PixelWidth / bitmap.PixelHeight, blocked, "", item);
            PublishState();
            if (index == _index && _rows.Count > 0 && _rows[_index].Id == row.Id) SelectedImageLoaded?.Invoke(item);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        {
            if (token.IsCancellationRequested) return;
            _previews[row.Id] = new Preview(key, "", 1, blocked, "图片无法读取", null);
            PublishState(); AppService.DebugLog("review preview: " + ex.Message);
        }
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != "about:blank") return;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var message = document.RootElement;
            var type = message.GetProperty("type").GetString();
            if (type == "ready") { _ready = true; PublishState(); StartLoads(); return; }
            if (!_active) return;
            if (type == "configure")
            {
                _angle = ReadNumber(message, "angle", _angle, 8, 17);
                _overlap = ReadNumber(message, "overlap", _overlap, 0, 95);
                _size = ReadNumber(message, "size", _size, 50, 95);
                AppService.Settings.Settings.layout_options["review_carousel"] = JsonSerializer.SerializeToElement(new { angle = _angle, overlap = _overlap, size = _size });
                AppService.Settings.Save(); return;
            }
            if (type == "step")
            {
                if (_rows.Count > 0) SelectionRequested?.Invoke(Math.Clamp(_index + Math.Sign(message.GetProperty("delta").GetInt32()), 0, _rows.Count - 1));
                return;
            }
            var id = message.GetProperty("id").GetInt32();
            var index = _rows.ToList().FindIndex(row => row.Id == id);
            if (index < 0 || Math.Abs(index - _index) > 4) return;
            if (type == "select") { SelectionRequested?.Invoke(index); return; }
            if (index != _index) return;
            if (type == "reveal") { _revealed.Add(id); PublishState(); StartLoads(); }
            else if (type == "context")
            {
                var row = _rows[index];
                var menu = new MenuFlyout();
                var source = new MenuFlyoutItem { Text = "打开来源", IsEnabled = row.PageUrl.Length > 0 };
                source.Click += (_, _) => OpenRequested?.Invoke(row, true);
                var file = new MenuFlyoutItem { Text = "打开文件", IsEnabled = File.Exists(IngestReview.LocalPath(row)) };
                file.Click += (_, _) => OpenRequested?.Invoke(row, false);
                menu.Items.Add(source); menu.Items.Add(file);
                menu.ShowAt(_browser, new Point(ReadNumber(message, "x", 0, 0, ActualWidth), ReadNumber(message, "y", 0, 0, ActualHeight)));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { AppService.DebugLog("review message: " + ex.Message); }
    }

    private static double ReadNumber(JsonElement root, string name, double fallback, double min, double max)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? Math.Clamp(number, min, max) : fallback;
    private sealed record Preview(string Key, string Source, double Ratio, bool Protected, string Status, PhotoItem? Item);
}
