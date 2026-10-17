using System.Runtime.InteropServices;

namespace Gallery.Services;

/// <summary>
/// 文件身份，逐行移植 galleryold <c>app/identity.py</c>：
/// <c>path_key</c> = normcase(绝对路径)（Windows 下全小写+反斜杠）；
/// <c>file_identity</c> = "卷序列号:文件索引"（同卷移动/改名不变，索引为 0 时返回空）。
/// </summary>
public static class FileIdentity
{
    public static string PathKey(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Replace('/', '\\').ToLowerInvariant();
    }

    public static string IdentityOf(string path)
    {
        var handle = Native.CreateFile(path,
            Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return "";
        try
        {
            if (!Native.GetFileInformationByHandle(handle, out var info)) return "";
            long index = ((long)info.nFileIndexHigh << 32) | info.nFileIndexLow;
            if (index == 0) return "";
            return $"{info.dwVolumeSerialNumber}:{index}";
        }
        finally
        {
            handle.Dispose(); // SafeFileHandle 释放即 CloseHandle
        }
    }

    public static string ContentMd5(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint dwVolumeSerialNumber;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint nNumberOfLinks;
            public uint nFileIndexHigh;
            public uint nFileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
