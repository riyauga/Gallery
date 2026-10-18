using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Gallery.Services;

/// <summary>
/// 托盘图标（Win32 Shell_NotifyIcon）。WinUI 3 没有内置托盘，这里开一个消息专用窗口
/// 收 WM_TRAYICON：左键/双击唤回主窗口，右键弹菜单（唤回 / 待审核 / 退出）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x8000 + 1;   // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const uint IDI_APPLICATION = 32512;

    private readonly uint _id = (uint)Environment.ProcessId;
    private Thread? _thread;
    private WndProcDelegate? _wndProc;
    private IntPtr _hwnd;
    private volatile bool _created;
    private string _tip = "Gallery";

    /// <summary>左键/双击：唤回主窗口。</summary>
    public event Action? ActivateRequested;

    /// <summary>右键请求：在 UI 线程使用应用主题的 Photos 菜单模板。</summary>
    public event Action<int, int>? MenuRequested;

    public bool IsCreated => _created;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "gallery-tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void UpdateTip(string tip)
    {
        _tip = tip;
        if (!_created) return;
        var data = BuildData();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_created)
        {
            var data = BuildData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _created = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            // HWND 属于托盘线程，只能让所属线程销毁并结束其消息循环。
            PostMessage(_hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ------------------------------------------------------------------ 内部

    private void Run()
    {
        try
        {
            RunCore();
        }
        catch (Exception ex)
        {
            Services.AppService.DebugLog("tray crashed: " + ex.Message);
        }
    }

    private void RunCore()
    {
        var className = "GalleryTrayWindow_" + Environment.ProcessId;
        _wndProc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        var atom = RegisterClassEx(ref wc);
        if (atom == 0)
            Services.AppService.DebugLog($"tray register failed err={Marshal.GetLastWin32Error()}");

        _hwnd = CreateWindowEx(0, className, "Gallery Tray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Services.AppService.DebugLog(
                $"tray create window failed err={Marshal.GetLastWin32Error()} atom={atom} cls={className}");
            return;
        }

        var data = BuildData();
        data.hWnd = _hwnd;
        _created = Shell_NotifyIcon(NIM_ADD, ref data);
        Services.AppService.DebugLog(_created ? "tray icon added" : "tray icon add failed");
        if (!_created)
            Services.AppService.DebugLog(
                $"tray add failed err={Marshal.GetLastWin32Error()} cbSize={data.cbSize} " +
                $"sizeof={Marshal.SizeOf<NOTIFYICONDATA>()} hwnd={_hwnd}");
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private NOTIFYICONDATA BuildData()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = _id,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION),
            szTip = _tip,
        };
        return data;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x0010) { DestroyWindow(hwnd); return IntPtr.Zero; }
        if (msg == 0x0002) { _hwnd = IntPtr.Zero; PostQuitMessage(0); return IntPtr.Zero; }
        if (msg == WM_TRAYICON)
        {
            var evt = lParam.ToInt32() & 0xFFFF;
            switch (evt)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    ActivateRequested?.Invoke();
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    GetCursorPos(out var point);
                    AppService.DebugLog($"tray right-up x={point.X} y={point.Y}");
                    MenuRequested?.Invoke(point.X, point.Y);
                    return IntPtr.Zero;
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ P/Invoke

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

}
