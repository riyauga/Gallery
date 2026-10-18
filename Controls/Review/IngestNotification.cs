using System.Runtime.InteropServices;
using Gallery.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Gallery.Controls.Review;

/// <summary>新图片提示卡：只展示文件信息，十秒倒计时在悬停时暂停，更新合并内容后重新计时。</summary>
public sealed class IngestNotificationCard : Grid
{
    /// <summary>整卡尺寸按用户要求缩到原来的 3/4（2026-10-19）：宽度、内边距、字号、按钮高一起缩。</summary>
    public const double CardScale = 0.75;

    private readonly TextBlock _heading = new() { FontSize = 20 * CardScale, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _name = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock _detail = new() { FontSize = 11, Opacity = .7 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long _remaining, _lastTick;
    private bool _hovered, _dismissed;
    public event Action? OpenRequested;
    public event Action? Dismissed;

    public IngestNotificationCard()
    {
        Width = 428 * CardScale;
        Padding = new Thickness(20 * CardScale, 16 * CardScale, 20 * CardScale, 20 * CardScale);
        CornerRadius = new CornerRadius(14 * CardScale);
        Background = Brush(0x29, 0x2f, 0x34);
        BorderBrush = Brush(0x55, 0x60, 0x69);
        BorderThickness = new Thickness(1);
        RequestedTheme = ElementTheme.Dark;
        var body = new StackPanel { Spacing = 14 * CardScale };
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = "▧  Gallery", FontSize = 12, Foreground = Brush(0xc0, 0xcb, 0xd0), VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(5, 0, 5, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "关闭新图片通知");
        close.Click += (_, _) => Dismiss();
        header.Children.Add(close);
        body.Children.Add(header);
        foreach (var text in new[] { _heading, _name, _detail }) { text.Foreground = Brush(0xf3, 0xf5, 0xf6); body.Children.Add(text); }
        var open = new Button { Content = "查看并归档  →", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brush(0xaf, 0xc1, 0xc5), Foreground = Brush(0x20, 0x30, 0x35), MinHeight = 42 * CardScale };
        open.Click += (_, _) => Open();
        body.Children.Add(open);
        Children.Add(body);
        Tapped += (_, _) => Open();
        PointerEntered += (_, _) => { AccountElapsed(); _hovered = true; };
        PointerExited += (_, _) => { _hovered = false; _lastTick = Environment.TickCount64; };
        _timer.Tick += (_, _) => { AccountElapsed(); if (_remaining <= 0) Dismiss(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void Update(int count, string name, string detail)
    {
        _heading.Text = count > 1 ? $"新增 {count} 张图片待归档" : "新图片待归档";
        _name.Text = count > 1 ? "最近：" + name : name;
        _detail.Text = detail;
        _remaining = 10_000;
        _lastTick = Environment.TickCount64;
        _timer.Start();
    }

    private void AccountElapsed()
    {
        var now = Environment.TickCount64;
        if (!_hovered) _remaining -= now - _lastTick;
        _lastTick = now;
    }

    private void Open() { if (_dismissed) return; OpenRequested?.Invoke(); Dismiss(); }
    public void Dismiss() { if (_dismissed) return; _dismissed = true; _timer.Stop(); Dismissed?.Invoke(); }
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
}

/// <summary>后台下载通知的无激活置顶窗口，位于主窗口所在显示器工作区右下角，不进入任务栏。</summary>
public sealed class IngestNotificationWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    public IngestNotificationCard Card { get; } = new();
    private bool _closed;

    public IngestNotificationWindow(Window owner)
    {
        Content = Card;
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // 当前运行时的 IsShownInSwitchers 未实现；工具窗口样式同时排除任务栏和 Alt+Tab，并禁止被动激活。
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new nint((style & ~0x00040000L) | 0x00000080L | 0x08000000L));
        var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        var scale = Math.Max(96, GetDpiForWindow(ownerHwnd)) / 96.0;
        var work = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = (int)Math.Ceiling(428 * IngestNotificationCard.CardScale * scale);
        var height = (int)Math.Ceiling(246 * IngestNotificationCard.CardScale * scale);
        var gap = (int)Math.Ceiling(18 * scale);
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - width - gap, work.Y + work.Height - height - gap, width, height));
        Card.Dismissed += Dismiss;
        Closed += (_, _) => { _closed = true; Card.Dismiss(); };
        Card.SizeChanged += (_, _) =>
        {
            if (_closed || Card.ActualHeight <= 0) return;
            var actualHeight = (int)Math.Ceiling(Card.DesiredSize.Height * scale);
            if (actualHeight > 0 && actualHeight != AppWindow.Size.Height)
                AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - width - gap, work.Y + work.Height - actualHeight - gap, width, actualHeight));
        };
    }

    public void ShowWithoutActivation()
    {
        AppWindow.Show(false);
        SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(this), new nint(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002);
    }

    public void Dismiss() { if (_closed) return; _closed = true; Close(); }
}
