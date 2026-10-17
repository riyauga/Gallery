using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Gallery.Models;
using Windows.System;

namespace Gallery.Controls;

public sealed partial class PreviewOverlay : Grid
{
    private IReadOnlyList<PhotoItem> _items = Array.Empty<PhotoItem>();
    private int _index;

    public PreviewOverlay()
    {
        InitializeComponent();
    }

    public void Open(IReadOnlyList<PhotoItem> items, PhotoItem item)
    {
        _items = items;
        _index = Math.Max(0, items.ToList().IndexOf(item));
        if (_items.Count == 0) return;
        ShowCurrent();
        Visibility = Visibility.Visible;
        Opacity = 0;
        ImageScale.ScaleX = 0.98;
        ImageScale.ScaleY = 0.98;

        var sb = new Storyboard();
        var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(150), EnableDependentAnimation = true };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sx = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(150), EnableDependentAnimation = true };
        Storyboard.SetTarget(sx, ImageScale);
        Storyboard.SetTargetProperty(sx, "ScaleX");
        var sy = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(150), EnableDependentAnimation = true };
        Storyboard.SetTarget(sy, ImageScale);
        Storyboard.SetTargetProperty(sy, "ScaleY");
        sb.Children.Add(fade);
        sb.Children.Add(sx);
        sb.Children.Add(sy);
        sb.Begin();

        Focus(FocusState.Programmatic);
    }

    private void ShowCurrent()
    {
        var item = _items[_index];
        NameText.Text = item.File;
        PreviewImage.Source = new BitmapImage(new Uri(item.FullPath));
        PrevButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Prev_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);

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
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case VirtualKey.Left:
                Move(-1);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                Move(1);
                e.Handled = true;
                break;
        }
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
            PreviewImage.Source = null;
        };
        sb.Begin();
    }
}
