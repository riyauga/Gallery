using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gallery.Services;

public static class ViewerCoordinator
{
    public static bool UpdateInProgress
    {
        get
        {
            if (!EventWaitHandle.TryOpenExisting(UpdateProtocol.UpdateGateName(AppPaths.LocalDataDir), out var gate)) return false;
            using (gate) return gate.WaitOne(0);
        }
    }
    public static async Task<DesktopReply> ImportAsync(string path)
    {
        if (UpdateInProgress) return new(false, "Gallery 正在更新，请完成后重试。");
        if (!SingleInstance.IsRunning(AppPaths.LocalDataDir))
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Gallery.exe")) { UseShellExecute = false };
#if GALLERY_DEVELOPMENT
            if (AppPaths.Sandbox is { } sandbox) { start.ArgumentList.Add("--dataroot"); start.ArgumentList.Add(sandbox); }
#endif
            using var launched = Process.Start(start);
        }
        return await DesktopPipe.SendAsync(false, new("import", [path]), 30000)
            ?? new(false, "未能连接 Gallery，请关闭旧版 Gallery 后重试。");
    }
    public static async Task<bool> PrepareMoveAsync(string path)
    {
        if (!SingleInstance.IsRunning(DesktopPipe.ViewerScope)) return true;
        var result = await DesktopPipe.SendAsync(true, new("release-file", [path]));
        return result?.Success == true || !SingleInstance.IsRunning(DesktopPipe.ViewerScope);
    }
    public static async Task FinishMoveAsync(string path, bool moved)
    {
        if (SingleInstance.IsRunning(DesktopPipe.ViewerScope))
            await DesktopPipe.SendAsync(true, new(moved ? "close-file" : "resume-file", [path]));
    }
    public static async Task<bool> CloseForUpdateAsync(XamlRoot root)
    {
        if (!SingleInstance.IsRunning(DesktopPipe.ViewerScope)) return true;
        var status = await DesktopPipe.SendAsync(true, new("status", []));
        if (status is null) return false;
        if (await DialogService.ShowAsync(new ContentDialog
        {
            XamlRoot = root, Title = "关闭查看窗口并更新？",
            Content = $"还有 {status.Count} 个独立查看窗口。更新前将正常关闭这些窗口，更新后只重新打开完整 Gallery。",
            PrimaryButtonText = "关闭并更新", CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Close,
        }) != ContentDialogResult.Primary) return false;
        var closed = await DesktopPipe.SendAsync(true, new("close-all", []));
        if (closed is { Success: false }) return false;
        // The last window may exit its process before its pipe reply is written.
        try
        {
            using var process = Process.GetProcessById(status.ProcessId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { }
        catch (OperationCanceledException) { return false; }
        return !SingleInstance.IsRunning(DesktopPipe.ViewerScope);
    }
}
