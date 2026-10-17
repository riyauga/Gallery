using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gallery.Views;

/// <summary>
/// 设置页。照搬 photos 的 SettingsViewModel.SwitchTheme：
/// 切换后由 ThemeService.SetTheme(ElementTheme) 应用，选中项与下拉文字同步更新。
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private ElementTheme _theme = ElementTheme.Dark;

    public SettingsPage()
    {
        InitializeComponent();
        ApplyState();
    }

    private void ThemeLightItem_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Light);

    private void ThemeDarkItem_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Dark);

    private void ThemeDefaultItem_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Default);

    private void SetTheme(ElementTheme theme)
    {
        _theme = theme;
        ThemeService.SetTheme(theme);
        ApplyState();
    }

    /// <summary>下拉文字 = GetThemeName(theme)；勾选项 = 当前主题（对应 IsChecked 绑定）。</summary>
    private void ApplyState()
    {
        ThemeDropdown.Content = ThemeService.GetThemeName(_theme);
        ThemeLightItem.IsChecked = _theme == ElementTheme.Light;
        ThemeDarkItem.IsChecked = _theme == ElementTheme.Dark;
        ThemeDefaultItem.IsChecked = _theme == ElementTheme.Default;
    }
}
