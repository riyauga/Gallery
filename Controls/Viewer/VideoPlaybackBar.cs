using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 视频传输条：照搬 Photos Lightbox 悬浮控制条（CustomMTC）的排版。
/// 去掉裁剪与倍速；只操作传入的播放面，不访问图库。
/// </summary>
public sealed class VideoPlaybackBar : Grid
{
    private readonly Border _chrome;
    private readonly VideoViewer _video;
    private readonly Button _play;
    private readonly Slider _seek = new() { Minimum = 0, StepFrequency = 0.01 };
    private readonly TextBlock _current = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _duration = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private ViewerChrome.Palette _palette = ViewerChrome.Palette.Dark;
    private bool _syncing, _seeking, _detached;

    public VideoPlaybackBar(VideoViewer video, Action cast)
    {
        _video = video;
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = new Thickness(0, 0, 0, 12);
        _chrome = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6),
            Height = 48,
            MaxWidth = 720,
            BorderThickness = new Thickness(1),
        };

        _play = ViewerChrome.IconButton(ViewerChrome.Glyph.Pause, "播放/暂停", _palette, 32, 15);
        _play.Click += (_, _) => video.TogglePlayPause();

        var castButton = ViewerChrome.IconButton(ViewerChrome.Glyph.Cast, "投屏", _palette, 32, 15);
        castButton.Click += (_, _) => cast();

        var volumeIcon = ViewerChrome.Icon(ViewerChrome.Glyph.Volume, 15, _palette.Text);
        var volume = new Slider
        {
            Width = 80,
            Minimum = 0,
            Maximum = 100,
            Value = video.Volume * 100,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(volume, "音量");
        volume.ValueChanged += (_, _) => video.Volume = volume.Value / 100;

        var grid = new Grid();
        for (int index = 0; index < 7; index++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = index == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });

        Add(_play, 0);
        var times = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
        };
        times.Children.Add(_current);
        times.Children.Add(new TextBlock { Text = "/", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        times.Children.Add(_duration);
        Add(times, 1);
        Add(_seek, 2);
        Add(volumeIcon, 3);
        Add(volume, 4);
        Add(castButton, 5);

        _chrome.Child = grid;
        Children.Add(_chrome);

        void Add(FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        _seek.ValueChanged += (_, _) =>
        {
            if (!_syncing && _video.Duration.TotalSeconds > 0)
                _video.Position = TimeSpan.FromSeconds(_seek.Value);
        };
        _seek.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _seeking = true), true);
        _seek.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _seeking = false; Refresh(); }), true);
        _seek.PointerCaptureLost += (_, _) => { _seeking = false; Refresh(); };
        video.PlaybackStateChanged += Refresh;
        video.PositionChanged += Refresh;
        video.DurationChanged += Refresh;
        ApplyTheme(ActualTheme);
        ActualThemeChanged += (_, _) => ApplyTheme(ActualTheme);
        Refresh();
    }

    public void ApplyTheme(ElementTheme theme)
    {
        _palette = ViewerChrome.Palette.For(theme);
        _chrome.Background = ViewerChrome.Brush(_palette.Panel);
        _chrome.BorderBrush = ViewerChrome.Brush(_palette.Divider);
        _current.Foreground = ViewerChrome.Brush(_palette.Text);
        _duration.Foreground = ViewerChrome.Brush(_palette.SecondaryText);
    }

    private static string Time(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    public void Refresh()
    {
        if (_detached) return;
        _play.Content = ViewerChrome.Icon(
            _video.IsPlaying ? ViewerChrome.Glyph.Pause : ViewerChrome.Glyph.Play, 15, _palette.Text);
        _syncing = true;
        _seek.Maximum = Math.Max(1, _video.Duration.TotalSeconds);
        if (!_seeking) _seek.Value = Math.Clamp(_video.Position.TotalSeconds, 0, _seek.Maximum);
        _current.Text = Time(_video.Position);
        _duration.Text = Time(_video.Duration);
        _syncing = false;
    }

    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        _video.PlaybackStateChanged -= Refresh;
        _video.PositionChanged -= Refresh;
        _video.DurationChanged -= Refresh;
    }
}
