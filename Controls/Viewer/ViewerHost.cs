using Gallery.Services;

namespace Gallery.Controls.Viewer;

/// <summary>
/// 一个轻量进程只持有一个查看窗口（照搬 Photos：再次打开文件切换当前窗口内容），
/// 不初始化图库。多个文件参数按顺序落到同一窗口，最后停在第一个文件上。
/// </summary>
public sealed class ViewerHost
{
    private readonly HashSet<string> _released = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fileOperations = new(1, 1);
    private ViewerWindow? _window;

    /// <summary>最后一个窗口关闭时回调，由 App 决定是否退出进程。</summary>
    public Action? LastWindowClosed { get; set; }

    public void Open(IEnumerable<string> paths)
    {
        var list = paths.ToArray();
        if (list.Length == 0) return;
        var window = _window;
        if (window is null)
        {
            window = new ViewerWindow(this, list[0]);
            _window = window;
            window.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_window, window)) return;
                _window = null;
                LastWindowClosed?.Invoke();
            };
            window.Activate();
            return;
        }
        window.ShowFile(list[0]);
    }

    /// <summary>单窗口下没有第二个窗口需要暂停；保留调用点，语义为「当前窗口已在播放」。</summary>
    public void Playing(ViewerWindow source) { }

    public async Task<DesktopReply> HandleAsync(DesktopCommand command)
    {
        if (command.Action == "status")
            return new(true, Count: _window is null ? 0 : 1, ProcessId: Environment.ProcessId);
        if (command.Action == "open")
        {
            if (ViewerCoordinator.UpdateInProgress) return new(false, "Gallery 正在更新，请稍后打开文件。");
            if (command.Paths.Any(path => IsPathReleased(Path.GetFullPath(path)))) return new(false, "图片正在处理，请完成后再打开。");
            Open(command.Paths);
            return new(true);
        }
        if (command.Action == "close-all")
        {
            if (_window is { IsBusy: true } || _released.Count > 0 || DialogService.IsShowing)
                return new(false, "查看窗口仍有操作进行中，请先完成操作。");
            _window?.CloseFromHost();
            return new(true);
        }
        if (command.Paths.Length != 1) return new(false, "没有指定文件。");
        var path = Path.GetFullPath(command.Paths[0]);
        if (command.Action == "release-file")
        {
            await _fileOperations.WaitAsync();
            try
            {
                if (_released.Contains(path)) return new(false, "该图片已有文件操作进行中。");
                if (_window is { } busyWindow && busyWindow.CurrentPath.Equals(path, StringComparison.OrdinalIgnoreCase) && busyWindow.IsBusy)
                    return new(false, "请先完成查看窗口中的文件操作。");
                _released.Add(path);
                await SuspendAsync(path);
                using (await Task.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))) { }
                return new(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Resume(path);
                return new(false, "图片仍在读取或文件已变动，请稍后重试。");
            }
            finally { _fileOperations.Release(); }
        }
        if (command.Action == "close-file")
        {
            _released.Remove(path);
            if (Matching(path) is { } window)
            {
                _released.Remove(path);
                await window.DropFileAsync(path);
            }
            return new(true);
        }
        if (command.Action == "resume-file")
        {
            Resume(path);
            return new(true);
        }
        return new(false, "无法处理这个请求。");
    }

    public bool IsPathReleased(string path) => _released.Contains(path);

    private ViewerWindow? Matching(string path) =>
        _window is { } window && window.CurrentPath.Equals(path, StringComparison.OrdinalIgnoreCase) ? window : null;

    private async Task SuspendAsync(string path)
    {
        if (Matching(path) is { } window) await window.SuspendAsync();
        await ImageLoader.WaitForFileIdleAsync(path);
    }

    private void Resume(string path)
    {
        _released.Remove(path);
        Matching(path)?.Resume();
    }

    public async Task ChangeFileAsync(string path, string? newPath, Func<Task> operation)
    {
        await _fileOperations.WaitAsync();
        bool ownsReservation = false;
        try
        {
            if (_released.Contains(path)) throw new IOException("这张图片正在归档，请稍候。");
            _released.Add(path);
            ownsReservation = true;
            await SuspendAsync(path);
            await operation();
            _released.Remove(path);
            if (Matching(path) is { } window) await window.FileChangedAsync(newPath);
        }
        catch
        {
            if (ownsReservation) Resume(path);
            throw;
        }
        finally
        {
            if (ownsReservation) _released.Remove(path);
            _fileOperations.Release();
        }
    }
}
