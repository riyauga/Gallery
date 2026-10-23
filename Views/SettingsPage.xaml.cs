using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnBackgroundPointerPressed), true);
        _theme = ThemeService.Parse(AppService.Settings.Settings.theme);
        ApplyState();
        Loaded += (_, _) =>
        {
            RefreshBridge();
            RefreshThumbCacheAsync();
        };
        Unloaded += (_, _) => SaveBlurStrength();
    }

    private void OnBackgroundPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox) return;
        for (var element = args.OriginalSource as DependencyObject;
             element is not null && !ReferenceEquals(element, this);
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or AutoSuggestBox or ButtonBase or RangeBase or ToggleSwitch) return;
        }
        SettingsRoot.Focus(FocusState.Pointer);
    }

    /// <summary>刷新桥接状态：地址用实际监听端口，令牌取已落盘的那个。</summary>
    private void RefreshBridge()
    {
        var settings = AppService.Settings.Settings;
        var bridge = AppService.Bridge;
        BridgeEnabledToggle.IsOn = settings.bridge_enabled;
        BridgePortBox.Text = settings.bridge_port.ToString();
        if (!settings.bridge_enabled)
        {
            BridgeStatusText.Text = "已关闭";
            BridgeTokenBox.Text = "";
        }
        else if (bridge is null || !bridge.Running)
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

    private void BridgeEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var on = BridgeEnabledToggle.IsOn;
        if (on == AppService.Settings.Settings.bridge_enabled) return;
        AppService.Settings.Settings.bridge_enabled = on;
        AppService.Settings.Save();
        AppService.RestartBridge();
        RefreshBridge();
    }

    private void BridgePortApply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BridgePortBox.Text.Trim(), out var port) || port < 1024 || port > 65535)
        {
            BridgePortStatusText.Text = "端口要在 1024 到 65535 之间。";
            return;
        }

        AppService.Settings.Settings.bridge_port = port;
        AppService.Settings.Save();
        AppService.RestartBridge();
        RefreshBridge();
        BridgePortStatusText.Text = "已应用";
    }

    // ---------------------------------------------------------------- 缩略图缓存

    private static ThumbnailService? _thumbCache;

    /// <summary>图库没连接时也要能清，所以不依赖 Scanner。</summary>
    private static ThumbnailService ThumbCache => AppService.Scanner?.Thumbs
        ?? (_thumbCache ??= new ThumbnailService(AppPaths.ThumbCacheDir, AppService.Settings.Settings.thumb_quality));

    private async void RefreshThumbCacheAsync()
    {
        try
        {
            var cache = ThumbCache;
            var (files, bytes) = await Task.Run(cache.Measure);
            ThumbCacheText.Text = files == 0 ? "空的" : $"{files} 个 · {DescribeBytes(bytes)}";
            ThumbCacheClearButton.IsEnabled = files > 0;
        }
        catch (Exception ex)
        {
            ThumbCacheText.Text = "读取失败：" + ex.Message;
        }
    }

    private async void ThumbCacheClear_Click(object sender, RoutedEventArgs e)
    {
        var cache = ThumbCache;
        ThumbCacheClearButton.IsEnabled = false;
        try
        {
            var (files, bytes) = await Task.Run(cache.Measure);
            if (files == 0)
            {
                ThumbCacheText.Text = "空的";
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "清理缩略图缓存",
                Content = $"删除 {files} 个文件（{DescribeBytes(bytes)}）？" +
                          "图片、标签和分级判定都不受影响，下次浏览到时会重新生成。",
                PrimaryButtonText = "清理",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await Gallery.Services.DialogService.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                ThumbCacheClearButton.IsEnabled = true;
                return;
            }

            var removed = await Task.Run(cache.Clear);
            var (left, leftBytes) = await Task.Run(cache.Measure);
            ThumbCacheText.Text = $"已清理 {removed} 个（{DescribeBytes(bytes - leftBytes)}）· " +
                                  $"现在 {left} 个 / {DescribeBytes(leftBytes)}";
            ThumbCacheClearButton.IsEnabled = left > 0;
        }
        catch (Exception ex)
        {
            ThumbCacheText.Text = "清理失败：" + ex.Message;
            ThumbCacheClearButton.IsEnabled = true;
        }
    }

    private static string DescribeBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private void ThemeLightItem_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Light);

    private void ThemeDarkItem_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Dark);

    private void ThemeDefaultItem_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Default);

    private void SetTheme(ElementTheme theme)
    {
        _theme = theme;
        ThemeService.SetTheme(theme);
        AppService.Settings.Settings.theme = ThemeService.Key(theme);
        AppService.Settings.Save();
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
        ApplyOpenModeState();
        ApplyKindWhitelistState();
        ApplySyncErrorState();
        ApplyTagDisplayState();
    }

    // ---------------------------------------------------------------- 敏感内容

    private bool _nsfwSyncing = true;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _strengthTimer;
    private bool _strengthPending;

    private void ApplyNsfwState()
    {
        _nsfwSyncing = true;
        _sysSyncing = true;
        var settings = Services.AppService.Settings.Settings;
        var mode = settings.nsfw_display;
        NsfwNormalRadio.IsChecked = mode == "normal";
        NsfwBlurRadio.IsChecked = mode == "blur";
        NsfwHideRadio.IsChecked = mode == "hide";
        NsfwStrengthSlider.Value = settings.nsfw_blur_strength;
        NsfwStrengthValue.Text = settings.nsfw_blur_strength.ToString();
        NsfwStrengthCard.Visibility = mode == "blur" ? Visibility.Visible : Visibility.Collapsed;

        TrayToggle.IsOn = settings.minimize_to_tray;
        AutoStartToggle.IsOn = Services.AutoStart.IsEnabled();
        ArchiveAutoByTagsToggle.IsOn = settings.archive_auto_by_tags;
        NotifyNewImagesToggle.IsOn = settings.notify_new_images;
        AutoTagToggle.IsOn = settings.auto_tag_from_folder;
        AutoTagDepthBox.IsEnabled = settings.auto_tag_from_folder;
        AutoModelTagsToggle.IsOn = settings.auto_model_tags;
        ImportTagCharacterCheck.IsChecked = settings.import_tag_character;
        ImportTagWorkCheck.IsChecked = settings.import_tag_work;
        ImportTagKindCheck.IsChecked = settings.import_tag_kind;
        ImportTagRatingCheck.IsChecked = settings.import_tag_rating;
        ImportTagCharacterCheck.IsEnabled = settings.auto_model_tags;
        ImportTagWorkCheck.IsEnabled = settings.auto_model_tags;
        ImportTagKindCheck.IsEnabled = settings.auto_model_tags;
        ImportTagRatingCheck.IsEnabled = settings.auto_model_tags;
        RescanTagCharacterCheck.IsChecked = settings.rescan_tag_character;
        RescanTagWorkCheck.IsChecked = settings.rescan_tag_work;
        RescanTagKindCheck.IsChecked = settings.rescan_tag_kind;
        RescanTagRatingCheck.IsChecked = settings.auto_nsfw_tag;
        SelectRescanWriteMode(settings.rescan_overwrite_tags);
        SelectAutoTagDepth(settings.auto_tag_depth);
        SelectHistoryKeep(settings.history_keep_days);
        SelectNsfwDevice(settings.nsfw_device);
        _sysSyncing = false;
        _nsfwSyncing = false;

        var dir = settings.nsfw_model_dir;
        NsfwModelText.Text = string.IsNullOrWhiteSpace(dir)
            ? "未配置"
            : Services.PixaiScorer.LooksConfigured(dir)
                ? dir
                : dir + "（需要 PixAI model.onnx 和 config.json）";

        var store = Services.AppService.Store;
        if (store is null)
        {
            NsfwSummaryText.Text = "图库还没就绪";
            return;
        }

        var (scored, nsfw, unknown) = store.NsfwSummary();
        NsfwSummaryText.Text = $"已识别 {scored} 张 · 敏感 {nsfw} 张 · 未识别 {unknown} 张";
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
            // 浮层滚动条会压在正文上，正文右侧自己留 16（UI_CONVENTIONS.md R1）。
            Content = new ScrollViewer { Content = body, MaxHeight = 420, MinWidth = 520, Padding = new Thickness(0, 0, 16, 0) },
            CloseButtonText = "关闭",
        };
        await Gallery.Services.DialogService.ShowAsync(dialog);
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
            KindWhitelistText.Text = "已打开，改完点「重新加载」。";
        }
        catch (Exception ex)
        {
            KindWhitelistText.Text = "打开失败：" + ex.Message;
        }
    }

    private void KindWhitelistReload_Click(object sender, RoutedEventArgs e) => ApplyKindWhitelistState();

    private void KindWhitelistReset_Click(object sender, RoutedEventArgs e)    {
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

    private void ArchiveAutoByTagsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        Services.AppService.Settings.Settings.archive_auto_by_tags = ArchiveAutoByTagsToggle.IsOn;
        Services.AppService.Settings.Save();
    }

    private void NotifyNewImagesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        Services.AppService.Settings.Settings.notify_new_images = NotifyNewImagesToggle.IsOn;
        Services.AppService.Settings.Save();
    }

    private void AutoTagToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        Services.AppService.Settings.Settings.auto_tag_from_folder = AutoTagToggle.IsOn;
        Services.AppService.Settings.Save();
        AutoTagDepthBox.IsEnabled = AutoTagToggle.IsOn;
    }

    private void AutoTagDepth_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sysSyncing) return;
        if (AutoTagDepthBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var depth))
        {
            Services.AppService.Settings.Settings.auto_tag_depth = depth;
            Services.AppService.Settings.Save();
        }
    }

    private void HistoryKeep_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sysSyncing) return;
        if (HistoryKeepBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var days))
        {
            Services.AppService.Settings.Settings.history_keep_days = days;
            Services.AppService.Settings.Save();
        }
    }

    private void SelectHistoryKeep(int days)
    {
        foreach (var item in HistoryKeepBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && int.TryParse(tag, out var value) && value == days)
            {
                HistoryKeepBox.SelectedItem = item;
                return;
            }
        }
        HistoryKeepBox.SelectedIndex = 0;
    }

    private void SelectAutoTagDepth(int depth)
    {
        foreach (var item in AutoTagDepthBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && int.TryParse(tag, out var value) && value == depth)
            {
                AutoTagDepthBox.SelectedItem = item;
                return;
            }
        }
        AutoTagDepthBox.SelectedIndex = 0;
    }

    private void NsfwDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_nsfwSyncing) return;
        if (NsfwDeviceBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Services.AppService.Settings.Settings.nsfw_device = Services.PixaiScorer.NormalizeDevice(tag);
        Services.AppService.Settings.Save();
    }

    private void SelectNsfwDevice(string device)
    {
        var mode = Services.PixaiScorer.NormalizeDevice(device);
        foreach (var item in NsfwDeviceBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == mode)
            {
                NsfwDeviceBox.SelectedItem = item;
                return;
            }
        }
        NsfwDeviceBox.SelectedIndex = 0;
    }

    private bool _tagDisplaySyncing;

    /// <summary>角色与作品的标签贴纸显示存库（meta）；搜索候选按输入语言显示。</summary>
    private void ApplyTagDisplayState()
    {
        _tagDisplaySyncing = true;
        try
        {
            var displayMode = Services.AppService.Store?.GlobalTagDisplayMode()
                              ?? Services.TagDisplayState.DefaultGlobalMode;
            SelectTagMode(TagDisplayModeBox, displayMode);
        }
        finally
        {
            _tagDisplaySyncing = false;
        }
    }

    private static void SelectTagMode(Gallery.Controls.SelectionDropDown box, string mode)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == mode)
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private void TagDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_tagDisplaySyncing) return;
        if (TagDisplayModeBox.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        Services.AppService.Store?.SetGlobalTagDisplayMode(mode);
    }

    private void AutoModelTagsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        var settings = Services.AppService.Settings.Settings;
        settings.auto_model_tags = AutoModelTagsToggle.IsOn;
        ImportTagCharacterCheck.IsEnabled = AutoModelTagsToggle.IsOn;
        ImportTagWorkCheck.IsEnabled = AutoModelTagsToggle.IsOn;
        ImportTagKindCheck.IsEnabled = AutoModelTagsToggle.IsOn;
        ImportTagRatingCheck.IsEnabled = AutoModelTagsToggle.IsOn;
        Services.AppService.Settings.Save();
    }

    private void ImportTag_Changed(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        var settings = Services.AppService.Settings.Settings;
        settings.import_tag_character = ImportTagCharacterCheck.IsChecked == true;
        settings.import_tag_work = ImportTagWorkCheck.IsChecked == true;
        settings.import_tag_kind = ImportTagKindCheck.IsChecked == true;
        settings.import_tag_rating = ImportTagRatingCheck.IsChecked == true;
        Services.AppService.Settings.Save();
    }

    private void RescanTag_Changed(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        var settings = Services.AppService.Settings.Settings;
        settings.rescan_tag_character = RescanTagCharacterCheck.IsChecked == true;
        settings.rescan_tag_work = RescanTagWorkCheck.IsChecked == true;
        settings.rescan_tag_kind = RescanTagKindCheck.IsChecked == true;
        settings.auto_nsfw_tag = RescanTagRatingCheck.IsChecked == true;
        Services.AppService.Settings.Save();
    }

    private void RescanWriteMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sysSyncing) return;
        if (RescanWriteModeBox.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        Services.AppService.Settings.Settings.rescan_overwrite_tags = mode == "overwrite";
        Services.AppService.Settings.Save();
    }

    private void SelectRescanWriteMode(bool overwrite)
    {
        var wanted = overwrite ? "overwrite" : "append";
        foreach (var item in RescanWriteModeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == wanted)
            {
                RescanWriteModeBox.SelectedItem = item;
                return;
            }
        }
        RescanWriteModeBox.SelectedIndex = 0;
    }

    private bool _openModeSyncing;

    private void ApplyOpenModeState()
    {
        var settings = Services.AppService.Settings.Settings;
        _openModeSyncing = true;
        foreach (var item in OpenModeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == settings.open_mode)
            {
                OpenModeBox.SelectedItem = item;
                break;
            }
        }
        if (OpenModeBox.SelectedIndex < 0) OpenModeBox.SelectedIndex = 1;
        _openModeSyncing = false;
        UpdateOpenProgramState();
    }

    private void UpdateOpenProgramState()
    {
        var settings = Services.AppService.Settings.Settings;
        var custom = settings.open_mode == "custom";
        OpenProgramButton.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        OpenProgramText.Text = custom
            ? (settings.open_custom_exe.Length > 0 ? Path.GetFileName(settings.open_custom_exe) : "未选择")
            : "";
    }

    private void OpenMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_openModeSyncing) return;
        if (OpenModeBox.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        Services.AppService.Settings.Settings.open_mode = mode;
        Services.AppService.Settings.Save();
        UpdateOpenProgramState();
    }

    private async void OpenProgram_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        if (MainWindow.Instance is { } window &&
            WinRT.Interop.WindowNative.GetWindowHandle(window) is var hwnd && hwnd != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        Services.AppService.Settings.Settings.open_custom_exe = file.Path;
        Services.AppService.Settings.Save();
        UpdateOpenProgramState();
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        var on = AutoStartToggle.IsOn;
        try
        {
            Services.AutoStart.Apply(on);
            if (Services.AutoStart.IsEnabled() != on) throw new InvalidOperationException("注册表未变更");
            Services.AppService.Settings.Settings.start_at_login = on;
            Services.AppService.Settings.Save();
            AutoStartStatusText.Text = "";
            ToolTipService.SetToolTip(AutoStartStatusText, null);
        }
        catch (Exception ex)
        {
            _sysSyncing = true;
            AutoStartToggle.IsOn = Services.AutoStart.IsEnabled();
            _sysSyncing = false;
            AutoStartStatusText.Text = "设置失败，请稍后重试。";
            ToolTipService.SetToolTip(AutoStartStatusText, ex.Message);
        }
    }

    private async void NsfwPreview_Click(object sender, RoutedEventArgs e)
    {
        if (Services.AppService.Store is null) return;
        if (!Services.AppService.FakeNsfwScorer &&
            !Services.PixaiScorer.LooksConfigured(Services.AppService.Settings.Settings.nsfw_model_dir))
        {
            NsfwScanText.Text = "还没有选择识别模型";
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
                NsfwSummaryText.Text = $"已识别 {scored} 张 · 敏感 {nsfw} 张 · 未识别 {unknown} 张";
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
                NsfwScanText.Text = $"识别中 {p.Done}/{p.Total}（可以点取消，已识别的会保留）");
            var result = await service.RunAsync(progress, token: _nsfwScanCts.Token);
            NsfwScanText.Text = Services.NsfwService.Describe(result) + service.DeviceSummary;
            ApplyNsfwState();
            Services.AppService.NotifyDisplayModeChanged();
        }
        catch (OperationCanceledException)
        {
            NsfwScanText.Text = "已取消；已经识别的图保持新结果";
            ApplyNsfwState();
            Services.AppService.NotifyDisplayModeChanged();
        }
        catch (Exception ex)
        {
            NsfwScanText.Text = "识别失败：" + ex.Message;
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
