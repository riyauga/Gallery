// Source: PhotosManager.Views.Controls/IconWithBadge.cs; namespace/platform projection adapted only.
// 原版源码未启用 nullable 标注；反编译产物保留 null 语义，故本文件关闭 nullable 警告，不做逐行改写。
#nullable disable warnings
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;



namespace Gallery.Controls;

[TemplatePart(Name = "PartContainerGrid", Type = typeof(Grid))]
[TemplatePart(Name = "PartIcon", Type = typeof(IconSourceElement))]
[TemplatePart(Name = "PartBadgeIcon", Type = typeof(IconSourceElement))]
public sealed class IconWithBadge : Control
{
	public static readonly DependencyProperty IconProperty = DependencyProperty.Register("Icon", typeof(IconSource), typeof(IconWithBadge), new PropertyMetadata((object)null, (PropertyChangedCallback)((DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		((IconWithBadge)(object)d).OnIconChanged();
	})));

	public static readonly DependencyProperty BadgeIconProperty = DependencyProperty.Register("BadgeIcon", typeof(IconSource), typeof(IconWithBadge), new PropertyMetadata((object)null, (PropertyChangedCallback)((DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		((IconWithBadge)(object)d).OnBadgeIconChanged();
	})));

	public static readonly DependencyProperty IconAccessibilityViewProperty = DependencyProperty.Register("IconAccessibilityView", typeof(AccessibilityView), typeof(IconWithBadge), new PropertyMetadata((object)(AccessibilityView)2, (PropertyChangedCallback)((DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		((IconWithBadge)(object)d).TryUpdateIconAccessibilityView();
	})));

	private const double WidthWithoutBadgeIcon = 20.0;

	private const double WidthWithBadgeIcon = 30.0;

	private const double IconHeight = 20.0;

	private const double IconWidth = 20.0;

	private const double BadgeIconHeight = 16.0;

	private const double BadgeIconWidth = 16.0;

	private const double BadgeIconRightMargin = 4.0;

	private const double BadgeIconBottomMargin = 4.0;

	private static readonly IconSource NullIcon = (IconSource)new FontIconSource();

	public IconSource Icon
	{
		get
		{
			return (IconSource)((DependencyObject)this).GetValue(IconProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(IconProperty, (object)value);
		}
	}

	public IconSource BadgeIcon
	{
		get
		{
			return (IconSource)((DependencyObject)this).GetValue(BadgeIconProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(BadgeIconProperty, (object)value);
		}
	}

	public AccessibilityView IconAccessibilityView
	{
		get
		{
			return (AccessibilityView)((DependencyObject)this).GetValue(IconAccessibilityViewProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(IconAccessibilityViewProperty, (object)value);
		}
	}

	private Grid? PartContainerGrid { get; set; }

	private IconSourceElement? PartIcon { get; set; }

	private IconSourceElement? PartBadgeIcon { get; set; }

	public IconWithBadge()
	{
		DefaultStyleKey = typeof(IconWithBadge);
		((UIElement)this).IsTabStop = false;
	}

	protected override void OnApplyTemplate()
	{
		if (PartIcon != null)
		{
			((FrameworkElement)PartIcon).LayoutUpdated -= OnPartIconLayoutUpdated;
		}
		base.OnApplyTemplate();
		PartIcon = null;
		PartBadgeIcon = null;
		PartContainerGrid = null;
		TryUpdateIcon();
		TryUpdateBadgeIcon();
		RequestIconAccessibilityViewUpdate();
	}

	private static void TryUpdateIconSourceElement(IconSourceElement? iconSourceElement, IconSource iconSource)
	{
		if (iconSourceElement != null)
		{
			iconSourceElement.IconSource = iconSource ?? NullIcon;
		}
	}

	private static void SetAccessibilityViewForSubtree(DependencyObject element, AccessibilityView accessibilityView)
	{
		AutomationProperties.SetAccessibilityView(element, accessibilityView);
		int childrenCount = VisualTreeHelper.GetChildrenCount(element);
		for (int i = 0; i < childrenCount; i++)
		{
			SetAccessibilityViewForSubtree(VisualTreeHelper.GetChild(element, i), accessibilityView);
		}
	}

	private void OnIconChanged()
	{
		TryUpdateIcon();
		RequestIconAccessibilityViewUpdate();
	}

	private void OnBadgeIconChanged()
	{
		TryUpdateBadgeIcon();
	}

	private void TryUpdateIcon()
	{
		if (PartIcon == null)
		{
			DependencyObject templateChild = GetTemplateChild("PartIcon");
			IconSourceElement val = (PartIcon = (IconSourceElement?)(object)((templateChild is IconSourceElement) ? templateChild : null));
		}
		TryUpdateIconSourceElement(PartIcon, Icon);
		TryUpdateContainerGrid();
		if (PartIcon != null)
		{
			bool flag = Icon != (IconSource)null;
			((FrameworkElement)PartIcon).Height = (flag ? 20.0 : 0.0);
			((FrameworkElement)PartIcon).Width = (flag ? 20.0 : 0.0);
		}
	}

	private void OnPartIconLayoutUpdated(object? sender, object e)
	{
		IconSourceElement val = (IconSourceElement)((sender is IconSourceElement) ? sender : null);
		if (val != null)
		{
			((FrameworkElement)val).LayoutUpdated -= OnPartIconLayoutUpdated;
		}
		TryUpdateIconAccessibilityView();
	}

	private void RequestIconAccessibilityViewUpdate()
	{
		TryUpdateIconAccessibilityView();
		if (PartIcon != null)
		{
			((FrameworkElement)PartIcon).LayoutUpdated -= OnPartIconLayoutUpdated;
			((FrameworkElement)PartIcon).LayoutUpdated += OnPartIconLayoutUpdated;
		}
	}

	private void TryUpdateIconAccessibilityView()
	{
		if (PartIcon != null)
		{
			SetAccessibilityViewForSubtree((DependencyObject)(object)PartIcon, IconAccessibilityView);
		}
	}

	private void TryUpdateBadgeIcon()
	{
		if (PartBadgeIcon == null)
		{
			DependencyObject templateChild = GetTemplateChild("PartBadgeIcon");
			IconSourceElement val = (PartBadgeIcon = (IconSourceElement?)(object)((templateChild is IconSourceElement) ? templateChild : null));
		}
		TryUpdateIconSourceElement(PartBadgeIcon, BadgeIcon);
		TryUpdateContainerGrid();
		if (PartBadgeIcon != null)
		{
			bool flag = BadgeIcon != (IconSource)null;
			((FrameworkElement)PartBadgeIcon).Height = (flag ? 16.0 : 0.0);
			((FrameworkElement)PartBadgeIcon).Width = (flag ? 16.0 : 0.0);
			((FrameworkElement)PartBadgeIcon).Margin = (flag ? new Thickness(0.0, 0.0, 4.0, 4.0) : new Thickness(0.0));
		}
	}

	private void TryUpdateContainerGrid()
	{
		if (PartContainerGrid == null)
		{
			DependencyObject templateChild = GetTemplateChild("PartContainerGrid");
			Grid val = (PartContainerGrid = (Grid?)(object)((templateChild is Grid) ? templateChild : null));
		}
		if (PartContainerGrid != null)
		{
			if (Icon != (IconSource)null)
			{
				bool flag = BadgeIcon != (IconSource)null;
				((FrameworkElement)PartContainerGrid).Width = (flag ? 30.0 : 20.0);
			}
			else
			{
				((FrameworkElement)PartContainerGrid).Width = 0.0;
			}
		}
	}
}
