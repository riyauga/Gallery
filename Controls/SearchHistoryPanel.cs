using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Gallery.Controls.Tags;

namespace Gallery.Controls;

/// <summary>
/// 搜索历史浮层（参考哔哩哔哩搜索历史）：标题「搜索历史」+ 右侧「清空」+ 标签流式换行。
/// 只负责渲染与回调；记录、持久化、弹出位置与时机由宿主（MainWindow）决定。
/// 自绘颜色一律按 <see cref="FrameworkElement.ActualTheme"/> 判定（见 UI_CONVENTIONS）。
/// </summary>
public sealed class SearchHistoryPanel
{
    private const double ChipHeight = 32;
    private const double ChipGap = 6;
    private const double ChipMaxWidth = 180;
    /// <summary>面板最多 5 行标签，超出后在面板内滚动。</summary>
    private const double BodyMaxHeight = ChipHeight * 5 + ChipGap * 4;

    private readonly Border _card;
    private readonly TextBlock _title;
    private readonly Button _clear;
    private readonly ScrollViewer _scroll;
    private readonly TagWrapPanel _chips = new();
    private readonly List<Button> _chipButtons = [];
    private IReadOnlyList<string> _items = [];

    /// <summary>点了某条历史。</summary>
    public event Action<string>? QueryPicked;

    /// <summary>点了「清空」。</summary>
    public event Action? ClearRequested;

    /// <summary>焦点在标签上按 Esc：宿主收起浮层并把焦点还给搜索框。</summary>
    public event Action? DismissRequested;

    /// <summary>焦点在标签上直接打字：把字符转交搜索框继续输入。</summary>
    public event Action<string>? TypingStarted;

    public UIElement Root => _card;

    /// <summary>标签按钮个数（0 表示没有可导航项）。</summary>
    public int ChipCount => _chipButtons.Count;

    /// <summary>把焦点在标签之间循环移动；delta=+1 向后，-1 向前。没有标签时返回 false。</summary>
    public bool MoveFocus(int delta)
    {
        if (_chipButtons.Count == 0) return false;
        var index = FocusedChipIndex();
        var next = index < 0
            ? (delta > 0 ? 0 : _chipButtons.Count - 1)
            : (index + delta + _chipButtons.Count) % _chipButtons.Count;
        _chipButtons[next].Focus(FocusState.Keyboard);
        return true;
    }

    private int FocusedChipIndex()
    {
        var focused = FocusManager.GetFocusedElement(_card.XamlRoot) as DependencyObject;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            for (var i = 0; i < _chipButtons.Count; i++)
                if (ReferenceEquals(node, _chipButtons[i])) return i;
        return -1;
    }

    /// <summary>面板宽度（宿主按搜索框实际宽度设置）。</summary>
    public double Width
    {
        get => _card.Width;
        set => _card.Width = value;
    }

    public SearchHistoryPanel()
    {
        _title = new TextBlock
        {
            Text = "搜索历史",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _clear = new Button
        {
            Content = new TextBlock { Text = "清空", FontSize = 12 },
            Padding = new Thickness(8, 2, 8, 2),
            MinWidth = 0,
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_clear, "清空搜索历史");
        _clear.Click += (_, _) => ClearRequested?.Invoke();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_title);
        Grid.SetColumn(_clear, 1);
        header.Children.Add(_clear);

        _scroll = new ScrollViewer
        {
            MaxHeight = BodyMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(0, 0, 16, 0),
            Content = _chips,
        };

        _card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 16),
            Child = new StackPanel { Children = { header, _scroll } },
        };
        _card.Loaded += (_, _) => ApplyTheme();
        _card.ActualThemeChanged += (_, _) => ApplyTheme();
    }

    public void SetItems(IReadOnlyList<string> items)
    {
        _items = items;
        RebuildChips();
    }

    private void RebuildChips()
    {
        _chips.Children.Clear();
        _chipButtons.Clear();
        foreach (var text in _items) _chips.Children.Add(BuildChip(text));
        _scroll.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyTheme()
    {
        var dark = _card.ActualTheme == ElementTheme.Dark;
        _card.Background = new SolidColorBrush(TagColors.FromHex(dark ? "#2C2C2C" : "#FAFAFA"));
        _card.BorderBrush = new SolidColorBrush(TagColors.FromHex(dark ? "#3D3D3D" : "#E0E0E0"));
        _title.Foreground = new SolidColorBrush(TagColors.FromHex(dark ? "#F2F2F2" : "#1A1A1A"));
        _clear.Foreground = new SolidColorBrush(TagColors.FromHex(dark ? "#A8A8A8" : "#5F5F5F"));
        // 标签颜色按主题判定，主题变了要重建。
        RebuildChips();
    }

    private UIElement BuildChip(string text)
    {
        var dark = _card.ActualTheme == ElementTheme.Dark;
        var normal = TagColors.FromHex(dark ? "#383838" : "#F0F1F3");
        var hover = TagColors.FromHex(dark ? "#454545" : "#E6E7EA");

        var frame = new Border
        {
            Background = new SolidColorBrush(normal),
            CornerRadius = new CornerRadius(8),
            Height = ChipHeight,
            Padding = new Thickness(12, 0, 12, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                MaxWidth = ChipMaxWidth - 24,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(TagColors.FromHex(dark ? "#E6E6E6" : "#1B1B1B")),
            },
        };

        var button = new Button
        {
            Content = frame,
            Height = ChipHeight,
            Padding = new Thickness(0),
            MinWidth = 0,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(8),
        };
        button.PointerEntered += (_, _) => frame.Background = new SolidColorBrush(hover);
        button.PointerExited += (_, _) => frame.Background = new SolidColorBrush(normal);
        button.Click += (_, _) => QueryPicked?.Invoke(text);
        // 焦点在标签上时：Tab 在标签间循环，Esc 收起浮层回搜索框，打字则转回搜索框继续输入。
        button.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Tab)
            {
                var backward = Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                e.Handled = MoveFocus(backward ? -1 : 1);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                DismissRequested?.Invoke();
            }
        };
        button.CharacterReceived += (_, e) =>
        {
            // 空格留给 Button 自己的 Space 激活，不抢。
            if (e.Character == ' ') return;
            e.Handled = true;
            TypingStarted?.Invoke(((char)e.Character).ToString());
        };
        // 长查询被省略号截断时，悬停能看到全文。
        ToolTipService.SetToolTip(button, text);
        AutomationProperties.SetName(button, $"搜索历史 {text}");
        _chipButtons.Add(button);
        return button;
    }
}
