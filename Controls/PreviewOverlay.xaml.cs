using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Gallery.Models;
using Gallery.Controls.Viewer;
using Gallery.Services;
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

    public void Open(IReadOnlyList<PhotoItem> items, PhotoItem item)
    {
        _items = items;
        _index = Math.Max(0, items.ToList().IndexOf(item));
        if (_items.Count == 0) return;
        ShowCurrent();
        Visibility = Visibility.Visible;
        Opacity = 0;

        var sb = new Storyboard();
        var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(150), EnableDependentAnimation = true };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Begin();

        Focus(FocusState.Programmatic);
        Relayout();
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
        var sb = new Storyboard();
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(120), EnableDependentAnimation = true };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            Viewer.Clear();
        };
        sb.Begin();
    }
}
