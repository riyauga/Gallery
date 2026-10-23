using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Gallery.Models;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using XamlImage = Microsoft.UI.Xaml.Controls.Image;

namespace Gallery.Controls.Review;

/// <summary>以草图原有的本地 CSS 圆环渲染预览，原生页保留选中、保护、右键与归档契约。</summary>
internal sealed class ReviewCarousel : Grid
{
    private readonly WebView2 _browser = new() { DefaultBackgroundColor = Microsoft.UI.Colors.Transparent };
    private readonly Canvas _scrollOverlay = new();
    private readonly ContinuousScrollBar _settingsScroll = new() { Step = 0, Visibility = Visibility.Collapsed, ThumbThickness = 8, MinimumThumbLength = 18 };
    private bool _settingsSyncing, _settingsInteracting, _settingsVisible;
    private int _settingsCommand;
    private readonly XamlImage _freeze = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Dictionary<int, Preview> _previews = [];
    private readonly Dictionary<int, string> _sentSources = [];
    private readonly HashSet<int> _revealed = [];
    private IReadOnlyList<StoreService.PendingIngestRow> _rows = [];
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _prefetch;
    private IReadOnlyList<StoreService.PendingIngestRow>? _prefetchedRows;
    private readonly HashSet<string> _placeholderLoading = [];
    private Preview? _clearPreview;
    private int _index, _selectSeq;
    private int? _resumeCenterId;
    private int _viewGeneration;
    private bool _batchMode;
    private readonly HashSet<int> _batchSelection = [];
    /// <summary>正在跑模型的记录：卡片压深色并在正中转圈。</summary>
    private readonly HashSet<int> _analyzing = [];
    /// <summary>按记录缓存「模型是否处理过这张」，payload 引用不变就不重解析。</summary>
    private readonly Dictionary<int, (string Payload, bool Analyzed)> _analysisCache = [];
    private int _gridFirst, _gridLast = 35;
    private string _gridSize = "medium";
    private bool _interactionEnabled = true;
    private bool _active = true, _initializing, _ready, _clearAllowed;
    private bool _frozen, _freezing, _freezeRequested;
    private double _angle = 10, _overlap = 67, _size = 95, _scale = 1.12, _drag = 1;
    private string _ease = "out";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public event Action<int, bool, bool>? SelectionRequested;
    public event Action? ClearSelectionRequested;
    public event Action<IReadOnlyList<int>, bool, string>? BoxSelectionRequested;
    public event Action<PhotoItem>? SelectedImageLoaded;
    public event Action<StoreService.PendingIngestRow, bool>? OpenRequested;
    public event Action<IReadOnlyList<string>>? FilesDropped;
    /// <summary>拖入/拖出卡片区，宿主据此显示覆盖提示。</summary>
    public event Action<bool>? DragStateChanged;

    public ReviewCarousel()
    {
        MinHeight = 320;
        Children.Add(_error);
        Children.Add(_freeze);
        Children.Add(_scrollOverlay);
        _scrollOverlay.Children.Add(_settingsScroll);
        Canvas.SetZIndex(_scrollOverlay, 10);
        _settingsScroll.ValueChanged += value =>
        {
            if (_settingsSyncing || !_ready || !_active) return;
            _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "panelScroll", value, command = ++_settingsCommand }, Json));
        };
        _settingsScroll.InteractionStarted += () => _settingsInteracting = true;
        _settingsScroll.InteractionCompleted += () => _settingsInteracting = false;
        _settingsScroll.PointerWheelChanged += (_, args) =>
        {
            _settingsScroll.Value = Math.Clamp(_settingsScroll.Value - args.GetCurrentPoint(_settingsScroll).Properties.MouseWheelDelta / 3.0, 0, _settingsScroll.Maximum);
            args.Handled = true;
        };
        Canvas.SetZIndex(_freeze, 20);
        if (AppService.Settings.Settings.layout_options.TryGetValue("review_carousel", out var saved) && saved.ValueKind == JsonValueKind.Object)
        {
            _angle = ReadNumber(saved, "angle", 10, 8, 17);
            _overlap = ReadNumber(saved, "overlap", 67, 0, 95);
            _size = ReadNumber(saved, "size", 95, 50, 95);
            _scale = ReadNumber(saved, "scale", 1.12, 1, 1.6);
            _drag = ReadNumber(saved, "drag", 1, .4, 2);
            _ease = ReadEase(saved, "ease", "out");
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
        if (_active && !ReferenceEquals(_prefetchedRows, rows))
        {
            _prefetchedRows = rows;
            PrunePreviews(rows);
            StartPrefetch(rows);
        }
        PublishState();
        StartLoads();
        if (_batchMode && rows.Count > 0 && _previews.GetValueOrDefault(rows[_index].Id)?.Item is { } item)
            SelectedImageLoaded?.Invoke(item);
    }

    public void SetBatchMode(bool enabled, IReadOnlyCollection<int> selected, int? singleIndex = null)
    {
        // 退出批量时先恢复圆环索引，避免首个状态仍带着网格索引启动入场。
        if (!enabled && singleIndex is int index && _rows.Count > 0)
            _index = Math.Clamp(index, 0, _rows.Count - 1);
        if (_batchMode != enabled)
        {
            _batchMode = enabled;
            _gridFirst = 0; _gridLast = 35;
            _clearAllowed = false;
            _loading?.Cancel();
        }
        _batchSelection.Clear();
        _batchSelection.UnionWith(selected);
        PublishState();
        StartLoads();
    }

    public void SetGridSize(string size)
    {
        _gridSize = size is "small" or "large" ? size : "medium";
        PublishState();
    }

    public void SetInteractionEnabled(bool enabled)
    {
        _interactionEnabled = enabled;
        PublishState();
    }

    /// <summary>宿主报告当前正在跑模型的记录；这些卡片压深色并显示转圈。</summary>
    public void SetAnalyzing(IReadOnlyCollection<int> ids)
    {
        if (_analyzing.SetEquals(ids)) return;
        _analyzing.Clear();
        _analyzing.UnionWith(ids);
        PublishState();
    }

    /// <summary>
    /// 卡片是否已经过模型处理：有分级，或模型判定留下过标记，都算跑完。
    /// 没配置模型时按「已处理」处理，退回原有的遮挡与揭示规则，避免整片卡片永久变黑。
    /// </summary>
    private bool IsAnalyzed(StoreService.PendingIngestRow row)
    {
        if (row.State != "pending") return true;
        if (_analysisCache.TryGetValue(row.Id, out var hit) && ReferenceEquals(hit.Payload, row.Payload))
            return hit.Analyzed;
        var analyzed = !PixaiScorer.LooksConfigured(AppService.Settings.Settings.nsfw_model_dir)
            || ArchiveService.NormalizeRating(row.Rating).Length > 0
            || IngestReview.RatingChecked(row.Payload)
            || IngestReview.ModelTagsChecked(row.Payload);
        _analysisCache[row.Id] = (row.Payload, analyzed);
        return analyzed;
    }

    /// <summary>连续滚动：只更新卡片窗口，不重建详情；详情由网页在停稳后发 settled 提交。</summary>
    public void ScrollWindow(int index)
    {
        if (!_active || !_ready || _rows.Count == 0) return;
        index = Math.Clamp(index, 0, _rows.Count - 1);
        if (index == _index) return;
        _index = index;
        // 占位图已经整队预取，换窗口只换 9 个 src，不再取消/重启加载。
        PublishState();
        // 预取还没轮到这一屏时，优先把当前窗口补上；已缓存/在加载的会被去重。
        var token = _prefetch?.Token ?? CancellationToken.None;
        if (!token.IsCancellationRequested)
            foreach (var (row, _) in Nearby()) _ = EnsurePlaceholderAsync(row, token);
    }

    /// <summary>滚动轴连续位置；follow=true 时网页直接落位，不走弹簧。</summary>
    public void ScrollTo(double position, bool follow)
    {
        if (!_active || !_ready || _rows.Count == 0) return;
        position = Math.Clamp(position, 0, _rows.Count - 1);
        _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "scroll", position, follow }, Json));
    }

    /// <summary>滚动轴结束：落到整数并让网页回 settled，由宿主统一提交详情。</summary>
    public void CommitScroll(int index)
    {
        if (!_active || !_ready || _rows.Count == 0) return;
        index = Math.Clamp(index, 0, _rows.Count - 1);
        _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "scroll", position = (double)index, follow = false, settle = true,
        }, Json));
    }

    public void SetActive(bool active)
    {
        if (_active != active)
        {
            ++_viewGeneration;
            if (!active && !_batchMode && _rows.Count > 0) _resumeCenterId = _rows[_index].Id;
            if (active && !_batchMode && _resumeCenterId is { } centerId)
            {
                var index = _rows.ToList().FindIndex(row => row.Id == centerId);
                if (index >= 0) _index = index;
            }
        }
        _active = active;
        if (!active)
        {
            _settingsVisible = false;
            _settingsScroll.Visibility = Visibility.Collapsed;
            _settingsInteracting = false;
            _settingsScroll.CancelInteraction();
            _settingsScroll.HideIndicator();
            _loading?.Cancel();
            _prefetch?.Cancel();
            _prefetchedRows = null;
            _previews.Clear(); _clearPreview = null; _revealed.Clear();
            _clearAllowed = false;
            UnfreezeWindow();
        }
        PublishState();
        if (active)
        {
            StartLoads();
            if (_rows.Count > 0)
            {
                _prefetchedRows = _rows;
                StartPrefetch(_rows);
            }
        }
    }

    public void RefreshPreview(bool preserveRevealed = false)
    {
        if (preserveRevealed) _revealed.IntersectWith(_rows.Select(row => row.Id));
        else _revealed.Clear();
        _previews.Clear(); _clearPreview = null;
        _prefetchedRows = _rows;
        PublishState(); StartLoads(); StartPrefetch(_rows);
    }


    /// <summary>窗口拖动/缩放时把 WebView 冻结成一张快照，避免整块网页参与合成。</summary>
    public async void FreezeForWindowMove()
    {
        _freezeRequested = true;
        if (_frozen || _freezing || !_ready) return;
        _freezing = true;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await _browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            if (!_freezeRequested) return;
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!_freezeRequested) return;
            _freeze.Source = bitmap;
            _freeze.Visibility = Visibility.Visible;
            _browser.Visibility = Visibility.Collapsed;
            _settingsScroll.Visibility = Visibility.Collapsed;
            _settingsInteracting = false;
            _settingsScroll.CancelInteraction();
            _settingsScroll.HideIndicator();
            _frozen = true;
        }
        catch (Exception ex)
        {
            AppService.DebugLog("review freeze: " + ex.Message);
        }
        finally { _freezing = false; }
    }

    public void UnfreezeWindow()
    {
        _freezeRequested = false;
        if (!_frozen && !_freezing) return;
        _freeze.Visibility = Visibility.Collapsed;
        _freeze.Source = null;
        if (_ready) _browser.Visibility = Visibility.Visible;
        _frozen = false;
        _settingsScroll.Visibility = _active && _settingsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSettingsScroll(JsonElement message)
    {
        _settingsVisible = message.TryGetProperty("visible", out var visible) && visible.GetBoolean();
        _settingsScroll.Visibility = _settingsVisible && !_frozen ? Visibility.Visible : Visibility.Collapsed;
        if (!_settingsVisible) return;
        var scaleX = _browser.ActualWidth / ReadNumber(message, "viewportWidth", 1, 1, 100000);
        var scaleY = _browser.ActualHeight / ReadNumber(message, "viewportHeight", 1, 1, 100000);
        Canvas.SetLeft(_settingsScroll, ReadNumber(message, "x", 0, -100000, 100000) * scaleX);
        Canvas.SetTop(_settingsScroll, ReadNumber(message, "y", 0, -100000, 100000) * scaleY);
        _settingsScroll.Width = ReadNumber(message, "width", 12, 1, 1000) * scaleX;
        _settingsScroll.Height = ReadNumber(message, "height", 0, 0, 100000) * scaleY;
        _settingsSyncing = true;
        try
        {
            _settingsScroll.Maximum = ReadNumber(message, "maximum", 0, 0, 10000000);
            _settingsScroll.ViewportSize = ReadNumber(message, "viewport", 1, 1, 10000000);
            var command = ReadNumber(message, "command", 0, 0, int.MaxValue);
            var userInput = message.TryGetProperty("userInput", out var user) && user.GetBoolean();
            if (userInput)
            {
                _settingsScroll.CancelInteraction();
                _settingsScroll.NotifyActivity();
            }
            if (userInput || (!_settingsInteracting && command >= _settingsCommand))
            {
                var value = ReadNumber(message, "value", 0, 0, _settingsScroll.Maximum);
                if (Math.Abs(_settingsScroll.Value - value) > .01) _settingsScroll.Value = value;
            }
        }
        finally { _settingsSyncing = false; }
    }

    private IEnumerable<(StoreService.PendingIngestRow Row, int Index)> Nearby()
    {
        var first = _batchMode ? Math.Min(_gridFirst, _rows.Count) : Math.Max(0, _index - 4);
        var end = _batchMode ? Math.Min(_rows.Count, _gridLast + 1) : Math.Min(_rows.Count, _index + 5);
        return Enumerable.Range(first, Math.Max(0, end - first)).Select(index => (_rows[index], index));
    }

    private static bool Blurred(StoreService.PendingIngestRow row)
        => AppService.Settings.Settings.nsfw_display is "blur" or "hide"
            && ArchiveService.NormalizeRating(row.Rating) is not ("g" or "s");

    private bool Protected(StoreService.PendingIngestRow row) => Blurred(row) && !_revealed.Contains(row.Id);

    /// <summary>当前队列里是否有需要保护的图片（决定批量预览按钮是否出现）。</summary>
    public bool HasBlurredRows => _rows.Any(Blurred);

    /// <summary>所有需要保护的图片都已显示预览。</summary>
    public bool AllPreviewsRevealed => _rows.Count > 0 && _rows.Where(Blurred).All(row => _revealed.Contains(row.Id));

    /// <summary>批量显示/隐藏当前队列所有受保护图片的预览。</summary>
    public void ToggleRevealAll()
    {
        var targets = _rows.Where(Blurred).ToList();
        if (targets.Count == 0) return;
        if (targets.All(row => _revealed.Contains(row.Id))) _revealed.Clear();
        else foreach (var row in targets) _revealed.Add(row.Id);
        PublishState();
        StartLoads();
    }

    private string PlaceholderKey(StoreService.PendingIngestRow row)
        => $"{IngestReview.CurrentPath(row)}|{Protected(row)}|ph";

    private string ClearKey(StoreService.PendingIngestRow row)
        => $"{IngestReview.CurrentPath(row)}|{Protected(row)}|clear";

    private void PublishState()
    {
        if (!_ready) return;
        var visibleIds = !_active ? [] : Nearby().Select(entry => entry.Row.Id).ToHashSet();
        foreach (var id in _sentSources.Keys.Where(id => !visibleIds.Contains(id)).ToArray()) _sentSources.Remove(id);
        var cards = !_active ? [] : Nearby().Select(entry =>
        {
            var (row, index) = entry;
            var placeholder = _previews.GetValueOrDefault(row.Id);
            var protectedNow = Protected(row);
            // 只有停稳后才把清晰图给中心卡片；滚动过程中所有卡片都用 256px 低清图 + 距离亮度。
            var clear = !_batchMode && _clearAllowed && index == _index && _clearPreview is not null && !_clearPreview.Protected
                && _clearPreview.Key == ClearKey(row) ? _clearPreview : null;
            var preview = clear ?? (placeholder is not null && placeholder.Protected == protectedNow ? placeholder : null);
            var source = preview?.Source ?? "";
            var changed = !_sentSources.TryGetValue(row.Id, out var sent) || !ReferenceEquals(source, sent);
            _sentSources[row.Id] = source;
            return new { row.Id, Index = index, row.InboxName, Selected = _batchSelection.Contains(row.Id), Src = changed ? source : null, Ratio = preview?.Ratio ?? 1,
                Blocked = protectedNow, Placeholder = preview?.Placeholder ?? true, Status = preview?.Status ?? "正在加载…",
                Analyzed = IsAnalyzed(row), Analyzing = _analyzing.Contains(row.Id),
                Avg = preview?.Average ?? "" };
        }).ToArray();
        _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "state", cards, active = _active, viewGeneration = _viewGeneration, batch = _batchMode, gridSize = _gridSize, interactive = _interactionEnabled,
            index = _index, seq = _selectSeq, authoritative = _selectSeq < 0, count = _rows.Count,
            design = new { angle = _angle, overlap = _overlap, size = _size, scale = _scale, drag = _drag, ease = _ease, aspect = 16.0 / 9 },
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
        if (_batchMode)
        {
            if (!_active || !_ready) return;
            var gridToken = _prefetch?.Token ?? CancellationToken.None;
            foreach (var (row, _) in Nearby()) _ = EnsurePlaceholderAsync(row, gridToken);
            return;
        }
        _loading?.Cancel(); _loading?.Dispose();
        _loading = new CancellationTokenSource();
        if (!_active || !_ready) return;
        var token = _loading.Token;
        var entries = Nearby().OrderBy(entry => Math.Abs(entry.Index - _index)).ToArray();
        foreach (var (row, _) in entries) _ = EnsurePlaceholderAsync(row, token);
        // 只有停稳后才为当前中心加载清晰图；滚动过程中全部用低清占位。
        if (!_batchMode && _clearAllowed && _rows.Count > 0 && !Protected(_rows[_index]))
        {
            var center = _rows[_index];
            if (_clearPreview is null || _clearPreview.Key != ClearKey(center))
                _ = LoadClearAsync(center, _index, token);
        }
    }

    private void StartPrefetch(IReadOnlyList<StoreService.PendingIngestRow> rows)
    {
        _prefetch?.Cancel();
        _prefetch?.Dispose();
        _prefetch = new CancellationTokenSource();
        if (!_active || rows.Count == 0) return;
        _ = PrefetchAsync(rows, _prefetch.Token);
    }

    /// <summary>后台预取整队 256px 占位图，滚动条长距离跳转时窗口切换不空白。</summary>
    private async Task PrefetchAsync(IReadOnlyList<StoreService.PendingIngestRow> rows, CancellationToken token)
    {
        try
        {
            // 待审核队列通常很小；超大列表只预取最近的 200 张，避免一次性压满。
            var limit = Math.Min(rows.Count, 200);
            var order = rows
                .Select((row, index) => (Row: row, Distance: Math.Abs(index - _index)))
                .OrderBy(entry => entry.Distance)
                .Take(limit)
                .Select(entry => entry.Row)
                .ToArray();
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(order.Select(async row =>
            {
                await gate.WaitAsync(token);
                try { await EnsurePlaceholderAsync(row, token); }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppService.DebugLog("review prefetch: " + ex.Message); }
    }

    private void PrunePreviews(IReadOnlyList<StoreService.PendingIngestRow> rows)
    {
        var ids = rows.Select(row => row.Id).ToHashSet();
        foreach (var id in _previews.Keys.Where(id => !ids.Contains(id)).ToArray()) _previews.Remove(id);
        _sentSources.Clear();
    }

    private async Task EnsurePlaceholderAsync(StoreService.PendingIngestRow row, CancellationToken token)
    {
        var key = PlaceholderKey(row);
        if (_previews.TryGetValue(row.Id, out var cached) && cached.Key == key && cached.Source.Length > 0) return;
        if (!_placeholderLoading.Add(key)) return;
        var blocked = Protected(row);
        try
        {
            var path = IngestReview.CurrentPath(row);
            if (path.Length == 0 || !File.Exists(path))
            {
                var status = row.ResolvedPath is not null && row.State == "done"
                    ? "文件已移走，未能在图库中定位"
                    : "本地文件不可用";
                _previews[row.Id] = new Preview(key, "", 1, blocked, status, null, true, "");
                PublishState(); return;
            }
            var item = await ReadItemAsync(row, token);
            // 不做运行时模糊，非中心卡片直接用 256px 低清图；受保护图片仍是预先糊好的 256px 底图。
            var thumb = (WriteableBitmap)await ImageLoader.LoadThumbnailAsync(item, 256, blocked ? 100 : 0, token);
            token.ThrowIfCancellationRequested();
            var encoded = await EncodeJpegAsync(thumb, 76, token);
            _previews[row.Id] = new Preview(key, encoded.Source,
                (double)thumb.PixelWidth / thumb.PixelHeight, blocked, "", item, true, encoded.Average);
            PublishState();
            if (_batchMode && _rows.Count > 0 && _rows[_index].Id == row.Id) SelectedImageLoaded?.Invoke(item);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        {
            if (token.IsCancellationRequested) return;
            _previews[row.Id] = new Preview(key, "", 1, blocked, "图片无法读取", null, true, "");
            PublishState(); AppService.DebugLog("review placeholder: " + ex.Message);
        }
        finally
        {
            _placeholderLoading.Remove(key);
            var retry = _batchMode ? _prefetch?.Token : _loading?.Token;
            if (token.IsCancellationRequested && _active && retry is { IsCancellationRequested: false } nextToken
                && Nearby().FirstOrDefault(entry => entry.Row.Id == row.Id).Row is { } nextRow)
                _ = EnsurePlaceholderAsync(nextRow, nextToken);
        }
    }

    private async Task LoadClearAsync(StoreService.PendingIngestRow row, int index, CancellationToken token)
    {
        var blocked = Protected(row);
        if (blocked) return;
        var key = ClearKey(row);
        if (_clearPreview is not null && _clearPreview.Key == key) return;
        try
        {
            var path = IngestReview.CurrentPath(row);
            if (path.Length == 0 || !File.Exists(path)) return;
            var item = await ReadItemAsync(row, token);
            var bitmap = (WriteableBitmap)await ImageLoader.LoadPreviewAsync(item, 1024, token);
            token.ThrowIfCancellationRequested();
            var encoded = await EncodeJpegAsync(bitmap, 86, token);
            _clearPreview = new Preview(key, encoded.Source,
                (double)bitmap.PixelWidth / bitmap.PixelHeight, false, "", item, false, encoded.Average);
            PublishState();
            if (_index == index && _rows.Count > 0 && _rows[_index].Id == row.Id) SelectedImageLoaded?.Invoke(item);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        {
            if (token.IsCancellationRequested) return;
            if (_previews.TryGetValue(row.Id, out var keep) && keep.Source.Length > 0)
                _previews[row.Id] = keep with { Status = "图片无法读取" };
            PublishState(); AppService.DebugLog("review clear: " + ex.Message);
        }
    }

    private static Task<PhotoItem> ReadItemAsync(StoreService.PendingIngestRow row, CancellationToken token)
    {
        var path = IngestReview.CurrentPath(row);
        return Task.Run(() =>
        {
            var meta = Gallery.Services.ImageInfo.ReadMeta(path);
            var file = new FileInfo(path);
            return new PhotoItem { File = file.Name, FullPath = path, PixelWidth = meta.Width ?? 0, PixelHeight = meta.Height ?? 0,
                Date = file.LastWriteTime, FileSize = file.Length, ModifiedAtUnix = StoreService.ToUnixSeconds(file.LastWriteTimeUtc), Tags = [("rating", "SFW")] };
        }, token);
    }

    private static Task<(string Source, string Average)> EncodeJpegAsync(WriteableBitmap bitmap, int quality, CancellationToken token)
    {
        var pixels = bitmap.PixelBuffer.ToArray();
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        return Task.Run(() =>
        {
            var average = AverageColor(pixels, width, height);
            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(pixels, width, height);
            using var stream = new MemoryStream();
            image.Save(stream, new JpegEncoder { Quality = quality });
            token.ThrowIfCancellationRequested();
            return ("data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray()), average);
        }, token);
    }

    private static string AverageColor(byte[] bgra, int width, int height)
    {
        var total = Math.Max(1, width * height);
        var step = Math.Max(1, total / 4096) * 4;
        long b = 0, g = 0, r = 0;
        var count = 0;
        for (var i = 0; i + 3 < bgra.Length; i += step)
        {
            b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; count++;
        }
        return count == 0 ? "" : $"rgb({r / count},{g / count},{b / count})";
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
            if (type == "files")
            {
                var paths = new List<string>();
                foreach (var item in args.AdditionalObjects)
                    if (item is CoreWebView2File file && file.Path.Length > 0) paths.Add(file.Path);
                if (paths.Count > 0) FilesDropped?.Invoke(paths);
                return;
            }
            if (type == "drag")
            {
                DragStateChanged?.Invoke(message.GetProperty("over").GetBoolean());
                return;
            }
            if (!_active) return;
            if (!message.TryGetProperty("viewGeneration", out var generation) || generation.GetInt32() != _viewGeneration) return;
            if (type == "clearSelection")
            {
                if (_batchMode && _interactionEnabled) ClearSelectionRequested?.Invoke();
                return;
            }
            if (type == "selectionBox")
            {
                if (!_batchMode || !_interactionEnabled) return;
                var indices = message.GetProperty("indices").EnumerateArray().Select(value => value.GetInt32())
                    .Where(index => index >= 0 && index < _rows.Count).Distinct().ToArray();
                var phase = message.GetProperty("phase").GetString() ?? "update";
                if (phase is "start" or "update" or "end" or "cancel")
                    BoxSelectionRequested?.Invoke(indices, message.GetProperty("additive").GetBoolean(), phase);
                return;
            }
            if (!_interactionEnabled && type is "select" or "reveal" or "context") return;
            if (type == "gridWindow")
            {
                if (!_batchMode || _rows.Count == 0) return;
                var first = Math.Clamp(message.GetProperty("first").GetInt32(), 0, _rows.Count - 1);
                var last = Math.Clamp(message.GetProperty("last").GetInt32(), first, Math.Min(_rows.Count - 1, first + 199));
                if (first == _gridFirst && last == _gridLast) return;
                _gridFirst = first; _gridLast = last;
                PublishState();
                var token = _prefetch?.Token ?? CancellationToken.None;
                foreach (var (row, _) in Nearby()) _ = EnsurePlaceholderAsync(row, token);
                return;
            }
            if (_batchMode && type is "window" or "settled" or "step") return;
            if (type == "panelMetrics") { UpdateSettingsScroll(message); return; }
            if (type == "configure")
            {
                _angle = ReadNumber(message, "angle", _angle, 8, 17);
                _overlap = ReadNumber(message, "overlap", _overlap, 0, 95);
                _size = ReadNumber(message, "size", _size, 50, 95);
                _scale = ReadNumber(message, "scale", _scale, 1, 1.6);
                _drag = ReadNumber(message, "drag", _drag, .4, 2);
                _ease = ReadEase(message, "ease", _ease);
                AppService.Settings.Settings.layout_options["review_carousel"] = JsonSerializer.SerializeToElement(new { angle = _angle, overlap = _overlap, size = _size, scale = _scale, drag = _drag, ease = _ease });
                AppService.Settings.Save(); return;
            }
            if (type == "window")
            {
                _clearAllowed = false;
                ScrollWindow(message.GetProperty("index").GetInt32());
                return;
            }
            if (type == "settled")
            {
                if (_rows.Count == 0) return;
                _clearAllowed = true;
                _index = Math.Clamp(message.GetProperty("index").GetInt32(), 0, _rows.Count - 1);
                // 停稳后才提交详情并加载中心清晰图。
                SelectionRequested?.Invoke(_index, false, false);
                return;
            }
            if (type == "step")
            {
                if (_rows.Count > 0) SelectionRequested?.Invoke(Math.Clamp(_index + Math.Sign(message.GetProperty("delta").GetInt32()), 0, _rows.Count - 1), false, false);
                return;
            }
            var id = message.GetProperty("id").GetInt32();
            var index = _rows.ToList().FindIndex(row => row.Id == id);
            if (index < 0 || (!_batchMode && Math.Abs(index - _index) > 4)) return;
            if (type == "select")
            {
                // 网页端带序号的选择只回显最新一条；卡片点击没有序号，属于宿主主动选中，必须直接生效。
                var seqValue = -1;
                var carriesSeq = message.TryGetProperty("seq", out var seq) && seq.TryGetInt32(out seqValue);
                _selectSeq = carriesSeq ? seqValue : -1;
                // 批量模式下滚动同步会顺带发 select，那不是用户点击，不能改勾选。
                if (_batchMode && carriesSeq) return;
                _clearAllowed = false;
                var shift = message.TryGetProperty("shift", out var shiftValue) && shiftValue.ValueKind == JsonValueKind.True;
                var ctrl = message.TryGetProperty("ctrl", out var ctrlValue) && ctrlValue.ValueKind == JsonValueKind.True;
                SelectionRequested?.Invoke(index, shift, ctrl);
                return;
            }
            if (!_batchMode && index != _index) return;
            if (type == "reveal") { _revealed.Add(id); _clearAllowed = true; PublishState(); StartLoads(); }
            else if (type == "context")
            {
                var row = _rows[index];
                var menu = new MenuFlyout();
                var source = new MenuFlyoutItem { Text = "打开来源", IsEnabled = row.PageUrl.Length > 0 };
                source.Click += (_, _) => OpenRequested?.Invoke(row, true);
                var file = new MenuFlyoutItem { Text = "打开文件", IsEnabled = File.Exists(IngestReview.CurrentPath(row)) };
                file.Click += (_, _) => OpenRequested?.Invoke(row, false);
                menu.Items.Add(source); menu.Items.Add(file);
                menu.ShowAt(_browser, new Windows.Foundation.Point(ReadNumber(message, "x", 0, 0, ActualWidth), ReadNumber(message, "y", 0, 0, ActualHeight)));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { AppService.DebugLog("review message: " + ex.Message); }
    }

    private static double ReadNumber(JsonElement root, string name, double fallback, double min, double max)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? Math.Clamp(number, min, max) : fallback;
    private static string ReadEase(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return fallback;
        var text = value.GetString();
        return text is "linear" or "out" or "in" or "inout" ? text : fallback;
    }
    private sealed record Preview(string Key, string Source, double Ratio, bool Protected, string Status, PhotoItem? Item, bool Placeholder, string Average);
}
