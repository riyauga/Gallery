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
        public int fAnyOperationsAborted;
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
        return SHFileOperation(ref op) == 0;
    }
}
