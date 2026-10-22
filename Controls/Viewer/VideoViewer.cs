using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Gallery.Models;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 全屏查看器里的视频播放面：系统 MediaPlayerElement + 传输控件，打开自动播放、带声音。
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
        _element = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            AutoPlay = true,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _element.SetMediaPlayer(_player);
        Children.Add(_element);
    }

    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

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
            _player.Volume = 1;
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
