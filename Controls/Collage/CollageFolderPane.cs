using System.IO;
using Gallery.Services;
using Gallery.Controls.Viewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Gallery.Controls.Collage;

/// <summary>左侧目录区：当前物理文件夹的静态图，点一下追加，拖出去可以插到画布指定位置。</summary>
public sealed class CollageFolderPane : Grid
{
    private const double TileSize = 72;

    private readonly TextBlock _header = new() { FontSize = 11, Margin = new Thickness(12, 14, 12, 10) };
    private readonly TextBlock _empty = new() { FontSize = 12, Margin = new Thickness(12, 4, 12, 0), Visibility = Visibility.Collapsed };
    private readonly ItemsRepeater _repeater = new();
    private readonly ScrollViewer _scroll = new() { HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly List<string> _paths = [];

    public CollageFolderPane()
    {
        Width = 252;
        _repeater.Layout = new UniformGridLayout
        {
            MinItemWidth = TileSize,
            MinItemHeight = TileSize,
            MinColumnSpacing = 6,
            MinRowSpacing = 6,
            ItemsJustification = UniformGridLayoutItemsJustification.Start,
            ItemsStretch = UniformGridLayoutItemsStretch.None,
        };
        _repeater.ItemTemplate = new TileFactory(this);
        _scroll.Content = _repeater;
        _scroll.Margin = new Thickness(8, 0, 4, 8);

        // 不能把 ScrollViewer 塞进垂直 StackPanel：StackPanel 在竖直方向给子元素无限高度，
        // ScrollViewer 会认为全内容都可见，于是既不出滚动条也不响应滚轮。
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(_header);
        Grid.SetRow(_empty, 1);
        Children.Add(_empty);
        Grid.SetRow(_scroll, 2);
        Children.Add(_scroll);
    }

    public event Action<string>? AddRequested;

    public void SetFolder(string folder, IEnumerable<string> paths)
    {
        _paths.Clear();
        _paths.AddRange(paths);
        _header.Text = string.IsNullOrEmpty(folder) ? "同文件夹" : "同文件夹 · " + Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        _empty.Text = "这个文件夹里没有可拼接的静态图片。";
        _empty.Visibility = _paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = _paths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _repeater.ItemsSource = null;
        _repeater.ItemsSource = _paths;
    }

#if GALLERY_DEVELOPMENT
    public double ScrollableHeight => _scroll.ScrollableHeight;
    public void ScrollBy(double offset) => _scroll.ChangeView(null, offset, null);
#endif

    public void ApplyTheme(ElementTheme theme)
    {
        var palette = ViewerChrome.Palette.For(theme);
        _hoverColor = palette.SecondaryText;
        _header.Foreground = ViewerChrome.Brush(palette.SecondaryText);
        _empty.Foreground = ViewerChrome.Brush(palette.SecondaryText);
        Background = ViewerChrome.Brush(palette.Pane);
    }

    private sealed class Tile : Grid
    {
        private readonly Border _frame;
        private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
        public string Path { get; private set; } = "";

        public Tile(CollageFolderPane owner)
        {
            Width = TileSize;
            Height = TileSize;
            Background = ViewerChrome.Transparent();
            CanDrag = true;
            _frame = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 20, 20)),
                BorderThickness = new Thickness(2),
                BorderBrush = ViewerChrome.Transparent(),
            };
            _frame.Child = _image;
            Children.Add(_frame);

            // 用 Tapped 而不是 PointerPressed：拖动会被系统拖拽接管，Tapped 就不会再触发，避免「拖一次加两张」。
            Tapped += (_, _) => owner.AddRequested?.Invoke(Path);
            PointerEntered += (_, _) => _frame.BorderBrush = ViewerChrome.Brush(owner._hoverColor);
            PointerExited += (_, _) => _frame.BorderBrush = ViewerChrome.Transparent();
            DragStarting += async (_, args) =>
            {
                if (Path.Length == 0) { args.Cancel = true; return; }
                var deferral = args.GetDeferral();
                try
                {
                    // 不能在 UI 线程同步等 WinRT 异步，否则会卡死甚至把进程拖崩。
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path);
                    args.Data.SetStorageItems(new List<Windows.Storage.IStorageItem> { file });
                    args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                }
                catch (Exception error)
                {
                    FaultLog.Write("collage-drag", error);
                    args.Cancel = true;
                }
                finally { deferral.Complete(); }
            };
        }

        public void Bind(string path)
        {
            if (Path == path) return;
            Path = path;
            _image.Source = null;
            ToolTipService.SetToolTip(this, System.IO.Path.GetFileName(path));
            AutomationProperties.SetName(this, System.IO.Path.GetFileName(path));
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = (int)TileSize * 2 };
                bitmap.UriSource = new Uri(path);
                _image.Source = bitmap;
            }
            catch (Exception) { /* 解不了的条目留空底 */ }
        }
    }

    private Windows.UI.Color _hoverColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);

    private sealed class TileFactory : IElementFactory
    {
        private readonly CollageFolderPane _owner;
        public TileFactory(CollageFolderPane owner) => _owner = owner;

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var tile = new Tile(_owner);
            if (args.Data is string path) tile.Bind(path);
            return tile;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) { }
    }
}
