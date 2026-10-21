using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Gallery.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Gallery.Controls.Review;

/// <summary>归档审阅页：圆环单图审阅与网格多选；多选标签草稿显式合并应用，另提供清空标签。</summary>
public sealed class ReviewPage : Grid
{
    private readonly Window _owner;
    private readonly ReviewCarousel _carousel = new();
    private readonly Grid _carouselHost = new();
    private readonly TextBlock _count = new() { FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _revealAllText = new() { FontSize = 12, TextDecorations = Windows.UI.Text.TextDecorations.Underline };
    private readonly HyperlinkButton _revealAll = new() { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
    private readonly PaneResizer _splitter = new() { Width = 16, Margin = new Thickness(0) };
    // 通用连续滚动条：拖动 1:1 跟手，点击轨道任意位置由控件自己做同步短滑。
    private readonly ContinuousScrollBar _queueScroll = new()
    {
        Margin = new Thickness(0, 14, -22, 14),
        Visibility = Visibility.Collapsed,
        ViewportSize = 9,
    };
    private bool _syncingScroll, _scrollDragging;
    // 分割线默认取当前拖出来的比例（左栏占两侧内容宽度的 48%），拖动后写回设置。
    private double _splitRatio = .48;
    private readonly Border _dropOverlay = new()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = .28 },
        BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(10),
        Margin = new Thickness(12),
        AllowDrop = false,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        Child = new TextBlock
        {
            Text = "松开即可导入到待审核",
            FontSize = 16,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };
    private readonly TextBlock _summary = Caption();
    private readonly TextBlock _title = new() { FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stateBadge = new() { FontSize = 12, Opacity = .7, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _time = Caption();
    private readonly TextBlock _spec = Caption();
    private readonly TextBlock _pageUrl = Caption();
    private readonly TextBlock _pathLabel = new() { Text = "本地文件", FontSize = 12, Opacity = .6 };
    private readonly TextBlock _path = Caption();
    private readonly TextBlock _currentPathLabel = new() { Text = "当前位置", FontSize = 12, Opacity = .6, Visibility = Visibility.Collapsed };
    private readonly TextBlock _currentPath = Caption();
    private RowDefinition? _currentPathRow;
    private readonly TextBlock _scopeHint = new() { FontSize = 11, Opacity = .65, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Text = "批量操作范围：全部待归档图片" };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly StackPanel _tagGroups = new() { Spacing = 15 };
    private readonly StackPanel _archiveDirGroup = new() { Spacing = 10 };
    private readonly TextBox _destination = new() { PlaceholderText = "选择图库内的归档目录", MinWidth = 0 };
    private readonly Button _pickFolder = new() { Content = "选择文件夹…", Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button _archive = new() { Content = "归档此图，并查看下一张", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _ignore = new() { Content = "忽略此图", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _archiveAll = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _ignoreAll = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleButton _batchToggle = new() { Content = "批量审核", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _selectAll = new() { Content = "全选", Visibility = Visibility.Collapsed };
    private readonly SelectionDropDown _cardSize = new() { Visibility = Visibility.Collapsed, MinWidth = 70 };
    private readonly Button _applyTags = new() { Content = "应用标签", HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly Button _clearTags = new() { Content = "清空标签", Visibility = Visibility.Collapsed };
    private readonly TextBlock _batchHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7, Visibility = Visibility.Collapsed };
    private readonly StackPanel _imageDetails = new() { Spacing = 12 };
    private readonly StackPanel _tagSection = new() { Spacing = 12 };
    private readonly TextBlock _detailsHeading = new() { Text = "图片详情", FontSize = 12, Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
    private readonly HashSet<int> _batchSelected = [];
    private bool _batchMode;
    private bool _batchTagsDirty;
    private HashSet<int>? _boxSelectionBase;
    private CancellationTokenSource? _batchClassifyCts;
    private CancellationTokenSource? _basicsCts;
    private Task? _basicsTask;
    private bool _basicsDirty;
    private int _basicsGeneration;
    private readonly ConcurrentDictionary<int, byte> _basicsFailed = new();
    private readonly ConcurrentQueue<int> _basicsExtras = new();
    private Action? _ratingRebuild;
    private string _batchRatingSummary = "";
    private int _singleIndexBeforeBatch;
    private bool BatchMultiple => _batchMode && _batchSelected.Count > 1;
    private readonly Button _restore = new() { Content = "恢复待处理", Visibility = Visibility.Collapsed };
    private readonly Button _showInGallery = new() { Content = "在图库中查看", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly Button _showInExplorer = new() { Content = "打开所在文件夹", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly Button _cleanHistory = new() { Content = "清理历史…", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly SelectionDropDown _states = new() { MinWidth = 115, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid _body = new() { ColumnSpacing = 24 };
    private Tags.TagChip? _ratingBadge;
    // 右侧留 16 给浮层滚动条，否则它会压在标签行与归档目录输入框的右缘（UI_CONVENTIONS.md R1）。
    private readonly ScrollViewer _detailScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 16, 0) };
    private List<StoreService.PendingIngestRow> _rows = [];
    private StoreService.PendingIngestRow? _selected;
    private Dictionary<string, List<string>> _tags = [];
    private readonly Dictionary<string, SearchInputBox> _tagInputs = [];
    private readonly Dictionary<string, string> _imageSpecs = new(StringComparer.OrdinalIgnoreCase);
    private string _state = "pending";
    // 历史只加载最近这么多条；更早的交给「清理历史」处理，避免一次拉几万条。
    private const int HistoryLimit = 200;
    private int _index, _selectionGeneration;
    private bool _busy, _active = true;
    private TaskCompletionSource? _workCompleted;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _carousel.SetInteractionEnabled(!busy);
        if (busy) _workCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        else _workCompleted?.TrySetResult();
    }

    public async Task DrainWorkAsync()
    {
        _batchClassifyCts?.Cancel();
        _basicsCts?.Cancel();
        if (_workCompleted is { } work) await work.Task;
        if (_basicsTask is { } basics)
        {
            try { await basics; }
            catch (OperationCanceledException) { }
        }
    }
    private bool _destinationEdited;   // 用户手改过当前这张的归档目录
    private bool _settingDestination;  // 程序正在往 _destination 里填内容
    public event Action? Changed;

    public ReviewPage(Window owner)
    {
        _owner = owner;
        Background = new SolidColorBrush(Colors.Transparent);
        AddHandler(TappedEvent, new TappedEventHandler(OnBackgroundTapped), true);
        _path.MaxLines = 2;
        _path.TextTrimming = TextTrimming.CharacterEllipsis;
        _pageUrl.MaxLines = 1;
        _pageUrl.TextTrimming = TextTrimming.CharacterEllipsis;
        Padding = new Thickness(28, 24, 28, 22);
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46, GridUnitType.Star) });
        // 分隔线列给足 16px，把手直接放在列里，不再靠负边距撑布局。
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54, GridUnitType.Star) });
        _body.Children.Add(BuildQueue());
        var divider = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Background = ResourceBrush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 28, 0, 28), IsHitTestVisible = false };
        Grid.SetColumn(divider, 1);
        _body.Children.Add(divider);
        var details = BuildDetails();
        Grid.SetColumn(details, 2);
        _body.Children.Add(details);
        // 把手填满 16px 分隔列，滚动条在左侧空隙，两者保持独立命中区。
        if (AppService.Settings.Settings.layout_options.TryGetValue("review_split_ratio", out var savedSplit)
            && savedSplit.ValueKind == JsonValueKind.Number && savedSplit.TryGetDouble(out var savedRatio) && double.IsFinite(savedRatio))
            _splitRatio = Math.Clamp(savedRatio, .2, .8);
        _splitter.MinLength = 300;
        _splitter.CurrentLength = () => _body.ColumnDefinitions[0].ActualWidth;
        _splitter.WidthChanged += next =>
        {
            _body.ColumnDefinitions[0].Width = new GridLength(next);
            var content = SplitContentWidth();
            if (content > 0) _splitRatio = Math.Clamp(next / content, .2, .8);
        };
        _splitter.DragFinished += () =>
        {
            AppService.Settings.Settings.layout_options["review_split_ratio"] = JsonSerializer.SerializeToElement(Math.Round(_splitRatio, 4));
            AppService.Settings.Save();
        };
        Grid.SetColumn(_splitter, 1);
        _body.Children.Add(_splitter);
        Children.Add(_body);
        _revealAll.Content = _revealAllText;
        _carousel.SelectionRequested += index =>
        {
            if (_batchMode) ToggleBatchSelection(index);
            else Select(index);
        };
        _carousel.ClearSelectionRequested += ClearBatchSelection;
        _carousel.BoxSelectionRequested += (indices, additive, phase) =>
        {
            if (!_batchMode || _busy || AppService.IsShuttingDown) return;
            if (phase == "start") _boxSelectionBase = new HashSet<int>(_batchSelected);
            if (_boxSelectionBase is null) return;
            var wasMultiple = _boxSelectionBase.Count > 1;
            _batchSelected.Clear();
            if (phase == "cancel" || additive) _batchSelected.UnionWith(_boxSelectionBase);
            if (phase != "cancel")
                _batchSelected.UnionWith(indices.Where(index => index >= 0 && index < _rows.Count).Select(index => _rows[index].Id));
            // 拖动只预览勾选，松手再切换详情，避免经过 0／1 张时丢掉尚未应用的草稿。
            if (phase == "end") RenderBatchSelection(!wasMultiple);
            else
            {
                _carousel.SetBatchMode(true, _batchSelected);
                _count.Text = $"已选 {_batchSelected.Count} / {_rows.Count}";
            }
            if (phase is "end" or "cancel") _boxSelectionBase = null;
        };
        _carousel.OpenRequested += (row, source) => Open(source ? row.PageUrl : IngestReview.CurrentPath(row), source);
        _carousel.FilesDropped += async paths => await ImportAsync(paths);
        _carousel.DragStateChanged += over => _dropOverlay.Visibility = over ? Visibility.Visible : Visibility.Collapsed;
        // 连续滚动轴：拖动 1:1 跟随；点击轨道由控件自己做同步短动画，详情在松手/动画结束后提交。
        _queueScroll.ValueChanged += value =>
        {
            if (_syncingScroll || _busy) return;
            _carousel.ScrollTo(value, follow: true);
        };
        _queueScroll.InteractionStarted += () =>
        {
            _scrollDragging = true;
        };
        _queueScroll.InteractionCompleted += () =>
        {
            _scrollDragging = false;
            CommitScroll();
        };
        AutomationProperties.SetName(_queueScroll, "图片列表滚动条");
        _carousel.SelectedImageLoaded += item =>
        {
            var text = $"{item.PixelWidth} × {item.PixelHeight} · {item.Extension} · {item.FileSize / 1024.0 / 1024:0.##} MB";
            _imageSpecs[item.FullPath] = text;
            if (_selected is { } row && string.Equals(IngestReview.CurrentPath(row), item.FullPath, StringComparison.OrdinalIgnoreCase))
                _spec.Text = text;
        };
        _destination.TextChanged += (_, _) =>
        {
            if (!_settingDestination) _destinationEdited = true;
        };
        AppService.Settings.Changed += RefreshDestination;
        _revealAll.Click += (_, _) =>
        {
            _carousel.ToggleRevealAll();
            UpdateButtons();
        };
        _archive.Click += async (_, _) => await ArchiveAsync(false);
        _archiveAll.Click += async (_, _) => await ArchiveAsync(true);
        _ignore.Click += (_, _) => Ignore(false);
        _ignoreAll.Click += (_, _) => Ignore(true);
        _batchToggle.Click += (_, _) => SetBatchMode(_batchToggle.IsChecked == true);
        _selectAll.Click += (_, _) =>
        {
            if (_busy) return;
            var wasMultiple = BatchMultiple;
            if (_batchSelected.Count == _rows.Count) _batchSelected.Clear();
            else _batchSelected.UnionWith(_rows.Select(row => row.Id));
            RenderBatchSelection(!wasMultiple);
        };
        _applyTags.Click += async (_, _) => await ApplyBatchTagsAsync();
        _clearTags.Click += async (_, _) => await ApplyBatchTagsAsync(true);
        foreach (var (key, label) in new[] { ("small", "小"), ("medium", "中"), ("large", "大") })
            _cardSize.Items.Add(new ComboBoxItem { Tag = key, Content = label });
        _cardSize.SelectedIndex = 1;
        _cardSize.SelectionChanged += (_, _) =>
        {
            if (_cardSize.SelectedItem is ComboBoxItem { Tag: string size }) _carousel.SetGridSize(size);
        };
        ToolTipService.SetToolTip(_cardSize, "卡片大小");
        AutomationProperties.SetName(_cardSize, "批量审核卡片大小");
        _restore.Click += (_, _) => Restore();
        _showInGallery.Click += async (_, _) => await ShowInGalleryAsync();
        _showInExplorer.Click += (_, _) => RevealInExplorer();
        _cleanHistory.Click += async (_, _) => await CleanHistoryAsync();
        _pickFolder.Click += async (_, _) => await PickFolderAsync();
        Loaded += (_, _) => { _active = true; _carousel.SetActive(true); Reload(); };
        Unloaded += (_, _) => SetActive(false);
        ActualThemeChanged += (_, _) => ApplyColors();
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth < 850;
            var pad = new Thickness(narrow ? 16 : 28, 20, narrow ? 16 : 28, 20);
            if (Padding != pad) Padding = pad;
            var spacing = narrow ? 16 : 28;
            if (_body.ColumnSpacing != spacing) _body.ColumnSpacing = spacing;
            // 滚动条落在圆环与分割线之间的空隙里，不压住卡片。
            var scrollMargin = new Thickness(0, 14, -(spacing + 16) / 2.0, 14);
            if (_queueScroll.Margin != scrollMargin) _queueScroll.Margin = scrollMargin;
            var content = SplitContentWidth();
            var maxQueue = Math.Max(300, content - 420);
            if (Math.Abs(_splitter.MaxLength - maxQueue) > .5) _splitter.MaxLength = maxQueue;
            // 固定按比例还原：窗口缩放时保持当前拖出来的左右比例。
            var target = Math.Clamp(_splitRatio * content, _splitter.MinLength, maxQueue);
            if (Math.Abs(_body.ColumnDefinitions[0].Width.Value - target) > .5)
                _body.ColumnDefinitions[0].Width = new GridLength(target);
        };
        ApplyColors();
    }

    private Grid BuildQueue()
    {
        var panel = new Grid { RowSpacing = 10 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Spacing = 5 };
        var heading = new Grid();
        heading.Children.Add(new TextBlock { Text = "归档审阅", FontSize = 25, FontWeight = FontWeights.SemiBold });
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        heading.Children.Add(_count);
        header.Children.Add(heading);
        header.Children.Add(_summary);
        var batchActions = new Grid { ColumnSpacing = 8 };
        batchActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        batchActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        batchActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var selectionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        selectionActions.Children.Add(_batchToggle);
        selectionActions.Children.Add(_selectAll);
        batchActions.Children.Add(selectionActions);
        Grid.SetColumn(_cardSize, 2);
        batchActions.Children.Add(_cardSize);
        header.Children.Add(batchActions);
        panel.Children.Add(header);
        // 拖拽范围就是左侧卡片容器：提示层也放在这里，不覆盖整页。
        _carouselHost.AllowDrop = true;
        _carouselHost.AddHandler(DragOverEvent, new DragEventHandler(OnDragOver), true);
        _carouselHost.AddHandler(DragLeaveEvent, new DragEventHandler(OnDragLeave), true);
        _carouselHost.AddHandler(DropEvent, new DragEventHandler(OnDrop), true);
        _carouselHost.Children.Add(_carousel);
        _carouselHost.Children.Add(_queueScroll);
        _carouselHost.Children.Add(_dropOverlay);
        Grid.SetRow(_carouselHost, 1);
        panel.Children.Add(_carouselHost);
        var footer = new StackPanel { Spacing = 10 };
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.Children.Add(_archiveAll);
        Grid.SetColumn(_ignoreAll, 1);
        actions.Children.Add(_ignoreAll);
        footer.Children.Add(actions);
        footer.Children.Add(_revealAll);
        footer.Children.Add(_cleanHistory);
        footer.Children.Add(_scopeHint);
        Grid.SetRow(footer, 2);
        panel.Children.Add(footer);
        return panel;
    }

    private Grid BuildDetails()
    {
        var host = new Grid { RowSpacing = 20, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Stretch };
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel { Spacing = 12 };
        var top = new Grid { MinHeight = 34 };
        top.Children.Add(_detailsHeading);
        foreach (var (key, title) in new[] { ("pending", "待归档"), ("missing", "文件丢失"), ("history", "历史") })
            _states.Items.Add(new ComboBoxItem { Tag = key, Content = title });
        _states.SelectedIndex = 0;
        _states.SelectionChanged += (_, _) =>
        {
            if (_states.SelectedItem is ComboBoxItem { Tag: string state } && state != _state && !_busy)
            {
                _state = state;
                SetBatchMode(false);
                _selected = null;
                _index = 0;
                Reload();
            }
        };
        top.Children.Add(_states);
        panel.Children.Add(top);
        var titleRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 2) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(_title);
        Grid.SetColumn(_stateBadge, 1);
        titleRow.Children.Add(_stateBadge);
        _imageDetails.Children.Add(titleRow);
        var facts = new Grid { RowSpacing = 7, ColumnSpacing = 12 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddFact("加入时间", _time);
        AddFact("图片规格", _spec);
        AddFact("来源页面", _pageUrl);
        AddFactWithLabel(_pathLabel, _path);
        var currentRow = facts.RowDefinitions.Count;
        AddFactWithLabel(_currentPathLabel, _currentPath);
        _currentPathRow = facts.RowDefinitions[currentRow];
        _imageDetails.Children.Add(facts);
        SetCurrentPathVisible(false);
        _imageDetails.Children.Add(new Border { Height = 1, Background = ResourceBrush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 6, 0, 4) });
        panel.Children.Add(_imageDetails);
        panel.Children.Add(_batchHint);
        _tagSection.Children.Add(new TextBlock { Text = "标签", FontSize = 18, FontWeight = FontWeights.SemiBold });
        _tagSection.Children.Add(_tagGroups);
        var tagActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tagActions.Children.Add(_applyTags);
        tagActions.Children.Add(_clearTags);
        _tagSection.Children.Add(tagActions);
        panel.Children.Add(_tagSection);
        _detailScroll.Content = panel;
        host.Children.Add(_detailScroll);
        var archivePanel = new StackPanel { Spacing = 10 };
        archivePanel.Children.Add(new Border { Height = 1, Background = ResourceBrush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 0, 0, 6) });
        _archiveDirGroup.Children.Add(new TextBlock { Text = "归档目录", FontSize = 12, Opacity = .7 });
        var folder = new Grid { ColumnSpacing = 8 };
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folder.Children.Add(_destination);
        Grid.SetColumn(_pickFolder, 1);
        folder.Children.Add(_pickFolder);
        _archiveDirGroup.Children.Add(folder);
        archivePanel.Children.Add(_archiveDirGroup);
        var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        _archive.MinHeight = _ignore.MinHeight = 40;
        actions.Children.Add(_archive);
        actions.Children.Add(_ignore);
        archivePanel.Children.Add(actions);
        actions.Children.Add(_showInGallery);
        actions.Children.Add(_showInExplorer);
        archivePanel.Children.Add(_restore);
        archivePanel.Children.Add(_status);
        Grid.SetRow(archivePanel, 1);
        host.Children.Add(archivePanel);
        return host;

        void AddFact(string title, TextBlock value)
            => AddFactWithLabel(new TextBlock { Text = title, FontSize = 12, Opacity = .6 }, value);

        void AddFactWithLabel(TextBlock label, TextBlock value)
        {
            var row = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            facts.Children.Add(label);
            facts.Children.Add(value);
        }
    }

    private void SetCurrentPathVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _currentPathLabel.Visibility = _currentPath.Visibility = visibility;
        if (_currentPathRow is not null)
            _currentPathRow.Height = visible ? GridLength.Auto : new GridLength(0);
    }

    public void Reload()
    {
        if (_busy || AppService.IsShuttingDown || AppService.Store is not { } store) return;
        var keep = _selected?.Id;
        var history = _state == "history";
        _rows = history
            ? store.IngestHistory(HistoryLimit)
            : store.IngestRows(_state, int.MaxValue).OrderBy(row => row.ReceivedAt).ThenBy(row => row.Id).ToList();
        if (history)
        {
            lock (AppService.DbLock)
                _rows = _rows.Select(row => row with { ResolvedPath = IngestReview.ResolveCurrentPath(store, row) }).ToList();
        }
        var pending = store.PendingIngestCount();
        _summary.Text = _state switch
        {
            "pending" => "待归档",
            "missing" => "文件丢失",
            _ => "历史",
        };
        _scopeHint.Text = _state switch
        {
            "pending" => "批量操作范围：全部待归档图片",
            "missing" => "源文件不可用；找回后可点「恢复待处理」",
            _ => HistoryHint(store),
        };
        _archiveAll.Content = $"一键归档（{pending}）";
        _ignoreAll.Content = $"一键忽略（{pending}）";
        if (string.IsNullOrWhiteSpace(_destination.Text))
        {
            var settings = AppService.Settings.Settings;
            _settingDestination = true;
            _destination.Text = settings.archive_root.Length > 0
                ? Path.Combine(settings.archive_root, settings.archive_sub)
                : store.Roots().FirstOrDefault(r => r.Enabled)?.Path ?? "";
            _settingDestination = false;
        }
        var found = keep.HasValue ? _rows.FindIndex(r => r.Id == keep.Value) : -1;
        if (_batchMode)
        {
            _batchSelected.IntersectWith(_rows.Select(row => row.Id));
            RenderBatchSelection(false);
        }
        else Select(found >= 0 ? found : Math.Min(_index, _rows.Count - 1), false);
        UpdateButtons();
        if (_state == "pending") StartBasicsEnrichment();
    }

    /// <summary>
    /// 待归档列表刷新后启动一次基础标签补全：分辨率/横竖图 + 空分级的模型判定。
    /// 同一时刻只跑一个后台任务；期间又有新记录进来就记脏，结束后再补一轮。
    /// </summary>
    private void StartBasicsEnrichment()
    {
        if (AppService.IsShuttingDown || AppService.Store is not { } store) return;
        if (_state != "pending" && _basicsExtras.IsEmpty) return;
        if (_basicsTask is { IsCompleted: false })
        {
            _basicsDirty = true;
            return;
        }

        var service = new IngestBasicsService(store, AppService.Settings.Settings.nsfw_model_dir);
        var ids = _rows
            .Where(row => row.State == "pending"
                && !_basicsFailed.ContainsKey(row.Id)
                && service.NeedsWork(row))
            .OrderByDescending(row => row.ReceivedAt)
            .ThenByDescending(row => row.Id)
            .Select(row => row.Id)
            .ToList();
        while (_basicsExtras.TryDequeue(out var extraId))
        {
            if (_basicsFailed.ContainsKey(extraId)) continue;
            var extra = store.IngestRow(extraId);
            if (extra is null) continue;
            if (ArchiveService.NormalizeRating(extra.Rating).Length > 0) continue;
            if (IngestReview.RatingChecked(extra.Payload)) continue;
            ids.Add(extraId);
        }
        ids = ids.Distinct().ToList();
        _basicsDirty = false;
        if (ids.Count == 0) return;

        if (_basicsCts is null || _basicsCts.IsCancellationRequested)
        {
            _basicsCts?.Dispose();
            _basicsCts = new CancellationTokenSource();
        }
        var token = _basicsCts.Token;
        var generation = ++_basicsGeneration;
        _basicsTask = Task.Run(() => RunBasics(service, ids, token, generation));
    }

    /// <summary>
    /// 归档成功后补排一次基础分级。即使这张图在后台批次之外被归档，
    /// 模型空下来后也会按图库里的当前位置补写。
    /// </summary>
    private void QueueBasics(int id)
    {
        if (AppService.IsShuttingDown || AppService.Store is not { } store) return;
        if (_basicsFailed.ContainsKey(id)) return;
        if (!AppService.FakeNsfwScorer
            && !NsfwScorer.LooksConfigured(AppService.Settings.Settings.nsfw_model_dir)) return;
        var row = store.IngestRow(id);
        if (row is null) return;
        if (ArchiveService.NormalizeRating(row.Rating).Length > 0) return;
        if (IngestReview.RatingChecked(row.Payload)) return;

        _basicsExtras.Enqueue(id);
        if (_basicsTask is { IsCompleted: false })
        {
            _basicsDirty = true;
            return;
        }
        StartBasicsEnrichment();
    }

    private void RunBasics(IngestBasicsService service, List<int> ids,
        CancellationToken token, int generation)
    {
        try
        {
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                BasicOutcome outcome;
                try
                {
                    outcome = service.Enrich(id, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _basicsFailed[id] = 0;
                    AppService.DebugLog($"ingest basics #{id}: {ex.Message}");
                    continue;
                }

                if (outcome.Error.Length > 0)
                {
                    _basicsFailed[id] = 0;
                    AppService.DebugLog($"ingest basics #{id}: {outcome.Error}");
                    continue;
                }
                if (outcome.Rating.Length == 0 && !outcome.ResolutionChanged) continue;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (generation != _basicsGeneration) return;
                    ApplyBasicsToUi(id);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _basicsGeneration) return;
                _basicsTask = null;
                if (_basicsDirty && !AppService.IsShuttingDown)
                {
                    _basicsDirty = false;
                    StartBasicsEnrichment();
                }
            });
        }
    }

    /// <summary>单张模式下只把结果落到当前选中的那张；正在输入的其他标签不动。批量面板不跟着改。</summary>
    private void ApplyBasicsToUi(int id)
    {
        if (AppService.IsShuttingDown || AppService.Store is not { } store) return;
        var index = _rows.FindIndex(row => row.Id == id);
        if (index < 0) return;

        StoreService.PendingIngestRow? updated;
        try { updated = store.IngestRow(id); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            AppService.DebugLog("ingest basics refresh: " + ex.Message);
            return;
        }
        if (updated is null) return;
        updated = updated with { ResolvedPath = _rows[index].ResolvedPath };
        _rows[index] = updated;

        if (!_active || _busy || _batchMode || _selected?.Id != id) return;
        _selected = updated;
        var rating = ArchiveService.NormalizeRating(updated.Rating);
        if (rating.Length == 0 || !_tags.TryGetValue("rating", out var current) || current.Count > 0) return;

        _tags["rating"] = [rating];
        _ratingRebuild?.Invoke();
        _carousel.RefreshPreview(preserveRevealed: true);
    }

    private string HistoryHint(StoreService store)
    {
        var stats = store.IngestStats();
        var total = stats.GetValueOrDefault("done") + stats.GetValueOrDefault("ignored");
        return total > _rows.Count
            ? $"历史仅显示最近 {_rows.Count} 条，共 {total} 条；更早的用「清理历史」处理"
            : "已归档与已忽略的队列记录；可跳回图库查看原图";
    }

    private void SetBatchMode(bool enabled)
    {
        if (_busy || AppService.IsShuttingDown) { _batchToggle.IsChecked = _batchMode; return; }
        enabled &= _state == "pending";
        if (_batchMode == enabled) return;
        if (enabled) _singleIndexBeforeBatch = _index;
        _batchMode = enabled;
        _batchToggle.IsChecked = enabled;
        _batchSelected.Clear();
        _boxSelectionBase = null;
        _batchRatingSummary = "";
        _batchTagsDirty = false;
        _carousel.SetBatchMode(enabled, _batchSelected, enabled ? null : _singleIndexBeforeBatch);
        if (enabled) RenderBatchSelection(true);
        else Select(Math.Clamp(_singleIndexBeforeBatch, 0, Math.Max(0, _rows.Count - 1)), false);
        UpdateButtons();
    }

    private void ClearBatchSelection()
    {
        if (!_batchMode || _busy || AppService.IsShuttingDown || _batchSelected.Count == 0) return;
        _batchSelected.Clear();
        _boxSelectionBase = null;
        RenderBatchSelection(true);
    }

    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs args)
    {
        if (args.OriginalSource is not DependencyObject source) return;
        for (DependencyObject? current = source; current is not null && current != this; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, _carouselHost) || current is Tags.TagChip
                || current is TextBlock { IsTextSelectionEnabled: true }
                || current is Control and not ScrollViewer) return;
        }
        ClearBatchSelection();
    }

    private void ToggleBatchSelection(int index)
    {
        if (_busy || AppService.IsShuttingDown || index < 0 || index >= _rows.Count) return;
        var wasMultiple = BatchMultiple;
        if (!_batchSelected.Add(_rows[index].Id)) _batchSelected.Remove(_rows[index].Id);
        RenderBatchSelection(!wasMultiple);
    }

    private void RenderBatchSelection(bool resetTags)
    {
        if (_batchSelected.Count == 1)
        {
            _batchTagsDirty = false;
            Select(_rows.FindIndex(row => _batchSelected.Contains(row.Id)), false);
        }
        else
        {
            _selected = null;
            var rebuildTags = resetTags || _batchSelected.Count == 0 || _tagGroups.Children.Count == 0;
            if (resetTags || _batchSelected.Count == 0)
            {
                _tags = IngestReview.Categories.ToDictionary(category => category, _ => new List<string>());
                _batchTagsDirty = false;
                _batchRatingSummary = "";
            }
            if (rebuildTags)
            {
                ++_selectionGeneration;
                _tagGroups.Children.Clear();
                _tagInputs.Clear();
                _ratingBadge = null;
                _ratingRebuild = null;
                if (BatchMultiple)
                    for (var i = 0; i < IngestReview.Categories.Length; i++)
                        BuildTagGroup(IngestReview.Categories[i], IngestReview.Titles[i]);
            }
            _carousel.SetItems(_rows, _index);
            if (resetTags || _batchSelected.Count == 0) _destinationEdited = false;
            RefreshDestination();
            _status.Text = "";
        }
        _queueScroll.Visibility = Visibility.Collapsed;
        _count.Text = $"已选 {_batchSelected.Count} / {_rows.Count}";
        _carousel.SetBatchMode(true, _batchSelected);
        UpdateButtons();
    }

    private async Task ApplyBatchTagsAsync(bool clear = false)
    {
        if (_busy || !BatchMultiple || AppService.IsShuttingDown || AppService.Store is not { } store) return;
        // 应用也接收输入框里尚未按回车的标签，不让可见输入被漏掉。
        if (!clear) foreach (var (category, input) in _tagInputs)
        {
            var name = input.Text.Trim();
            if (name.Length > 0 && !_tags[category].Contains(name, StringComparer.OrdinalIgnoreCase))
                _tags[category].Add(name);
        }
        if (!clear)
        {
            ++_selectionGeneration;
            _tagGroups.Children.Clear();
            _tagInputs.Clear();
            _ratingRebuild = null;
            for (var i = 0; i < IngestReview.Categories.Length; i++)
                BuildTagGroup(IngestReview.Categories[i], IngestReview.Titles[i]);
        }
        var ids = _batchSelected.ToArray();
        var tags = _tags.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        var rating = tags["rating"].FirstOrDefault() ?? "";
        if (!clear) _batchTagsDirty = true;
        SetBusy(true);
        UpdateButtons();
        try
        {
            var applied = await Task.Run(() =>
            {
                using var tx = store.Transaction();
                var count = 0;
                foreach (var id in ids)
                {
                    if (store.IngestRow(id) is not { State: "pending" } row) continue;
                    var itemRating = clear ? "" : rating.Length > 0 ? rating : ArchiveService.NormalizeRating(row.Rating);
                    var itemTags = clear
                        ? IngestReview.Categories.ToDictionary(category => category, _ => new List<string>())
                        : IngestReview.Tags(row.Payload, row.Rating);
                    if (!clear)
                        foreach (var category in IngestReview.Categories.Where(category => category != "rating"))
                            itemTags[category] = itemTags[category].Concat(tags[category]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    itemTags["rating"] = itemRating.Length > 0 ? [itemRating] : [];
                    store.UpdateIngestReview(id, IngestReview.UpdatePayload(row.Payload, itemTags, itemRating), itemRating);
                    count++;
                }
                tx.Success = true;
                return count;
            });
            _batchTagsDirty = false;
            if (clear)
            {
                _tags = IngestReview.Categories.ToDictionary(category => category, _ => new List<string>());
                _batchRatingSummary = "";
                ++_selectionGeneration;
                _tagGroups.Children.Clear();
                _tagInputs.Clear();
                _ratingBadge = null;
                _ratingRebuild = null;
                for (var i = 0; i < IngestReview.Categories.Length; i++)
                    BuildTagGroup(IngestReview.Categories[i], IngestReview.Titles[i]);
            }
            SetBusy(false);
            Reload();
            _carousel.RefreshPreview(preserveRevealed: true);
            _status.Text = clear ? $"已清空 {applied} 张图片的全部标签" : $"已合并应用 {applied} 张图片的标签";
        }
        catch (Exception ex) when (ex is JsonException or Microsoft.Data.Sqlite.SqliteException)
        { _status.Text = (clear ? "批量清空失败：" : "批量应用失败：") + ex.Message; }
        finally { SetBusy(false); UpdateButtons(); OpenDeferredNotification(); }
    }

    private void Select(int index, bool revealScroll = true)
    {
        if (_busy) return;
        var changed = _index != (index = _rows.Count == 0 ? 0 : Math.Clamp(index, 0, _rows.Count - 1));
        _index = index;
        _selected = _rows.Count == 0 ? null : _rows[_index];
        _syncingScroll = true;
        _queueScroll.Maximum = Math.Max(0, _rows.Count - 1);
        _queueScroll.ViewportSize = Math.Min(9, Math.Max(1, _rows.Count));
        // 拖动中不回写滑块，松手后再吸附到整数。
        if (!_scrollDragging) _queueScroll.Value = _index;
        _queueScroll.Visibility = !_batchMode && _rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count > 1 && revealScroll && changed) _queueScroll.NotifyActivity();
        _syncingScroll = false;
        ++_selectionGeneration;
        _carousel.SetItems(_rows, _index);
        _count.Text = _rows.Count == 0 ? "0 / 0" : $"{_index + 1} / {_rows.Count}";
        _tagGroups.Children.Clear();
        _tagInputs.Clear();
        _ratingBadge = null;
        _ratingRebuild = null;
        _destinationEdited = false;
        if (_selected is not { } row)
        {
            _title.Text = _state switch
            {
                "pending" => "没有待归档图片",
                "missing" => "没有文件丢失记录",
                _ => "没有历史记录",
            };
            _time.Text = _spec.Text = _pageUrl.Text = _path.Text = _currentPath.Text = "";
            _pathLabel.Text = "本地文件";
            _stateBadge.Visibility = Visibility.Collapsed;
            SetCurrentPathVisible(false);
            _status.Text = "";
            _tags = [];
            UpdateButtons();
            return;
        }
        _title.Text = row.InboxName.Length > 0 ? row.InboxName : Path.GetFileName(IngestReview.LocalPath(row));
        _time.Text = DateTimeOffset.FromUnixTimeSeconds((long)row.ReceivedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        _spec.Text = _imageSpecs.GetValueOrDefault(IngestReview.CurrentPath(row), "—");
        _pageUrl.Text = row.PageUrl.Length == 0 ? "未记录" : row.PageUrl;
        var local = IngestReview.LocalPath(row);
        _pathLabel.Text = row.State switch
        {
            "done" => "归档位置",
            "ignored" => "原文件",
            _ => "本地文件",
        };
        var suffix = File.Exists(local) ? "" : _state == "history" ? "（已不在原位置）" : "（不可用）";
        _path.Text = local.Length == 0 ? "记录没有本地路径" : local + suffix;
        _stateBadge.Text = row.State switch
        {
            "done" => "已归档",
            "ignored" => "已忽略",
            _ => "",
        };
        _stateBadge.Visibility = _state == "history" ? Visibility.Visible : Visibility.Collapsed;
        var showCurrent = row.State == "done";
        SetCurrentPathVisible(showCurrent);
        if (showCurrent)
        {
            var current = IngestReview.CurrentPath(row);
            _currentPath.Text = current.Length == 0
                ? "未能在图库中定位（可能已改名或移出图库）"
                : current;
            ToolTipService.SetToolTip(_currentPath, current);
        }
        if (row.State == "missing")
            _status.Text = row.Note.Length > 0 ? row.Note : "源文件不可用";
        else if (_state != "pending")
            _status.Text = "";
        ToolTipService.SetToolTip(_path, local);
        ToolTipService.SetToolTip(_pageUrl, row.PageUrl);
        _tags = IngestReview.Tags(row.Payload, row.Rating);
        for (var i = 0; i < IngestReview.Categories.Length; i++)
            BuildTagGroup(IngestReview.Categories[i], IngestReview.Titles[i]);
        UpdateButtons();
        RefreshDestination();
    }

    /// <summary>通知点击后按队列记录定位，避免依赖列表排序或当前选中项。</summary>
    public void OpenPending(int id)
    {
        if (_busy) { _pendingNotificationId = id; return; }
        _state = "pending";
        SetBatchMode(false);
        _states.SelectedIndex = 0;
        Reload();
        var index = _rows.FindIndex(row => row.Id == id);
        if (index >= 0) Select(index, false);
        else _status.Text = "这张图片已处理，不在待归档队列中。";
    }

    private int? _pendingNotificationId;

    private int _tagLabelGeneration = -1;
    private readonly Dictionary<(string Category, string Name), string> _tagLabels = [];

    private string TagLabel(string category, string name)
    {
        if (category is not ("character" or "work") || AppService.Store is not { } store) return name;
        if (_tagLabelGeneration != _selectionGeneration) { _tagLabels.Clear(); _tagLabelGeneration = _selectionGeneration; }
        if (!_tagLabels.ContainsKey((category, name)))
        {
            var names = _tags.Where(pair => pair.Key is "character" or "work")
                .SelectMany(pair => pair.Value.Select(value => (Category: pair.Key, Name: value)));
            foreach (var (key, label) in store.ResolveTagLabels(names)) _tagLabels[key] = label;
        }
        return _tagLabels.GetValueOrDefault((category, name), name);
    }

    private void BuildTagGroup(string category, string title)
    {
        var group = new Grid { ColumnSpacing = 12 };
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        group.Children.Add(new TextBlock { Text = title, FontSize = 12, Opacity = .7, Margin = new Thickness(0, 5, 0, 0) });
        var controls = new StackPanel { Spacing = 7 };
        Grid.SetColumn(controls, 1);
        group.Children.Add(controls);
        var chips = new TagWrapPanel();
        controls.Children.Add(chips);
        Rebuild();
        if (category == "rating") _ratingRebuild = Rebuild;
        if (category != "rating")
        {
            var search = new SearchInputBox { PlaceholderText = $"输入或搜索{title}…", MinWidth = 0, IsEnabled = _state == "pending" };
            _tagInputs[category] = search;
            AutomationProperties.SetName(search, $"搜索或添加{title}标签");
            var generation = _selectionGeneration;
            var searchVersion = 0;
            search.TextChanged += async (_, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                if (BatchMultiple) { _batchTagsDirty = true; UpdateButtons(); }
                var version = ++searchVersion;
                var query = search.Text.Trim();
                if (query.Length == 0) { search.ItemsSource = null; return; }
                await Task.Delay(160);
                if (version != searchVersion || generation != _selectionGeneration || !_active) return;
                try
                {
                    var hits = await Task.Run(() =>
                    {
                        lock (AppService.DbLock)
                        {
                            return TagSearchService.Search(AppService.Store!, category, query)
                                .Select(s => new TagSuggestion(s.Name, s.SearchDisplay))
                                .ToList();
                        }
                    });
                    if (version == searchVersion && generation == _selectionGeneration && _active) search.ItemsSource = hits;
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) { _status.Text = "标签搜索失败：" + ex.Message; }
                catch (ObjectDisposedException) when (!_active) { }
            };
            search.QuerySubmitted += (_, args) =>
            {
                var name = args.ChosenSuggestion is TagSuggestion hit ? hit.Name : args.QueryText.Trim();
                if (name.Length == 0 || _busy) return;
                if (!_tags[category].Contains(name, StringComparer.OrdinalIgnoreCase)) _tags[category].Add(name);
                SaveTags(category == "rating");
                Rebuild();
                search.Text = "";
                search.ItemsSource = null;
            };
            controls.Children.Add(search);
        }
        _tagGroups.Children.Add(group);

        void Rebuild()
        {
            chips.Children.Clear();
            if (category == "rating")
            {
                var rating = _tags[category].FirstOrDefault();
                var label = BatchMultiple && _batchRatingSummary.Length > 0 && string.IsNullOrEmpty(rating)
                    ? "逐图分级" : rating is "g" or "s" ? "SFW" : rating is "q" or "e" ? "NSFW" : "未分级";
                _ratingBadge = new Tags.TagChip("rating", label) { IsHitTestVisible = false };
                if (BatchMultiple && _batchRatingSummary.Length > 0) ToolTipService.SetToolTip(_ratingBadge, _batchRatingSummary);
                chips.Children.Add(_ratingBadge);
            }
            var values = category == "rating" ? new[] { "g", "s", "q", "e" } : _tags[category].ToArray();
            foreach (var name in values)
            {
                // 分级：四个级别互斥切换，左边贴纸由级别算出来；其他标签：左边叉号点掉即取消
                if (category == "rating")
                {
                    var toggle = new ToggleButton
                    {
                        Content = RatingText.Level(name),
                        IsChecked = _tags[category].Contains(name, StringComparer.OrdinalIgnoreCase),
                        Padding = new Thickness(9, 4, 9, 4), MinWidth = 0, MinHeight = 28, FontSize = 12,
                        MaxWidth = 320,
                        IsEnabled = _state == "pending",
                    };
                    ToolTipService.SetToolTip(toggle, RatingText.Tooltip(name));
                    toggle.Click += (_, _) =>
                    {
                        if (_busy) return;
                        _tags[category].Clear();
                        _tags[category].Add(name);
                        _batchRatingSummary = "";
                        SaveTags(true);
                        Rebuild();
                    };
                    chips.Children.Add(toggle);
                }
                else
                {
                    // 只有待归档的记录能改标签；查看已归档/已忽略时不显示叉号
                    Action? remove = _state != "pending" ? null : () =>
                    {
                        if (_busy) return;
                        _tags[category].RemoveAll(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
                        SaveTags(false);
                        Rebuild();
                    };
                    var label = TagLabel(category, name);
                    var chip = new Tags.TagChip(category, label, remove);
                    ToolTipService.SetToolTip(chip, label == name ? name : $"{label} · {name}");
                    chips.Children.Add(chip);
                }
            }
            if (category == "rating")
            {
                var classify = new Button { Content = new FontIcon { Glyph = "\uE895", FontSize = 14 }, Padding = new Thickness(9, 5, 9, 5), IsEnabled = _state == "pending" };
                ToolTipService.SetToolTip(classify, BatchMultiple ? "对所选图片逐张自动分级" : "自动判断这张图的敏感程度");
                AutomationProperties.SetName(classify, BatchMultiple ? "批量自动分级" : "自动判断敏感程度");
                classify.Click += async (_, _) => { await ClassifyAsync(); Rebuild(); };
                chips.Children.Add(classify);
            }
        }
    }

    /// <summary>只判定当前待归档文件，后台推理期间锁定编辑，结果写回同一条队列记录。</summary>
    private async Task ClassifyAsync()
    {
        if (BatchMultiple) { await ClassifyBatchAsync(); return; }
        if (_busy || AppService.IsShuttingDown || _selected is not { State: "pending" } row) return;
        SetBusy(true);
        UpdateButtons();
        _status.Text = "正在自动判断…";
        try
        {
            var modelDir = AppService.Settings.Settings.nsfw_model_dir;
            var result = await Task.Run(() =>
            {
                var scorer = NsfwScorer.TryCreate(modelDir, out var error);
                return scorer is null ? (Rating: "", Error: error) : (Rating: scorer.Score(IngestReview.LocalPath(row)).Rating, Error: "");
            });
            if (result.Error.Length > 0) { _status.Text = "自动判断失败：" + result.Error; return; }
            _tags["rating"].Clear();
            _tags["rating"].Add(result.Rating);
            SaveTags(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException
            or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        { _status.Text = "自动判断失败：" + ex.Message; }
        finally
        {
            SetBusy(false);
            UpdateButtons();
            OpenDeferredNotification();
        }
    }

    private async Task ClassifyBatchAsync()
    {
        if (_busy || AppService.IsShuttingDown || AppService.Store is not { } store) return;
        var ids = _batchSelected.ToArray();
        using var cancellation = new CancellationTokenSource();
        _batchClassifyCts = cancellation;
        var modelDir = AppService.Settings.Settings.nsfw_model_dir;
        _batchRatingSummary = "";
        SetBusy(true);
        UpdateButtons();
        var progress = new Progress<(int Done, int Failed)>(value =>
        {
            if (_busy && ReferenceEquals(_batchClassifyCts, cancellation))
                _status.Text = $"正在批量分级 {value.Done} / {ids.Length}，失败 {value.Failed} 张…";
        });
        try
        {
            var result = await Task.Run(() =>
            {
                var scorer = NsfwScorer.TryCreate(modelDir, out var error)
                    ?? throw new InvalidOperationException(error);
                var ratings = new Dictionary<string, int>();
                var failures = new List<string>();
                var done = 0;
                foreach (var id in ids)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var row = store.IngestRow(id);
                        if (row is not { State: "pending" }) continue;
                        var rating = scorer.Score(IngestReview.LocalPath(row), cancellation.Token).Rating;
                        cancellation.Token.ThrowIfCancellationRequested();
                        using var tx = store.Transaction();
                        // 推理期间标签可能有其他来源更新；写入前重读，仅修改本图分级。
                        if (store.IngestRow(id) is not { State: "pending" } current) continue;
                        var tags = IngestReview.Tags(current.Payload, current.Rating);
                        tags["rating"] = [rating];
                        store.UpdateIngestReview(id, IngestReview.UpdatePayload(current.Payload, tags, rating), rating);
                        tx.Success = true;
                        ratings[rating] = ratings.GetValueOrDefault(rating) + 1;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException
                        or ArgumentException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException
                        or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
                    { failures.Add($"#{id}：{ex.Message}"); }
                    finally { ((IProgress<(int Done, int Failed)>)progress).Report((++done, failures.Count)); }
                }
                return (Ratings: ratings, Failures: failures);
            }, cancellation.Token);
            _tags["rating"].Clear();
            _batchRatingSummary = string.Join(" · ", result.Ratings.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}：{pair.Value} 张"));
            _status.Text = $"已逐图分级 {result.Ratings.Values.Sum()} 张，失败 {result.Failures.Count} 张"
                + (_batchRatingSummary.Length > 0 ? $"（{_batchRatingSummary}）" : "")
                + (result.Failures.Count > 0 ? "\n" + string.Join("\n", result.Failures.Take(3)) : "");
        }
        catch (OperationCanceledException) { _status.Text = "批量分级已停止，已完成的分级已保存。"; }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        { _status.Text = "批量分级失败：" + ex.Message; }
        finally
        {
            _batchClassifyCts = null;
            SetBusy(false);
            var status = _status.Text;
            Reload();
            _carousel.RefreshPreview(preserveRevealed: true);
            _status.Text = status;
            UpdateButtons();
            OpenDeferredNotification();
        }
    }

    private void OpenDeferredNotification()
    {
        if (_pendingNotificationId is not int id) return;
        _pendingNotificationId = null;
        OpenPending(id);
    }

    private void SaveTags(bool refreshProtection)
    {
        if (BatchMultiple)
        {
            _batchTagsDirty = true;
            _status.Text = "标签尚未应用；点击“应用标签”追加合并到所选图片。";
            RefreshDestination();
            UpdateButtons();
            return;
        }
        if (_selected is not { State: "pending" } row || AppService.Store is not { } store) return;
        try
        {
            var rating = _tags["rating"].FirstOrDefault() ?? "";
            var payload = IngestReview.UpdatePayload(row.Payload, _tags, rating);
            store.UpdateIngestReview(row.Id, payload, rating);
            _selected = row with { Payload = payload, Rating = rating };
            _rows[_index] = _selected;
            _status.Text = "标签已保存";
            RefreshDestination();
            if (refreshProtection)
            {
                _carousel.RefreshPreview(preserveRevealed: true);
                _carousel.SetItems(_rows, _index);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            _status.Text = "标签保存失败：" + ex.Message;
        }
    }

    /// <summary>开启「按标签自动分目录」时，当前这张的目录随标签走；用户手改过就不覆盖。</summary>
    private void RefreshDestination()
    {
        if (_state != "pending") return;
        if (BatchMultiple)
        {
            if (_destinationEdited) return;
            _settingDestination = true;
            _destination.Text = "";
            _destination.PlaceholderText = AppService.Settings.Settings.archive_auto_by_tags
                ? "按每张图片的标签分别归档" : "请选择统一归档目录";
            _settingDestination = false;
            return;
        }
        _destination.PlaceholderText = "选择图库内的归档目录";
        if (_selected is null || _destinationEdited) return;
        if (!AppService.Settings.Settings.archive_auto_by_tags) return;
        var target = ArchiveTargetFor(_tags);
        if (target.Length == 0) return;
        _settingDestination = true;
        _destination.Text = target;
        _settingDestination = false;
    }

    /// <summary>按标签算归档目录（绝对路径）：作品/画师两级，缺作品进 no_source、缺画师进 _无画师。</summary>
    private string ArchiveTargetFor(IReadOnlyDictionary<string, List<string>> tags)
    {
        if (AppService.Store is not { } store) return "";
        var root = ArchiveRoot(store);
        if (root is null) return "";
        var map = FolderMap.Load(AppPaths.FolderMapPath);
        var works = tags.TryGetValue("work", out var w) ? w : new List<string>();
        var artists = tags.TryGetValue("artist", out var a) ? a : new List<string>();
        var relative = map.TargetDirRel(works, artists);
        return Path.Combine(root.Path, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>归档用哪个根目录：优先上次用过的（还在图库里），否则第一个启用的根。</summary>
    private static RootInfo? ArchiveRoot(StoreService store)
    {
        var enabled = store.Roots().Where(r => r.Enabled).ToList();
        var remembered = AppService.Settings.Settings.archive_root;
        if (remembered.Length > 0)
        {
            var hit = enabled.FirstOrDefault(r =>
                string.Equals(FileIdentity.PathKey(r.Path), FileIdentity.PathKey(remembered), StringComparison.Ordinal));
            if (hit is not null) return hit;
        }
        return enabled.FirstOrDefault();
    }

    private async Task PickFolderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            if (AppService.Archiver?.ResolveRoot(folder.Path).RootId is not >= 0)
            {
                _status.Text = "请选择已登记图库内的目录；新图库请先在设置中添加。";
                return;
            }
            _destination.Text = folder.Path;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _status.Text = "无法选择目录：" + ex.Message;
        }
    }

    private async Task ArchiveAsync(bool all)
    {
        if (_busy || AppService.IsShuttingDown || AppService.Store is not { } store || AppService.Archiver is not { } archiver) return;
        if (BatchMultiple && _batchTagsDirty) { _status.Text = "请先应用标签，再批量归档。"; return; }
        var destination = _destination.Text.Trim();
        var auto = AppService.Settings.Settings.archive_auto_by_tags;
        // 开着自动分目录、且这张没被手改过时，每张图用自己算出来的目录，不再校验框里那一个
        var fixedDestination = !auto || _destinationEdited;
        try
        {
            if (fixedDestination && (destination.Length == 0 || archiver.ResolveRoot(destination).RootId < 0))
            {
                _status.Text = "请选择已登记图库内的归档目录。";
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _status.Text = "归档目录无效：" + ex.Message;
            return;
        }

        string DestOf(int id)
        {
            if (_batchMode && _destinationEdited) return destination;
            if (!auto) return destination;
            if (_selected is { } current && current.Id == id && _destinationEdited) return destination;
            var record = store.IngestRow(id);
            return record is null ? "" : ArchiveTargetFor(IngestReview.Tags(record.Payload, record.Rating));
        }

        var ids = _batchMode ? _rows.Where(row => _batchSelected.Contains(row.Id)).Select(row => row.Id).ToArray()
            : all ? store.IngestRows("pending", int.MaxValue).Select(r => r.Id).ToArray()
            : _selected is { State: "pending" } row ? [row.Id] : Array.Empty<int>();
        if (ids.Length == 0) return;
        SetBusy(true);
        UpdateButtons();
        var succeeded = 0;
        var failures = new List<string>();
        var archived = new List<int>();
        try
        {
            foreach (var id in ids)
            {
                _status.Text = $"正在归档 {succeeded + failures.Count + 1} / {ids.Length}…";
                var target = DestOf(id);
                if (target.Length == 0) { failures.Add($"#{id}：没有可用的归档目录"); continue; }
                var result = await Task.Run(() => archiver.Archive(id, target));
                if (result.Ok) { succeeded++; archived.Add(id); }
                else failures.Add($"#{id}：{result.Message}");
            }
            if (succeeded > 0 && !auto)
            {
                var (rootId, relative) = archiver.ResolveRoot(destination);
                AppService.Settings.Settings.archive_root = store.Roots().First(r => r.Id == rootId).Path;
                AppService.Settings.Settings.archive_sub = relative.Replace('/', Path.DirectorySeparatorChar);
                AppService.Settings.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            failures.Add(ex.Message);
        }
        finally
        {
            SetBusy(false);
            foreach (var id in archived)
            {
                try { QueueBasics(id); }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
                {
                    AppService.DebugLog($"ingest basics queue #{id}: {ex.Message}");
                }
            }
            Reload();
            Changed?.Invoke();
            OpenDeferredNotification();
        }
        _status.Text = failures.Count == 0 ? $"已归档 {succeeded} 张" : $"已归档 {succeeded} 张，失败 {failures.Count} 条。\n" + string.Join("\n", failures.Take(3));
    }

    private void Ignore(bool all)
    {
        if (_busy || AppService.IsShuttingDown || AppService.Store is not { } store) return;
        var rows = _batchMode ? _rows.Where(row => _batchSelected.Contains(row.Id)).ToList() : all
            ? store.IngestRows("pending", int.MaxValue)
            : _selected is { State: "pending" or "missing" } row
                ? [row]
                : new List<StoreService.PendingIngestRow>();
        var ignored = 0;
        foreach (var item in rows)
        {
            if (_batchMode)
            {
                if (store.TrySetIngestState(item.Id, "pending", "ignored", "手动忽略")) ignored++;
            }
            else { store.SetIngestState(item.Id, "ignored", "手动忽略"); ignored++; }
        }
        Reload();
        Changed?.Invoke();
        _status.Text = $"已忽略 {ignored} 张，磁盘文件保留";
    }

    private void Restore()
    {
        if (_busy || AppService.IsShuttingDown || _selected is not { } row || AppService.Store is not { } store) return;
        if (row.State == "done") { _status.Text = "已归档记录无需重复归档"; return; }
        store.SetIngestState(row.Id, "pending", "");
        Reload();
        Changed?.Invoke();
    }

    private async Task ShowInGalleryAsync()
    {
        if (_busy || AppService.IsShuttingDown || _selected is not { } row) return;
        var path = IngestReview.CurrentPath(row);
        if (path.Length == 0) { _status.Text = "没有可定位的当前文件"; return; }
        if (_owner is not MainWindow main) return;
        try
        {
            await main.ShowInGalleryAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _status.Text = "无法在图库中定位：" + ex.Message;
        }
    }

    private void RevealInExplorer()
    {
        if (_selected is not { } row) return;
        var path = IngestReview.CurrentPath(row);
        if (path.Length == 0 || !File.Exists(path)) { _status.Text = "没有可定位的当前文件"; return; }
        try
        {
            FileActions.RevealInExplorer(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "无法打开所在文件夹：" + ex.Message;
        }
    }

    private async Task CleanHistoryAsync()
    {
        if (_busy || AppService.IsShuttingDown || AppService.Store is not { } store) return;
        var root = XamlRoot ?? _owner.Content?.XamlRoot;
        if (root is null) return;
        var choice = await HistoryCleanupDialog.ShowAsync(root, AppService.Settings.Settings.history_keep_days);
        if (choice is null) return;
        if (!choice.DeleteAll && choice.Days <= 0)
        {
            _status.Text = "保留天数为 0（永久），没有删除任何记录。";
            return;
        }
        if (!choice.DeleteAll)
        {
            AppService.Settings.Settings.history_keep_days = choice.Days;
            AppService.Settings.Save();
        }

        var cutoff = choice.DeleteAll
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
            : DateTimeOffset.UtcNow.AddDays(-choice.Days).ToUnixTimeMilliseconds() / 1000.0;
        SetBusy(true);
        UpdateButtons();
        StoreService.IngestHistoryCleanup? result = null;
        try
        {
            _status.Text = "正在清理历史…";
            result = await Task.Run(() =>
            {
                lock (AppService.DbLock) return store.DeleteIngestHistory(cutoff, choice.IncludeExistingSources);
            });
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            _status.Text = "清理失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
            Reload();
            Changed?.Invoke();
        }
        if (result is { } cleanup)
            _status.Text = $"已删除 {cleanup.Deleted} 条历史" +
                (cleanup.KeptExistingSource > 0 ? $"；保留 {cleanup.KeptExistingSource} 条（原文件仍存在）" : "");
    }

    private void UpdateButtons()
    {
        var pending = _batchMode ? _batchSelected.Count > 0 : _selected is { State: "pending" };
        var missing = _selected is { State: "missing" };
        var ignored = _selected is { State: "ignored" };
        var history = _state == "history";
        _archive.IsEnabled = !_busy && pending && !(BatchMultiple && _batchTagsDirty);
        _archive.Content = BatchMultiple ? $"批量归档（{_batchSelected.Count}）" : "归档此图，并查看下一张";
        _ignore.Content = BatchMultiple ? $"批量忽略（{_batchSelected.Count}）" : "忽略此图";
        _archive.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _ignore.IsEnabled = !_busy && (pending || missing);
        _ignore.Visibility = _state is "pending" or "missing" ? Visibility.Visible : Visibility.Collapsed;
        _restore.Visibility = ignored || missing ? Visibility.Visible : Visibility.Collapsed;
        _restore.IsEnabled = !_busy && _selected is not null;
        _archiveAll.IsEnabled = _ignoreAll.IsEnabled = !_busy && _state == "pending" && _rows.Count > 0;
        _archiveAll.Visibility = _ignoreAll.Visibility = _state == "pending" && !_batchMode ? Visibility.Visible : Visibility.Collapsed;
        _batchToggle.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _batchToggle.Content = _batchMode ? "退出批量审核" : "批量审核";
        _batchToggle.IsEnabled = !_busy;
        _selectAll.Visibility = _batchMode ? Visibility.Visible : Visibility.Collapsed;
        _selectAll.IsEnabled = !_busy && _rows.Count > 0;
        _selectAll.Content = _rows.Count > 0 && _batchSelected.Count == _rows.Count ? "取消全选" : "全选";
        _cardSize.Visibility = _batchMode ? Visibility.Visible : Visibility.Collapsed;
        _cardSize.IsEnabled = !_busy;
        _imageDetails.Visibility = _batchMode && _batchSelected.Count != 1 ? Visibility.Collapsed : Visibility.Visible;
        _detailsHeading.Text = _batchMode ? "批量审核" : "图片详情";
        _batchHint.Visibility = _batchMode && _batchSelected.Count != 1 ? Visibility.Visible : Visibility.Collapsed;
        _batchHint.Text = BatchMultiple
            ? $"已选择 {_batchSelected.Count} 张 · “应用标签”追加合并并去重；“清空标签”清除全部标签及分级。"
            : "点击左侧卡片选择图片，或使用全选。";
        _tagSection.Visibility = _batchMode && _batchSelected.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _applyTags.Visibility = BatchMultiple ? Visibility.Visible : Visibility.Collapsed;
        _applyTags.IsEnabled = !_busy;
        _clearTags.Visibility = BatchMultiple ? Visibility.Visible : Visibility.Collapsed;
        _clearTags.IsEnabled = !_busy;
        if (_batchMode) _scopeHint.Text = $"操作范围：已选择的 {_batchSelected.Count} 张图片";
        else if (_state == "pending") _scopeHint.Text = "批量操作范围：全部待归档图片";
        _revealAll.Visibility = _state == "pending" && _carousel.HasBlurredRows ? Visibility.Visible : Visibility.Collapsed;
        _revealAll.IsEnabled = !_busy;
        _revealAllText.Text = _carousel.AllPreviewsRevealed ? "一键隐藏预览" : "一键显示预览";
        _states.IsEnabled = !_busy;
        _archiveDirGroup.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _destination.IsEnabled = _pickFolder.IsEnabled = !_busy && _state == "pending" && (!_batchMode || _batchSelected.Count > 0);
        _tagGroups.IsHitTestVisible = !_busy;
        var current = _selected is { } row ? IngestReview.CurrentPath(row) : "";
        var done = _selected is { State: "done" };
        _showInGallery.Visibility = history && done ? Visibility.Visible : Visibility.Collapsed;
        _showInGallery.IsEnabled = !_busy && current.Length > 0;
        _showInExplorer.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        _showInExplorer.IsEnabled = !_busy && current.Length > 0 && File.Exists(current);
        _cleanHistory.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        _cleanHistory.IsEnabled = !_busy && _rows.Count > 0;
    }

    public void SetActive(bool active)
    {
        _active = active;
        ++_selectionGeneration;
        if (!active)
        {
            _queueScroll.HideIndicator();
            _scrollDragging = false;
            _carousel.UnfreezeWindow();
        }
        _carousel.SetActive(active);
        if (!active) _imageSpecs.Clear();
        else
        {
            var pending = _tagInputs.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
            _tagGroups.Children.Clear(); _tagInputs.Clear(); _ratingBadge = null; _ratingRebuild = null;
            if (_selected is not null || BatchMultiple)
                for (var i = 0; i < IngestReview.Categories.Length; i++)
                    BuildTagGroup(IngestReview.Categories[i], IngestReview.Titles[i]);
            foreach (var (category, text) in pending)
                if (_tagInputs.TryGetValue(category, out var input)) input.Text = text;
            UpdateButtons();
        }
    }

    /// <summary>侧栏「导入」与拖拽共用的入口：选完选项后写入待审核队列并刷新。</summary>
    public async Task ImportAsync(IReadOnlyList<string> sources)
    {
        if (_busy || AppService.IsShuttingDown || sources.Count == 0) return;
        SetBusy(true);
        UpdateButtons();
        try
        {
            if (await ImportCoreAsync(sources))
            {
                SetBusy(false);
                Reload();
                Changed?.Invoke();
            }
        }
        finally { SetBusy(false); UpdateButtons(); OpenDeferredNotification(); }
    }

    private async Task<bool> ImportCoreAsync(IReadOnlyList<string> sources)
    {
        var root = XamlRoot ?? _owner.Content?.XamlRoot;
        if (root is null) return false;
        var result = await LocalImportFlow.RunAsync(root, sources);
        if (result is null) return false;
        if (result.Added == 0 && result.Skipped == 0)
        {
            _status.Text = "这些位置里没有可导入的图片";
            return false;
        }

        _state = "pending";
        _states.SelectedIndex = 0;
        _status.Text = result.Skipped > 0
            ? $"已导入 {result.Added} 张，跳过重复或不可用 {result.Skipped} 张"
            : $"已导入 {result.Added} 张";
        return true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "导入到待审核";
        e.DragUIOverride.IsCaptionVisible = true;
        _dropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => _dropOverlay.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _dropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var sources = items
                .Select(item => item.Path)
                .Where(path => path.Length > 0)
                .ToList();
            if (sources.Count > 0) await ImportAsync(sources);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _status.Text = "读取拖入的内容失败：" + ex.Message;
        }
    }

    public void RefreshPreview()
    {
        _carousel.RefreshPreview();
        UpdateButtons();
    }

    private void Open(string target, bool source)
    {
        if (target.Length == 0) return;
        if (source && (!Uri.TryCreate(target, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")))
        { _status.Text = "来源页面不是有效的网页地址"; return; }
        if (!source && !File.Exists(target)) { _status.Text = "本地文件不可用"; return; }
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { _status.Text = "无法打开：" + ex.Message; }
    }

    private void ApplyColors()
    {
        _archive.Style = _archiveAll.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        _ignoreAll.Background = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 64, 55, 43) : Windows.UI.Color.FromArgb(255, 247, 237, 223));
        _ignoreAll.Foreground = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 225, 194, 145) : Windows.UI.Color.FromArgb(255, 128, 85, 26));
        _ignore.Opacity = .72;
        _revealAllText.Foreground = ResourceBrush("AccentTextFillColorPrimaryBrush");
    }

    private void CommitScroll()
    {
        if (_rows.Count == 0) return;
        // 控件已经吸附到整数；这里发最终位置，网页回 settled 后统一提交详情和清晰图。
        _carousel.CommitScroll((int)Math.Round(_queueScroll.Value));
    }

    /// <summary>窗口开始拖动/缩放：把 WebView 冻结成快照。</summary>
    public void BeginWindowMove()
    {
        if (!_active) return;
        _carousel.FreezeForWindowMove();
    }

    public void EndWindowMove() => _carousel.UnfreezeWindow();

    private double SplitContentWidth()
    {
        var width = _body.ActualWidth > 0 ? _body.ActualWidth : Math.Max(0, ActualWidth - Padding.Left - Padding.Right);
        return Math.Max(1, width - 16 - _body.ColumnSpacing * 2);
    }

    private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Caption() => new() { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private sealed record TagSuggestion(string Name, string Display) { public override string ToString() => Display; }

    /// <summary>按可用宽度换行排列标签，不把长标签或窄窗口撑出右侧信息栏。</summary>
    private sealed class TagWrapPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double x = 0, y = 0, height = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > availableSize.Width) { y += height + 6; x = height = 0; }
                x += size.Width + 6;
                height = Math.Max(height, size.Height);
            }
            return new Size(double.IsInfinity(availableSize.Width) ? x : availableSize.Width, y + height);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0, y = 0, height = 0;
            foreach (var child in Children)
            {
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > finalSize.Width) { y += height + 6; x = height = 0; }
                child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
                x += size.Width + 6;
                height = Math.Max(height, size.Height);
            }
            return finalSize;
        }
    }
}
