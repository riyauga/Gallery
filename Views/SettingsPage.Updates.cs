using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gallery.Views;

public sealed partial class SettingsPage
{
    private readonly UpdateService _updates = new();
    private AvailableUpdate? _availableUpdate;
    private PreparedUpdate? _preparedUpdate;
    private CancellationTokenSource? _updateDownload;
    private bool _updateChecking, _startupUpdateChecked;
    private Func<XamlRoot>? _updateRoot;
    private Func<PreparedUpdate, Task<bool>>? _installUpdate;
    private Action<string>? _updateToast;

    private void InitializeUpdates()
    {
        AppVersionText.Text = "当前版本 " + UpdateService.CurrentVersion;
        AutoUpdateToggle.IsOn = AppService.Settings.Settings.check_updates_on_start;
        try
        {
            var root = UpdateProtocol.UpdatesRoot(AppPaths.LocalDataDir);
            if (Directory.Exists(root))
                foreach (var directory in Directory.GetDirectories(root).OrderBy(Directory.GetLastWriteTimeUtc))
                    if (File.Exists(Path.Combine(directory, "failed")))
                        UpdateStatusText.Text = File.ReadAllText(Path.Combine(directory, "failed"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FaultLog.Write("update-history", ex); }

    }
    public void SetUpdateHost(Func<XamlRoot> root, Func<PreparedUpdate, Task<bool>> install, Action<string> toast)
    { _updateRoot = root; _installUpdate = install; _updateToast = toast; }

    public async Task CheckStartupUpdateAsync()
    {
        if (_startupUpdateChecked) return;
        _startupUpdateChecked = true;
        if (AppService.Settings.Settings.check_updates_on_start) await CheckUpdatesAsync(false);
    }
    private void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (AutoUpdateToggle is null) return;
        AppService.Settings.Settings.check_updates_on_start = AutoUpdateToggle.IsOn;
        AppService.Settings.Save();
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_updateChecking || _updateDownload is not null || _preparedUpdate is not null) return;
        _updateChecking = true;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            _availableUpdate = await _updates.CheckAsync();
            if (AppService.IsShuttingDown) return;
            UpdateDetailsButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateStatusText.Text = _availableUpdate is null ? "当前已是最新版。" : "发现新版本 " + _availableUpdate.Version;
            if (_availableUpdate is { } update)
            {
                if (manual) await ShowUpdateDetailsAsync();
                else if (AppService.Settings.Settings.skipped_update_version != update.Version)
                    _updateToast?.Invoke("发现新版本 " + update.Version + "，可在设置 → 关于中查看变化并更新。");
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException or KeyNotFoundException or FormatException)
        {
            FaultLog.Write("update-check", ex);
            UpdateStatusText.Text = "暂时无法检查更新，请检查网络连接后重试。";
            if (manual) _updateToast?.Invoke(UpdateStatusText.Text);
        }
        finally { _updateChecking = false; CheckUpdateButton.IsEnabled = true; }
    }
    private async void UpdateDetails_Click(object sender, RoutedEventArgs e) => await ShowUpdateDetailsAsync();
    private async Task ShowUpdateDetailsAsync()
    {
        if (_availableUpdate is not { } update || _updateRoot is null || AppService.IsShuttingDown) return;
        var notes = new TextBlock { Text = update.Notes, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 620 };
        var result = await DialogService.ShowAsync(new ContentDialog
        {
            XamlRoot = _updateRoot(), Title = "Gallery " + update.Version,
            Content = new ScrollViewer { Content = notes, MaxHeight = 440 },
            PrimaryButtonText = "下载并更新", SecondaryButtonText = "跳过此版本", CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Close,
        });
        if (result == ContentDialogResult.Secondary)
        {
            AppService.Settings.Settings.skipped_update_version = update.Version;
            AppService.Settings.Save();
            UpdateStatusText.Text = "已跳过此版本。你仍可手动检查并安装。";
        }
        if (result == ContentDialogResult.Primary) await DownloadUpdateAsync(update);
    }
    private async Task DownloadUpdateAsync(AvailableUpdate update)
    {
        if (_updateDownload is not null || _preparedUpdate is not null) return;
        using var cts = new CancellationTokenSource();
        _updateDownload = cts;
        CheckUpdateButton.IsEnabled = false;
        UpdateDetailsButton.IsEnabled = false;
        CancelUpdateButton.Visibility = UpdateDownloadProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "正在下载更新，你可以继续使用程序。";
        try
        {
            _preparedUpdate = await _updates.DownloadAsync(update, new Progress<double>(p => UpdateDownloadProgress.Value = p * 100), cts.Token);
            UpdateStatusText.Text = "更新已下载。安装前会完成保存，随后自动重启。";
            InstallUpdateButton.Visibility = Visibility.Visible;
            _updateToast?.Invoke("更新已下载，可在设置 → 关于中安装并重启。");
        }
        catch (OperationCanceledException) { UpdateStatusText.Text = "已取消下载。"; }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        { FaultLog.Write("update-download", ex); UpdateStatusText.Text = "更新下载或校验未完成，请重试。原程序未修改。"; }
        finally
        {
            _updateDownload = null;
            CheckUpdateButton.IsEnabled = UpdateDetailsButton.IsEnabled = true;
            CancelUpdateButton.Visibility = UpdateDownloadProgress.Visibility = Visibility.Collapsed;
        }
    }
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => _updateDownload?.Cancel();
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_preparedUpdate is not { } prepared || _installUpdate is null || _updateRoot is null) return;
        var choice = await DialogService.ShowAsync(new ContentDialog
        {
            XamlRoot = _updateRoot(), Title = "安装更新并重启？",
            Content = "请先完成尚未确认的输入。程序会等待当前任务结束并保存已确认的修改，然后安装更新并自动重启。",
            PrimaryButtonText = "安装并重启", CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Close,
        });
        if (choice != ContentDialogResult.Primary) return;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在完成任务和保存，请稍候…";
        if (!await _installUpdate(prepared))
        { InstallUpdateButton.IsEnabled = true; UpdateStatusText.Text = "暂时无法安装更新。请完成当前操作后重试。"; }
    }
    public void CancelUpdateDownload() => _updateDownload?.Cancel();
}
