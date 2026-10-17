using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    public PreviewOverlay()
    {
        InitializeComponent();
        Viewer.ZoomChanged += pct => ZoomText.Text = $"{pct}%";
        Detail.EditTagsRequested += _ => ToastRequested?.Invoke("标签编辑将在批次 3 提供");
        Detail.OpenExternalRequested += item =>
            FileActions.OpenImage(item.FullPath,
                AppService.Settings.Settings.open_mode,
                AppService.Settings.Settings.open_custom_exe);
        SizeChanged += (_, _) => Relayout();
    }

    private GalleryPage.PreviewRequest? _request;
    private Windows.Foundation.Rect _lastSourceRect;

    public void Open(GalleryPage.PreviewRequest request)
    {
        _request = request;
        _lastSourceRect = request.SourceRect;
        _items = request.Items;
        _index = Math.Max(0, request.Items.ToList().IndexOf(request.Item));
        if (_items.Count == 0) return;

        Visibility = Visibility.Visible;
        Opacity = 0;
        Relayout();
        UpdateLayout();

        ShowCurrent();
        Focus(FocusState.Programmatic);

        // galleryold：源缩略图完整可见 → 飞入（300ms OutCubic，圆角 8）；否则渐入
        bool fly = request.FullyVisible && request.SourceRect.Width > 0 && ActualWidth >= 900;
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(fly ? 300 : 150),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        if (fly)
        {
            // sourceRect（窗口 DIP）→ 相对 ContentArea（margin 16,52）
            double srcX = request.SourceRect.X - 16;
            double srcY = request.SourceRect.Y - 52;
            double hostW = PhotoArea.ActualWidth, hostH = PhotoArea.ActualHeight;
            if (hostW > 0 && hostH > 0)
            {
                FlightTransform.ScaleX = request.SourceRect.Width / hostW;
                FlightTransform.ScaleY = request.SourceRect.Height / hostH;
                FlightTransform.TranslateX = srcX;
                FlightTransform.TranslateY = srcY;

                var cubic = new SplineDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(300) };
                cubic.KeySpline = new KeySpline { ControlPoint1 = new Point(0.215, 0.61), ControlPoint2 = new Point(0.355, 1) };
                cubic.Value = 1;
                foreach (var (prop, target) in new[]
                {
                    ("ScaleX", FlightTransform), ("ScaleY", FlightTransform),
                    ("TranslateX", FlightTransform), ("TranslateY", FlightTransform),
                })
                {
                    var kf = new SplineDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(300) };
                    kf.KeySpline = new KeySpline { ControlPoint1 = new Point(0.215, 0.61), ControlPoint2 = new Point(0.355, 1) };
                    kf.Value = prop.StartsWith("Scale") ? 1 : 0;
                    var anim = new DoubleAnimationUsingKeyFrames();
                    anim.KeyFrames.Add(kf);
                    Storyboard.SetTarget(anim, target);
                    Storyboard.SetTargetProperty(anim, prop);
                    sb.Children.Add(anim);
                }
                var cubic2 = cubic; // 遮罩与飞行同 300ms
            }
        }
        sb.Begin();
    }

    private void Relayout()
    {
        // 照 galleryold layout_panels：宽 ≥900 左图+右详情 360；窄窗上下分栏（图片区高 50%）
        double areaW = ActualWidth, areaH = ActualHeight;
        if (areaW <= 0) return;
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

    private void ShowCurrent()
    {
        var item = _items[_index];
        Detail.SetItem(item);
        Viewer.Clear();
        LoadingText.Visibility = Visibility.Visible;
        // 异步载入大图（不阻塞 UI）
        _ = LoadAsync(item);
        PrevButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async System.Threading.Tasks.Task LoadAsync(PhotoItem item)
    {
        var path = await System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var thumbs = AppService.Scanner?.Thumbs;
                if (thumbs is not null && item.ImageId > 0)
                {
                    var mtime = item.ModifiedAtUnix ?? 0;
                    return thumbs.GetOrBuild(item.FullPath, mtime, item.FileSize) ?? item.FullPath;
                }
                return item.FullPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return item.FullPath;
            }
        });
        if (!IsOpen) return;
        Viewer.SetSource(path, item.PixelWidth, item.PixelHeight);
        LoadingText.Visibility = Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Prev_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => Viewer.ZoomFit();
    private void Zoom100_Click(object sender, RoutedEventArgs e) => Viewer.Zoom100();

    private void Move(int delta)
    {
        int next = Math.Clamp(_index + delta, 0, _items.Count - 1);
        if (next == _index) return;
        _index = next;
        ShowCurrent();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void HandleKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Escape: Close(); break;
            case VirtualKey.Left: Move(-1); break;
            case VirtualKey.Right: Move(1); break;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape: Close(); e.Handled = true; break;
            case VirtualKey.Left: Move(-1); e.Handled = true; break;
            case VirtualKey.Right: Move(1); e.Handled = true; break;
        }
    }

    /// <summary>仅精确命中遮罩空白（非按钮/图片/详情）时关闭。</summary>
    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, this)) Close();
    }

    private void Close()
    {
        // galleryold：飞回缩略图位置（300ms OutCubic）+ 遮罩同步淡出，完成后隐藏
        bool flyBack = _request is { FullyVisible: true } && ActualWidth >= 900 &&
                       _lastSourceRect.Width > 0;
        var sb = new Storyboard();

        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(flyBack ? 300 : 120),
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
            Visibility = Visibility.Collapsed;
            Viewer.Clear();
            FlightTransform.ScaleX = FlightTransform.ScaleY = 1;
            FlightTransform.TranslateX = FlightTransform.TranslateY = 0;
        };
        sb.Begin();
    }
}
