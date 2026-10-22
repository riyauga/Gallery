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
    public Func<PhotoItem, Task>? RenameRequested { get; set; }
    public Func<PhotoItem, bool, Task<bool>>? FavoriteRequested { get; set; }

    public PreviewOverlay()
    {
        InitializeComponent();
        // ImageViewer 平移时会吃掉 PointerMoved（Handled=true），边缘唤醒必须用 handledEventsToo 订阅
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnOverlayPointerMoved), true);
        // 详情面板的贴纸显示中文名（词库补出来的），点开/编辑仍然用原名
        Detail.DisplayName = name => _tagDisplay.TryGetValue(name, out var shown) ? shown : name;
        Detail.RatingTooltip = BuildRatingTooltip;
        Detail.FavoriteRequested = async (item, favorite) =>
        {
            var result = FavoriteRequested is null
                ? item.Favorite
                : await FavoriteRequested(item, favorite);
            Detail.SetFavorite(result);
            return result;
        };
        Viewer.ZoomChanged += SyncZoomControls;
        Viewer.NavigateRequested += Move;
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
        Detail.ToastRequested += message => ToastRequested?.Invoke(message);
        SizeChanged += (_, _) => Relayout();

        // 百分比下拉（Photos 缩放条）：预设档位，当前缩放以占位文本显示
        foreach (var percent in new[] { "25%", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%" })
            ZoomPercentBox.Items.Add(percent);

        // 下拉展开期间不收起缩放条（指针会移到弹出层里）
        ZoomPercentBox.DropDownOpened += (_, _) => ShowZoomBar();
        ZoomPercentBox.DropDownClosed += (_, _) => TryHideZoomBar();
    }

    private bool _syncingZoom;
    private Storyboard? _zoomBarAnimation;
    private bool _fullscreen;
    private bool _infoInFullscreen;
    private double _pointerX;
    private Storyboard? _topBarAnimation;

    /// <summary>WinUI 3 的 UIElement.PointerCaptures 在无捕获时可能返回 null，别直接取 Count。</summary>
    private bool HasPointerCapture() => ZoomSlider.PointerCaptures is { Count: > 0 };

    private void ZoomBarHost_PointerEntered(object sender, PointerRoutedEventArgs e) => ShowZoomBar();
    private void ZoomBarHost_PointerExited(object sender, PointerRoutedEventArgs e) => TryHideZoomBar();

    /// <summary>浮出底部缩放条（Photos 是 PointerMoved 后自动淡出，这里按用户要求改成悬停才显示）。</summary>
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
        if (ZoomPercentBox.IsDropDownOpen) return;
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
            if (ZoomPercentBox.IsDropDownOpen || HasPointerCapture()) return;
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

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!IsFullscreen);

    private bool IsFullscreen =>
        GetAppWindow()?.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;

    /// <summary>
    /// 全屏：切窗口 presenter + 收起应用标题栏行 + 遮罩转不透明 + 图片区上下留白对齐
    /// （原来只在窗口层全屏、`ContentArea` 还留着 52 的标题栏上边距，导致顶部一条黑带）。
    /// 证据：Photos 的 FullScreenService（TryEnter/Exit/ToggleFullScreenMode + FullScreenModeChanged）
    /// 与 ViewerRootViewModel.GetTitleBarRowHeight(isFullScreenMode) —— 具体实现是原生 WinRT 组件，源码不可读。
    /// </summary>
    private void SetFullscreen(bool full)
    {
        var appWindow = GetAppWindow();
        if (appWindow is null) return;
        _fullscreen = full;
        _infoInFullscreen = false;
        appWindow.SetPresenter(full
            ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
            : Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
        MainWindow.Instance?.SetTitleBarHidden(full);
        // 全屏时遮罩转不透明（否则图库在下面隐约可见）
        Background = new SolidColorBrush(full
            ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0, 0, 0)
            : Microsoft.UI.ColorHelper.FromArgb(0xCC, 0, 0, 0));
        // 全屏是沉浸式：图片贴边铺满，只留顶栏/缩放条/左右箭头这些按需浮出的控件
        ContentArea.Margin = full
            ? new Thickness(0)
            : new Thickness(16, 52, 16, 16);
        FullscreenIcon.Glyph = full ? "\uE73F" : "\uE740";
        TopBar.Visibility = Visibility.Collapsed;
        TopBar.Opacity = 0;
        CloseButton.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        ApplyFullscreenLayout();
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
        bool left = _index > 0 && x <= edge;
        bool right = _index < _items.Count - 1 && x >= ActualWidth - edge;
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
        PrevButton.Visibility = Visibility.Collapsed;
    }

    private void RightEdgeHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        NextButton.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RightEdgeHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        NextButton.Visibility = Visibility.Collapsed;
    }

    private void TopBarHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
        ShowTopBar();
    }

    private void TopBarHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullscreen) return;
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
        if (TopBar.Visibility != Visibility.Visible) return;
        _topBarAnimation?.Stop();
        var fade = new DoubleAnimation { To = 0.01, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(fade, TopBar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) =>
        {
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
        var appWindow = GetAppWindow();
        if (appWindow is not null && appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
            appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
        MainWindow.Instance?.SetTitleBarHidden(false);
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

    private void Relayout()
    {
        // 照 galleryold layout_panels：宽 ≥900 左图+右详情 360；窄窗上下分栏（图片区高 50%）
        double areaW = ActualWidth, areaH = ActualHeight;
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
                Viewer.Clear();
                Viewer.IsAnimating = false;
                VideoPlayer.SetSource(item);
            }
        }
        else
        {
            VideoPlayer.Clear();
            Viewer.SetSource(item, thumbnail);
        }

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

    public void HandleKey(VirtualKey key)
    {
        if (_closing) return;
        switch (key)
        {
            // Photos 语义：全屏时 Esc 先退出全屏，再按一次才关查看器
            case VirtualKey.Escape when IsFullscreen: SetFullscreen(false); break;
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
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space)
        {
            if (!_closing && _items.Count > 0 && _items[_index].IsVideo)
            {
                VideoPlayer.TogglePlayPause();
                e.Handled = true;
            }
            return;
        }
        if (e.Key is VirtualKey.Escape or VirtualKey.Left or VirtualKey.Right or VirtualKey.Delete or VirtualKey.F2)
        {
            HandleKey(e.Key);
            e.Handled = true;
        }
    }

    /// <summary>删除/重命名/移动前由宿主调用：暂停并释放视频文件句柄。</summary>
    public void ReleaseVideo()
    {
        if (!IsOpen || _items.Count == 0 || !_items[_index].IsVideo) return;
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

        sb.Completed += (_, _) =>
        {
            // 先移出焦点再隐藏，避免 XAML 自动选中窗口内第一个输入框。
            ReturnFocusRequested?.Invoke();
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
        };
        _animation = sb;
        sb.Begin();
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

    private static List<(string Category, string Name)> LoadTagPairs(int imageId)
    {
        var pairs = new List<(string, string)>();
        if (AppService.Store is null || imageId <= 0) return pairs;
        lock (AppService.DbLock)
        {
            try
            {
                var details = AppService.Store.TagDetailsForImages([imageId]);
                if (details.TryGetValue(imageId, out var list))
                    pairs.AddRange(list.Select(t => (t.Category, t.Name)));
            }
            catch { }
        }
        return pairs;
    }
}
