using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Gallery.Models;
using Windows.Media.Casting;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 全屏查看器里的视频播放面：MediaPlayerElement 只负责画面，播放条由 PreviewOverlay 自绘。
/// 只负责播放与释放；图片/视频的互斥、翻页和键盘在 PreviewOverlay 里协调。
/// </summary>
public sealed class VideoViewer : Grid
{
    private readonly MediaPlayerElement _element;
    private readonly MediaPlayer _player;
    private MediaSource? _source;
    private int _version;

    public event Action<bool>? LoadingChanged;
    public event Action<string>? Failed;
    public event Action? PlaybackStateChanged;
    public event Action? PositionChanged;
    public event Action? DurationChanged;

    public VideoViewer()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _player = new MediaPlayer
        {
            AutoPlay = true,
            Volume = 1,
        };
        _player.MediaOpened += (_, _) => LoadingChanged?.Invoke(false);
        _player.MediaFailed += (_, args) =>
        {
            LoadingChanged?.Invoke(false);
            Failed?.Invoke(string.IsNullOrWhiteSpace(args.ErrorMessage) ? "系统解码器无法打开这个文件" : args.ErrorMessage);
        };
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => RaiseOnUi(PlaybackStateChanged);
        _player.PlaybackSession.PositionChanged += (_, _) => RaiseOnUi(PositionChanged);
        _player.PlaybackSession.NaturalDurationChanged += (_, _) => RaiseOnUi(DurationChanged);
        _player.MediaEnded += (_, _) => RaiseOnUi(PlaybackStateChanged);
        _element = new MediaPlayerElement
        {
            // 播放条在 PreviewOverlay 里自绘；系统条的 Cast 等按钮也一并收起。
            AreTransportControlsEnabled = false,
            AutoPlay = true,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _element.SetMediaPlayer(_player);
        Children.Add(_element);
    }

    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public TimeSpan Position
    {
        get
        {
            try { return _player.PlaybackSession.Position; }
            catch (ObjectDisposedException) { return TimeSpan.Zero; }
        }
        set
        {
            try { _player.PlaybackSession.Position = value < TimeSpan.Zero ? TimeSpan.Zero : value; }
            catch (InvalidOperationException) { }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            try { return _player.PlaybackSession.NaturalDuration; }
            catch (ObjectDisposedException) { return TimeSpan.Zero; }
        }
    }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public Stretch Stretch
    {
        get => _element.Stretch;
        set => _element.Stretch = value;
    }

    public double PlaybackRate
    {
        get
        {
            try { return _player.PlaybackSession.PlaybackRate; }
            catch (ObjectDisposedException) { return 1; }
        }
        set
        {
            try { _player.PlaybackSession.PlaybackRate = value; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    public CastingSource GetAsCastingSource() => _player.GetAsCastingSource();

    private void RaiseOnUi(Action? action)
    {
        if (action is null) return;
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    public async void SetSource(PhotoItem item)
    {
        Clear();
        int version = ++_version;
        LoadingChanged?.Invoke(true);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
            if (version != _version) return;
            var source = MediaSource.CreateFromStorageFile(file);
            _source = source;
            _element.Source = source;
            _player.Play();
            // 个别文件不会触发 MediaOpened；媒体失败会由 MediaFailed 收尾。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            if (version != _version) return;
            LoadingChanged?.Invoke(false);
            Failed?.Invoke(ex.Message);
        }
    }

    public void Pause()
    {
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
    }

    public void TogglePlayPause()
    {
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
        else _player.Play();
    }

    /// <summary>暂停并释放文件句柄；删除/重命名/切页/关闭前调用。</summary>
    public void Clear()
    {
        _version++;
        try { _player.Pause(); } catch (InvalidOperationException) { }
        _element.Source = null;
        try { _player.Source = null; } catch (InvalidOperationException) { }
        _source?.Dispose();
        _source = null;
        LoadingChanged?.Invoke(false);
    }
}
