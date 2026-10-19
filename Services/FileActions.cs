using System.Runtime.InteropServices;

namespace Gallery.Services;

/// <summary>
/// 文件操作（对应 galleryold <c>app/file_actions.py</c> 的回收站语义）。
/// 批次 1 先提供删除到回收站；移动/重命名批次 2 补。
/// </summary>
public static class FileActions
{
 private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted; // 1 = 用户中止/未完成
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>删除文件/目录到回收站（FOF_ALLOWUNDO）。成功返回 true。</summary>
    public static bool DeleteToRecycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // 双 null 结尾（可批量）
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };
        var error = SHFileOperation(ref op);
        if (error == 0 && op.fAnyOperationsAborted == 0) return true;
        // OneDrive 等同步目录可能误报失败但实际已移入回收站——以文件消失为准
        return !File.Exists(path) && !Directory.Exists(path);
    }

    // ------------------------------------------------------------------ 批次 2

    /// <summary>unique_destination：同名自动加序号 (n)，不覆盖（inbox.py 语义）。</summary>
    public static string UniqueDestination(string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("同名文件过多，请选择其他名称");
    }

    private static readonly char[] InvalidName = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>delete_images：逐张移入回收站并标记 missing=1。</summary>
    public static (List<int> Done, List<string> Errors) DeleteImages(
        StoreService store, IReadOnlyCollection<int> ids)
    {
        var done = new List<int>();
        var errors = new List<string>();
        foreach (var id in ids)
        {
            var image = store.GetImage(id);
            if (image is null) continue;
            try
            {
                if (!DeleteToRecycle(image.AbsPath))
                    throw new IOException("回收站操作未完成");
                store.Exec($"UPDATE images SET missing=1 WHERE id={id}");
                store.Commit();
                done.Add(id);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                errors.Add($"{image.Name}：{ex.Message}");
            }
        }
        return (done, errors);
    }

    /// <summary>create_folder：只在选中目录下新建一级文件夹（名称校验照搬）。</summary>
    public static string CreateFolder(string rootPath, string dirRel, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') ||
            name.IndexOfAny(InvalidName) >= 0)
            throw new InvalidOperationException("文件夹名称无效");
        var parent = dirRel.Length > 0
            ? Path.Combine(rootPath, dirRel.Replace('/', '\\'))
            : rootPath;
        var target = Path.Combine(parent, name);
        Directory.CreateDirectory(target);
        return target;
    }

    /// <summary>delete_folder：整目录进回收站；根目录及祖先受保护；索引标 missing。</summary>
    public static string DeleteFolder(StoreService store, string rootPath, string dirRel)
    {
        var target = dirRel.Length > 0
            ? Path.Combine(rootPath, dirRel.Replace('/', '\\'))
            : rootPath;
        foreach (var root in store.Roots())
        {
            var protectedPath = Path.GetFullPath(root.Path);
            var pk = FileIdentity.PathKey(protectedPath).TrimEnd('\\');
            var tk = FileIdentity.PathKey(target);
            if (tk == pk || tk.StartsWith(pk + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能删除图库根目录或包含图库根目录的文件夹");
        }
        var relKey = FileIdentity.PathKey(target);
        var rootId = store.Roots().First(r =>
            FileIdentity.PathKey(r.Path) == FileIdentity.PathKey(rootPath)).Id;
        var ids = new List<int>();
        foreach (var row in store.QueryAll(
            "SELECT id,abs_path FROM images WHERE root_id=$r AND missing=0", ("$r", rootId)))
        {
            var dirName = Path.GetDirectoryName(row.AbsPath) ?? "";
            if (FileIdentity.PathKey(dirName) == relKey ||
                FileIdentity.PathKey(dirName).StartsWith(relKey.TrimEnd('\\') + "\\"))
                ids.Add(row.Id);
        }
        if (!DeleteToRecycle(target))
            throw new IOException("回收站操作未完成");
        foreach (var id in ids)
            store.Exec($"UPDATE images SET missing=1 WHERE id={id}");
        store.Commit();
        return target;
    }

    /// <summary>rename_image：只改磁盘文件名，file_key 保身份；库记录/sidecar 跟上。</summary>
    public static (bool Ok, string Error) RenameImage(
        StoreService store, int imageId, string newStem)
    {
        var image = store.GetImage(imageId);
        if (image is null) return (false, "找不到这张图");
        var source = image.AbsPath;
        if (!File.Exists(source)) return (false, "文件已经不在原位了");
        var stem = newStem.Trim().TrimEnd('.');
        if (stem.Length == 0) return (false, "名字不能为空");
        if (stem.IndexOfAny(InvalidName) >= 0)
            return (false, "名字里不能有 \\ / : * ? \" < > |");
        var target = Path.Combine(Path.GetDirectoryName(source)!, stem + Path.GetExtension(source));
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase)) return (true, "");
        if (File.Exists(target)) return (false, "这个名字已经被占用了");
        try { File.Move(source, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (false, $"改不了：{ex.Message}"); }
        try
        {
            var found = store.RootContaining(target)
                ?? throw new IOException("目标不在任何启用根目录内");
            var (rootId, rel) = found;
            var parent = Path.GetDirectoryName(rel)!;
            var dirRel = parent == "." ? "" : parent.Replace('\\', '/');
            store.Exec($"UPDATE images SET root_id={rootId},rel_path='{Esc2(rel)}',abs_path='{Esc2(target)}'," +
                       $"dir_rel='{Esc2(dirRel)}',name='{Esc2(Path.GetFileName(target))}'," +
                       $"file_key='{Esc2(FileIdentity.IdentityOf(target))}',missing=0 WHERE id={imageId}");
            store.Exec($"UPDATE review_drafts SET path_key='{Esc2(FileIdentity.PathKey(target))}' " +
                       $"WHERE path_key='{Esc2(FileIdentity.PathKey(source))}'");
            store.Commit();
            store.WriteSidecar(rootId, dirRel);
        }
        catch (Exception ex) // 磁盘已改名，库没跟上也要如实报出来
        {
            return (false, $"文件已改名，但图库记录没更新：{ex.Message}");
        }
        return (true, "");
    }

    private static string Esc2(string s) => s.Replace("'", "''");

    /// <summary>copy_images：复制到图库外目录，原文件保留、记录不动；同名加序号。</summary>
    public static (List<int> Done, List<string> Errors) CopyImages(
        StoreService store, IReadOnlyCollection<int> ids, string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new InvalidOperationException("目标文件夹不存在");
        var done = new List<int>();
        var errors = new List<string>();
        foreach (var id in ids)
        {
            var image = store.GetImage(id);
            if (image is null) continue;
            var source = image.AbsPath;
            if (!File.Exists(source)) { errors.Add($"{image.Name}：文件已不在原位"); continue; }
            try
            {
                File.Copy(source, UniqueDestination(Path.Combine(directory, image.Name)));
                done.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{image.Name}：{ex.Message}");
            }
        }
        return (done, errors);
    }

    /// <summary>move_images：同盘改名/跨盘临时复制+md5 校验；同名拒绝；managed=1；双目录 sidecar。</summary>
    public static (List<int> Done, List<string> Errors) MoveImages(
        StoreService store, IReadOnlyCollection<int> ids, string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory) || store.RootContaining(directory) is null)
            throw new InvalidOperationException("请选择已配置图库内的现有目录");
        var done = new List<int>();
        var errors = new List<string>();
        var sync = new HashSet<(int, string)>();
        foreach (var id in ids)
        {
            var image = store.GetImage(id);
            if (image is null) continue;
            var source = image.AbsPath;
            var destination = Path.Combine(directory, image.Name);
            string? temporary = null;
            var copied = false;
            try
            {
                if (FileIdentity.PathKey(source) == FileIdentity.PathKey(destination)) continue;
                if (File.Exists(destination)) throw new IOException("已有同名文件，未覆盖");
                if (Path.GetPathRoot(source)!.Equals(Path.GetPathRoot(directory)!,
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(source, destination); // 同盘：系统改名，身份不变
                }
                else
                {
                    temporary = Path.Combine(directory, ".gallery-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var src = File.OpenRead(source))
                    using (var dst = File.Create(temporary))
                        src.CopyTo(dst);
                    if (FileIdentity.ContentMd5(source) != FileIdentity.ContentMd5(temporary))
                        throw new IOException("移动复制校验失败");
                    File.Move(temporary, destination);
                    temporary = null;
                    copied = true;
                }
                var (rootId, rel) = store.RootContaining(destination)
                    ?? throw new IOException("目标不在任何启用根目录内");
                var parent = Path.GetDirectoryName(rel)!;
                var dirRel = parent == "." ? "" : parent.Replace('\\', '/');
                store.Exec($"UPDATE images SET root_id={rootId},rel_path='{Esc2(rel)}'," +
                           $"abs_path='{Esc2(destination)}',dir_rel='{Esc2(dirRel)}'," +
                           $"file_key='{Esc2(FileIdentity.IdentityOf(destination))}',missing=0,managed=1 WHERE id={id}");
                store.Exec($"UPDATE review_drafts SET path_key='{Esc2(FileIdentity.PathKey(destination))}' " +
                           $"WHERE path_key='{Esc2(FileIdentity.PathKey(source))}'");
                store.Commit();
                if (copied)
                {
                    try { File.Delete(source); }
                    catch (IOException ex) { errors.Add($"{image.Name}：已移动，源副本未能清理：{ex.Message}"); }
                }
                sync.Add((image.RootId, image.DirRel));
                sync.Add((rootId, dirRel));
                done.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                errors.Add($"{image.Name}：{ex.Message}");
            }
            finally
            {
                if (temporary is not null)
                    try { File.Delete(temporary); } catch (IOException) { }
            }
        }
        foreach (var (rootId, dirRel) in sync)
            store.WriteSidecar(rootId, dirRel);
        return (done, errors);
    }

    /// <summary>openers.reveal_in_explorer：资源管理器中定位。</summary>
    public static void RevealInExplorer(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "explorer.exe", "/select,\"" + Path.GetFullPath(path) + "\"") { UseShellExecute = true });
    }

    /// <summary>详情面板「打开」按钮的文案。</summary>
    public static string OpenLabel() => "用别的程序打开";

    /// <summary>openers.open_path：photos(ms-photos:) / openwith(ShellExecute verb) / custom / default。</summary>
    public static void OpenImage(string path, string mode = "photos", string customExe = "")
    {
        path = Path.GetFullPath(path);
        switch (mode)
        {
            case "photos":
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "ms-photos:viewer?fileName=" + Uri.EscapeDataString(path))
                    { UseShellExecute = true });
                    return;
                }
                catch (System.ComponentModel.Win32Exception) { /* 未注册则回退默认 */ }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return;
            case "openwith":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                { UseShellExecute = true, Verb = "openas" }); // 系统「打开方式」
                return;
            case "custom" when customExe.Length > 0 && File.Exists(customExe):
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(customExe, $"\"{path}\"") { UseShellExecute = true });
                return;
            default:
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return;
        }
    }
}
