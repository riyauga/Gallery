using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gallery.Controls;

/// <summary>无数字提示点；宿主负责状态与消失条件，颜色跟随主题强调色，不接收焦点或点击。</summary>
public sealed partial class NotificationDot : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(NotificationDot), new PropertyMetadata(false,
            (sender, args) => ((NotificationDot)sender).Visibility = (bool)args.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public NotificationDot() => InitializeComponent();
}
