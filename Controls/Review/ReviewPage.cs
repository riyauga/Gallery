using System.Diagnostics;
using Gallery.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Gallery.Controls.Review;

/// <summary>归档审阅页：左侧九张圆环预览，右侧六类标签和单目录归档；批量操作使用当前全部待归档记录。</summary>
public sealed class ReviewPage : Grid
{
    private readonly Window _owner;
    private readonly ReviewCarousel _carousel = new();
    private readonly TextBlock _count = new() { FontSize = 28 };
    private readonly TextBlock _summary = Caption();
    private readonly TextBlock _position = Caption();
    private readonly TextBlock _title = new() { FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _time = Caption();
    private readonly TextBlock _spec = Caption();
    private readonly TextBlock _pageUrl = Caption();
    private readonly TextBlock _path = Caption();
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly StackPanel _tagGroups = new() { Spacing = 15 };
    private readonly TextBox _destination = new() { PlaceholderText = "选择图库内的归档目录", MinWidth = 0 };
    private readonly Button _pickFolder = new() { Content = "选择文件夹…", Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button _archive = new() { Content = "归档此图，并查看下一张", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _ignore = new() { Content = "忽略此图", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _archiveAll = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _ignoreAll = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _previous = new() { Content = "↑", MinWidth = 36 };
    private readonly Button _next = new() { Content = "↓", MinWidth = 36 };
    private readonly Button _restore = new() { Content = "恢复待处理", Visibility = Visibility.Collapsed };
    private readonly ComboBox _states = new() { MinWidth = 115, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid _body = new() { ColumnSpacing = 24 };
    private Tags.TagChip? _ratingBadge;
    private readonly ScrollViewer _detailScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private List<StoreService.PendingIngestRow> _rows = [];
    private StoreService.PendingIngestRow? _selected;
    private Dictionary<string, List<string>> _tags = [];
    private readonly Dictionary<string, string> _imageSpecs = new(StringComparer.OrdinalIgnoreCase);
    private string _state = "pending";
    private int _index, _selectionGeneration;
    private bool _busy, _active = true;
    public event Action? Changed;

    public ReviewPage(Window owner)
    {
        _owner = owner;
        _path.MaxLines = 2;
        _path.TextTrimming = TextTrimming.CharacterEllipsis;
        _pageUrl.MaxLines = 1;
        _pageUrl.TextTrimming = TextTrimming.CharacterEllipsis;
        Padding = new Thickness(28, 24, 28, 22);
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46, GridUnitType.Star) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54, GridUnitType.Star) });
        _body.Children.Add(BuildQueue());
        var divider = new Border { Background = ResourceBrush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 28, 0, 28), IsHitTestVisible = false };
        Grid.SetColumn(divider, 1);
        _body.Children.Add(divider);
        var details = BuildDetails();
        Grid.SetColumn(details, 2);
        _body.Children.Add(details);
        Children.Add(_body);
        _carousel.SelectionRequested += Select;
        _carousel.OpenRequested += (row, source) => Open(source ? row.PageUrl : IngestReview.LocalPath(row), source);
        _carousel.SelectedImageLoaded += item =>
        {
            var text = $"{item.PixelWidth} × {item.PixelHeight} · {item.Extension} · {item.FileSize / 1024.0 / 1024:0.##} MB";
            _imageSpecs[item.FullPath] = text;
            if (_selected is { } row && string.Equals(IngestReview.LocalPath(row), item.FullPath, StringComparison.OrdinalIgnoreCase))
                _spec.Text = text;
        };
        _previous.Click += (_, _) => Select(_index - 1);
        _next.Click += (_, _) => Select(_index + 1);
        _archive.Click += async (_, _) => await ArchiveAsync(false);
        _archiveAll.Click += async (_, _) => await ArchiveAsync(true);
        _ignore.Click += (_, _) => Ignore(false);
        _ignoreAll.Click += (_, _) => Ignore(true);
        _restore.Click += (_, _) => Restore();
        _pickFolder.Click += async (_, _) => await PickFolderAsync();
        Loaded += (_, _) => { _active = true; _carousel.SetActive(true); Reload(); };
        Unloaded += (_, _) => SetActive(false);
        ActualThemeChanged += (_, _) => ApplyColors();
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth < 850;
            Padding = new Thickness(narrow ? 16 : 28, 20, narrow ? 16 : 28, 20);
            _body.ColumnSpacing = narrow ? 16 : 28;
            // 左栏跟随可容纳的画框宽度，不让较高窗口中的预览两边留下大片空白。
            _body.ColumnDefinitions[0].Width = new GridLength(Math.Min(Math.Max(300, (ActualHeight - 220) * .67), Math.Max(300, (ActualWidth - 80) * .49)));
        };
        ApplyColors();
    }

    private Grid BuildQueue()
    {
        var panel = new Grid { RowSpacing = 10 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Spacing = 5 };
        var heading = new Grid();
        heading.Children.Add(new TextBlock { Text = "归档审阅", FontSize = 25, FontWeight = FontWeights.SemiBold });
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        heading.Children.Add(_count);
        header.Children.Add(heading);
        header.Children.Add(_summary);
        panel.Children.Add(header);
        Grid.SetRow(_carousel, 1);
        panel.Children.Add(_carousel);
        var footer = new StackPanel { Spacing = 10 };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 14 };
        _position.VerticalAlignment = VerticalAlignment.Center;
        navigation.Children.Add(_previous);
        navigation.Children.Add(_position);
        navigation.Children.Add(_next);
        footer.Children.Add(navigation);
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.Children.Add(_archiveAll);
        Grid.SetColumn(_ignoreAll, 1);
        actions.Children.Add(_ignoreAll);
        footer.Children.Add(actions);
        footer.Children.Add(new TextBlock { Text = "批量操作范围：全部待归档图片", FontSize = 11, Opacity = .65, HorizontalAlignment = HorizontalAlignment.Center });
        Grid.SetRow(footer, 2);
        panel.Children.Add(footer);
        AutomationProperties.SetName(_previous, "上一张待归档图片");
        AutomationProperties.SetName(_next, "下一张待归档图片");
        return panel;
    }

    private Grid BuildDetails()
    {
        var host = new Grid { RowSpacing = 20, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Stretch };
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel { Spacing = 12 };
        var top = new Grid { MinHeight = 34 };
        top.Children.Add(new TextBlock { Text = "图片详情", FontSize = 12, Opacity = .65, VerticalAlignment = VerticalAlignment.Center });
        foreach (var (key, title) in new[] { ("pending", "待归档"), ("missing", "文件丢失"), ("done", "已归档"), ("ignored", "已忽略"), ("deferred", "已延期"), ("hidden", "已隐藏") })
            _states.Items.Add(new ComboBoxItem { Tag = key, Content = title });
        _states.SelectedIndex = 0;
        _states.SelectionChanged += (_, _) =>
        {
            if (_states.SelectedItem is ComboBoxItem { Tag: string state } && state != _state && !_busy)
            {
                _state = state;
                _selected = null;
                _index = 0;
                Reload();
            }
        };
        top.Children.Add(_states);
        panel.Children.Add(top);
        panel.Children.Add(_title);
        var facts = new Grid { RowSpacing = 7, ColumnSpacing = 12 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddFact("加入时间", _time);
        AddFact("图片规格", _spec);
        AddFact("来源页面", _pageUrl);
        AddFact("本地文件", _path);
        panel.Children.Add(facts);
        panel.Children.Add(new Border { Height = 1, Background = ResourceBrush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 6, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "标签", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_tagGroups);
        _detailScroll.Content = panel;
        host.Children.Add(_detailScroll);
        var archivePanel = new StackPanel { Spacing = 10 };
        archivePanel.Children.Add(new Border { Height = 1, Background = ResourceBrush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 0, 0, 6) });
        archivePanel.Children.Add(new TextBlock { Text = "归档目录", FontSize = 12, Opacity = .7 });
        var folder = new Grid { ColumnSpacing = 8 };
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folder.Children.Add(_destination);
        Grid.SetColumn(_pickFolder, 1);
        folder.Children.Add(_pickFolder);
        archivePanel.Children.Add(folder);
        var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        _archive.MinHeight = _ignore.MinHeight = 40;
        actions.Children.Add(_archive);
        actions.Children.Add(_ignore);
        archivePanel.Children.Add(actions);
        archivePanel.Children.Add(_restore);
        archivePanel.Children.Add(_status);
        Grid.SetRow(archivePanel, 1);
        host.Children.Add(archivePanel);
        return host;

        void AddFact(string title, TextBlock value)
        {
            var row = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = title, FontSize = 12, Opacity = .6 };
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            facts.Children.Add(label);
            facts.Children.Add(value);
        }
    }

    public void Reload()
    {
        if (_busy || AppService.Store is not { } store) return;
        var keep = _selected?.Id;
        _rows = store.IngestRows(_state, int.MaxValue).OrderBy(row => row.ReceivedAt).ThenBy(row => row.Id).ToList();
        var pending = store.PendingIngestCount();
        _count.Text = $"{_rows.Count} 张";
        _summary.Text = _state switch
        {
            "pending" => "待归档",
            "missing" => "文件丢失",
            _ => "历史记录",
        };
        _archiveAll.Content = $"一键归档（{pending}）";
        _ignoreAll.Content = $"一键忽略（{pending}）";
        if (string.IsNullOrWhiteSpace(_destination.Text))
        {
            var settings = AppService.Settings.Settings;
            _destination.Text = settings.archive_root.Length > 0
                ? Path.Combine(settings.archive_root, settings.archive_sub)
                : store.Roots().FirstOrDefault(r => r.Enabled)?.Path ?? "";
        }
        var found = keep.HasValue ? _rows.FindIndex(r => r.Id == keep.Value) : -1;
        Select(found >= 0 ? found : Math.Min(_index, _rows.Count - 1));
        UpdateButtons();
    }

    private void Select(int index)
    {
        if (_busy) return;
        _index = _rows.Count == 0 ? 0 : Math.Clamp(index, 0, _rows.Count - 1);
        _selected = _rows.Count == 0 ? null : _rows[_index];
        ++_selectionGeneration;
        _carousel.SetItems(_rows, _index);
        _position.Text = _rows.Count == 0 ? "0 / 0" : $"{_index + 1} / {_rows.Count}";
        _tagGroups.Children.Clear();
        _ratingBadge = null;
        if (_selected is not { } row)
        {
            _title.Text = _state switch
            {
                "pending" => "没有待归档图片",
                "missing" => "没有文件丢失记录",
                _ => "没有此类记录",
            };
            _time.Text = _spec.Text = _pageUrl.Text = _path.Text = "";
            _status.Text = "";
            _tags = [];
            UpdateButtons();
            return;
        }
        _title.Text = row.InboxName.Length > 0 ? row.InboxName : Path.GetFileName(IngestReview.LocalPath(row));
        _time.Text = DateTimeOffset.FromUnixTimeSeconds((long)row.ReceivedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        _spec.Text = _imageSpecs.GetValueOrDefault(IngestReview.LocalPath(row), "—");
        _pageUrl.Text = row.PageUrl.Length == 0 ? "未记录" : row.PageUrl;
        var local = IngestReview.LocalPath(row);
        _path.Text = local.Length == 0 ? "记录没有本地路径" : local + (File.Exists(local) ? "" : "（不可用）");
        if (row.State == "missing")
            _status.Text = row.Note.Length > 0 ? row.Note : "源文件不可用";
        else if (_state != "pending")
            _status.Text = "";
        ToolTipService.SetToolTip(_path, local);
        ToolTipService.SetToolTip(_pageUrl, row.PageUrl);
        _tags = IngestReview.Tags(row.Payload, row.Rating);
        for (var i = 0; i < IngestReview.Categories.Length; i++)
            BuildTagGroup(IngestReview.Categories[i], IngestReview.Titles[i]);
        UpdateButtons();
    }

    /// <summary>通知点击后按队列记录定位，避免依赖列表排序或当前选中项。</summary>
    public void OpenPending(int id)
    {
        if (_busy) { _pendingNotificationId = id; return; }
        _state = "pending";
        _states.SelectedIndex = 0;
        Reload();
        var index = _rows.FindIndex(row => row.Id == id);
        if (index >= 0) Select(index);
        else _status.Text = "这张图片已处理，不在待归档队列中。";
    }

    private int? _pendingNotificationId;

    private void BuildTagGroup(string category, string title)
    {
        var group = new Grid { ColumnSpacing = 12 };
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        group.Children.Add(new TextBlock { Text = title, FontSize = 12, Opacity = .7, Margin = new Thickness(0, 5, 0, 0) });
        var controls = new StackPanel { Spacing = 7 };
        Grid.SetColumn(controls, 1);
        group.Children.Add(controls);
        var chips = new TagWrapPanel();
        controls.Children.Add(chips);
        Rebuild();
        if (category != "rating")
        {
            var search = new AutoSuggestBox { PlaceholderText = $"输入或搜索{title}…", MinWidth = 0, IsEnabled = _state == "pending" };
            AutomationProperties.SetName(search, $"搜索或添加{title}标签");
            var generation = _selectionGeneration;
            var searchVersion = 0;
            search.TextChanged += async (_, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                var version = ++searchVersion;
                var query = search.Text.Trim();
                if (query.Length == 0) { search.ItemsSource = null; return; }
                await Task.Delay(160);
                if (version != searchVersion || generation != _selectionGeneration || !_active) return;
                try
                {
                    var hits = await Task.Run(() =>
                    {
                        lock (AppService.DbLock)
                        {
                            var tags = AppService.Store!.SearchTags(query, 200).Where(t => t.Category == category)
                                .Select(t => new TagSuggestion(t.Name, t.Display));
                            var entities = AppService.Catalog?.SearchEntities(query, 30).Where(e => e.Kind == category)
                                .Select(e => new TagSuggestion(e.Zh.Length > 0 ? e.Zh : e.En, e.Zh.Length > 0 ? e.Zh : e.En)) ?? [];
                            return tags.Concat(entities).DistinctBy(t => t.Name).Take(12).ToList();
                        }
                    });
                    if (version == searchVersion && generation == _selectionGeneration && _active) search.ItemsSource = hits;
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) { _status.Text = "标签搜索失败：" + ex.Message; }
                catch (ObjectDisposedException) when (!_active) { }
            };
            search.QuerySubmitted += (_, args) =>
            {
                var name = args.ChosenSuggestion is TagSuggestion hit ? hit.Name : args.QueryText.Trim();
                if (name.Length == 0 || _busy) return;
                if (!_tags[category].Contains(name, StringComparer.OrdinalIgnoreCase)) _tags[category].Add(name);
                SaveTags(category == "rating");
                Rebuild();
                search.Text = "";
                search.ItemsSource = null;
            };
            controls.Children.Add(search);
        }
        _tagGroups.Children.Add(group);

        void Rebuild()
        {
            chips.Children.Clear();
            if (category == "rating")
            {
                var rating = _tags[category].FirstOrDefault();
                var label = rating is "g" or "s" ? "SFW" : rating is "q" or "e" ? "NSFW" : "未分级";
                _ratingBadge = new Tags.TagChip("rating", label) { IsHitTestVisible = false };
                chips.Children.Add(_ratingBadge);
            }
            var values = category == "rating" ? new[] { "g", "s", "q", "e" } : _tags[category].ToArray();
            foreach (var name in values)
            {
                ButtonBase chip = category == "rating" ? new ToggleButton
                {
                    Content = name.ToUpperInvariant(),
                    IsChecked = _tags[category].Contains(name, StringComparer.OrdinalIgnoreCase),
                    Padding = new Thickness(9, 4, 9, 4), MinWidth = 0, MinHeight = 28, FontSize = 12,
                    MaxWidth = 320,
                    IsEnabled = _state == "pending",
                } : new Button
                {
                    Content = new Tags.TagChip(category, name), Padding = new Thickness(0), MinWidth = 0, MinHeight = 28,
                    Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
                    IsEnabled = _state == "pending",
                };
                ToolTipService.SetToolTip(chip, category == "rating" ? $"{name.ToUpperInvariant()} · {(name is "g" or "s" ? "SFW" : "NSFW")}" : "点击移除此标签");
                chip.Click += (_, _) =>
                {
                    if (_busy) return;
                    if (category == "rating") { _tags[category].Clear(); _tags[category].Add(name); }
                    else _tags[category].RemoveAll(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
                    SaveTags(category == "rating");
                    Rebuild();
                };
                chips.Children.Add(chip);
            }
            if (category == "rating")
            {
                var classify = new Button { Content = new FontIcon { Glyph = "\uE895", FontSize = 14 }, Padding = new Thickness(9, 5, 9, 5), IsEnabled = _state == "pending" };
                ToolTipService.SetToolTip(classify, "用本地模型自动分级此图");
                AutomationProperties.SetName(classify, "自动分级此图");
                classify.Click += async (_, _) => { await ClassifyAsync(); Rebuild(); };
                chips.Children.Add(classify);
            }
        }
    }

    /// <summary>只判定当前待归档文件，后台推理期间锁定编辑，结果写回同一条队列记录。</summary>
    private async Task ClassifyAsync()
    {
        if (_busy || _selected is not { State: "pending" } row) return;
        _busy = true;
        UpdateButtons();
        _status.Text = "正在自动分级…";
        try
        {
            var modelDir = AppService.Settings.Settings.nsfw_model_dir;
            var result = await Task.Run(() =>
            {
                var scorer = NsfwScorer.TryCreate(modelDir, out var error);
                return scorer is null ? (Rating: "", Error: error) : (Rating: scorer.Score(IngestReview.LocalPath(row)).Rating, Error: "");
            });
            if (result.Error.Length > 0) { _status.Text = "自动分级失败：" + result.Error; return; }
            _tags["rating"].Clear();
            _tags["rating"].Add(result.Rating);
            SaveTags(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException
            or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        { _status.Text = "自动分级失败：" + ex.Message; }
        finally
        {
            _busy = false;
            UpdateButtons();
            OpenDeferredNotification();
        }
    }

    private void OpenDeferredNotification()
    {
        if (_pendingNotificationId is not int id) return;
        _pendingNotificationId = null;
        OpenPending(id);
    }

    private void SaveTags(bool refreshProtection)
    {
        if (_selected is not { State: "pending" } row || AppService.Store is not { } store) return;
        try
        {
            var rating = _tags["rating"].FirstOrDefault() ?? "";
            var payload = IngestReview.UpdatePayload(row.Payload, _tags, rating);
            store.UpdateIngestReview(row.Id, payload, rating);
            _selected = row with { Payload = payload, Rating = rating };
            _rows[_index] = _selected;
            _status.Text = "标签已保存";
            if (refreshProtection)
            {
                _carousel.RefreshPreview();
                _carousel.SetItems(_rows, _index);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            _status.Text = "标签保存失败：" + ex.Message;
        }
    }

    private async Task PickFolderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            if (AppService.Archiver?.ResolveRoot(folder.Path).RootId is not >= 0)
            {
                _status.Text = "请选择已登记图库内的目录；新图库请先在设置中添加。";
                return;
            }
            _destination.Text = folder.Path;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _status.Text = "无法选择目录：" + ex.Message;
        }
    }

    private async Task ArchiveAsync(bool all)
    {
        if (_busy || AppService.Store is not { } store || AppService.Archiver is not { } archiver) return;
        var destination = _destination.Text.Trim();
        try
        {
            if (destination.Length == 0 || archiver.ResolveRoot(destination).RootId < 0)
            {
                _status.Text = "请选择已登记图库内的归档目录。";
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _status.Text = "归档目录无效：" + ex.Message;
            return;
        }
        var ids = all ? store.IngestRows("pending", int.MaxValue).Select(r => r.Id).ToArray()
            : _selected is { State: "pending" } row ? [row.Id] : Array.Empty<int>();
        if (ids.Length == 0) return;
        _busy = true;
        UpdateButtons();
        var succeeded = 0;
        var failures = new List<string>();
        try
        {
            foreach (var id in ids)
            {
                _status.Text = $"正在归档 {succeeded + failures.Count + 1} / {ids.Length}…";
                var result = await Task.Run(() => archiver.Archive(id, destination));
                if (result.Ok) succeeded++; else failures.Add($"#{id}：{result.Message}");
            }
            if (succeeded > 0)
            {
                var (rootId, relative) = archiver.ResolveRoot(destination);
                AppService.Settings.Settings.archive_root = store.Roots().First(r => r.Id == rootId).Path;
                AppService.Settings.Settings.archive_sub = relative.Replace('/', Path.DirectorySeparatorChar);
                AppService.Settings.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            failures.Add(ex.Message);
        }
        finally
        {
            _busy = false;
            Reload();
            Changed?.Invoke();
            OpenDeferredNotification();
        }
        _status.Text = failures.Count == 0 ? $"已归档 {succeeded} 张" : $"已归档 {succeeded} 张，失败 {failures.Count} 条。\n" + string.Join("\n", failures.Take(3));
    }

    private void Ignore(bool all)
    {
        if (_busy || AppService.Store is not { } store) return;
        var rows = all
            ? store.IngestRows("pending", int.MaxValue)
            : _selected is { State: "pending" or "missing" } row
                ? [row]
                : new List<StoreService.PendingIngestRow>();
        foreach (var item in rows) store.SetIngestState(item.Id, "ignored", "手动忽略");
        Reload();
        Changed?.Invoke();
        _status.Text = $"已忽略 {rows.Count} 张，磁盘文件保留";
    }

    private void Restore()
    {
        if (_busy || _selected is not { } row || AppService.Store is not { } store) return;
        if (row.State == "done") { _status.Text = "已归档记录无需重复归档"; return; }
        store.SetIngestState(row.Id, "pending", "");
        Reload();
        Changed?.Invoke();
    }

    private void UpdateButtons()
    {
        var pending = _selected is { State: "pending" };
        var missing = _selected is { State: "missing" };
        _archive.IsEnabled = !_busy && pending;
        _archive.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _ignore.IsEnabled = !_busy && (pending || missing);
        _ignore.Visibility = _state is "pending" or "missing" ? Visibility.Visible : Visibility.Collapsed;
        _restore.Visibility = _state is "ignored" or "deferred" or "hidden" or "missing" ? Visibility.Visible : Visibility.Collapsed;
        _restore.IsEnabled = !_busy && _selected is not null;
        _archiveAll.IsEnabled = _ignoreAll.IsEnabled = !_busy && _state == "pending" && _rows.Count > 0;
        _archiveAll.Visibility = _ignoreAll.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _previous.IsEnabled = !_busy && _index > 0;
        _next.IsEnabled = !_busy && _index + 1 < _rows.Count;
        _states.IsEnabled = !_busy;
        _destination.IsEnabled = _pickFolder.IsEnabled = !_busy && _state == "pending";
        _tagGroups.IsHitTestVisible = !_busy;
    }

    public void SetActive(bool active)
    {
        _active = active;
        ++_selectionGeneration;
        _carousel.SetActive(active);
        if (!active) _imageSpecs.Clear();
    }

    public void RefreshPreview() => _carousel.RefreshPreview();

    private void Open(string target, bool source)
    {
        if (target.Length == 0) return;
        if (source && (!Uri.TryCreate(target, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")))
        { _status.Text = "来源页面不是有效的网页地址"; return; }
        if (!source && !File.Exists(target)) { _status.Text = "本地文件不可用"; return; }
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { _status.Text = "无法打开：" + ex.Message; }
    }

    private void ApplyColors()
    {
        _archive.Style = _archiveAll.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        _ignoreAll.Background = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 64, 55, 43) : Windows.UI.Color.FromArgb(255, 247, 237, 223));
        _ignoreAll.Foreground = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 225, 194, 145) : Windows.UI.Color.FromArgb(255, 128, 85, 26));
        _ignore.Opacity = .72;
    }

    private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Caption() => new() { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private sealed record TagSuggestion(string Name, string Display) { public override string ToString() => Display; }

    /// <summary>按可用宽度换行排列标签，不把长标签或窄窗口撑出右侧信息栏。</summary>
    private sealed class TagWrapPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double x = 0, y = 0, height = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > availableSize.Width) { y += height + 6; x = height = 0; }
                x += size.Width + 6;
                height = Math.Max(height, size.Height);
            }
            return new Size(double.IsInfinity(availableSize.Width) ? x : availableSize.Width, y + height);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0, y = 0, height = 0;
            foreach (var child in Children)
            {
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > finalSize.Width) { y += height + 6; x = height = 0; }
                child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
                x += size.Width + 6;
                height = Math.Max(height, size.Height);
            }
            return finalSize;
        }
    }
}
