using System.Text;
using Microsoft.UI.Xaml;
using Gallery.Services;

namespace Gallery;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    e.Exception?.ToString() ?? e.Message);
            }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cli = Environment.GetCommandLineArgs();

        // --dataroot <dir>：沙箱重定向（自动化测试零接触真实数据）
        var drIdx = Array.IndexOf(cli, "--dataroot");
        if (drIdx >= 0 && drIdx + 1 < cli.Length)
            AppPaths.ApplySandbox(cli[drIdx + 1]);

        // --verify-db：批次 0 自动验收（读老库写报告后退出，不出窗口）
        if (cli.Contains("--verify-db"))
        {
            RunVerification();
            Exit();
            return;
        }

        // --verify-query：批次 1 验收（8 键排序 + 搜索，写报告后退出）
        if (cli.Contains("--verify-query"))
        {
            var q = new StringBuilder();
            try
            {
                var store2 = new StoreService(AppPaths.DbPath);
                foreach (var sortKey in SortKeys.All)
                {
                    var rows2 = store2.Query(new StoreService.QuerySpec
                    {
                        Sort = sortKey, Desc = false, Limit = 5,
                    });
                    q.AppendLine($"sort={sortKey,-8} 前5: {string.Join(" | ", rows2.Select(r => r.Name))}");
                }
                var hits = store2.Query(new StoreService.QuerySpec
                {
                    Search = "2026-0", Sort = "name", Limit = 5,
                });
                q.AppendLine($"search=2026-0 命中 {hits.Count}（LIMIT5: {string.Join(" | ", hits.Select(r => r.Name))}）");
                var byName = store2.Query(new StoreService.QuerySpec
                    { Search = "zenless", Sort = "name" });
                q.AppendLine($"search=zenless（标签名路径）命中 {byName.Count}（LIMIT5: {string.Join(" | ", byName.Select(r => r.Name))}）");
            }
            catch (Exception ex) { q.AppendLine("ERROR: " + ex.Message); }
            var outDir3 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
            Directory.CreateDirectory(outDir3);
            File.WriteAllText(Path.Combine(outDir3, "verify_query.txt"), q.ToString());
            Exit();
            return;
        }

        // --thumbtest <image>：验证缩略图 key/目录结构（自动化测试用）
        var ti = Array.IndexOf(cli, "--thumbtest");
        if (ti >= 0 && ti + 1 < cli.Length)
        {
            var thumbs = new ThumbnailService(AppPaths.ThumbCacheDir, 82);
            var fi = new System.IO.FileInfo(cli[ti + 1]);
            var ftUtc = fi.LastWriteTimeUtc.ToFileTimeUtc();
            long secDbg = ftUtc / 10_000_000 - 11644473600;
            double fracDbg = (ftUtc % 10_000_000) / 10_000_000.0;
            var mtime = secDbg + fracDbg;
            var built = thumbs.GetOrBuild(cli[ti + 1], mtime, fi.Length);
            var report2 = "ft = " + ftUtc + "\nsec = " + secDbg +
                          "\nfrac(R) = " + fracDbg.ToString("R") +
                          "\nsum(R) = " + mtime.ToString("R") +
                          "\nmtime = " + StoreService.Fmt(mtime) +
                          "\nF9 = " + mtime.ToString("F9", System.Globalization.CultureInfo.InvariantCulture) +
                          "\ncustom = " + mtime.ToString("0.000000000", System.Globalization.CultureInfo.InvariantCulture) +
                          "\nbits = " + BitConverter.ToString(BitConverter.GetBytes(mtime)) +
                          "\nkey = " + thumbs.Key(cli[ti + 1], mtime, fi.Length) + "\n" +
                          "cache = " + (built ?? "(null)");
            var outDir2 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
            Directory.CreateDirectory(outDir2);
            File.WriteAllText(Path.Combine(outDir2, "verify_thumb.txt"), report2);
            Exit();
            return;
        }

        // --addroot <path>：非交互添加根目录 + 扫描（自动化测试用）
        var idx = Array.IndexOf(cli, "--addroot");
        if (idx >= 0 && idx + 1 < cli.Length)
        {
            RunAddRoot(cli[idx + 1]);
            Exit();
            return;
        }

        AppService.Init();
        _window = new MainWindow();
        _window.Activate();
    }

    private static void RunVerification()
    {
        var report = new StringBuilder();
        try
        {
            SettingsStore settings = new();
            settings.Load();
            report.AppendLine($"settings.roots = {settings.Settings.roots.Count}");
            var store = new StoreService(AppPaths.DbPath);
            report.AppendLine($"schema_version = {store.SchemaVersion}");
            var roots = store.Roots();
            report.AppendLine($"roots = {roots.Count} (enabled {roots.Count(r => r.Enabled)})");
            foreach (var root in roots) report.AppendLine($"  root[{root.Id}] {root.Path} enabled={root.Enabled}");
            report.AppendLine($"images(missing=0) = {store.CountImages()}");
            report.AppendLine($"favorites = {store.CountFavorites()}");

            // 自然排序抽样（中英数字混合）
            var names = new[] { "a2.jpg", "a10.jpg", "ab.jpg", "图2.png", "图10.png", "B.jpg", "#1.jpg", "z3.jpg" };
            var sorted = names.OrderBy(n => n, Comparer<string>.Create((x, y) => NaturalSort.CompareNames(x, y)));
            report.AppendLine("natural_sort = " + string.Join(" | ", sorted));

            // 逐根目录计数（与 galleryold 口径对照）
            foreach (var root in roots)
            {
                var folders = store.Folders(root.Id);
                var total = folders.Sum(f => f.Count);
                report.AppendLine($"  root[{root.Id}] dirs={folders.Count} images={total}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex);
        }
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "verify_db.txt"), report.ToString());
    }

    private static void RunAddRoot(string path)
    {
        var report = new StringBuilder();
        try
        {
            SettingsStore settings = new();
            settings.Load();
            var store = new StoreService(AppPaths.DbPath);
            var folders = new FolderService(store, settings);
            var before = store.CountImages();
            var (root, scan) = folders.AddRootAndScan(path);
            report.AppendLine($"added root[{root.Id}] {root.Path}");
            report.AppendLine($"scan = {scan.Summary()}");
            report.AppendLine($"images before/after = {before}/{store.CountImages()}");
            foreach (var note in store.TrackingNotes) report.AppendLine("note: " + note);
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex.Message);
        }
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "verify_addroot.txt"), report.ToString());
    }
}
