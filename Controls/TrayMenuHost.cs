using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Graphics;
using Microsoft.UI.Windowing;

namespace Gallery.Controls;

/// <summary>托盘位置的临时 WinUI 宿主：复用 Photos 的 MenuFlyout，由原生模板处理主题、焦点和菜单交互。</summary>
public sealed class TrayMenuHost : Window
{
    private readonly Grid _anchor;
    private readonly MenuFlyout _menu;
    private bool _active;
    private bool _showQueued;
    private bool _dismissed;

    public TrayMenuHost(ElementTheme theme, int x, int y, int pending,
        Action open, Action review, Action quit)
    {
        _anchor = new Grid { RequestedTheme = theme };
        Content = _anchor;
        // 菜单允许越过 1px 宿主边界，由 WinUI 在屏幕可用区域内放置。
        _menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        Add("打开图库", open);
        Add(pending > 0 ? $"待处理 {pending} 条" : "待处理", review);
        _menu.Items.Add(new MenuFlyoutSeparator());
        Add("退出 Gallery", quit);
        _menu.Opened += (_, _) => Services.AppService.DebugLog("tray menu opened");
        _menu.Closed += (_, _) =>
        {
            _dismissed = true;
            Services.AppService.DebugLog("tray menu closed");
            Close();
        };
        Closed += (_, _) =>
        {
            _dismissed = true;
            Services.AppService.DebugLog("tray menu host closed");
        };
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        // 托盘坐标在任务栏内；把锚点移到同一显示器的工作区，避免菜单宿主藏在任务栏后。
        var workArea = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest).WorkArea;
        x = Math.Clamp(x, workArea.X, workArea.X + workArea.Width - 2);
        y = Math.Clamp(y, workArea.Y, workArea.Y + workArea.Height - 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, 1, 1));
        Services.AppService.DebugLog($"tray menu host ready anchorLoaded={_anchor.IsLoaded} bounds={AppWindow.Size.Width}x{AppWindow.Size.Height}");
        Activated += (_, args) =>
        {
            _active = args.WindowActivationState != WindowActivationState.Deactivated;
            QueueShow();
        };
        _anchor.Loaded += (_, _) =>
        {
            Services.AppService.DebugLog($"tray menu anchor loaded size={_anchor.ActualWidth}x{_anchor.ActualHeight} root={_anchor.XamlRoot is not null}");
            QueueShow();
        };
    }

    private void QueueShow()
    {
        if (_dismissed || _showQueued || !_active || !_anchor.IsLoaded || _menu.IsOpen) return;
        _showQueued = true;
        // Loaded 会发生在 Activate 内部；等激活消息处理完再打开，避免菜单被随后的焦点变更关闭。
        DispatcherQueue.TryEnqueue(() =>
        {
            _showQueued = false;
            if (_dismissed || !_active || _menu.IsOpen) return;
            _menu.ShowAt(_anchor, new FlyoutShowOptions
            {
                Position = new Point(0, 0), Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
            });
            Services.AppService.DebugLog($"tray menu ShowAt returned open={_menu.IsOpen}");
        });
    }

    private void Add(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => { _menu.Hide(); action(); };
        _menu.Items.Add(item);
    }

    public void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        if (_menu.IsOpen) _menu.Hide();
        else Close();
    }
}
