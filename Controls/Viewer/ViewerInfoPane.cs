using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 右侧信息面板：照搬 Photos Lightbox 的「信息」侧板。
/// 分节依次为 快捷操作、文件名、日期和时间、大小信息、源和文件夹位置。
/// </summary>
public sealed class ViewerInfoPane : Grid
{
    private readonly Border _chrome;
    private readonly StackPanel _sections = new() { Spacing = 0 };
    private readonly StackPanel _actions = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        Margin = new Thickness(0, 4, 0, 12),
    };
    private readonly TextBlock _fileName = Value();
    private readonly TextBlock _dateTime = Value();
    private readonly TextBlock _time = Value();
    private readonly TextBlock _dimensions = Value();
    private readonly TextBlock _size = Value();
    private readonly TextBlock _source = Value();
    private readonly HyperlinkButton _location = new()
    {
        Padding = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Left,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        FontSize = 13,
    };
    private readonly TextBlock _locationText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    private ViewerChrome.Palette _palette = ViewerChrome.Palette.Dark;

    public event Action? CloseRequested;
    public event Action? CopyPathRequested;
    public event Action? OpenLocationRequested;
    public event Action? SetBackgroundRequested;
    public event Action? ResizeRequested;

    public ViewerInfoPane()
    {
        Width = ViewerChrome.InfoPaneWidth;
        HorizontalAlignment = HorizontalAlignment.Right;
        _chrome = new Border
        {
            CornerRadius = new CornerRadius(8, 0, 0, 8),
            Padding = new Thickness(16, 0, 16, 16),
        };

        var close = ViewerChrome.IconButton(ViewerChrome.Glyph.Close, "关闭", _palette, 32, 14);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => CloseRequested?.Invoke();

        var header = new Grid { Height = 48 };
        _title.Text = "信息";
        _title.Margin = new Thickness(0, 0, 0, 0);
        _title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_title);
        header.Children.Add(close);

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(header);
        body.Children.Add(_actions);
        body.Children.Add(Section("文件名", [_fileName]));
        body.Children.Add(Section("日期和时间", [_dateTime, _time]));
        body.Children.Add(Section("大小信息", [_dimensions, _size]));
        body.Children.Add(SourceSection());
        _sections.Children.Add(body);

        var scroll = new ScrollViewer
        {
            Content = _sections,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        _chrome.Child = scroll;
        Children.Add(_chrome);

        _location.Click += (_, _) => OpenLocationRequested?.Invoke();
        BuildActions();
        ApplyTheme(ActualTheme);
        ActualThemeChanged += (_, _) => ApplyTheme(ActualTheme);
    }

    private static TextBlock Value() => new()
    {
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 32, 0),
    };

    private StackPanel Section(string label, TextBlock[] values)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 2) });
        foreach (var value in values) panel.Children.Add(value);
        return panel;
    }

    private StackPanel SourceSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(new TextBlock { Text = "源和文件夹位置", FontSize = 12, Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(_source);
        panel.Children.Add(_location);
        return panel;
    }

    private void BuildActions()
    {
        _actions.Children.Add(Action("调整大小", ViewerChrome.Glyph.Resize, () => ResizeRequested?.Invoke()));
        _actions.Children.Add(Action("复制路径", ViewerChrome.Glyph.Copy, () => CopyPathRequested?.Invoke()));
        _actions.Children.Add(Action("打开位置", ViewerChrome.Glyph.OpenFolder, () => OpenLocationRequested?.Invoke()));
        _actions.Children.Add(Action("设为背景", ViewerChrome.Glyph.Wallpaper, () => SetBackgroundRequested?.Invoke()));
    }

    private Button Action(string text, string glyph, Action action)
    {
        var stack = new StackPanel();
        stack.Children.Add(ViewerChrome.Icon(glyph, 18, _palette.Text));
        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });
        var button = new Button
        {
            Content = stack,
            Width = 79,
            Height = 72,
            Padding = new Thickness(4),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = ViewerChrome.Transparent(),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    public void ApplyTheme(ElementTheme theme)
    {
        _palette = ViewerChrome.Palette.For(theme);
        _chrome.Background = ViewerChrome.Brush(_palette.Pane);
        _title.Foreground = ViewerChrome.Brush(_palette.Text);
        foreach (var block in new[] { _fileName, _dateTime, _time, _dimensions, _size, _source })
            block.Foreground = ViewerChrome.Brush(_palette.Text);
        _location.Foreground = ViewerChrome.Brush(_palette.Text);
        _locationText.Foreground = ViewerChrome.Brush(_palette.Text);
        Recolor(_sections);
    }

    private void Recolor(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is TextBlock block)
            {
                if (block.FontSize <= 12) block.Foreground = ViewerChrome.Brush(_palette.SecondaryText);
            }
            else if (child is FontIcon icon) icon.Foreground = ViewerChrome.Brush(_palette.Text);
            Recolor(child);
        }
    }

    public sealed record Info(string Path, bool IsVideo, int Width, int Height, long Size, DateTime ModifiedAt, TimeSpan Duration);

    public void Update(Info info)
    {
        _fileName.Text = System.IO.Path.GetFileName(info.Path);
        _dateTime.Text = info.ModifiedAt.ToString("D");
        _time.Text = info.ModifiedAt.ToString("t");
        _dimensions.Text = info.Width > 0 && info.Height > 0 ? $"{info.Width} × {info.Height}" : (info.IsVideo ? "视频" : "—");
        var size = ViewerChrome.FormatSize(info.Size);
        if (info.IsVideo && info.Duration > TimeSpan.Zero)
        {
            var duration = info.Duration.TotalHours >= 1 ? info.Duration.ToString(@"h\:mm\:ss") : info.Duration.ToString(@"m\:ss");
            _size.Text = $"{size}  ·  {duration}";
        }
        else _size.Text = size;
        _source.Text = "此电脑";
        _locationText.Text = info.Path;
        _location.Content = _locationText;
    }
}
