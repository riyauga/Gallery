using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Gallery.Services;
using Gallery.Views;
using Windows.Graphics;
using System.Runtime.InteropServices;

namespace Gallery;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private readonly Library _library = Library.Load();
    private readonly GalleryPage _page;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        Title = "照片";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
        }

        _page = new GalleryPage(_library);
        _page.ToastRequested += ShowToast;
        _page.PreviewRequested += (item, items) => Preview.Open(items, item);
        ContentHost.Children.Add(_page);

        Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        Nav.Loaded += (_, _) =>
        {
            Nav.IsPaneOpen = true;
            Nav.SelectedItem = Nav.MenuItems[0];
        };

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            AnimateToast(0, 200);
        };

        Activated += OnFirstActivated;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        DispatcherQueue.TryEnqueue(() =>
        {
            ResizeAndCenter(1290, 700);
        });
    }

    private void ResizeAndCenter(int dipWidth, int dipHeight)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        int screenW = GetSystemMetrics(0);
        int screenH = GetSystemMetrics(1);
        int width = Math.Min((int)(dipWidth * scale), screenW - 80);
        int height = Math.Min((int)(dipHeight * scale), screenH - 80);
        int x = (screenW - width) / 2;
        int y = (screenH - height) / 2;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x0004);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        int index = args.SelectedItem is NavigationViewItem item ? Nav.MenuItems.IndexOf(item) : 0;
        _page.SetMode(index == 1);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => _page.SetSearch(SearchBox.Text);

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen) { Preview.HandleKey(Windows.System.VirtualKey.Escape); args.Handled = true; }
    }

    private void OnLeftInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen) { Preview.HandleKey(Windows.System.VirtualKey.Left); args.Handled = true; }
    }

    private void OnRightInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Preview.IsOpen) { Preview.HandleKey(Windows.System.VirtualKey.Right); args.Handled = true; }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        AnimateToast(1, 150);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void AnimateToast(double to, int ms)
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, Toast);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }
}
