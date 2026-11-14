using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 底部电影胶片：照搬 Photos Lightbox 的悬浮胶片条。
/// 单项 96x72、间距 4、选中白边；只列当前文件夹，不递归。
/// </summary>
public sealed class ViewerFilmstrip : Grid
{
    private readonly Border _chrome;
    private readonly ScrollViewer _scroll;
    private readonly ItemsRepeater _repeater;
    private readonly List<FilmstripTile> _tiles = [];
    private readonly List<string> _paths = [];
    private ViewerChrome.Palette _palette = ViewerChrome.Palette.Dark;
    private string _current = "";

    public event Action<string>? ItemSelected;

    public ViewerFilmstrip()
    {
        _chrome = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(1),
        };
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Center;
        _repeater = new ItemsRepeater
        {
            Layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 4 },
            VerticalAlignment = VerticalAlignment.Center,
            ItemTemplate = new TileFactory(this),
        };
        _repeater.ElementPrepared += (_, args) =>
        {
            if (args.Element is FilmstripTile tile && !_tiles.Contains(tile)) _tiles.Add(tile);
        };
        _repeater.ElementClearing += (_, args) =>
        {
            if (args.Element is FilmstripTile tile) _tiles.Remove(tile);
        };
        _scroll = new ScrollViewer
        {
            Content = _repeater,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = ViewerChrome.FilmstripItemHeight,
        };
        _chrome.Child = _scroll;
        Children.Add(_chrome);
        ApplyTheme(ActualTheme);
        SizeChanged += (_, _) => ClampWidth();
    }

    public void ApplyTheme(ElementTheme theme)
    {
        _palette = ViewerChrome.Palette.For(theme);
        _chrome.Background = ViewerChrome.Brush(_palette.Panel);
        _chrome.BorderBrush = ViewerChrome.Brush(_palette.Divider);
        foreach (var tile in _tiles) tile.SetSelected(tile.Path.Equals(_current, StringComparison.OrdinalIgnoreCase));
    }

    private void ClampWidth()
    {
        // Photos 的胶片条约占内容区宽度的六成，条目多时横向滚动。
        if (Parent is not FrameworkElement parent || parent.ActualWidth <= 0) return;
        _chrome.MaxWidth = Math.Max(ViewerChrome.FilmstripItemWidth * 2 + 12, parent.ActualWidth * 0.6);
    }

    public void SetItems(IReadOnlyList<string> paths, string currentPath)
    {
        _paths.Clear();
        _paths.AddRange(paths);
        _current = currentPath;
        _tiles.Clear();
        ClampWidth();
        _repeater.ItemsSource = _paths.Select(path => new FilmstripEntry(path)).ToArray();
        DispatcherQueue.TryEnqueue(ScrollToCurrent);
    }

    private void ScrollToCurrent()
    {
        int index = _paths.FindIndex(path => path.Equals(_current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        double pitch = ViewerChrome.FilmstripItemWidth + 4;
        double target = index * pitch - Math.Max(0, _scroll.ActualWidth - pitch) / 2;
        _scroll.ChangeView(Math.Max(0, target), null, null, true);
    }

    internal void RaiseSelect(string path) => ItemSelected?.Invoke(path);
    internal ViewerChrome.Palette Palette => _palette;
    internal string Current => _current;

    private sealed record FilmstripEntry(string Path);

    private sealed class FilmstripTile : Grid
    {
        private readonly ViewerFilmstrip _owner;
        private readonly Border _frame;
        private readonly Image _image;
        public string Path { get; private set; } = "";

        public FilmstripTile(ViewerFilmstrip owner)
        {
            _owner = owner;
            Width = ViewerChrome.FilmstripItemWidth;
            Height = ViewerChrome.FilmstripItemHeight;
            _frame = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(2),
                BorderBrush = ViewerChrome.Transparent(),
                Background = ViewerChrome.Brush(Color.FromArgb(255, 0, 0, 0)),
            };
            _image = new Image { Stretch = Stretch.UniformToFill };
            _frame.Child = _image;
            Children.Add(_frame);
            PointerPressed += (_, _) => _owner.RaiseSelect(Path);
        }

        public void Bind(FilmstripEntry entry)
        {
            if (Path != entry.Path)
            {
                Path = entry.Path;
                _image.Source = null;
                ToolTipService.SetToolTip(this, System.IO.Path.GetFileName(entry.Path));
                try
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = (int)ViewerChrome.FilmstripItemWidth * 2 };
                    bitmap.UriSource = new Uri(entry.Path);
                    _image.Source = bitmap;
                }
                catch (Exception)
                {
                    // 无法解码的条目保留空底，不影响其他缩略图。
                }
            }
            SetSelected(entry.Path.Equals(_owner.Current, StringComparison.OrdinalIgnoreCase));
        }

        public void SetSelected(bool selected)
        {
            _frame.BorderBrush = selected
                ? ViewerChrome.Brush(_owner.Palette.SelectedBorder)
                : ViewerChrome.Transparent();
        }
    }

    private sealed class TileFactory : IElementFactory
    {
        private readonly ViewerFilmstrip _owner;
        public TileFactory(ViewerFilmstrip owner) => _owner = owner;

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var tile = new FilmstripTile(_owner);
            if (args.Data is FilmstripEntry entry) tile.Bind(entry);
            return tile;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) { }
    }
}
