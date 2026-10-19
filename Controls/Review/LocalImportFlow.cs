using Gallery.Services;
using Microsoft.UI.Xaml;

namespace Gallery.Controls.Review;

/// <summary>选中目录或拖入文件夹后的共用流程：问选项、枚举图片、写进待归档队列。</summary>
public static class LocalImportFlow
{
    public sealed record Result(int Added, int Skipped, bool KeepSource);

    public static async Task<Result?> RunAsync(XamlRoot root, IReadOnlyList<string> sources)
    {
        if (sources.Count == 0) return null;
        var options = await ImportDialog.ShowAsync(root, sources);
        if (options is null) return null;

        var folders = sources.Where(Directory.Exists).ToList();
        var files = sources.Where(File.Exists).ToList();
        var store = AppService.Store;
        if (store is null) return null;

        return await Task.Run(() =>
        {
            var paths = LocalImageImport.EnumerateAll(folders, files, options.Recursive);
            if (paths.Count == 0) return new Result(0, 0, options.KeepSource);
            lock (AppService.DbLock)
            {
                var (added, skipped) = store.EnqueueLocalImages(paths, options.KeepSource);
                return new Result(added, skipped, options.KeepSource);
            }
        });
    }
}
