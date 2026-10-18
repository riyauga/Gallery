// 筛选面板（阶段 C）：Photos 筛选弹窗形态——媒体类型 + 标签（含/排除、任一/全部）+ 未打标签。
// 状态由 GalleryPage 持有，面板只负责渲染与回调；每次改动立刻应用（Photos 同款即时筛选）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Gallery.Services;

namespace Gallery.Controls;

public sealed class FilterPanel
{
    public enum MediaKind { All, Photos, Videos }


    private readonly StackPanel _chipHost = new() { Spacing = 6 };
    private readonly StackPanel _resultHost = new() { Spacing = 1 };
    private readonly TextBox _search = new() { PlaceholderText = "搜索标签", Width = 300 };
    private readonly ComboBox _mode = new() { Width = 120 };
    private readonly CheckBox _untagged = new() { Content = "只看没打标签的" };
    private readonly RadioButtons _media = new();
    private readonly CheckBox _subfolder = new() { Content = "显示子文件夹内容", Visibility = Visibility.Collapsed };

    private readonly List<(int Id, string Name)> _include = [];
    private readonly List<(int Id, string Name)> _exclude = [];
    private readonly Dictionary<string, string> _displayBy = new(StringComparer.Ordinal);
    public UIElement Root { get; }

    public event Action? Changed;

    public MediaKind Media
    {
        get => _media.SelectedIndex switch { 1 => MediaKind.Photos, 2 => MediaKind.Videos, _ => MediaKind.All };
        set => _media.SelectedIndex = value switch { MediaKind.Photos => 1, MediaKind.Videos => 2, _ => 0 };
    }

    public IReadOnlyList<(int Id, string Name)> Include => _include;
    public IReadOnlyList<(int Id, string Name)> Exclude => _exclude;
    public string Mode => _mode.SelectedIndex == 1 ? "and" : "or";
    public bool UntaggedOnly => _untagged.IsChecked == true;

    public bool Subfolder => _subfolder.IsChecked == true;

    /// <summary>目录页才显示「显示子文件夹内容」，并按设置回填。</summary>
    public void SyncSubfolder(bool recursive, bool visible)
    {
        _subfolder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _subfolder.IsChecked = recursive;
    }


    public FilterPanel()
    {
        _mode.Items.Add("任一标签");
        _mode.Items.Add("全部标签");
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += (_, _) => Raise();

        _untagged.Checked += (_, _) => Raise();
        _untagged.Unchecked += (_, _) => Raise();

        _media.Items.Add("所有媒体");
        _media.Items.Add("照片");
        _media.Items.Add("视频");
        _media.SelectedIndex = 0;
        _media.SelectionChanged += (_, _) => Raise();

        _search.TextChanged += (_, _) => _ = RefreshResultsAsync();
        _subfolder.Checked += (_, _) => Raise();
        _subfolder.Unchecked += (_, _) => Raise();

        var body = new StackPanel
        {
            Width = 400,
            Spacing = 10,
            Children =
            {
                Section("媒体类型"),
                _media,
                Section("标签"),
                _chipHost,
                _search,
                new ScrollViewer
                {
                    MaxHeight = 170,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _resultHost,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        _mode,
                        ClearTagsButton(),
                    },
                },
                _untagged,
                _subfolder,
            },
        };

        // Photos 的 FlyoutPresenter 负责主题、底板、描边和留白；内容不再叠第二张卡片。
        Root = body;
        RebuildChips();
        _ = RefreshResultsAsync();
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.6,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private Button ClearTagsButton()
    {
        var b = new Button { Content = "清除标签筛选", Padding = new Thickness(10, 2, 10, 2) };
        b.Click += (_, _) =>
        {
            _include.Clear();
            _exclude.Clear();
            RebuildChips();
            _ = RefreshResultsAsync();
            Raise();
        };
        return b;
    }

    public void SetState(IEnumerable<(int Id, string Name)> include,
                         IEnumerable<(int Id, string Name)> exclude,
                         string mode,
                         bool untagged)
    {
        _include.Clear();
        _include.AddRange(include);
        _exclude.Clear();
        _exclude.AddRange(exclude);
        _mode.SelectedIndex = mode == "and" ? 1 : 0;
        _untagged.IsChecked = untagged;
        RebuildChips();
    }

    private void Raise() => Changed?.Invoke();

    private void RebuildChips()
    {
        _chipHost.Children.Clear();
        if (_include.Count == 0 && _exclude.Count == 0)
        {
            _chipHost.Children.Add(new TextBlock { Text = "没有标签筛选", FontSize = 12, Opacity = 0.55 });
            return;
        }
        foreach (var line in Chunk(_include.Select(x => (x, false)).Concat(_exclude.Select(x => (x, true)))))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var ((id, name), excluded) in line) row.Children.Add(BuildChip(id, name, excluded));
            _chipHost.Children.Add(row);
        }
    }

    private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size = 3)
    {
        var buffer = new List<T>(size);
        foreach (var item in source)
        {
            buffer.Add(item);
            if (buffer.Count < size) continue;
            yield return buffer;
            buffer = new List<T>(size);
        }
        if (buffer.Count > 0) yield return buffer;
    }

    private UIElement BuildChip(int id, string name, bool excluded)
    {
        var icon = new FontIcon
        {
            Glyph = excluded ? "\uE711" : "\uE73E",
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = _displayBy.TryGetValue(name, out var shown) ? shown : name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = excluded ? 0.6 : 1,
        };
        var border = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            BorderBrush = excluded
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x99, 0xFF, 0x99, 0xA4))
                : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 3, 2),
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                icon,
                text,
                new FontIcon
                {
                    Glyph = "\uE894",
                    FontSize = 8,
                    Opacity = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        border.Child = content;

        var button = new Button
        {
            Content = border,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        button.Click += (_, _) =>
        {
            _include.RemoveAll(x => x.Id == id);
            _exclude.RemoveAll(x => x.Id == id);
            RebuildChips();
            _ = RefreshResultsAsync();
            Raise();
        };
        return button;
    }

    private async Task RefreshResultsAsync()
    {
        var query = _search.Text.Trim();
        _resultHost.Children.Clear();
        if (AppService.Store is null) return;
        var store = AppService.Store;
        List<StoreService.TagInfo> hits = [];
        await Task.Run(() =>
        {
            lock (AppService.DbLock)
            {
                try
                {
                    hits = query.Length == 0
                        ? store.AllTags().OrderByDescending(t => t.Count).Take(14).ToList()
                        : store.SearchTags(query, 12);
                }
                catch { }
            }
        });
        if (query != _search.Text.Trim()) return;

        foreach (var tag in hits)
        {
            bool included = _include.Any(x => x.Id == tag.Id);
            bool excluded = _exclude.Any(x => x.Id == tag.Id);
            AddResultRow(tag, included, excluded);
        }
        if (_resultHost.Children.Count == 0)
            _resultHost.Children.Add(new TextBlock { Text = "没有匹配的标签", FontSize = 12, Opacity = 0.55 });
    }

    private void AddResultRow(StoreService.TagInfo tag, bool included, bool excluded)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _displayBy[tag.Name] = tag.Display;
        label.Children.Add(new TextBlock { Text = tag.Display, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        if (tag.Display != tag.Name)
            label.Children.Add(new TextBlock { Text = tag.Name, FontSize = 11, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock
        {
            Text = $"已用 {tag.Count}",
            FontSize = 11,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (included || excluded)
            label.Children.Add(new FontIcon
            {
                Glyph = excluded ? "\uE711" : "\uE73E",
                FontSize = 11,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
            });
        Grid.SetColumn(label, 0);

        var add = IconButton("\uE710", "作为筛选条件", () =>
        {
            _exclude.RemoveAll(x => x.Id == tag.Id);
            if (!_include.Any(x => x.Id == tag.Id)) _include.Add((tag.Id, tag.Name));
            RebuildChips();
            _ = RefreshResultsAsync();
            Raise();
        });
        Grid.SetColumn(add, 1);

        var deny = IconButton("\uE711", "排除该标签", () =>
        {
            _include.RemoveAll(x => x.Id == tag.Id);
            if (!_exclude.Any(x => x.Id == tag.Id)) _exclude.Add((tag.Id, tag.Name));
            RebuildChips();
            _ = RefreshResultsAsync();
            Raise();
        });
        Grid.SetColumn(deny, 2);

        grid.Children.Add(label);
        grid.Children.Add(add);
        grid.Children.Add(deny);

        var row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 3, 6, 3),
            Content = grid,
        };
        _resultHost.Children.Add(row);
    }

    private static Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 11 },
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }
}
