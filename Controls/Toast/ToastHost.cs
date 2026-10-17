using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls;

public enum ToastKind { Info, Success, Pending }

/// <summary>
/// 通知浮层，照搬 galleryold <c>app/ui/toast.py</c>：
/// 宽 348、高度随内容、寿命 5000ms、右下堆叠间距 10、屏幕/窗口边距 18、最多 4 条（新的在最下），
/// 左缘 4px 色带：成功 #3f9d63 / 需处理 #d08a2c / 中性用强调灰；
/// 圆角 12，深 #2b2b2b/边 #4a4a4a，浅 #ffffff/边 #dedede；标题 15px w500，副文 13px muted。
/// </summary>
public sealed class ToastHost : StackPanel
{
    private const int MaxVisible = 4;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(5000);

    public ToastHost()
    {
        Orientation = Orientation.Vertical;
        Spacing = 10;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 18, 18);
        IsHitTestVisible = false;
    }

    public void Show(string message, ToastKind kind = ToastKind.Info, string? detail = null)
    {
        var card = BuildCard(message, kind, detail);
        Children.Add(card);
        while (Children.Count > MaxVisible)
            Children.RemoveAt(0);

        var timer = new DispatcherTimer { Interval = Lifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Children.Contains(card)) Children.Remove(card);
        };
        timer.Start();
    }

    private static Border BuildCard(string message, ToastKind kind, string? detail)
    {
        bool dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var band = kind switch
        {
            ToastKind.Success => ColorFrom("#3f9d63"),
            ToastKind.Pending => ColorFrom("#d08a2c"),
            _ => ColorFrom(dark ? "#5a5a5a" : "#9a9a9a"),
        };
        var bg = dark ? ColorFrom("#2b2b2b") : ColorFrom("#ffffff");
        var border = dark ? ColorFrom("#4a4a4a") : ColorFrom("#dedede");

        var title = new TextBlock
        {
            Text = message,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(dark ? ColorFrom("#f3f3f3") : ColorFrom("#1a1a1a")),
        };

        object content = detail is null ? title : new StackPanel
        {
            Spacing = 2,
            Children =
            {
                title,
                new TextBlock
                {
                    Text = detail,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Opacity = 0.65,
                    Foreground = new SolidColorBrush(dark ? ColorFrom("#c9c9c9") : ColorFrom("#555555")),
                },
            }
        };

        return new Border
        {
            Width = 348,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            Child = new Grid
            {
                ColumnSpacing = 0,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(4) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                },
                Children =
                {
                    BuildBand(band),
                    BuildBody(content),
                }
            },
        };
    }

    private static Microsoft.UI.Xaml.Shapes.Rectangle BuildBand(Windows.UI.Color color)
        => new() { Fill = new SolidColorBrush(color), RadiusX = 2, RadiusY = 2 };

    private static ContentPresenter BuildBody(object content)
    {
        var presenter = new ContentPresenter
        {
            Margin = new Thickness(12, 10, 12, 10),
            VerticalAlignment = VerticalAlignment.Center,
            Content = content,
        };
        Grid.SetColumn(presenter, 1);
        return presenter;
    }

    private static Windows.UI.Color ColorFrom(string hex)
        => Tags.TagColors.FromHex(hex);
}
