using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Gallery.Controls.Tags;
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

/// <summary>归档审阅页：圆环单图审阅与网格多选；批量标签工作台支持喷涂、回收和整笔撤销。</summary>
public sealed partial class ReviewPage : Grid
{
    public event Action? GalleryImagesChanged;
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
    // 整页底色：深色是有意的不透明炭灰（WebView 透明表面要从这层取同一底色），
    // 宿主把它复用给内容区左侧那段留白，避免页面左缘露出一条内容岛底色的灰缝。
    private readonly SolidColorBrush _surface = new(Microsoft.UI.Colors.Transparent);
    /// <summary>页面底色画笔（主题切换时原地改色，宿主可直接持有同一个实例）。</summary>
    public SolidColorBrush SurfaceBrush => _surface;
    // 新版从 70:30 起步；旧版比例不再读取，后续拖动仍写回设置。
    private const string SplitRatioSetting = "review_split_ratio_v2";
    private double _splitRatio = .7;
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
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly StackPanel _tagGroups = new() { Spacing = 15 };
    private readonly StackPanel _archiveDirGroup = new() { Spacing = 10 };
    private readonly TextBox _destination = new() { PlaceholderText = "选择图库内的归档目录", MinWidth = 0 };
    private readonly Button _pickFolder = new() { Content = "选择文件夹…", Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button _archive = new() { Content = "归档此图，并查看下一张", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _ignore = new() { Content = "忽略此图", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleButton _batchToggle = new() { Content = "批量审核", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _selectAll = new() { Content = "全选", Visibility = Visibility.Collapsed };
    private readonly SelectionDropDown _cardSize = new() { Visibility = Visibility.Collapsed, MinWidth = 70 };
    /// <summary>单图重跑模型：四类顶替冲突、不冲突的新增；作者与自定义不动。</summary>
    private readonly Button _aiAnalyze = new()
    {
        Content = "AI分析", Padding = new Thickness(10, 4, 10, 4), FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
    };
    /// <summary>批量审核模式下对已勾选的图片逐张跑同一套模型识别；尺寸与「全选」「卡片大小」下拉一致，靠右贴着下拉。</summary>
    private readonly Button _batchAiAnalyze = new()
    {
        Content = "AI分析", Padding = new Thickness(12, 6, 12, 6),
        VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Right,
    };
    private readonly StackPanel _imageDetails = new() { Spacing = 12 };
    private readonly StackPanel _tagSection = new() { Spacing = 12 };
    private readonly TextBlock _detailsHeading = new() { Text = "图片详情", FontSize = 12, Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
    private readonly HashSet<int> _batchSelected = [];
    /// <summary>Shift 连选的起点（行下标）；换单选、清空、全选后作废。</summary>
    private int? _batchAnchorIndex;
    private bool _batchMode;
    private HashSet<int>? _boxSelectionBase;
    private CancellationTokenSource? _batchClassifyCts;
    private CancellationTokenSource? _basicsCts;
    private Task? _basicsTask;
    private bool _basicsDirty;
    private int _basicsGeneration;
    private readonly ConcurrentDictionary<int, byte> _basicsFailed = new();
    /// <summary>后台模型队列里正在处理的记录，用于圆环卡片的「分析中」外观。</summary>
    private readonly HashSet<int> _analyzing = [];
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
    private readonly Grid _detailsHost = new() { RowSpacing = 20, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _detailsPanel = new() { Spacing = 12 };
    private readonly StackPanel _archivePanel = new() { Spacing = 10 };
    private Tags.TagChip? _ratingBadge;
    // 右侧留 16 给浮层滚动条，否则它会压在标签行与归档目录输入框的右缘（UI_INTERACTION.md R1）。
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
    /// <summary>待归档队列里见过的最大 ID：比它更大的记录进来就算新图，用来回到最新那张。</summary>
    private int _newestSeen;
    private bool _busy, _active;
    private readonly Queue<ArchiveBatch> _archiveQueue = new();
    private Task _archiveTask = Task.CompletedTask;
    private readonly TextBlock _archiveProgress = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private int _reloadVersion;
    private Task _reloadTask = Task.CompletedTask;
    private string _loadedState = "";
    private bool _tagDisplayDirty;
    private readonly Dictionary<int, string> _normalizedPayloads = [];
    private TaskCompletionSource? _workCompleted;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _carousel.SetInteractionEnabled(!busy);
        if (busy) _workCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        else _workCompleted?.TrySetResult();
    }

    public bool HasUnconfirmedInput => _destinationEdited || _paintGesture ||
        _tagInputs.Values.Any(input => !string.IsNullOrWhiteSpace(input.Text));

    public async Task DrainWorkAsync()
    {
        _batchClassifyCts?.Cancel();
        _basicsCts?.Cancel();
        if (_workCompleted is { } work) await work.Task;
        await _paintTask;
        await _archiveTask;
        await _reloadTask;
        if (_basicsTask is { } basics)
        {
            try { await basics; }
            catch (OperationCanceledException) { }
        }
    }
    private bool _destinationEdited;   // 用户手改过当前这张的归档目录
    private bool _settingDestination;  // 程序正在往 _destination 里填内容
    private string _autoDestination = "";  // 程序最近一次写入的目录值，用来区分自动填充和手改
    public event Action? Changed;

    public ReviewPage(Window owner)
    {
        _owner = owner;
        Background = _surface;
        AddHandler(TappedEvent, new TappedEventHandler(OnBackgroundTapped), true);
        _path.MaxLines = 2;
        _path.TextTrimming = TextTrimming.CharacterEllipsis;
        _pageUrl.MaxLines = 1;
        _pageUrl.TextTrimming = TextTrimming.CharacterEllipsis;
        Padding = new Thickness(28, 24, 28, 22);
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70, GridUnitType.Star) });
        // 分隔线列给足 16px，把手直接放在列里，不再靠负边距撑布局。
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30, GridUnitType.Star) });
        _body.Children.Add(BuildQueue());
        var divider = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Background = ResourceBrush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 28, 0, 28), IsHitTestVisible = false };
        Grid.SetColumn(divider, 1);
        _body.Children.Add(divider);
        var details = BuildDetails();
        Grid.SetColumn(details, 2);
        _body.Children.Add(details);
        // 把手填满 16px 分隔列，滚动条在左侧空隙，两者保持独立命中区。
        if (AppService.Settings.Settings.layout_options.TryGetValue(SplitRatioSetting, out var savedSplit)
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
            AppService.Settings.Settings.layout_options[SplitRatioSetting] = JsonSerializer.SerializeToElement(Math.Round(_splitRatio, 4));
            AppService.Settings.Save();
        };
        Grid.SetColumn(_splitter, 1);
        _body.Children.Add(_splitter);
        Children.Add(_body);
        BuildPaletteDragLayer();
        _revealAll.Content = _revealAllText;
        _carousel.SelectionRequested += (index, shift, ctrl) =>
        {
            if (_batchMode) SelectBatchIndex(index, shift, ctrl);
            else Select(index);
        };
        _carousel.ClearSelectionRequested += ClearBatchSelection;
        _carousel.SelectAllRequested += SelectAllBatch;
        ConnectWorkbenchInput();
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
            // 拖动只预览勾选，松手更新操作范围；右侧工作台不随选区重建。
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
        _carousel.QueueScrollChanged += (maximum, viewport, value, userInput) =>
        {
            if (!_batchMode || !_active) return;
            if (userInput) _queueScroll.CancelInteraction();
            _syncingScroll = true;
            _queueScroll.Maximum = maximum;
            _queueScroll.ViewportSize = viewport;
            if (!_scrollDragging) _queueScroll.Value = value;
            _queueScroll.Visibility = maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
            _syncingScroll = false;
            _queueScroll.NotifyActivity();
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
            // 只比对值，不依赖事件时机：程序写入的目录即使晚到一步也算「自动值」，不会被当成人手改。
            if (_settingDestination || _destination.Text == _autoDestination) return;
            _destinationEdited = true;
        };
        // 设置保存可能来自后台线程（例如「添加文件夹」在 Task.Run 里落盘），
        // 那时直接改控件会抛「接口为不同线程封送」，异常会顺着 Save 冒出去，
        // 把添加根目录这类已经开了事务的操作整笔回滚掉。统一回到 UI 线程再刷新。
        AppService.Settings.Changed += () =>
        {
            if (DispatcherQueue.HasThreadAccess) RefreshDestination();
            else DispatcherQueue.TryEnqueue(RefreshDestination);
        };
        _revealAll.Click += (_, _) =>
        {
            _carousel.ToggleRevealAll();
            UpdateButtons();
        };
        _archive.Click += async (_, _) => await ArchiveAsync();
        _ignore.Click += (_, _) => Ignore();
        _batchToggle.Click += (_, _) => SetBatchMode(_batchToggle.IsChecked == true);
        _selectAll.Click += (_, _) =>
        {
            if (_busy) return;
            var wasMultiple = BatchMultiple;
            if (_batchSelected.Count == _rows.Count) _batchSelected.Clear();
            else { SelectAllBatch(); return; }
            _batchAnchorIndex = null;
            RenderBatchSelection(!wasMultiple);
        };
        foreach (var (key, label) in new[] { ("small", "小"), ("medium", "中"), ("large", "大"), ("xlarge", "超大") })
            _cardSize.Items.Add(new ComboBoxItem { Tag = key, Content = label });
        _cardSize.SelectedIndex = AppService.Settings.Settings.review_card_size switch { "small" => 0, "large" => 2, "xlarge" => 3, _ => 1 };
        _carousel.SetGridSize(AppService.Settings.Settings.review_card_size);
        _cardSize.SelectionChanged += (_, _) =>
        {
            if (_cardSize.SelectedItem is ComboBoxItem { Tag: string size })
            {
                _carousel.SetGridSize(size);
                AppService.Settings.Settings.review_card_size = size;
                AppService.Settings.Save();
            }
        };
        ToolTipService.SetToolTip(_cardSize, "卡片大小");
        AutomationProperties.SetName(_cardSize, "批量审核卡片大小");
        _restore.Click += (_, _) => Restore();
        _showInGallery.Click += async (_, _) => await ShowInGalleryAsync();
        _showInExplorer.Click += (_, _) => RevealInExplorer();
        _cleanHistory.Click += async (_, _) => await CleanHistoryAsync();
        _pickFolder.Click += async (_, _) => await PickFolderAsync();
        Loaded += (_, _) => FolderMap.Changed += OnDirectoryRulesChanged;
        Unloaded += (_, _) => { FolderMap.Changed -= OnDirectoryRulesChanged; SetActive(false, releasePreviews: true); };
        ActualThemeChanged += (_, _) => { ApplyColors(); RebuildPalette(); };
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth < 850;
            var pad = new Thickness(narrow ? 16 : 28, 20, narrow ? 16 : 28, 20);
            if (Padding != pad) Padding = pad;
            var dragMargin = new Thickness(-pad.Left, -pad.Top, -pad.Right, -pad.Bottom);
            if (_paletteDragLayer.Margin != dragMargin) _paletteDragLayer.Margin = dragMargin;
            var spacing = narrow ? 16 : 28;
            if (_body.ColumnSpacing != spacing) _body.ColumnSpacing = spacing;
            // 滚动条落在圆环与分割线之间的空隙里，不压住卡片。
            var scrollMargin = new Thickness(0, 14, -(spacing + 16) / 2.0, 14);
            if (_queueScroll.Margin != scrollMargin) _queueScroll.Margin = scrollMargin;
            var content = SplitContentWidth();
            var maxQueue = Math.Max(300, content - 300);
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
        // 批量 AI 分析放在中间空隙的右端，靠右贴着卡片大小下拉，尺寸与两侧按钮一致。
        _batchAiAnalyze.Click += async (_, _) => await AnalyzeBatchWithAiAsync();
        AutomationProperties.SetName(_batchAiAnalyze, "用模型分析已选的图片");
        ToolTipService.SetToolTip(_batchAiAnalyze, "对已勾选的待归档图片逐张角色、作品、分级非空覆盖；类型去重追加；作者和自定义不动");
        Grid.SetColumn(_batchAiAnalyze, 1);
        batchActions.Children.Add(_batchAiAnalyze);
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
        footer.Children.Add(_archiveProgress);
        footer.Children.Add(_revealAll);
        footer.Children.Add(_cleanHistory);
        Grid.SetRow(footer, 2);
        panel.Children.Add(footer);
        return panel;
    }

    private Grid BuildDetails()
    {
        var host = _detailsHost;
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = _detailsPanel;
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
        // 「AI分析」与标题同一水平线，靠右对齐。
        var tagHeader = new Grid();
        tagHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tagHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tagHeader.Children.Add(new TextBlock
        {
            Text = "标签", FontSize = 18, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        AutomationProperties.SetName(_aiAnalyze, "用模型重新分析这张图");
        ToolTipService.SetToolTip(_aiAnalyze, "角色、作品、分级非空覆盖；类型去重追加；作者和自定义不动");
        _aiAnalyze.Click += async (_, _) => await AnalyzeWithAiAsync();
        Grid.SetColumn(_aiAnalyze, 1);
        tagHeader.Children.Add(_aiAnalyze);
        _tagSection.Children.Add(tagHeader);
        _tagSection.Children.Add(_tagGroups);
        panel.Children.Add(_tagSection);
        panel.Children.Add(BuildWorkbench());
        _detailScroll.Content = panel;
        host.Children.Add(_detailScroll);
        var archivePanel = _archivePanel;
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
        ++_reloadVersion;
        if (_active && _reloadTask.IsCompleted) _reloadTask = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        while (_active && !_busy && !AppService.IsShuttingDown && AppService.Store is { } store)
        {
            var version = _reloadVersion;
            var state = _state;
            var history = state == "history";
            List<StoreService.PendingIngestRow> rows;
            string fallbackRoot;
            try
            {
                (rows, fallbackRoot) = await Task.Run(() =>
                {
                    using var reader = new StoreService(store.DbPath, readOnly: true);
                    var result = history ? reader.IngestHistory(HistoryLimit)
                        : reader.IngestRows(state, int.MaxValue).OrderByDescending(row => row.ReceivedAt).ThenByDescending(row => row.Id).ToList();
                    if (history)
                        result = result.Select(row => row with { ResolvedPath = IngestReview.ResolveCurrentPath(reader, row) }).ToList();
                    // 一次后台快照预备好所有可选记录；只在 payload 变化后重新归一化。
                    var valid = result.Select(row => row.Id).ToHashSet();
                    foreach (var id in _normalizedPayloads.Keys.Where(id => !valid.Contains(id)).ToArray()) _normalizedPayloads.Remove(id);
                    for (var i = 0; i < result.Count; i++)
                    {
                        var current = result[i];
                        if (current.State != "pending" || _normalizedPayloads.GetValueOrDefault(current.Id) == current.Payload) continue;
                        store.NormalizeIngestSkins(current.Id);
                        result[i] = reader.IngestRow(current.Id) ?? current;
                        _normalizedPayloads[current.Id] = result[i].Payload;
                    }
                    result.RemoveAll(row => row.State == "pending" && store.IsIngestArchiving(row.Id));
                    return (result, reader.Roots().FirstOrDefault(root => root.Enabled)?.Path ?? "");
                });
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            { _status.Text = "刷新审核队列失败：" + ex.Message; return; }
            if (version != _reloadVersion || state != _state) continue;
            if (!_active || _busy || AppService.IsShuttingDown) return;
            if (_loadedState != state || !_rows.SequenceEqual(rows))
                ApplyReload(rows, state, fallbackRoot);
            if (_pendingNotificationId is int requested && _state == "pending")
            {
                _pendingNotificationId = null;
                var index = _rows.FindIndex(row => row.Id == requested);
                if (index >= 0) Select(index, false);
                else _status.Text = "这张图片已处理，不在待归档队列中。";
            }
            if (version == _reloadVersion) return;
        }
    }

    private void ApplyReload(List<StoreService.PendingIngestRow> rows, string state, string fallbackRoot)
    {
        if (_paintGesture) ExitPaintMode();
        var keep = _selected?.Id;
        _loadedState = state;
        _rows = rows;
        if (_batchMode && state == "pending" && _rows.Count == 0) SetBatchMode(false);
        _summary.Text = _state switch
        {
            "pending" => "待归档",
            "missing" => "文件丢失",
            _ => "历史",
        };
        if (string.IsNullOrWhiteSpace(_destination.Text))
        {
            var settings = AppService.Settings.Settings;
            var fallback = settings.archive_root.Length > 0
                ? Path.Combine(settings.archive_root, settings.archive_sub)
                : fallbackRoot;
            _settingDestination = true;
            _autoDestination = fallback;
            _destination.Text = fallback;
            _settingDestination = false;
        }
        var found = keep.HasValue ? _rows.FindIndex(r => r.Id == keep.Value) : -1;
        // 队列改成上新下旧（下标 0 是最新）。待归档里出现更大的 ID 就说明来了新图／新一批导入，直接停到最新那张。
        var newest = _state == "pending" && _rows.Count > 0 ? _rows[0].Id : 0;
        var arrived = newest > _newestSeen;
        if (newest > _newestSeen) _newestSeen = newest;
        if (_batchMode)
        {
            var validIds = _rows.Select(row => row.Id).ToHashSet();
            _batchSelected.IntersectWith(validIds);
            foreach (var id in _workbenchTagCache.Keys.Where(id => !validIds.Contains(id)).ToArray()) _workbenchTagCache.Remove(id);
            RenderBatchSelection(false);
        }
        else if (arrived)
        {
            // 有新图时不再还原切走前记住的旧中心，回来就停在最新那张。
            _carousel.ForgetResumeCenter();
            Select(0, false);
        }
        else if (found >= 0 && _selected == _rows[found])
        {
            _index = found;
            UpdateQueuePosition(false, false);
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
        var rows = _rows.ToArray();
        _basicsDirty = false;
        if (_basicsCts is null || _basicsCts.IsCancellationRequested)
        {
            _basicsCts?.Dispose();
            _basicsCts = new CancellationTokenSource();
        }
        var token = _basicsCts.Token;
        var generation = ++_basicsGeneration;
        _basicsTask = Task.Run(() => RunBasics(store, service, rows, token, generation));
    }

    /// <summary>
    /// 归档成功后补排一次基础分级。即使这张图在后台批次之外被归档，
    /// 模型空下来后也会按图库里的当前位置补写。
    /// </summary>
    private void QueueBasics(int id)
    {
        if (AppService.IsShuttingDown || AppService.Store is not { } store) return;
        if (_basicsFailed.ContainsKey(id)) return;
        if (!AppService.ModelAvailable(AppService.Settings.Settings.nsfw_model_dir)) return;
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

    private void RunBasics(StoreService store, IngestBasicsService service, IReadOnlyList<StoreService.PendingIngestRow> rows,
        CancellationToken token, int generation)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var ids = rows
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
            if (ids.Count == 0) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _basicsGeneration) return;
                _analyzing.Clear();
                _analyzing.UnionWith(ids);
                _carousel.SetAnalyzing(_analyzing);
            });
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
                    MarkAnalyzed(id);
                    continue;
                }

                if (outcome.Error.Length > 0)
                {
                    _basicsFailed[id] = 0;
                    AppService.DebugLog($"ingest basics #{id}: {outcome.Error}");
                    MarkAnalyzed(id);
                    continue;
                }
                if (outcome.Rating.Length == 0 && !outcome.DimensionsChanged && !outcome.TagsChanged)
                {
                    MarkAnalyzed(id);
                    continue;
                }
                DispatcherQueue.TryEnqueue(() =>
                {
                    _analyzing.Remove(id);
                    _carousel.SetAnalyzing(_analyzing);
                    if (generation != _basicsGeneration) return;
                    ApplyBasicsToUi(id, outcome.TagsChanged);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            AppService.DebugLog("review basics preparation: " + ex.Message);
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _analyzing.Clear();
                _carousel.SetAnalyzing(_analyzing);
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

    /// <summary>这一张的模型处理已结束：撤掉「分析中」并刷新卡片外观。</summary>
    private void MarkAnalyzed(int id)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_analyzing.Remove(id)) return;
            _carousel.SetAnalyzing(_analyzing);
        });
    }

    /// <summary>单张模式下只把结果落到当前选中的那张；正在输入的其他标签不动。批量面板不跟着改。</summary>
    private void ApplyBasicsToUi(int id, bool tagsChanged)
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

        if (_batchMode)
        {
            _carousel.UpdateTagRows(_rows, true);
            UpdateWorkbenchState();
            return;
        }
        if (!_active || _busy || _selected?.Id != id) return;
        _selected = updated;

        // 自动标签回来时刷新整组标签；用户正在输入时先不动，避免把输入框里的内容冲掉。
        if (tagsChanged && !HasTagInputFocus())
        {
            ++_selectionGeneration;
            _tags = IngestReview.Tags(updated.Payload, updated.Rating);
            RebuildTagGroups();
            _carousel.RefreshPreview(preserveRevealed: true);
        }

        var rating = ArchiveService.NormalizeRating(updated.Rating);
        if (rating.Length == 0 || !_tags.TryGetValue("rating", out var current) || current.Count > 0) return;

        _tags["rating"] = [rating];
        _ratingRebuild?.Invoke();
        _carousel.RefreshPreview(preserveRevealed: true);
    }

    private bool HasTagInputFocus()
        => XamlRoot is { } root
            && FocusManager.GetFocusedElement(root) is TextBox or AutoSuggestBox;

    private void RebuildTagGroups()
    {
        _tagDisplaySnapshot = null;
        var pending = _tagInputs.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
        _tagGroups.Children.Clear();
        _tagInputs.Clear();
        _ratingBadge = null;
        _ratingRebuild = null;
        for (var i = 0; i < IngestReview.Categories.Length; i++)
            BuildTagGroup(IngestReview.Categories[i], IngestReview.Titles[i]);
        foreach (var (category, text) in pending)
            if (_tagInputs.TryGetValue(category, out var input)) input.Text = text;
    }

    private void SetBatchMode(bool enabled)
    {
        if (_busy || AppService.IsShuttingDown) { _batchToggle.IsChecked = _batchMode; return; }
        enabled &= _state == "pending";
        if (_batchMode == enabled) return;
        if (enabled) _singleIndexBeforeBatch = _index;
        ExitPaintMode();
        if (!enabled) CancelPaletteDrag();
        _queueScroll.CancelInteraction();
        _queueScroll.Visibility = Visibility.Collapsed;
        _batchMode = enabled;
        _queueScroll.Step = enabled ? 0 : 1;
        _destinationEdited = false;
        _batchToggle.IsChecked = enabled;
        _batchSelected.Clear();
        _boxSelectionBase = null;
        _batchAnchorIndex = null;
        _batchRatingSummary = "";
        _tagGroups.Children.Clear(); _tagInputs.Clear();
        ++_selectionGeneration;
        _carousel.SetBatchMode(enabled, _batchSelected, enabled ? null : _singleIndexBeforeBatch);
        if (enabled)
        {
            _ = ResolveSavedPaletteLabelsAsync();
            RenderBatchSelection(true);
        }
        else Select(Math.Clamp(_singleIndexBeforeBatch, 0, Math.Max(0, _rows.Count - 1)), false);
        UpdateButtons();
    }

    private void SelectAllBatch()
    {
        if (!_batchMode || !_active || _busy || _paintGesture || AppService.IsShuttingDown) return;
        var wasMultiple = BatchMultiple;
        _batchSelected.UnionWith(_rows.Select(row => row.Id));
        _batchAnchorIndex = null;
        RenderBatchSelection(!wasMultiple);
    }

    private void ClearBatchSelection()
    {
        if (!_batchMode || _busy || AppService.IsShuttingDown || _batchSelected.Count == 0) return;
        _batchSelected.Clear();
        _boxSelectionBase = null;
        _batchAnchorIndex = null;
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

    /// <summary>
    /// 批量勾选：Shift 从锚点连到当前（Ctrl+Shift 追加，否则替换），
    /// 其余点击切换单张并把锚点移到这里。
    /// </summary>
    private void SelectBatchIndex(int index, bool shift, bool ctrl)
    {
        if (_busy || AppService.IsShuttingDown || index < 0 || index >= _rows.Count) return;
        if (!shift)
        {
            _batchAnchorIndex = index;
            ToggleBatchSelection(index);
            return;
        }
        var anchor = _batchAnchorIndex is int value && value >= 0 && value < _rows.Count ? value : index;
        var wasMultiple = BatchMultiple;
        if (!ctrl) _batchSelected.Clear();
        for (var i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++)
            _batchSelected.Add(_rows[i].Id);
        RenderBatchSelection(!wasMultiple);
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
        _selected = _batchSelected.Count == 1 ? _rows.FirstOrDefault(row => _batchSelected.Contains(row.Id)) : null;
        _tags = IngestReview.Categories.ToDictionary(category => category, _ => new List<string>());
        _count.Text = $"已选 {_batchSelected.Count} / {_rows.Count}";
        _carousel.SetItems(_rows, _index);
        _carousel.SetBatchMode(true, _batchSelected);
        RefreshDestination();
        UpdateButtons();
    }

    private void Select(int index, bool revealScroll = true)
    {
        if (_busy) return;
        var changed = _index != (index = _rows.Count == 0 ? 0 : Math.Clamp(index, 0, _rows.Count - 1));
        _index = index;
        _selected = _rows.Count == 0 ? null : _rows[_index];
        UpdateQueuePosition(revealScroll, changed);
        ++_selectionGeneration;
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

    private void UpdateQueuePosition(bool revealScroll, bool changed)
    {
        _syncingScroll = true;
        _queueScroll.Maximum = Math.Max(0, _rows.Count - 1);
        _queueScroll.ViewportSize = Math.Min(9, Math.Max(1, _rows.Count));
        // 拖动中不回写滑块，松手后再吸附到整数。
        if (!_scrollDragging) _queueScroll.Value = _index;
        _queueScroll.Visibility = !_batchMode && _rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count > 1 && revealScroll && changed) _queueScroll.NotifyActivity();
        _syncingScroll = false;
        _carousel.SetItems(_rows, _index);
        _count.Text = _rows.Count == 0 ? "0 / 0" : $"{_index + 1} / {_rows.Count}";
    }

    /// <summary>通知点击后按队列记录定位，避免依赖列表排序或当前选中项。</summary>
    public void OpenPending(int id)
    {
        if (_busy) { _pendingNotificationId = id; return; }
        _state = "pending";
        SetBatchMode(false);
        _states.SelectedIndex = 0;
        _pendingNotificationId = id;
        Reload();
    }

    private int? _pendingNotificationId;

    private sealed record TagDisplaySnapshot(Dictionary<(string Category, string Name), string> Labels,
        Dictionary<string, List<CharacterSkin>> Skins);
    private Task<TagDisplaySnapshot>? _tagDisplaySnapshot;
    private string _tagDisplaySnapshotKey = "";

    private Task<TagDisplaySnapshot> TagDisplaySnapshotAsync(string payload)
    {
        var names = _tags.Where(p => p.Key is "work" or "character")
            .SelectMany(p => p.Value.Select(name => (Category: p.Key, Name: name))).Distinct().ToArray();
        var key = _selectionGeneration + "\n" + payload + "\n" + JsonSerializer.Serialize(names.Select(p => new[] { p.Category, p.Name }));
        if (_tagDisplaySnapshot is not null && key == _tagDisplaySnapshotKey) return _tagDisplaySnapshot;
        _tagDisplaySnapshotKey = key;
        var path = AppService.Store!.DbPath;
        return _tagDisplaySnapshot = Task.Run(() =>
        {
            using var reader = new StoreService(path, readOnly: true);
            var labels = reader.ResolveTagLabels(names);
            var skins = names.Where(p => p.Category == "character")
                .ToDictionary(p => p.Name, p => reader.SkinsForCharacter(payload, p.Name));
            return new TagDisplaySnapshot(labels, skins);
        });
    }

    private async Task<bool> LoadTagChipLabels(string category, string payload, TagWrapPanel panel,
        IReadOnlyList<(string Name, Tags.TagChip Chip)> chips, Action rebuild)
    {
        if (chips.Count == 0) return true;
        if (AppService.Store is null) return false;
        var generation = _selectionGeneration;
        try
        {
            var snapshot = await TagDisplaySnapshotAsync(payload);
            var labels = snapshot.Labels;
            var skins = snapshot.Skins;
            if (generation != _selectionGeneration || !_active) return false;
            foreach (var (name, chip) in chips)
            {
                if (!panel.Children.Contains(chip)) continue;
                var label = labels.GetValueOrDefault((category, name), name);
                var variants = skins.GetValueOrDefault(name);
                if (variants is { Count: > 0 }) label += " · " + string.Join(" / ", variants.Select(skin => skin.Display));
                chip.Label.Text = label;
                ToolTipService.SetToolTip(chip, label == name ? name : $"{label} · {name}");
                if (variants is not { Count: > 0 }) continue;
                var menu = new MenuFlyout();
                foreach (var skin in variants)
                {
                    var edit = new MenuFlyoutItem { Text = $"编辑皮肤中文名：{skin.Display}" };
                    edit.Click += async (_, _) =>
                    {
                        await EditSkinChineseNameAsync(skin);
                        _tagDisplaySnapshot = null;
                        if (generation == _selectionGeneration) rebuild();
                    };
                    menu.Items.Add(edit);
                }
                chip.ContextFlyout = menu;
                ToolTipService.SetToolTip(chip, $"{label}\n原始标签：{string.Join(", ", variants.Select(skin => skin.SourceTag))}\n右键可编辑皮肤中文名");
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            _tagDisplaySnapshot = null;
            AppService.DebugLog("review tag labels: " + ex.Message);
            return false;
        }
        return true;
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
        var chipHost = new Grid();
        controls.Children.Add(chipHost);
        var chipVersion = 0;
        Rebuild();
        if (category == "rating") _ratingRebuild = Rebuild;
        if (category != "rating")
        {
            var search = new SearchInputBox { EnableSuggestionTabNavigation = true, PlaceholderText = $"输入或搜索{title}…", MinWidth = 0, IsEnabled = _state == "pending" };
            _tagInputs[category] = search;
            AutomationProperties.SetName(search, $"搜索或添加{title}标签");
            var generation = _selectionGeneration;
            var searchVersion = 0;
            search.TextChanged += async (_, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
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
                                .Select(s => new TagSuggestion(s.Name, s.SearchDisplay, s.EntityKey))
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
                // 手输的作品中文名换成资料里的英文键，中文只留作显示；候选里选中的已经是英文，不动。
                if (category == "work" && args.ChosenSuggestion is not TagSuggestion
                    && AppService.Store is { } workStore)
                    name = workStore.PreferWorkSourceName(name);
                if (!_tags[category].Contains(name, StringComparer.OrdinalIgnoreCase)) _tags[category].Add(name);
                var autoWork = category == "character"
                    ? SuggestWorkForCharacter(name, args.ChosenSuggestion as TagSuggestion)
                    : "";
                SaveTags(category == "rating");
                if (autoWork.Length > 0) _status.Text = $"已保存，并按角色补上作品：{autoWork}";
                Rebuild();
                search.Text = "";
                search.ItemsSource = null;
            };
            controls.Children.Add(search);
        }
        _tagGroups.Children.Add(group);

        async void Rebuild()
        {
            var version = ++chipVersion;
            var generation = _selectionGeneration;
            var chips = new TagWrapPanel();
            var namedChips = new List<(string Name, Tags.TagChip Chip)>();
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
                    var chip = new Tags.TagChip(category, name, remove);
                    chip.Label.IsTextSelectionEnabled = true;
                    ToolTipService.SetToolTip(chip, name);
                    if (category is "character" or "work") namedChips.Add((name, chip));
                    chips.Children.Add(chip);
                }
            }
            if (namedChips.Count > 0 && _selected is { } row
                && !await LoadTagChipLabels(category, row.Payload, chips, namedChips, Rebuild)) return;
            if (version != chipVersion || generation != _selectionGeneration) return;
            if (category == "rating")
            {
                var classify = new Button { Content = new FontIcon { Glyph = "\uE895", FontSize = 14 }, Padding = new Thickness(9, 5, 9, 5), IsEnabled = _state == "pending" };
                ToolTipService.SetToolTip(classify, BatchMultiple ? "对所选图片逐张自动分级" : "自动判断这张图的敏感程度");
                AutomationProperties.SetName(classify, BatchMultiple ? "批量自动分级" : "自动判断敏感程度");
                classify.Click += async (_, _) => { await ClassifyAsync(); Rebuild(); };
                chips.Children.Add(classify);
            }
            // 名称和皮肤准备完再原位替换，不把原名当作临时显示值。
            chipHost.Children.Clear();
            chipHost.Children.Add(chips);
        }
    }

    private async Task EditSkinChineseNameAsync(CharacterSkin skin)
    {
        if (DialogService.IsShowing || AppService.Store is not { } store) return;
        var input = new TextBox { Text = skin.Zh, PlaceholderText = "输入可靠的中文皮肤名；留空恢复待译", MaxLength = 160 };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = skin.En, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "皮肤中文名", Content = content,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        try { store.SetSkinChineseName(skin.SkinKey, input.Text); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        { _status.Text = "皮肤名称保存失败：" + ex.Message; }
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
                var scorer = PixaiScorer.TryCreate(modelDir, out var error);
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

    /// <summary>单图重跑模型：四类按结果顶替，作者与自定义不动；批量模式下同时给这张卡片播贴标签动画。</summary>
    private async Task AnalyzeWithAiAsync()
    {
        if (_busy || AppService.IsShuttingDown || AppService.Store is not { } store
            || _selected is not { State: "pending" } row) return;
        if (!AppService.ModelAvailable(AppService.Settings.Settings.nsfw_model_dir))
        {
            _status.Text = "还没有选择识别模型";
            return;
        }
        SetBusy(true);
        UpdateButtons();
        _status.Text = "AI 分析中…";
        _carousel.SetAnalyzing([row.Id]);
        try
        {
            var before = IngestReview.Tags(row.Payload, row.Rating);
            var (error, _, device) = await Task.Run(() => ApplyModelToRow(store, row.Id));
            if (error.Length > 0) { _status.Text = "AI 分析失败：" + error; return; }
            FinishAiRows(store, [row.Id]);
            var index = _rows.FindIndex(item => item.Id == row.Id);
            if (index >= 0) FlyTagApply(row.Id, AddedLabels(before, IngestReview.Tags(_rows[index].Payload, _rows[index].Rating)));
            _carousel.RefreshPreview(preserveRevealed: true);
            _status.Text = "AI 分析完成" + (device == "gpu" ? "（显卡）" : "（CPU）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or Microsoft.Data.Sqlite.SqliteException
            or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        {
            _status.Text = "AI 分析失败：" + ex.Message;
        }
        finally
        {
            _carousel.SetAnalyzing([]);
            SetBusy(false);
            UpdateButtons();
            OpenDeferredNotification();
        }
    }

    /// <summary>
    /// 对一条待归档记录跑一次完整识别并写回：角色/作品顶替（模型没给就保留原值）、类型合并去重、分级替换。
    /// 返回错误文本，空串表示成功。
    /// </summary>
    private static (string Error, string Rating, string Device) ApplyModelToRow(StoreService store, int id)
    {
        var scorer = PixaiScorer.TryCreate(AppService.Settings.Settings.nsfw_model_dir, out var error);
        if (scorer is null) return (error, "", "");
        var row = store.IngestRow(id);
        if (row is not { State: "pending" }) return ("记录已不在待归档", "", "");
        var path = IngestReview.LocalPath(row);
        if (!File.Exists(path)) return ("文件不存在：" + path, "", "");
        var score = scorer.Score(path);
        var whitelist = Taxonomy.LoadKindWhitelist(AppService.Settings.Settings.kind_whitelist_path);
        var tags = IngestReview.Tags(row.Payload, row.Rating);
        ActiveAiTags.Apply(tags, score, whitelist);
        var rating = score.Rating.Length > 0 ? score.Rating : row.Rating ?? "";
        store.UpdateIngestReview(id, IngestReview.ApplyModelOverwrite(row.Payload, tags, rating, score), rating);
        return ("", score.Rating, scorer.EffectiveDevice);
    }

    /// <summary>模型写回后刷新内存队列：单图模式同步当前详情，两种模式都重推卡片与工作台匹配。</summary>
    private void FinishAiRows(StoreService store, IReadOnlyList<int> ids)
    {
        foreach (var id in ids.Distinct()) ApplyRowTagUpdate(store, id);
        if (_batchMode) _carousel.UpdateTagRows(_rows, true);
        else _carousel.SetItems(_rows, _index);
        RefreshDestination();
        UpdateWorkbenchState();
        Changed?.Invoke();
    }

    /// <summary>把一条记录的最新标签写回内存队列；单图模式下顺带同步右侧详情。</summary>
    private void ApplyRowTagUpdate(StoreService store, int id)
    {
        var index = _rows.FindIndex(item => item.Id == id);
        if (index < 0 || store.IngestRow(id) is not { } updated) return;
        _rows[index] = updated with { ResolvedPath = _rows[index].ResolvedPath };
        if (!_batchMode && _selected?.Id == id)
        {
            _selected = _rows[index];
            _tags = IngestReview.Tags(updated.Payload, updated.Rating);
            RebuildTagGroups();
        }
        // 工作台按 payload 引用缓存，旧缓存要丢掉，否则喷涂匹配还按旧标签算。
        _workbenchTagCache.Remove(id);
    }

    /// <summary>比较前后标签，挑出这次新增的项，用来从对应类型飞向卡片。</summary>
    private static IReadOnlyList<PaletteTag> AddedLabels(
        IReadOnlyDictionary<string, List<string>> before, IReadOnlyDictionary<string, List<string>> after)
    {
        var added = new List<PaletteTag>();
        foreach (var (category, names) in after)
        {
            var old = before.GetValueOrDefault(category) ?? [];
            foreach (var name in names)
                if (!old.Contains(name, StringComparer.OrdinalIgnoreCase))
                    added.Add(new PaletteTag(category, name,
                        category == "rating" ? RatingText.Level(name) : name));
        }
        return added;
    }

    /// <summary>
    /// 把这张图这次新贴上的标签交给卡片：飞过去并缩进卡片。
    /// origin 为空时起点取工作台里对应的类型图标（AI 分析走这条路）；点击工作台标签时传那颗标签的中心。
    /// </summary>
    private void FlyTagApply(int id, IReadOnlyList<PaletteTag> labels, Point? origin = null)
    {
        if (!_batchMode || !_active || labels.Count == 0) return;
        var rays = labels.Select(tag =>
        {
            var colors = TagColors.Get(tag.Category, ActualTheme == ElementTheme.Dark);
            // 图标没显示时退回卡片区中心，起点永远有一个确定值。
            var anchor = origin ?? RailAnchor(tag.Category) ?? new Point(ActualWidth * .3, ActualHeight / 2);
            return new ReviewCarousel.TagRay(id, tag.Category, tag.Display, anchor.X, anchor.Y,
                colors.Bg, colors.Border, colors.Fg);
        }).ToArray();
        _carousel.FlyTagApply(rays);
    }

    /// <summary>批量移除时这张卡的对应标签：先在卡片上弹一下，再飞向工作台那颗标签并缩没。</summary>
    private void FlyTagDetach(int id, IReadOnlyList<PaletteTag> labels, Point target)
    {
        if (!_batchMode || !_active || labels.Count == 0) return;
        var rays = labels.Select(tag =>
        {
            var colors = TagColors.Get(tag.Category, ActualTheme == ElementTheme.Dark);
            return new ReviewCarousel.DetachRay(id, tag.Display, colors.Bg, colors.Border, colors.Fg);
        }).ToArray();
        _carousel.FlyTagDetach(rays, target);
    }

    /// <summary>
    /// 批量「AI分析」：对已勾选的待归档图片逐张跑同一套完整识别并写回（规则同单图，作者与自定义不动）。
    /// </summary>
    private async Task AnalyzeBatchWithAiAsync()
    {
        if (_busy || AppService.IsShuttingDown || AppService.Store is not { } store || _batchSelected.Count == 0) return;
        if (!AppService.ModelAvailable(AppService.Settings.Settings.nsfw_model_dir))
        {
            _status.Text = "还没有选择识别模型";
            return;
        }
        var ids = _batchSelected.ToArray();
        SetBusy(true);
        UpdateButtons();
        var done = 0;
        var failures = new List<string>();
        _status.Text = $"AI 分析中 0 / {ids.Length}…";
        try
        {
            foreach (var id in ids)
            {
                if (AppService.IsShuttingDown) break;
                _carousel.SetAnalyzing([id]);
                var rows = _rows;
                var at = rows.FindIndex(item => item.Id == id);
                var before = at >= 0 ? IngestReview.Tags(rows[at].Payload, rows[at].Rating) : null;
                var (error, _, _) = await Task.Run(() => ApplyModelToRow(store, id));
                if (error.Length > 0) failures.Add($"#{id}：{error}");
                done++;
                _status.Text = $"AI 分析中 {done} / {ids.Length}…";
                // 每张跑完立刻贴一张卡片的动画，而不是等整批结束。
                ApplyRowTagUpdate(store, id);
                var index = rows.FindIndex(item => item.Id == id);
                if (before is not null && index >= 0)
                    FlyTagApply(id, AddedLabels(before, IngestReview.Tags(rows[index].Payload, rows[index].Rating)));
            }
            FinishAiRows(store, ids);
            _status.Text = $"AI 分析完成 {done - failures.Count} 张，失败 {failures.Count} 张"
                + (failures.Count > 0 ? "\n" + string.Join("\n", failures.Take(3)) : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or Microsoft.Data.Sqlite.SqliteException
            or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        {
            _status.Text = "AI 分析失败：" + ex.Message;
        }
        finally
        {
            _carousel.SetAnalyzing([]);
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
                var scorer = PixaiScorer.TryCreate(modelDir, out var error)
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

    /// <summary>
    /// 手动加角色标签时顺带补作品：作品行是单选，已有作品就不覆盖，只填空缺。
    /// 返回补上的作品标签名；没补返回空串。词库对不上或角色跨多作品且没有候选时不补。
    /// </summary>
    private string SuggestWorkForCharacter(string character, TagSuggestion? picked)
    {
        if (AppService.Store is not { } store || _tags["work"].Count > 0) return "";
        string? work;
        try
        {
            work = new TagCatalogService(store).SuggestWorkTag(character, picked?.EntityKey ?? "", _tags["work"]);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _status.Text = "补作品失败：" + ex.Message;
            return "";
        }
        if (string.IsNullOrEmpty(work)) return "";
        _tags["work"].Add(work);

        return work;
    }

    private void SaveTags(bool refreshProtection)
    {
        if (_batchMode) return;
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

    private void OnDirectoryRulesChanged() => DispatcherQueue.TryEnqueue(RefreshDestination);

    /// <summary>开启「按标签自动分目录」时，当前这张的目录随标签走；用户手改过就不覆盖。</summary>
    private int _destinationRevision;

    private async void RefreshDestination()
    {
        if (_state != "pending") return;
        if (_batchMode)
        {
            if (_destinationEdited && !string.IsNullOrWhiteSpace(_destination.Text)) return;
            _settingDestination = true;
            _autoDestination = "";
            _destination.Text = "";
            _destination.PlaceholderText = "留空：按每张图片的推荐目录分别归档";
            _settingDestination = false;
            return;
        }
        _destination.PlaceholderText = "选择图库内的归档目录";
        if (_selected is null || _destinationEdited) return;
        if (AppService.Store is not { } store) return;
        var row = _selected;
        var revision = ++_destinationRevision;
        var tags = _tags.ToDictionary(pair => pair.Key, pair => new List<string>(pair.Value));
        var context = new FolderMap.FolderContext(row.PageUrl, row.ReceivedAt);
        string target;
        try
        {
            target = await Task.Run(() =>
            {
                using var reader = new StoreService(store.DbPath, readOnly: true);
                return ArchiveTargetFor(reader, tags, context);
            });
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            AppService.DebugLog("review destination: " + ex.Message);
            return;
        }
        if (revision != _destinationRevision || _selected?.Id != row.Id || _state != "pending" ||
            _batchMode || _destinationEdited || target.Length == 0) return;
        _settingDestination = true;
        _autoDestination = target;
        _destination.Text = target;
        _settingDestination = false;
    }

    /// <summary>按逐级目录模板计算归档目录（绝对路径）；模板为空时就是根目录。</summary>
    private static string ArchiveTargetFor(StoreService store, IReadOnlyDictionary<string, List<string>> tags,
        FolderMap.FolderContext? context = null)
    {
        var map = FolderMap.Load(AppPaths.FolderMapPath);
        var root = ArchiveRoot(store, map.Root);
        if (root is null) return "";
        var relative = map.Resolve(tags, context);
        return Path.Combine(root.Path, relative.Length == 0 ? "" : relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>归档根目录：模板里选的目录优先，其次上次用过的（仍在图库里），最后第一个启用的根。</summary>
    private static RootInfo? ArchiveRoot(StoreService store, string configured = "")
    {
        var enabled = store.Roots().Where(r => r.Enabled).ToList();
        foreach (var candidate in new[] { configured, AppService.Settings.Settings.archive_root })
        {
            if (candidate.Length == 0) continue;
            var hit = enabled.FirstOrDefault(r =>
                string.Equals(FileIdentity.PathKey(r.Path), FileIdentity.PathKey(candidate), StringComparison.Ordinal));
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

    private async Task ArchiveAsync()
    {
        if (_busy || _paintGesture || AppService.IsShuttingDown || AppService.Store is not { } store || AppService.Archiver is not { } archiver) return;
        if (!store.Roots().Any(root => root.Enabled))
        {
            if (await DialogService.ShowAsync(new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "先添加一个图库",
                Content = "图片已保留在待审核中。请先选择用来保存归档图片的图库文件夹。",
                PrimaryButtonText = "添加图库", CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Close,
            }) == ContentDialogResult.Primary && _owner is MainWindow main) await main.AddLibraryForArchiveAsync();
            return;
        }
        var destination = _destination.Text.Trim();
        // 没有归档模式开关：明确指定目录时用它，否则按逐级模板算（模板为空就进根目录）。
        var fixedDestination = _batchMode ? destination.Length > 0 : _destinationEdited;
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

        var ids = (_batchMode ? _rows.Where(row => _batchSelected.Contains(row.Id)).Select(row => row.Id)
            : _selected is { State: "pending" } row ? new[] { row.Id } : Array.Empty<int>())
            .Where(id => !store.IsIngestArchiving(id)).ToArray();
        if (ids.Length == 0) return;
        var map = FolderMap.Load(AppPaths.FolderMapPath);
        var batch = new ArchiveBatch(ids, fixedDestination, destination, true,
            ArchiveRoot(store, map.Root)?.Path ?? "");
        store.BeginIngestArchiving(ids);
        _archiveQueue.Enqueue(batch);
        _rows.RemoveAll(row => store.IsIngestArchiving(row.Id));
        _batchSelected.ExceptWith(ids);
        if (_batchMode && _rows.Count == 0) SetBatchMode(false);
        if (_batchMode) RenderBatchSelection(false);
        else Select(Math.Min(_index, Math.Max(0, _rows.Count - 1)), false);
        UpdateButtons();
        _archiveProgress.Visibility = Visibility.Visible;
        _archiveProgress.Text = $"后台归档：还有 {store.ArchivingIngestCount} 张…";
        Changed?.Invoke();
        if (_archiveTask.IsCompleted) _archiveTask = RunArchiveQueueAsync();
    }

    /// <summary>
    /// 归档前是否需要先跑模型：待归档、没有分级、且角色/作品/作者/类型/自定义五类都空。
    /// 分辨率由导入时算好，不参与「有没有标签」的判断。
    /// </summary>
    private static bool NeedsModelBeforeArchive(StoreService store, int id)
    {
        var row = store.IngestRow(id);
        if (row is not { State: "pending" }) return false;
        if (ArchiveService.NormalizeRating(row.Rating).Length > 0) return false;
        var tags = IngestReview.Tags(row.Payload, row.Rating);
        return tags["character"].Count == 0 && tags["work"].Count == 0 && tags["artist"].Count == 0
            && tags["kind"].Count == 0 && tags["custom"].Count == 0;
    }

    private void Ignore()
    {
        if (_busy || AppService.IsShuttingDown || AppService.Store is not { } store) return;
        var rows = _batchMode ? _rows.Where(row => _batchSelected.Contains(row.Id)).ToList()
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
        _archive.IsEnabled = !_busy && pending;
        _archive.Content = _batchMode ? $"批量归档（{_batchSelected.Count}）" : "归档此图，并查看下一张";
        _ignore.Content = _batchMode ? $"批量忽略（{_batchSelected.Count}）" : "忽略此图";
        _archive.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _ignore.IsEnabled = !_busy && (pending || missing);
        _ignore.Visibility = _state is "pending" or "missing" ? Visibility.Visible : Visibility.Collapsed;
        _restore.Visibility = ignored || missing ? Visibility.Visible : Visibility.Collapsed;
        _restore.IsEnabled = !_busy && _selected is not null;
        _batchToggle.Visibility = _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _batchToggle.Content = _batchMode ? "退出批量审核" : "批量审核";
        _batchToggle.IsEnabled = !_busy && (_batchMode || _rows.Count > 0);
        _selectAll.Visibility = _batchMode ? Visibility.Visible : Visibility.Collapsed;
        _selectAll.IsEnabled = !_busy && _rows.Count > 0;
        _selectAll.Content = _rows.Count > 0 && _batchSelected.Count == _rows.Count ? "取消全选" : "全选";
        _cardSize.Visibility = _batchMode ? Visibility.Visible : Visibility.Collapsed;
        _cardSize.IsEnabled = !_busy;
        _imageDetails.Visibility = _batchMode ? Visibility.Collapsed : Visibility.Visible;
        _detailsHeading.Text = _batchMode ? "标签工作台" : "图片详情";
        _detailsHeading.FontSize = _batchMode ? 25 : 12;
        _detailsHeading.Opacity = _batchMode ? 1 : .65;
        _tagSection.Visibility = _batchMode ? Visibility.Collapsed : Visibility.Visible;
        _workbench.Visibility = _batchMode ? Visibility.Visible : Visibility.Collapsed;
        if (_batchMode && _archivePanel.Parent == _detailsHost)
        {
            _detailsHost.Children.Remove(_archivePanel);
            _detailsPanel.Children.Add(_archivePanel);
        }
        else if (!_batchMode && _archivePanel.Parent == _detailsPanel)
        {
            _detailsPanel.Children.Remove(_archivePanel);
            _detailsHost.Children.Add(_archivePanel);
        }
        _aiAnalyze.Visibility = _state == "pending" && !_batchMode ? Visibility.Visible : Visibility.Collapsed;
        _aiAnalyze.IsEnabled = !_busy && _selected is { State: "pending" };
        _batchAiAnalyze.Visibility = _batchMode && _state == "pending" ? Visibility.Visible : Visibility.Collapsed;
        _batchAiAnalyze.IsEnabled = !_busy && _batchSelected.Count > 0;
        _batchAiAnalyze.Content = _batchSelected.Count > 0 ? $"AI分析 ({_batchSelected.Count})" : "AI分析";
        // 放在最后：工作台会按喷涂手势再压一遍上面这些按钮的可用状态。
        UpdateWorkbenchState();
        _revealAll.Visibility = _state == "pending" && _carousel.HasBlurredRows ? Visibility.Visible : Visibility.Collapsed;
        _revealAll.IsEnabled = !_busy;
        _revealAllText.Text = _carousel.AllPreviewsRevealed ? "一键隐藏预览" : "一键显示预览";
        _states.IsEnabled = !_busy;
        _states.Visibility = _batchMode ? Visibility.Collapsed : Visibility.Visible;
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

    public void SetActive(bool active, bool releasePreviews = false)
    {
        if (_active == active && !releasePreviews) return;
        _active = active;
        ++_selectionGeneration;
        if (!active)
        {
            if (_selected is not null) _tagDisplayDirty = true;
            ExitPaintMode();
            CancelPaletteDrag();
            _queueScroll.CancelInteraction();
            _queueScroll.HideIndicator();
            _scrollDragging = false;
            _carousel.UnfreezeWindow();
        }
        _carousel.SetActive(active, releasePreviews);
        if (releasePreviews) _imageSpecs.Clear();
        if (active)
        {
            Reload();
            UpdateButtons();
            if (_batchMode)
            {
                _ = ResolveSavedPaletteLabelsAsync();
                UpdateWorkbenchState();
            }
            if (_tagDisplayDirty && !_batchMode && _selected is not null)
            { _tagDisplayDirty = false; DispatcherQueue.TryEnqueue(RebuildTagGroups); }
        }
    }

    public bool IsBusyForExternalImport => _busy || _paintGesture || HasUnconfirmedInput;
    public async Task<DesktopReply> ImportExternalFileAsync(string path)
    {
        if (AppService.Store is not { } store || _busy) return new(false, "审核页尚未就绪。");
        if (store.PendingLocalFile(path) is int existing)
        {
            if (store.IngestRow(existing) is { State: "missing" }) store.SetIngestState(existing, "pending", "");
            OpenPending(existing); return new(true, "这张图片已在待审核中，已定位。");
        }
        await ImportAsync([path]);
        if (store.PendingLocalFile(path) is int added)
        { OpenPending(added); return new(true, "已加入待审核。"); }
        return new(false, "未加入待审核，原文件保持不变。");
    }

    /// <summary>侧栏「导入」与拖拽共用的入口：选完选项后写入待审核队列并刷新。</summary>
    public async Task ImportAsync(IReadOnlyList<string> sources)
    {
        AppService.DebugLog($"import-review: ImportAsync busy={_busy} shuttingDown={AppService.IsShuttingDown} n={sources.Count}");
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
        AppService.DebugLog("import-review: root=" + (root is null ? "<null>" : "ok")
            + " showing=" + DialogService.IsShowing);
        if (root is null) return false;
        var result = await LocalImportFlow.RunAsync(root, sources);
        AppService.DebugLog("import-review: result=" + (result is null ? "<null>"
            : $"added={result.Added} skipped={result.Skipped}"));
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
        _tagDisplayDirty = !_active;
        if (_active && !_batchMode && _selected is not null) RebuildTagGroups();
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
        // 深色审核页统一为中性炭灰，WebView 从此承载层读取同一底色。
        _surface.Color = ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 33, 35) : Colors.Transparent;
        _archive.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        _ignore.Opacity = .72;
        _revealAllText.Foreground = ResourceBrush("AccentTextFillColorPrimaryBrush");
    }

    private void CommitScroll()
    {
        if (_batchMode || _rows.Count == 0) return;
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
    private sealed record TagSuggestion(string Name, string Display, string EntityKey = "")
    {
        public override string ToString() => Display;
    }

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
