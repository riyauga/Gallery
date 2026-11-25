using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Gallery.Controls.Catalog;

/// <summary>详情页关联资料卡片；宿主负责打开资料和执行删除。</summary>
public sealed class CatalogRelationCard : Button
{
    // 自绘勾选框：空框只在「已选中」时出现强调色填充与勾。
    // 不用 CheckBox——套在 Button 内容里时它的底色与勾来自两套主题字典，
    // 深色下会渲染成「深底 + 黑勾」，和未选中几乎分不清（2026-11-25 反馈）。
    private readonly Border _box = new()
    {
        CornerRadius = new(4), BorderThickness = new(1), Background = null,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };

    private readonly FontIcon _tick = new()
    {
        Glyph = "\uE73E", FontSize = 11, Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    private bool _selected;

    // 强调色高亮层：与资料列表 IndexRow 的 FeedbackLayer 同一套脉冲（新建后定位用）。
    private readonly Border _feedback;
    private Storyboard? _pulse;

    /// <summary>选择模式：勾选框常显；单击由宿主切换选中，不再打开资料。</summary>
    public bool SelectMode
    {
        get => _box.Visibility == Visibility.Visible;
        set
        {
            _box.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value) Selected = false;
        }
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            ApplyCheckVisual();
        }
    }

    public CatalogRelationCard(string name, string subtitle, Action delete)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        // 内容区拉满，高亮层才能覆盖整张卡片（文字块自己靠左）。
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        MinHeight = 57;
        Padding = new(12, 7, 12, 7);
        var body = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        body.Children.Add(new Grid
        {
            Width = 20, Height = 20, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { _box, _tick },
        });
        body.Children.Add(new StackPanel { Spacing = 2, Children =
        {
            new TextBlock { Text = name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            new TextBlock { Text = subtitle, FontSize = 12, Opacity = .68 },
        } });
        _feedback = new Border
        {
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Opacity = .01,
            IsHitTestVisible = false,
            CornerRadius = new(4),
            Margin = new(-12, -7, -12, -7),
        };
        Content = new Grid { Children = { _feedback, body } };
        AutomationProperties.SetName(this, subtitle.Length > 0 ? $"{name}，{subtitle}" : name);
        var red = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var item = new MenuFlyoutItem
        {
            Text = "删除资料", Foreground = red,
            Icon = new FontIcon { Glyph = (string)Application.Current.Resources["DeleteGlyph"], Foreground = red },
        };
        item.Resources["MenuFlyoutItemForegroundPointerOver"] = red;
        item.Resources["MenuFlyoutItemForegroundPressed"] = red;
        item.Click += (_, _) => delete();
        ContextFlyout = new MenuFlyout { Items = { item } };

        ApplyCheckVisual();
        ActualThemeChanged += (_, _) => ApplyCheckVisual();
    }

    /// <summary>空框：透明填充 + 描边；选中：强调色填充 + 勾（颜色按元素实际主题取）。</summary>
    private void ApplyCheckVisual()
    {
        _tick.Visibility = _selected ? Visibility.Visible : Visibility.Collapsed;
        if (_selected)
        {
            var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            _box.Background = accent;
            _box.BorderBrush = accent;
            _tick.Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        }
        else
        {
            // 透明填充而不是 null：null 会让 Border 不参与命中测试，页面空白点击判定会把它当空洞。
            _box.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            _box.BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
        }
    }

    /// <summary>
    /// 新建后定位用：强调色脉冲两次，与资料列表的 HighlightCreatedEntry 一致
    /// （淡出终点留 0.01，避免合成层停止绘制后不再显示）。
    /// </summary>
    public void Pulse()
    {
        StopPulse();
        var opacity = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (milliseconds, value) in new[] { (0, .01), (125, .36), (300, .01), (425, .36), (700, .01) })
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
                Value = value,
            });
        Storyboard.SetTarget(opacity, _feedback);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        var storyboard = _pulse = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Completed += (_, _) => { _pulse = null; _feedback.Opacity = .01; };
        storyboard.Begin();
    }

    public void StopPulse()
    {
        var storyboard = _pulse;
        _pulse = null;
        storyboard?.Stop();
        _feedback.Opacity = .01;
    }

    /// <summary>右键进入选择并选中：宿主接到后替换菜单为批量删除。</summary>
    public Action? RightSelected;

    protected override void OnRightTapped(Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        base.OnRightTapped(e);
        RightSelected?.Invoke();
    }
}
