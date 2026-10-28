using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace Gallery.Controls;

/// <summary>
/// Photos 收藏按钮：EB51 空心 / EB52 红色实心。
/// 按钮本身保持透明，悬停时也不出现系统按钮底色。
/// </summary>
public sealed class FavoriteButton : Button
{
    private const string OutlineGlyph = "\uEB51";
    private const string FilledGlyph = "\uEB52";
    private static readonly Color FavoriteRed = Color.FromArgb(0xFF, 0xE8, 0x11, 0x23);

    private readonly FontIcon _icon;
    private bool _favorite;
    private bool _toggling;

    public event Func<bool, Task<bool>>? ToggleRequested;

    private Brush? _outlineForeground;

    /// <summary>
    /// 未收藏时的心形颜色。默认取应用级主题资源（缩略图上就是白心）；
    /// 宿主面板若是主题化的浅色/深色底，可改设成对比色。
    /// </summary>
    public Brush? OutlineForeground
    {
        get => _outlineForeground;
        set
        {
            _outlineForeground = value;
            ApplyGlyphColor();
        }
    }

    public bool Favorite
    {
        get => _favorite;
        set
        {
            _favorite = value;
            _icon.Glyph = value ? FilledGlyph : OutlineGlyph;
            ApplyGlyphColor();
            AutomationProperties.SetName(this, value ? "取消收藏" : "添加到收藏夹");
            ToolTipService.SetToolTip(this, value ? "取消收藏" : "添加到收藏夹");
        }
    }

    private void ApplyGlyphColor() => _icon.Foreground = _favorite
        ? new SolidColorBrush(FavoriteRed)
        : _outlineForeground ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    public FavoriteButton()
    {
        Width = 32;
        Height = 32;
        Padding = new Thickness(0);
        MinWidth = 0;
        MinHeight = 0;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderThickness = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        UseSystemFocusVisuals = true;
        _icon = new FontIcon
        {
            FontFamily = (FontFamily)Application.Current.Resources["PhotosUWPFontFamily"],
            FontSize = 18,
        };
        Content = _icon;
        Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PointerPressed += (_, e) => e.Handled = true;
        PointerReleased += (_, e) => e.Handled = true;
        Tapped += (_, e) => e.Handled = true;
        Favorite = false;
        Click += async (_, _) =>
        {
            if (ToggleRequested is null || _toggling) return;
            // 不用 IsEnabled 防重入：禁用会立刻把焦点从按钮上赶走，回车收藏后焦点就跳到下一个控件了。
            _toggling = true;
            try
            {
                Favorite = await ToggleRequested(!_favorite);
            }
            finally
            {
                _toggling = false;
            }
        };
    }
}
