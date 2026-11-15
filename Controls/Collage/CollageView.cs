using System.IO;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Gallery.Controls.Viewer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Controls.Collage;

/// <summary>
/// 拼图模式宿主：顶部工具条（齿轮 + 撤销重做 + 保存）、左侧目录区、中央画布。
/// 参数收在齿轮的 Flyout 里，按「方向 / 尺寸 / 对齐 / 对齐对象」分组；
/// 不适用的分组直接不出现，出现时带进场动效。
/// </summary>
public sealed class CollageView : Grid
{
    private const double BarHeight = 48;
    private const double ControlHeight = 32;

    private static readonly HashSet<string> StaticExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".webp", ".bmp", ".tif", ".tiff",
    };

    private readonly CollageCanvas _canvas = new();
    private readonly CollageFolderPane _folder = new();
    private readonly Button _gear, _undo, _redo, _save;
    private readonly SegmentGroup _axis, _scale, _align, _target;
    private readonly StackPanel _alignBlock, _targetBlock;
    private readonly Border _barBorder, _folderDivider;
    private ViewerChrome.Palette _palette = ViewerChrome.Palette.Dark;

    private readonly List<List<(string Path, int Width, int Height, double Offset)>> _undoStack = [];
    private readonly List<List<(string Path, int Width, int Height, double Offset)>> _redoStack = [];
    private List<(string Path, int Width, int Height, double Offset)> _baseline = [];
    private List<(string Path, int Width, int Height, double Offset)> _current = [];
    private bool _restoring, _ready;

    public CollageView()
    {
        _axis = new SegmentGroup(_palette, "横向", "纵向");
        _scale = new SegmentGroup(_palette, "统一边长", "原始像素");
        _align = new SegmentGroup(_palette, "上", "中", "下", "自由");
        _target = new SegmentGroup(_palette, "首图", "上一张");

        _axis.SelectedChanged += index => { if (_ready) OnAxisChanged(index); };
        _scale.SelectedChanged += index => { if (_ready) OnScaleChanged(index); };
        _align.SelectedChanged += index => { if (_ready) { _canvas.SetAlign((CollageAlign)Math.Clamp(index, 0, 3)); SyncVisibility(); } };
        _target.SelectedChanged += index => { if (_ready) _canvas.SetTarget((CollageTarget)Math.Clamp(index, 0, 1)); };

        _alignBlock = BuildBlock("对齐", _align);
        _targetBlock = BuildBlock("对齐对象", _target);
        _alignBlock.Visibility = Visibility.Collapsed;
        _targetBlock.Visibility = Visibility.Collapsed;

        _gear = IconButton(ViewerChrome.Glyph.Settings, "拼图设置");
        _gear.Flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom, Content = BuildPanel() };
        _undo = IconButton(ViewerChrome.Glyph.Undo, "撤销 (Ctrl+Z)");
        _redo = IconButton(ViewerChrome.Glyph.Redo, "重做 (Ctrl+Y)");
        _save = new Button
        {
            Content = Text("保存"),
            Height = ControlHeight,
            MinWidth = 76,
            Padding = new Thickness(16, 0, 16, 0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _undo.Click += (_, _) => Undo();
        _redo.Click += (_, _) => Redo();
        _save.Click += (_, _) => RenderRequested?.Invoke();

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        left.Children.Add(_gear);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(_undo);
        right.Children.Add(_redo);
        right.Children.Add(new Border { Width = 6 });
        right.Children.Add(_save);

        var bar = new Grid { Height = BarHeight, Padding = new Thickness(12, 0, 12, 0), ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(left);
        Grid.SetColumn(right, 1);
        bar.Children.Add(right);

        _barBorder = new Border { Height = BarHeight, Child = bar };
        _folderDivider = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Right };

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(252) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumnSpan(_barBorder, 2);
        Children.Add(_barBorder);
        Grid.SetRow(_folder, 1);
        Children.Add(_folder);
        Children.Add(_folderDivider);
        Grid.SetRow(_folderDivider, 1);
        Grid.SetRow(_canvas, 1);
        Grid.SetColumn(_canvas, 1);
        Children.Add(_canvas);

        var canvasMenu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem { Text = "复制到剪贴板" };
        copyItem.Click += (_, _) => CopyRequested?.Invoke();
        canvasMenu.Items.Add(copyItem);
        _canvas.ContextFlyout = canvasMenu;

        _canvas.Changed += OnCanvasChanged;
        _canvas.Status += text => Status?.Invoke(text);
        _folder.AddRequested += path => _canvas.Add(path);
        _ready = true;
        ApplyTheme(ElementTheme.Dark);
    }

    public CollageCanvas Canvas => _canvas;
    public event Action? RenderRequested;
    public event Action? CopyRequested;
    /// <summary>需要提示用户的一句话，由宿主窗口统一展示。</summary>
    public event Action<string>? Status;

    public CollageLayoutResult Layout => _canvas.Layout;
    public bool IsSaving { get; set; }

    // ---------- 打开与导出 ----------

    public async Task OpenAsync(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var files = await Task.Run(() => Directory.EnumerateFiles(folder)
            .Where(file => StaticExts.Contains(Path.GetExtension(file)))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray());
        _folder.SetFolder(folder, files);
        _canvas.RootFolder = folder;
        _canvas.Add(path);
        _baseline = _canvas.Snapshot();
        _current = _canvas.Snapshot();
    }

    public bool HasChanges()
    {
        if (_baseline.Count != _current.Count) return true;
        for (int index = 0; index < _baseline.Count; index++)
            if (!_baseline[index].Equals(_current[index])) return true;
        return false;
    }

    public Task<Image<Rgba32>?> RenderAsync() =>
        CollageService.RenderAsync(_canvas.Entries, _canvas.Layout.Width, _canvas.Layout.Height);

#if GALLERY_DEVELOPMENT
    /// <summary>开发自测：把四个参数各切一遍，用于验证回调不崩。</summary>
    public void RunSelfTest()
    {
        // 全部切一遍确认回调不炸，最后停在「原始像素 + 自由」，此时两块子项都应可见。
        _axis.SelectedIndex = 1;
        _scale.SelectedIndex = 1;
        _align.SelectedIndex = 3;
        _target.SelectedIndex = 1;
        _axis.SelectedIndex = 0;
        _align.SelectedIndex = 1;
        _scale.SelectedIndex = 0;
        _target.SelectedIndex = 0;
        _scale.SelectedIndex = 1;
        _align.SelectedIndex = 3;
        _target.SelectedIndex = 1;
    }

    public string DumpState() =>
        $"axis={_axis.SelectedIndex} scale={_scale.SelectedIndex} align={_align.SelectedIndex} target={_target.SelectedIndex} " +
        $"alignVisible={_alignBlock.Visibility} targetVisible={_targetBlock.Visibility} layout={_canvas.Layout.Width:0}x{_canvas.Layout.Height:0}";

    /// <summary>开发自测用：把当前拼图界面渲染成位图，避免外部抓屏拿到合成层旧帧。</summary>
    public async Task<bool> SaveSnapshotAsync(string path)
    {
        try
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(this);
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                reader.ReadBytes(pixels);
            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
            await image.SaveAsPngAsync(path);
            return true;
        }
        catch (Exception error)
        {
            FaultLog.Write("collage-snapshot", error);
            return false;
        }
    }
#endif

    // ---------- 面板 ----------

    private StackPanel BuildPanel()
    {
        var panel = new StackPanel { Width = 260 };
        panel.Children.Add(Section("拼接方向"));
        panel.Children.Add(_axis);
        panel.Children.Add(Section("尺寸", top: 14));
        panel.Children.Add(_scale);
        panel.Children.Add(_alignBlock);
        panel.Children.Add(_targetBlock);
        return panel;
    }

    private static TextBlock Section(string text, double top = 0) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = .65,
        Margin = new Thickness(0, top, 0, 6),
    };

    private static StackPanel BuildBlock(string caption, UIElement content)
    {
        var block = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        block.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 12),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 62, 62, 64)),
        });
        block.Children.Add(Section(caption));
        block.Children.Add(content);
        block.RenderTransform = new TranslateTransform();
        return block;
    }

    private void OnAxisChanged(int index)
    {
        // 横向是上/中/下，纵向是左/中/右：只换文案，不重建控件。
        _align.SetLabels(index == 1 ? ["左", "中", "右", "自由"] : ["上", "中", "下", "自由"]);
        _canvas.SetAxis((CollageAxis)index);
    }

    private void OnScaleChanged(int index)
    {
        _canvas.SetScaleMode((CollageScaleMode)index);
        if (index == 0 && _align.SelectedIndex == 3)
        {
            // 统一边长下没有自由可言，回到居中，避免切回来时状态不明。
            _align.SelectedIndex = 1;
            _canvas.SetAlign(CollageAlign.Center);
        }
        SyncVisibility();
    }

    /// <summary>原始像素才出现对齐，自由档才出现对齐对象；出现时滑入，不出现时直接收起。</summary>
    private void SyncVisibility()
    {
        bool original = _scale.SelectedIndex == 1;
        bool free = original && _align.SelectedIndex == 3;
        Reveal(_alignBlock, original);
        Reveal(_targetBlock, free);
    }

    private static void Reveal(FrameworkElement element, bool show)
    {
        if (!show)
        {
            if (element.Visibility == Visibility.Visible) element.Visibility = Visibility.Collapsed;
            return;
        }
        if (element.Visibility == Visibility.Visible) return;
        element.Visibility = Visibility.Visible;
        if (element.RenderTransform is not TranslateTransform shift) return;
        element.Opacity = 0;
        var storyboard = new Storyboard();
        void Add(string property, double from, double to, DependencyObject target)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(180),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
        Add("Y", -8, 0, shift);
        Add("Opacity", 0, 1, element);
        storyboard.Begin();
    }

    // ---------- 主题 ----------

    public void ApplyTheme(ElementTheme theme)
    {
        _palette = ViewerChrome.Palette.For(theme);
        Background = ViewerChrome.Brush(_palette.Background);
        _canvas.SetTheme(theme);
        _folder.ApplyTheme(theme);
        _barBorder.Background = ViewerChrome.Brush(_palette.Bar);
        _barBorder.BorderBrush = ViewerChrome.Brush(_palette.Divider);
        _barBorder.BorderThickness = new Thickness(0, 0, 0, 1);
        _folderDivider.Background = ViewerChrome.Brush(_palette.Divider);
        _axis.ApplyTheme(_palette);
        _scale.ApplyTheme(_palette);
        _align.ApplyTheme(_palette);
        _target.ApplyTheme(_palette);
        StyleIcon(_gear);
        StyleIcon(_undo);
        StyleIcon(_redo);
        StyleSaveButton();
    }

    private void StyleIcon(Button button)
    {
        button.Background = ViewerChrome.Transparent();
        if (button.Content is FontIcon icon) icon.Foreground = ViewerChrome.Brush(_palette.Text);
    }

    /// <summary>保存按钮取与工具条反差最大的一档，不用强调色填充：强调色偏浅时白字会糊。</summary>
    private void StyleSaveButton()
    {
        bool light = _palette.Background.R > 128;
        var fill = light ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 237, 241, 242);
        var text = light ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 20, 20, 20);
        _save.Background = ViewerChrome.Brush(fill);
        if (_save.Content is TextBlock label) label.Foreground = ViewerChrome.Brush(text);
        _save.PointerEntered += (_, _) => _save.Background = ViewerChrome.Brush(Lift(fill));
        _save.PointerExited += (_, _) => _save.Background = ViewerChrome.Brush(fill);
        _save.PointerCaptureLost += (_, _) => _save.Background = ViewerChrome.Brush(fill);
    }

    private static Windows.UI.Color Lift(Windows.UI.Color color) => Windows.UI.Color.FromArgb(
        255,
        (byte)Math.Clamp(color.R + (color.R > 128 ? -14 : 14), 0, 255),
        (byte)Math.Clamp(color.G + (color.G > 128 ? -14 : 14), 0, 255),
        (byte)Math.Clamp(color.B + (color.B > 128 ? -14 : 14), 0, 255));

    // ---------- 撤销 ----------

    private void OnCanvasChanged()
    {
        if (_restoring)
        {
            _current = _canvas.Snapshot();
            return;
        }
        _undoStack.Add(_current);
        if (_undoStack.Count > 80) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        _current = _canvas.Snapshot();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Add(_current);
        var target = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _restoring = true;
        _canvas.Restore(target);
        _restoring = false;
        _current = _canvas.Snapshot();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Add(_current);
        var target = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _restoring = true;
        _canvas.Restore(target);
        _restoring = false;
        _current = _canvas.Snapshot();
    }

    // ---------- 小控件 ----------

    private Button IconButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Width = ControlHeight,
            Height = ControlHeight,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = ViewerChrome.Transparent(),
            Content = new FontIcon { Glyph = glyph, FontSize = 14, FontFamily = ViewerChrome.IconFamily },
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.PointerEntered += (_, _) => button.Background = ViewerChrome.Brush(_palette.Hover);
        button.PointerExited += (_, _) => button.Background = ViewerChrome.Transparent();
        button.PointerPressed += (_, _) => button.Background = ViewerChrome.Brush(_palette.Pressed);
        button.PointerReleased += (_, _) => button.Background = ViewerChrome.Brush(_palette.Hover);
        ViewerChrome.WirePressMotion(button);
        return button;
    }

    private static TextBlock Text(string value) => new()
    {
        Text = value,
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>面板内的一行单选：等宽分段，选中态用实底，其余用浅底。</summary>
    private sealed class SegmentGroup : Grid
    {
        private readonly List<Button> _buttons = [];
        private readonly List<TextBlock> _labels = [];
        private ViewerChrome.Palette _palette;
        private int _selected;

        public event Action<int>? SelectedChanged;

        public SegmentGroup(ViewerChrome.Palette palette, params string[] labels)
        {
            _palette = palette;
            Height = 32;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            for (int index = 0; index < labels.Length; index++)
            {
                int slot = index;
                var text = new TextBlock { Text = labels[index], FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                var button = new Button
                {
                    Content = text,
                    Height = 30,
                    MinWidth = 52,
                    Padding = new Thickness(10, 0, 10, 0),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                };
                button.Click += (_, _) => SelectedIndex = slot;
                ViewerChrome.WirePressMotion(button);
                _buttons.Add(button);
                _labels.Add(text);
                row.Children.Add(button);
            }
            Children.Add(row);
            ApplyTheme(palette);
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                int next = Math.Clamp(value, 0, _buttons.Count - 1);
                if (next == _selected) return;
                _selected = next;
                Paint();
                SelectedChanged?.Invoke(next);
            }
        }

        public void SetLabels(params string[] labels)
        {
            for (int index = 0; index < labels.Length && index < _labels.Count; index++)
                _labels[index].Text = labels[index];
        }

        public void ApplyTheme(ViewerChrome.Palette palette)
        {
            _palette = palette;
            Paint();
        }

        private void Paint()
        {
            bool light = _palette.Background.R > 128;
            var onFill = light ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 90, 90, 94);
            var offFill = light ? Windows.UI.Color.FromArgb(255, 237, 237, 237) : Windows.UI.Color.FromArgb(255, 58, 58, 58);
            var onText = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            var offText = light ? Windows.UI.Color.FromArgb(255, 40, 40, 40) : Windows.UI.Color.FromArgb(255, 184, 184, 184);
            for (int index = 0; index < _buttons.Count; index++)
            {
                bool active = index == _selected;
                _buttons[index].Background = ViewerChrome.Brush(active ? onFill : offFill);
                _buttons[index].BorderBrush = ViewerChrome.Brush(active ? onFill : offFill);
                _labels[index].Foreground = ViewerChrome.Brush(active ? onText : offText);
            }
        }
    }
}
