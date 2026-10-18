using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 单图查看器右侧详情面板，照搬 galleryold preview.py（容器与美术按 photos 规范）：
/// 宽 360、圆角 12；小节「图片信息」（文件名 / 宽×高 / 目录）、七类标签贴纸、
/// 「来源」；按钮「编辑分类标签」（批次 3 接入）、「用照片打开」。
/// </summary>
public sealed class DetailPanel : Grid
{
    private PhotoItem? _item;
    private readonly TextBlock _title;
    private readonly TextBlock _formatText;
    private readonly TextBlock _dimensionText;
    private readonly TextBlock _sizeText;
    private readonly TextBlock _directoryText;
    private readonly TextBlock _source;
    private readonly StackPanel _tagsHost;
    private readonly FavoriteButton _favorite;

    public event Action<PhotoItem>? EditTagsRequested;
    public event Action<PhotoItem>? OpenExternalRequested;
    public Func<PhotoItem, bool, Task<bool>>? FavoriteRequested { get; set; }

    public DetailPanel()
    {
        bool dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        Width = 360;
        HorizontalAlignment = HorizontalAlignment.Right;
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(dark
            ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x2b, 0x2b, 0x2b)
            : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xff, 0xff, 0xff));
        BorderBrush = new SolidColorBrush(dark
            ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x4a, 0x4a, 0x4a)
            : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xde, 0xde, 0xde));

        _title = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        _formatText = InfoText();
        _dimensionText = InfoText();
        _sizeText = InfoText();
        _directoryText = InfoText();
        _tagsHost = new StackPanel { Spacing = 6 };
        _source = new TextBlock { FontSize = 13, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        _favorite = new FavoriteButton { VerticalAlignment = VerticalAlignment.Center };
        _favorite.ToggleRequested += favorite =>
            _item is null || FavoriteRequested is null
                ? Task.FromResult(_item?.Favorite ?? false)
                : FavoriteRequested(_item, favorite);

        var editBtn = new Button { Content = "编辑分类标签", HorizontalAlignment = HorizontalAlignment.Stretch };
        editBtn.Click += (_, _) => { if (_item is not null) EditTagsRequested?.Invoke(_item); };
        var openBtn = new Button { Content = "用照片打开", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
        openBtn.Click += (_, _) => { if (_item is not null) OpenExternalRequested?.Invoke(_item); };

        var infoHeader = new Grid();
        infoHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var infoTitle = new TextBlock
        {
            Text = "图片信息",
            FontSize = 13,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        infoHeader.Children.Add(infoTitle);
        Grid.SetColumn(_favorite, 1);
        infoHeader.Children.Add(_favorite);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            Content = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(16, 14, 16, 16),
                Children =
                {
                    infoHeader,
                    _title,
                    BuildInfoRow(),
                    _directoryText,
                    new TextBlock { Text = "标签", FontSize = 13, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) },
                    _tagsHost,
                    new TextBlock { Text = "来源", FontSize = 13, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) },
                    _source,
                    editBtn,
                    openBtn,
                }
            }
        };
        Children.Add(scroll);
    }

    public void SetItem(PhotoItem item)
    {
        _item = item;
        _title.Text = item.File;
        _formatText.Text = FormatOf(item.File);
        _dimensionText.Text = item.PixelWidth > 0 ? $"{item.PixelWidth} x {item.PixelHeight}" : "";
        _sizeText.Text = FormatSize(item.FileSize);
        _directoryText.Text = $"目录：{(item.DirRel.Length > 0 ? item.DirRel : "图库根目录")}";
        _source.Text = !string.IsNullOrWhiteSpace(item.PageUrl) ? item.PageUrl :
            !string.IsNullOrWhiteSpace(item.SourceUrl) ? item.SourceUrl : "未记录";
        _favorite.Favorite = item.Favorite;

        RenderTags(item.Tags);
    }

    /// <summary>标签改完后只重画标签区（PhotoItem.Tags 是 init-only，不能回写）。</summary>
    public void SetTags(IReadOnlyList<(string Category, string Name)> tags) => RenderTags(tags);

    public void SetFavorite(bool favorite) => _favorite.Favorite = favorite;

    public void SetSource(string? pageUrl, string? sourceUrl)
    {
        _source.Text = !string.IsNullOrWhiteSpace(pageUrl) ? pageUrl :
            !string.IsNullOrWhiteSpace(sourceUrl) ? sourceUrl : "未记录";
    }

    /// <summary>标签 → 展示名（词库补出的中文名）；没配就用原名。</summary>
    public Func<string, string>? DisplayName { get; set; }

    /// <summary>分级标签的悬停说明：四级依据/来源/概率；没有依据时给「未知」。</summary>
    public Func<string, string?>? RatingTooltip { get; set; }

    private void RenderTags(IReadOnlyList<(string Category, string Name)> itemTags)
    {
        // 七类分组贴纸（顺序与分类名统一取词库 Taxonomy）
        _tagsHost.Children.Clear();
        var cats = Taxonomy.Categories;
        var titles = Taxonomy.CategoryTitles;
        for (var c = 0; c < cats.Length; c++)
        {
            var cat = cats[c];
            var tags = itemTags.Where(t => t.Category == cat).ToList();
            if (tags.Count == 0) continue;
            var group = new StackPanel { Spacing = 4 };
            group.Children.Add(new TextBlock { Text = titles[c], FontSize = 12, Opacity = 0.55 });
            var row = new Tags.TagWrapPanel();
            foreach (var (category, name) in tags)
            {
                var label = DisplayName?.Invoke(name) ?? name;
                var chip = new Tags.TagChip(category, label);
                var tip = category == "rating" && RatingTooltip?.Invoke(name) is { Length: > 0 } ratingTip
                    ? ratingTip
                    : label == name ? name : $"{label} · {name}";
                ToolTipService.SetToolTip(chip, tip);
                chip.HorizontalAlignment = HorizontalAlignment.Left;
                row.Children.Add(chip);
            }
            group.Children.Add(row);
            _tagsHost.Children.Add(group);
        }
        if (itemTags.Count == 0)
            _tagsHost.Children.Add(new TextBlock { Text = "未打标签", FontSize = 13, Opacity = 0.5 });
    }

    private static TextBlock InfoText() => new()
    {
        FontSize = 13,
        Opacity = 0.7,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Photos 详情面板的三联信息行：格式 │ 尺寸 │ 文件大小（图标 + 数值，竖线分隔）。</summary>
    private StackPanel BuildInfoRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
        };
        row.Children.Add(_formatText);
        row.Children.Add(Divider());
        row.Children.Add(Part("\uE8C8", _dimensionText));
        row.Children.Add(Divider());
        row.Children.Add(Part("\uE74E", _sizeText));
        return row;
    }

    private static StackPanel Part(string glyph, TextBlock text)
    {
        var part = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6,
        };
        part.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Opacity = 0.7 });
        part.Children.Add(text);
        return part;
    }

    private static Border Divider() => new()
    {
        Width = 1,
        Height = 14,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        Opacity = 0.35,
    };

    private static string FormatOf(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? "—" : ext.TrimStart('.').ToUpperInvariant();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
