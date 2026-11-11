using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Gallery.Controls;

public sealed class SearchInputBox : UserControl
{
    private readonly AutoSuggestBox _input = new();
    private bool _shouldFocusBeOnSearch;

    public string Text { get => _input.Text; set => _input.Text = value; }
    public string PlaceholderText { get => _input.PlaceholderText; set => _input.PlaceholderText = value; }
    public object? ItemsSource { get => _input.ItemsSource; set => _input.ItemsSource = value; }

    public event Windows.Foundation.TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs> TextChanged
    {
        add => _input.TextChanged += value;
        remove => _input.TextChanged -= value;
    }

    public event Windows.Foundation.TypedEventHandler<AutoSuggestBox, AutoSuggestBoxQuerySubmittedEventArgs>? QuerySubmitted;

    /// <summary>焦点真正进入输入框后触发（被 PreventAutomaticFocus 挡回的焦点不算）。</summary>
    public event EventHandler? FocusEntered;

    /// <summary>焦点当前是否在输入框内。</summary>
    public bool HasFocus => ContainsFocus(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject);

    /// <summary>仅标签输入启用：Tab 在已打开的候选中导航，Enter 仍使用原生提交。</summary>
    public bool EnableSuggestionTabNavigation { get; set; }

    /// <summary>
    /// 搜索框内按 Tab 时先问宿主：参数为是否按住 Shift，返回 true 表示已接管（不再走默认焦点切换）。
    /// 没接管时保持原行为，Ctrl/Alt 组合键不拦截。
    /// </summary>
    public Func<bool, bool>? TabHandler { get; set; }

    /// <summary>把字符追加到搜索框末尾并接管焦点（历史标签上直接打字时用）。</summary>
    public void AppendText(string text)
    {
        _input.Text += text;
        _input.Focus(FocusState.Keyboard);
        if (Descendants<TextBox>(_input).FirstOrDefault() is { } box)
        {
            box.SelectionStart = box.Text.Length;
            box.SelectionLength = 0;
        }
    }

    public static readonly DependencyProperty PreventAutomaticFocusProperty = DependencyProperty.Register(
        nameof(PreventAutomaticFocus), typeof(bool), typeof(SearchInputBox), new PropertyMetadata(false));

    public bool PreventAutomaticFocus
    {
        get => (bool)GetValue(PreventAutomaticFocusProperty);
        set => SetValue(PreventAutomaticFocusProperty, value);
    }

    public UIElement? DefaultFocusAnchor { get; set; }

    public SearchInputBox()
    {
        IsTabStop = false;
        AllowFocusOnInteraction = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
        _input.HorizontalAlignment = HorizontalAlignment.Stretch;
        _input.VerticalAlignment = VerticalAlignment.Stretch;
        _input.AllowFocusOnInteraction = true;
        _input.SetBinding(CornerRadiusProperty, new Binding
        {
            Source = this,
            Path = new PropertyPath(nameof(CornerRadius)),
        });
        Content = _input;
        GettingFocus += SearchBox_GettingFocus;
        _input.PreviewKeyDown += Input_PreviewKeyDown;
        _input.TextChanged += (_, _) => EndSuggestionNavigation();
        _input.QuerySubmitted += (sender, args) =>
        {
            EndSuggestionNavigation();
            QuerySubmitted?.Invoke(sender, args);
            if (EnableSuggestionTabNavigation) FocusSearch();
        };
        Unloaded += (_, _) => EndSuggestionNavigation();
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            if (!PreventAutomaticFocus) return;
            _shouldFocusBeOnSearch = true;
            if (!ContainsFocus(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject)) _input.Focus(FocusState.Pointer);
            _shouldFocusBeOnSearch = false;
        }), true);
    }

    public void FocusSearch()
    {
        _shouldFocusBeOnSearch = true;
        _input.Focus(FocusState.Keyboard);
        _shouldFocusBeOnSearch = false;
    }

    private ListView? _focusedList;
    private Popup? _keyboardPopup;
    private Popup? _nativePopup;
    private UIElement? _suggestionContent;

    private void EndSuggestionNavigation()
    {
        if (_focusedList is not null)
        {
            _focusedList.PreviewKeyDown -= Suggestions_PreviewKeyDown;
            _focusedList.SelectedIndex = -1;
        }
        _focusedList = null;
        var popup = _keyboardPopup;
        var nativePopup = _nativePopup;
        var content = _suggestionContent;
        _keyboardPopup = null;
        _nativePopup = null;
        _suggestionContent = null;
        if (popup is not null)
        {
            popup.Child = null;
            popup.IsOpen = false;
        }
        if (nativePopup is not null) nativePopup.Child = content;
    }

    private void Input_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!e.Handled && e.Key == VirtualKey.Tab && !IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu)
            && TabHandler?.Invoke(IsDown(VirtualKey.Shift)) == true)
        {
            e.Handled = true;
            return;
        }
        if (!EnableSuggestionTabNavigation || e.Handled || e.Key != VirtualKey.Tab || !_input.IsSuggestionListOpen) return;
        if (IsDown(VirtualKey.Shift) || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu)) return;
        if (XamlRoot is not { } root) return;
        foreach (var popup in Descendants<Popup>(_input))
        {
            if (!popup.IsOpen || popup.Child is not FrameworkElement content) continue;
            var list = Descendants<ListView>(content).FirstOrDefault(candidate =>
                candidate.Name == "SuggestionsList" && candidate.IsLoaded &&
                candidate.ActualHeight > 0 && candidate.Items.Count > 0);
            if (list is null) continue;
            var position = content.TransformToVisual(root.Content).TransformPoint(new Windows.Foundation.Point());
            // 原生 AutoSuggestBox 的 LostFocus 必然关单。仅键盘导航期间转移整棵原生候选视图，
            // 仍使用相同的行、模板、数据源与原生 Enter/鼠标提交，不另造一套候选 UI。
            _nativePopup = popup;
            _suggestionContent = content;
            _focusedList = list;
            list.PreviewKeyDown += Suggestions_PreviewKeyDown;
            popup.Child = null;
            _input.IsSuggestionListOpen = false;
            var keyboardPopup = new Popup
            {
                XamlRoot = root,
                Child = content,
                HorizontalOffset = position.X,
                VerticalOffset = position.Y,
                IsLightDismissEnabled = true,
                ShouldConstrainToRootBounds = popup.ShouldConstrainToRootBounds,
                RequestedTheme = ActualTheme,
            };
            _keyboardPopup = keyboardPopup;
            keyboardPopup.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_keyboardPopup, keyboardPopup)) return;
                EndSuggestionNavigation();
                _input.IsSuggestionListOpen = false;
            };
            keyboardPopup.IsOpen = true;
            if (FocusSuggestion(0)) e.Handled = true;
            else
            {
                EndSuggestionNavigation();
                _input.IsSuggestionListOpen = true;
            }
            return;
        }
    }

    private void Suggestions_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _keyboardPopup is not { IsOpen: true } || _focusedList is not { } list) return;
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndSuggestionNavigation();
            FocusSearch();
            _input.IsSuggestionListOpen = false;
            return;
        }
        if (e.Key != VirtualKey.Tab || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || list.Items.Count == 0) return;
        var index = FocusedSuggestionIndex(list);
        if (index < 0) return;
        if (IsDown(VirtualKey.Shift) && index == 0)
        {
            e.Handled = true;
            EndSuggestionNavigation();
            FocusSearch();
            return;
        }
        var next = IsDown(VirtualKey.Shift) ? index - 1 : (index + 1) % list.Items.Count;
        e.Handled = FocusSuggestion(next);
    }

    private int FocusedSuggestionIndex(ListView list)
    {
        for (var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
             current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ListViewItem item) return list.IndexFromContainer(item);
        return -1;
    }

    private bool FocusSuggestion(int index)
    {
        if (_focusedList is not { } list || index < 0 || index >= list.Items.Count) return false;
        list.ScrollIntoView(list.Items[index]);
        list.UpdateLayout();
        if (list.ContainerFromIndex(index) is not Control container) return false;
        var updateText = _input.UpdateTextOnSelect;
        _input.UpdateTextOnSelect = false;
        try
        {
            if (!container.Focus(FocusState.Keyboard)) return false;
            // 原生候选列表的 Enter/鼠标提交仍以 SelectedItem 为准。
            list.SelectedIndex = index;
            return true;
        }
        finally { _input.UpdateTextOnSelect = updateText; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            if (child is Popup { Child: DependencyObject popupChild })
                foreach (var nested in Descendants<T>(popupChild)) yield return nested;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    protected override void OnPointerPressed(PointerRoutedEventArgs args)
    {
        _shouldFocusBeOnSearch = true;
        base.OnPointerPressed(args);
        _shouldFocusBeOnSearch = false;
    }

    private void SearchBox_GettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (PreventAutomaticFocus &&
            !IsFocusOnSearchIntentional(args.OldFocusedElement, args.FocusState, args.Direction))
        {
            if (DefaultFocusAnchor is null || !args.TrySetNewFocusedElement(DefaultFocusAnchor))
                args.TryCancel();
            args.Handled = true;
        }
        _shouldFocusBeOnSearch = false;
        if (!args.Handled) FocusEntered?.Invoke(this, EventArgs.Empty);
    }

    private bool IsFocusOnSearchIntentional(DependencyObject? oldFocusObject,
        FocusState focusState, FocusNavigationDirection focusDirection)
    {
        if (_shouldFocusBeOnSearch) return true;
        if (ContainsFocus(oldFocusObject)) return true;
        if (focusState is FocusState.Programmatic or FocusState.Unfocused) return false;
        if (focusDirection is FocusNavigationDirection.Next or FocusNavigationDirection.Previous) return true;
        if ((focusState is FocusState.Pointer or FocusState.Keyboard) && IsFromKnownFocusStealingSubtree(oldFocusObject))
            return false;
        return true;
    }

    private bool ContainsFocus(DependencyObject? focusedElement)
    {
        for (var parent = focusedElement; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (ReferenceEquals(parent, this)) return true;
        return false;
    }

    private bool IsFromKnownFocusStealingSubtree(DependencyObject? oldFocusObject)
    {
        for (var parent = oldFocusObject is null ? null : VisualTreeHelper.GetParent(oldFocusObject);
             parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, DefaultFocusAnchor) ||
                parent is PhotoGalleryPanel or ToastHost or TeachingTip or PreviewOverlay)
                return true;
        }
        return false;
    }
}
