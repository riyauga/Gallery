using Gallery.Services;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 纯查看进程只读用户设置（主题与窗口位置），不初始化图库服务。
/// 设置文件缺失或损坏时保持默认值，不阻塞打开文件。
/// </summary>
internal static class ViewerPreferences
{
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try { AppService.Settings.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppService.DebugLog("viewer settings load failed: " + ex.Message);
        }
    }
}
