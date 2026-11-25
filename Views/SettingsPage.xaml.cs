using Gallery.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Gallery.Views;

/// <summary>
/// 设置首页与六个常驻二级面板；导航保留任务、滚动位置及输入来源。
/// 主题沿用 photos 的 SettingsViewModel.SwitchTheme：
/// 切换后由 ThemeService.SetTheme(ElementTheme) 应用，选中项与下拉文字同步更新。
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private ElementTheme _theme = ElementTheme.Dark;

    public SettingsPage()
    {
        InitializeComponent();
        _currentSettingsPanel = HomeSettingsPanel;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnBackgroundPointerPressed), true);
        _theme = ThemeService.Parse(AppService.Settings.Settings.theme);
        ApplyState();
        InitializeUpdates();
        foreach (var text in new[] { BridgeStatusText, PendingText, NsfwScanText, ThumbCacheText, SyncErrorText })
            text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => RefreshSettingsSummaries());
        NsfwCancelButton.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => RefreshSettingsSummaries());
        RefreshSettingsSummaries();
        Loaded += (_, _) =>
        {
            RefreshBridge();
            RefreshThumbCacheAsync();
            RefreshDirectoryRules();
            AppService.PackChanged += OnPackChanged;
            ApplyNsfwState();
        };
        Unloaded += (_, _) => { AppService.PackChanged -= OnPackChanged; SaveBlurStrength(); };
    }

    private void OnPackChanged(string message)
        => DispatcherQueue.TryEnqueue(() => { if (!_packBusy) ApplyNsfwState(); });

    private FrameworkElement _currentSettingsPanel = null!;
    private readonly Dictionary<FrameworkElement, double> _settingsOffsets = new();
    private SettingsCard? _settingsReturnEntry;

    private static FocusState NavigationFocus(Control control)
        => control.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Pointer;

    private void SettingsCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not SettingsCard entry) return;
        var panel = (entry.Tag as string) switch
        {
            "Import" => ImportSettingsPanel,
            "Catalog" => CatalogSettingsPanel,
            "TagManage" => TagManageSettingsPanel,
            "Recognition" => RecognitionSettingsPanel,
            "Bridge" => BridgeSettingsPanel,
            "Storage" => StorageSettingsPanel,
            "About" => AboutSettingsPanel,
            _ => HomeSettingsPanel,
        };
        _settingsReturnEntry = entry;
        NavigateSettings(panel, entry.Header?.ToString() ?? "设置", NavigationFocus(entry));
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
        => NavigateSettings(HomeSettingsPanel, "设置", NavigationFocus((Control)sender));

    /// <summary>当前面板的稳定键（Home / Import / Catalog / Recognition / Bridge / Storage），供导航历史记录与回放。</summary>
    public string CurrentSection => SectionKey(_currentSettingsPanel);

    /// <summary>面板切换完成时触发，携带切换后的稳定键。</summary>
    public event Action<string>? SectionChanged;

    /// <summary>按稳定键切回对应面板，用于导航历史回放；未知键回首页。</summary>
    public void ShowSection(string key)
    {
        var panel = key switch
        {
            "Import" => ImportSettingsPanel,
            "Catalog" => CatalogSettingsPanel,
            "TagManage" => TagManageSettingsPanel,
            "Recognition" => RecognitionSettingsPanel,
            "Bridge" => BridgeSettingsPanel,
            "Storage" => StorageSettingsPanel,
            "About" => AboutSettingsPanel,
            _ => HomeSettingsPanel,
        };
        NavigateSettings(panel, SectionTitle(panel), FocusState.Pointer);
    }

    private static string SectionKey(FrameworkElement panel) => panel.Name switch
    {
        "ImportSettingsPanel" => "Import",
        "CatalogSettingsPanel" => "Catalog",
        "TagManageSettingsPanel" => "TagManage",
        "RecognitionSettingsPanel" => "Recognition",
        "BridgeSettingsPanel" => "Bridge",
        "StorageSettingsPanel" => "Storage",
        "AboutSettingsPanel" => "About",
        _ => "Home",
    };

    private string SectionTitle(FrameworkElement panel) => SectionKey(panel) switch
    {
        "Import" => "导入与归档",
        "Catalog" => "资料库",
        "TagManage" => "标签管理",
        "Recognition" => "智能识别",
        "Bridge" => "浏览器插件",
        "Storage" => "存储与诊断",
        "About" => "关于",
        _ => "设置",
    };

    /// <summary>窗口 Esc 返回设置首页；已在首页时不处理。</summary>
    public bool TryGoBack()
    {
        if (ReferenceEquals(_currentSettingsPanel, HomeSettingsPanel)) return false;
        NavigateSettings(HomeSettingsPanel, "设置", FocusState.Keyboard);
        return true;
    }

    private void NavigateSettings(FrameworkElement panel, string title, FocusState focusState)
    {
        if (ReferenceEquals(panel, _currentSettingsPanel)) return;
        SaveBlurStrength();
        _settingsOffsets[_currentSettingsPanel] = SettingsScroll.VerticalOffset;
        // 先移到中性锚点，避免隐藏当前控件时被自动转移到标题栏搜索框。
        SettingsRoot.Focus(focusState);
        _currentSettingsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        _currentSettingsPanel = panel;
        bool home = ReferenceEquals(panel, HomeSettingsPanel);
        SettingsHomeTitle.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        SettingsHomeBreadcrumb.Visibility = SettingsBreadcrumbSeparator.Visibility = SettingsPageTitle.Visibility = home
            ? Visibility.Collapsed : Visibility.Visible;
        SettingsPageTitle.Text = home ? "" : title;
        if (ReferenceEquals(panel, TagManageSettingsPanel)) LoadTagManage();
        RefreshSettingsSummaries();
        SettingsScroll.UpdateLayout();
        if (home) _settingsReturnEntry?.Focus(focusState);
        else SettingsHomeBreadcrumb.Focus(focusState);
        // 聚焦入口可能触发自动滚动，最后恢复该页自己的滚动位置。
        SettingsScroll.ChangeView(null, _settingsOffsets.GetValueOrDefault(panel), null, true);
        SectionChanged?.Invoke(SectionKey(panel));
    }

    private void RefreshSettingsSummaries()
    {
        ImportSettingsEntry.Description = $"自动分目录{(ArchiveAutoByTagsToggle.IsOn ? "已开启" : "已关闭")} · 文件夹标签{(AutoTagToggle.IsOn ? "已开启" : "已关闭")}";
        RecognitionSettingsEntry.Description = NsfwCancelButton.Visibility == Visibility.Visible
            ? NsfwScanText.Text
            : $"{(string.IsNullOrWhiteSpace(AppService.Settings.Settings.nsfw_model_dir) ? "模型未配置" : "模型已配置")} · 导入自动识别{(AutoModelTagsToggle.IsOn ? "已开启" : "已关闭")}";
        BridgeSettingsEntry.Description = $"{BridgeStatusText.Text} · 待处理 {PendingText.Text}";
        StorageSettingsEntry.Description = $"缓存：{ThumbCacheText.Text} · 最近错误 {SyncErrorText.Text}";
        var packs = PackFolders.ArchivePacks();
        CatalogSettingsEntry.Description = packs.Count == 0
            ? "拖入词库包，可并存多个"
            : $"{packs.Count} 个来源 · 拖入新包追加";
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
            : AppService.Store.ReviewableIngestCount() switch
            {
                0 => "0 条",
                var n => $"{n} 条"
            };
        RefreshBrowserLaunchPath();
    }

    private void RefreshBrowserLaunchPath()
    {
        var path = GalleryLaunchProtocol.ConfiguredPath();
        BrowserLaunchCard.Description = path is null ? "选择 Gallery.exe 后可从浏览器启动。"
            : File.Exists(path) ? path : $"找不到程序，请重新选择：{path}";
    }

    private async void ChooseBrowserLaunchPath_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) SaveBrowserLaunchPath(file.Path);
    }

    private void ResetBrowserLaunchPath_Click(object sender, RoutedEventArgs e) => SaveBrowserLaunchPath(null);

    private void SaveBrowserLaunchPath(string? path)
    {
        try
        {
            GalleryLaunchProtocol.SetTarget(path);
            BrowserLaunchFeedback.Text = "启动路径已保存";
            RefreshBrowserLaunchPath();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            BrowserLaunchFeedback.Text = error.Message;
        }
    }

    private void BridgeCopy_Click(object sender, RoutedEventArgs e)
    {
        if (BridgeTokenBox.Text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(System.Text.Json.JsonSerializer.Serialize(new
        {
            port = AppService.Bridge?.Port ?? AppService.Settings.Settings.bridge_port,
            token = BridgeTokenBox.Text,
        }));
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
        ApplyDefaultAppsState();
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
        AuthorCommentaryToggle.IsOn = settings.show_author_commentary;
        SearchHistoryToggle.IsOn = settings.search_history_enabled;
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

        RefreshPackLists();

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
        if (_packBusy) return;
        var folder = await PickFolderAsync();
        if (folder is null) return;
        await ImportPacksAsync(new[] { (Windows.Storage.IStorageItem)folder }, isModel: true);
    }

    private async void CatalogPickFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_packBusy) return;
        var folder = await PickFolderAsync();
        if (folder is null) return;
        await ImportPacksAsync(new[] { (Windows.Storage.IStorageItem)folder }, isModel: false);
    }

    private async Task<Windows.Storage.StorageFolder?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (MainWindow.Instance is { } window &&
            WinRT.Interop.WindowNative.GetWindowHandle(window) is var hwnd && hwnd != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
        return await picker.PickSingleFolderAsync();
    }

    // ---- 资料包拖拽 / 导入 ----

    private bool _packBusy;

    private void PackDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (_packBusy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        // 模型只能留一个：已经是模型框且已有模型时，直接显示拒绝，而不是拖进去才失败。
        if (ReferenceEquals(sender, ModelDropZone) && PackLibrary.HasModel)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            if (e.DragUIOverride is { } refused)
            {
                refused.Caption = "已有模型，先移除再导入";
                refused.IsCaptionVisible = true;
            }
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        SetPackDropHighlight(sender, true);
        if (e.DragUIOverride is { } accept)
        {
            accept.Caption = "松开即可导入";
            accept.IsCaptionVisible = true;
        }
    }

    private void PackDrop_DragLeave(object sender, DragEventArgs e)
    {
        SetPackDropHighlight(sender, false);
    }

    private void SetPackDropHighlight(object sender, bool highlighted)
    {
        var outline = ReferenceEquals(sender, ModelDropZone) ? ModelDropOutline : CatalogDropOutline;
        outline.Stroke = (Brush)Application.Current.Resources[highlighted ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];
    }

    private async void PackDrop_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetPackDropHighlight(sender, false);
        if (_packBusy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var isModel = ReferenceEquals(sender, ModelDropZone);
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0) await ImportPacksAsync(items, isModel);
        }
        catch (Exception ex)
        {
            AppService.DebugLog("pack drop failed: " + ex);
            SetPackStatus(isModel, "无法读取拖入的文件：" + ex.Message, isError: true);
        }
        finally { deferral.Complete(); }
    }

    private async Task ImportPacksAsync(IReadOnlyList<Windows.Storage.IStorageItem> items, bool isModel)
    {
        if (_packBusy) return;
        _packBusy = true;
        NsfwPickButton.IsEnabled = false;
        CatalogPickButton.IsEnabled = false;
        CatalogExportButton.IsEnabled = false;
        PersonalRulesExportButton.IsEnabled = false;
        PersonalRulesImportButton.IsEnabled = false;
        SetPackStatus(isModel, "正在导入…", isError: false);
        try
        {
            var report = await PackLibrary.ImportAsync(items, isModel);
            foreach (var error in report.Errors) AppService.DebugLog("pack import: " + error);
            if (report.Model > 0) Services.AppService.ActivateModelPack();
            if (report.Catalog > 0) await Task.Run(Services.AppService.ImportCatalogPacks);
            ApplyNsfwState();
            RefreshPackLists();

            if (report.Errors.Count > 0)
                SetPackStatus(isModel, string.Join("；", report.Errors), isError: true);
            else if (report.Any)
                SetPackStatus(isModel, isModel ? "模型已导入，下次识别时使用。" : "资料库已导入。", isError: false);
            else
                SetPackStatus(isModel, "这些资料已导入，无需重复添加。", isError: false);
        }
        catch (Exception ex)
        {
            // 拖放走的是 async void，异常逃出去就是整个应用崩掉；这里兜住并留证据。
            AppService.DebugLog("pack import failed: " + ex);
            SetPackStatus(isModel, "导入失败：" + ex.Message, isError: true);
        }
        finally
        {
            _packBusy = false;
            NsfwPickButton.IsEnabled = !PackLibrary.HasModel;
            CatalogPickButton.IsEnabled = true;
            CatalogExportButton.IsEnabled = true;
            PersonalRulesExportButton.IsEnabled = true;
            PersonalRulesImportButton.IsEnabled = true;
        }
    }

    /// <summary>卡片内的状态行：成功用次要色，失败用系统危险色。空字符串表示隐藏。</summary>
    private void SetPackStatus(bool isModel, string message, bool isError)
    {
        var host = isModel ? ModelPackStatus : CatalogPackStatus;
        if (host is null) return;
        if (string.IsNullOrWhiteSpace(message))
        {
            host.Visibility = Visibility.Collapsed;
            host.Text = "";
            return;
        }
        host.Text = message;
        host.Visibility = Visibility.Visible;
        host.Foreground = isError
            ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    /// <summary>重画两处已导入列表；模型单一、资料库多个。</summary>
    private void RefreshPackLists()
    {
        var entries = PackLibrary.List();
        var models = entries.Where(e => e.IsModel).ToList();
        var catalogs = entries.Where(e => !e.IsModel).ToList();
        BuildPackList(ModelPackList, models, "还没有导入模型");
        BuildPackList(CatalogPackList, catalogs, "还没有导入资料库");
        NsfwPickButton.IsEnabled = !_packBusy && models.Count == 0;
        RefreshSettingsSummaries();
    }

    private void BuildPackList(StackPanel host, List<PackEntry> entries, string emptyHint)
    {
        host.Children.Clear();
        if (entries.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = emptyHint,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.55,
            });
            return;
        }
        foreach (var entry in entries) host.Children.Add(BuildPackRow(entry));
    }

    private FrameworkElement BuildPackRow(PackEntry entry)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 8, 8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (entry.IsModel) ToolTipService.SetToolTip(text, entry.Path);
        text.Children.Add(new TextBlock
        {
            Text = entry.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = entry.Detail,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        grid.Children.Add(text);

        var remove = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
        };
        ToolTipService.SetToolTip(remove, entry.IsModel ? "移除模型" : "移除这个资料库");
        AutomationProperties.SetName(remove, "移除 " + entry.Name);
        remove.Click += (_, _) => { if (!_packBusy) ShowRemoveConfirm(grid, entry); };
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);

        border.Child = grid;
        return border;
    }

    /// <summary>行内二级确认：清除来源资料，保留图片已有标签与人工修改。</summary>
    private void ShowRemoveConfirm(Grid row, PackEntry entry)
    {
        row.Children.Clear();
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = $"移除「{entry.Name}」？" });
        stack.Children.Add(new TextBlock
        {
            Text = entry.IsModel
                ? "移除后要重新拖入模型才能继续识别。"
                : "将清除这份资料库提供的资料；图片已有标签和人工修改会保留。",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "取消" };
        var confirm = new Button
        {
            Content = "移除",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        cancel.Click += (_, _) => RefreshPackLists();
        confirm.Click += async (_, _) =>
        {
            if (_packBusy) return;
            _packBusy = true;
            confirm.IsEnabled = false;
            cancel.IsEnabled = false;
            NsfwPickButton.IsEnabled = false;
            CatalogPickButton.IsEnabled = false;
            try
            {
                await PackLibrary.RemoveAsync(entry);
                ApplyNsfwState();
            }
            catch (Exception ex)
            {
                AppService.DebugLog("pack remove failed: " + ex);
                SetPackStatus(entry.IsModel, "移除失败：" + ex.Message, isError: true);
            }
            finally
            {
                _packBusy = false;
                RefreshPackLists();
                CatalogPickButton.IsEnabled = true;
            }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        stack.Children.Add(buttons);
        Grid.SetColumnSpan(stack, 2);
        row.Children.Add(stack);
    }

    private async void CatalogExport_Click(object sender, RoutedEventArgs e)
    {
        if (_packBusy) return;
        _packBusy = true;
        CatalogExportButton.IsEnabled = false;
        CatalogPickButton.IsEnabled = false;
        PersonalRulesExportButton.IsEnabled = false;
        PersonalRulesImportButton.IsEnabled = false;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = $"Gallery-资料库-{DateTime.Now:yyyyMMdd-HHmm}",
            };
            picker.FileTypeChoices.Add("资料库压缩包", new List<string> { ".zip" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var count = await PackLibrary.ExportAsync(file.Path);
            CatalogExportStatus.Text = $"已导出 {count} 条资料。";
            CatalogExportStatus.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppService.DebugLog("pack export failed: " + ex);
            CatalogExportStatus.Text = "导出失败：" + ex.Message;
            CatalogExportStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            _packBusy = false;
            CatalogExportButton.IsEnabled = true;
            CatalogPickButton.IsEnabled = true;
            PersonalRulesExportButton.IsEnabled = true;
            PersonalRulesImportButton.IsEnabled = true;
        }
    }

    private async void PersonalRulesExport_Click(object sender, RoutedEventArgs e)
    {
        if (_packBusy || AppService.Catalog is not { } catalog) return;
        _packBusy = true;
        PersonalRulesExportButton.IsEnabled = false;
        PersonalRulesImportButton.IsEnabled = false;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"Gallery-个人规则-{DateTime.Now:yyyyMMdd}" };
            picker.FileTypeChoices.Add("个人规则备份", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await Task.Run(() => catalog.ExportPersonalRules(file.Path));
            PersonalRulesStatus.Text = "个人规则已导出，请妥善保管，不要作为公共资料包发布。";
            PersonalRulesStatus.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            PersonalRulesStatus.Text = "导出失败：" + ex.Message;
            PersonalRulesStatus.Visibility = Visibility.Visible;
            AppService.DebugLog("personal rules export failed: " + ex);
        }
        finally
        {
            _packBusy = false;
            PersonalRulesExportButton.IsEnabled = true;
            PersonalRulesImportButton.IsEnabled = true;
        }
    }

    private async void PersonalRulesImport_Click(object sender, RoutedEventArgs e)
    {
        if (_packBusy || AppService.Catalog is not { } catalog) return;
        _packBusy = true;
        PersonalRulesExportButton.IsEnabled = false;
        PersonalRulesImportButton.IsEnabled = false;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var choice = await DialogService.ShowAsync(new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "怎样恢复个人备份？",
                Content = new TextBlock
                {
                    Text = "保留现在的设置：只补充尚未设置的项目。\n采用备份中的设置：同一标签的关联和显示设置改回备份中的值；若备份包含目录规则，也恢复该版本。\n两种方式都不会删除图片，备份中的历史整理规则只处理没有关联图片的重复标签。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "保留现在的设置",
                SecondaryButtonText = "采用备份中的设置",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            });
            if (choice == ContentDialogResult.None) return;
            var pending = await Task.Run(() => catalog.ImportPersonalRules(file.Path, choice == ContentDialogResult.Secondary));
            RefreshDirectoryRules();
            AppService.NotifyPersonalRulesRestored();
            PersonalRulesStatus.Text = choice == ContentDialogResult.Secondary
                ? "已恢复备份中的个人设置。" : "已补充个人规则，现有设置已保留。";
            if (pending > 0) PersonalRulesStatus.Text += $" 有 {pending} 项资料或标签等待匹配。";
            PersonalRulesStatus.Visibility = Visibility.Visible;
            ApplyNsfwState();
        }
        catch (Exception ex)
        {
            PersonalRulesStatus.Text = "恢复失败：" + ex.Message;
            PersonalRulesStatus.Visibility = Visibility.Visible;
            AppService.DebugLog("personal rules import failed: " + ex);
        }
        finally
        {
            _packBusy = false;
            PersonalRulesExportButton.IsEnabled = true;
            PersonalRulesImportButton.IsEnabled = true;
        }
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            // 代码里新建的 ContentDialog 不会自动挂到可视树，缺 XamlRoot 会直接抛参数错误（不是返回失败）。
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            await Gallery.Services.DialogService.ShowAsync(dialog);
        }
        catch (Exception ex)
        {
            AppService.DebugLog("settings dialog failed: " + ex.Message + " | " + title + " / " + message);
        }
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

    private void AuthorCommentaryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        Services.AppService.Settings.Settings.show_author_commentary = AuthorCommentaryToggle.IsOn;
        Services.AppService.Settings.Save();
    }

    private void SearchHistoryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sysSyncing) return;
        Services.AppService.Settings.Settings.search_history_enabled = SearchHistoryToggle.IsOn;
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

    private void ApplyDefaultAppsState()
    {
        DefaultAppsStatusText.Text = Services.FileAssociationService.IsRegistered()
            ? "已登记为打开方式候选"
            : "尚未登记";
    }

    /// <summary>先确保登记了候选，再把用户送到系统“默认应用”，默认由用户在系统里点确认。</summary>
    private async void DefaultApps_Click(object sender, RoutedEventArgs e)
    {
        DefaultAppsButton.IsEnabled = false;
        try
        {
            if (!Services.FileAssociationService.EnsureRegistered())
            {
                DefaultAppsStatusText.Text = "登记失败，请检查权限";
                return;
            }
            ApplyDefaultAppsState();
            if (!await Services.FileAssociationService.OpenDefaultAppsAsync())
                DefaultAppsStatusText.Text = "未能打开系统设置，请在“默认应用”里手动选择 Gallery";
        }
        finally { DefaultAppsButton.IsEnabled = true; }
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
        if (!Services.AppService.ModelAvailable(Services.AppService.Settings.Settings.nsfw_model_dir))
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
