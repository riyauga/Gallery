using Microsoft.UI.Xaml;

namespace Gallery;

/// <summary>
/// 主题服务。对应 photos 的 SharedServices.IThemeService + ReadableStringService.GetThemeName。
/// SetTheme 应用 ElementTheme 到根元素；GetThemeName 返回下拉显示名。
/// </summary>
public static class ThemeService
{
    /// <summary>根元素引用，由 MainWindow 在构造时注入。</summary>
    public static FrameworkElement? Root { get; set; }

    /// <summary>对应 IThemeService.SetTheme(ElementTheme)。</summary>
    public static void SetTheme(ElementTheme theme)
    {
        if (Root is not null) Root.RequestedTheme = theme;
    }

    /// <summary>对应 ReadableStringService.GetThemeName(ElementTheme)。</summary>
    public static string GetThemeName(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => "浅色",
        ElementTheme.Dark => "深色",
        _ => "Windows 默认"
    };
}
