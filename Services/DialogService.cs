using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

namespace Gallery.Services;

/// <summary>主 UI 线程的模态入口：已有弹窗时忽略新请求，避免重复打开或延迟执行旧操作。</summary>
public static class DialogService
{
    private static bool _showing;
    private static ContentDialog? _current;
    public static bool IsShowing => _showing;

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_showing || AppService.IsShuttingDown)
        {
            AppService.DebugLog($"import-review: dialog gated showing={_showing} shuttingDown={AppService.IsShuttingDown}");
            return ContentDialogResult.None;
        }
        _showing = true;
        _current = dialog;
        // Esc 关闭兜底：焦点在 AutoSuggestBox／TextBox 里时，ContentDialog 自带的 Esc 会被控件吞掉，
        // 窗口级 KeyboardAccelerator 又轮不到（焦点在弹窗内）。带 handledEventsToo 才能拿到被吞的那一次。
        KeyEventHandler onKey = (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            e.Handled = true;
            dialog.Hide();
        };
        dialog.AddHandler(UIElement.KeyDownEvent, onKey, handledEventsToo: true);
        try
        {
            var result = await dialog.ShowAsync();
            return AppService.IsShuttingDown ? ContentDialogResult.None : result;
        }
        finally
        {
            dialog.RemoveHandler(UIElement.KeyDownEvent, onKey);
            _current = null;
            _showing = false;
        }
    }

    public static void UseDestructivePrimary(ContentDialog dialog)
    {
        dialog.PrimaryButtonStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   TargetType="Button" BasedOn="{StaticResource DefaultButtonStyle}">
                <Setter Property="ContentTemplate">
                    <Setter.Value>
                        <DataTemplate>
                            <TextBlock Text="{Binding}" Foreground="{ThemeResource SystemFillColorCriticalBrush}"/>
                        </DataTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """);
    }

    public static void Dismiss() => _current?.Hide();
}
