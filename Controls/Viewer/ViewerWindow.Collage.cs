using Gallery.Controls.Collage;
using Gallery.Services;
using SixLabors.ImageSharp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 拼图模式接入：复用查看器窗口的标题栏与返回按钮，
/// 中间内容整体换成 <see cref="CollageView"/>，与原版网页编辑器互不影响。
/// </summary>
public sealed partial class ViewerWindow
{
    private CollageView? _collage;
    private bool _collaging, _enteringCollage, _confirmingCollageExit;

    private async Task EnterCollageAsync()
    {
        if (_collaging || _enteringCollage || _item is null || _item.IsVideo) return;
        _enteringCollage = true;
        _collaging = true;
        var view = new CollageView();
        _collage = view;
        // 不能套 RunAsync：SaveEditedPixelsAsync 内部自己还有一层 RunAsync，
        // 外层占住 _busy 会让内层直接返回，表现为「选完路径什么都没发生」。
        view.RenderRequested += () => _ = SaveCollageAsync(false);
        view.CopyRequested += () => _ = SaveCollageAsync(true);
        view.Status += text => { if (!_closed) ShowStatus(text); };
        Grid.SetRow(view, 1);
        Grid.SetRowSpan(view, 3);
        _root.Children.Add(view);
        _image.Visibility = Visibility.Collapsed;
        _filmstrip.Visibility = Visibility.Collapsed;
        _image.Margin = new Thickness(0);
        _video.Margin = new Thickness(0);
        SetInfoPane(false);
        SetArrows(false);
        _bottomBar.Visibility = Visibility.Collapsed;
        _titleCommands.Visibility = Visibility.Collapsed;
        _backButton!.Visibility = Visibility.Visible;
        _titleText.HorizontalAlignment = HorizontalAlignment.Left;
        _titleText.Margin = new Thickness(44, 0, 0, 0);
        view.ApplyTheme(_root.ActualTheme);
        try { await view.OpenAsync(CurrentPath); }
        catch (Exception error)
        {
            if (!_closed && ReferenceEquals(_collage, view))
            {
                FaultLog.Write("collage-open", error);
                ExitCollage();
                ShowStatus("无法打开拼图模式，请确认图片可读。");
            }
        }
        finally { _enteringCollage = false; }
#if GALLERY_DEVELOPMENT
        await RunCollageSelfTestAsync();
        var shot = Environment.GetCommandLineArgs().FirstOrDefault(item => item.StartsWith("--collage-shot=", StringComparison.Ordinal));
        if (shot is not null)
        {
            if (!_closed && _collage is not null)
            {
                var state0 = _collage.DumpState();
                _collage.RunSelfTest();
                await Task.Delay(600);
                var state1 = _collage.DumpState();
                var report = Path.Combine(Path.GetTempPath(), "ptv", "collage_state.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, "before: " + state0 + "\nafter:  " + state1);
            }
            await Task.Delay(1200);
            if (!_closed && _collage is not null) await _collage.SaveSnapshotAsync(shot[15..]);
            _root.RequestedTheme = ElementTheme.Light;
            await Task.Delay(900);
            if (!_closed && _collage is not null) await _collage.SaveSnapshotAsync(shot[15..].Replace(".png", "-light.png"));
        }
#endif
    }

    private async Task<bool> RequestExitCollageAsync(bool closeWindow = false)
    {
        if (_confirmingCollageExit || _collage?.IsSaving == true || _enteringCollage) return false;
        _confirmingCollageExit = true;
        try
        {
            if (_collage is not null && _collage.HasChanges())
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = _root.XamlRoot,
                    Title = "是否离开而不保存?",
                    Content = "拼图尚未保存。",
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    Background = ViewerChrome.Brush(_palette.Background),
                    BorderBrush = (Brush)Application.Current.Resources["SystemControlTransientBorderBrush"],
                };
                dialog.Resources["ContentDialogBackground"] = ViewerChrome.Brush(_palette.Background);
                dialog.Resources["ContentDialogTopOverlay"] = ViewerChrome.Transparent();
                if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
            }
            ExitCollage();
            if (closeWindow) Close();
            return true;
        }
        finally { _confirmingCollageExit = false; }
    }

    private void ExitCollage()
    {
        if (!_collaging) return;
        _collaging = false;
        if (_collage is not null)
        {
            _root.Children.Remove(_collage);
            _collage = null;
        }
        _bottomBar.Visibility = Visibility.Visible;
        _titleCommands.Visibility = Visibility.Visible;
        if (_backButton is not null) _backButton.Visibility = Visibility.Collapsed;
        _titleText.HorizontalAlignment = HorizontalAlignment.Center;
        _titleText.Margin = new Thickness(0);
        if (!_closed && _item?.IsVideo != true) _image.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 拼图保存。产物永远是一张新图：保存与另存为都交给系统文件对话框，
    /// 默认目录同源图目录、默认名「原名-拼图」，重名与覆盖由系统自己处理。
    /// </summary>
    private async Task SaveCollageAsync(bool toClipboard)
    {
        if (_collage is null || _collage.IsSaving || _busy || _closed) return;
        if (toClipboard)
        {
            _collage.IsSaving = true;
            try
            {
                ShowStatus("正在合成拼图…");
                using var copy = await _collage.RenderAsync();
                if (copy is null) { ShowStatus("拼图合成失败，请确认图片仍然可读。"); return; }
                await CopyImageToClipboardAsync(copy, "已复制拼图到剪贴板。");
            }
            finally { if (_collage is not null) _collage.IsSaving = false; }
            return;
        }
        var target = await PickCollageTargetAsync();
        if (target is null) return;
        await WriteCollageAsync(target);
    }

    /// <summary>合成并以临时文件落位，成功后退出拼图并打开结果。</summary>
    private async Task WriteCollageAsync(string target)
    {
        if (_collage is null || _collage.IsSaving || _busy || _closed) return;
        _collage.IsSaving = true;
        try
        {
            ShowStatus("正在合成拼图…");
            using var image = await _collage.RenderAsync();
            if (image is null)
            {
                ShowStatus("拼图合成失败，请确认图片仍然可读。");
                return;
            }
            await RunAsync(async () =>
            {
                var temporary = Path.Combine(Path.GetDirectoryName(target)!, "." + Guid.NewGuid().ToString("N") + Path.GetExtension(target));
                try
                {
                    if (Path.GetExtension(target).ToLowerInvariant() is ".jpg" or ".jpeg")
                        await image.SaveAsync(temporary, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92 });
                    else await image.SaveAsync(temporary);
                    File.Move(temporary, target, true);
                    ExitCollage();
                    await OpenPathAsync(target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
        }
        finally
        {
            if (_collage is not null) _collage.IsSaving = false;
        }
    }

    /// <summary>另存为：系统选择器，任意位置。</summary>
    private async Task<string?> PickCollageTargetAsync()
    {
        var original = CurrentPath;
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(original) + "-拼图",
            SuggestedFolder = Path.GetDirectoryName(original)!,
        };
        picker.FileTypeChoices.Add("PNG 图像", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG 图像", new List<string> { ".jpg" });
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

#if GALLERY_DEVELOPMENT
    /// <summary>开发自测：验证保存链路与返回按钮分发，结果写到 %TEMP%\ptv\collage_io.txt。</summary>
    private async Task RunCollageSelfTestAsync()
    {
        var cli = Environment.GetCommandLineArgs();
        bool saving = cli.Contains("--collage-save"), backing = cli.Contains("--collage-back");
        if (!saving && !backing) return;
        var log = new List<string>();
        try
        {
            if (saving && _collage is not null)
            {
                var originalPath = CurrentPath;
                var before = new FileInfo(originalPath).Length;
                var folder = Path.GetDirectoryName(originalPath);
                var extra = Directory.EnumerateFiles(folder!).FirstOrDefault(file =>
                    !file.Equals(originalPath, StringComparison.OrdinalIgnoreCase) && file.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                if (extra is not null) _collage.Canvas.Add(extra);
                var layout = _collage.Layout;
                log.Add($"layout={layout.Width:0}x{layout.Height:0} count={_collage.Canvas.Count}");
                var expected = Path.Combine(folder!, Path.GetFileNameWithoutExtension(originalPath) + "-拼图.png");
                await WriteCollageAsync(expected);
                await Task.Delay(1500);
                log.Add($"new={Path.GetFileName(expected)} exists={File.Exists(expected)} bytes={(File.Exists(expected) ? new FileInfo(expected).Length : 0)}");
                var meta = Gallery.Services.ImageInfo.ReadMeta(expected);
                log.Add($"newSize={meta.Width}x{meta.Height} original={new FileInfo(originalPath).Length}B unchanged={new FileInfo(originalPath).Length == before} collaging={_collaging}");
            }
        }
        catch (Exception error) { log.Add("EX " + error.GetType().Name + ": " + error.Message); }
        if (backing)
        {
            var closed = await RequestExitCollageAsync();
            log.Add($"back: accepted={closed} collaging={_collaging}");
        }
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ptv"));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "ptv", "collage_io.txt"), log);
    }
#endif
}
