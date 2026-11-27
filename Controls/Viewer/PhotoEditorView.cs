using System.IO;
using System.Text.Json;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Storage.Streams;

namespace Gallery.Controls.Viewer;

/// <summary>原版 Photos 网页编辑器宿主，仅负责图片缓冲、主题、文案及文件交接。</summary>
public sealed class PhotoEditorView : Grid, IDisposable
{
    private readonly WebView2 _browser = new();
    private readonly TaskCompletionSource _loaded = new();
    private CoreWebView2Environment? _environment;
    private CoreWebView2SharedBuffer? _input, _output;
    private byte[]? _pixels;
    private int _width, _height, _saveKind;
    private bool _disposed;
    public bool Saving { get; private set; }
    public Func<Image<Rgba32>, int, Task>? SaveRequested { get; set; }
    public event Action? CloseRequested;
    public event Action? EscapeRequested;
    public event Action<string>? Failed;

    public async Task OpenAsync(string path, ElementTheme theme)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var sourceStream = await file.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(sourceStream);
        var pixels = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Straight, new Windows.Graphics.Imaging.BitmapTransform(),
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);
        var decoded = (pixels.DetachPixelData(), (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        if (_disposed) return;
        (_pixels, _width, _height) = decoded;
        _environment = await CoreWebView2Environment.CreateWithOptionsAsync("", Path.Combine(AppPaths.LocalDataDir, "photo-editor-webview"), null);
        if (_disposed) return;
        Children.Add(_browser);
        await _browser.EnsureCoreWebView2Async(_environment);
        if (_disposed) return;
        var core = _browser.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(TextSelectionBehavior.WebScript);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationStarting += (_, args) => args.Cancel = !args.Uri.StartsWith("https://gallery-photo-editor.test/", StringComparison.Ordinal);
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, _) => { if (!_disposed) Failed?.Invoke("图片编辑器进程已停止，请重新打开编辑。"); };
        core.SetVirtualHostNameToFolderMapping("gallery-photo-editor.test", Path.Combine(AppContext.BaseDirectory, "assets", "PhotoEditor"), CoreWebView2HostResourceAccessKind.DenyCors);
        var palette = ViewerChrome.Palette.For(theme);
        var windowsTheme = new
        {
            isDarkMode = theme != ElementTheme.Light, isHighContrast = false,
            UIColors = new { Accent = new[] { 176, 191, 194 }, EditorBackground = new[] { (int)palette.Background.R, palette.Background.G, palette.Background.B } },
        };
        core.Navigate("https://gallery-photo-editor.test/index.html?themeJson=" + Uri.EscapeDataString(JsonSerializer.Serialize(windowsTheme))
            + "&fullImageSize=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { x = _width, y = _height }))
            + "&showControlPivot=true&enableSave=" + (new[] { ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(path).ToLowerInvariant()) ? "true" : "false") + "&isCancelVisible=true&fileFormatSupportsTransparency=" + (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "true" : "false"));
        await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task<bool> HasEditsAsync()
    {
        if (_disposed || !_loaded.Task.IsCompletedSuccessfully) return false;
        return await _browser.CoreWebView2.ExecuteScriptAsync("window.imageHasEdits?.()") == "\"true\"";
    }

    private async void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_disposed) return;
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var root = message.RootElement;
            var values = root.GetProperty("args");
            switch (root.GetProperty("name").GetString())
            {
                case "notifyOnReady":
                    var localization = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "assets", "PhotoEditor", "localization.json"));
                    if (_disposed) return;
                    await sender.ExecuteScriptAsync("setLocalizationJson(" + JsonSerializer.Serialize(localization) + ")");
                    _input = _environment!.CreateSharedBuffer((ulong)_pixels!.Length);
                    using (var stream = _input.OpenStream())
                    using (var writer = new DataWriter(stream))
                    {
                        writer.WriteBytes(_pixels);
                        await writer.StoreAsync();
                        writer.DetachStream();
                    }
                    _pixels = null;
                    if (_disposed) return;
                    sender.PostSharedBufferToScript(_input, CoreWebView2SharedBufferAccess.ReadOnly,
                        JsonSerializer.Serialize(new { type = "fullImage", payload = new { width = _width, height = _height, orientation = 1, format = 1 } }));
                    break;
                case "notifyOnFullImageBytesLoaded":
                    _loaded.TrySetResult();
                    break;
                case "notifyOnUserSave":
                    if (Saving) return;
                    Saving = true;
                    _saveKind = values[0].GetInt32();
                    _output = _environment!.CreateSharedBuffer(values[1].GetUInt64());
                    sender.PostSharedBufferToScript(_output, CoreWebView2SharedBufferAccess.ReadWrite,
                        JsonSerializer.Serialize(new { type = "editedImage", enableTransparency = values[2].GetBoolean() }));
                    break;
                case "notifyOnEditedImageReady":
                    int width = values[0].GetInt32(), height = values[1].GetInt32();
                    var bytes = new byte[checked(width * height * 4)];
                    using (var stream = _output!.OpenStream())
                    using (var reader = new DataReader(stream))
                    {
                        await reader.LoadAsync((uint)bytes.Length);
                        reader.ReadBytes(bytes);
                    }
                    _output.Dispose(); _output = null;
                    using (var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(bytes, width, height))
                        if (SaveRequested is not null) await SaveRequested(image, _saveKind);
                    Saving = false;
                    break;
                case "notifyOnUserClose": CloseRequested?.Invoke(); break;
                case "notifyOnEscape": EscapeRequested?.Invoke(); break;
                case "notifyOnError":
                    Saving = false;
                    FaultLog.Write("photo-editor-page");
                    _loaded.TrySetException(new InvalidOperationException(values[0].GetString()));
                    Failed?.Invoke("图片编辑失败，请重新打开编辑。");
                    break;
            }
        }
        catch (Exception error)
        {
            if (_disposed) return;
            Saving = false;
            FaultLog.Write("photo-editor-host", error);
            _loaded.TrySetException(error);
            Failed?.Invoke("图片编辑失败，请重新打开编辑。");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loaded.TrySetCanceled();
        _browser.Close();
        Children.Clear();
        _input?.Dispose(); _output?.Dispose();
        _pixels = null;
    }
}
