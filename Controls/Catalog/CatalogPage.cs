using Gallery.Models;
using Gallery.Views;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Numerics;
using Gallery.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Gallery.Controls.Catalog;

public sealed class CatalogPage : Grid
{
    private readonly TagCatalogService _catalog;
    private readonly SearchInputBox _search = new() { PreventAutomaticFocus = true };
    private readonly ListView _list = new();
    private readonly GridView _grid = new();
    private readonly AppBarButton _view = new() { Label = "视图", LabelPosition = CommandBarLabelPosition.Collapsed };
    private readonly AppBarButton _select = new()
    {
        Label = "选择",
        Icon = new FontIcon
        {
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["PhotosUWPFontFamily"],
            Glyph = "\uE73A",
        },
    };
    private readonly AppBarButton _deleteSelected = new() { Label = "删除", Icon = new FontIcon { Glyph = (string)Application.Current.Resources["DeleteGlyph"] }, Visibility = Visibility.Collapsed };
    private readonly TextBlock _bannerTitle = Text("资料库", 28, true);
    private bool _selectionMode;
    private readonly HashSet<string> _selectedKeys = new(StringComparer.Ordinal);
    private string? _selectionAnchor;
    private readonly FontIcon _viewIcon = new() { FontSize = 16 };
    private readonly RadioMenuFlyoutItem _listMode = new() { Text = "列表", GroupName = "CatalogView" };
    private readonly RadioMenuFlyoutItem _gridMode = new() { Text = "网格", GroupName = "CatalogView" };
    private readonly TextBlock _empty = Text("", 14, false, .68);
    private readonly Grid _detailHost = new();
    private Grid _banner = null!;
    private readonly StackPanel _detail = new() { Spacing = 24 };
    private StackPanel? _linkedTags;
    // 标签折叠区里的缩略图对账队列：每个面板各自去重，避免多标签同时展开时互相压制。
    private readonly HashSet<PhotoGalleryPanel> _tagThumbnailPending = [];
    private int _tagRefreshRevision;

    private sealed class LinkedTag(int id)
    {
        public int Id { get; } = id;
        public int MediaRevision { get; set; }
    }
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
        Padding = new(0, 0, 16, 0),
    };
    private readonly Button _worksTab = new() { Content = "作品库" };
    private readonly Button _peopleTab = new() { Content = "角色库" };
    private readonly DropDownButton _create = (DropDownButton)XamlReader.Load("""
        <DropDownButton xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        Content="创建" Background="{ThemeResource AccentButtonBackground}"
                        Foreground="{ThemeResource AccentButtonForeground}"
                        BorderBrush="{ThemeResource AccentButtonBorderBrush}" BackgroundSizing="OuterBorderEdge">
            <DropDownButton.Resources>
                <StaticResource x:Key="ButtonBackgroundPointerOver" ResourceKey="AccentButtonBackgroundPointerOver"/>
                <StaticResource x:Key="ButtonBackgroundPressed" ResourceKey="AccentButtonBackgroundPressed"/>
                <StaticResource x:Key="ButtonBorderBrushPointerOver" ResourceKey="AccentButtonBorderBrushPointerOver"/>
                <StaticResource x:Key="ButtonBorderBrushPressed" ResourceKey="AccentButtonBorderBrushPressed"/>
                <StaticResource x:Key="ButtonForegroundPointerOver" ResourceKey="AccentButtonForegroundPointerOver"/>
                <StaticResource x:Key="ButtonForegroundPressed" ResourceKey="AccentButtonForegroundPressed"/>
                <StaticResource x:Key="DropDownButtonForegroundSecondary" ResourceKey="AccentButtonForeground"/>
                <StaticResource x:Key="DropDownButtonForegroundSecondaryPointerOver" ResourceKey="AccentButtonForegroundPointerOver"/>
                <StaticResource x:Key="DropDownButtonForegroundSecondaryPressed" ResourceKey="AccentButtonForegroundPressed"/>
            </DropDownButton.Resources>
        </DropDownButton>
        """);
    private readonly AppBarButton _sort = new()
    {
        Label = "排序选项和方向", LabelPosition = CommandBarLabelPosition.Collapsed,
        Icon = new FontIcon { Glyph = (string)Application.Current.Resources["SortGlyph"], FontSize = 16 },
    };
    private readonly Grid _body;
    private Surface? _surface;
    private Func<Task>? _popup;
    private readonly Dictionary<string, CatalogRelationCard> _cards = new(StringComparer.Ordinal);
    private TagCatalogService.LibrarySnapshot? _snapshot;
    private ILookup<string, string>? _workRoster;
    private Dictionary<string, TagCatalogService.LibraryEntry> _workIndex = [];
    private Dictionary<string, TagCatalogService.LibraryEntry> _peopleIndex = [];
    private string _kind = "work";
    private string? _key;
    private bool _selecting;
    private bool _nameDescending;
    private bool _useGrid;
    private int _refreshRevision;
    private bool _refreshInFlight, _renderSnapshotOnShow;
    private IndexRow? _pendingHighlight;
    private SelectorItem? _pulsingContainer;
    private UIElement? _pulsingLayer;
    private Storyboard? _cardPulse;
    // 淡出终点留 0.01 而非 0：Opacity 归零后 WinUI 合成层停止绘制，再设值不会显示。
    private const double FeedbackIdleOpacity = 0.01;
    private readonly Dictionary<(string Kind, string Key), Task<BitmapImage?>> _coverThumbnails = [];
    private readonly Dictionary<(string Kind, string Key), Task<BitmapImage?>> _coverImages = [];
    private TextBlock? _heading;
    private Button? _edit;
    private readonly ContentControl _editSlot = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private Grid? _cardGrid;
    private int _cardColumns;
    // 详情关系区：局部搜索 + 通用选择模式，宿主无关状态机由 LocalSelectionController 管。
    private readonly LocalSelectionController _relationSelection = new();
    private SearchInputBox? _relationSearch;
    private Button? _relationSelect;
    private Button? _relationDelete;
    private Button? _relationAdd;
    private TagCatalogService.LibraryEntry[] _relationItems = [];
    // 关联图库标签区不做批量选择：解除关联走每个标签自身的右键菜单。
    private SearchInputBox? _tagSearch;

    public event Action? Changed;
    public event Action<string, ToastKind, string?>? ToastRequested;
    public event Action<GalleryPage.PreviewRequest>? PreviewRequested;
    public Func<PhotoItem, bool, Task<bool>>? ToggleFavorite { get; set; }
    public bool DetailOpen => _key is not null;
    public bool Editing => _surface is not null;
    public event Action<Location, Location>? Navigated;
    public event Func<int, bool>? HistoryRequested;
    public event Action<int, string>? TagFilterRequested;
    public event Action<string>? TagCopied;
    public bool HasHistoryPopup => _popup is not null;

    public sealed record Surface(FrameworkElement Content, Control Focus);
    public sealed record Location(string Kind, string? Key, string Query, double Offset, double IndexOffset,
        string? Card, Surface? Surface, Func<Task>? Popup, bool GridView);

    public sealed record IndexRow(string Key, string Name, string Subtitle, string Type, string Icon, string Kind, bool HasCover);
    public sealed record Choice<T>(T Value, string Label);

    public CatalogPage(TagCatalogService catalog)
    {
        _catalog = catalog;
        IsTabStop = true;
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });

        var banner = _banner = new Grid { Margin = new(32, 16, 32, 8), ColumnSpacing = 12 };
        banner.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        banner.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        banner.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        banner.Children.Add(_bannerTitle);
        _search.MaxWidth = 260;
        _search.DefaultFocusAnchor = this;
        _search.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_search, 1);
        banner.Children.Add(_search);
        // 搜索只过滤视图：不清选区、不退出选择模式。用户可跨多次搜索累积选择。
        _search.TextChanged += (_, _) => { if (!_selecting) RenderIndex(); };

        _create.VerticalAlignment = VerticalAlignment.Center;
        var createMenu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        foreach (var (kind, label) in new[] { ("work", "新建作品"), ("character", "新建角色") })
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += async (_, _) => await Run(() => Edit(null, kind));
            createMenu.Items.Add(item);
        }
        _create.Flyout = createMenu;
        Grid.SetColumn(_create, 2);
        banner.Children.Add(_create);
        Children.Add(banner);

        var body = _body = new Grid { Margin = new(32, 0, 0, 16) };
        body.SizeChanged += (_, _) => SnapIndexHeight();
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });

        var toolbar = new Grid { Margin = new(0, 0, 32, 0), ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var tabs = Horizontal();
        _worksTab.Click += (_, _) => SelectTab("work");
        _peopleTab.Click += (_, _) => SelectTab("character");
        var radius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
        _worksTab.CornerRadius = radius;
        _peopleTab.CornerRadius = radius;
        tabs.Children.Add(_worksTab);
        tabs.Children.Add(_peopleTab);
        toolbar.Children.Add(tabs);

        var tools = new CustomCommandBar
        {
            Padding = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new(0), ClosedDisplayMode = AppBarClosedDisplayMode.Compact,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            IsDynamicOverflowEnabled = false, OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed,
        };

        _view.Icon = _viewIcon;
        _listMode.Icon = new FontIcon { Glyph = "\uE8FD" };
        _gridMode.Icon = PhotosIcon("GridLayoutGlyph");
        _grid.Resources["GridViewItemCornerRadius"] = new CornerRadius(8);
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _grid.Resources["GridViewItemBackgroundPointerOver"] = transparent;
        _grid.Resources["GridViewItemBackgroundPressed"] = transparent;
        _grid.Resources["GridViewItemPointerOverBorderBrush"] = transparent;
        _list.Resources["ListViewItemBackgroundPointerOver"] = transparent;
        _listMode.Click += (_, _) => SetView(false);
        _gridMode.Click += (_, _) => SetView(true);
        var viewMenu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        viewMenu.Items.Add(_listMode);
        viewMenu.Items.Add(_gridMode);
        _view.Flyout = viewMenu;
        _view.Resources["AppBarButtonHasFlyoutChevronVisibility"] = Visibility.Visible;
        _sort.Resources["AppBarButtonHasFlyoutChevronVisibility"] = Visibility.Visible;
        _sort.Flyout = BuildSortMenu();
        AutomationProperties.SetName(_sort, "资料库排序");
        // 与图库 SelectButton 内联 ThemeDictionaries 逐项一致：常驻灰底 + 描边，悬停/按下走同栏灰阶。
        // 选择模式只改文字/图标，不改底色——文字变成「取消选择」本身就是状态提示。
        _select.Resources = (ResourceDictionary)XamlReader.Load("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ResourceDictionary.ThemeDictionaries>
                <ResourceDictionary x:Key="Default">
                  <StaticResource x:Key="AppBarButtonBackground" ResourceKey="ControlFillColorDefaultBrush"/>
                  <StaticResource x:Key="AppBarButtonBackgroundPointerOver" ResourceKey="ControlFillColorSecondaryBrush"/>
                  <StaticResource x:Key="AppBarButtonBackgroundPressed" ResourceKey="ControlFillColorTertiaryBrush"/>
                  <StaticResource x:Key="AppBarButtonBackgroundDisabled" ResourceKey="ControlFillColorDisabledBrush"/>
                  <StaticResource x:Key="AppBarButtonForeground" ResourceKey="TextFillColorPrimaryBrush"/>
                  <StaticResource x:Key="AppBarButtonForegroundPointerOver" ResourceKey="TextFillColorPrimaryBrush"/>
                  <StaticResource x:Key="AppBarButtonForegroundPressed" ResourceKey="TextFillColorSecondaryBrush"/>
                  <StaticResource x:Key="AppBarButtonForegroundDisabled" ResourceKey="TextFillColorDisabledBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrush" ResourceKey="ControlElevationBorderBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrushPointerOver" ResourceKey="ControlElevationBorderBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrushPressed" ResourceKey="ControlStrokeColorDefaultBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrushDisabled" ResourceKey="ControlStrokeColorDefaultBrush"/>
                </ResourceDictionary>
                <ResourceDictionary x:Key="Light">
                  <StaticResource x:Key="AppBarButtonBackground" ResourceKey="ControlFillColorDefaultBrush"/>
                  <StaticResource x:Key="AppBarButtonBackgroundPointerOver" ResourceKey="ControlFillColorSecondaryBrush"/>
                  <StaticResource x:Key="AppBarButtonBackgroundPressed" ResourceKey="ControlFillColorTertiaryBrush"/>
                  <StaticResource x:Key="AppBarButtonBackgroundDisabled" ResourceKey="ControlFillColorDisabledBrush"/>
                  <StaticResource x:Key="AppBarButtonForeground" ResourceKey="TextFillColorPrimaryBrush"/>
                  <StaticResource x:Key="AppBarButtonForegroundPointerOver" ResourceKey="TextFillColorPrimaryBrush"/>
                  <StaticResource x:Key="AppBarButtonForegroundPressed" ResourceKey="TextFillColorSecondaryBrush"/>
                  <StaticResource x:Key="AppBarButtonForegroundDisabled" ResourceKey="TextFillColorDisabledBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrush" ResourceKey="ControlElevationBorderBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrushPointerOver" ResourceKey="ControlElevationBorderBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrushPressed" ResourceKey="ControlStrokeColorDefaultBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrushDisabled" ResourceKey="ControlStrokeColorDefaultBrush"/>
                </ResourceDictionary>
                <ResourceDictionary x:Key="HighContrast">
                  <StaticResource x:Key="AppBarButtonBackground" ResourceKey="SystemControlBackgroundBaseLowBrush"/>
                  <StaticResource x:Key="AppBarButtonBackgroundPointerOver" ResourceKey="SystemColorHighlightTextColorBrush"/>
                  <StaticResource x:Key="AppBarButtonBackgroundPressed" ResourceKey="SystemColorHighlightTextColorBrush"/>
                  <StaticResource x:Key="AppBarButtonBackgroundDisabled" ResourceKey="SystemControlBackgroundBaseLowBrush"/>
                  <StaticResource x:Key="AppBarButtonForeground" ResourceKey="SystemColorButtonTextColorBrush"/>
                  <StaticResource x:Key="AppBarButtonForegroundPointerOver" ResourceKey="SystemControlHighlightBaseHighBrush"/>
                  <StaticResource x:Key="AppBarButtonForegroundPressed" ResourceKey="SystemControlHighlightBaseHighBrush"/>
                  <StaticResource x:Key="AppBarButtonForegroundDisabled" ResourceKey="SystemControlDisabledBaseMediumLowBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrush" ResourceKey="SystemControlForegroundTransparentBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrushPointerOver" ResourceKey="SystemColorHighlightColorBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrushPressed" ResourceKey="SystemColorHighlightTextColorBrush"/>
                  <StaticResource x:Key="AppBarButtonBorderBrushDisabled" ResourceKey="SystemControlDisabledTransparentBrush"/>
                </ResourceDictionary>
              </ResourceDictionary.ThemeDictionaries>
            </ResourceDictionary>
            """);
        _select.Click += (_, _) => SetSelectionMode(!_selectionMode);
        _deleteSelected.Click += async (_, _) => await DeleteSelected();
        tools.PrimaryCommands.Add(_select);
        tools.PrimaryCommands.Add(_deleteSelected);
        tools.PrimaryCommands.Add(_view);
        tools.PrimaryCommands.Add(_sort);
        Grid.SetColumn(tools, 1);
        toolbar.Children.Add(tools);
        body.Children.Add(toolbar);

        var divider = new Border { Height = 1, Margin = new(0, 8, 32, 0),
            Background = BrushFor("ControlStrokeColorDefaultBrush") };
        Grid.SetRow(divider, 1);
        body.Children.Add(divider);

        ConfigureIndex(_list, ListTemplate, typeof(ListViewItem));
        ConfigureIndex(_grid, GridTemplate, typeof(GridViewItem));
        _grid.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <ItemsWrapGrid Orientation="Horizontal" />
            </ItemsPanelTemplate>
            """);
        _grid.Loaded += (_, _) => ResizeGridCells();
        _grid.SizeChanged += (_, _) => ResizeGridCells();
        Grid.SetRow(_empty, 2);
        _empty.Margin = new(0, 16, 48, 0);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.IsHitTestVisible = false;
        body.Children.Add(_empty);

        _scroll.Margin = new(32, 0, 0, 16);
        _scroll.Padding = new(0, 0, 48, 48);
        _scroll.Content = _detail;
        _scroll.SizeChanged += (_, _) => LayoutCards();
        // 滚动时对展开的标签媒体重新对账：Repeater 只在视口扫过时才实现瓦片，
        // 不挂这一处，展开后停在原地就永远没有瓦片可请求。
        _scroll.ViewChanged += (_, _) => ResumeMediaThumbnails();
        // 详情滚动条恢复 WinUI 原生线性行为，避开全局 ContinuousScrollBar 的缓动与吸附。
        _scroll.Resources[typeof(ScrollBar)] = new Style(typeof(ScrollBar))
        {
            BasedOn = (Style)Application.Current.Resources["DefaultScrollBarStyle"],
        };
        Grid.SetRow(body, 1);
        Children.Add(body);
        _detailHost.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _detailHost.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var back = ActionButton("返回", Back);
        back.Margin = new(32, 16, 16, 24);
        _detailHost.Children.Add(back);
        Grid.SetRow(_scroll, 1);
        _detailHost.Children.Add(_scroll);
        Grid.SetRow(_detailHost, 1);
        Children.Add(_detailHost);
        RenderIndex();
        ShowDetail();

        SizeChanged += (_, args) => _search.Width = Math.Clamp((args.NewSize.Width - 64) * .34, 125, 260);
        Loaded += (_, _) => QueueVisibleRefresh();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => QueueVisibleRefresh());
    }

    private ListViewBase ActiveIndex => _useGrid ? _grid : _list;

    private static FontIcon PhotosIcon(string glyph) => new()
    {
        FontFamily = (FontFamily)Application.Current.Resources["PhotosUWPFontFamily"],
        Glyph = (string)Application.Current.Resources[glyph],
    };

    private void ConfigureIndex(ListViewBase view, string template, Type itemType)
    {
        view.Padding = new(0, 0, 48, 0);
        view.SelectionMode = ListViewSelectionMode.None;
        view.IsItemClickEnabled = true;
        view.ItemTemplate = (DataTemplate)XamlReader.Load(template);
        var style = new Style(itemType);
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        if (view is GridView)
        {
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4)));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        }
        style.Setters.Add(new Setter(Control.UseSystemFocusVisualsProperty, true));
        view.ItemContainerStyle = style;
        view.ContainerContentChanging += (sender, args) =>
        {
            var container = args.ItemContainer;
            if (args.InRecycleQueue)
            {
                SetIndexHover(container, false);
                if (container.ContentTemplateRoot is FrameworkElement recycled)
                {
                    if (recycled.FindName("SelectionCheck") is CheckBox recycledCheck)
                    {
                        recycledCheck.Visibility = Visibility.Collapsed;
                        recycledCheck.IsChecked = false;
                    }
                    ResetIndexFeedback(recycled);
                }
                if (ReferenceEquals(container, _pulsingContainer)) StopCardPulse();
                return;
            }
            if (args.Item is not IndexRow row) return;
            UpdateIndexSelection(container, row);
            if (ReferenceEquals(_pendingHighlight, row))
                DispatcherQueue.TryEnqueue(() => HighlightCreatedEntry(view, row));
            if (args.Phase == 0)
            {
                if (container.Tag is null)
                {
                    container.Tag = true;
                    container.PointerEntered += (_, _) => SetIndexHover(container, true);
                    container.PointerExited += (_, _) => SetIndexHover(container, false);
                    container.PointerCanceled += (_, _) => SetIndexHover(container, false);
                }
                // 与图库 WireTile 等价：模板就绪后再灌一次权威状态，补上 Phase 0 时 FindName 尚未命中的容器。
                args.RegisterUpdateCallback(1, (_, update) =>
                {
                    if (update.Item is IndexRow ready) UpdateIndexSelection(update.ItemContainer, ready);
                    if (update.ItemContainer.ContentTemplateRoot is FrameworkElement readyCard)
                        ResetIndexFeedback(readyCard);
                    PrepareIndexCover(update);
                });
            }
        };
        view.ContextRequested += (_, args) =>
        {
            for (var source = args.OriginalSource as DependencyObject; source is not null && source != view;
                 source = VisualTreeHelper.GetParent(source))
            {
                if (source is not SelectorItem { Content: IndexRow row } container ||
                    !Entries(row.Kind).ContainsKey(row.Key)) continue;
                if (!_selectionMode) SetSelectionMode(true);
                if (!_selectedKeys.Contains(row.Key))
                {
                    _selectedKeys.Clear();
                    _selectedKeys.Add(row.Key);
                    _selectionAnchor = row.Key;
                    UpdateSelectionUi();
                }
                var menu = new MenuFlyout { Items = { DestructiveItem("删除资料", DeleteSelected) } };
                if (args.TryGetPosition(container, out var position))
                    menu.ShowAt(container, new FlyoutShowOptions { Position = position });
                else menu.ShowAt(container);
                args.Handled = true;
                break;
            }
        };
        ScrollViewer.SetVerticalScrollBarVisibility(view, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(view, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(view, ScrollBarVisibility.Disabled);
        view.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not IndexRow row) return;
            bool ctrl = IsSelectionKeyDown(Windows.System.VirtualKey.Control), shift = IsSelectionKeyDown(Windows.System.VirtualKey.Shift);
            if (_selectionMode || ctrl || shift)
                SelectIndexRow(view, row, ctrl, shift);
            else Navigate(_kind, row.Key, _search.Text, sourceCard: row.Key);
        };
        view.KeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.A && IsSelectionKeyDown(Windows.System.VirtualKey.Control))
            {
                SetSelectionMode(true);
                foreach (var row in view.Items.OfType<IndexRow>()) _selectedKeys.Add(row.Key);
                UpdateSelectionUi();
                args.Handled = true;
            }
            else if (args.Key == Windows.System.VirtualKey.Delete && _selectedKeys.Count > 0)
            {
                args.Handled = true;
                await DeleteSelected();
            }
        };
        Grid.SetRow(view, 2);
        _body.Children.Add(view);
    }

    private static bool IsSelectionKeyDown(Windows.System.VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource
        .GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void SelectIndexRow(ListViewBase view, IndexRow row, bool ctrl, bool shift)
    {
        if (!_selectionMode) SetSelectionMode(true);
        if (shift && _selectionAnchor is { } anchor)
        {
            var rows = view.Items.OfType<IndexRow>().ToList();
            int first = rows.FindIndex(item => item.Key == anchor), last = rows.FindIndex(item => item.Key == row.Key);
            if (first >= 0 && last >= 0)
            {
                if (!ctrl) _selectedKeys.Clear();
                for (int i = Math.Min(first, last); i <= Math.Max(first, last); i++) _selectedKeys.Add(rows[i].Key);
            }
        }
        else
        {
            if (!_selectedKeys.Add(row.Key)) _selectedKeys.Remove(row.Key);
            _selectionAnchor = row.Key;
        }
        UpdateSelectionUi();
    }

    private void UpdateIndexSelection(SelectorItem container, IndexRow row)
    {
        if (container.ContentTemplateRoot is FrameworkElement root && root.FindName("SelectionCheck") is CheckBox check)
        {
            check.Visibility = _selectionMode ? Visibility.Visible : Visibility.Collapsed;
            check.IsChecked = _selectedKeys.Contains(row.Key);
        }
        AutomationProperties.SetName(container, _selectionMode
            ? $"{row.Name}，{(_selectedKeys.Contains(row.Key) ? "已选择" : "未选择")}"
            : row.Name);
    }

    private static void SetIndexHover(SelectorItem container, bool active)
    {
        if (container.ContentTemplateRoot is FrameworkElement card && card.FindName("HoverLayer") is UIElement hover)
            hover.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    // 与图库 PhotoTile.Recycle/Bind 一致：容器回收与复用时显式复位强调色反馈层。
    // WinUI 的 Storyboard.Stop 不保证把中途打断的 opacity 还原，残留会随容器复用变成蓝绿色影子。
    private static void ResetIndexFeedback(FrameworkElement card)
    {
        if (card.FindName("FeedbackLayer") is UIElement feedback) feedback.Opacity = FeedbackIdleOpacity;
    }

    private void PrepareIndexCover(ContainerContentChangingEventArgs args)
    {
        var container = args.ItemContainer;
        if (args.Item is not IndexRow row || container.ContentTemplateRoot is not FrameworkElement root ||
            root.FindName("CoverLayer") is not Border cover) return;
        cover.Background = null;
        cover.Visibility = Visibility.Collapsed;
        if (row.HasCover)
            _ = LoadCover(cover, row.Kind, row.Key, () => ReferenceEquals(container.Content, row), thumbnail: true);
    }

    private const string ListTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid MinHeight="56" Padding="12,6,12,7"
                BorderThickness="0,0,0,1" BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto"/>
              <ColumnDefinition Width="Auto"/>
              <ColumnDefinition Width="*"/>
              <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <Border x:Name="CoverLayer" Grid.ColumnSpan="4" Margin="-12,-6,-12,-7" Visibility="Collapsed"
                    CornerRadius="6" IsHitTestVisible="False">
              <Border Background="#AA141414" CornerRadius="6"/>
            </Border>
            <Border x:Name="HoverLayer" Grid.ColumnSpan="4" Margin="-12,-6,-12,-7" Visibility="Collapsed" CornerRadius="6"
                    Background="{ThemeResource SubtleFillColorSecondaryBrush}" IsHitTestVisible="False"/>
            <Border x:Name="FeedbackLayer" Grid.ColumnSpan="4" Margin="-12,-6,-12,-7" Opacity="0.01" CornerRadius="6"
                    Background="{ThemeResource AccentFillColorDefaultBrush}" IsHitTestVisible="False"/>
            <CheckBox x:Name="SelectionCheck" Visibility="Collapsed" IsHitTestVisible="False" IsTabStop="False"
                      MinWidth="0" Padding="0" Margin="0,0,14,0" VerticalAlignment="Center"/>
            <Border Grid.Column="1" Width="32" Height="32" CornerRadius="6" VerticalAlignment="Center"
                    Background="{ThemeResource ControlFillColorSecondaryBrush}">
              <FontIcon Glyph="{Binding Icon}" FontSize="15"/>
            </Border>
            <StackPanel Grid.Column="2" Spacing="2" Margin="14,0,0,0" VerticalAlignment="Center">
              <TextBlock Text="{Binding Name}" FontSize="14" TextTrimming="CharacterEllipsis"/>
              <TextBlock Text="{Binding Subtitle}" FontSize="12" Opacity=".68" TextTrimming="CharacterEllipsis"/>
            </StackPanel>
            <TextBlock Grid.Column="3" Text="{Binding Type}" Margin="14,0,10,0" Opacity=".72" VerticalAlignment="Center"/>
          </Grid>
        </DataTemplate>
        """;

    private const string GridTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid Padding="12" MinHeight="80" RowSpacing="3"
                Background="{ThemeResource ControlFillColorSecondaryBrush}"
                BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="8">
            <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <Border x:Name="CoverLayer" Grid.RowSpan="3" Grid.ColumnSpan="2" Margin="-12" Visibility="Collapsed"
                    CornerRadius="8" IsHitTestVisible="False">
              <Border Background="#AA141414" CornerRadius="8"/>
            </Border>
            <Border x:Name="HoverLayer" Grid.RowSpan="3" Grid.ColumnSpan="2" Margin="-12" Visibility="Collapsed" CornerRadius="8"
                    Background="{ThemeResource SubtleFillColorSecondaryBrush}" IsHitTestVisible="False"/>
            <Border x:Name="FeedbackLayer" Grid.RowSpan="3" Grid.ColumnSpan="2" Margin="-12" Opacity="0.01" CornerRadius="8"
                    Background="{ThemeResource AccentFillColorDefaultBrush}" IsHitTestVisible="False"/>
            <CheckBox x:Name="SelectionCheck" Grid.RowSpan="3" Visibility="Collapsed" IsHitTestVisible="False"
                      IsTabStop="False" MinWidth="0" Padding="0" Margin="0,0,10,0" VerticalAlignment="Center"/>
            <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
              <FontIcon Glyph="{Binding Icon}" FontSize="13" Opacity=".72"/>
              <TextBlock Text="{Binding Type}" FontSize="12" Opacity=".72"/>
            </StackPanel>
            <TextBlock Grid.Column="1" Grid.Row="1" Text="{Binding Name}" FontSize="15" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
            <TextBlock Grid.Column="1" Grid.Row="2" Text="{Binding Subtitle}" FontSize="12" Opacity=".68" TextTrimming="CharacterEllipsis"/>
          </Grid>
        </DataTemplate>
        """;

    /// <summary>
    /// 列表／网格底部露出半行：容器高度取「n 行 + 0.5 行」，让最下一行只显示一半，
    /// 用户不用滚动就知道下方还有条目。内容不足一屏时保持拉伸，不制造半行。
    /// </summary>
    private void SnapIndexHeight()
    {
        if (_body.RowDefinitions.Count < 3) return;
        var available = _body.RowDefinitions[2].ActualHeight;
        if (available <= 0) return;
        var index = ActiveIndex;
        var pitch = _useGrid ? 94d : 56d;
        var count = index.ItemsSource is IEnumerable<IndexRow> source ? source.Count() : 0;
        var columns = 1;
        if (_useGrid)
        {
            var width = Math.Max(1, _grid.ActualWidth - _grid.Padding.Right);
            columns = Math.Max(1, (int)(width / 210));
        }
        var contentRows = (int)Math.Ceiling(count / (double)columns);
        var fitRows = (int)Math.Floor(available / pitch);
        if (fitRows < 1) return;
        if (contentRows <= fitRows)
        {
            index.Height = double.NaN;
            index.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }
        var height = fitRows * pitch + pitch / 2;
        if (height > available) height = (fitRows - 1) * pitch + pitch / 2;
        index.Height = Math.Max(pitch, height);
        index.VerticalAlignment = VerticalAlignment.Top;
    }

    private void ResizeGridCells()
    {
        if (_grid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        var width = Math.Max(1, _grid.ActualWidth - _grid.Padding.Right);
        var columns = Math.Max(1, (int)(width / 210));
        panel.ItemWidth = Math.Floor(width / columns);
        panel.ItemHeight = 94;
        SnapIndexHeight();
    }

    private void SetView(bool grid)
    {
        if (_useGrid == grid) return;
        _useGrid = grid;
        RenderIndex();
        if (FindScroll(ActiveIndex) is { } scroll) RestoreScroll(scroll, 0);
    }

    private void SetSelectionMode(bool enabled)
    {
        _selectionMode = enabled;
        if (!enabled)
        {
            _selectedKeys.Clear();
            _selectionAnchor = null;
        }
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        var count = _selectedKeys.Count;
        // 文字/图标跟「是否在选择模式」走，不跟「选了几项」走：
        // 刚进入选择模式时一项未选，若按钮毫无变化，用户会以为没生效，
        // 接着点卡片发现变成了勾选（2026-11-25 反馈）。底色不变，只改文字。
        var active = _selectionMode;
        _bannerTitle.Text = count > 0 ? $"已选择 {count} 个项目" : "资料库";
        _select.Label = active ? "取消选择" : "选择";
        ((FontIcon)_select.Icon).Glyph = active ? "\uE711" : "\uE73A";
        _deleteSelected.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _create.Visibility = _key is null && count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 按数据项取容器，不依赖 ItemsPanelRoot.Children 当时的实现状态；
        // 尚未实现的容器由 ContainerContentChanging 的 Phase 0/1 回调灌入同一权威状态。
        var index = ActiveIndex;
        foreach (var row in index.Items.OfType<IndexRow>())
            if (index.ContainerFromItem(row) is SelectorItem container)
                UpdateIndexSelection(container, row);
    }

    public bool ExitSelection()
    {
        if (!_selectionMode) return false;
        SetSelectionMode(false);
        return true;
    }

#if GALLERY_DEVELOPMENT
    private Action? _pickerEscapeForVerification;

    /// <summary>无窗口自测：走一次 Pick 弹窗的 Esc 关闭路径。</summary>
    public void RaisePickerEscapeForVerification() => _pickerEscapeForVerification?.Invoke();

    /// <summary>
    /// 无窗口自测（只读）：详情关系区的勾选框与「选择」按钮状态。
    /// 不写库——写库会在沙箱副本上撞 SQLite 锁。
    /// </summary>
    public async System.Threading.Tasks.Task<string> VerifyRelationCheckAsync()
    {
        var lines = new List<string>();
        try
        {
            await RefreshAsync();
            var works = Source("work").ToArray();
            lines.Add($"works={works.Length} chars={Source("character").Count()} " +
                $"maxRel={(works.Length == 0 ? 0 : works.Max(w => Related(w).Count))} " +
                $"roster={( _workRoster is null ? "null" : _workRoster.Count.ToString())} " +
                $"appear={(_snapshot?.Appearances.Count ?? -1)}");
            var work = works.FirstOrDefault(w => Related(w).Count >= 2) ?? works.FirstOrDefault();
            if (work is null) return "NO_WORK";
            Navigate(work.Kind, work.Key);
            UpdateLayout();
            var cards = _cardGrid?.Children.OfType<CatalogRelationCard>().ToArray() ?? [];
            lines.Add($"cards={cards.Length} boxes={cards.Count(c => c.SelectMode)} ticked={cards.Count(c => c.Selected)}");

            // 外层工具栏：进入选择模式即换强调色 + 文字，即使一项未选。
            SetSelectionMode(true);
            lines.Add($"outer label={_select.Label} glyph={((FontIcon)_select.Icon).Glyph}");
            SetSelectionMode(false);
            lines.Add($"outer off label={_select.Label} glyph={((FontIcon)_select.Icon).Glyph}");

            // 详情关系区：进入后所有框常显但都为空，点两张才出两个勾。
            _relationSelection.Enter();
            lines.Add($"enter boxes={cards.Count(c => c.SelectMode)} ticked={cards.Count(c => c.Selected)}");
            var order = _relationItems.Select(x => x.Key).ToArray();
            foreach (var item in _relationItems.Take(2)) _relationSelection.Toggle(item.Key, false, false, order);
            lines.Add($"pick2 ticked={cards.Count(c => c.Selected)} expect=2");
            _relationSelection.Toggle(_relationItems[0].Key, false, false, order);
            lines.Add($"untoggle1 ticked={cards.Count(c => c.Selected)} expect=1");
            _relationSelection.Exit();
            lines.Add($"exit boxes={cards.Count(c => c.SelectMode)} ticked={cards.Count(c => c.Selected)}");
        }
        catch (Exception ex) { lines.Add("VERIFY_ERR " + ex); }
        return string.Join("\n", lines);
    }

    /// <summary>无窗口自测：打开「添加角色」弹窗，用于验证 Esc 关闭。</summary>
    public void OpenAddRelationForVerification()
    {
        if (Source("work").FirstOrDefault() is not { } work) return;
        Navigate(work.Kind, work.Key);
        if (Entries("work").GetValueOrDefault(work.Key) is { } current) _ = AddRelation(current);
    }

    /// <summary>无窗口自测：打开当前库第一条资料，触发完整 ShowDetail 路径。</summary>
    public async System.Threading.Tasks.Task OpenFirstEntryForVerification()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "verify_catalog_detail.txt");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            try
            {
                var direct = await Task.Run(_catalog.ReadLibrary);
                System.IO.File.WriteAllText(path, $"DIRECT works={direct.Works.Count} chars={direct.Characters.Count}");
            }
            catch (Exception ex) { System.IO.File.WriteAllText(path, "DIRECT_ERR " + ex); }
            await RefreshAsync();
            var first = Source(_kind).FirstOrDefault();
            if (first is null)
            {
                System.IO.File.AppendAllText(path, $"\nNO_ENTRY kind={_kind} snapshot={(_snapshot is null ? "null" : _snapshot.Works.Count + "+" + _snapshot.Characters.Count)}");
                return;
            }
            var lines = new List<string> { $"OK kind={first.Kind} key={first.Key}" };
            // 覆盖：作品详情、角色详情、选择模式、搜索过滤、退出详情
            var all = Source("work").Concat(Source("character")).Take(6).ToArray();
            var tagMediaChecked = false;
            foreach (var e in all)
            {
                Navigate(e.Kind, e.Key);
                lines.Add($"DETAIL {e.Kind}/{e.Key} ok");
                // 标签展开后不滚动也应该有缩略图（不能等用户滚一次才加载）
                if (e.Kind == "work" && !tagMediaChecked)
                {
                    // LoadTags 是异步的，等标签折叠区就绪（最多 3 秒），没标签的作品跳过
                    for (var wait = 0; wait < 15 &&
                        (_linkedTags is null || !_linkedTags.Children.OfType<Expander>().Any()); wait++)
                        await Task.Delay(200);
                    if (_linkedTags?.Children.OfType<Expander>().FirstOrDefault() is { } expander)
                    {
                        tagMediaChecked = true;
                        expander.IsExpanded = true;
                        await Task.Delay(2500);
                        var tiles = (expander.Content as PhotoGalleryPanel)?.Children.OfType<PhotoTile>().ToArray() ?? [];
                        lines.Add($"  TAGMEDIA tiles={tiles.Length} loaded={tiles.Count(t => t.PreviewThumbnail is not null)}");
                        expander.IsExpanded = false;
                    }
                }
                // 关系区选择模式：连续点两条应保留 2 个选中（多选）
                _relationSelection.Enter();
                var multi = _relationItems.Take(2).ToArray();
                foreach (var m in multi)
                    _relationSelection.Toggle(m.Key, false, false, _relationItems.Select(x => x.Key).ToArray());
                lines.Add($"  MULTI selected={_relationSelection.Count}/{multi.Length}");
                // 勾选框只有「已选中」才出勾：卡片自绘，状态必须与控制器逐条一致。
                var cards = _cardGrid?.Children.OfType<CatalogRelationCard>().ToArray() ?? [];
                lines.Add($"  CHECK ticked={cards.Count(c => c.Selected)} boxes={cards.Count(c => c.SelectMode)} expected={multi.Length}");
                if (_relationSearch is not null)
                {
                    var kept = _relationSelection.Count;
                    _relationSearch.Text = "zzz-no-match";
                    ApplyRelationFilter();
                    var noneVisible = _cardGrid?.Children.OfType<CatalogRelationCard>()
                        .Count(c => c.Visibility == Visibility.Visible) ?? -1;
                    lines.Add($"  SEARCH none -> visible={noneVisible} keeps={_relationSelection.Count}/{kept}");
                    _relationSearch.Text = "";
                    ApplyRelationFilter();
                    var allVisible = _cardGrid?.Children.OfType<CatalogRelationCard>()
                        .Count(c => c.Visibility == Visibility.Visible) ?? -1;
                    lines.Add($"  SEARCH clear -> visible={allVisible}/{_cardGrid?.Children.Count} keeps={_relationSelection.Count}/{kept}");
                    // 过滤后首个命中必须回到左上角，不能留在原来的列
                    if (_relationItems.FirstOrDefault() is { } probe)
                    {
                        _relationSearch.Text = probe.Name;
                        ApplyRelationFilter();
                        var firstHit = _cardGrid?.Children.OfType<CatalogRelationCard>()
                            .FirstOrDefault(c => c.Visibility == Visibility.Visible);
                        lines.Add(firstHit is null
                            ? "  SEARCH hit -> none"
                            : $"  SEARCH hit -> pos={Grid.GetRow(firstHit)}/{Grid.GetColumn(firstHit)}");
                        _relationSearch.Text = "";
                        ApplyRelationFilter();
                    }
                }
                _relationSelection.Exit();
                lines.Add($"  CHECK cleared={cards.Count(c => c.Selected)}/{cards.Length} boxes={cards.Count(c => c.SelectMode)}");
                SetSelectionMode(false);
                // 详情内新建角色：必须留在当前作品详情，并把新卡片定位到视口顶部附近
                if (e.Kind == "work")
                {
                    var tempKey = _catalog.CreateLibraryEntry("character", "验证新建角色", "verify-new",
                        Array.Empty<string>(), e.Key).Key;
                    await RefreshAsync();
                    await RevealCreatedInDetail(tempKey);
                    var hasCard = _cards.TryGetValue(tempKey, out var newCard);
                    var topGap = hasCard ? newCard!.TransformToVisual(_detail).TransformPoint(new(0, 0)).Y - _scroll.VerticalOffset : -1;
                    lines.Add($"  DETAIL-NEW kind={_kind} keepsDetail={_key == e.Key} card={hasCard} topGap={topGap:0} focus={newCard?.FocusState}");
                }
            }
            ExitDetailSelection();
            _key = null;
            ShowDetail();
            // 外层列表：搜索只过滤视图，选中项（含被隐藏的）必须保留
            if (Source(_kind).Count() >= 2)
            {
                SetSelectionMode(true);
                foreach (var e in Source(_kind).Take(2)) _selectedKeys.Add(e.Key);
                UpdateSelectionUi();
                var kept = _selectedKeys.Count;
                _search.Text = "zzz-no-match";
                RenderIndex();
                lines.Add($"LIST search keeps={_selectedKeys.Count}/{kept}");
                _search.Text = "";
                RenderIndex();
                lines.Add($"LIST clear keeps={_selectedKeys.Count}/{kept}");
                var avail = _body.RowDefinitions[2].ActualHeight;
                lines.Add($"LIST-SNAP height={ActiveIndex.Height:0} avail={avail:0} pitch=56 remainder={ActiveIndex.Height % 56:0}");
                SetSelectionMode(false);
            }
            lines.Add("BACK_TO_LIST ok");
            System.IO.File.WriteAllText(path, string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(path, "ERROR " + ex);
        }
    }
#endif

    /// <summary>详情关系区的选择模式退出，窗口级空白点击与 Esc 先走这里。</summary>
    public bool ExitDetailSelection() => _relationSelection.Exit();

    private Task DeleteSelected()
    {
        var entries = _selectedKeys.Select(key => Entries(_kind).GetValueOrDefault(key)).OfType<TagCatalogService.LibraryEntry>().ToArray();
        return entries.Length == 0 ? Task.CompletedTask : RunPopup(() => Delete(entries));
    }

    private void QueueVisibleRefresh()
    {
        if (!IsLoaded || Visibility != Visibility.Visible) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded || Visibility != Visibility.Visible || AppService.IsShuttingDown) return;
            if (_snapshot is null && !_refreshInFlight) _ = RefreshAsync();
            else if (_renderSnapshotOnShow) RenderSnapshot();
            ResumeMediaThumbnails();
        });
    }

    internal void ResumeMediaThumbnails()
    {
        if (_linkedTags is not { } content) return;
        foreach (var section in content.Children.OfType<Expander>())
            if (section.IsExpanded && section.Content is PhotoGalleryPanel panel)
                ScheduleTagThumbnails(section, panel);
    }

    /// <summary>
    /// 对账标签折叠区里可见的缩略图。展开瞬间的 Loaded／LayoutReady／ViewportInvalidated
    /// 都早于首帧测量，此时 Repeater 还没实现瓦片；没有已实现瓦片时强制布局并在下一帧重试（最多两次），
    /// 与图库「布局后再 ScheduleViewport」是同一思路。
    /// </summary>
    private void ScheduleTagThumbnails(Expander section, PhotoGalleryPanel panel, int attempt = 0)
    {
        if (!_tagThumbnailPending.Add(panel)) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _tagThumbnailPending.Remove(panel);
            if (!ReferenceEquals(section.Content, panel) || !section.IsExpanded ||
                Visibility != Visibility.Visible) return;
            var top = panel.TransformToVisual(_scroll).TransformPoint(new(0, 0)).Y;
            var viewport = _scroll.ViewportHeight;
            if (top >= viewport || top + panel.ActualHeight <= 0) return;
            var (first, last) = panel.VisibleRange(-top, viewport, 0);
            var realized = false;
            for (var index = first; index <= last; index++)
            {
                var rect = panel.Rects[index];
                if (top + rect.Bottom <= 0 || top + rect.Top >= viewport) continue;
                if (panel.TryGetTile(index) is not { } tile) continue;
                tile.RequestThumbnail(ThumbnailService.CompactEdge, ImagePriority.Visible,
                    galleryViewport: false, compact: true);
                realized = true;
            }
            if (!realized && attempt < 2)
            {
                panel.UpdateLayout();
                ScheduleTagThumbnails(section, panel, attempt + 1);
            }
        });
    }

    public async Task RefreshAsync()
    {
        var revision = ++_refreshRevision;
        _refreshInFlight = true;
        try
        {
            var snapshot = await Task.Run(_catalog.ReadLibrary);
            if (revision != _refreshRevision) return;
            _coverImages.Clear();
            _coverThumbnails.Clear();
            ApplySnapshot(snapshot);
            _renderSnapshotOnShow = true;
            if (Visibility == Visibility.Visible) RenderSnapshot();
        }
        catch (Exception ex) { ToastRequested?.Invoke("资料库读取失败", ToastKind.Info, ex.Message); }
        finally { if (revision == _refreshRevision) _refreshInFlight = false; }
    }

    private void RenderSnapshot()
    {
        _renderSnapshotOnShow = false;
        var indexOffset = FindScroll(ActiveIndex)?.VerticalOffset ?? 0;
        if (_key is not null && !Entries(_kind).ContainsKey(_key)) _key = null;
        RenderIndex();
        ShowDetail(_scroll.VerticalOffset);
        if (_key is null && _surface is null && FindScroll(ActiveIndex) is { } indexScroll)
            RestoreScroll(indexScroll, indexOffset);
    }

    private void ApplySnapshot(TagCatalogService.LibrarySnapshot snapshot)
    {
        _snapshot = snapshot;
        _workIndex = snapshot.Works.ToDictionary(e => e.Key);
        _peopleIndex = snapshot.Characters.ToDictionary(e => e.Key);
        _workRoster = snapshot.Appearances.SelectMany(g => g.Select(work => (Work: work, Person: g.Key)))
            .ToLookup(m => m.Work, m => m.Person);
    }

    private Dictionary<string, TagCatalogService.LibraryEntry> Entries(string kind) => kind == "work" ? _workIndex : _peopleIndex;
    private IReadOnlyList<TagCatalogService.LibraryEntry> Source(string kind)
        => (kind == "work" ? _snapshot?.Works : _snapshot?.Characters) ?? [];
    private string KindName => _kind == "work" ? "作品" : "角色";
    private string WorkName(string key) => _workIndex.GetValueOrDefault(key)?.Name ?? "待指定原作";

    public Location CaptureLocation(string? card = null) => new(_kind, _key, _search.Text,
        _scroll.VerticalOffset, FindScroll(ActiveIndex)?.VerticalOffset ?? 0, card, _surface, _popup, _useGrid);

    public void RestoreLocation(Location location)
    {
        var hadPopup = _popup is not null;
        _popup = null;
        if (hadPopup) DialogService.Dismiss();
        var indexChanged = _kind != location.Kind || _useGrid != location.GridView || _search.Text != location.Query;
        if (_kind != location.Kind || _key != location.Key) ExitSelection();
        _kind = location.Kind;
        _key = location.Key is { } key && (_snapshot is null || Entries(_kind).ContainsKey(key)) ? key : null;
        _useGrid = location.GridView;
        _surface = location.Surface;
        _selecting = true;
        _search.Text = location.Query;
        _selecting = false;
        if (indexChanged) RenderIndex();
        ShowDetail(location.Offset);
        if (location.Surface is { } surface) FocusSurface(surface);
        else if (location.Card is { } cardKey && _cards.TryGetValue(cardKey, out var card))
            card.Focus(FocusState.Programmatic);
        if (_key is null && _surface is null && FindScroll(ActiveIndex) is { } indexScroll)
        {
            Focus(FocusState.Programmatic);
            RestoreScroll(indexScroll, location.IndexOffset);
            if (location.Card is { } rowKey)
            {
                var row = ActiveIndex.Items.OfType<IndexRow>().FirstOrDefault(item => item.Key == rowKey);
                if (row is not null && ActiveIndex.ContainerFromItem(row) is Control container)
                    container.Focus(FocusState.Programmatic);
            }
        }
        if (location.Popup is { } popup)
        {
            _popup = popup;
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (ReferenceEquals(_popup, popup)) await RunPopup(popup, record: false);
            });
        }
    }

    private void SelectTab(string kind)
    {
        if (kind != _kind) { ExitSelection(); Navigate(kind, null); }
    }

    private void Navigate(string kind, string? key, string query = "", string? sourceCard = null)
    {
        var previous = CaptureLocation(sourceCard);
        RestoreLocation(new(kind, key, query, 0, 0, null, null, null, _useGrid));
        if (key is not null) _edit?.Focus(FocusState.Programmatic);
        Navigated?.Invoke(previous, CaptureLocation());
    }

    private IEnumerable<TagCatalogService.LibraryEntry> OrderedEntries(string kind)
        => _nameDescending ? Source(kind).Reverse() : Source(kind);

    private void RenderIndex()
    {
        UpdateTabStyle(_worksTab, _kind == "work");
        UpdateTabStyle(_peopleTab, _kind == "character");
        _search.PlaceholderText = $"搜索{KindName}名称、别名";
        _listMode.IsChecked = !_useGrid;
        _gridMode.IsChecked = _useGrid;
        if (_useGrid) _viewIcon.FontFamily = (FontFamily)Application.Current.Resources["PhotosUWPFontFamily"];
        else _viewIcon.ClearValue(FontIcon.FontFamilyProperty);
        _viewIcon.Glyph = _useGrid ? (string)Application.Current.Resources["GridLayoutGlyph"] : "\uE8FD";

        var matches = TagCatalogService.LibraryEntry.SearchMatcher(_search.Text);
        var rows = OrderedEntries(_kind).Where(matches).Select(e => new IndexRow(e.Key,
            e.Name, _kind == "work" ? $"{_workRoster?[e.Key].Count() ?? 0} 位角色" : WorkName(e.Origin),
            KindName, _kind == "work" ? "\uE8F1" : "\uE77B", _kind, HasCover(_kind, e.Key))).ToList();
        _list.Visibility = _useGrid ? Visibility.Collapsed : Visibility.Visible;
        _grid.Visibility = _useGrid ? Visibility.Visible : Visibility.Collapsed;
        _pendingHighlight = null;
        _list.Footer = null;
        _grid.Footer = null;
        ActiveIndex.ItemsSource = rows;
        if (_selectionMode)
        {
            // 过滤后仍保留被隐藏的选中项（与 File Explorer／Gmail 一致：搜索是视图过滤，不是选区重置）；
            // 只是区间锚点若被过滤掉就失效，避免 Shift 跨筛选算出意外的区间。
            if (_selectionAnchor is { } anchor && !rows.Any(row => row.Key == anchor)) _selectionAnchor = null;
            UpdateSelectionUi();
        }
        _empty.Text = _snapshot is null ? "正在读取资料…" :
            string.IsNullOrWhiteSpace(_search.Text) ? $"尚无{KindName}" : "没有匹配的资料";
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnapIndexHeight();
    }

    private MenuFlyout BuildSortMenu()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        menu.Items.Add(new RadioMenuFlyoutItem
        {
            Text = "名字", GroupName = "CatalogSortType", IsChecked = true,
            Icon = PhotosIcon("NameSortGlyph"),
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var descending in new[] { false, true })
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = descending ? "降序" : "升序",
                Icon = PhotosIcon(descending ? "DescendingSortGlyph" : "AscendingSortGlyph"),
                GroupName = "CatalogNameSort",
                IsChecked = descending == _nameDescending,
            };
            item.Click += (_, _) =>
            {
                if (_nameDescending == descending) return;
                _nameDescending = descending;
                RenderIndex();
                if (FindScroll(ActiveIndex) is { } scroll) RestoreScroll(scroll, 0);
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private void ShowDetail(double offset = 0)
    {
        _linkedTags = null;
        _detail.Children.Clear();
        var browsing = _key is null;
        _banner.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        _body.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        _detailHost.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        _create.Visibility = browsing && _selectedKeys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _search.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        _editSlot.Content = null;
        _editSlot.Visibility = Visibility.Collapsed;
        _cards.Clear();
        _cardGrid = null;
        _heading = null;
        _edit = null;
        if (browsing || _key is null || !Entries(_kind).TryGetValue(_key, out var current)) return;
        _detail.Children.Add(BuildCover(current));

        var hero = new Grid { ColumnSpacing = 12 };
        hero.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        hero.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 5 };
        titles.Children.Add(Text(KindName, 13, false, .68));
        _heading = Text(current.Name, 36, true);
        titles.Children.Add(_heading);
        if (current.Zh.Length > 0 && current.En.Length > 0) titles.Children.Add(Text(current.En, 15, false, .72));
        if (current.Aliases.Length > 0) titles.Children.Add(Text("别名：" + string.Join("、", current.Aliases), 14, false, .78));
        hero.Children.Add(titles);
        var delete = ActionButton("删除资料", () => RunPopup(() => Delete(current)));
        var red = BrushFor("SystemFillColorCriticalBrush");
        delete.Foreground = red;
        delete.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new FontIcon { Glyph = (string)Application.Current.Resources["DeleteGlyph"], Foreground = red, FontSize = 14 },
            new TextBlock { Text = "删除资料", Foreground = red },
        } };
        _edit = ActionButton("编辑资料", () => Edit(current, current.Kind));
        _edit.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            PhotosIcon("EditGlyph"), new TextBlock { Text = "编辑资料" },
        } };
        _edit.VerticalAlignment = VerticalAlignment.Top;
        hero.ContextFlyout = new MenuFlyout { Items = { DestructiveItem("删除资料", () => RunPopup(() => Delete(current))) } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(delete);
        actions.Children.Add(_edit);
        Grid.SetColumn(actions, 1);
        hero.Children.Add(actions);
        _detail.Children.Add(hero);
        _detail.Children.Add(new Border { Height = 1, Background = BrushFor("ControlStrokeColorDefaultBrush") });
        _detail.Children.Add(_editSlot);

        var items = Related(current);
        _relationItems = [.. items];
        _relationSelection.Exit();
        var addRelation = _relationAdd = IconActionButton("\uE710",
            current.Kind == "work" ? "添加角色" : "添加作品", () => AddRelation(current));
        var (relationSelect, relationDelete) = LocalSelectionButtons(_relationSelection,
            () => DeleteSelectedRelations(current));
        _relationSelect = relationSelect;
        _relationDelete = relationDelete;
        _relationSelection.Changed -= RefreshRelationUi;
        _relationSelection.Changed += RefreshRelationUi;
        var relationSearch = BuildRelationSearch();
        var toolbar = Horizontal();
        toolbar.Children.Add(relationSearch);
        toolbar.Children.Add(addRelation);
        toolbar.Children.Add(relationSelect);
        toolbar.Children.Add(relationDelete);
        var group = Section(current.Kind == "work" ? "角色" : "登场作品", items.Count, toolbar);
        if (items.Count == 0) group.Children.Add(Text("暂无关联", 13, false, .68));
        _cardGrid = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
        _cardColumns = 0;
        foreach (var entry in items)
        {
            var subtitle = current.Kind == "character" && current.Origin == entry.Key ? "原作"
                : current.Kind == "work" && entry.Origin != current.Key ? WorkName(entry.Origin) : entry.En;
            var button = new CatalogRelationCard(entry.Name, subtitle, () => _ = RunPopup(() => Delete(entry)))
            {
                Tag = entry,
            };
            button.Click += (_, _) =>
            {
                if (_relationSelection.IsActive)
                    _relationSelection.Toggle(entry.Key, IsSelectionKeyDown(Windows.System.VirtualKey.Control),
                        IsSelectionKeyDown(Windows.System.VirtualKey.Shift), _relationItems.Select(e => e.Key).ToArray());
                else OpenRelation(entry, button);
            };
            button.KeyDown += (_, args) =>
            {
                if (!_relationSelection.IsActive) return;
                if (IsSelectionKeyDown(Windows.System.VirtualKey.Control) && args.Key == Windows.System.VirtualKey.A)
                {
                    _relationSelection.SelectAll(_relationItems.Select(e => e.Key));
                    args.Handled = true;
                }
                else if (args.Key == Windows.System.VirtualKey.Delete)
                {
                    DeleteSelectedRelations(current);
                    args.Handled = true;
                }
            };
            button.RightSelected += () =>
            {
                _relationSelection.RightSelect(entry.Key);
                button.ContextFlyout = new MenuFlyout { Items =
                {
                    DestructiveItem("删除资料", () => RunPopup(() => DeleteSelectedRelations(current))),
                } };
            };
            _cardGrid.Children.Add(button);
            _cards[entry.Key] = button;
        }
        LayoutCards();
        group.Children.Add(_cardGrid);
        _detail.Children.Add(group);

        var addTag = IconActionButton("\uE710", "关联标签", () => RunPopup(() => Link(current)));
        var tagToolbar = Horizontal();
        tagToolbar.Children.Add(BuildTagSearch());
        tagToolbar.Children.Add(addTag);
        var tagGroup = Section("关联图库标签", action: tagToolbar);
        var linkedTags = _linkedTags = new StackPanel { Spacing = 8 };
        linkedTags.Children.Add(Text("正在读取…", 13, false, .68));
        tagGroup.Children.Add(linkedTags);
        _detail.Children.Add(tagGroup);
        _ = LoadTags(current, linkedTags);

        AttachSurface();
        RestoreScroll(_scroll, offset);
    }

    private SearchInputBox BuildRelationSearch()
    {
        var search = _relationSearch = new SearchInputBox { PlaceholderText = "搜索关联角色或作品", Height = 32, Width = 180 };
        search.TextChanged += (_, _) => ApplyRelationFilter();
        return search;
    }

    private SearchInputBox BuildTagSearch()
    {
        var search = _tagSearch = new SearchInputBox { PlaceholderText = "搜索关联标签", Height = 32, Width = 180 };
        search.TextChanged += (_, _) => ApplyTagFilter();
        return search;
    }

    private void ApplyRelationFilter()
    {
        if (_cardGrid is null) return;
        var term = _relationSearch?.Text ?? "";
        var matcher = TagCatalogService.LibraryEntry.SearchMatcher(term);
        foreach (var card in _cardGrid.Children.OfType<CatalogRelationCard>())
            card.Visibility = card.Tag is TagCatalogService.LibraryEntry entry && matcher(entry)
                ? Visibility.Visible : Visibility.Collapsed;
        // 不裁选区：被过滤掉的条目仍算选中，清空搜索后勾选回到原样。
        RefreshRelationUi();
        LayoutCards();
    }

    private void ApplyTagFilter()
    {
        if (_linkedTags is null) return;
        var term = _tagSearch?.Text ?? "";
        foreach (var section in _linkedTags.Children.OfType<Expander>())
        {
            var label = AutomationProperties.GetName(section);
            section.Visibility = term.Length == 0 || label.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RefreshRelationUi()
    {
        if (_relationAdd is not null) _relationAdd.Visibility = _relationSelection.IsActive ? Visibility.Collapsed : Visibility.Visible;
        if (_cardGrid is not null)
            foreach (var card in _cardGrid.Children.OfType<CatalogRelationCard>())
            {
                card.SelectMode = _relationSelection.IsActive;
                card.Selected = card.Tag is TagCatalogService.LibraryEntry entry && _relationSelection.Contains(entry.Key);
            }
    }

    private Task DeleteSelectedRelations(TagCatalogService.LibraryEntry current)
    {
        var entries = _relationItems.Where(e => _relationSelection.Contains(e.Key)).ToArray();
        return entries.Length == 0 ? Task.CompletedTask : RunPopup(() => Delete(entries));
    }

    private bool HasCover(string kind, string key) => _snapshot?.Covers.Contains((kind, key)) == true;

    private UIElement BuildCover(TagCatalogService.LibraryEntry entry)
    {
        var hasCover = HasCover(entry.Kind, entry.Key);
        var cover = new Border { Height = hasCover ? 220 : 58, CornerRadius = new(8) };
        if (!hasCover)
        {
            var caption = Text("设置背景图片", 13, false, .8);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.VerticalAlignment = VerticalAlignment.Center;
            cover.Child = caption;
        }
        var button = new Button
        {
            Content = cover, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(0),
        };
        AutomationProperties.SetName(button, hasCover ? "编辑背景图片" : "设置背景图片");
        button.Click += async (_, _) => await Run(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, AppService.MainWindowHandle);
            if (await picker.PickSingleFileAsync() is not { } file) return;
            using var image = await ImageEditService.LoadAsync(file.Path, 1600);
            if (image is null) throw new InvalidDataException("无法读取这张图片");
            await Task.Run(async () =>
            {
                using var bytes = new MemoryStream();
                await image.SaveAsJpegAsync(bytes, new JpegEncoder { Quality = 85, SkipMetadata = true });
                _catalog.SaveLibraryCover(entry.Kind, entry.Key, bytes.ToArray());
            });
            await Saved("已更新背景图片");
        });
        if (!hasCover) return button;

        Func<Task> removeCover = async () =>
        {
            await Task.Run(() => _catalog.SaveLibraryCover(entry.Kind, entry.Key, null));
            await Saved("已移除背景图片");
        };
        _ = LoadCover(cover, entry.Kind, entry.Key, () => _kind == entry.Kind && _key == entry.Key);

        // 叉号与更换按钮同级，避免 Button 嵌套抢点击；悬停背景区域才显示，无图时不生成。
        var removeButton = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new(8), CornerRadius = new(4), BorderThickness = new(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0)),
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(removeButton, "移除背景图片");
        removeButton.Click += async (_, _) => await Run(removeCover);

        var host = new Grid();
        host.Children.Add(button);
        host.Children.Add(removeButton);
        host.PointerEntered += (_, _) => removeButton.Visibility = Visibility.Visible;
        host.PointerExited += (_, _) => removeButton.Visibility = Visibility.Collapsed;
        return host;
    }

    private async Task LoadCover(Border target, string kind, string key, Func<bool> current, bool thumbnail = false)
    {
        try
        {
            var cache = thumbnail ? _coverThumbnails : _coverImages;
            var cacheKey = (kind, key);
            if (!cache.TryGetValue(cacheKey, out var loading))
            {
                if (cache.Count == (thumbnail ? 48 : 6)) cache.Remove(cache.Keys.First());
                cache[cacheKey] = loading = DecodeCover(kind, key, thumbnail);
            }
            var bitmap = await loading;
            if (bitmap is null || !current()) return;
            target.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            target.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { AppService.DebugLog("catalog cover: " + ex.Message); }
    }

    private async Task<BitmapImage?> DecodeCover(string kind, string key, bool thumbnail)
    {
        var bytes = await Task.Run(() => _catalog.ReadLibraryCover(kind, key));
        if (bytes is null) return null;
        using var buffer = new MemoryStream(bytes, writable: false);
        using var stream = buffer.AsRandomAccessStream();
        var bitmap = new BitmapImage();
        if (thumbnail)
        {
            var info = SixLabors.ImageSharp.Image.Identify(bytes);
            if (info.Width >= info.Height && info.Width > 480) bitmap.DecodePixelWidth = 480;
            if (info.Height > info.Width && info.Height > 480) bitmap.DecodePixelHeight = 480;
        }
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private MenuFlyoutItem DestructiveItem(string label, Func<Task> action)
    {
        var red = BrushFor("SystemFillColorCriticalBrush");
        var item = new MenuFlyoutItem
        {
            Text = label, Foreground = red,
            Icon = new FontIcon { Glyph = (string)Application.Current.Resources["DeleteGlyph"], Foreground = red },
        };
        item.Resources["MenuFlyoutItemForegroundPointerOver"] = red;
        item.Resources["MenuFlyoutItemForegroundPressed"] = red;
        item.Click += async (_, _) => await Run(action);
        return item;
    }

    public Task RefreshLinkedTagsAsync()
    {
        if (Visibility != Visibility.Visible || _linkedTags is not { } content ||
            _key is not { } key || !Entries(_kind).TryGetValue(key, out var entry)) return Task.CompletedTask;
        return LoadTags(entry, content);
    }

    private async Task LoadTags(TagCatalogService.LibraryEntry entry, StackPanel content)
    {
        var revision = ++_tagRefreshRevision;
        try
        {
            var (tags, labels) = await Task.Run(() =>
            {
                var rows = _catalog.LibraryTags(entry.Kind, entry.Key);
                var ids = rows.Select(t => t.Id).ToHashSet();
                var names = rows.Count == 0 ? new Dictionary<int, string>() :
                    AppService.Store?.AllTags().Where(t => ids.Contains(t.Id)).ToDictionary(t => t.Id, t => t.Display) ?? [];
                return (rows, names);
            });
            if (!ReferenceEquals(content, _linkedTags) || revision != _tagRefreshRevision) return;
            var current = content.Children.OfType<Expander>().ToDictionary(section => ((LinkedTag)section.Tag).Id);
            var retained = tags.Select(tag => tag.Id).ToHashSet();
            for (var i = content.Children.Count - 1; i >= 0; i--)
                if (content.Children[i] is not Expander { Tag: LinkedTag state } || !retained.Contains(state.Id))
                    content.Children.RemoveAt(i);
            if (tags.Count == 0) content.Children.Add(Text("暂无关联标签", 13, false, .68));
            var mediaRefreshes = new List<Task>();
            for (var i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                var label = labels.GetValueOrDefault(tag.Id, tag.Name);
                var chip = Tags.TagChip.CreateInteractive(tag.Category, label, tag.Name,
                    () => TagFilterRequested?.Invoke(tag.Id, tag.Name), text => TagCopied?.Invoke(text),
                    $"{tag.Name} · {tag.Usage} 张图片");
                var menu = (MenuFlyout)chip.Label.ContextFlyout;
                menu.Items.Add(new MenuFlyoutSeparator());
                var unlink = new MenuFlyoutItem { Text = "解除关联" };
                unlink.Click += async (_, _) => await RunPopup(() => Unlink(entry, tag));
                menu.Items.Add(unlink);
                var item = Horizontal();
                item.Children.Add(chip);
                item.Children.Add(Text($"{tag.Usage} 张", 12, false, .68));
                if (!current.TryGetValue(tag.Id, out var section))
                {
                    var state = new LinkedTag(tag.Id);
                    section = new Expander { Tag = state, HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    section.Expanding += async (_, _) =>
                    {
                        RevealTagMediaWhenArranged(section);
                        await ShowTagMedia(section, state);
                    };
                    section.Collapsed += (_, _) =>
                    {
                        state.MediaRevision++;
                        section.Content = null;
                    };
                }
                section.Header = item;
                AutomationProperties.SetName(section, $"{label} 的图片和视频");
                var previousIndex = content.Children.IndexOf(section);
                if (previousIndex != i)
                {
                    if (previousIndex >= 0) content.Children.RemoveAt(previousIndex);
                    content.Children.Insert(i, section);
                }
                if (section.IsExpanded)
                    mediaRefreshes.Add(ShowTagMedia(section, (LinkedTag)section.Tag, refresh: true));
            }
            ApplyTagFilter();
            await Task.WhenAll(mediaRefreshes);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(content, _linkedTags) || revision != _tagRefreshRevision) return;
            if (content.Children.OfType<Expander>().Any())
                ToastRequested?.Invoke("关联标签刷新失败", ToastKind.Info, ex.Message);
            else
            {
                content.Children.Clear();
                content.Children.Add(Text("读取标签失败：" + ex.Message, 13));
            }
        }
    }

    private void RevealTagMediaWhenArranged(Expander section)
    {
        var offset = _scroll.VerticalOffset;
        void Stop()
        {
            section.LayoutUpdated -= OnLayout;
            section.Collapsed -= OnCollapsed;
            section.Unloaded -= OnUnloaded;
        }
        void OnCollapsed(Expander sender, ExpanderCollapsedEventArgs args) => Stop();
        void OnUnloaded(object sender, RoutedEventArgs args) => Stop();
        void OnLayout(object? sender, object args)
        {
            if (!section.IsExpanded || section.Content is not FrameworkElement content ||
                content is Border or PhotoGalleryPanel { IsBuilding: true } ||
                content.ActualHeight <= 0 || _scroll.ViewportHeight <= 0) return;
            if (Visibility != Visibility.Visible || Math.Abs(_scroll.VerticalOffset - offset) > 1)
            {
                Stop();
                return;
            }
            var top = section.TransformToVisual(_scroll).TransformPoint(new(0, 0)).Y;
            var target = Math.Clamp(offset + top, 0, _scroll.ScrollableHeight);
            Stop();
            if (Math.Abs(target - offset) <= 1) return;
            _scroll.ChangeView(null, target, null, disableAnimation: true);
        }

        section.LayoutUpdated += OnLayout;
        section.Collapsed += OnCollapsed;
        section.Unloaded += OnUnloaded;
    }

    private async Task ShowTagMedia(Expander section, LinkedTag state, bool refresh = false)
    {
        if ((!refresh && section.Content is not null) || AppService.Store is not { } store) return;
        var revision = ++state.MediaRevision;
        bool IsCurrent() => revision == state.MediaRevision && section.IsExpanded &&
            _linkedTags?.Children.Contains(section) == true;
        if (section.Content is null)
        {
            var loading = Text("正在读取媒体…");
            loading.VerticalAlignment = VerticalAlignment.Center;
            section.Content = new Border { MinHeight = 96, Child = loading };
        }
        try
        {
            var hide = AppService.Settings.Settings.nsfw_display == "hide";
            var items = await Task.Run(() =>
            {
                using var reader = new StoreService(store.DbPath, readOnly: true);
                return GalleryPage.MapRows(reader.Query(new StoreService.QuerySpec
                    { TagIds = [state.Id], HideNsfw = hide, Sort = "taken", Desc = true }), reader.FavoriteIds(), "taken");
            });
            if (!IsCurrent()) return;
            if (items.Count == 0)
            {
                section.Content = Text("暂无可显示的媒体", 13, false, .68);
                return;
            }

            if (section.Content is PhotoGalleryPanel existing)
            {
                existing.SetItems(items);
                ScheduleTagThumbnails(section, existing);
                return;
            }

            var panel = new PhotoGalleryPanel { Mode = GalleryLayoutMode.Grid, Size = ThumbnailSize.Small,
                RealizationBufferRatio = .25,
                TileTemplate = (DataTemplate)Application.Current.Resources["CatalogPhotoTileTemplate"] };
            section.Content = panel;
            panel.Loaded += (_, _) => ScheduleTagThumbnails(section, panel);
            panel.LayoutReady += () => ScheduleTagThumbnails(section, panel);
            panel.ViewportInvalidated += () => ScheduleTagThumbnails(section, panel);
            panel.SizeChanged += (_, _) => ScheduleTagThumbnails(section, panel);
            panel.TilePrepared += tile =>
            {
                if (tile.EventsWired) return;
                tile.EventsWired = true;
                tile.FavoriteRequested = ToggleFavorite;
                tile.ThumbnailInvalidated += () => ScheduleTagThumbnails(section, panel);
                tile.Tapped += (_, args) =>
                {
                    if (tile.Detached || !ReferenceEquals(section.Content, panel) || panel.IndexOfTile(tile) < 0) return;
                    args.Handled = true;
                    OpenTagMediaPreview(panel, tile);
                };
            };
            panel.SetItems(items);
            // 展开这一帧的 Loaded／LayoutReady／ViewportInvalidated 都早于首次测量，
            // 此刻 Repeater 还没有瓦片；强制布局后下一帧再对账一次。
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!ReferenceEquals(section.Content, panel)) return;
                panel.UpdateLayout();
                ScheduleTagThumbnails(section, panel);
            });
        }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            if (section.Content is PhotoGalleryPanel)
                ToastRequested?.Invoke("关联媒体刷新失败", ToastKind.Info, ex.Message);
            else section.Content = Text("读取媒体失败：" + ex.Message);
        }
    }

    private void OpenTagMediaPreview(PhotoGalleryPanel panel, PhotoTile tile)
    {
        var (rect, fully) = GalleryPage.TileFlightRect(tile, _scroll);
        PreviewRequested?.Invoke(new(tile.Item, panel.Items.ToArray(), rect, fully, item =>
            panel.Children.FirstOrDefault(t => !t.Detached && t.Item.ImageId == item.ImageId && panel.IndexOfTile(t) >= 0) is { IsLoaded: true } source
                ? GalleryPage.TileFlightRect(source, _scroll).Rect : null,
            tile.PreviewThumbnail, FollowGalleryResults: false));
    }

    private void LayoutCards()
    {
        if (_cardGrid is null) return;
        var columns = Math.Clamp((int)(_scroll.ActualWidth / 225), 1, 3);
        if (_cardColumns != columns)
        {
            _cardColumns = columns;
            _cardGrid.ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++)
                _cardGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        }
        // 过滤后必须重排：Collapsed 卡片仍持有原行列，不重排会让唯一命中项留在中间或末尾。
        var visible = _cardGrid.Children.OfType<CatalogRelationCard>()
            .Where(card => card.Visibility == Visibility.Visible).ToArray();
        var rows = (visible.Length + columns - 1) / columns;
        if (_cardGrid.RowDefinitions.Count != rows)
        {
            _cardGrid.RowDefinitions.Clear();
            for (var i = 0; i < rows; i++) _cardGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        }
        for (var i = 0; i < visible.Length; i++)
        {
            Grid.SetColumn(visible[i], i % columns);
            Grid.SetRow(visible[i], i / columns);
        }
    }

    private List<TagCatalogService.LibraryEntry> Related(TagCatalogService.LibraryEntry entry)
        => entry.Kind == "work"
            ? (_workRoster?[entry.Key] ?? []).Where(_peopleIndex.ContainsKey).Select(k => _peopleIndex[k])
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : (_snapshot?.Appearances[entry.Key] ?? []).Where(_workIndex.ContainsKey).Select(k => _workIndex[k])
                .OrderByDescending(e => e.Key == entry.Origin).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void OpenRelation(TagCatalogService.LibraryEntry to, Button source)
    {
        var old = source.TransformToVisual(this).TransformPoint(new(10, 9));
        Navigate(to.Kind, to.Key, sourceCard: to.Key);
        _edit?.Focus(FocusState.Programmatic);
        if (_heading is null || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        UpdateLayout();
        var at = _heading.TransformToVisual(this).TransformPoint(new(0, 0));
        var visual = ElementCompositionPreview.GetElementVisual(_heading);
        var shift = visual.Compositor.CreateVector3KeyFrameAnimation();
        shift.InsertKeyFrame(0, new((float)(old.X - at.X), (float)(old.Y - at.Y), 0));
        shift.InsertKeyFrame(1, Vector3.Zero);
        shift.Duration = TimeSpan.FromMilliseconds(220);
        ElementCompositionPreview.SetIsTranslationEnabled(_heading, true);
        visual.StartAnimation("Translation", shift);
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = shift.Duration;
        scale.InsertKeyFrame(0, new Vector3(.58f, .58f, 1));
        scale.InsertKeyFrame(1, Vector3.One);
        visual.StartAnimation("Scale", scale);
    }

    private Task Back()
    {
        if (HistoryRequested?.Invoke(-1) != true) ReturnToIndex();
        return Task.CompletedTask;
    }

    public bool ReturnToIndex()
    {
        if (_key is null) return false;
        var previous = CaptureLocation(_key);
        var next = previous with { Key = null, Offset = 0, Surface = null, Popup = null };
        RestoreLocation(next);
        Navigated?.Invoke(previous, CaptureLocation());
        return true;
    }

    private void RestoreScroll(ScrollViewer viewer, double offset)
    {
        UpdateLayout();
        viewer.ChangeView(null, offset, null, true);
    }

    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }

    private Task Edit(TagCatalogService.LibraryEntry? entry, string kind, string? origin = null, string initialName = "",
        bool replacePopup = false, bool revealInDetail = false)
    {
        var createTitle = entry is null ? "新建" + (kind == "work" ? "作品" : "角色") : null;
        if (kind == "character" && _workIndex.Count == 0)
            return RunPopup(() => Message("需要一部作品", "请先在作品库新建一部作品。"));

        var zh = new TextBox { Header = "中文名", Text = entry?.Zh ?? initialName };
        var en = new TextBox { Header = "英文名", Text = entry?.En ?? "" };
        var aliases = new TextBox { Header = "别名（用顿号或逗号分隔）", Text = string.Join("、", entry?.Aliases ?? []) };
        var panel = new StackPanel { Spacing = 12, Children = { zh, en, aliases } };
        var originKey = origin ?? entry?.Origin;
        if (originKey is not null && !_workIndex.ContainsKey(originKey)) originKey = null;
        Button? originButton = null;
        Action? originChanged = null;
        List<Choice<TagCatalogService.LibraryEntry>> SearchWorks(string query)
        {
            var matches = TagCatalogService.LibraryEntry.SearchMatcher(query);
            return Source("work").Where(matches).Select(work => new Choice<TagCatalogService.LibraryEntry>(work, work.Name)).ToList();
        }
        void SetOrigin(TagCatalogService.LibraryEntry work)
        {
            originKey = work.Key;
            if (originButton is not null) originButton.Content = work.Name;
            originChanged?.Invoke();
        }
        if (kind == "character")
        {
            originButton = new Button { Content = originKey is null ? "选择原作…" : WorkName(originKey),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(originButton, "选择原作");
            panel.Children.Add(new StackPanel { Spacing = 6, Children = { Text("原作"), originButton } });
        }
        async Task<TagCatalogService.LibraryEntry?> SaveEntry()
        {
            var selectedOrigin = originKey;
            var aliasList = aliases.Text.Split(['、', ',', '，', ';', '；', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var chinese = zh.Text;
            var english = en.Text;
            return await Task.Run<TagCatalogService.LibraryEntry?>(() =>
            {
                if (entry is null) return _catalog.CreateLibraryEntry(kind, chinese, english, aliasList, selectedOrigin);
                _catalog.SaveLibraryEntry(kind, entry.Key, chinese, english, aliasList, selectedOrigin == entry.Origin ? null : selectedOrigin);
                return null;
            });
        }

        bool IsValid() => (!string.IsNullOrWhiteSpace(zh.Text) || !string.IsNullOrWhiteSpace(en.Text))
            && (originButton is null || originKey is not null);

        void WatchFields(Action validate)
        {
            zh.TextChanged += (_, _) => validate();
            en.TextChanged += (_, _) => validate();
            originChanged += validate;
            validate();
        }

        if (entry is null)
        {
            var dialog = Dialog(createTitle!, panel, "新建");
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.Opened += (_, _) => zh.Focus(FocusState.Programmatic);
            WatchFields(() => dialog.IsPrimaryButtonEnabled = IsValid());
            if (originButton is { } button)
            {
                SetPickerWidth(dialog);
                Picker<TagCatalogService.LibraryEntry>? picker = null;
                void ReturnToForm()
                {
                    picker = null;
                    dialog.Content = panel;
                    dialog.Title = createTitle;
                    dialog.PrimaryButtonText = "新建";
                    dialog.IsPrimaryButtonEnabled = IsValid();
                    DispatcherQueue.TryEnqueue(() => button.Focus(FocusState.Programmatic));
                }

                button.Click += (_, _) =>
                {
                    picker = BuildPicker(SearchWorks, work => work.Key == originKey);
                    picker.List.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = picker?.Selected is not null;
                    dialog.Title = "选择原作";
                    dialog.PrimaryButtonText = "选择";
                    dialog.Content = picker.Panel;
                    dialog.IsPrimaryButtonEnabled = picker.Selected is not null;
                    var input = picker.Input;
                    DispatcherQueue.TryEnqueue(() => input.FocusSearch());
                };
                dialog.PrimaryButtonClick += (_, args) =>
                {
                    if (picker is null) return;
                    args.Cancel = true;
                    if (picker.Selected is { } work) { SetOrigin(work); ReturnToForm(); }
                };
                dialog.CloseButtonClick += (_, args) =>
                {
                    if (picker is null) return;
                    args.Cancel = true;
                    ReturnToForm();
                };
                dialog.Closing += (_, args) =>
                {
                    if (picker is null || args.Result != ContentDialogResult.None) return;
                    // Side-button navigation clears the history popup before dismissing it.
                    if (!HasHistoryPopup) return;
                    args.Cancel = true;
                    ReturnToForm();
                };
            }
            return RunPopup(async () =>
            {
                if (await ShowDialog(dialog) != ContentDialogResult.Primary) return;
                var created = (await SaveEntry())!;
                // 上下文内新建（如作品详情里新建角色）不换页：只刷新并在该分区定位；
                // 只有从外层列表新建才切到目标库并定位条目。
                if (revealInDetail && _key is not null) await RevealCreatedInDetail(created.Key);
                else
                {
                    await RefreshAsync();
                    RevealCreatedEntry(kind, created.Key);
                }
                ToastRequested?.Invoke("已新建资料", ToastKind.Success, null);
            }, replace: replacePopup);
        }

        var actions = Horizontal();
        var save = ActionButton("保存", async () =>
        {
            await SaveEntry();
            _surface = null;
            await Saved("已保存资料");
            var location = CaptureLocation();
            Navigated?.Invoke(location, location);
            _edit?.Focus(FocusState.Programmatic);
        });
        save.Style = StyleFor(true);
        actions.Children.Add(save);
        actions.Children.Add(ActionButton("取消", Back));
        panel.Children.Add(actions);
        WatchFields(() => save.IsEnabled = IsValid());
        if (originButton is { } editOrigin)
            editOrigin.Click += async (_, _) => await RunPopup(async () =>
            {
                var (work, _) = await Pick("选择原作", SearchWorks, isSelected: work => work.Key == originKey);
                if (work is not null) SetOrigin(work);
                editOrigin.Focus(FocusState.Programmatic);
            });
        return OpenSurface(panel, zh);
    }

    private Task AddRelation(TagCatalogService.LibraryEntry current)
    {
        var work = current.Kind == "work";
        return RunPopup(async () =>
        {
            var linked = Related(current).Select(e => e.Key).ToHashSet();
            var candidates = Source(work ? "character" : "work").Where(e => !linked.Contains(e.Key)).ToArray();
            var (item, createName) = await Pick(work ? "添加角色" : "添加登场作品", query =>
                candidates.Where(e => e.Matches(query)).Select(e => new Choice<TagCatalogService.LibraryEntry>(
                    e, work ? $"{e.Name} · {WorkName(e.Origin)}" : e.Name)).ToList(),
                secondary: work ? "新建角色" : null);
            if (createName is not null)
            {
                await Edit(null, "character", current.Key, createName, replacePopup: true, revealInDetail: true);
                return;
            }
            if (item is null) return;
            if (!work && current.Origin.Length == 0)
            {
                _catalog.SaveLibraryEntry("character", current.Key, current.Zh, current.En, current.Aliases, item.Key);
                await Saved("已设为原作");
                return;
            }
            if (work && item.Origin.Length == 0)
            {
                await RunPopup(async () =>
                {
                    if (!await Confirm("设置原作", $"将「{current.Name}」设为「{item.Name}」的原作？", "设为原作")) return;
                    _catalog.SaveLibraryEntry("character", item.Key, item.Zh, item.En, item.Aliases, current.Key);
                    await Saved("已设为原作");
                    await RevealInDetail(item.Key);
                }, replace: true);
                return;
            }
            _catalog.SetAppearance(work ? item.Key : current.Key, work ? current.Key : item.Key, true);
            await Saved("已添加登场关系");
            // 关联已有条目同样留在当前详情，把该卡片带到视野顶部（不脉冲）。
            await RevealInDetail(item.Key);
        });
    }

    private Task OpenSurface(FrameworkElement panel, Control focus)
    {
        var previous = CaptureLocation();
        var frame = new Border
        {
            Child = panel, Padding = new(14), CornerRadius = new(8), MaxWidth = 560,
            Background = BrushFor("ControlFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        frame.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            _ = Back();
            e.Handled = true;
        };
        _surface = new(frame, focus);
        ShowDetail(previous.Offset);
        FocusSurface(_surface);
        Navigated?.Invoke(previous, CaptureLocation());
        return Task.CompletedTask;
    }

    private void AttachSurface()
    {
        if (_surface is not { } surface) return;
        _editSlot.Content = surface.Content;
        _editSlot.Visibility = Visibility.Visible;
    }

    private void FocusSurface(Surface surface)
    {
        UpdateLayout();
        if (surface.Focus is SearchInputBox search) search.FocusSearch();
        else surface.Focus.Focus(FocusState.Programmatic);
    }

    private async Task RunPopup(Func<Task> action, bool record = true, bool replace = false)
    {
        if (record)
        {
            var previous = CaptureLocation();
            _popup = action;
            var next = CaptureLocation();
            Navigated?.Invoke(replace ? next : previous, next);
        }
        try { await Run(action); }
        finally
        {
            if (ReferenceEquals(_popup, action))
            {
                _popup = null;
                var location = CaptureLocation();
                Navigated?.Invoke(location, location);
            }
        }
    }

    private async Task Link(TagCatalogService.LibraryEntry current)
    {
        var route = _popup;
        var available = await Task.Run(() =>
        {
            var existing = _catalog.LibraryTags(current.Kind, current.Key).Select(t => t.Id).ToHashSet();
            return _catalog.LibraryTags(current.Kind).Where(t => !existing.Contains(t.Id)).ToList();
        });
        if (!ReferenceEquals(_popup, route) || _kind != current.Kind || _key != current.Key) return;
        var duplicates = available.GroupBy(t => (t.Name, t.Identity, t.Usage))
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var (tag, _) = await Pick("关联已有图库标签", q => available
            .Where(t => t.Name.Contains(q.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .Select(t => new Choice<TagCatalogService.LibraryTag>(t,
                $"{t.Name} · {t.Usage} 张 · {TagTarget(t, duplicates)}")).ToList());
        if (tag is null) return;
        if (!ReferenceEquals(_popup, route)) return;
        await RunPopup(async () =>
        {
            if (!await Confirm(tag.Identity.Length > 0 ? "确认改绑标签" : "确认关联标签",
                $"{tag.Name}\n当前关联：{TagTarget(tag, duplicates)}\n新关联：{current.Name}\n使用此标签的图片：{tag.Usage} 张", "确认关联")) return;
            _catalog.SetLibraryTag(tag.Id, current.Kind, current.Key, tag.Identity);
            await Saved("已关联标签");
        }, replace: true);
    }

    private string TagTarget(TagCatalogService.LibraryTag tag, HashSet<(string Name, string Identity, int Usage)> duplicates)
    {
        var name = tag.AssociatedName.Length > 0 ? tag.AssociatedName : tag.Identity.Length > 0 ? "关联资料不可用" : "未关联";
        if (tag.Category == "character" && _peopleIndex.TryGetValue(tag.Identity, out var person))
            name += " · " + WorkName(person.Origin);
        // Same-spelling tags can also have identical display metadata. Keep the rows distinguishable.
        if (duplicates.Contains((tag.Name, tag.Identity, tag.Usage)))
            name += $" · 标签 {tag.Id}";
        return name;
    }

    private async Task Unlink(TagCatalogService.LibraryEntry current, TagCatalogService.LibraryTag tag)
    {
        if (!await Confirm("解除关联", $"{tag.Name} 将与「{current.Name}」解除关联。使用该标签的 {tag.Usage} 张图片会保留标签。", "解除关联")) return;
        _catalog.SetLibraryTag(tag.Id, current.Kind, null, tag.Identity);
        await Saved("已解除标签关联");
    }

    private Task Delete(TagCatalogService.LibraryEntry entry) => Delete([entry]);

    private async Task Delete(IReadOnlyList<TagCatalogService.LibraryEntry> entries)
    {
        var route = _popup;
        var impact = new StackPanel { Spacing = 7 };
        impact.Children.Add(Text(entries.Count == 1 ? $"将删除「{entries[0].Name}」的资料。图库图片和已贴标签会保留。"
            : $"将删除选中的 {entries.Count} 个资料。图库图片和已贴标签会保留。"));
        // 搜索时选区可跨筛选累积：明确标出有多少项当前看不到，避免误删。
        if (!string.IsNullOrWhiteSpace(_search.Text) &&
            ActiveIndex.ItemsSource is IEnumerable<IndexRow> visibleRows)
        {
            var visibleKeys = visibleRows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
            var hidden = entries.Count(entry => !visibleKeys.Contains(entry.Key));
            if (hidden > 0) impact.Children.Add(Text($"其中 {hidden} 项不在当前搜索结果中。", 12, false, .68));
        }
        if (entries.Count > 1)
            foreach (var entry in entries) impact.Children.Add(Text(entry.Name));
        var progress = Horizontal();
        progress.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        progress.Children.Add(Text("正在读取删除影响…"));
        impact.Children.Add(progress);

        var scroll = new ScrollViewer
        {
            Content = impact, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Disabled, IsVerticalScrollChainingEnabled = false,
            Padding = new(0, 0, 16, 0),
        };
        var bar = new ContinuousScrollBar
        {
            Step = 0, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new(0, 4, 0, 4),
        };
        AutomationProperties.SetName(bar, "删除影响滚动条");
        var host = new Grid { MaxHeight = 330, Children = { scroll, bar } };
        var dialog = Dialog("删除资料", host, "删除");
        dialog.IsPrimaryButtonEnabled = false;
        DialogService.UseDestructivePrimary(dialog);

        TagCatalogService.LibraryDeletion[]? previews = null;
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                bar.BindVerticalViewport(scroll);
                var loaded = await Task.Run(() => _catalog.GetLibraryDeletions(
                    entries.Select(entry => (entry.Kind, entry.Key)).ToArray()));
                if (closed || !ReferenceEquals(_popup, route)) return;
                progress.Visibility = Visibility.Collapsed;
                var characters = loaded.SelectMany(item => item.Characters).Distinct().ToArray();
                var tags = loaded.SelectMany(item => item.Tags).DistinctBy(tag => tag.Id).ToArray();
                if (characters.Any(key => !_peopleIndex.ContainsKey(key)))
                {
                    impact.Children.Add(Text("资料已更新，请关闭此窗口后重新确认删除。"));
                    await RefreshAsync();
                    return;
                }

                if (characters.Length > 0)
                {
                    impact.Children.Add(Text($"将一并删除以此作品为原作的角色（{characters.Length}）", 14, true));
                    foreach (var name in characters.Select(key => _peopleIndex[key].Name).Order(StringComparer.CurrentCultureIgnoreCase))
                        impact.Children.Add(Text(name));
                }
                var deletedCharacters = characters.ToHashSet();
                var related = entries.SelectMany(Related).Where(item => !deletedCharacters.Contains(item.Key))
                    .DistinctBy(item => (item.Kind, item.Key)).ToArray();
                if (related.Length > 0)
                {
                    impact.Children.Add(Text($"将移除的登场关系（{related.Length}）", 14, true));
                    foreach (var item in related) impact.Children.Add(Text(item.Name));
                }
                if (tags.Length > 0)
                {
                    impact.Children.Add(Text($"将解除的标签（{tags.Length}）", 14, true));
                    foreach (var tag in tags) impact.Children.Add(Text($"{tag.Name} · {tag.Usage} 张图片"));
                }
                previews = loaded;
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (Exception ex)
            {
                if (closed || !ReferenceEquals(_popup, route)) return;
                progress.Visibility = Visibility.Collapsed;
                impact.Children.Add(Text("无法读取删除影响：" + ex.Message));
            }
        };
        if (await ShowDialog(dialog) != ContentDialogResult.Primary || previews is null) return;
        var tagsChanged = await Task.Run(() => _catalog.DeleteLibraryEntries(previews));

        // 重读资料库统一刷新列表与详情；删除关联条目时详情区也必须同步，不能只改内存快照。
        await RefreshAsync();
        ExitSelection();
        Focus(FocusState.Programmatic);
        ToastRequested?.Invoke("已删除资料", ToastKind.Success, null);
        if (tagsChanged) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Changed?.Invoke());
    }

    private sealed record Picker<T>(Grid Panel, SearchInputBox Input, ListView List) where T : class
    {
        public T? Selected => (List.SelectedItem as Choice<T>)?.Value;
    }

    private static Picker<T> BuildPicker<T>(Func<string, List<Choice<T>>> search, Func<T, bool>? isSelected)
        where T : class
    {
        var input = new SearchInputBox { PlaceholderText = "搜索名称、别名" };
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Padding = new(0, 0, 16, 0),
            ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis"
                               ToolTipService.ToolTip="{Binding Label}" />
                </DataTemplate>
                """),
        };
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        ScrollViewer.SetIsVerticalScrollChainingEnabled(list, false);
        var bar = new ContinuousScrollBar { Step = 0, Visibility = Visibility.Collapsed, Margin = new(0, 4, 0, 4) };
        AutomationProperties.SetName(bar, "候选列表滚动条");
        // 高度 = 搜索框 32 + RowSpacing 8 + 6 行（44）+ 半行 22：底部露半条候选，提示还有更多。
        var panel = new Grid { Height = 326, RowSpacing = 8 };
        panel.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = new(16) });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        Grid.SetColumnSpan(input, 2);
        panel.Children.Add(input);
        Grid.SetRow(list, 1);
        panel.Children.Add(list);
        Grid.SetRow(bar, 1);
        Grid.SetColumn(bar, 1);
        panel.Children.Add(bar);

        ScrollViewer? bound = null;
        list.Loaded += (_, _) =>
        {
            list.ApplyTemplate();
            if (FindScroll(list) is { } scroll && !ReferenceEquals(bound, scroll))
            {
                bound = scroll;
                bar.BindVerticalViewport(scroll);
            }
        };
        void Filter()
        {
            var previous = (list.SelectedItem as Choice<T>)?.Value;
            var results = search(input.Text);
            list.ItemsSource = results;
            if (previous is not null)
                list.SelectedItem = results.FirstOrDefault(candidate => EqualityComparer<T>.Default.Equals(candidate.Value, previous));
            else if (isSelected is not null && input.Text.Length == 0)
                list.SelectedItem = results.FirstOrDefault(candidate => isSelected(candidate.Value));
        }
        input.TextChanged += (_, _) => Filter();
        Filter();
        return new(panel, input, list);
    }

    private static void SetPickerWidth(ContentDialog dialog)
    {
        var width = Math.Clamp(dialog.XamlRoot.Size.Width - 32, 1, 520);
        dialog.Resources["ContentDialogMinWidth"] = width;
        dialog.Resources["ContentDialogMaxWidth"] = width;
    }

    private async Task<(T? Selected, string? SecondaryText)> Pick<T>(string title, Func<string, List<Choice<T>>> search,
        string? secondary = null, Func<T, bool>? isSelected = null) where T : class
    {
        var picker = BuildPicker(search, isSelected);
        var dialog = Dialog(title, picker.Panel, "选择");
        SetPickerWidth(dialog);
        picker.Input.EscapePressed += dialog.Hide;
#if GALLERY_DEVELOPMENT
        _pickerEscapeForVerification = dialog.Hide;
#endif
        dialog.Opened += (_, _) => picker.Input.FocusSearch();
        dialog.SecondaryButtonText = secondary ?? "";
        dialog.IsPrimaryButtonEnabled = picker.Selected is not null;
        picker.List.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = picker.Selected is not null;
        var result = await ShowDialog(dialog);
        return (result == ContentDialogResult.Primary ? picker.Selected : null,
            result == ContentDialogResult.Secondary ? picker.Input.Text.Trim() : null);
    }

    private ContentDialog Dialog(string title, object content, string primary = "") => new()
    {
        XamlRoot = XamlRoot, Title = title, Content = content, PrimaryButtonText = primary,
        CloseButtonText = primary.Length == 0 ? "关闭" : "取消", DefaultButton = ContentDialogButton.None,
    };

    private async Task<bool> Confirm(string title, string message, string action) =>
        await ShowDialog(Dialog(title, Text(message), action)) == ContentDialogResult.Primary;

    private async Task Message(string title, string message) => await ShowDialog(Dialog(title, Text(message)));

    private async Task<ContentDialogResult> ShowDialog(ContentDialog dialog)
    {
        Microsoft.UI.Xaml.Input.PointerEventHandler onPointer = (_, args) =>
        {
            var delta = args.GetCurrentPoint(dialog).Properties.PointerUpdateKind switch
            {
                Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed => -1,
                Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed => 1,
                _ => 0,
            };
            if (delta == 0 || !HasHistoryPopup) return;
            args.Handled = true;
            DispatcherQueue.TryEnqueue(() => HistoryRequested?.Invoke(delta));
        };
        dialog.AddHandler(PointerPressedEvent, onPointer, true);
        try { return await DialogService.ShowAsync(dialog); }
        finally { dialog.RemoveHandler(PointerPressedEvent, onPointer); }
    }

    /// <summary>
    /// 在当前详情内新建关联条目后的落点：不切换顶层导航，重绘详情，
    /// 把新卡片滚到分区第一行并聚焦（与外层列表「新条目成为第一行」同一套语义）。
    /// </summary>
    private async Task RevealCreatedInDetail(string key)
    {
        await RefreshAsync(); // 可见时内部 RenderSnapshot → ShowDetail 已重建关系区
        await RevealInDetail(key);
    }

    /// <summary>在当前详情里把某个关系卡片带到视口第一行；新建/关联后给焦点框并脉冲强调色。</summary>
    private async Task RevealInDetail(string key, bool pulse = true)
    {
        _scroll.UpdateLayout();
        if (!_cards.TryGetValue(key, out var card)) return;
        await ScrollCardToTopAsync(card);
        card.Focus(FocusState.Keyboard);
        if (pulse) card.Pulse();
    }

    /// <summary>
    /// 让卡片成为视口第一行。`ScrollViewer.ChangeView` 是下一帧生效，必须等一帧再校正；
    /// 内容不足一屏时滚不到顶，补一段底部占位撑出可滚距离（与外层列表 `RevealCreatedEntry` 的 Footer 做法一致，
    /// 下次 ShowDetail 会清掉）。
    /// </summary>
    private async Task ScrollCardToTopAsync(FrameworkElement card)
    {
        var top = card.TransformToVisual(_detail).TransformPoint(new(0, 0)).Y;
        var target = Math.Max(0, top - 8);
        // 可滚距离含 ScrollViewer 的上下 Padding；ScrollableHeight 会随滚动条显隐延迟更新，不用它。
        for (var pass = 0; pass < 2; pass++)
        {
            var maxOffset = Math.Max(0, _detail.ActualHeight + _scroll.Padding.Top + _scroll.Padding.Bottom
                - _scroll.ViewportHeight);
            if (target <= maxOffset + 1) break;
            // +32：滚动条出现后视口收窄会让实际可滚距离小于这里算出的值，留一点余量。
            _detail.Children.Add(new Border
            {
                Height = target - maxOffset + 32, IsHitTestVisible = false, Tag = "reveal-filler",
            });
            _scroll.UpdateLayout();
        }
        _scroll.ChangeView(null, target, null, disableAnimation: true);
        await NextFrameAsync();
    }

    private Task NextFrameAsync()
    {
        var frame = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => frame.SetResult());
        return frame.Task;
    }

    private void RevealCreatedEntry(string kind, string key)
    {
        var previous = CaptureLocation();
        var entry = Entries(kind).GetValueOrDefault(key);
        if (entry is null) return;
        var query = kind == _kind && entry.Matches(_search.Text) ? _search.Text : "";
        RestoreLocation(previous with
        {
            Kind = kind, Key = null, Query = query, Offset = 0, IndexOffset = 0,
            Card = null, Surface = null, Popup = null,
        });
        if (previous.Kind == kind && previous.Query == query) RenderIndex();
        var next = CaptureLocation();
        Navigated?.Invoke(next, next);
        var index = ActiveIndex;
        var row = index.Items.OfType<IndexRow>().FirstOrDefault(item => item.Key == key);
        if (row is null) return;
        _pendingHighlight = row;
        index.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
        index.UpdateLayout();
        if (FindScroll(index) is { } viewport && viewport.VerticalOffset >= viewport.ScrollableHeight - 1 &&
            index.ContainerFromItem(row) is FrameworkElement item)
        {
            var gap = item.TransformToVisual(viewport).TransformPoint(new(0, 0)).Y;
            if (gap > 8)
            {
                index.Footer = new Border { Height = gap, IsHitTestVisible = false };
                index.UpdateLayout();
                index.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
            }
        }
        DispatcherQueue.TryEnqueue(() => HighlightCreatedEntry(index, row));
    }

    private void HighlightCreatedEntry(ListViewBase index, IndexRow row)
    {
        if (!ReferenceEquals(_pendingHighlight, row) || _key is not null || ActiveIndex != index ||
            index.ContainerFromItem(row) is not SelectorItem container) return;
        if (!container.Focus(FocusState.Keyboard)) return;
        _pendingHighlight = null;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled ||
            container.ContentTemplateRoot is not FrameworkElement root ||
            root.FindName("FeedbackLayer") is not UIElement layer) return;
        var pulse = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (milliseconds, opacity) in new[]
            { (0, FeedbackIdleOpacity), (125, .36), (300, FeedbackIdleOpacity), (425, .36), (700, FeedbackIdleOpacity) })
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
                Value = opacity,
            });
        StopCardPulse();
        var storyboard = _cardPulse = new Storyboard();
        _pulsingContainer = container;
        _pulsingLayer = layer;
        storyboard.Children.Add(pulse);
        Storyboard.SetTarget(pulse, layer);
        Storyboard.SetTargetProperty(pulse, nameof(Opacity));
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_cardPulse, storyboard)) StopCardPulse();
        };
        storyboard.Begin();
    }

    private void StopCardPulse()
    {
        var storyboard = _cardPulse;
        _cardPulse = null;
        _pulsingContainer = null;
        storyboard?.Stop();
        // Stop 不保证还原中途打断的 opacity；显式写回，避免容器复用后残留蓝绿色。
        if (_pulsingLayer is { } layer) layer.Opacity = FeedbackIdleOpacity;
        _pulsingLayer = null;
    }

    private async Task Saved(string message)
    {
        await RefreshAsync();
        Changed?.Invoke();
        ToastRequested?.Invoke(message, ToastKind.Success, null);
    }

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { await Message("无法完成操作", ex.Message); }
    }

    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await Run(action);
        return button;
    }

    /// <summary>
    /// 图标 + 文字。同一工具条内的按钮要一起有图标或一起没有；需要图标时用 `FontIcon`，
    /// 不要在文字里手写「＋」这类字符——它和真正的图标字号、对齐都对不上。
    /// </summary>
    private static StackPanel IconLabel(string glyph, string text, out FontIcon icon, out TextBlock label)
    {
        icon = new FontIcon { Glyph = glyph, FontSize = 12 };
        label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        host.Children.Add(icon);
        host.Children.Add(label);
        return host;
    }

    private Button IconActionButton(string glyph, string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = IconLabel(glyph, text, out _, out _),
            Padding = new(10, 4, 10, 4), MinWidth = 0, MinHeight = 32,
        };
        button.Resources = LocalButtonResources();
        button.Click += async (_, _) => await Run(action);
        return button;
    }

    private static StackPanel Section(string label, int? count = null, FrameworkElement? action = null)
    {
        var group = new StackPanel { Spacing = 9 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(Text(count.HasValue ? $"{label}  {count}" : label, 16, true));
        if (action is not null)
        {
            Grid.SetColumn(action, 1);
            header.Children.Add(action);
        }
        group.Children.Add(header);
        return group;
    }

    /// <summary>详情分区的通用选择/删除按钮，样式与图库「选择」按钮一致。</summary>
    private static (Button Select, Button Delete) LocalSelectionButtons(LocalSelectionController controller, Action onDelete)
    {
        var select = new Button { Padding = new(10, 4, 10, 4), MinWidth = 0, MinHeight = 32 };
        select.Content = IconLabel("\uE73A", "选择", out var selectIcon, out var selectLabel);
        select.Tag = (selectIcon, selectLabel);
        var delete = new Button { Padding = new(10, 4, 10, 4), MinWidth = 0, MinHeight = 32,
            Visibility = Visibility.Collapsed };
        delete.Content = IconLabel("\uE74D", "删除", out _, out _);
        delete.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        // 每个控件必须持有独立的 ResourceDictionary 实例；共享同一实例会让 WinUI 抛 ArgumentException。
        select.Resources = LocalButtonResources();
        delete.Resources = LocalButtonResources();
        select.Click += (_, _) => { if (!controller.Exit()) controller.Enter(); };
        delete.Click += (_, _) => onDelete();
        controller.BindButtons(select, delete, SyncLocalButtons);
        return (select, delete);
    }

    /// <summary>
    /// 选择按钮只改文字/图标（灰底样式不变）：文字变「取消选择」就是模式已打开的提示。
    /// 判据是「是否在选择模式」，不是选中数——否则一项未选时按钮毫无变化。
    /// </summary>
    private static void SyncLocalButtons(LocalSelectionController controller, Button select, Button delete)
    {
        var active = controller.IsActive;
        if (select.Tag is (FontIcon icon, TextBlock label))
        {
            icon.Glyph = active ? "\uE711" : "\uE73A";
            label.Text = active ? "取消选择" : "选择";
        }
        delete.Visibility = controller.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ResourceDictionary LocalButtonResources() => (ResourceDictionary)XamlReader.Load("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <ResourceDictionary.ThemeDictionaries>
            <ResourceDictionary x:Key="Default">
              <StaticResource x:Key="ButtonBackground" ResourceKey="ControlFillColorDefaultBrush"/>
              <StaticResource x:Key="ButtonBackgroundPointerOver" ResourceKey="ControlFillColorSecondaryBrush"/>
              <StaticResource x:Key="ButtonBackgroundPressed" ResourceKey="ControlFillColorTertiaryBrush"/>
              <StaticResource x:Key="ButtonForeground" ResourceKey="TextFillColorPrimaryBrush"/>
              <StaticResource x:Key="ButtonBorderBrush" ResourceKey="ControlElevationBorderBrush"/>
            </ResourceDictionary>
            <ResourceDictionary x:Key="Light">
              <StaticResource x:Key="ButtonBackground" ResourceKey="ControlFillColorDefaultBrush"/>
              <StaticResource x:Key="ButtonBackgroundPointerOver" ResourceKey="ControlFillColorSecondaryBrush"/>
              <StaticResource x:Key="ButtonBackgroundPressed" ResourceKey="ControlFillColorTertiaryBrush"/>
              <StaticResource x:Key="ButtonForeground" ResourceKey="TextFillColorPrimaryBrush"/>
              <StaticResource x:Key="ButtonBorderBrush" ResourceKey="ControlElevationBorderBrush"/>
            </ResourceDictionary>
            <ResourceDictionary x:Key="HighContrast">
              <StaticResource x:Key="ButtonBackground" ResourceKey="SystemControlBackgroundBaseLowBrush"/>
              <StaticResource x:Key="ButtonBackgroundPointerOver" ResourceKey="SystemColorHighlightTextColorBrush"/>
              <StaticResource x:Key="ButtonBackgroundPressed" ResourceKey="SystemColorHighlightTextColorBrush"/>
              <StaticResource x:Key="ButtonForeground" ResourceKey="SystemColorButtonTextColorBrush"/>
              <StaticResource x:Key="ButtonBorderBrush" ResourceKey="SystemControlForegroundTransparentBrush"/>
            </ResourceDictionary>
          </ResourceDictionary.ThemeDictionaries>
        </ResourceDictionary>
        """);

    private static StackPanel Horizontal() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private static Brush BrushFor(string token) => (Brush)Application.Current.Resources[token];
    private static Style? StyleFor(bool selected) => selected ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
    private static void UpdateTabStyle(Button button, bool selected)
    {
        if (selected) button.Style = StyleFor(true);
        else button.ClearValue(StyleProperty);
    }
    private static TextBlock Text(string text, double size = 14, bool strong = false, double opacity = 1) => new()
    {
        Text = text, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        Opacity = opacity, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };
}
