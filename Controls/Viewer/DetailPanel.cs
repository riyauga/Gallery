using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Gallery.Models;

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
    private readonly TextBlock _meta;
    private readonly TextBlock _source;
    private readonly StackPanel _tagsHost;

    public event Action<PhotoItem>? EditTagsRequested;
    public event Action<PhotoItem>? OpenExternalRequested;

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
        _meta = new TextBlock { FontSize = 13, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        _tagsHost = new StackPanel { Spacing = 6 };
        _source = new TextBlock { FontSize = 13, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

        var editBtn = new Button { Content = "编辑分类标签", HorizontalAlignment = HorizontalAlignment.Stretch };
        editBtn.Click += (_, _) => { if (_item is not null) EditTagsRequested?.Invoke(_item); };
        var openBtn = new Button { Content = "用照片打开", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
        openBtn.Click += (_, _) => { if (_item is not null) OpenExternalRequested?.Invoke(_item); };

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
                    new TextBlock { Text = "图片信息", FontSize = 13, Opacity = 0.6 },
                    _title,
                    _meta,
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
        _meta.Text = item.PixelWidth > 0
            ? $"{item.PixelWidth} × {item.PixelHeight}\n目录：{(item.DirRel.Length > 0 ? item.DirRel : "图库根目录")}"
            : "";
        _source.Text = item.PageUrl ?? item.SourceUrl ?? "未记录";

        // 七类分组贴纸（顺序照 taxonomy）
        _tagsHost.Children.Clear();
        string[] cats = ["work", "character", "artist", "kind", "rating", "resolution", "custom"];
        string[] titles = ["作品", "角色", "画师", "类型", "分级", "分辨率", "自定义"];
        for (var c = 0; c < cats.Length; c++)
        {
            var cat = cats[c];
            var tags = item.Tags.Where(t => t.Category == cat).ToList();
            if (tags.Count == 0) continue;
            var group = new StackPanel { Spacing = 4 };
            group.Children.Add(new TextBlock { Text = titles[c], FontSize = 12, Opacity = 0.55 });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var (category, name) in tags)
            {
                var chip = new Tags.TagChip(category, name);
                chip.HorizontalAlignment = HorizontalAlignment.Left;
                row.Children.Add(chip);
            }
            group.Children.Add(row);
            _tagsHost.Children.Add(group);
        }
        if (item.Tags.Count == 0)
            _tagsHost.Children.Add(new TextBlock { Text = "未打标签", FontSize = 13, Opacity = 0.5 });
    }
}
