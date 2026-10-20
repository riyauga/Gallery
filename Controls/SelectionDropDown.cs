using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;

namespace Gallery.Controls;

/// <summary>与主题下拉共用原生按钮和单选菜单，统一外观、按钮避让、键盘导航与边界定位。</summary>
[ContentProperty(Name = nameof(Items))]
public sealed class SelectionDropDown : DropDownButton
{
    private readonly MenuFlyout _menu = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly string _group = Guid.NewGuid().ToString();
    private bool _isOpen;

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(SelectionDropDown),
        new PropertyMetadata(-1, OnSelectedIndexChanged));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(SelectionDropDown),
        new PropertyMetadata(string.Empty, (sender, _) => ((SelectionDropDown)sender).UpdateSelection()));

    public ObservableCollection<object> Items { get; } = new();

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public object? SelectedItem
    {
        get => GetItem(SelectedIndex);
        set => SelectedIndex = value is null ? -1 : Items.IndexOf(value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => _isOpen;
        set
        {
            if (value == _isOpen) return;
            if (value) _menu.ShowAt(this);
            else _menu.Hide();
        }
    }

    public event SelectionChangedEventHandler? SelectionChanged;
    public event EventHandler<object>? DropDownOpened;
    public event EventHandler<object>? DropDownClosed;

    public SelectionDropDown()
    {
        DefaultStyleKey = typeof(DropDownButton);
        Flyout = _menu;
        Items.CollectionChanged += (_, _) => RebuildMenu();
        _menu.Opened += (_, args) =>
        {
            _isOpen = true;
            DropDownOpened?.Invoke(this, args);
        };
        _menu.Closed += (_, args) =>
        {
            _isOpen = false;
            DropDownClosed?.Invoke(this, args);
        };
        Unloaded += (_, _) => _menu.Hide();
    }

    private object? GetItem(int index) => index >= 0 && index < Items.Count ? Items[index] : null;

    private static void OnSelectedIndexChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (SelectionDropDown)sender;
        control.UpdateSelection();
        var previous = control.GetItem((int)args.OldValue);
        var current = control.SelectedItem;
        control.SelectionChanged?.Invoke(control, new SelectionChangedEventArgs(
            previous is null ? Array.Empty<object>() : new[] { previous },
            current is null ? Array.Empty<object>() : new[] { current }));
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        for (var index = 0; index < Items.Count; index++)
        {
            var selectedIndex = index;
            var content = Items[index] is ComboBoxItem item ? item.Content : Items[index];
            var option = new RadioMenuFlyoutItem
            {
                Text = content?.ToString() ?? string.Empty,
                GroupName = _group,
                IsEnabled = Items[index] is not ComboBoxItem descriptor || descriptor.IsEnabled,
            };
            option.Click += (_, _) => SelectedIndex = selectedIndex;
            _menu.Items.Add(option);
        }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = SelectedItem;
        Content = selected is ComboBoxItem item ? item.Content : selected ?? PlaceholderText;
        for (var index = 0; index < _menu.Items.Count; index++)
            ((RadioMenuFlyoutItem)_menu.Items[index]).IsChecked = index == SelectedIndex;
    }
}
