using System.Runtime.InteropServices;
using Gallery.Models;
using Gallery.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Media.Casting;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 独立查看窗口。布局照搬 Windows 照片 Lightbox：48 DIP 合并标题栏 + 悬浮胶片条 +
/// 48 DIP 底部工具条 + 右侧信息面板 + 悬浮视频传输条。
/// 只读文件与自身状态，不初始化图库数据库、标签详情、监视、桥接或识别。
/// </summary>
public sealed partial class ViewerWindow : Window
{
    private const double EdgeArrowSize = 48;
    private const double EdgeArrowMargin = 12;

    private readonly ViewerHost _host;
    private readonly Grid _root = new();
    private readonly Grid _titleBar = new();
    private readonly TextBlock _titleText;
    private readonly StackPanel _titleCommands = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private Gallery.Controls.CustomCommandBar? _commandBar;
    private readonly Grid _contentRow = new();
    private readonly Grid _surface = new();
    private readonly ImageViewer _image = new();
    private readonly VideoViewer _video = new();
    private readonly VideoPlaybackBar _playback;
    private readonly ViewerFilmstrip _filmstrip = new();
    private readonly ViewerInfoPane _infoPane = new();
    private readonly Grid _bottomBar = new();
    private readonly StackPanel _leftGroup = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _centerGroup = new() { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _rightGroup = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _fileInfo = new() { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _fileType = ViewerChrome.Label("", 12, ViewerChrome.Palette.Dark.SecondaryText);
    private readonly TextBlock _fileDimensions = ViewerChrome.Label("", 12, ViewerChrome.Palette.Dark.SecondaryText);
    private readonly TextBlock _fileSize = ViewerChrome.Label("", 12, ViewerChrome.Palette.Dark.SecondaryText);
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Border _statusHost = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 12, 0, 0),
        Padding = new Thickness(12, 6, 12, 6),
        CornerRadius = new CornerRadius(4),
        Visibility = Visibility.Collapsed,
    };
    private readonly Button _filmstripButton;
    private readonly Button _infoButton;
    private readonly Button _fullscreenButton;
    private Button _previousButton = null!;
    private Button _nextButton = null!;
    private readonly Button _zoomButton;
    private readonly TextBlock _zoomLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _zoomSlider;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private readonly List<string> _files = [];
    private ViewerChrome.Palette _palette = ViewerChrome.Palette.Dark;
    private PhotoItem? _item;
    private int _width;
    private int _height;
    private CastingDevicePicker? _castingPicker;
    private CastingConnection? _casting;
    private bool _started, _busy, _importing, _suspended, _closed, _allowClose, _fullscreen;
    private bool _updatingZoom;

    public string CurrentPath { get; private set; } = "";
    public bool IsBusy => _busy || _importing || _enteringEdit || _photoEditor?.Saving == true || _collage?.IsSaving == true;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string first, string second);

    public ViewerWindow(ViewerHost host, string path)
    {
        _host = host;
        ViewerPreferences.EnsureLoaded();
        Title = "Gallery";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "assets", "Gallery.ico"));
        RestoreBounds();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_titleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        _titleText = ViewerChrome.Label("", 12, _palette.Text);
        _titleText.HorizontalAlignment = HorizontalAlignment.Center;
        _titleText.VerticalAlignment = VerticalAlignment.Center;

        BuildTitleBar();
        (_previousButton, _nextButton) = BuildSurface();
        _playback = new VideoPlaybackBar(_video, Cast);
        Grid.SetRow(_playback, 0);
        _surface.Children.Add(_playback);

        _infoButton = ViewerChrome.IconButton(ViewerChrome.Glyph.Info, "信息", _palette, 32, 15);
        _infoButton.Click += (_, _) => ToggleInfoPane();
        _filmstripButton = ViewerChrome.IconButton(ViewerChrome.Glyph.Filmstrip, "显示影片", _palette, 32, 15);
        _filmstripButton.Content = ViewerChrome.Icon(ViewerChrome.Glyph.Filmstrip, 15, _palette.Text);
        _filmstripButton.Click += (_, _) => ToggleFilmstrip();
        _fullscreenButton = ViewerChrome.IconButton(ViewerChrome.Glyph.Fullscreen, "全屏", _palette, 32, 15);
        _fullscreenButton.Click += (_, _) => ToggleFullscreen();
        _zoomButton = BuildZoomCombo();
        _zoomSlider = BuildZoomSlider();

        BuildBottomBar();
        BuildContent();
        BuildRoot();
        _image.AllowFitUpscale = false;
        BuildEditChrome();
        _root.RequestedTheme = ThemeService.Parse(AppService.Settings.Settings.theme);
        if (AppService.Settings.Settings.viewer_info_pane) SetInfoPane(true);
        ApplyTheme(_root.ActualTheme);
        _root.ActualThemeChanged += (_, _) => ApplyTheme(_root.ActualTheme);

        _image.ZoomChanged += _ => SyncZoomWidget();
        _image.LoadFailed += error => ShowStatus("无法打开图片：" + error);
        _video.Failed += error => DispatcherQueue.TryEnqueue(() => { if (!_closed) ShowStatus("无法播放视频：" + error); });
        _video.PlaybackStateChanged += () => { if (!_closed && _video.IsPlaying) _host.Playing(this); };
        _video.DurationChanged += () =>
        {
            if (_closed) return;
            UpdateFileInfo();
            if (_infoPane.Visibility == Visibility.Visible) UpdateInfoPane();
        };
        _filmstrip.ItemSelected += path2 => _ = RunAsync(() => NavigateToAsync(path2));
        _infoPane.CloseRequested += () => SetInfoPane(false);
        _infoPane.CopyPathRequested += () => _ = RunAsync(() => CopyAsync(false, true));
        _infoPane.OpenLocationRequested += () => _ = RunAsync(OpenFolderAsync);
        _infoPane.SetBackgroundRequested += () => _ = RunAsync(() => WallpaperAsync(WallpaperTarget.Desktop));
        _infoPane.ResizeRequested += () => _ = RunAsync(ResizeImageAsync);

        _root.Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await RunAsync(() => OpenPathAsync(path));
#if GALLERY_DEVELOPMENT
            // 开发自测：--collage 启动后直接进拼图模式，用于沙箱冒烟，不出现在发布构建。
            if (Environment.GetCommandLineArgs().Contains("--collage", StringComparer.OrdinalIgnoreCase))
                await EnterCollageAsync();
            if (Environment.GetCommandLineArgs().Contains("--edit", StringComparer.OrdinalIgnoreCase))
                await EnterEditAsync();
#endif
        };
        _root.SizeChanged += (_, _) => UpdateTitleBarRegions();
        AppWindow.Closing += async (_, e) =>
        {
            if (!_allowClose && (IsBusy || _suspended)) { e.Cancel = true; ShowStatus("正在处理文件，请完成后再关闭窗口。"); return; }
            if (_collaging) { e.Cancel = true; await RequestExitCollageAsync(closeWindow: true); return; }
            if (_editing) { e.Cancel = true; await RequestExitEditAsync(closeWindow: true); }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            SaveBounds();
            _photoEditor?.Dispose();
            _image.Clear();
            _playback.Detach();
            _video.DisposePlayer();
            _casting?.Dispose();
            _casting = null;
        };
        Content = _root;
        TextSelectionBehavior.Attach(_root);
    }

    // ---------- 结构 ----------

    private void BuildRoot()
    {
        _root.Background = ViewerChrome.Brush(_palette.Background);
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ViewerChrome.TitleBarHeight) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(_titleBar);
        Grid.SetRow(_contentRow, 2);
        _root.Children.Add(_contentRow);
        _bottomBar.Height = ViewerChrome.BarHeight;
        Grid.SetRow(_bottomBar, 3);
        _root.Children.Add(_bottomBar);
    }

    /// <summary>
    /// 标题栏：照照片的结构——左边「编辑」胶囊，右边一条命令栏（主命令只有删除，其余走溢出菜单）。
    /// 命令栏用项目里的 CustomCommandBar（Photos 命令栏的托管移植），样式、溢出菜单位置、
    /// 菜单项的图标与快捷键都由现成模板给出，不再自绘。
    /// </summary>
    private void BuildTitleBar()
    {
        _titleBar.Children.Add(_titleCommands);
        _titleCommands.HorizontalAlignment = HorizontalAlignment.Left;
        _titleCommands.VerticalAlignment = VerticalAlignment.Center;
        _titleCommands.Margin = new Thickness(6, 0, 0, 0);
        _titleBar.Children.Add(_titleText);

        _commandBar = new Gallery.Controls.CustomCommandBar
        {
            Background = ViewerChrome.Transparent(),
            VerticalAlignment = VerticalAlignment.Center,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed,
            IsDynamicOverflowEnabled = false,
            OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed,
            IsOpen = false,
            Height = ViewerChrome.TitleBarHeight,
        };
        _commandBar.PrimaryCommands.Add(BarCommand("删除", ViewerChrome.Glyph.Delete, DeleteAsync, "Del"));
        var more = new AppBarButton
        {
            Label = "更多",
            Width = ViewerChrome.TitleButtonSize,
            Icon = new FontIcon { Glyph = ViewerChrome.Glyph.More, FontFamily = ViewerChrome.IconFamily },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "更多");
        more.Flyout = BuildViewerMenu();
        _commandBar.PrimaryCommands.Add(more);
        _titleCommands.Children.Add(_commandBar);
    }

    /// <summary>命令栏条目：图标 + 文字 +（可选）快捷键提示，全部复用系统 AppBarButton 模板。</summary>
    private AppBarButton BarCommand(string label, string glyph, Func<Task> action, string? shortcut = null)
    {
        var command = new AppBarButton
        {
            Label = label,
            Icon = new FontIcon { Glyph = glyph, FontFamily = ViewerChrome.IconFamily },
            KeyboardAcceleratorTextOverride = shortcut,
            Width = ViewerChrome.TitleButtonSize,
            IsEnabled = _item is not null && !_busy && !_suspended,
        };
        command.Click += async (_, _) => await RunAsync(action);
        return command;
    }

    /// <summary>
    /// 溢出菜单：菜单项用 MenuFlyoutItem（图标 + 文字 + 快捷键提示），二级「设置为」用 MenuFlyoutSubItem，
    /// 全部走系统模板，位置锚在按钮下方。
    /// </summary>
    private MenuFlyout BuildViewerMenu()
    {
        var menu = new MenuFlyout();
        void Item(string text, string glyph, Func<Task> action, string? shortcut = null)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph, FontFamily = ViewerChrome.IconFamily },
                KeyboardAcceleratorTextOverride = shortcut,
            };
            item.Click += async (_, _) => await RunAsync(action);
            menu.Items.Add(item);
        }
        // 分组与顺序照照片的“更多”菜单：复制 / 调整图像大小、设置为 / 文件操作。
        Item("复制", ViewerChrome.Glyph.Copy, () => CopyAsync(false, false), "Ctrl+C");
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("调整图像大小", ViewerChrome.Glyph.Resize, ResizeImageAsync);
        Item("创建拼图", ViewerChrome.Glyph.Crop, EnterCollageAsync);
        var setAs = new MenuFlyoutSubItem
        {
            Text = "设置为",
            Icon = new FontIcon { Glyph = ViewerChrome.Glyph.SetAs, FontFamily = ViewerChrome.IconFamily },
        };
        foreach (var target in new[] { WallpaperTarget.LockScreen, WallpaperTarget.Desktop })
        {
            var item = new MenuFlyoutItem
            {
                Text = target == WallpaperTarget.LockScreen ? "锁屏界面" : "背景",
                Icon = new FontIcon
                {
                    Glyph = target == WallpaperTarget.LockScreen ? ViewerChrome.Glyph.Lock : ViewerChrome.Glyph.Wallpaper,
                    FontFamily = ViewerChrome.IconFamily,
                },
            };
            item.Click += async (_, _) => await RunAsync(() => WallpaperAsync(target));
            setAs.Items.Add(item);
        }
        menu.Items.Add(setAs);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("导入归档", ViewerChrome.Glyph.Import, ImportAsync);
        Item("打开所在文件夹", ViewerChrome.Glyph.OpenFolder, OpenFolderAsync);
        Item("文件信息", ViewerChrome.Glyph.FileInfo, ShowInfoAsync);
        return menu;
    }

    private void BuildContent()
    {
        _contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_surface, 0);
        _contentRow.Children.Add(_surface);
        _infoPane.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_infoPane, 1);
        _contentRow.Children.Add(_infoPane);
    }

    private (Button Previous, Button Next) BuildSurface()
    {
        _surface.Background = ViewerChrome.Brush(_palette.Background);
        _surface.Children.Add(_image);
        _surface.Children.Add(_video);

        var previous = EdgeArrow(ViewerChrome.Glyph.Previous, HorizontalAlignment.Left, -1);
        var next = EdgeArrow(ViewerChrome.Glyph.Next, HorizontalAlignment.Right, 1);
        _surface.Children.Add(previous);
        _surface.Children.Add(next);
        _surface.Children.Add(_filmstrip);
        _statusHost.Child = _status;
        _surface.Children.Add(_statusHost);
        _filmstrip.Visibility = Visibility.Collapsed;

        _surface.PointerEntered += (_, _) => SetArrows(true);
        _surface.PointerExited += (_, _) => SetArrows(false);
        _surface.RightTapped += (_, e) =>
        {
            if (_busy || _suspended) return;
            var flyout = BuildContextMenu();
            flyout.ShowAt(_surface, new FlyoutShowOptions { Position = e.GetPosition(_surface) });
            e.Handled = true;
        };
        _surface.DoubleTapped += (_, _) => { if (_item?.IsVideo != true) ToggleFit(); };
        _surface.PointerWheelChanged += OnWheel;
        _root.PreviewKeyDown += OnKey;
        return (previous, next);
    }

    /// <summary>
    /// 左右翻页：照照片实测，可点热区是 94 宽整条，但视觉只是中间一颗 32×32 小按钮。
    /// 这里外层保持整条热区，内层画小按钮。
    /// </summary>
    /// <summary>
    /// 左右翻页：照 Lightbox/NavigationControl.xbf 的原实现——
    /// 48×48 圆角底板（静止为亚克力、悬停 ControlStrongFill）、箭头 U+E76B/E76C、字号 20、
    /// 距内容边缘 12、按下整体缩到 0.88（200ms）。不是「整条热区 + 小图标」。
    /// </summary>
    private Button EdgeArrow(string glyph, HorizontalAlignment side, int delta)
    {
        var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        var button = new Button
        {
            Width = EdgeArrowSize,
            Height = EdgeArrowSize,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(EdgeArrowMargin, 0, EdgeArrowMargin, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = ViewerChrome.Brush(_palette.Acrylic),
            Content = ViewerChrome.Icon(glyph, 20, _palette.Text),
            Opacity = 0,
            IsTabStop = false,
            RenderTransformOrigin = new Windows.Foundation.Point(.5, .5),
            RenderTransform = scale,
        };
        AutomationProperties.SetName(button, delta < 0 ? "上一项" : "下一项");
        ToolTipService.SetToolTip(button, delta < 0 ? "上一项" : "下一项");
        button.Click += (_, _) => _ = RunAsync(() => NavigateAsync(delta));
        button.PointerEntered += (_, _) => button.Background = ViewerChrome.Brush(_palette.StrongFill);
        button.PointerExited += (_, _) =>
        {
            button.Background = ViewerChrome.Brush(_palette.Acrylic);
            AnimateScale(scale, 1, 200);
        };
        button.PointerPressed += (_, _) => AnimateScale(scale, .88, 200);
        button.PointerReleased += (_, _) => AnimateScale(scale, 1, 200);
        return button;
    }

    private static void AnimateScale(ScaleTransform scale, double to, int milliseconds)
    {
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, scale);
        Storyboard.SetTargetProperty(animation, "ScaleX");
        var animationY = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animationY, scale);
        Storyboard.SetTargetProperty(animationY, "ScaleY");
        storyboard.Children.Add(animation);
        storyboard.Children.Add(animationY);
        storyboard.Begin();
    }

    private void BuildBottomBar()
    {
        _bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _leftGroup.Children.Add(_filmstripButton);
        BuildFileInfo();
        _leftGroup.Children.Add(_fileInfo);
        _leftGroup.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_leftGroup, 0);
        _bottomBar.Children.Add(_leftGroup);

        _centerGroup.Children.Add(_infoButton);
        Grid.SetColumn(_centerGroup, 0);
        Grid.SetColumnSpan(_centerGroup, 3);
        _centerGroup.HorizontalAlignment = HorizontalAlignment.Center;
        _bottomBar.Children.Add(_centerGroup);

        _rightGroup.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(_rightGroup, 2);
        _bottomBar.Children.Add(_rightGroup);
    }

    private readonly Border _dividerOne = new() { Width = 1, Height = 16, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _dividerTwo = new() { Width = 1, Height = 16, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    private void BuildFileInfo()
    {
        _fileInfo.Children.Add(_fileType);
        _fileInfo.Children.Add(_dividerOne);
        _fileInfo.Children.Add(InfoGroup(ViewerChrome.Glyph.Dimensions, _fileDimensions));
        _fileInfo.Children.Add(_dividerTwo);
        _fileInfo.Children.Add(InfoGroup(ViewerChrome.Glyph.FileSize, _fileSize));
    }

    private StackPanel InfoGroup(string glyph, TextBlock text)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(ViewerChrome.Icon(glyph, 14, _palette.SecondaryText));
        stack.Children.Add(text);
        return stack;
    }

    /// <summary>
    /// 底部缩放：照片是圆角药丸 + 向下弹？不——底部栏贴屏幕下沿，菜单一律向上弹出。
    /// 这里用按钮 + MenuFlyout(Placement=Top)，避免 ComboBox 在贴边时方向不确定。
    /// </summary>
    private Button BuildZoomCombo()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(_zoomLabel);
        content.Children.Add(ViewerChrome.Icon(ViewerChrome.Glyph.ChevronDown, 10, _palette.SecondaryText));
        var button = new Button
        {
            Content = content,
            Height = 32,
            Width = 88,
            Padding = new Thickness(12, 0, 10, 0),
            // 照片里这是矩形（小圆角），不是胶囊
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Background = ViewerChrome.Brush(_palette.Bar),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "缩放选择");
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        void Preset(string text)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                if (text == "适应窗口") _image.ZoomFit();
                else if (int.TryParse(text.TrimEnd('%'), out int percent)) ZoomToDisplay(percent);
            };
            menu.Items.Add(item);
        }
        Preset("适应窗口");
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var preset in new[] { "25%", "50%", "75%", "100%", "150%", "200%", "400%" }) Preset(preset);
        button.Flyout = menu;
        ToolTipService.SetToolTip(button, "缩放选择");
        return button;
    }

    private Slider BuildZoomSlider()
    {
        var slider = new Slider
        {
            Width = 130,
            Minimum = 10,
            Maximum = 400,
            StepFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        };
        slider.ValueChanged += (_, _) =>
        {
            if (_updatingZoom) return;
            ZoomToDisplay(slider.Value);
        };
        ToolTipService.SetToolTip(slider, "缩放");
        return slider;
    }

    private void BuildImageTools()
    {
        var fit = ViewerChrome.IconButton(ViewerChrome.Glyph.Fit, "缩放以适应窗口", _palette, 32, 15);
        fit.Click += (_, _) => _image.ZoomFit();
        var zoomOut = ViewerChrome.IconButton(ViewerChrome.Glyph.ZoomOut, "缩小", _palette, 32, 15);
        zoomOut.Click += (_, _) => _image.ZoomBy(1 / 1.2);
        var zoomIn = ViewerChrome.IconButton(ViewerChrome.Glyph.ZoomIn, "放大", _palette, 32, 15);
        zoomIn.Click += (_, _) => _image.ZoomBy(1.2);
        _rightGroup.Children.Add(fit);
        _rightGroup.Children.Add(_zoomButton);
        _rightGroup.Children.Add(zoomOut);
        _rightGroup.Children.Add(_zoomSlider);
        _rightGroup.Children.Add(zoomIn);
        _rightGroup.Children.Add(_fullscreenButton);
    }

    // ---------- 主题 ----------

    private void ApplyTheme(ElementTheme theme)
    {
        _palette = ViewerChrome.Palette.For(theme);
        _root.Background = ViewerChrome.Brush(_palette.Background);
        _surface.Background = ViewerChrome.Brush(_palette.Background);
        _bottomBar.Background = ViewerChrome.Brush(_palette.Bar);
        _titleText.Foreground = ViewerChrome.Brush(_palette.Text);
        _fileType.Foreground = ViewerChrome.Brush(_palette.SecondaryText);
        _fileDimensions.Foreground = ViewerChrome.Brush(_palette.SecondaryText);
        _fileSize.Foreground = ViewerChrome.Brush(_palette.SecondaryText);
        _dividerOne.Background = ViewerChrome.Brush(_palette.Divider);
        _dividerTwo.Background = ViewerChrome.Brush(_palette.Divider);
        _status.Foreground = ViewerChrome.Brush(_palette.Text);
        _statusHost.Background = ViewerChrome.Brush(_palette.Panel);
        _filmstrip.ApplyTheme(theme);
        _collage?.ApplyTheme(theme);
        _infoPane.ApplyTheme(theme);
        _playback.ApplyTheme(theme);
        RecolorButtons(_root);
        AppWindow.TitleBar.ButtonForegroundColor = _palette.Text;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = _palette.SecondaryText;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = _palette.Hover;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = _palette.Pressed;
    }

    private void RecolorButtons(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is Button { Content: FontIcon icon }) icon.Foreground = ViewerChrome.Brush(_palette.Text);
            else if (child is FontIcon other) other.Foreground = ViewerChrome.Brush(_palette.Text);
            RecolorButtons(child);
        }
    }

    // ---------- 打开与翻页 ----------

    private async Task OpenPathAsync(string path)
    {
        if (_editing && _photoEditor?.Saving != true && !await RequestExitEditAsync()) return;
        if (_collaging && !await RequestExitCollageAsync()) return;
        await DisplayAsync(path);
        try { await ReadFolderAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _files.Clear();
            _files.Add(path);
            ShowStatus("文件已打开，但无法读取同文件夹中的其他文件。");
        }
        _filmstrip.SetItems(_files, CurrentPath);
    }

    /// <summary>单个查看窗口复用：再次从资源管理器打开文件时切换内容而不是新建窗口。</summary>
    public void ShowFile(string path)
    {
        if (_closed) return;
        if (IsBusy) { ShowStatus("正在处理当前图片，请完成后再打开其他文件。"); return; }
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ToggleFullscreen();
        Activate();
        _ = RunAsync(() => OpenPathAsync(path));
    }

    private async Task ReadFolderAsync(string path)
    {
        var files = await Task.Run(() => Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
            .Where(file => ScannerService.MediaExts.Contains(Path.GetExtension(file)))
            .OrderBy(file => Path.GetFileName(file), Comparer<string>.Create(StrCmpLogicalW)).ToArray());
        _files.Clear();
        _files.AddRange(files);
    }

    private Task DisplayAsync(string path)
    {
        if (_editing) ExitEdit(false);
        if (_collaging) ExitCollage();
        if (_host.IsPathReleased(path)) { ShowStatus("图片正在处理，请完成后再查看。"); return Task.CompletedTask; }
        CurrentPath = path;
        _image.Clear();
        _video.Clear();
        _casting?.Dispose();
        _casting = null;
        _titleText.Text = Path.GetFileName(path);
        Title = Path.GetFileName(path);
        ClearStatus();
        if (!File.Exists(path))
        {
            _item = null;
            ShowStatus("文件已不在原位，请重新选择文件。");
            RefreshChrome();
            return Task.CompletedTask;
        }
        if (!ScannerService.MediaExts.Contains(Path.GetExtension(path)))
        {
            _item = null;
            ShowStatus("Gallery 暂不支持这个文件格式。");
            RefreshChrome();
            return Task.CompletedTask;
        }
        var file = new FileInfo(path);
        _width = 0;
        _height = 0;
        _item = new PhotoItem
        {
            FullPath = path,
            File = file.Name,
            PixelWidth = 0,
            PixelHeight = 0,
            Date = file.LastWriteTime,
            FileSize = file.Length,
            ModifiedAtUnix = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0,
            IsVideo = ScannerService.VideoExts.Contains(file.Extension),
        };
        if (_item.IsVideo)
        {
            _host.Playing(this);
            _video.SetSource(_item);
        }
        else _image.SetSource(_item);
        RefreshChrome();
        return Task.CompletedTask;    }

    private void RefreshChrome()
    {
        bool video = _item?.IsVideo == true;
        bool missing = _item is null;
        _image.Visibility = missing || video ? Visibility.Collapsed : Visibility.Visible;
        _video.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        _playback.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        if (missing) _filmstrip.Visibility = Visibility.Collapsed;
        if (_editEntry is not null) _editEntry.Visibility = missing || video ? Visibility.Collapsed : Visibility.Visible;
        RebuildRightGroup(video);
        UpdateFileInfo();
        if (_infoPane.Visibility == Visibility.Visible) UpdateInfoPane();
    }

    private void RebuildRightGroup(bool video)
    {
        _rightGroup.Children.Clear();
        if (video)
        {
            _rightGroup.Children.Add(_fullscreenButton);
            return;
        }
        BuildImageTools();
    }

    private void UpdateFileInfo()
    {
        if (_item is null)
        {
            _fileType.Text = "";
            _fileDimensions.Text = "";
            _fileSize.Text = "";
            _fileInfo.Visibility = Visibility.Collapsed;
            return;
        }
        _fileInfo.Visibility = Visibility.Visible;
        var extension = Path.GetExtension(CurrentPath).TrimStart('.').ToUpperInvariant();
        _fileType.Text = extension;
        if (_item.IsVideo)
        {
            uint videoWidth = _video.NaturalVideoWidth, videoHeight = _video.NaturalVideoHeight;
            if (videoWidth > 0 && videoHeight > 0)
            {
                _width = (int)videoWidth;
                _height = (int)videoHeight;
            }
        }
        _fileDimensions.Text = _width > 0 && _height > 0 ? $"{_width} × {_height}" : "";
        _fileSize.Text = ViewerChrome.FormatSize(_item.FileSize);
        if (!_item.IsVideo && (_width == 0 || _height == 0))
        {
            var path = CurrentPath;
            _ = Task.Run(() => Gallery.Services.ImageInfo.ReadMeta(path))
                .ContinueWith(task =>
                {
                    if (_closed) return;
                    var meta = task.Result;
                    if (meta.Width is not int width || meta.Height is not int height) return;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closed || !CurrentPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
                        _width = width;
                        _height = height;
                        _fileDimensions.Text = $"{width} × {height}";
                        if (_infoPane.Visibility == Visibility.Visible) UpdateInfoPane();
                    });
                }, TaskScheduler.Default);
        }
    }

    private async Task NavigateAsync(int delta)
    {
        if (CurrentPath.Length == 0) return;
        await ReadFolderAsync(CurrentPath);
        if (_files.Count == 0) { ShowStatus("这个文件夹中没有其他可查看的文件。"); return; }
        int index = _files.FindIndex(path => path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase));
        int next = index < 0 ? 0 : index + delta;
        if (next < 0 || next >= _files.Count)
        {
            ShowStatus(delta < 0 ? "已经是第一项。" : "已经是最后一项。");
            return;
        }
        await NavigateToAsync(_files[next]);
    }

    private async Task NavigateToAsync(string path)
    {
        await DisplayAsync(path);
        _filmstrip.SetItems(_files, CurrentPath);
    }

    private void SetArrows(bool visible)
    {
        if (_closed) return;
        int index = _files.FindIndex(path => path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase));
        // 编辑态是「针对这一张」的模式，翻页按钮整体不出现，也不响应。
        bool hasPrevious = !_editing && index > 0;
        bool hasNext = !_editing && index >= 0 && index < _files.Count - 1;
        _previousButton.Opacity = visible && hasPrevious ? 1 : 0;
        _nextButton.Opacity = visible && hasNext ? 1 : 0;
        _previousButton.IsHitTestVisible = hasPrevious;
        _nextButton.IsHitTestVisible = hasNext;
    }

    private void ToggleFit()
    {
        if (_item is null) return;
        if (Math.Abs(DisplayPercent - 100) < 0.5) _image.ZoomFit();
        else ZoomToDisplay(100);
    }

    /// <summary>
    /// ImageViewer 的 Percent 已经是「一个图像像素占用的物理像素数」，
    /// 与 Photos 的百分比语义一致，不需要再乘栅格化倍率。
    /// </summary>
    private double DisplayPercent => _image.Percent;

    private void ZoomToDisplay(double percent) => _image.ZoomTo(percent);

    private void SyncZoomWidget()
    {
        _updatingZoom = true;
        _zoomLabel.Text = Math.Round(DisplayPercent) + "%";
        _zoomSlider.Value = Math.Clamp(DisplayPercent, _zoomSlider.Minimum, _zoomSlider.Maximum);
        _updatingZoom = false;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_busy || _suspended || _item?.IsVideo == true) return;
        var point = e.GetCurrentPoint(_surface);
        int delta = point.Properties.MouseWheelDelta;
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl)
        {
            _image.ZoomBy(delta > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
            return;
        }
        if (_image.Percent > 100.5 && Math.Abs(_image.Percent - 100) > 0.5) return;
        _ = RunAsync(() => NavigateAsync(delta > 0 ? -1 : 1));
        e.Handled = true;
    }

    // ---------- 胶片条 / 信息面板 ----------

    private void ToggleFilmstrip()
    {
        bool show = _filmstrip.Visibility != Visibility.Visible;
        _filmstrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _image.Margin = new Thickness(0, 0, 0, show ? 92 : 0);
        _video.Margin = _image.Margin;
        ToolTipService.SetToolTip(_filmstripButton, show ? "隐藏影片" : "显示影片");
        if (show) _filmstrip.SetItems(_files, CurrentPath);
    }

    private void ToggleInfoPane() => SetInfoPane(_infoPane.Visibility != Visibility.Visible);

    /// <summary>信息面板用项目统一的滑出：从右侧 28px 滑入并淡入，关闭反向；不是直接闪现。</summary>
    private void SetInfoPane(bool open)
    {
        if (open)
        {
            UpdateInfoPane();
            _infoPane.Visibility = Visibility.Visible;
        }
        if (_infoPane.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform { X = open ? 28 : 0 };
            _infoPane.RenderTransform = slide;
        }
        var storyboard = new Storyboard();
        var move = new DoubleAnimation
        {
            To = open ? 0 : 28,
            Duration = TimeSpan.FromMilliseconds(open ? 200 : 150),
            EasingFunction = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(move, slide);
        Storyboard.SetTargetProperty(move, "X");
        var fade = new DoubleAnimation
        {
            To = open ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(open ? 200 : 150),
            EasingFunction = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, _infoPane);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(move);
        storyboard.Children.Add(fade);
        if (!open)
        {
            storyboard.Completed += (_, _) =>
            {
                if (_infoPane.Visibility == Visibility.Visible && _infoPane.Opacity < .05)
                    _infoPane.Visibility = Visibility.Collapsed;
            };
        }
        else _infoPane.Opacity = 0;
        storyboard.Begin();
    }

    private void UpdateInfoPane()
    {
        if (_item is null)
        {
            _infoPane.Visibility = Visibility.Collapsed;
            return;
        }
        _infoPane.Update(new ViewerInfoPane.Info(
            CurrentPath, _item.IsVideo, _width, _height,
            _item.FileSize, _item.Date, _item.IsVideo ? _video.Duration : TimeSpan.Zero));
    }

    // ---------- 菜单 ----------

    private CommandBarFlyout BuildContextMenu()
    {
        var flyout = new CommandBarFlyout { AlwaysExpanded = true };
        flyout.PrimaryCommands.Add(Menu("复制", ViewerChrome.Glyph.Copy, () => CopyAsync(true, false), _item is not null));
        flyout.PrimaryCommands.Add(Menu("删除", ViewerChrome.Glyph.Delete, DeleteAsync, _item is not null));
        flyout.PrimaryCommands.Add(Menu("重命名", ViewerChrome.Glyph.Rename, RenameAsync, _item is not null));
        flyout.SecondaryCommands.Add(Menu("复制图片内容", ViewerChrome.Glyph.Copy, () => CopyAsync(true, true), _item?.IsVideo != true));
        flyout.SecondaryCommands.Add(Menu("复制路径", ViewerChrome.Glyph.Copy, () => CopyAsync(false, true), _item is not null));
        flyout.SecondaryCommands.Add(Menu("打开所在文件夹", ViewerChrome.Glyph.OpenFolder, OpenFolderAsync, _item is not null));
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        flyout.SecondaryCommands.Add(Menu("文件信息", ViewerChrome.Glyph.FileInfo, ShowInfoAsync, _item is not null));
        flyout.SecondaryCommands.Add(SetAsButton());
        return flyout;
    }

    private MenuFlyout BuildMoreMenu()
    {
        var menu = new MenuFlyout();
        void Item(string text, Func<Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled && !_busy && !_suspended };
            item.Click += async (_, _) => await RunAsync(action);
            menu.Items.Add(item);
        }
        Item("导入归档", ImportAsync, _item?.IsVideo != true && _item is not null);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("打开所在文件夹", OpenFolderAsync, _item is not null);
        Item("调整图像大小…", ResizeImageAsync, _item?.IsVideo != true && _item is not null);
        Item("文件信息", ShowInfoAsync, _item is not null);
        menu.Items.Add(new MenuFlyoutSeparator());
        var setAs = new MenuFlyoutSubItem
        {
            Text = "设置为",
            IsEnabled = _item?.IsVideo != true && !_busy && !_suspended,
        };
        foreach (var target in new[] { WallpaperTarget.LockScreen, WallpaperTarget.Desktop })
        {
            var item = new MenuFlyoutItem { Text = target == WallpaperTarget.LockScreen ? "锁屏界面" : "背景" };
            item.Click += async (_, _) => await RunAsync(() => WallpaperAsync(target));
            setAs.Items.Add(item);
        }
        menu.Items.Add(setAs);
        return menu;
    }

    /// <summary>照片把“设为锁屏/背景”放在“设置为”二级菜单里；CommandBarFlyout 用带 Flyout 的按钮实现同等效果。</summary>
    private AppBarButton SetAsButton()
    {
        var menu = new MenuFlyout();
        foreach (var target in new[] { WallpaperTarget.LockScreen, WallpaperTarget.Desktop })
        {
            var item = new MenuFlyoutItem { Text = target == WallpaperTarget.LockScreen ? "锁屏界面" : "背景" };
            item.Click += async (_, _) => await RunAsync(() => WallpaperAsync(target));
            menu.Items.Add(item);
        }
        return new AppBarButton
        {
            Label = "设置为",
            Icon = new FontIcon { Glyph = ViewerChrome.Glyph.SetAs, FontFamily = ViewerChrome.IconFamily },
            Flyout = menu,
            IsEnabled = _item?.IsVideo != true && !_busy && !_suspended,
        };
    }

    private AppBarButton Menu(string text, string glyph, Func<Task> action, bool enabled)
    {
        var button = new AppBarButton
        {
            Label = text,
            Icon = new FontIcon { Glyph = glyph, FontFamily = ViewerChrome.IconFamily },
            IsEnabled = enabled && !_busy && !_suspended,
        };
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    // ---------- 文件操作 ----------

    private async Task RunAsync(Func<Task> action)
    {
        if (_closed || _busy || _importing || _suspended) return;
        _busy = true;
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or COMException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { if (!_closed) ShowStatus("操作未完成：" + ex.Message); }
        finally { _busy = false; }
    }

    private async Task ImportAsync()
    {
        ShowStatus("正在打开 Gallery…");
        _importing = true;
        _busy = false;
        try
        {
            var result = await ViewerCoordinator.ImportAsync(CurrentPath);
            ShowStatus(result.Message);
        }
        finally { _importing = false; }
    }

    private async Task CopyAsync(bool imageContents, bool silent)
    {
        var file = await StorageFile.GetFileFromPathAsync(CurrentPath);
        var package = new DataPackage();
        if (imageContents && _item?.IsVideo != true) package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
        else if (imageContents) package.SetStorageItems([file]);
        else package.SetText(CurrentPath);
        Clipboard.SetContent(package);
        ShowStatus(imageContents ? (_item?.IsVideo == true ? "已复制文件。" : "已复制图片。") : "已复制路径。");
    }

    private async Task OpenFolderAsync()
    {
        var file = await StorageFile.GetFileFromPathAsync(CurrentPath);
        var folder = await file.GetParentAsync();
        var options = new FolderLauncherOptions();
        options.ItemsToSelect.Add(file);
        if (!await Launcher.LaunchFolderAsync(folder, options)) ShowStatus("无法打开所在文件夹，请稍后重试。");
    }

    private async Task WallpaperAsync(WallpaperTarget target)
    {
        var result = await WallpaperService.SetAsync(CurrentPath, target);
        ShowStatus(result.Success ? "图片已设置。" : result.Error);
    }

    private async Task RenameAsync()
    {
        if (_item is null || !File.Exists(CurrentPath)) { ShowStatus("文件已不在原位。"); return; }
        var path = CurrentPath;
        var text = new TextBox { Text = Path.GetFileName(path), MinWidth = 300 };
        if (await DialogService.ShowAsync(new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "重命名",
            Content = text,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        }) != ContentDialogResult.Primary) return;
        var name = text.Text.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowStatus("请填写有效的文件名。");
            return;
        }
        var next = Path.Combine(Path.GetDirectoryName(path)!, name);
        if (path.Equals(next, StringComparison.Ordinal)) return;
        await _host.ChangeFileAsync(path, next, () => Task.Run(() => File.Move(path, next)));
    }

    private async Task DeleteAsync()
    {
        if (_item is null || !File.Exists(CurrentPath)) { ShowStatus("文件已不在原位。"); return; }
        var path = CurrentPath;
        var confirm = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "删除这个文件？",
            Content = "将“" + Path.GetFileName(path) + "”移到回收站。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        DialogService.UseDestructivePrimary(confirm);
        if (await DialogService.ShowAsync(confirm) != ContentDialogResult.Primary) return;
        await _host.ChangeFileAsync(path, null, async () =>
        {
            if (!await Task.Run(() => FileActions.DeleteToRecycle(path))) throw new IOException("文件仍在使用，未能删除。");
        });
    }

    private async Task ShowInfoAsync()
    {
        if (_item is null) return;
        var file = new FileInfo(CurrentPath);
        var info = $"文件名：{file.Name}\n位置：{file.DirectoryName}\n大小：{ViewerChrome.FormatSize(file.Length)}\n修改时间：{file.LastWriteTime:g}";
        if (!_item.IsVideo)
        {
            if (_width > 0) info += $"\n尺寸：{_width} × {_height}";
        }
        else info += "\n时长：" + _video.Duration.ToString(@"hh\:mm\:ss");
        await DialogService.ShowAsync(new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "文件信息",
            Content = new TextBlock { Text = info, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "关闭",
        });
    }

    // ---------- 全屏 / 投屏 / 关闭 ----------

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        _fullscreenButton.Content = ViewerChrome.Icon(
            _fullscreen ? ViewerChrome.Glyph.ExitFullscreen : ViewerChrome.Glyph.Fullscreen, 15, _palette.Text);
        ToolTipService.SetToolTip(_fullscreenButton, _fullscreen ? "退出全屏" : "全屏");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_fullscreenButton, _fullscreen ? "退出全屏" : "全屏");
        AppWindow.SetPresenter(_fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        // 全屏只切换呈现方式，标题栏与底部栏保留：仍有可见的退出入口，Esc/F11 同样可用。
    }

    private void Cast()
    {
        if (_item?.IsVideo != true) return;
        try
        {
            if (_castingPicker is null)
            {
                _castingPicker = new CastingDevicePicker();
                _castingPicker.Filter.SupportsVideo = true;
                WinRT.Interop.InitializeWithWindow.Initialize(_castingPicker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                _castingPicker.CastingDeviceSelected += async (_, args) =>
                {
                    if (_closed || _item?.IsVideo != true) return;
                    try
                    {
                        _casting?.Dispose();
                        _casting = args.SelectedCastingDevice.CreateCastingConnection();
                        var error = await _casting.RequestStartCastingAsync(_video.GetAsCastingSource());
                        if (error != CastingConnectionErrorStatus.Succeeded) ShowStatus("投屏未能开始，请检查设备连接。");
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException)
                    { ShowStatus("暂时无法投屏，请重试。"); }
                };
            }
            _castingPicker.Show(new Rect(20, 60, 100, 40));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { ShowStatus("暂时无法打开投屏设备，请稍后重试。"); }
    }

    public void PauseVideo()
    {
        if (!_closed) _video.Pause();
    }

    public async Task SuspendAsync()
    {
        _suspended = true;
        _video.Clear();
        await _image.ReleaseFileAsync();
        ShowStatus("正在处理文件，请稍候…");
    }


    public void Resume()
    {
        _suspended = false;
        if (_editing) { ClearStatus(); return; }
        if (!_closed && CurrentPath.Length > 0)
        {
            try { _ = DisplayAsync(CurrentPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { ShowStatus("文件已变化，请重新打开。"); }
        }
    }

    public void CloseFromHost()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// 归档移动成功后该文件已不在原位：不再显示它，切到相邻文件；
    /// 最后一张也移走时才关闭窗口。
    /// </summary>
    public async Task DropFileAsync(string path)
    {
        if (_closed) return;
        _suspended = false;
        int index = _files.FindIndex(file => file.Equals(path, StringComparison.OrdinalIgnoreCase));
        _files.RemoveAll(file => file.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (!CurrentPath.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            _filmstrip.SetItems(_files, CurrentPath);
            return;
        }
        if (_files.Count == 0)
        {
            CloseFromHost();
            return;
        }
        await DisplayAsync(_files[Math.Clamp(index, 0, _files.Count - 1)]);
        _filmstrip.SetItems(_files, CurrentPath);
    }

    public async Task FileChangedAsync(string? newPath)
    {
        bool wasSuspended = _suspended;
        _suspended = false;
        if (_closed) return;
        if (newPath is not null) { await OpenPathAsync(newPath); return; }
        int old = Math.Max(0, _files.FindIndex(path => path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase)));
        await ReadFolderAsync(CurrentPath);
        if (_files.Count > 0)
        {
            await DisplayAsync(_files[Math.Min(old, _files.Count - 1)]);
            _filmstrip.SetItems(_files, CurrentPath);
        }
        else
        {
            _item = null;
            CurrentPath = "";
            _titleText.Text = "Gallery";
            Title = "Gallery";
            _filmstrip.SetItems([], "");
            ShowStatus("这个文件夹中没有其他可查看的文件。");
            RefreshChrome();
        }
        if (wasSuspended) ShowStatus("");
    }

    // ---------- 状态提示 ----------

    private void ShowStatus(string text)
    {
        if (_closed) return;
        _status.Text = text;
        _statusHost.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Tick -= HideStatus;
        if (text.Length == 0) return;
        _statusTimer.Tick += HideStatus;
        _statusTimer.Start();
    }

    private void HideStatus(object? sender, object e)
    {
        _statusTimer.Stop();
        if (!_closed) _statusHost.Visibility = Visibility.Collapsed;
    }

    private void ClearStatus()
    {
        _statusTimer.Stop();
        _statusHost.Visibility = Visibility.Collapsed;
        _status.Text = "";
    }

    // ---------- 键盘 ----------

    private async void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (_busy || _suspended || DialogService.IsShowing) return;
        if (_collaging)
        {
            bool ctrlZ = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            if (e.Key == VirtualKey.Escape) { await RequestExitCollageAsync(); e.Handled = true; }
            else if (ctrlZ && e.Key == VirtualKey.Z) { _collage?.Undo(); e.Handled = true; }
            else if (ctrlZ && e.Key == VirtualKey.Y) { _collage?.Redo(); e.Handled = true; }
            else if (ctrlZ && e.Key == VirtualKey.S) { _ = SaveCollageAsync(false); e.Handled = true; }
            return;
        }
        if (_editing)
        {
            if (e.Key == VirtualKey.Escape) { await RequestExitEditAsync(); e.Handled = true; }
            return;
        }
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(_root.XamlRoot).Any(popup => popup.IsOpen && popup.Child is not ToolTip)) return;
        var focused = FocusManager.GetFocusedElement(_root.XamlRoot) as DependencyObject;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is TextBox or PasswordBox or RichEditBox or Slider or ComboBox or TextBlock { IsTextSelectionEnabled: true }) return;
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down)) return;

        Func<Task>? action = null;
        if (ctrl && e.Key == VirtualKey.C && _item is not null) action = () => CopyAsync(_item.IsVideo != true, false);
        else if (ctrl && _item?.IsVideo == false && e.Key is VirtualKey.L or VirtualKey.B)
            action = () => WallpaperAsync(e.Key == VirtualKey.L ? WallpaperTarget.LockScreen : WallpaperTarget.Desktop);
        else if (ctrl && (e.Key == (VirtualKey)0xBB || e.Key == VirtualKey.Add)) { _image.ZoomBy(1.2); e.Handled = true; return; }
        else if (ctrl && (e.Key == (VirtualKey)0xBD || e.Key == VirtualKey.Subtract)) { _image.ZoomBy(1 / 1.2); e.Handled = true; return; }
        else if (ctrl && e.Key == VirtualKey.Number0) { ZoomToDisplay(100); e.Handled = true; return; }
        else if (!ctrl)
        {
            action = e.Key switch
            {
                VirtualKey.Left => () => NavigateAsync(-1),
                VirtualKey.Right => () => NavigateAsync(1),
                VirtualKey.Delete => DeleteAsync,
                VirtualKey.F2 => RenameAsync,
                _ => null,
            };
            if (e.Key == VirtualKey.F11) { ToggleFullscreen(); e.Handled = true; return; }
            if (e.Key == VirtualKey.Escape)
            {
                if (_fullscreen) ToggleFullscreen();
                else Close();
                e.Handled = true;
                return;
            }
            if (e.Key == VirtualKey.Space && _item?.IsVideo == true) { _video.TogglePlayPause(); e.Handled = true; return; }
        }
        if (action is not null) { e.Handled = true; await RunAsync(action); }
    }

    // ---------- 窗口位置与标题栏拖动区 ----------

    private void RestoreBounds()
    {
        var settings = AppService.Settings.Settings;
        double scale = 1.0;
        try
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (handle != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(handle);
                if (dpi >= 96) scale = dpi / 96.0;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            AppService.DebugLog("viewer dpi probe failed: " + ex.Message);
        }
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        // 记下的位置与尺寸是物理像素；首次打开按 Photos 的 1440x755 DIP 折算。
        int width = settings.viewer_width > 320 ? (int)settings.viewer_width : (int)Math.Round(1440 * scale);
        int height = settings.viewer_height > 240 ? (int)settings.viewer_height : (int)Math.Round(755 * scale);
        bool hasSaved = settings.viewer_width > 320 && settings.viewer_height > 240;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);
        int x = settings.viewer_x, y = settings.viewer_y;
        bool valid = hasSaved && x > work.X - 40 && y > work.Y - 40 && x < work.X + work.Width - 120 && y < work.Y + work.Height - 120;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            valid ? x : work.X + (work.Width - width) / 2,
            valid ? y : work.Y + (work.Height - height) / 2,
            width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SaveBounds()
    {
        try
        {
            var settings = AppService.Settings;
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) return;
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            settings.Settings.viewer_width = size.Width;
            settings.Settings.viewer_height = size.Height;
            settings.Settings.viewer_x = position.X;
            settings.Settings.viewer_y = position.Y;
            settings.Settings.viewer_info_pane = _infoPane.Visibility == Visibility.Visible;
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppService.DebugLog("viewer bounds save failed: " + ex.Message);
        }
    }

    private void UpdateTitleBarRegions()
    {
        var scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
        var rects = new List<Windows.Graphics.RectInt32>();
        void Add(FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible) return;
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var bounds = element.TransformToVisual(null)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new Windows.Graphics.RectInt32(
                (int)Math.Floor(bounds.Left * scale), (int)Math.Floor(bounds.Top * scale),
                (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale)));
        }
        Add(_titleCommands);
        try
        {
            var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            if (rects.Count == 0) source.ClearRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough);
            else source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch (Exception ex)
        {
            AppService.DebugLog("viewer titlebar passthrough failed: " + ex.Message);
        }
    }
}
