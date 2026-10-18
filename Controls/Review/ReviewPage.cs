using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Gallery.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Gallery.Controls.Review;

/// <summary>
/// 待审核页（对应 galleryold 的待处理队列）：左边列出浏览器扩展送进来、还没归档的记录，
/// 右边看详情（站点 / 页面 / 标签 / 本地文件），可以打开来源页面、忽略或删除记录。
/// 预览沿用旧版的后台解码、选中项令牌和敏感/未知分级显式揭示流程。
/// </summary>
public sealed class ReviewPage : Grid
{
    private readonly ListView _list = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly TextBlock _pageUrl = new();
    private readonly TextBlock _imageUrl = new();
    private readonly TextBlock _pathText = new();
    private readonly TextBlock _tagsText = new();
    private readonly TextBlock _status = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, Height = 280 };
    private readonly TextBlock _previewStatus = new()
    {
        Text = "选择图片查看", HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
    };
    private readonly Button _reveal = new() { Content = "显示预览", Visibility = Visibility.Collapsed };
    private int _previewToken;
    private readonly Button _openPage = new() { Content = "打开来源页面", IsEnabled = false };
    private readonly Button _openFile = new() { Content = "打开本地文件", IsEnabled = false };
    private readonly Button _ignore = new() { Content = "忽略", IsEnabled = false };
    private readonly Button _delete = new() { Content = "删除记录", IsEnabled = false };
    private readonly ComboBox _folder = new() { Width = 190 };
    private readonly TextBox _subfolder = new() { Width = 120, PlaceholderText = "子目录（可空）" };
    private readonly Button _archive = new() { Content = "归档到此处", IsEnabled = false, MinWidth = 96 };
    private readonly Button _defer = new() { Content = "延期", IsEnabled = false };
    private readonly Button _hide = new() { Content = "隐藏", IsEnabled = false };
    private readonly Button _restore = new() { Content = "恢复待处理", IsEnabled = false, Visibility = Visibility.Collapsed };

    private StackPanel? _archiveRow;

    /// <summary>当前视图：pending / deferred / hidden / done。</summary>
    private string _state = "pending";

    private List<StoreService.PendingIngestRow> _rows = [];
    private StoreService.PendingIngestRow? _selected;

    /// <summary>记录被处理掉一条时通知外面刷新角标。</summary>
    public event Action? Changed;

    public ReviewPage()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        BuildTabs();
        Children.Add(BuildHeader());

        var body = new Grid
        {
            Margin = new Thickness(32, 8, 32, 24),
            ColumnSpacing = 12,
        };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(BuildListPane());

        var detail = BuildDetailPane();
        Grid.SetColumn(detail, 1);
        body.Children.Add(detail);

        Grid.SetRow(body, 1);
        Children.Add(body);

        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => ClearPreview();
    }

    private readonly StackPanel _tabs = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
    };

    private FrameworkElement BuildHeader()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(32, 28, 32, 18),
            Spacing = 4,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "待审核",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(_summary);
        _summary.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

        _tabs.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_tabs);
        return panel;
    }

    private void BuildTabs()
    {
        var states = new (string Key, string Title)[]
        {
            ("pending", "待处理"), ("deferred", "已延期"),
            ("hidden", "已隐藏"), ("done", "已归档"),
        };
        var group = "ReviewStateGroup";
        foreach (var (key, title) in states)
        {
            var radio = new RadioButton
            {
                Content = title,
                Tag = key,
                GroupName = group,
                IsChecked = key == _state,
                MinWidth = 0,
                Padding = new Thickness(10, 4, 10, 6),
            };
            radio.Checked += (sender, _) =>
            {
                if (sender is RadioButton { Tag: string next } && next != _state)
                {
                    _state = next;
                    _selected = null;
                    Reload();
                }
            };
            _tabs.Children.Add(radio);
        }
    }

    private FrameworkElement BuildListPane()
    {
        _list.SelectionMode = ListViewSelectionMode.Single;
        _list.IsItemClickEnabled = false;
        _list.SelectionChanged += (_, _) =>
        {
            _selected = (_list.SelectedItem as RowView)?.Row;
            ShowSelection();
        };
        _list.ItemTemplate = BuildRowTemplate();
        return _list;
    }

    private static DataTemplate BuildRowTemplate()
    {
        const string xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid Padding="4,6" ColumnSpacing="8">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <StackPanel Spacing="2">
                        <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis" MaxLines="1" />
                        <TextBlock Text="{Binding Subtitle}" Style="{StaticResource CaptionTextBlockStyle}"
                                   TextTrimming="CharacterEllipsis" MaxLines="1" />
                    </StackPanel>
                    <TextBlock Grid.Column="1" Text="{Binding Badge}" VerticalAlignment="Center"
                               Style="{StaticResource CaptionTextBlockStyle}" />
                </Grid>
            </DataTemplate>
            """;
        return (DataTemplate)XamlReader.Load(xaml);
    }

    private FrameworkElement BuildDetailPane()
    {
        var panel = new StackPanel { Spacing = 6 };
        _title.Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"];
        _title.TextWrapping = TextWrapping.Wrap;
        _subtitle.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

        _pageUrl.TextWrapping = TextWrapping.Wrap;
        _pageUrl.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _imageUrl.TextWrapping = TextWrapping.Wrap;
        _imageUrl.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _pathText.TextWrapping = TextWrapping.Wrap;
        _pathText.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _tagsText.TextWrapping = TextWrapping.Wrap;
        _status.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

        panel.Children.Add(_title);
        panel.Children.Add(_subtitle);
        var previewHost = new Grid { MinHeight = 180 };
        previewHost.Children.Add(_preview);
        previewHost.Children.Add(_previewStatus);
        panel.Children.Add(previewHost);
        _reveal.Click += (_, _) => _ = LoadPreviewAsync(reveal: true);
        panel.Children.Add(_reveal);
        panel.Children.Add(Section("来源页面"));
        panel.Children.Add(_pageUrl);
        panel.Children.Add(Section("图片地址"));
        panel.Children.Add(_imageUrl);
        panel.Children.Add(Section("本地文件"));
        panel.Children.Add(_pathText);
        panel.Children.Add(Section("标签"));
        panel.Children.Add(_tagsText);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _openPage.Click += (_, _) => OpenUrl(_selected?.PageUrl ?? "");
        _openFile.Click += (_, _) => OpenFile();
        _ignore.Click += (_, _) => SetState("ignored", "手动忽略");
        _delete.Click += (_, _) => DeleteRecord();
        _defer.Click += (_, _) => SetState("deferred", "延期处理");
        _hide.Click += (_, _) => SetState("hidden", "手动隐藏");
        _restore.Click += (_, _) => SetState("pending", "");
        actions.Children.Add(_openPage);
        actions.Children.Add(_openFile);
        actions.Children.Add(_ignore);
        actions.Children.Add(_defer);
        actions.Children.Add(_hide);
        actions.Children.Add(_restore);
        panel.Children.Add(actions);

        // 删除是不可撤销的，单独一行放，避免和常用动作挤在一起被裁掉
        var danger = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
        };
        danger.Children.Add(_delete);
        panel.Children.Add(danger);

        _archiveRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _archive.Click += (_, _) => ArchiveSelected();
        _archiveRow.Children.Add(_folder);
        _archiveRow.Children.Add(_subfolder);
        _archiveRow.Children.Add(_archive);
        panel.Children.Add(_archiveRow);
        panel.Children.Add(_status);

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 4, 12, 0),
        };
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 10, 0, 0),
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
    };

    /// <summary>目标文件夹下拉 = 已登记根目录（归档只允许落在图库里）。</summary>
    private void RefreshFolders()
    {
        var store = AppService.Store;
        if (store is null) return;

        var roots = store.Roots().Where(r => r.Enabled).ToList();
        var current = _folder.SelectedItem as string;
        _folder.ItemsSource = roots.Select(r => r.Path).ToList();
        var remembered = AppService.Settings?.Settings.archive_root ?? "";
        _folder.SelectedItem = current is not null && roots.Any(r => r.Path == current)
            ? current
            : roots.FirstOrDefault(r => string.Equals(r.Path, remembered, StringComparison.OrdinalIgnoreCase))?.Path
              ?? roots.FirstOrDefault()?.Path;

        if (_subfolder.Text.Length == 0)
            _subfolder.Text = AppService.Settings?.Settings.archive_sub ?? "";
        _folder.IsEnabled = roots.Count > 0;
    }

    private void ArchiveSelected()
    {
        var row = _selected;
        var archiver = AppService.Archiver;
        if (row is null || archiver is null)
        {
            _status.Text = "归档服务没就绪。";
            return;
        }

        var root = _folder.SelectedItem as string ?? "";
        if (root.Length == 0)
        {
            _status.Text = "先选一个图库文件夹。";
            return;
        }

        var sub = _subfolder.Text.Trim().Replace('\\', '/').Trim('/');
        var destination = sub.Length == 0 ? root : Path.Combine(root, sub.Replace('/', '\\'));

        _archive.IsEnabled = false;
        try
        {
            var result = archiver.Archive(row.Id, destination);
            _status.Text = result.Message;
            if (result.Ok)
            {
                if (AppService.Settings is { } memory)
                {
                    memory.Settings.archive_root = root;
                    memory.Settings.archive_sub = sub;
                    memory.Save();
                }

                _selected = null;
                Reload();
                Changed?.Invoke();
            }
        }
        finally
        {
            _archive.IsEnabled = true;
        }
    }

    public void Reload()
    {
        var store = AppService.Store;
        if (store is null) return;

        _rows = store.IngestRows(_state);
        var stats = store.IngestStats();
        int Count(string key) => stats.TryGetValue(key, out var v) ? v : 0;

        _summary.Text =
            $"待处理 {Count("pending")} 条 · 已延期 {Count("deferred")} · 已隐藏 {Count("hidden")} " +
            $"· 已归档 {Count("done")} · 已忽略 {Count("ignored")}";

        if (_archiveRow is not null)
            _archiveRow.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _archive.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _ignore.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _defer.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _hide.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _restore.Visibility = _state == "pending" ? Visibility.Collapsed : Visibility.Visible;

        RefreshFolders();

        var items = _rows.Select(r => new RowView(r)).ToList();
        var keep = _selected;
        _list.ItemsSource = items;
        if (keep is not null)
        {
            var again = items.FirstOrDefault(x => x.Row.Id == keep.Id);
            if (again is not null) _list.SelectedItem = again;
            else if (items.Count > 0) _list.SelectedIndex = 0;
        }
        else if (items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }

        if (_rows.Count == 0)
        {
            _selected = null;
            ClearPreview();
            _title.Text = _state switch
            {
                "deferred" => "没有已延期的记录",
                "hidden" => "没有已隐藏的记录",
                "done" => "没有已归档的记录",
                _ => "没有待处理记录",
            };
            _subtitle.Text = _state == "pending"
                ? "浏览器扩展送来下载记录后会出现在这里。"
                : "切回「待处理」可以继续处理新记录。";
            _pageUrl.Text = "";
            _imageUrl.Text = "";
            _pathText.Text = "";
            _tagsText.Text = "";
            _openPage.IsEnabled = false;
            _openFile.IsEnabled = false;
            _ignore.IsEnabled = false;
            _delete.IsEnabled = false;
            _defer.IsEnabled = false;
            _hide.IsEnabled = false;
            _restore.IsEnabled = false;
            _archive.IsEnabled = false;
        }
    }

    private void ShowSelection()
    {
        var row = _selected;
        if (row is null)
        {
            ClearPreview();
            return;
        }

        _title.Text = row.InboxName.Length > 0 ? row.InboxName : $"记录 #{row.Id}";
        var when = DateTimeOffset.FromUnixTimeSeconds((long)row.ReceivedAt).ToLocalTime();
        var site = row.Source.Length > 0 ? row.Source : "未知来源";
        var rating = row.Rating.Length > 0 ? row.Rating : "—";
        _subtitle.Text = $"{site} · 分级 {rating} · {when:MM-dd HH:mm}";

        _pageUrl.Text = row.PageUrl.Length > 0 ? row.PageUrl : "（没有记录）";
        _imageUrl.Text = row.ImageUrl.Length > 0 ? row.ImageUrl : "（没有记录）";

        var localPath = PreviewPath(row);
        var path = localPath.Length > 0 ? localPath : "（没有记录）";
        var exists = localPath.Length > 0 && File.Exists(localPath);
        _pathText.Text = exists ? path : path + (row.InboxPath.Length > 0 ? "（文件已不在原位）" : "");

        _tagsText.Text = DescribeTags(row.Payload);

        _openPage.IsEnabled = row.PageUrl.Length > 0;
        _openFile.IsEnabled = exists;
        _ignore.IsEnabled = true;
        _delete.IsEnabled = true;
        _defer.IsEnabled = true;
        _hide.IsEnabled = true;
        _restore.IsEnabled = true;
        _archive.IsEnabled = _folder.SelectedItem is string;
        _status.Text = "";
        RefreshPreview();
    }

    private static string PreviewPath(StoreService.PendingIngestRow row)
        => row.State == "done" && row.Note.Length > 0 ? row.Note : row.InboxPath;

    private CancellationTokenSource? _previewCancellation;

    private void ClearPreview()
    {
        _previewCancellation?.Cancel();
        ++_previewToken;
        _preview.Source = null;
        _previewStatus.Text = "选择图片查看";
        _reveal.Visibility = Visibility.Collapsed;
    }

    public void SetActive(bool active) { if (!active) ClearPreview(); }
    public void RefreshPreview() => _ = LoadPreviewAsync();

    private async Task LoadPreviewAsync(bool reveal = false)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation = _previewCancellation = new CancellationTokenSource();
        var token = ++_previewToken;
        var row = _selected;
        _preview.Source = null;
        _reveal.Visibility = Visibility.Collapsed;
        if (row is null) { _previewStatus.Text = "选择图片查看"; return; }
        var path = PreviewPath(row);
        if (!File.Exists(path)) { _previewStatus.Text = "文件已不在原位，无法预览"; return; }
        var rating = ArchiveService.NormalizeRating(row.Rating);
        // Gallery_Old/app/ui/review_dialog.py: show_preview；未知分级也要显式揭示。
        if (!reveal && AppService.Settings.Settings.blur_nsfw && rating is not ("g" or "s"))
        {
            _previewStatus.Text = "NSFW / 未填分级预览已隐藏";
            _reveal.Visibility = Visibility.Visible;
            return;
        }
        _previewStatus.Text = "正在加载预览…";
        try
        {
            var meta = await Task.Run(() => ImageInfo.ReadMeta(path), cancellation.Token);
            if (token != _previewToken) return;
            var file = new FileInfo(path);
            var item = new Gallery.Models.PhotoItem
            {
                File = file.Name, FullPath = path, PixelWidth = meta.Width ?? 0, PixelHeight = meta.Height ?? 0,
                Date = file.LastWriteTime, FileSize = file.Length, ModifiedAtUnix = StoreService.ToUnixSeconds(file.LastWriteTimeUtc),
            };
            var bitmap = await ImageLoader.LoadPreviewAsync(item, 2048, cancellation.Token);
            if (token != _previewToken) return;
            _preview.Source = bitmap;
            _previewStatus.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or SixLabors.ImageSharp.UnknownImageFormatException
            or SixLabors.ImageSharp.InvalidImageContentException or System.Runtime.InteropServices.COMException)
        {
            if (token == _previewToken) _previewStatus.Text = "图片无法读取：" + ex.Message;
        }
    }

    /// <summary>把 payload 里的五类标签压成一行摘要。</summary>
    private static string DescribeTags(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
                return "（没有标签）";

            var parts = new List<string>();
            foreach (var group in tags.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Array) continue;
                var names = group.Value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? "")
                    .Where(x => x.Length > 0)
                    .ToList();
                if (names.Count == 0) continue;
                var shown = names.Count <= 6 ? string.Join("、", names) : string.Join("、", names.Take(6)) + $"…（共 {names.Count}）";
                // payload 里是 danbooru 的组名（artist/copyright/general/meta），
                // 归档时会被折成七类，这里也用同一套中文标题，免得前后看到两套名字
                var mapped = Services.Taxonomy.DanbooruCategoryMap.TryGetValue(group.Name, out var category)
                    ? category
                    : "custom";
                var title = Array.IndexOf(Services.Taxonomy.Categories, mapped) is var index && index >= 0
                    ? Services.Taxonomy.CategoryTitles[index]
                    : group.Name;
                parts.Add($"{title}：{shown}");
            }

            return parts.Count == 0 ? "（没有标签）" : string.Join(Environment.NewLine, parts);
        }
        catch (JsonException)
        {
            return "（标签解析失败）";
        }
    }

    private void SetState(string state, string note)
    {
        var row = _selected;
        if (row is null || AppService.Store is null) return;
        AppService.Store.SetIngestState(row.Id, state, note);
        _status.Text = state == "ignored" ? "已忽略这条记录。" : "已更新。";
        _selected = null;
        Reload();
        Changed?.Invoke();
    }

    private void DeleteRecord()
    {
        var row = _selected;
        if (row is null || AppService.Store is null) return;
        AppService.Store.DeleteIngest(row.Id);
        _status.Text = "记录已删除（磁盘文件没动）。";
        _selected = null;
        Reload();
        Changed?.Invoke();
    }

    private void OpenFile()
    {
        var path = _selected is { } row ? PreviewPath(row) : "";
        if (path.Length == 0 || !File.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            _status.Text = "打不开文件：" + ex.Message;
        }
    }

    private void OpenUrl(string url)
    {
        if (url.Length == 0) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "打不开链接：" + ex.Message;
        }
    }

    /// <summary>列表行（DataTemplate 绑这三列）。</summary>
    public sealed class RowView
    {
        public RowView(StoreService.PendingIngestRow row)
        {
            Row = row;
            Title = row.InboxName.Length > 0 ? row.InboxName : $"记录 #{row.Id}";
            var site = row.Source.Length > 0 ? row.Source : "未知来源";
            var when = DateTimeOffset.FromUnixTimeSeconds((long)row.ReceivedAt).ToLocalTime();
            Subtitle = $"{site} · {(row.Rating.Length > 0 ? row.Rating : "—")} · {when:MM-dd HH:mm}";
            Badge = "#" + row.Id;
        }

        public StoreService.PendingIngestRow Row { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public string Badge { get; }
    }
}
