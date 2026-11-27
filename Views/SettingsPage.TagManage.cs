using System.ComponentModel;
using System.Runtime.CompilerServices;
using Gallery.Controls;
using Gallery.Controls.Catalog;
using Gallery.Controls.Tags;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Views;

/// <summary>
/// 设置页「标签管理」面板：搜索（全字段即时过滤）、分类切换、新建、点行开详情弹窗（改显示名/分类/别名、删除）、
/// 自动检测重复、以及选择模式下的批量删除与两两合并。
/// </summary>
public sealed partial class SettingsPage
{
    private readonly LocalSelectionController _tagManageSelection = new();
    private SearchInputBox? _tagManageSearch;
    private ListView? _tagManageList;
    private TextBlock? _tagManageStatus;
    private TextBlock? _tagManageEmpty;
    private Button? _tagManageSelect;
    private Button? _tagManageDelete;
    private Button? _tagManageMerge;
    private (FontIcon Icon, TextBlock Label)? _tagManageSelectParts;
    private MenuFlyoutItem? _tagManageZeroToggle;
    /// <summary>下一次重绘是否跑进入动效（切分类/切选择模式/新建后），搜索逐字输入不跑。</summary>
    private bool _tagManageAnimateNext;
    private StackPanel? _tagManageTabs;
    private readonly List<TagManageTab> _tagManageTabButtons = [];
    private string _tagManageCategory = "";
    private bool _tagManageIncludeZero;
    private bool _tagManageSyncingTabs;
    private int _tagManageSearchToken;
    private List<TagManageService.TagRow> _tagManageRows = [];
    private TagManageService.TagRow[] _tagManageVisible = [];
    private List<TagManageViewItem> _tagManageItems = [];
    private string? _tagManageHighlightKey;

    /// <summary>分类栏的一个分段。保留 Label/Count 引用，刷新时只改文本与选中态，不重建控件。</summary>
    private sealed record TagManageTab(string Key, ToggleButton Host, TextBlock Label, TextBlock Count);

    private void EnsureTagManageUI()
    {
        if (_tagManageList is not null) return;

        // 行 1：搜索 + 主操作 + 收纳菜单。
        // 低频开关（检测重复、显示 0 张）收进 ⋯：主条每多一个常驻控件，列表就少一行可见项
        // （规范「改一次就不动的开关进次级入口」「一个动作一个按钮，分叉放进菜单」）。
        var search = _tagManageSearch = new SearchInputBox
        {
            PlaceholderText = "搜索内部名、显示名、身份键或别名",
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        search.TextChanged += (_, _) => QueueTagManageSearch();

        var create = ToolButton("\uE710", "新建", accent: true);
        create.Click += (_, _) => _ = CreateTagAsync();
        _tagManageSelect = ToolButton("\uE73A", "选择");
        _tagManageSelectParts = ((FontIcon)((StackPanel)_tagManageSelect.Content).Children[0],
            (TextBlock)((StackPanel)_tagManageSelect.Content).Children[1]);
        _tagManageSelect.Click += (_, _) =>
        {
            // 勾选框是随模式出现的元素，必须带进入动效（规范「动态出现的次级选项」）。
            if (!_tagManageSelection.Exit()) _tagManageSelection.Enter();
            AnimateTagListIn();
        };
        _tagManageDelete = ToolButton("\uE74D", "删除");
        _tagManageDelete.Visibility = Visibility.Collapsed;
        _tagManageDelete.Click += (_, _) => _ = DeleteSelectedTagsAsync();
        _tagManageMerge = ToolButton("\uE81E", "合并");
        _tagManageMerge.Visibility = Visibility.Collapsed;
        _tagManageMerge.Click += (_, _) => _ = MergeSelectedTagsAsync();
        var more = new Button
        {
            Content = new FontIcon { Glyph = "\uE712", FontSize = 14 },
            MinWidth = 0, MinHeight = 32, Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(more, "更多操作");
        more.Flyout = BuildTagManageMenu();

        // 选中数只在这一刻有意义：总数已经写在「全部 N」上，平时不重复显示。
        _tagManageStatus = new TextBlock
        {
            Text = "", FontSize = 12, Opacity = .75, Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(create);
        actions.Children.Add(_tagManageDelete);
        actions.Children.Add(_tagManageMerge);
        actions.Children.Add(_tagManageSelect);
        actions.Children.Add(more);
        actions.Children.Add(_tagManageStatus);

        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(search);
        Grid.SetColumn(actions, 1);
        top.Children.Add(actions);
        Grid.SetRow(top, 0);
        TagManageHost.Children.Add(top);

        var tabs = _tagManageTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetRow(tabs, 1);
        TagManageHost.Children.Add(tabs);

        // 面板内列表：滚动条是浮层，内容右侧显式留 16（UI 规范「滚动条与内容必须分离」）。
        _tagManageList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            Padding = new Thickness(0, 0, 16, 0),
            ItemContainerStyle = (Style)XamlReader.Load("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                       TargetType="ListViewItem">
                  <!-- 行高是显式常量：视口高度按 n + 0.5 行反算，改这里要同步改 TagManageHost 的行高 -->
                  <Setter Property="MinHeight" Value="48"/>
                  <Setter Property="Padding" Value="4,0,4,0"/>
                  <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                  <Setter Property="CornerRadius" Value="4"/>
                </Style>
                """),
        };
        _tagManageList.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid AutomationProperties.Name="{Binding Display}">
                <!-- 高亮层常驻、默认几乎全透明：定位后由 Composition 打两下强调色（规范「脉冲两次」）。
                     不用 Visibility 切换，否则既做不了动画，又要靠绑定反复重绘。 -->
                <Border x:Name="HighlightLayer" Background="{ThemeResource AccentFillColorDefaultBrush}"
                        CornerRadius="4" Opacity="0.01" IsHitTestVisible="False"/>
                <Grid ColumnSpacing="10" Margin="12,0">
                  <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="24"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto" MinWidth="72"/>
                  </Grid.ColumnDefinitions>
                  <!-- 勾选框自绘：选中才有勾，未选中只有描边（不用 CheckBox，深色下会是深底黑勾）。
                       列宽固定 24 不随显隐塌陷，否则进出选择模式整行文字会横跳。 -->
                  <Grid Width="20" Height="20" VerticalAlignment="Center" Visibility="{Binding SelectVisible}">
                    <Border CornerRadius="4" BorderThickness="1"
                            BorderBrush="{ThemeResource ControlStrongStrokeColorDefaultBrush}"/>
                    <Border CornerRadius="4" Background="{ThemeResource AccentFillColorDefaultBrush}"
                            Visibility="{Binding CheckVisible}">
                      <FontIcon Glyph="&#xE73E;" FontSize="10"
                                Foreground="{ThemeResource TextOnAccentFillColorPrimaryBrush}"/>
                    </Border>
                  </Grid>
                  <StackPanel Grid.Column="1" Spacing="1" VerticalAlignment="Center">
                    <TextBlock Text="{Binding Display}" FontSize="14" FontWeight="SemiBold"
                               TextTrimming="CharacterEllipsis"/>
                    <TextBlock Text="{Binding SubName}" FontSize="11" Opacity=".55" Visibility="{Binding SubNameVisible}"
                               TextTrimming="CharacterEllipsis" FontFamily="Consolas, Cascadia Mono, Segoe UI"/>
                  </StackPanel>
                  <!-- 分类沿用图库标签贴纸的七类配色，同一分类在两处颜色一致 -->
                  <Border Grid.Column="2" CornerRadius="4" BorderThickness="1" Padding="6,1,6,2"
                          VerticalAlignment="Center" Background="{Binding ChipBackground}"
                          BorderBrush="{Binding ChipBorder}">
                    <TextBlock Text="{Binding CategoryLabel}" FontSize="11" Foreground="{Binding ChipForeground}"/>
                  </Border>
                  <TextBlock Grid.Column="3" Text="{Binding UsageText}" Opacity=".65" FontSize="12"
                             HorizontalAlignment="Right" VerticalAlignment="Center"/>
                </Grid>
              </Grid>
            </DataTemplate>
            """);
        _tagManageList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not TagManageViewItem item) return;
            if (!_tagManageSelection.IsActive)
            {
                _ = OpenTagDetailAsync(item.Id);
                return;
            }
            _tagManageSelection.Toggle(item.Key,
                IsCtrlDown(), IsShiftDown(), _tagManageVisible.Select(t => t.Id.ToString()).ToArray());
        };
        _tagManageSelection.Changed += RefreshTagManageUi;

        _tagManageEmpty = new TextBlock
        {
            Text = "没有匹配的标签", FontSize = 13, Opacity = .55,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            Visibility = Visibility.Collapsed,
        };
        var listHost = new Grid();
        listHost.Children.Add(_tagManageList);
        listHost.Children.Add(_tagManageEmpty);
        Grid.SetRow(listHost, 2);
        TagManageHost.Children.Add(listHost);

        // 主题切换后分类贴纸与胶囊的配色要重算（两套色都在 TagColors / ThemeDictionaries 里）。
        ActualThemeChanged += (_, _) =>
        {
            foreach (var tab in _tagManageTabButtons) ApplyTabAccent(tab.Host);
            if (_tagManageItems.Count > 0) RenderTagManageList();
        };
        RebuildTagManageTabs();
        RefreshTagManageUi();
    }

    /// <summary>⋯ 菜单：低频项进次级入口，主条不再被它们占位。</summary>
    private MenuFlyout BuildTagManageMenu()
    {
        // MenuFlyout 默认 Placement 是 Top，⋯ 按钮在工具条上会向上弹、盖住搜索框；
        // 这类「点按钮出菜单」一律向下展开，底部空间不足时由框架自动翻转。
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

        // 两项都带图标列：一项有图标、一项没有会显示成「上有下无」，文字起点还对不齐。
        var detect = new MenuFlyoutItem
        {
            Text = "自动检测重复标签",
            Icon = new FontIcon { Glyph = "\uE8EF", FontSize = 14 },
        };
        detect.Click += (_, _) => _ = DetectDuplicatesAsync();

        // 开关不用 ToggleMenuFlyoutItem：它的 ✓ 独占图标位，**未勾选时整行没有任何标记**，
        // 看上去就是一条普通菜单项。这里照项目内勾选框的画法给两态图标——空框 / 带勾的方框。
        _tagManageZeroToggle = new MenuFlyoutItem { Text = "显示 0 张的标签" };
        ApplyZeroToggleIcon(_tagManageIncludeZero);
        _tagManageZeroToggle.Click += (_, _) =>
        {
            _tagManageIncludeZero = !_tagManageIncludeZero;
            ApplyZeroToggleIcon(_tagManageIncludeZero);
            _tagManageAnimateNext = true;
            LoadTagManage();
        };

        menu.Items.Add(detect);
        // 动作与开关是两件事，用分隔线分组（与筛选面板的菜单同一写法）。
        // 默认那条 1px 线用的是 `DividerStrokeColorDefaultBrush` 级别的最低对比度，深色下贴在
        // #2C2C2C 底上等于看不见；这里按元素主题给一条能辨认的分组线。
        menu.Items.Add(new MenuFlyoutSeparator
        {
            Height = 1,
            Margin = new Thickness(0, 2, 0, 2),
            Background = DividerBrush(),
        });
        menu.Items.Add(_tagManageZeroToggle);
        TightenMenuWidth(menu);
        return menu;
    }

    /// <summary>勾选态用图标表达：`E739` 空框、`E73A` 带勾的方框。</summary>
    private void ApplyZeroToggleIcon(bool on)
    {
        if (_tagManageZeroToggle is null) return;
        _tagManageZeroToggle.Icon = new FontIcon { Glyph = on ? "\uE73A" : "\uE739", FontSize = 14 };
    }

    /// <summary>
    /// 菜单分组线。`MenuFlyoutSeparator` 自带的描边色是「几乎看不见」那一档，深色下贴在菜单底上
    /// 完全消失，所以按元素主题自己给：浅色用黑 20%、深色用白 18%。
    /// 不能用 `Application.Current.RequestedTheme` 判色——本项目主题设在 RootGrid 上，应用级值恒为系统主题。
    /// </summary>
    private Brush DividerBrush() => new SolidColorBrush(ActualTheme == ElementTheme.Light
        ? Windows.UI.Color.FromArgb(0x33, 0, 0, 0)
        : Windows.UI.Color.FromArgb(0x2E, 255, 255, 255));

    /// <summary>
    /// `MenuFlyoutPresenter` 自带约 250 DIP 的最小宽度，两条短项会被撑出一大块空行。
    /// 只压低 `MinWidth`，模板、圆角、边距仍走默认主题样式。
    /// </summary>
    private static void TightenMenuWidth(MenuFlyout menu)
    {
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        menu.MenuFlyoutPresenterStyle = style;
    }

    /// <summary>
    /// 列表换内容时的进入动效：淡入 + 6px 上移，160ms。只用于「换了一批数据」的场合
    /// （切分类、切选择模式、新建后定位），搜索逐字输入不跑——高频重放等于闪烁。
    /// 系统关闭动画时直接显示（规范「动态出现的次级选项必须有进入动效」）。
    /// </summary>
    private void AnimateTagListIn()
    {
        if (_tagManageList is null) return;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(_tagManageList);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(_tagManageList, true);
        // 起始值留 1：动画若因环境没跑起来，列表最多不动，不会永久透明。
        visual.Opacity = 1f;
        visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 6, 0));

        var ease = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0f, 0f),
            new System.Numerics.Vector2(0.35f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }

    /// <summary>新建后定位到的行打两下强调色：125／300／425／700ms，淡出终点留 0.01。</summary>
    private void PulseTagRow(ListViewItem row)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        if (row.ContentTemplateRoot is not FrameworkElement root) return;
        if (FindDescendant(root, "HighlightLayer") is not FrameworkElement layer) return;
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        var compositor = visual.Compositor;
        var pulse = compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0f, 0.01f);
        pulse.InsertKeyFrame(0.18f, 0.30f);
        pulse.InsertKeyFrame(0.43f, 0.01f);
        pulse.InsertKeyFrame(0.61f, 0.30f);
        pulse.InsertKeyFrame(1f, 0.01f);
        pulse.Duration = TimeSpan.FromMilliseconds(700);
        visual.StartAnimation("Opacity", pulse);
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FindDescendant(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
    }

    /// <summary>工具条按钮：图标（12px）+ 文字，统一 32 高，与搜索框同一条高度线。</summary>
    private static Button ToolButton(string glyph, string label, bool accent = false)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 12 };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, text },
            },
            MinWidth = 0, MinHeight = 32, Padding = new Thickness(12, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        // 内容是面板时 UIA 不会自己拼出名字，必须显式给
        AutomationProperties.SetName(button, label);
        return button;
    }

    private sealed class TagManageViewItem : INotifyPropertyChanged
    {
        public int Id { get; init; }
        public string Key => Id.ToString();
        public string Display { get; init; } = "";
        public string SubName { get; init; } = "";
        public string CategoryLabel { get; init; } = "";
        public string UsageText { get; init; } = "";
        public Visibility SubNameVisible { get; init; } = Visibility.Collapsed;
        public Brush ChipBackground { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        public Brush ChipBorder { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        public Brush ChipForeground { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Gray);

        private Visibility _selectVisible = Visibility.Collapsed;
        private Visibility _checkVisible = Visibility.Collapsed;
        private Visibility _highlight = Visibility.Collapsed;

        public Visibility SelectVisible { get => _selectVisible; set => Set(ref _selectVisible, value); }
        public Visibility CheckVisible { get => _checkVisible; set => Set(ref _checkVisible, value); }
        public Visibility Highlight { get => _highlight; set => Set(ref _highlight, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static bool IsCtrlDown() => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
        Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    private static bool IsShiftDown() => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
        Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void LoadTagManage()
    {
        EnsureTagManageUI();
        try
        {
            var keyword = _tagManageSearch?.Text ?? "";
            _tagManageRows = TagManageService.List(keyword, _tagManageCategory, _tagManageIncludeZero);
            var counts = TagManageService.Counts(keyword, _tagManageIncludeZero);
            UpdateTagManageTabs(counts);
            RenderTagManageList();
        }
        catch (Exception ex)
        {
            AppService.DebugLog("tag-manage-load failed: " + ex);
            if (_tagManageStatus is not null) _tagManageStatus.Text = "读取失败：" + ex.Message;
        }
    }

    /// <summary>输入即时过滤；每次按键查库对 2000 条标签是几毫秒，加 180ms 防抖只为少打断输入。</summary>
    private async void QueueTagManageSearch()
    {
        var token = ++_tagManageSearchToken;
        try
        {
            await Task.Delay(180);
            if (token != _tagManageSearchToken || _tagManageList is null) return;
            _tagManageHighlightKey = null;
            LoadTagManage();
        }
        catch (Exception ex) { AppService.DebugLog("tag-manage-search failed: " + ex); }
    }

    /// <summary>
    /// 分类栏用胶囊分段（ToggleButton，项目里「批量审核」同款控件）：RadioButton 的圆点在八项横排时
    /// 既占宽度又抢视线，而这组只是视图过滤，不是表单里的必选项。
    /// </summary>
    private void RebuildTagManageTabs()
    {
        if (_tagManageTabs is null) return;
        _tagManageTabs.Children.Clear();
        _tagManageTabButtons.Clear();
        var keys = new List<string> { "" };
        keys.AddRange(TagManageService.AllCategories.Select(c => c.Key));
        foreach (var key in keys)
        {
            var label = new TextBlock { Text = "", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var count = new TextBlock
            {
                Text = "", FontSize = 11, Opacity = .7, VerticalAlignment = VerticalAlignment.Center,
            };
            var host = new ToggleButton
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6, Children = { label, count },
                },
                MinWidth = 0, MinHeight = 32, Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(4),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ApplyTabAccent(host);
            host.Click += (_, _) =>
            {
                if (_tagManageSyncingTabs) return;
                _tagManageCategory = key;
                _tagManageAnimateNext = true;
                LoadTagManage();
            };
            _tagManageTabButtons.Add(new TagManageTab(key, host, label, count));
            _tagManageTabs.Children.Add(host);
        }
    }

    /// <summary>
    /// 选中段用强调色填充。默认的 checked 只是抬一档灰，八个胶囊排在一起几乎看不出选了哪个。
    /// 颜色必须按元素主题从 ThemeDictionaries 里取——Application.Resources 的索引器走的是
    /// 系统主题（本项目主题设在 RootGrid 上），取到的会和当前皮肤对不上。
    /// </summary>
    private void ApplyTabAccent(ToggleButton host)
    {
        var accent = ThemeBrush("AccentFillColorDefaultBrush");
        var onAccent = ThemeBrush("TextOnAccentFillColorPrimaryBrush");
        var resources = new ResourceDictionary();
        if (accent is not null)
        {
            resources["ToggleButtonBackgroundChecked"] = accent;
            resources["ToggleButtonBackgroundCheckedPointerOver"] = accent;
            resources["ToggleButtonBackgroundCheckedPressed"] = accent;
        }
        if (onAccent is not null)
        {
            resources["ToggleButtonForegroundChecked"] = onAccent;
            resources["ToggleButtonForegroundCheckedPointerOver"] = onAccent;
            resources["ToggleButtonForegroundCheckedPressed"] = onAccent;
        }
        // 未选中靠描边成形（不然就是一串纯文字），选中靠实底；只靠底色在浅强调色主题下几乎看不出差别。
        var stroke = ThemeBrush("ControlStrokeColorDefaultBrush");
        if (stroke is not null)
        {
            resources["ToggleButtonBorderBrush"] = stroke;
            resources["ToggleButtonBorderBrushPointerOver"] = stroke;
            resources["ToggleButtonBorderBrushChecked"] = stroke;
            resources["ToggleButtonBorderBrushCheckedPointerOver"] = stroke;
            resources["ToggleButtonBorderBrushCheckedPressed"] = stroke;
            host.BorderThickness = new Thickness(1);
        }
        host.Resources = resources;
    }

    private Brush? ThemeBrush(string key)
    {
        var themeKey = ActualTheme == ElementTheme.Dark ? "Default" : "Light";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var theme)
            && theme is ResourceDictionary dictionary
            && dictionary.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return Application.Current.Resources.TryGetValue(key, out var fallback) && fallback is Brush other
            ? other : null;
    }

    private void UpdateTagManageTabs(Dictionary<string, int> counts)
    {
        _tagManageSyncingTabs = true;
        try
        {
            var total = counts.Values.Sum();
            foreach (var tab in _tagManageTabButtons)
            {
                var selected = tab.Key == _tagManageCategory;
                var count = tab.Key.Length == 0 ? total : counts.GetValueOrDefault(tab.Key);
                tab.Label.Text = tab.Key.Length == 0 ? "全部" : TagManageService.Label(tab.Key);
                tab.Count.Text = count.ToString();
                AutomationProperties.SetName(tab.Host, $"{tab.Label.Text} {count}");
                tab.Host.IsChecked = selected;
                // 一条都没有、又不在当前选中的分类直接隐藏（规范：不适用的选项不置灰）。
                tab.Host.Visibility = count == 0 && !selected && tab.Key.Length > 0
                    ? Visibility.Collapsed : Visibility.Visible;
                tab.Count.Opacity = selected ? .85 : .55;
            }
        }
        finally { _tagManageSyncingTabs = false; }
    }

    private void RenderTagManageList()
    {
        if (_tagManageList is null) return;
        var dark = ActualTheme == ElementTheme.Dark;
        _tagManageVisible = [.. _tagManageRows];
        _tagManageItems = _tagManageVisible.Select(t =>
        {
            var colors = TagColors.Get(t.Category, dark);
            var hasZh = !string.IsNullOrWhiteSpace(t.Zh);
            return new TagManageViewItem
            {
                Id = t.Id,
                // 有中文名时中文名做标题、内部名做副标题；没有中文名就只显示内部名，不重复两遍。
                Display = hasZh ? t.Zh : t.Name,
                SubName = hasZh ? t.Name : "",
                SubNameVisible = hasZh ? Visibility.Visible : Visibility.Collapsed,
                CategoryLabel = TagManageService.Label(t.Category),
                UsageText = $"{t.Usage} 张",
                ChipBackground = new SolidColorBrush(TagColors.FromHex(colors.Bg)),
                ChipBorder = new SolidColorBrush(TagColors.FromHex(colors.Border)),
                ChipForeground = new SolidColorBrush(TagColors.FromHex(colors.Fg)),
                SelectVisible = _tagManageSelection.IsActive ? Visibility.Visible : Visibility.Collapsed,
                CheckVisible = _tagManageSelection.Contains(t.Id.ToString()) ? Visibility.Visible : Visibility.Collapsed,
                Highlight = _tagManageHighlightKey == "id:" + t.Id ? Visibility.Visible : Visibility.Collapsed,
            };
        }).ToList();
        _tagManageList.ItemsSource = _tagManageItems;

        if (_tagManageAnimateNext)
        {
            _tagManageAnimateNext = false;
            AnimateTagListIn();
        }

        var empty = _tagManageItems.Count == 0;
        _tagManageList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (_tagManageEmpty is not null) _tagManageEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (_tagManageHighlightKey is { } key && key.StartsWith("id:"))
        {
            var target = _tagManageItems.FirstOrDefault(i => i.Key == key[3..]);
            if (target is not null)
            {
                _tagManageHighlightKey = null;
                _tagManageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
                _tagManageList.UpdateLayout();
                // 三步缺一不可：滚到首行、给键盘焦点框、强调色脉冲两次（规范「新建后的落点」）。
                if (_tagManageList.ContainerFromItem(target) is ListViewItem row)
                {
                    row.Focus(FocusState.Keyboard);
                    PulseTagRow(row);
                }
            }
        }
    }

    private void SyncTagManageChecks()
    {
        foreach (var item in _tagManageItems)
        {
            item.SelectVisible = _tagManageSelection.IsActive ? Visibility.Visible : Visibility.Collapsed;
            item.CheckVisible = _tagManageSelection.Contains(item.Key) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RefreshTagManageUi()
    {
        if (_tagManageStatus is null) return;
        SyncTagManageChecks();
        var count = _tagManageSelection.Count;
        _tagManageStatus.Text = count > 0 ? $"已选 {count}" : "";
        _tagManageStatus.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 只改文字与图标：文字变「取消选择」就说明已进入选择模式，一项未选也看得出来。
        var active = _tagManageSelection.IsActive;
        if (_tagManageSelectParts is (var icon, var label))
        {
            icon.Glyph = active ? "\uE711" : "\uE73A";
            label.Text = active ? "取消选择" : "选择";
        }
        if (_tagManageDelete is not null)
            _tagManageDelete.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_tagManageMerge is not null)
            _tagManageMerge.Visibility = count == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task OpenTagDetailAsync(int id)
    {
        try
        {
            var detail = await Task.Run(() => TagManageService.Get(id));
            if (detail is null) { LoadTagManage(); return; }
            var dialog = new TagManageDialog(XamlRoot, detail);
            await DialogService.ShowAsync(dialog);
            if (dialog.Result is null) return;
            _tagManageHighlightKey = dialog.Result.Deleted ? null : "id:" + id;
            _tagManageSelection.Exit();
            LoadTagManage();
        }
        catch (Exception ex)
        {
            AppService.DebugLog("tag-manage-detail failed: " + ex);
            if (_tagManageStatus is not null) _tagManageStatus.Text = "打开失败：" + ex.Message;
        }
    }

    private async Task CreateTagAsync()
    {
        try
        {
            var category = new ComboBox { Header = "分类", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var key in TagManageService.ManualCategories)
                category.Items.Add(new ComboBoxItem { Content = TagManageService.Label(key), Tag = key });
            category.SelectedIndex = 4;
            var name = new TextBox { Header = "内部名", PlaceholderText = "英文或原样名，作为识别与身份对齐的键" };
            var zh = new TextBox { Header = "显示名（中）" };
            var en = new TextBox { Header = "显示名（英）" };
            var error = new TextBlock
            {
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "新建标签", PrimaryButtonText = "创建",
                CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel { Spacing = 8, MinWidth = 360, Children = { category, name, zh, en, error } },
            };
            Controls.TextSelectionBehavior.Attach(dialog);
            while (true)
            {
                if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return;
                var categoryKey = (category.SelectedItem as ComboBoxItem)?.Tag as string ?? "custom";
                var raw = name.Text.Trim();
                if (raw.Length == 0)
                {
                    error.Text = "内部名不能为空";
                    error.Visibility = Visibility.Visible;
                    continue;
                }
                if (AppService.Store is not { } store) return;
                var created = await Task.Run(() =>
                    TagManageService.Create(store, categoryKey, raw, zh.Text, en.Text));
                if (created <= 0)
                {
                    error.Text = $"「{raw}」在同一分类下已存在";
                    error.Visibility = Visibility.Visible;
                    continue;
                }
                // 新标签关联图为 0：必须先放开「只显示有图的」，再定位，否则建完看不见自己建的。
                _tagManageIncludeZero = true;
                ApplyZeroToggleIcon(true);
                _tagManageCategory = "";
                _tagManageSearch!.Text = "";
                _tagManageHighlightKey = "id:" + created;
                LoadTagManage();
                return;
            }
        }
        catch (Exception ex)
        {
            AppService.DebugLog("tag-manage-create failed: " + ex);
            if (_tagManageStatus is not null) _tagManageStatus.Text = "新建失败：" + ex.Message;
        }
    }

    private async Task DetectDuplicatesAsync()
    {
        try
        {
            var dialog = new TagDuplicateDialog(XamlRoot);
            await DialogService.ShowAsync(dialog);
            if (!dialog.Changed) return;
            _tagManageSelection.Exit();
            LoadTagManage();
        }
        catch (Exception ex)
        {
            AppService.DebugLog("tag-manage-detect failed: " + ex);
            if (_tagManageStatus is not null) _tagManageStatus.Text = "检测失败：" + ex.Message;
        }
    }

    private async Task DeleteSelectedTagsAsync()
    {
        var rows = _tagManageVisible.Where(t => _tagManageSelection.Contains(t.Id.ToString())).ToArray();
        if (rows.Length == 0) return;
        var total = rows.Sum(t => t.Usage);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "删除标签",
            Content = new TextBlock
            {
                Text = $"将删除 {rows.Length} 个标签，并移除 {total} 条图片标签关联。图片本身不会删除。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.None,
        };
        DialogService.UseDestructivePrimary(dialog);
        if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (AppService.Store is not { } store) return;
        await Task.Run(() => TagManageService.Delete(store, rows.Select(t => t.Id).ToArray()));
        _tagManageSelection.Exit();
        AppService.NotifyPackRemoved();
        LoadTagManage();
    }

    private async Task MergeSelectedTagsAsync()
    {
        var rows = _tagManageVisible.Where(t => _tagManageSelection.Contains(t.Id.ToString())).ToArray();
        if (rows.Length != 2) return;
        if (!string.Equals(rows[0].Category, rows[1].Category, StringComparison.Ordinal))
        {
            if (_tagManageStatus is not null) _tagManageStatus.Text = "只能合并同一分类下的标签";
            return;
        }
        var box = new ComboBox { Header = "保留哪一个", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var row in rows)
            box.Items.Add(new ComboBoxItem { Content = $"{row.Name}（{row.Usage} 张）", Tag = row });
        box.SelectedIndex = rows[0].Usage >= rows[1].Usage ? 0 : 1;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "合并标签",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "另一个标签的图片、别名与显示名会并入保留项，被合并的名字会留成别名。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                },
            },
            PrimaryButtonText = "合并", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary ||
            (box.SelectedItem as ComboBoxItem)?.Tag is not TagManageService.TagRow winner) return;
        var loser = rows.First(r => r.Id != winner.Id);
        var planPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "tag_manage_merge.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(planPath)!);
        var plan = new[]
        {
            new
            {
                loser = new[] { loser.Category, loser.Name },
                winner = new[] { winner.Category, winner.Name },
                zh = winner.Zh,
            },
        };
        System.IO.File.WriteAllText(planPath, System.Text.Json.JsonSerializer.Serialize(plan));
        var preview = await Task.Run(() =>
        {
            using var store = new StoreService(AppPaths.DbPath);
            return TagMergeService.Run(store, planPath, apply: false);
        });
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "合并影响",
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                MaxHeight = 360, Padding = new Thickness(0, 0, 16, 0),
            },
            PrimaryButtonText = "确认合并", CloseButtonText = "取消", DefaultButton = ContentDialogButton.None,
        };
        if (await DialogService.ShowAsync(confirm) != ContentDialogResult.Primary) return;
        await Task.Run(() =>
        {
            using var store = new StoreService(AppPaths.DbPath);
            TagMergeService.Run(store, planPath, apply: true);
        });
        _tagManageSelection.Exit();
        AppService.NotifyPackRemoved();
        LoadTagManage();
    }

#if GALLERY_DEVELOPMENT
    /// <summary>
    /// 无窗口自测（沙箱）：列表/搜索/分类/零使用/详情/重复检测/增删改与别名全链路。
    /// 写操作只用临时标签，跑完自己删掉，不会在真实库里留垃圾。
    /// </summary>
    public async Task<string> VerifyTagManageAsync()
    {
        var lines = new List<string>();
        try
        {
            LoadTagManage();
            lines.Add($"all rows={_tagManageRows.Count} tabs={_tagManageTabButtons.Count}");

            _tagManageSearch!.Text = "malenia";
            LoadTagManage();
            lines.Add($"search=malenia rows={_tagManageRows.Count} names=" +
                      string.Join(",", _tagManageRows.Take(4).Select(r => r.Name)));

            _tagManageSearch.Text = "玛莲妮亚";
            LoadTagManage();
            lines.Add($"search=中文 rows={_tagManageRows.Count} names=" +
                      string.Join(",", _tagManageRows.Take(4).Select(r => r.Name)));

            _tagManageSearch.Text = "";
            _tagManageCategory = "work";
            LoadTagManage();
            lines.Add($"cat=work rows={_tagManageRows.Count} onlyWork={_tagManageRows.All(r => r.Category == "work")}");

            _tagManageCategory = "";
            _tagManageIncludeZero = true;
            LoadTagManage();
            lines.Add($"includeZero rows={_tagManageRows.Count} hasZero={_tagManageRows.Any(r => r.Usage == 0)} " +
                      $"listCount={_tagManageItems.Count}");

            var sample = _tagManageRows.FirstOrDefault(r => r.AliasCount > 0) ?? _tagManageRows.FirstOrDefault(r => r.Usage > 0);
            if (sample is not null)
            {
                var detail = TagManageService.Get(sample.Id);
                lines.Add($"detail {detail?.Name} zh={detail?.Zh} aliases={detail?.Aliases.Count} usage={detail?.Usage}");
            }

            foreach (var condition in new[] { "display", "identity", "alias", "contains" })
            {
                var groups = await Task.Run(() => TagManageService.FindDuplicates([condition]));
                var first = groups.FirstOrDefault();
                lines.Add($"dup {condition,-8} groups={groups.Count} sample=" +
                          (first is null ? "-" :
                              string.Join(" | ", first.Members.Select(m => $"{m.Name}:{m.Usage}"))));
            }
            var both = await Task.Run(() => TagManageService.FindDuplicates(["display", "identity"]));
            lines.Add($"dup display+identity groups={both.Count}");

            if (AppService.Store is { } store)
            {
                var created = await Task.Run(() => TagManageService.Create(store, "custom", "zz_verify_tmp", "验证标签", "verify"));
                lines.Add($"create id={created}");
                if (created > 0)
                {
                    var duplicate = await Task.Run(() => TagManageService.Create(store, "custom", "zz_verify_tmp", "", ""));
                    lines.Add($"create duplicate rejected={duplicate == 0}");
                    await Task.Run(() =>
                    {
                        TagManageService.Update(store, created, "artist", "验证改名", "verify-en");
                        TagManageService.SetAliases(store, created, ["v1", "v2", "v1"]);
                    });
                    var after = TagManageService.Get(created);
                    lines.Add($"after category={after?.Category} zh={after?.Zh} aliases={after?.Aliases.Count}");
                    var removed = await Task.Run(() => TagManageService.Delete(store, [created]));
                    lines.Add($"delete removed={removed} gone={TagManageService.Get(created) is null}");
                }
            }

            // 合并链路：两个临时标签各挂一张图 → 预演 → 真合并 → 核对 loser 消失、图片归 winner、名字留成别名。
            if (AppService.Store is { } mergeStore)
            {
                var a = await Task.Run(() => TagManageService.Create(mergeStore, "custom", "zz_merge_a", "合并A", ""));
                var b = await Task.Run(() => TagManageService.Create(mergeStore, "custom", "zz_merge_b", "合并B", ""));
                if (a > 0 && b > 0)
                {
                    await Task.Run(() =>
                    {
                        mergeStore.Exec($"INSERT OR IGNORE INTO image_tags(image_id,tag_id) " +
                                        $"SELECT id,{a} FROM images ORDER BY id LIMIT 1");
                        mergeStore.Exec($"INSERT OR IGNORE INTO image_tags(image_id,tag_id) " +
                                        $"SELECT id,{b} FROM images ORDER BY id DESC LIMIT 1");
                    });
                    var planPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "tag_manage_verify_merge.json");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(planPath)!);
                    System.IO.File.WriteAllText(planPath, System.Text.Json.JsonSerializer.Serialize(new[]
                    {
                        new { loser = new[] { "custom", "zz_merge_b" }, winner = new[] { "custom", "zz_merge_a" }, zh = "合并A" },
                    }));
                    var previewText = await Task.Run(() =>
                    {
                        using var store2 = new StoreService(AppPaths.DbPath);
                        return TagMergeService.Run(store2, planPath, apply: false);
                    });
                    lines.Add($"merge preview lines={previewText.Split('\n').Length} hasLoser={previewText.Contains("zz_merge_b")}");
                    var appliedText = await Task.Run(() =>
                    {
                        using var store2 = new StoreService(AppPaths.DbPath);
                        return TagMergeService.Run(store2, planPath, apply: true);
                    });
                    var merged = TagManageService.Get(a);
                    lines.Add($"merge applied loserGone={TagManageService.Get(b) is null} winnerUsage={merged?.Usage} " +
                              $"aliasi={string.Join(",", merged?.Aliases ?? [])} reportOk={appliedText.Contains("已合并")}");
                    await Task.Run(() => TagManageService.Delete(mergeStore, [a]));
                    lines.Add($"merge cleanup gone={TagManageService.Get(a) is null}");
                }
            }

            // 点一行会不会真的弹出详情：用同一入口调一次，看 DialogService 是否认为有弹窗。
            var dialogTarget = _tagManageRows.FirstOrDefault(r => r.Usage > 0);
            if (dialogTarget is not null && XamlRoot is not null)
            {
                var detail = TagManageService.Get(dialogTarget.Id);
                if (detail is not null)
                {
                    var dialog = new TagManageDialog(XamlRoot, detail);
                    _ = DialogService.ShowAsync(dialog);
                    await Task.Delay(700);
                    lines.Add($"dialog opened={DialogService.IsShowing} title={dialog.Title}");
                    dialog.Hide();
                    for (var i = 0; i < 20 && DialogService.IsShowing; i++) await Task.Delay(100);
                    lines.Add($"dialog closed={!DialogService.IsShowing}");
                }
            }
            lines.Add("done");
        }
        catch (Exception ex) { lines.Add("VERIFY_ERR " + ex); }
        return string.Join("\n", lines);
    }
#endif
}
