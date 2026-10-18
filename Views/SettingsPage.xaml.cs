using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Gallery.Views;

/// <summary>
/// 设置页。照搬 photos 的 SettingsViewModel.SwitchTheme：
/// 切换后由 ThemeService.SetTheme(ElementTheme) 应用，选中项与下拉文字同步更新。
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private ElementTheme _theme = ElementTheme.Dark;

    public SettingsPage()
    {
        InitializeComponent();
        ApplyState();
        Loaded += (_, _) => RefreshBridge();
        Unloaded += (_, _) => SaveBlurStrength();
    }

    /// <summary>刷新桥接状态：地址用实际监听端口，令牌取已落盘的那个。</summary>
    private void RefreshBridge()
    {
        var bridge = AppService.Bridge;
        if (bridge is null || !bridge.Running)
        {
            BridgeStatusText.Text = AppService.BridgeStatus;
            BridgeTokenBox.Text = "";
        }
        else
        {
            var state = bridge.BrowserConnected ? "浏览器已连接" : "等待浏览器连接";
            BridgeStatusText.Text = $"{state} · http://127.0.0.1:{bridge.Port}/";
            BridgeTokenBox.Text = bridge.Token;
        }

        PendingText.Text = AppService.Store is null
            ? "—"
            : AppService.Store.PendingIngestCount() switch
            {
                0 => "0 条",
                var n => $"{n} 条"
            };
    }

    private void BridgeCopy_Click(object sender, RoutedEventArgs e)
    {
        if (BridgeTokenBox.Text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(BridgeTokenBox.Text);
        Clipboard.SetContent(package);
    }

    private void BridgeRefresh_Click(object sender, RoutedEventArgs e) => RefreshBridge();

    private void ThemeLightItem_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Light);

    private void ThemeDarkItem_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Dark);

    private void ThemeDefaultItem_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Default);

    private void SetTheme(ElementTheme theme)
    {
        _theme = theme;
        ThemeService.SetTheme(theme);
        ApplyState();
    }

    /// <summary>下拉文字 = GetThemeName(theme)；勾选项 = 当前主题（对应 IsChecked 绑定）。</summary>
    private void ApplyState()
    {
        ThemeDropdown.Content = ThemeService.GetThemeName(_theme);
        ThemeLightItem.IsChecked = _theme == ElementTheme.Light;
        ThemeDarkItem.IsChecked = _theme == ElementTheme.Dark;
        ThemeDefaultItem.IsChecked = _theme == ElementTheme.Default;
        ApplyNsfwState();
        ApplyKindWhitelistState();
        ApplySyncErrorState();
    }

    // ---------------------------------------------------------------- 敏感内容

    private bool _nsfwSyncing = true;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _strengthTimer;
    private bool _strengthPending;

    private void ApplyNsfwState()
    {
        _nsfwSyncing = true;
        var mode = Services.AppService.Settings.Settings.nsfw_display;
        NsfwNormalRadio.IsChecked = mode == "normal";
        NsfwBlurRadio.IsChecked = mode == "blur";
        NsfwHideRadio.IsChecked = mode == "hide";
        NsfwStrengthSlider.Value = AppService.Settings.Settings.nsfw_blur_strength;
        NsfwStrengthValue.Text = AppService.Settings.Settings.nsfw_blur_strength.ToString();
        NsfwStrengthCard.Visibility = mode == "blur" ? Visibility.Visible : Visibility.Collapsed;
        _nsfwSyncing = false;

        var store = Services.AppService.Store;
        if (store is null)
        {
            NsfwSummaryText.Text = "图库还没就绪";
            return;
        }

        var (scored, nsfw, unknown) = store.NsfwSummary();
        NsfwSummaryText.Text = $"已打分 {scored} 张 · 判定敏感 {nsfw} 张 · 未判定 {unknown} 张";

        _sysSyncing = true;
        TrayToggle.IsOn = Services.AppService.Settings.Settings.minimize_to_tray;
        AutoStartToggle.IsOn = Services.AutoStart.IsEnabled();
        _sysSyncing = false;

        var dir = Services.AppService.Settings.Settings.nsfw_model_dir;
        NsfwModelText.Text = string.IsNullOrWhiteSpace(dir)
            ? "未配置"
            : Services.NsfwScorer.LooksConfigured(dir)
                ? dir
                : dir + "（缺少 model.onnx）";
    }

    private async void NsfwPickFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (MainWindow.Instance is { } window &&
            WinRT.Interop.WindowNative.GetWindowHandle(window) is var hwnd && hwnd != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        Services.AppService.Settings.Settings.nsfw_model_dir = folder.Path;
        Services.AppService.Settings.Save();
        ApplyNsfwState();
    }

    private void ApplySyncErrorState()
    {
        var store = Services.AppService.Store;
        SyncErrorText.Text = store is null ? "图库还没就绪" : $"{store.SyncErrorCount()} 条";
    }

    private async void SyncErrorView_Click(object sender, RoutedEventArgs e)
    {
        var store = Services.AppService.Store;
        if (store is null) return;
        var rows = store.SyncErrors();
        var body = new StackPanel { Spacing = 10 };
        if (rows.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "没有记录。", Opacity = 0.7 });
        }
        else
        {
            foreach (var row in rows)
            {
                var item = new StackPanel { Spacing = 2 };
                item.Children.Add(new TextBlock
                {
                    Text = DateTimeOffset.FromUnixTimeSeconds((long)row.UpdatedAt).ToLocalTime()
                        .ToString("MM-dd HH:mm"),
                    Opacity = 0.6,
                    FontSize = 12,
                });
                item.Children.Add(new TextBlock { Text = row.Message, TextWrapping = TextWrapping.Wrap });
                item.Children.Add(new TextBlock
                {
                    Text = row.Path,
                    Opacity = 0.5,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
                body.Children.Add(item);
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "最近错误",
            Content = new ScrollViewer { Content = body, MaxHeight = 420, MinWidth = 520 },
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }

    private void SyncErrorClear_Click(object sender, RoutedEventArgs e)
    {
        Services.AppService.Store?.ClearSyncErrors();
        ApplySyncErrorState();
    }

    private void ApplyKindWhitelistState()
    {
        var path = string.IsNullOrWhiteSpace(Services.AppService.Settings.Settings.kind_whitelist_path)
            ? Services.AppPaths.KindWhitelistPath
            : Services.AppService.Settings.Settings.kind_whitelist_path;
        var map = Services.Taxonomy.LoadKindWhitelist(path);
        var tags = map.Values.Sum(v => v.Length);
        KindWhitelistText.Text = $"{map.Count} 组 · {tags} 条 · {(File.Exists(path) ? "自定义" : "默认")}";
    }

    private void KindWhitelistOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(Services.AppService.Settings.Settings.kind_whitelist_path)
                ? Services.AppPaths.KindWhitelistPath
                : Services.AppService.Settings.Settings.kind_whitelist_path;
            if (!File.Exists(path))
            {
                Services.Taxonomy.SaveKindWhitelist(
                    path, Services.Taxonomy.LoadKindWhitelist(path));
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            KindWhitelistText.Text = "已用系统默认程序打开，改完回到这里点「恢复默认」上方的刷新即可生效";
        }
        catch (Exception ex)
        {
            KindWhitelistText.Text = "打开失败：" + ex.Message;
        }
    }

    private void KindWhitelistReset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(Services.AppService.Settings.Settings.kind_whitelist_path)
                ? Services.AppPaths.KindWhitelistPath
                : Services.AppService.Settings.Settings.kind_whitelist_path;
            Services.Taxonomy.SaveKindWhitelist(path, Services.Taxonomy.DefaultKindWhitelist);
            ApplyKindWhitelistState();
        }
        catch (Exception ex)
        {
            KindWhitelistText.Text = "写文件失败：" + ex.Message;
        }
    }

    private bool _nsfwScanning;
    private bool _sysSyncing;

    private void TrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        Services.AppService.Settings.Settings.minimize_to_tray = TrayToggle.IsOn;
        Services.AppService.Settings.Save();
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        Services.AppService.Settings.Settings.start_at_login = AutoStartToggle.IsOn;
        Services.AppService.Settings.Save();
        try
        {
            Services.AutoStart.Apply(AutoStartToggle.IsOn);
        }
        catch (Exception ex)
        {
            NsfwScanText.Text = "写注册表失败：" + ex.Message;
        }
    }

    private async void NsfwPreview_Click(object sender, RoutedEventArgs e)
    {
        if (Services.AppService.Store is null) return;
        if (!Services.AppService.FakeNsfwScorer &&
            !Services.NsfwScorer.LooksConfigured(Services.AppService.Settings.Settings.nsfw_model_dir))
        {
            NsfwScanText.Text = "还没选模型文件夹";
            return;
        }

        NsfwPreviewButton.IsEnabled = false;
        _nsfwPreviewCts = new();
        _previewCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var service = Services.AppService.CreateNsfwService();
            var applied = await Controls.Nsfw.NsfwPreviewDialog.ShowAsync(XamlRoot, service, _nsfwPreviewCts.Token);
            NsfwScanText.Text = applied ? "已应用预览结果" : "预览已取消，没有写入";
            if (applied && Services.AppService.Store is { } storeAfter)
            {
                var (scored, nsfw, unknown) = storeAfter.NsfwSummary();
                NsfwSummaryText.Text = $"已打分 {scored} 张 · 判定敏感 {nsfw} 张 · 未判定 {unknown} 张";
            }
        }
        finally
        {
            _nsfwPreviewCts.Dispose();
            _nsfwPreviewCts = null;
            _previewCompleted.TrySetResult();
            NsfwPreviewButton.IsEnabled = true;
        }
    }

    private System.Threading.CancellationTokenSource? _nsfwScanCts;
    private System.Threading.CancellationTokenSource? _nsfwPreviewCts;
    private TaskCompletionSource? _scanCompleted;
    private TaskCompletionSource? _previewCompleted;

    private async void NsfwScan_Click(object sender, RoutedEventArgs e)
    {
        if (_nsfwScanning || Services.AppService.Store is null) return;
        _nsfwScanning = true;
        _scanCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _nsfwScanCts = new System.Threading.CancellationTokenSource();
        NsfwScanButton.IsEnabled = false;
        NsfwCancelButton.Visibility = Visibility.Visible;
        NsfwScanText.Text = "准备中…";

        try
        {
            var service = new Services.NsfwService(Services.AppService.Store);
            var progress = new Progress<(int Done, int Total)>(p =>
                NsfwScanText.Text = $"扫描中 {p.Done}/{p.Total}（可以点取消，已判定的会保留）");
            var result = await service.RunAsync(progress, token: _nsfwScanCts.Token);
            NsfwScanText.Text = Services.NsfwService.Describe(result);
            ApplyNsfwState();
            Services.AppService.NotifyDisplayModeChanged();
        }
        catch (OperationCanceledException)
        {
            NsfwScanText.Text = "已取消；已经判定的图保持新结果";
            ApplyNsfwState();
            Services.AppService.NotifyDisplayModeChanged();
        }
        catch (Exception ex)
        {
            NsfwScanText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _nsfwScanCts?.Dispose();
            _nsfwScanCts = null;
            NsfwCancelButton.Visibility = Visibility.Collapsed;
            NsfwScanButton.IsEnabled = true;
            _nsfwScanning = false;
            _scanCompleted.TrySetResult();
        }
    }

    private void NsfwCancel_Click(object sender, RoutedEventArgs e)
    {
        NsfwScanText.Text = "正在取消…";
        NsfwCancelButton.IsEnabled = false;
        _nsfwScanCts?.Cancel();
        NsfwCancelButton.IsEnabled = true;
    }

    private void NsfwDisplay_Checked(object sender, RoutedEventArgs e)
    {
        if (_nsfwSyncing) return;
        var tag = (sender as RadioButton)?.Tag as string ?? "normal";
        Services.AppService.Settings.Settings.nsfw_display = tag;
        NsfwStrengthCard.Visibility = tag == "blur" ? Visibility.Visible : Visibility.Collapsed;
        Services.AppService.Settings.Save();
        // 显示策略和强度共同决定缩略图，统一通知图库重建。
        Services.AppService.NotifyDisplayModeChanged();
    }

    private void NsfwStrength_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_nsfwSyncing) return;
        var strength = (int)Math.Round(e.NewValue);
        NsfwStrengthValue.Text = strength.ToString();
        AppService.Settings.Settings.nsfw_blur_strength = strength;
        AppService.NotifyDisplayModeChanged();
        _strengthPending = true;
        // Gallery_Old/settings_dialog.py: strength_changed / save_strength，300ms 单次延迟落盘。
        if (_strengthTimer is null)
        {
            _strengthTimer = DispatcherQueue.CreateTimer();
            _strengthTimer.Interval = TimeSpan.FromMilliseconds(300);
            _strengthTimer.IsRepeating = false;
            _strengthTimer.Tick += (_, _) => SaveBlurStrength();
        }
        _strengthTimer.Stop();
        _strengthTimer.Start();
    }

    private void SaveBlurStrength()
    {
        _strengthTimer?.Stop();
        if (!_strengthPending) return;
        AppService.Settings.Save();
        _strengthPending = false;
    }

    public async Task StopBackgroundWorkAsync()
    {
        SaveBlurStrength();
        _nsfwScanCts?.Cancel();
        _nsfwPreviewCts?.Cancel();
        await Task.WhenAll(_scanCompleted?.Task ?? Task.CompletedTask,
            _previewCompleted?.Task ?? Task.CompletedTask);
    }
}
