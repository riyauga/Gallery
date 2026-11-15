using System.IO;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Storage.Streams;

namespace Gallery.Controls.Viewer;

public sealed partial class ViewerWindow
{
    private PhotoEditorView? _photoEditor;
    private Button? _editEntry, _backButton;
    private bool _editing, _enteringEdit, _confirmingEditExit;

    private void BuildEditChrome()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(ViewerChrome.Icon(ViewerChrome.Glyph.Edit, 16, _palette.Text));
        content.Children.Add(new TextBlock { Text = "编辑", VerticalAlignment = VerticalAlignment.Center });
        _editEntry = new Button { Content = content, Height = 36, MinWidth = 78, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(6, 0, 6, 0), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(_editEntry, "编辑");
        ViewerChrome.WirePressMotion(_editEntry);
        _editEntry.Click += async (_, _) => await EnterEditAsync();
        _titleCommands.Children.Insert(0, _editEntry);
        _backButton = ViewerChrome.IconButton(ViewerChrome.Glyph.Back, "返回", _palette, 32, 15);
        _backButton.Click += async (_, _) => await RequestExitEditAsync();
        _backButton.Visibility = Visibility.Collapsed;
        _backButton.HorizontalAlignment = HorizontalAlignment.Left;
        _backButton.Margin = new Thickness(6, 0, 0, 0);
        _titleBar.Children.Add(_backButton);
    }

    private async Task EnterEditAsync()
    {
        if (_editing || _enteringEdit || _item is null || _item.IsVideo) return;
        _enteringEdit = true;
        _editing = true;
        var editor = new PhotoEditorView();
        _photoEditor = editor;
        editor.SaveRequested = SaveEditedPixelsAsync;
        editor.CloseRequested += () => _ = RequestExitEditAsync();
        editor.EscapeRequested += () => _ = RequestExitEditAsync();
        editor.Failed += ShowStatus;
        Grid.SetRow(editor, 1);
        Grid.SetRowSpan(editor, 3);
        _root.Children.Add(editor);
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
        try { await editor.OpenAsync(CurrentPath, _root.ActualTheme); }
        catch (Exception error)
        {
            if (!_closed && ReferenceEquals(_photoEditor, editor))
            {
                FaultLog.Write("photo-editor-open", error);
                ExitEdit(false);
                ShowStatus("无法打开图片编辑器，请确认图片格式和 WebView2 可用。");
            }
        }
        finally { _enteringEdit = false; }
    }

    private async Task<bool> RequestExitEditAsync(bool closeWindow = false)
    {
        if (_confirmingEditExit || _photoEditor?.Saving == true || _enteringEdit) return false;
        _confirmingEditExit = true;
        try
        {
            if (_photoEditor is not null && await _photoEditor.HasEditsAsync())
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = _root.XamlRoot, Title = "是否离开而不保存?", Content = "最近的更改将丢失。",
                    PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
                    Background = ViewerChrome.Brush(_palette.Background),
                    BorderBrush = (Brush)Application.Current.Resources["SystemControlTransientBorderBrush"],
                };
                dialog.Resources["ContentDialogBackground"] = ViewerChrome.Brush(_palette.Background);
                dialog.Resources["ContentDialogTopOverlay"] = ViewerChrome.Transparent();
                if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
            }
            ExitEdit(false);
            if (closeWindow) Close();
            return true;
        }
        finally { _confirmingEditExit = false; }
    }

    private void ExitEdit(bool refresh)
    {
        if (!_editing) return;
        _editing = false;
        if (_photoEditor is not null)
        {
            _root.Children.Remove(_photoEditor);
            _photoEditor.Dispose();
            _photoEditor = null;
        }
        _bottomBar.Visibility = Visibility.Visible;
        _titleCommands.Visibility = Visibility.Visible;
        if (_backButton is not null) _backButton.Visibility = Visibility.Collapsed;
        _titleText.HorizontalAlignment = HorizontalAlignment.Center;
        _titleText.Margin = new Thickness(0);
        if (!_closed && _item?.IsVideo != true) _image.Visibility = Visibility.Visible;
        if (refresh && !_closed) _ = RunAsync(() => OpenPathAsync(CurrentPath));
    }

    private async Task SaveEditedPixelsAsync(Image<Rgba32> image, int kind)
    {
        if (kind == 2)
        {
            using var buffer = new MemoryStream();
            await image.SaveAsPngAsync(buffer);
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(buffer.ToArray()); await writer.StoreAsync(); writer.DetachStream();
            }
            stream.Seek(0);
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            ShowStatus("已复制编辑后的图片。");
            return;
        }
        var original = CurrentPath;
        var target = original;
        if (kind == 1)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(original) + "-副本",
                SuggestedFolder = Path.GetDirectoryName(original)!,
            };
            picker.FileTypeChoices.Add("PNG 图像", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG 图像", new List<string> { ".jpg" });
            picker.FileTypeChoices.Add("WebP 图像", new List<string> { ".webp" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            target = file.Path;
        }
        await RunAsync(async () =>
        {
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, "." + Guid.NewGuid().ToString("N") + Path.GetExtension(target));
            try
            {
                if (Path.GetExtension(target).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif" or ".jpe")
                    await image.SaveAsync(temporary, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 90 });
                else await image.SaveAsync(temporary);
                if (target.Equals(original, StringComparison.OrdinalIgnoreCase))
                    await _host.ChangeFileAsync(original, original, () => Task.Run(() => File.Move(temporary, original, true)));
                else
                {
                    File.Move(temporary, target, true);
                    ExitEdit(false);
                    await OpenPathAsync(target);
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
    }

    private async Task ResizeImageAsync()
    {
        if (_item is null || _item.IsVideo) return;
        var path = CurrentPath;
        var meta = await Task.Run(() => Gallery.Services.ImageInfo.ReadMeta(path));
        int width = meta.Width ?? 0, height = meta.Height ?? 0;
        if (width <= 0 || height <= 0) { ShowStatus("读不到图片尺寸。"); return; }

        var widthBox = new NumberBox { Value = width, Minimum = 1, Maximum = 20000, Height = 30, MinHeight = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var heightBox = new NumberBox { Value = height, Minimum = 1, Maximum = 20000, Height = 30, MinHeight = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var widthHeader = new TextBlock { Text = "宽度 (px)", FontSize = 12 };
        var heightHeader = new TextBlock { Text = "高度 (px)", FontSize = 12 };
        var keep = new FontIcon { Glyph = "\uE71B", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, Width = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
        var pixels = new RadioButton { Content = "像素", IsChecked = true, GroupName = "ResizeUnits" };
        var percent = new RadioButton { Content = "百分比", GroupName = "ResizeUnits" };
        var units = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { pixels, percent } };
        bool syncing = false;
        double ratio = width / (double)height;
        bool sourceIsJpeg = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif" or ".jpe";
        var qualityLabel = new TextBlock();
        var quality = new Slider { Minimum = 10, Maximum = 100, Value = sourceIsJpeg ? 80 : 100, StepFrequency = 1, MinWidth = 200 };
        var format = new ComboBox { Header = "文件类型", ItemsSource = new[] { "JPG", "PNG", "BMP", "TIFF" }, SelectedIndex = sourceIsJpeg ? 0 : 1, MinWidth = 110 };
        var currentDimensions = new TextBlock { Text = $"{width} x {height} 像素", HorizontalAlignment = HorizontalAlignment.Right };
        var proposedDimensions = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        var proposedSize = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        var proposedFormat = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        (int Width, int Height) Dimensions() => (
            (int)Math.Clamp(Math.Round(double.IsFinite(widthBox.Value) ? (percent.IsChecked == true ? widthBox.Value * width / 100.0 : widthBox.Value) : width, MidpointRounding.AwayFromZero), 1, 20000),
            (int)Math.Clamp(Math.Round(double.IsFinite(heightBox.Value) ? (percent.IsChecked == true ? heightBox.Value * height / 100.0 : heightBox.Value) : height, MidpointRounding.AwayFromZero), 1, 20000));
        int estimateVersion = 0;
        bool resizeOpen = true;
        async Task EstimateAsync(int version, int targetWidth, int targetHeight, int qualityValue, int formatIndex)
        {
            await Task.Delay(180);
            if (!resizeOpen || version != estimateVersion) return;
            using var source = await ImageEditService.LoadAsync(path, 0);
            if (source is null || !resizeOpen || version != estimateVersion) return;
            using var snapshot = source.Clone();
            long bytes = await Task.Run(() =>
            {
                using var edited = ImageEditService.Resize(snapshot, targetWidth, targetHeight);
                using var buffer = new MemoryStream();
                SixLabors.ImageSharp.Formats.IImageEncoder encoder = formatIndex switch
                {
                    0 => new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = qualityValue },
                    1 => new SixLabors.ImageSharp.Formats.Png.PngEncoder(),
                    2 => new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder(),
                    _ => new SixLabors.ImageSharp.Formats.Tiff.TiffEncoder(),
                };
                edited.Save(buffer, encoder);
                return buffer.Length;
            });
            if (resizeOpen && version == estimateVersion) proposedSize.Text = ViewerChrome.FormatSize(bytes);
        }
        void UpdateSummary()
        {
            var size = Dimensions();
            proposedDimensions.Text = $"{size.Width} x {size.Height} 像素";
            proposedFormat.Text = format.SelectedItem?.ToString();
            proposedSize.Text = "…";
            qualityLabel.Text = $"质量: {quality.Value:0}% ({(quality.Value >= 70 ? "高" : quality.Value >= 35 ? "中" : "低")})";
            quality.IsEnabled = format.SelectedIndex == 0;
            _ = EstimateAsync(++estimateVersion, size.Width, size.Height, (int)quality.Value, format.SelectedIndex);
        }
        widthBox.ValueChanged += (_, _) => { if (syncing) return; syncing = true; heightBox.Value = percent.IsChecked == true ? widthBox.Value : Math.Round(widthBox.Value / ratio, MidpointRounding.AwayFromZero); syncing = false; UpdateSummary(); };
        heightBox.ValueChanged += (_, _) => { if (syncing) return; syncing = true; widthBox.Value = percent.IsChecked == true ? heightBox.Value : Math.Round(heightBox.Value * ratio, MidpointRounding.AwayFromZero); syncing = false; UpdateSummary(); };
        void ChangeUnits(bool percentage)
        {
            syncing = true;
            widthHeader.Text = percentage ? "宽度 (%)" : "宽度 (px)";
            heightHeader.Text = percentage ? "高度 (%)" : "高度 (px)";
            widthBox.Value = percentage ? widthBox.Value / width * 100 : widthBox.Value * width / 100;
            heightBox.Value = percentage ? heightBox.Value / height * 100 : heightBox.Value * height / 100;
            syncing = false; UpdateSummary();
        }
        percent.Checked += (_, _) => ChangeUnits(true);
        pixels.Checked += (_, _) => ChangeUnits(false);
        quality.ValueChanged += (_, _) => UpdateSummary();
        format.SelectionChanged += (_, _) => { quality.Value = format.SelectedIndex == 0 ? 80 : 100; UpdateSummary(); };
        var sizes = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 20, 0, 0) };
        sizes.ColumnDefinitions.Add(new ColumnDefinition()); sizes.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); sizes.ColumnDefinitions.Add(new ColumnDefinition());
        sizes.Children.Add(new StackPanel { Spacing = 5, Children = { widthHeader, widthBox } });
        Grid.SetColumn(keep, 1); sizes.Children.Add(keep);
        var heightInput = new StackPanel { Spacing = 5, Children = { heightHeader, heightBox } };
        Grid.SetColumn(heightInput, 2); sizes.Children.Add(heightInput);
        var options = new Grid { ColumnSpacing = 30, Margin = new Thickness(0, 25, 0, 0) };
        options.ColumnDefinitions.Add(new ColumnDefinition()); options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.Children.Add(new StackPanel { Spacing = 5, Children = { qualityLabel, quality } }); Grid.SetColumn(format, 1); options.Children.Add(format);
        var summary = new Grid { ColumnSpacing = 15, RowSpacing = 7 };
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int column = 1; column < 4; column++) summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var summaryCells = new TextBlock[]
        {
            new() { Text = "当前:" }, currentDimensions,
            new() { Text = ViewerChrome.FormatSize(new FileInfo(path).Length), HorizontalAlignment = HorizontalAlignment.Right },
            new() { Text = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), HorizontalAlignment = HorizontalAlignment.Right },
            new() { Text = "新建:" }, proposedDimensions, proposedSize, proposedFormat,
        };
        for (int index = 0; index < summaryCells.Length; index++)
        {
            var cell = summaryCells[index];
            cell.FontSize = 14;
            Grid.SetRow(cell, index / 4); Grid.SetColumn(cell, index % 4);
            summary.Children.Add(cell);
        }
        var panel = new StackPanel
        {
            Padding = new Thickness(40, 0, 40, 36),
            Children = { units, sizes, options,
                new Border { Height = 1, Margin = new Thickness(0, 20, 0, 13), Background = ViewerChrome.Brush(_palette.Divider) }, summary },
        };
        var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancel = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Stretch };
        ViewerChrome.WirePressMotion(save); ViewerChrome.WirePressMotion(cancel);
        var footer = new Grid { Padding = new Thickness(20), ColumnSpacing = 10, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultSolidBrush"] };
        footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.Children.Add(save); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        var content = new StackPanel { Width = 420, Children =
        {
            new TextBlock { Text = "重设大小", FontSize = 20, Margin = new Thickness(40, 24, 0, 22) }, panel, footer,
        } };
        UpdateSummary();
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, Content = content,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        dialog.Resources["ContentDialogPadding"] = new Thickness(0);
        dialog.Resources["ContentDialogTitleMargin"] = new Thickness(0);
        dialog.Resources["ContentDialogMinWidth"] = 420.0;
        dialog.Resources["ContentDialogMaxWidth"] = 700.0;
        dialog.Resources["ContentDialogBackground"] = ViewerChrome.Brush(_palette.Background);
        dialog.Resources["ContentDialogTopOverlay"] = ViewerChrome.Transparent();
        dialog.Background = ViewerChrome.Brush(_palette.Background);
        dialog.BorderBrush = (Brush)Application.Current.Resources["SystemControlTransientBorderBrush"];
        bool resizeAccepted = false;
        save.Click += (_, _) => { resizeAccepted = true; dialog.Hide(); };
        cancel.Click += (_, _) => dialog.Hide();
        await DialogService.ShowAsync(dialog);
        resizeOpen = false;
        if (!resizeAccepted) return;
        var dimensions = Dimensions();
        int targetWidth = dimensions.Width, targetHeight = dimensions.Height;
        string extension = format.SelectedIndex switch { 0 => ".jpg", 1 => ".png", 2 => ".bmp", _ => ".tiff" };
        var target = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_" + targetWidth + "x" + targetHeight + extension);
        if (File.Exists(target)) { ShowStatus("已有同名文件，请先移动或重命名。"); return; }
        if (!await ImageEditService.SaveAsync(path, target, targetWidth, targetHeight, (int)quality.Value))
        {
            ShowStatus("调整失败，请检查文件权限。");
            return;
        }
        await OpenPathAsync(target);
    }

}
