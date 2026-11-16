// 筛选面板：媒体类型 + 标签（匹配方式、仅未打标签、子文件夹收在齿轮菜单里）。
// 状态由 GalleryPage 持有，面板只负责渲染与回调；每次改动立刻应用（Photos 同款即时筛选）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Gallery.Controls.Tags;
using Gallery.Services;

namespace Gallery.Controls;

public sealed class FilterPanel
{
    public enum MediaKind { All, Photos, Videos }

    // Flyout 自带 16 的四周留白，内容宽度由媒体类型那一行决定。
    private const double ControlHeight = 32;
    private const double ListHeight = 176;
    private const double GearSize = 28;
    // 图标字形的墨水比中文偏上，垂直补偿；标题另作视觉位置调整。
    private const double GlyphNudge = 1;
    private const string MediaGroup = "galleryMediaKind";

    private readonly TagWrapPanel _chipHost = new() { Margin = new Thickness(0, 2, 0, 0), MinHeight = 28 };
    private readonly StackPanel _resultHost = new() { Spacing = 1 };
    private readonly SearchInputBox _search = new() { PlaceholderText = "搜索标签", Height = ControlHeight };

    private readonly RadioButton _allMedia = new() { Content = "所有媒体", GroupName = MediaGroup, MinWidth = 0, MinHeight = ControlHeight, IsChecked = true, Margin = new Thickness(0, 0, 20, 0) };
    private readonly RadioButton _photoMedia = new() { Content = "照片", GroupName = MediaGroup, MinWidth = 0, MinHeight = ControlHeight, Margin = new Thickness(0, 0, 20, 0) };
    private readonly RadioButton _videoMedia = new() { Content = "视频", GroupName = MediaGroup, MinWidth = 0, MinHeight = ControlHeight };

    private readonly RadioMenuFlyoutItem _modeAny = new() { Text = "任一标签", GroupName = "galleryTagMode", IsChecked = true };
    private readonly RadioMenuFlyoutItem _modeAll = new() { Text = "全部标签", GroupName = "galleryTagMode" };
    private readonly ToggleMenuFlyoutItem _untagged = new() { Text = "只看没打标签的" };
    private readonly ToggleMenuFlyoutItem _subfolder = new() { Text = "显示子文件夹内容" };

    private readonly Button _clearAll;
    private readonly Button _gear;

    private readonly List<(int Id, string Name)> _include = [];
    private readonly Dictionary<string, string> _displayBy = new(StringComparer.Ordinal);

    public UIElement Root { get; }
    public event Action? Changed;

    public MediaKind Media
    {
        get => _photoMedia.IsChecked == true ? MediaKind.Photos : _videoMedia.IsChecked == true ? MediaKind.Videos : MediaKind.All;
        set
        {
            _allMedia.IsChecked = value == MediaKind.All;
            _photoMedia.IsChecked = value == MediaKind.Photos;
            _videoMedia.IsChecked = value == MediaKind.Videos;
        }
    }

    public IReadOnlyList<(int Id, string Name)> Include => _include;
    public string Mode => _modeAll.IsChecked ? "and" : "or";
    public bool UntaggedOnly => _untagged.IsChecked;
    public bool Subfolder => _subfolder.IsChecked;

    /// <summary>目录页才显示「显示子文件夹内容」，并按设置回填。</summary>
    public void SyncSubfolder(bool recursive, bool visible)
    {
        _subfolder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _subfolder.IsChecked = recursive;
    }

    public FilterPanel()
    {
        var mediaRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = ControlHeight,
            Children = { _allMedia, _photoMedia, _videoMedia },
        };
        _allMedia.Click += (_, _) => Raise();
        _photoMedia.Click += (_, _) => Raise();
        _videoMedia.Click += (_, _) => Raise();

        // 清除全部：常驻红色，和贴纸里的白色移除叉区分；无已选标签时整颗隐藏。
        _clearAll = DeleteButton(8, true, GlyphNudge);
        _clearAll.RenderTransform = new TranslateTransform { Y = 1 };
        _clearAll.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(_clearAll, "清除所有标签筛选");
        _clearAll.Click += (_, _) =>
        {
            _include.Clear();
            RebuildChips();
            _ = RefreshResultsAsync();
            Raise();
        };

        // 和标签贴纸一样定高 28、子元素居中；标题字号不同，红叉另下移 1 以调整视觉中心。
        var titleRow = new Border
        {
            Height = 28,
            Margin = new Thickness(0, 11, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "标签", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center },
                    _clearAll,
                },
            },
        };

        // 齿轮叠在搜索框右端，点它打开筛选选项（匹配方式 + 两个勾选）。
        var menu = new MenuFlyout();
        menu.Items.Add(_modeAny);
        menu.Items.Add(_modeAll);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_untagged);
        menu.Items.Add(_subfolder);
        _modeAny.Click += (_, _) => Raise();
        _modeAll.Click += (_, _) => Raise();
        _untagged.Click += (_, _) => Raise();
        _subfolder.Click += (_, _) => Raise();

        // 图标用三条右端递减的横线，不借用全局设置齿轮。
        var bars = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), "M2,4 H14 M2,8 H10 M2,12 H6"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _gear = new Button
        {
            Content = bars,
            Width = GearSize,
            Height = GearSize,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Flyout = menu,
        };
        ToolTipService.SetToolTip(_gear, "筛选选项");
        bars.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = _gear,
            Path = new PropertyPath(nameof(Control.Foreground)),
        });

        var searchRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        searchRow.Children.Add(_search);
        searchRow.Children.Add(_gear);

        var scroll = new ScrollViewer
        {
            Height = ListHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // 浮层滚动条会盖住结果行的右端，行的右侧留 16（UI_CONVENTIONS.md R1）。
            Padding = new Thickness(0, 0, 16, 0),
            Margin = new Thickness(0, 6, 0, 0),
            Content = _resultHost,
        };

        // 宽度跟随媒体类型那一行，右侧不留空白；TagWrapPanel 只在给定宽度内换行。
        var body = new Grid();
        foreach (var _ in Enumerable.Range(0, 5)) body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(mediaRow, 0);
        Grid.SetRow(titleRow, 1);
        Grid.SetRow(_chipHost, 2);
        Grid.SetRow(searchRow, 3);
        Grid.SetRow(scroll, 4);
        body.Children.Add(mediaRow);
        body.Children.Add(titleRow);
        body.Children.Add(_chipHost);
        body.Children.Add(searchRow);
        body.Children.Add(scroll);
        body.Loaded += (_, _) =>
        {
            var width = mediaRow.DesiredSize.Width;
            if (width > 0 && double.IsNaN(body.Width)) body.Width = Math.Ceiling(width);
        };

        Root = body;
        _search.TextChanged += (_, _) =>
        {
            // 模板里的清除叉可能晚于 Loaded 生成，每次输入顺带校正一次位置。
            InsetSearchDeleteButton();
            _ = RefreshResultsAsync();
        };
        _search.Loaded += (_, _) => InsetSearchDeleteButton();
        RebuildChips();
        _ = RefreshResultsAsync();
    }

    /// <summary>搜索框自带的清除叉：复用默认样式，只把位置让出齿轮的宽度。</summary>
    private void InsetSearchDeleteButton()
    {
        foreach (var button in Descendants<Button>(_search))
        {
            if (!string.Equals(button.Name, "DeleteButton", StringComparison.Ordinal)) continue;
            button.Margin = new Thickness(0, 0, GearSize + 4, 0);
            return;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// 清除/移除叉。danger 为真时固定红色（清除全部），否则沿用默认前景（移除单个）。
    /// 图标字形的墨水比中文偏上，nudgeY 是字形的垂直补偿。
    /// </summary>
    private static Button DeleteButton(double iconSize, bool danger, double nudgeY)
    {
        var side = iconSize + 9;
        var glyph = new TextBlock
        {
            Text = "\uE711",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = iconSize,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, nudgeY, 0, 0),
        };
        var button = new Button
        {
            Content = glyph,
            Width = side,
            Height = side,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (danger) button.Foreground = DangerBrush();
        glyph.SetBinding(TextBlock.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = button,
            Path = new PropertyPath(nameof(Control.Foreground)),
        });
        return button;
    }

    private static Brush DangerBrush() =>
        new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));

    private void Raise() => Changed?.Invoke();

    /// <summary>
    /// 外部把一个标签并进筛选条件（详情面板右键「按此标签进行筛选」），
    /// 和点候选行同一条路径：已选就跳过，未选就加、记一次使用、立刻生效。
    /// </summary>
    public void IncludeTag(int id, string name)
    {
        if (_include.Any(x => x.Id == id)) return;
        _include.Add((id, name));
        _ = Task.Run(() =>
        {
            lock (AppService.DbLock)
            {
                try { AppService.Store?.TouchFilterTag(id); } catch { }
            }
        });
        RebuildChips();
        _ = RefreshResultsAsync();
        Raise();
    }

    private void RebuildChips()
    {
        _chipHost.Children.Clear();
        if (_include.Count == 0)
        {
            _clearAll.Visibility = Visibility.Collapsed;
            _chipHost.Children.Add(new TextBlock
            {
                Text = "未选择标签",
                FontSize = 13,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }
        _clearAll.Visibility = Visibility.Visible;
        foreach (var (id, name) in _include) _chipHost.Children.Add(BuildChip(id, name));
    }

    private UIElement BuildChip(int id, string name)
    {
        var text = new TextBlock
        {
            Text = _displayBy.TryGetValue(name, out var shown) ? shown : name,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var remove = DeleteButton(8, false, GlyphNudge);
        ToolTipService.SetToolTip(remove, "移除该标签");
        remove.Click += (_, _) =>
        {
            _include.RemoveAll(x => x.Id == id);
            RebuildChips();
            _ = RefreshResultsAsync();
            Raise();
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { text, remove },
        };

        return new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 2, 0),
            Child = content,
        };
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
                        ? Ranked(store)
                        : store.SearchTags(query, 12);
                }
                catch { }
            }
        });
        if (query != _search.Text.Trim()) return;

        foreach (var tag in hits) AddResultRow(tag);
        if (_resultHost.Children.Count == 0)
            _resultHost.Children.Add(new TextBlock { Text = "没有匹配的标签", FontSize = 12, Opacity = 0.55 });
    }

    /// <summary>最近用过的排最前，其余按使用次数。两个口径都不缺，新库也不至于空列表。</summary>
    private static List<StoreService.TagInfo> Ranked(StoreService store)
    {
        var recent = store.RecentFilterTags(30);
        var rank = new Dictionary<int, int>(recent.Count);
        for (var i = 0; i < recent.Count; i++) rank[recent[i]] = i;
        return store.AllTags()
            .OrderBy(t => rank.TryGetValue(t.Id, out var r) ? r : int.MaxValue)
            .ThenByDescending(t => t.Count)
            .Take(14)
            .ToList();
    }

    private void AddResultRow(StoreService.TagInfo tag)
    {
        _displayBy[tag.Name] = tag.Display;
        bool included = _include.Any(x => x.Id == tag.Id);

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mark = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = included ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(mark, 0);

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Children.Add(new TextBlock { Text = tag.Display, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        if (tag.Display != tag.Name)
            label.Children.Add(new TextBlock { Text = tag.Name, FontSize = 11, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(label, 1);

        var count = new TextBlock
        {
            Text = $"已用 {tag.Count}",
            FontSize = 11,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 2);

        grid.Children.Add(mark);
        grid.Children.Add(label);
        grid.Children.Add(count);

        var row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = ControlHeight,
            CornerRadius = new CornerRadius(4),
            Background = included ? SelectedBrush() : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 6, 0),
            Content = grid,
        };
        row.Click += (_, _) => Toggle(tag);
        _resultHost.Children.Add(row);
    }

    private static Brush SelectedBrush()
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(ColorHelper.FromArgb(0x1A, accent.R, accent.G, accent.B));
    }

    private void Toggle(StoreService.TagInfo tag)
    {
        var index = _include.FindIndex(x => x.Id == tag.Id);
        if (index >= 0) _include.RemoveAt(index);
        else
        {
            _include.Add((tag.Id, tag.Name));
            var id = tag.Id;
            _ = Task.Run(() =>
            {
                lock (AppService.DbLock)
                {
                    try { AppService.Store?.TouchFilterTag(id); } catch { }
                }
            });
        }
        RebuildChips();
        _ = RefreshResultsAsync();
        Raise();
    }
}
