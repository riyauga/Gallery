using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Gallery.Models;
using Gallery.Views;
using Gallery.Controls.Viewer;
using Gallery.Services;
using Windows.Foundation;
using Windows.Media.Casting;
using Windows.System;

namespace Gallery.Controls;

/// <summary>
/// 单图查看器：照搬 galleryold preview.py 的行为（容器与美术按 photos 规范）。
/// 宽 ≥900：左图 + 右详情 360px；窄窗上下分栏。
/// 缩放/平移/翻页在 ImageViewer；详情在 DetailPanel；
/// 打开走 150ms 渐入（现有 fade 退化模式保留，飞入动画后续接入缩略图起点）。
/// </summary>
public sealed partial class PreviewOverlay : Grid
{
    private IReadOnlyList<PhotoItem> _items = Array.Empty<PhotoItem>();
    private int _index;

    public event Action<string>? ToastRequested;
    public event Action<Gallery.Models.PhotoItem>? TagsEdited;
    public event Action? ReturnFocusRequested;

    /// <summary>宿主注入菜单构建器（GalleryPage.BuildContextMenu 不含「打开」变体）。</summary>
    public Func<PhotoItem, MenuFlyout>? ContextMenuBuilder { get; set; }
    public Func<IReadOnlyCollection<int>, Task>? DeleteRequested { get; set; }
    public Func<PhotoItem, WallpaperTarget, Task>? SetWallpaperRequested { get; set; }
    public Func<PhotoItem, Task>? RenameRequested { get; set; }
    public Func<PhotoItem, bool, Task<bool>>? FavoriteRequested { get; set; }

    public PreviewOverlay()
    {
        InitializeComponent();
        // ImageViewer 平移时会吃掉 PointerMoved（Handled=true），边缘唤醒必须用 handledEventsToo 订阅
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnOverlayPointerMoved), true);
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Tab || HasInputPopup()) return;
            _keyboardChrome = true;
            if (_items.Count > 0 && !_items[_index].IsVideo) ShowZoomBar();
            if (_fullscreen) { ShowTopBar(); ShowEdgeArrows(_pointerX); }
        };
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _keyboardChrome = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                TryHideZoomBar();
                if (_fullscreen && !_topBarHovered) HideTopBar();
            });
        }), true);
        ZoomBar.GotFocus += (_, _) => ShowZoomBar();
        ZoomBar.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(TryHideZoomBar);
        TopBar.GotFocus += (_, _) => { if (_fullscreen) ShowTopBar(); };
        TopBar.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_fullscreen && !_topBarHovered) HideTopBar();
        });
        // 详情面板的贴纸显示中文名（词库补出来的），点开/编辑仍然用原名
        Detail.DisplayName = name => _tagDisplay.TryGetValue(name, out var shown) ? shown : name;
        Detail.RatingTooltip = BuildRatingTooltip;
        Detail.TagCopied += text => ToastRequested?.Invoke("已复制标签：" + text);
        Detail.FavoriteRequested = async (item, favorite) =>
        {
            var result = FavoriteRequested is null
                ? item.Favorite
                : await FavoriteRequested(item, favorite);
            Detail.SetFavorite(result);
            return result;
        };
        Viewer.ZoomChanged += SyncZoomControls;
        Viewer.LoadingChanged += loading =>
        {
            LoadingText.Text = "正在加载图片…";
            LoadingText.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        };
        Viewer.LoadFailed += message =>
        {
            LoadingText.Text = "无法显示图片";
            LoadingText.Visibility = Visibility.Visible;
            ToastRequested?.Invoke("图片加载失败：" + message);
        };
        VideoPlayer.LoadingChanged += loading =>
        {
            if (loading)
            {
                LoadingText.Text = "正在打开视频…";
                LoadingText.Visibility = Visibility.Visible;
            }
            else LoadingText.Visibility = Visibility.Collapsed;
        };
        VideoPlayer.Failed += message =>
        {
            LoadingText.Text = "无法播放这个视频";
            LoadingText.Visibility = Visibility.Visible;
            ToastRequested?.Invoke("视频播放失败：" + message);
        };
        VideoPlayer.PlaybackStateChanged += UpdateVideoPlayPause;
        VideoPlayer.PositionChanged += UpdateVideoProgress;
        VideoPlayer.DurationChanged += UpdateVideoDuration;
        if (VideoVolumeButton.Flyout is Flyout volumeFlyout)
            volumeFlyout.Opened += (_, _) => SyncVideoVolume();
        Detail.EditTagsRequested += async item =>
        {
            if (XamlRoot is null) return;
            var saved = await Gallery.Controls.Tags.TagEditorDialog.ShowAsync(XamlRoot, [item],
                (message, _, _) => ToastRequested?.Invoke(message));
            if (!saved || AppService.Store is null) return;
            ToastRequested?.Invoke("标签已保存");
            Detail.SetTags(LoadTagPairs(item.ImageId));
            Detail.SetSource(item.PageUrl, item.SourceUrl);
            TagsEdited?.Invoke(item);
        };
        Detail.OpenExternalRequested += item =>
            FileActions.OpenImage(item.FullPath,
                AppService.Settings.Settings.open_mode,
                AppService.Settings.Settings.open_custom_exe);

        // 百分比下拉（Photos 缩放条）：预设档位，当前缩放以占位文本显示
        foreach (var percent in new[] { "25%", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%" })
            ZoomPercentBox.Items.Add(percent);

        // 下拉展开期间不收起缩放条（指针会移到弹出层里）
        ZoomPercentBox.DropDownOpened += (_, _) => ShowZoomBar();
        ZoomPercentBox.DropDownClosed += (_, _) => TryHideZoomBar();
    }

    private bool _keyboardChrome, _zoomBarHovered, _topBarHovered;
    private bool _syncingZoom;
    private Storyboard? _zoomBarAnimation;
    private bool _syncingSeek;
    private bool _seeking;
    private bool _syncingVolume;
    private CastingDevicePicker? _castingPicker;
    private CastingConnection? _castingConnection;
    private bool _fullscreen;
    private bool _infoInFullscreen;
    private double _pointerX;
    private Storyboard? _topBarAnimation;

    /// <summary>WinUI 3 的 UIElement.PointerCaptures 在无捕获时可能返回 null，别直接取 Count。</summary>
    private bool HasPointerCapture() => ZoomSlider.PointerCaptures is { Count: > 0 };

    private void ZoomBarHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _zoomBarHovered = true;
        ShowZoomBar();
    }

    private void ZoomBarHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _zoomBarHovered = false;
        TryHideZoomBar();
    }

    private bool HasFocusWithin(DependencyObject element)
    {
        if (XamlRoot is null) return false;
        for (var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
             current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, element)) return true;
        return false;
    }

    /// <summary>浮出底部缩放条：鼠标悬停或键盘 Tab 进入时显示，持有焦点时不隐藏。</summary>
    private void ShowZoomBar()
    {
        _zoomBarAnimation?.Stop();
        ZoomBar.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(120) };
        Storyboard.SetTarget(fade, ZoomBar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        _zoomBarAnimation = sb;
        sb.Begin();
    }

    private void TryHideZoomBar()
    {
        if (_zoomBarHovered || HasFocusWithin(ZoomBar) || ZoomPercentBox.IsDropDownOpen) return;
        if (HasPointerCapture()) return;
        HideZoomBar();
    }

    /// <summary>淡出终点留 0.01：本项目合成层在 Opacity 归零后会停绘制（见 MEMORY 的已知坑）。</summary>
    private void HideZoomBar()
    {
        if (ZoomBar.Visibility != Visibility.Visible) return;
        _zoomBarAnimation?.Stop();
        var fade = new DoubleAnimation { To = 0.01, Duration = TimeSpan.FromMilliseconds(120) };
        Storyboard.SetTarget(fade, ZoomBar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            if (_zoomBarHovered || HasFocusWithin(ZoomBar) || ZoomPercentBox.IsDropDownOpen || HasPointerCapture()) return;
            ZoomBar.Visibility = Visibility.Collapsed;
            ZoomBar.Opacity = 1;
        };
        _zoomBarAnimation = sb;
        sb.Begin();
    }

    /// <summary>把查看器当前缩放同步到滑块与百分比下拉（避免回环触发）。</summary>
    private void SyncZoomControls(double percent)
    {
        _syncingZoom = true;
        ZoomPercentBox.SelectedIndex = -1;
        ZoomPercentBox.PlaceholderText = $"{percent:0}%";
        ZoomSlider.Value = Math.Clamp(percent, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _syncingZoom = false;
    }

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingZoom) return;
        Viewer.ZoomTo(e.NewValue);
    }

    private void ZoomPercent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingZoom || ZoomPercentBox.SelectedItem is not string text) return;
        if (double.TryParse(text.TrimEnd('%'), out double percent)) Viewer.ZoomTo(percent);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Viewer.ZoomBy(1 / 1.25);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Viewer.ZoomBy(1.25);

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private bool IsFullscreen =>
        GetAppWindow()?.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;

    private void SetFullscreen(bool full)
    {
        if (MainWindow.Instance is not { } window || _closing || !IsOpen || _fullscreen == full) return;
        _animation?.Stop();
        _animation = null;
        Viewer.IsAnimating = false;
        Opacity = 1;
        FlightTransform.ScaleX = FlightTransform.ScaleY = 1;
        FlightTransform.TranslateX = FlightTransform.TranslateY = 0;
        // 不隐藏内容、不插入黑帧：窗口 presenter 与布局在一次同步操作内切换。
        window.SetViewerFullscreen(full, () =>
        {
            _fullscreen = full;
            _infoInFullscreen = false;
            Background = new SolidColorBrush(full
                ? Microsoft.UI.Colors.Black
                : Microsoft.UI.ColorHelper.FromArgb(0xCC, 0, 0, 0));
            ContentArea.Margin = full ? new Thickness(0) : new Thickness(16, 52, 16, 16);
            FullscreenIcon.Glyph = full ? "\uE73F" : "\uE740";
            CloseButton.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            TopBar.Visibility = Visibility.Collapsed;
            TopBar.Opacity = 0;
            ApplyFullscreenLayout();
            UpdateVideoBar();
        });
    }

    /// <summary>
    /// 全屏布局：收起右侧详情栏（顶栏 ⓘ 可临时唤回），图片铺满，翻页箭头改成边缘唤醒。
    /// 退出全屏时恢复窗口态（详情栏常显、箭头常显）。
    /// </summary>
    private void ApplyFullscreenLayout()
    {
        if (!_fullscreen)
        {
            TopBarHost.IsHitTestVisible = false;
            LeftEdgeHost.IsHitTestVisible = false;
            RightEdgeHost.IsHitTestVisible = false;
            TopBar.Visibility = Visibility.Collapsed;
            Detail.Visibility = Visibility.Visible;
            Relayout();
            PrevButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        PhotoArea.Margin = new Thickness(0);
        TopBarHost.IsHitTestVisible = true;
        LeftEdgeHost.IsHitTestVisible = true;
        RightEdgeHost.IsHitTestVisible = true;
        Detail.Visibility = _infoInFullscreen ? Visibility.Visible : Visibility.Collapsed;
        if (_infoInFullscreen)
        {
            Detail.HorizontalAlignment = HorizontalAlignment.Right;
            Detail.VerticalAlignment = VerticalAlignment.Stretch;
            Detail.Width = 360;
            Detail.Height = double.NaN;
            Detail.Margin = new Thickness(0, 0, 16, 16);
        }

        ShowEdgeArrows(_pointerX);
    }

    /// <summary>全屏时左右翻页箭头只在指针贴到对应边缘 96 DIP 内出现。</summary>
    private void ShowEdgeArrows(double x)
    {
        const double edge = 96;
        bool left = _index > 0 && (_keyboardChrome || HasFocusWithin(PrevButton) || x <= edge);
        bool right = _index < _items.Count - 1 && (_keyboardChrome || HasFocusWithin(NextButton) || x >= ActualWidth - edge);
        PrevButton.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOverlayPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        var position = e.GetCurrentPoint(this).Position;
        _pointerX = position.X;
        ShowEdgeArrows(position.X);
    }

    private void LeftEdgeHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        PrevButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LeftEdgeHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        if (!_keyboardChrome && !HasFocusWithin(PrevButton)) PrevButton.Visibility = Visibility.Collapsed;
    }

    private void RightEdgeHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        NextButton.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RightEdgeHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        if (!_keyboardChrome && !HasFocusWithin(NextButton)) NextButton.Visibility = Visibility.Collapsed;
    }

    private void TopBarHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        _topBarHovered = true;
        ShowTopBar();
    }

    private void TopBarHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        _topBarHovered = false;
        HideTopBar();
    }

    /// <summary>顶栏淡入（150ms），退场淡到 0.01 再收起（合成层归零会停绘，见 MEMORY）。</summary>
    private void ShowTopBar()
    {
        _topBarAnimation?.Stop();
        TopBar.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(fade, TopBar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        _topBarAnimation = storyboard;
        storyboard.Begin();
    }

    private void HideTopBar()
    {
        if (_keyboardChrome || HasFocusWithin(TopBar) || TopBar.Visibility != Visibility.Visible) return;
        _topBarAnimation?.Stop();
        var fade = new DoubleAnimation { To = 0.01, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(fade, TopBar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) =>
        {
            if (_keyboardChrome || _topBarHovered || HasFocusWithin(TopBar)) return;
            TopBar.Visibility = Visibility.Collapsed;
            TopBar.Opacity = 1;
        };
        _topBarAnimation = storyboard;
        storyboard.Begin();
    }

    private void TopInfo_Click(object sender, RoutedEventArgs e)
    {
        if (!_fullscreen) return;
        _infoInFullscreen = !_infoInFullscreen;
        ApplyFullscreenLayout();
    }

    private async void TopDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        var item = _items[_index];
        if (item.ImageId <= 0 || DeleteRequested is null) return;
        await DeleteRequested(new[] { item.ImageId });
    }

    private void TopMore_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        var menu = ContextMenuBuilder?.Invoke(_items[_index]);
        menu?.ShowAt(TopMoreButton);
    }

    private void TopMinimize_Click(object sender, RoutedEventArgs e)
    {
        var appWindow = GetAppWindow();
        if (appWindow is null) return;
        if (_fullscreen) SetFullscreen(false);
        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Minimize();
    }

    private void TopExitFullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(false);

    private static Microsoft.UI.Windowing.AppWindow? GetAppWindow()
    {
        var hwnd = AppService.MainWindowHandle;
        if (hwnd == IntPtr.Zero) return null;
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
    }

    /// <summary>退出全屏并恢复标题栏（打开/关闭查看器时都要回到普通窗口）。</summary>
    private void RestoreChrome()
    {
        if (IsFullscreen) MainWindow.Instance?.SetViewerFullscreen(false, () => _fullscreen = false);
        else MainWindow.Instance?.SetTitleBarHidden(false);
        Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xCC, 0, 0, 0));
        ContentArea.Margin = new Thickness(16, 52, 16, 16);
        FullscreenIcon.Glyph = "\uE740";
        _fullscreen = false;
        _infoInFullscreen = false;
        TopBar.Visibility = Visibility.Collapsed;
        TopBar.Opacity = 0;
        TopBarHost.IsHitTestVisible = false;
        CloseButton.Visibility = Visibility.Visible;
        Detail.Visibility = Visibility.Visible;
        UpdateVideoBar();
    }

    private GalleryPage.PreviewRequest? _request;
    private Windows.Foundation.Rect _lastSourceRect;
    private Storyboard? _animation;
    private bool _closing;
    private string? _shownPath;

    public void Open(GalleryPage.PreviewRequest request)
    {
        _animation?.Stop();
        _closing = false;
        _keyboardChrome = _zoomBarHovered = _topBarHovered = false;
        RefreshTagDisplay();
        RestoreChrome();
        Viewer.IsAnimating = true;
        FlightTransform.ScaleX = FlightTransform.ScaleY = 1;
        FlightTransform.TranslateX = FlightTransform.TranslateY = 0;
        _request = request;
        _lastSourceRect = request.SourceRect;
        _items = request.Items;
        _index = Math.Max(0, request.Items.ToList().IndexOf(request.Item));
        if (_items.Count == 0) return;
        _shownPath = null;

        Visibility = Visibility.Visible;
        Opacity = 0;
        Relayout();
        UpdateLayout();

        ShowCurrent(request.Thumbnail);
        Focus(FocusState.Programmatic);

        // 动画统一飞入（用户裁决：抛弃淡入淡出）；遮罩同步渐入 300ms
        double srcX = request.SourceRect.X - 16;
        double srcY = request.SourceRect.Y - 52;
        double hostW = PhotoArea.ActualWidth, hostH = PhotoArea.ActualHeight;
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(300),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        if (hostW > 0 && hostH > 0 && request.SourceRect.Width > 0 && !request.Item.IsVideo)
        {
            FlightTransform.ScaleX = request.SourceRect.Width / hostW;
            FlightTransform.ScaleY = request.SourceRect.Height / hostH;
            FlightTransform.TranslateX = srcX;
            FlightTransform.TranslateY = srcY;
            foreach (var prop in new[] { "ScaleX", "ScaleY", "TranslateX", "TranslateY" })
            {
                var kf = new SplineDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(300) };
                kf.KeySpline = new KeySpline { ControlPoint1 = new Point(0.215, 0.61), ControlPoint2 = new Point(0.355, 1) };
                kf.Value = prop.StartsWith("Scale") ? 1 : 0;
                var anim = new DoubleAnimationUsingKeyFrames();
                anim.KeyFrames.Add(kf);
                Storyboard.SetTarget(anim, FlightTransform);
                Storyboard.SetTargetProperty(anim, prop);
                sb.Children.Add(anim);
            }
        }
        sb.Completed += (_, _) => Viewer.IsAnimating = false;
        _animation = sb;
        sb.Begin();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 在测量子控件前确定图片区，避免 SizeChanged 后改 Margin 引发第二轮适应／回弹。
        Relayout(availableSize.Width, availableSize.Height);
        return base.MeasureOverride(availableSize);
    }

    private void Relayout() => Relayout(ActualWidth, ActualHeight);

    private void Relayout(double areaW, double areaH)
    {
        // 照 galleryold layout_panels：宽 ≥900 左图+右详情 360；窄窗上下分栏（图片区高 50%）
        if (areaW <= 0) return;
        if (_fullscreen)
        {
            PhotoArea.Margin = new Thickness(0);
            return;
        }
        if (areaW >= 900)
        {
            PhotoArea.Margin = new Thickness(0, 0, 380, 0); // 图片区宽 = area-380
            Detail.HorizontalAlignment = HorizontalAlignment.Right;
            Detail.VerticalAlignment = VerticalAlignment.Stretch;
            Detail.Margin = new Thickness(0);
            Detail.Width = 360;
            Detail.Height = double.NaN;
        }
        else
        {
            PhotoArea.Margin = new Thickness(0, 0, 0, areaH / 2); // 图片区上半 50%
            Detail.HorizontalAlignment = HorizontalAlignment.Stretch;
            Detail.VerticalAlignment = VerticalAlignment.Bottom;
            Detail.Margin = new Thickness(0);
            Detail.Width = double.NaN;
            Detail.Height = areaH / 2;
        }
    }

    /// <summary>分级标签的悬停说明：有分级依据就给四级+来源+概率，否则明确说不知道。</summary>
    private string? BuildRatingTooltip(string tagName)
    {
        var item = _items.Count == 0 ? null : _items[Math.Clamp(_index, 0, _items.Count - 1)];
        if (item is null || Services.AppService.Store is not { } store) return null;
        var score = store.NsfwScoreFor(item.ImageId);
        if (score is null)
            return $"{tagName} · 手动标记";

        var level = Services.RatingText.Level(score.Rating);
        var source = score.Source switch
        {
            "danbooru" => "来源网站",
            "model" => "自动识别",
            "manual" => "手动标记",
            _ => score.Source,
        };
        var parts = new List<string> { $"{tagName} · {level}", source };
        if (score.Source == "model")
        {
            parts.Add($"可信度 {score.Confidence:0.00}");
        }
        return string.Join(" · ", parts);
    }

    private void ShowCurrent(Microsoft.UI.Xaml.Media.ImageSource? thumbnail = null)
    {
        var item = _items[_index];
        bool video = item.IsVideo;
        // 收藏/标签刷新会重复调到这里；同一段视频不能因此从头播。
        bool restart = _shownPath != item.FullPath;
        _shownPath = item.FullPath;

        Detail.SetItem(item);
        TopFileName.Text = System.IO.Path.GetFileName(item.FullPath);
        Viewer.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        VideoPlayer.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        ZoomBarHost.Visibility = video ? Visibility.Collapsed : Visibility.Visible;

        if (video)
        {
            ZoomBar.Visibility = Visibility.Collapsed;
            if (restart)
            {
                _ = StopCastingAsync();
                Viewer.Clear();
                Viewer.IsAnimating = false;
                ResetVideoProgress(item);
                VideoPlayer.SetSource(item);
            }
        }
        else
        {
            VideoPlayer.Clear();
            Viewer.SetSource(item, thumbnail);
        }
        UpdateVideoBar();

        if (_fullscreen)
        {
            ShowEdgeArrows(_pointerX);
        }
        else
        {
            PrevButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>视频条只在当前项是视频时显示；下一个视频按当前预览列表向后找，找不到就置灰。</summary>
    private void UpdateVideoBar()
    {
        bool video = _items.Count > 0 && _items[_index].IsVideo;
        VideoBar.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        if (!video)
        {
            _seeking = false;
            return;
        }

        UpdateVideoPlayPause();
        UpdateVideoNext();
        SyncVideoVolume();
        UpdateVideoFullscreen();
    }

    private int FindNextVideoIndex()
    {
        for (int i = _index + 1; i < _items.Count; i++)
            if (_items[i].IsVideo) return i;
        return -1;
    }

    private void UpdateVideoNext()
    {
        bool hasNext = FindNextVideoIndex() >= 0;
        VideoNextButton.IsEnabled = hasNext;
        VideoNextButton.Opacity = hasNext ? 1 : 0.35;
    }

    private void UpdateVideoPlayPause()
    {
        if (_items.Count == 0 || !_items[_index].IsVideo) return;
        bool playing = VideoPlayer.IsPlaying;
        VideoPlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
        ToolTipService.SetToolTip(VideoPlayPauseButton, playing ? "暂停" : "播放");
    }

    private void UpdateVideoFullscreen()
    {
        VideoFullscreenIcon.Glyph = _fullscreen ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(VideoFullscreenButton, _fullscreen ? "退出全屏" : "全屏");
    }

    private void SyncVideoVolume()
    {
        _syncingVolume = true;
        VideoVolumeSlider.Value = Math.Clamp(VideoPlayer.Volume * 100, 0, 100);
        _syncingVolume = false;
        UpdateVideoVolumeIcon();
    }

    private void UpdateVideoVolumeIcon()
    {
        double volume = VideoPlayer.Volume;
        VideoVolumeIcon.Glyph = volume <= 0.001 ? "\uE74F" : "\uE767";
        ToolTipService.SetToolTip(VideoVolumeButton, volume <= 0.001 ? "已静音" : $"音量 {volume * 100:0}%");
    }

    private void ResetVideoProgress(PhotoItem item)
    {
        double seconds = Math.Max(0, item.DurationSeconds ?? 0);
        _seeking = false;
        _syncingSeek = true;
        VideoSeekSlider.Maximum = Math.Max(1, seconds);
        VideoSeekSlider.Value = 0;
        _syncingSeek = false;
        VideoElapsedText.Text = "0:00";
        VideoDurationText.Text = FormatTime(TimeSpan.FromSeconds(seconds));
    }

    private void UpdateVideoProgress()
    {
        if (_items.Count == 0 || !_items[_index].IsVideo || _seeking) return;
        var position = VideoPlayer.Position;
        _syncingSeek = true;
        VideoSeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, VideoSeekSlider.Maximum);
        _syncingSeek = false;
        VideoElapsedText.Text = FormatTime(position);
    }

    private void UpdateVideoDuration()
    {
        if (_items.Count == 0 || !_items[_index].IsVideo) return;
        double seconds = VideoPlayer.Duration.TotalSeconds;
        if (seconds <= 0) seconds = _items[_index].DurationSeconds ?? 0;
        _syncingSeek = true;
        VideoSeekSlider.Maximum = Math.Max(1, seconds);
        _syncingSeek = false;
        VideoDurationText.Text = FormatTime(TimeSpan.FromSeconds(seconds));
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private void VideoPlayPause_Click(object sender, RoutedEventArgs e) => VideoPlayer.TogglePlayPause();

    private void VideoNext_Click(object sender, RoutedEventArgs e)
    {
        int next = FindNextVideoIndex();
        if (next < 0) return;
        _index = next;
        ShowCurrent();
    }

    private void VideoFullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void VideoSeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSeek) return;
        VideoElapsedText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
        VideoPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void VideoSeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _seeking = true;

    private void VideoSeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e) => _seeking = false;

    private void VideoSeekSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _seeking = false;

    private void VideoVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingVolume) return;
        VideoPlayer.Volume = e.NewValue / 100.0;
        UpdateVideoVolumeIcon();
    }

    private void VideoFit_Click(object sender, RoutedEventArgs e) => VideoPlayer.Stretch = Stretch.Uniform;

    private void VideoFill_Click(object sender, RoutedEventArgs e) => VideoPlayer.Stretch = Stretch.UniformToFill;

    private void VideoSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string tag }) return;
        if (double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double rate))
            VideoPlayer.PlaybackRate = rate;
    }

    private void VideoBar_RightTapped(object sender, RightTappedRoutedEventArgs e) => e.Handled = true;

    private void VideoCast_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0 || !_items[_index].IsVideo) return;
        try
        {
            _castingPicker ??= CreateCastingPicker();
            var root = XamlRoot?.Content as UIElement;
            var point = root is null
                ? VideoSettingsButton.TransformToVisual(this).TransformPoint(new Point(0, 0))
                : VideoSettingsButton.TransformToVisual(root).TransformPoint(new Point(0, 0));
            _castingPicker.Show(new Rect(point.X, point.Y, VideoSettingsButton.ActualWidth, VideoSettingsButton.ActualHeight));
        }
        catch (Exception ex)
        {
            ToastRequested?.Invoke("无法打开投屏设备列表：" + ex.Message);
        }
    }

    private CastingDevicePicker CreateCastingPicker()
    {
        var picker = new CastingDevicePicker();
        picker.Filter.SupportsVideo = true;
        picker.CastingDeviceSelected += OnCastingDeviceSelected;
        var hwnd = AppService.MainWindowHandle;
        if (hwnd != IntPtr.Zero)
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return picker;
    }

    private void OnCastingDeviceSelected(CastingDevicePicker sender, CastingDeviceSelectedEventArgs args)
    {
        var device = args.SelectedCastingDevice;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await StopCastingAsync();
                var connection = device.CreateCastingConnection();
                connection.ErrorOccurred += (_, errorArgs) => DispatcherQueue.TryEnqueue(
                    () => ToastRequested?.Invoke("投屏失败：" + errorArgs.ErrorStatus));
                _castingConnection = connection;
                await connection.RequestStartCastingAsync(VideoPlayer.GetAsCastingSource());
                DispatcherQueue.TryEnqueue(() => ToastRequested?.Invoke("已开始投放到 " + device.FriendlyName));
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => ToastRequested?.Invoke("投屏失败：" + ex.Message));
            }
        });
    }

    private async Task StopCastingAsync()
    {
        var connection = _castingConnection;
        _castingConnection = null;
        if (connection is null) return;
        try { await connection.DisconnectAsync(); }
        catch (Exception) { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Prev_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => Viewer.ZoomFit();
    private void Zoom100_Click(object sender, RoutedEventArgs e) => Viewer.Zoom100();

    private void Move(int delta)
    {
        if (_closing || _items.Count == 0) return;
        int next = Math.Clamp(_index + delta, 0, _items.Count - 1);
        if (next == _index) return;
        _index = next;
        ShowCurrent();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>文件操作或查询更新后按图片 ID 保持当前位置；删末图时退至前一张。</summary>
    /// <summary>标签改完后重新读库刷新详情面板（页面刷新会用旧 PhotoItem.Tags 覆盖，所以要在之后调用）。</summary>
    public void RefreshCurrentTags()
    {
        if (!IsOpen || _items.Count == 0) return;
        RefreshTagDisplay();
        Detail.SetTags(LoadTagPairs(_items[_index].ImageId));
    }
    public void RefreshItems(IReadOnlyList<PhotoItem> items)
    {
        if (!IsOpen || _items.Count == 0) return;
        var current = _items[_index];
        int next = items.ToList().FindIndex(item => current.ImageId > 0
            ? item.ImageId == current.ImageId : item.FullPath == current.FullPath);
        _items = items.ToArray();
        if (_items.Count == 0) { Close(); return; }
        _index = next >= 0 ? next : Math.Min(_index, _items.Count - 1);
        ShowCurrent();
    }

    private bool HasInputPopup() => DialogService.IsShowing || XamlRoot is { } root
        && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => p.IsOpen && p.Child is not ToolTip);

    /// <summary>窗口加速器和查看器按键共用的隔离入口；控件与弹层保留自身按键。</summary>
    public bool TryHandleKey(VirtualKey key)
    {
        if (!IsOpen || _closing || HasInputPopup()) return false;
        bool control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (control && key is VirtualKey.B or VirtualKey.L)
        {
            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
                || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return false;
            var focus = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            bool insideViewer = false;
            for (var node = focus; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or SearchInputBox
                    or TextBlock { IsTextSelectionEnabled: true }) return false;
                if (ReferenceEquals(node, this)) { insideViewer = true; break; }
            }
            if (!insideViewer || _items.Count == 0 || _items[_index].IsVideo || SetWallpaperRequested is null) return false;
            _ = SetWallpaperRequested(_items[_index], key == VirtualKey.L ? WallpaperTarget.LockScreen : WallpaperTarget.Desktop);
            return true;
        }
        if (key is not (VirtualKey.Escape or VirtualKey.Left or VirtualKey.Right
            or VirtualKey.Space or VirtualKey.Delete or VirtualKey.F2)) return false;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return false;
        // Delete/F2/Esc 是查看器自身命令：按钮、滑块、只读文本都不消费它们，焦点落在这些控件上也要穿透到遮罩；
        // 空格/方向键仍让给控件（按钮激活、焦点导航、滑块调值），输入控件一律保留自身按键。
        bool viewerKey = key is VirtualKey.Delete or VirtualKey.F2 or VirtualKey.Escape;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        bool inside = false;
        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this)) { inside = true; break; }
            if (current is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or SearchInputBox) return false;
            if (current is TextBlock { IsTextSelectionEnabled: true } && !viewerKey) return false;
            if (current is (ButtonBase or RangeBase or Selector)
                && key is VirtualKey.Space or VirtualKey.Left or VirtualKey.Right) return false;
        }
        if (!inside && !viewerKey) return false;
        if (key == VirtualKey.Space && (_items.Count == 0 || !_items[_index].IsVideo)) return false;
        switch (key)
        {
            // Photos 语义：全屏时 Esc 先退出全屏，再按一次才关查看器
            case VirtualKey.Escape when _fullscreen: SetFullscreen(false); break;
            case VirtualKey.Escape: Close(); break;
            case VirtualKey.Left: Move(-1); break;
            case VirtualKey.Right: Move(1); break;
            case VirtualKey.Space when _items.Count > 0 && _items[_index].IsVideo:
                VideoPlayer.TogglePlayPause();
                break;
            case VirtualKey.Delete when _items.Count > 0:
                if (DeleteRequested is { } delete) _ = delete(new[] { _items[_index].ImageId });
                break;
            case VirtualKey.F2 when _items.Count > 0:
                if (RenameRequested is { } rename) _ = rename(_items[_index]);
                break;
        }
        return true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!e.Handled && TryHandleKey(e.Key)) e.Handled = true;
    }

    /// <summary>删除/重命名/移动前由宿主调用：暂停并释放视频文件句柄。</summary>
    public void ReleaseVideo()
    {
        if (!IsOpen || _items.Count == 0 || !_items[_index].IsVideo) return;
        _ = StopCastingAsync();
        VideoPlayer.Clear();
        _shownPath = null;
    }

    /// <summary>查看器内右键：菜单与外部一致，但无「打开」（photos 语义）。</summary>
    private void OnPhotoAreaRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ContextMenuBuilder is null || _items.Count == 0) return;
        var menu = ContextMenuBuilder(_items[_index]);
        menu.ShowAt(PhotoArea, e.GetPosition(PhotoArea));
        e.Handled = true;
    }

    /// <summary>仅精确命中遮罩空白（非按钮/图片/详情）时关闭。</summary>
    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, this)) Close();
    }

    /// <summary>
    /// 切页前收摊：不做飞回动画。飞回的终点是图库里那张格子，切页后它已经不在视野里，
    /// 动画只会和新页面抢 300ms 的显示时间。
    /// </summary>
    public void CloseImmediate()
    {
        if (!IsOpen) return;
        _animation?.Stop();
        _ = StopCastingAsync();
        ResetAfterClose(restoreFocus: false);
    }

    /// <summary>导航后退先收起预览，不改变页面历史。</summary>
    public void CloseFromNavigation() => Close();

    private void Close()
    {
        if (_closing || !IsOpen) return;
        _closing = true;
        Viewer.IsAnimating = true;
        _animation?.Stop();
        if (_items.Count > 0)
            _lastSourceRect = _request?.ResolveSourceRect(_items[_index]) ?? default;
        else _lastSourceRect = default;
        // 动画统一飞回（用户裁决）；遮罩同步淡出 300ms。视频只淡出，不让播放表面参与缩放。
        bool flyBack = _lastSourceRect.Width > 0 && _items.Count > 0 && !_items[_index].IsVideo;
        _ = StopCastingAsync();
        if (_items.Count > 0) VideoPlayer.Pause();
        var sb = new Storyboard();

        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(300),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        if (flyBack)
        {
            double srcX = _lastSourceRect.X - 16;
            double srcY = _lastSourceRect.Y - 52;
            double hostW = PhotoArea.ActualWidth, hostH = PhotoArea.ActualHeight;
            if (hostW > 0 && hostH > 0)
            {
                foreach (var (prop, to) in new[]
                {
                    ("ScaleX", _lastSourceRect.Width / hostW),
                    ("ScaleY", _lastSourceRect.Height / hostH),
                    ("TranslateX", srcX), ("TranslateY", srcY),
                })
                {
                    var anim = new DoubleAnimation
                    {
                        To = to,
                        Duration = TimeSpan.FromMilliseconds(300),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        EnableDependentAnimation = true,
                    };
                    Storyboard.SetTarget(anim, FlightTransform);
                    Storyboard.SetTargetProperty(anim, prop);
                    sb.Children.Add(anim);
                }
            }
        }

        sb.Completed += (_, _) => ResetAfterClose(restoreFocus: true);
        _animation = sb;
        sb.Begin();
    }

    /// <summary>收起遮罩并清掉这次预览的状态；是否把焦点还给图库由调用方决定。</summary>
    private void ResetAfterClose(bool restoreFocus)
    {
        if (restoreFocus) ReturnFocusRequested?.Invoke();
        Visibility = Visibility.Collapsed;
        RestoreChrome();
        Viewer.Clear();
        Viewer.IsAnimating = false;
        VideoPlayer.Clear();
        _shownPath = null;
        _items = Array.Empty<PhotoItem>();
        _request = null;
        _animation = null;
        _closing = false;
        FlightTransform.ScaleX = FlightTransform.ScaleY = 1;
        FlightTransform.TranslateX = FlightTransform.TranslateY = 0;
    }
    /// <summary>标签保存后重新读一次，让详情面板立刻显示新集合。</summary>
    private readonly Dictionary<string, string> _tagDisplay = new(StringComparer.Ordinal);

    /// <summary>把标签原名 → 展示名（有中文名用中文）缓存下来，供详情面板贴纸用。</summary>
    private void RefreshTagDisplay()
    {
        _tagDisplay.Clear();
        if (AppService.Store is not { } store) return;
        try
        {
            foreach (var tag in store.AllTags())
                if (tag.Display != tag.Name) _tagDisplay[tag.Name] = tag.Display;
        }
        catch (Exception ex)
        {
            AppService.DebugLog("tag display cache failed: " + ex.Message);
        }
    }

    private List<(string Category, string Name)> LoadTagPairs(int imageId)
    {
        var pairs = new List<(string, string)>();
        if (AppService.Store is null || imageId <= 0) return pairs;
        lock (AppService.DbLock)
        {
            try
            {
                var details = AppService.Store.TagDetailsForImages([imageId]);
                if (details.TryGetValue(imageId, out var list))
                {
                    RefreshTagDisplay();
                    var skins = AppService.Store.ImageSkins(imageId);
                    foreach (var tag in list.Where(t => t.Category == "character"))
                    {
                        var owned = skins.Where(s => s.IdentityKey == tag.IdentityKey || string.Equals(s.CharacterTag, tag.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (owned.Count > 0) _tagDisplay[tag.Name] = tag.Display + " · " + string.Join(" / ", owned.Select(s => s.Display));
                    }
                    pairs.AddRange(list.Select(t => (t.Category, t.Name)));
                }
            }
            catch { }
        }
        return pairs;
    }
}
