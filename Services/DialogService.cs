using Microsoft.UI.Xaml.Controls;

namespace Gallery.Services;

/// <summary>主 UI 线程的模态入口：已有弹窗时忽略新请求，避免重复打开或延迟执行旧操作。</summary>
public static class DialogService
{
    private static bool _showing;
    private static ContentDialog? _current;
    public static bool IsShowing => _showing;

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_showing || AppService.IsShuttingDown) return ContentDialogResult.None;
        _showing = true;
        _current = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            return AppService.IsShuttingDown ? ContentDialogResult.None : result;
        }
        finally { _current = null; _showing = false; }
    }

    public static void Dismiss() => _current?.Hide();
}
