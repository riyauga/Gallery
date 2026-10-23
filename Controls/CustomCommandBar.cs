using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Gallery.Controls;

/// <summary>
/// Photos CustomCommandBar 与 CommandBarAccessibilityBehaviourFixer 的托管移植。
/// 保留 Photos 的 Programmatic 聚焦、弹层首项/入口恢复及 UIA 分组；焦点视觉交给原生控件。
/// 来源：PhotosManager.Views.Controls/CustomCommandBar.cs、PhotosManager.Helpers/CommandBarAccessibilityBehaviourFixer.cs。
/// </summary>
public sealed class CustomCommandBar : CommandBar
{
    public CustomCommandBar()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private IEnumerable<AppBarButton> Buttons => PrimaryCommands.Concat(SecondaryCommands).OfType<AppBarButton>();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        foreach (var button in Buttons)
        {
            button.Loaded -= Button_Loaded;
            button.Loaded += Button_Loaded;
            button.Tapped -= Button_Tapped;
            button.Tapped += Button_Tapped;
            Button_Loaded(button, args);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        foreach (var button in Buttons)
        {
            if (button.Flyout is { } flyout) UnsubscribeFlyout(flyout);
            button.Loaded -= Button_Loaded;
            button.Tapped -= Button_Tapped;
        }
    }

    private static void UnsubscribeFlyout(FlyoutBase flyout)
    {
        flyout.Opened -= Flyout_Opened;
        flyout.Closing -= Flyout_Closing;
    }

    private static void Button_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not AppBarButton { Flyout: { } flyout } button) return;
        UnsubscribeFlyout(flyout);
        flyout.Opened += Flyout_Opened;
        flyout.Closing += Flyout_Closing;
        AutomationProperties.SetName(flyout, button.Label);
    }

    private static void Button_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (sender is AppBarButton { Flyout: null } button &&
            !ReferenceEquals(FocusManager.GetFocusedElement(button.XamlRoot), button))
            button.Focus(FocusState.Programmatic);
    }

    private static void Flyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (sender.Target is AppBarButton { IsInOverflow: false } button &&
            !ReferenceEquals(FocusManager.GetFocusedElement(button.XamlRoot), button))
            button.Focus(FocusState.Programmatic);
    }

    private static void Flyout_Opened(object? sender, object args)
    {
        if (sender is not MenuFlyout menu || menu.Items.Count == 0) return;
        SetFlyoutItemGroup(menu);
        menu.Items.FirstOrDefault(item => item.IsEnabled && item.IsTabStop &&
            item is not MenuFlyoutSeparator)?.Focus(FocusState.Programmatic);
    }

    private static void SetFlyoutItemGroup(MenuFlyout menu)
    {
        var groups = new Dictionary<string, List<string>>();
        foreach (var item in menu.Items.OfType<RadioMenuFlyoutItem>())
        {
            if (string.IsNullOrEmpty(item.GroupName)) continue;
            if (!groups.TryGetValue(item.GroupName, out var labels))
                groups.Add(item.GroupName, labels = []);
            if (!labels.Contains(item.Text)) labels.Add(item.Text);
        }
        if (groups.Count == 0) return;
        int position = 0, ungroupedSize = menu.Items.Count - groups.Count;
        foreach (var item in menu.Items)
        {
            if (item is RadioMenuFlyoutItem radio && groups.TryGetValue(radio.GroupName, out var labels))
            {
                position = 0;
                // 原版 Localized() 友好名称格式未恢复；保留现有文本，避免编造资源字符串。
                AutomationProperties.SetPositionInSet(item, labels.IndexOf(radio.Text) + 1);
                AutomationProperties.SetSizeOfSet(item, labels.Count);
            }
            else
            {
                AutomationProperties.SetPositionInSet(item, ++position);
                AutomationProperties.SetSizeOfSet(item, ungroupedSize);
            }
        }
    }
}
