using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

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

    public event Windows.Foundation.TypedEventHandler<AutoSuggestBox, AutoSuggestBoxQuerySubmittedEventArgs> QuerySubmitted
    {
        add => _input.QuerySubmitted += value;
        remove => _input.QuerySubmitted -= value;
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

    protected override void OnPointerPressed(PointerRoutedEventArgs args)
    {
        _shouldFocusBeOnSearch = true;
        base.OnPointerPressed(args);
        _shouldFocusBeOnSearch = false;
    }

    private void SearchBox_GettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (!PreventAutomaticFocus) return;
        if (!IsFocusOnSearchIntentional(args.OldFocusedElement, args.FocusState, args.Direction))
        {
            if (DefaultFocusAnchor is null || !args.TrySetNewFocusedElement(DefaultFocusAnchor))
                args.TryCancel();
            args.Handled = true;
        }
        _shouldFocusBeOnSearch = false;
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
