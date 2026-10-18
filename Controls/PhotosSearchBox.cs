using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls;

/// <summary>
/// 移植 Photos SearchBoxControl 的主动聚焦判断，阻止启动、内容卸载等自动聚焦搜索框。
/// 来源：PhotosManager.Views.Controls/SearchBoxControl.cs:1406-1434、1622-1666。
/// 原版 AutoSuggestBox/IFocusService 适配为当前 TextBox/内容区锚点。
/// </summary>
public sealed class PhotosSearchBox : TextBox
{
    private bool _shouldFocusBeOnSearch;

    public UIElement? DefaultFocusAnchor { get; set; }

    public PhotosSearchBox()
    {
        GettingFocus += SearchBox_GettingFocus;
        // Photos 原版也用 handledEventsToo，内部文本编辑器已处理的鼠标点击仍须识别。
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _shouldFocusBeOnSearch = true;
            if (FocusState == FocusState.Unfocused) Focus(FocusState.Pointer);
            _shouldFocusBeOnSearch = false;
        }), true);
    }

    /// <summary>明确的搜索命令允许聚焦；不依赖之前恰好聚焦在哪个控件。</summary>
    public void FocusSearch()
    {
        _shouldFocusBeOnSearch = true;
        Focus(FocusState.Keyboard);
        _shouldFocusBeOnSearch = false;
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        // TextBox 自身处理点击前标记意图，替代原版 AutoSuggestBox 内部文本框接线。
        _shouldFocusBeOnSearch = true;
        base.OnPointerPressed(e);
        _shouldFocusBeOnSearch = false;
    }

    private void SearchBox_GettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        if (!IsFocusOnSearchIntentional(e.OldFocusedElement, e.FocusState, e.Direction))
        {
            if (DefaultFocusAnchor is null || !e.TrySetNewFocusedElement(DefaultFocusAnchor))
                e.TryCancel();
            e.Handled = true;
        }
        _shouldFocusBeOnSearch = false;
    }

    private bool IsFocusOnSearchIntentional(DependencyObject? oldFocusObject,
        FocusState focusState, FocusNavigationDirection focusDirection)
    {
        if (_shouldFocusBeOnSearch) return true;
        if (focusState is FocusState.Programmatic or FocusState.Unfocused) return false;
        if (focusDirection is FocusNavigationDirection.Next or FocusNavigationDirection.Previous) return true;
        if ((focusState is FocusState.Pointer or FocusState.Keyboard) && IsFromKnownFocusStealingSubtree(oldFocusObject))
            return false;
        return true;
    }

    private bool IsFromKnownFocusStealingSubtree(DependencyObject? oldFocusObject)
    {
        // Photos 的 GalleryVirtualView / ToastNotificationControl / TeachingTip 对应当前实现。
        // 内容区锚点涵盖图库与设置页切换；查看器属于当前壳中的独立浮层。
        for (var parent = oldFocusObject is null ? null : VisualTreeHelper.GetParent(oldFocusObject);
             parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, DefaultFocusAnchor) ||
                parent is PhotoGalleryPanel or ToastHost or TeachingTip or PreviewOverlay)
                return true;
        }
        return false;
    }
}
