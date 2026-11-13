using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Gallery.Controls.Review;

/// <summary>The review surface owns its native spray cursor; CSS custom cursors are lost by WinUI's WebView adapter.</summary>
internal sealed class ReviewWebView : WebView2
{
    private InputCursor? _spray, _previous;
    private double _cursorScale;
    private bool _spraying, _queued;

    public ReviewWebView()
    {
        PointerMoved += (_, _) =>
        {
            // Run after WebView's input handlers, which may restore their CSS system cursor.
            if (!_spraying || _queued) return;
            _queued = DispatcherQueue.TryEnqueue(() => { _queued = false; if (_spraying) ApplySpray(); });
        };
        PointerExited += (_, _) => SetSprayCursor(false);
        Unloaded += (_, _) =>
        {
            SetSprayCursor(false);
            _spray?.Dispose(); _spray = null;
        };
    }

    public void SetSprayCursor(bool enabled)
    {
        if (enabled)
        {
            if (!_spraying) _previous = ProtectedCursor;
            _spraying = true;
            ApplySpray();
        }
        else if (_spraying)
        {
            _spraying = false;
            ProtectedCursor = _previous;
            _previous = null;
        }
    }

    private void ApplySpray()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        if (_spray is null || _cursorScale != scale)
        {
            var next = CreateSpray(scale);
            var old = _spray;
            _spray = next; _cursorScale = scale;
            ProtectedCursor = next;
            old?.Dispose();
        }
        else ProtectedCursor = _spray;
    }

    private static InputCursor CreateSpray(double scale)
    {
        using var stream = typeof(ReviewWebView).Assembly.GetManifestResourceStream("Gallery.Controls.Review.Spray.cur")!;
        using var reader = new BinaryReader(stream);
        reader.ReadUInt16(); reader.ReadUInt16();
        var count = reader.ReadUInt16();
        var requested = (int)Math.Ceiling(32 * scale);
        (int Size, ushort X, ushort Y, int Length, uint Offset) best = default;
        for (var i = 0; i < count; i++)
        {
            int size = reader.ReadByte(); reader.ReadByte(); reader.ReadUInt16();
            var x = reader.ReadUInt16(); var y = reader.ReadUInt16();
            var length = reader.ReadInt32(); var offset = reader.ReadUInt32();
            if (best.Size == 0 || (best.Size < requested && size > best.Size)) best = (size, x, y, length, offset);
        }
        stream.Position = best.Offset;
        var data = new byte[best.Length + 4];
        BitConverter.TryWriteBytes(data.AsSpan(0, 2), best.X);
        BitConverter.TryWriteBytes(data.AsSpan(2, 2), best.Y);
        stream.ReadExactly(data.AsSpan(4));
        var handle = CreateIconFromResourceEx(data, (uint)data.Length, false, 0x00030000, best.Size, best.Size, 0);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            const string className = "Microsoft.UI.Input.InputCursor";
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var name));
            try
            {
                var iid = typeof(IInputCursorStaticsInterop).GUID;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref iid, out var factory));
                try
                {
                    Marshal.ThrowExceptionForHR(factory.CreateFromHCursor(handle, out var result));
                    try { return WinRT.MarshalInterface<InputCursor>.FromAbi(result); }
                    finally { Marshal.Release(result); }
                }
                finally { Marshal.ReleaseComObject(factory); }
            }
            finally { WindowsDeleteString(name); }
        }
        finally { DestroyCursor(handle); }
    }

    [ComImport, Guid("ac6f5065-90c4-46ce-beb7-05e138e54117"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInputCursorStaticsInterop
    {
        void GetIids(out uint count, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr name);
        void GetTrustLevel(out int level);
        [PreserveSig] int CreateFromHCursor(IntPtr cursor, out IntPtr result);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr cursor);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IInputCursorStaticsInterop factory);
}
