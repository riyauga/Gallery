using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls;

/// <summary>
/// 侧栏拖拽把手，照搬 photos 的 CollectionRootPage Gripper
/// （CommunityToolkit SizerBase：E784 双竖线字形（TextBlock + SymbolThemeFontFamily）、
/// 透明底、宽 16、悬停双向箭头光标）。
/// 拖动经 <see cref="WidthChanged"/> 通知宿主改 OpenPaneLength；
/// 松开触发 <see cref="DragFinished"/>（宿主记忆宽度）。
/// </summary>
public sealed class PaneResizer : Grid
{
    public event Action<double>? WidthChanged;
    public event Action? DragFinished;

    /// <summary>宽度范围（galleryold：180–500）。</summary>
    public double MinLength { get; set; } = 180;
    public double MaxLength { get; set; } = 500;

    /// <summary>宿主提供当前 OpenPaneLength。</summary>
    public Func<double>? CurrentLength { get; set; }

    private double _startLength, _startScreenX;
    private bool _dragging;
    private readonly DispatcherTimer _dragTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public PaneResizer()
    {
        Width = 16;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0x00, 0, 0, 0)); // 非空刷子保证命中

        // 逐字照搬 photos LeftNavGripperStyle 模板：
        // TextBlock + SymbolThemeFontFamily + Text=E784（两条竖线）+ TextFillColorTertiary
        Children.Add(new TextBlock
        {
            Text = "\uE784",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });

        PointerEntered += (_, _) => ProtectedCursor =
            InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast); // 悬停双向箭头
        PointerExited += (_, _) => ProtectedCursor = null;

        PointerPressed += (_, _) =>
        {
            _startLength = CurrentLength?.Invoke() ?? 0;
            GetCursorPos(out var pt);
            _startScreenX = pt.X;
            _dragging = true;
            _dragTimer.Start();
        };
        // 拖动更新走 16ms 轮询（GetCursorPos 系统级光标），
        // 因为按住状态下的 PointerMoved 在本环境不可靠（实测注入与真实输入都会丢）。
        _dragTimer.Tick += (_, _) =>
        {
            if (!_dragging) { _dragTimer.Stop(); return; }
            if ((GetAsyncKeyState(0x01) & 0x8000) == 0) // 左键已松开
            {
                _dragging = false;
                _dragTimer.Stop();
                DragFinished?.Invoke();
                return;
            }
            GetCursorPos(out var pt);
            var dx = (pt.X - _startScreenX) / (XamlRoot?.RasterizationScale ?? 1.0);
            var next = Math.Clamp(_startLength + dx, MinLength, MaxLength);
            WidthChanged?.Invoke(next);
        };
        PointerReleased += (_, _) =>
        {
            if (_dragging)
            {
                _dragging = false;
                _dragTimer.Stop();
                DragFinished?.Invoke();
            }
        };
    }
}
