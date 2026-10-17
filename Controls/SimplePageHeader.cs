// Source: PhotosManager.Views.Controls/SimplePageHeader.cs; namespace/platform projection adapted only.
// 原版源码未启用 nullable 标注；反编译产物保留 null 语义，故本文件关闭 nullable 警告，不做逐行改写。
#nullable disable warnings
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;



namespace Gallery.Controls;

[TemplatePart(Name = "PartTitleTextBlock", Type = typeof(TextBlock))]
[TemplatePart(Name = "PartIconWithBadge", Type = typeof(IconWithBadge))]
[TemplatePart(Name = "PartIconWithBadgeGrid", Type = typeof(Grid))]
[TemplatePart(Name = "PartTitleTooltip", Type = typeof(ToolTip))]
public sealed class SimplePageHeader : Control
{
	public static readonly DependencyProperty TitleProperty = DependencyProperty.Register("Title", typeof(string), typeof(SimplePageHeader), new PropertyMetadata((object)string.Empty, (PropertyChangedCallback)((DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		((SimplePageHeader)(object)d).OnTitleChanged();
	})));

	public static readonly DependencyProperty TooltipTextProperty = DependencyProperty.Register("TooltipText", typeof(string), typeof(SimplePageHeader), new PropertyMetadata((object)string.Empty, (PropertyChangedCallback)((DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		((SimplePageHeader)(object)d).OnTooltipTextChanged();
	})));

	public static readonly DependencyProperty IconProperty = DependencyProperty.Register("Icon", typeof(IconSource), typeof(SimplePageHeader), new PropertyMetadata((object)null, (PropertyChangedCallback)((DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		((SimplePageHeader)(object)d).OnIconChanged();
	})));

	public static readonly DependencyProperty BadgeIconProperty = DependencyProperty.Register("BadgeIcon", typeof(IconSource), typeof(SimplePageHeader), new PropertyMetadata((object)null, (PropertyChangedCallback)((DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		((SimplePageHeader)(object)d).OnBadgeIconChanged();
	})));

	public static readonly DependencyProperty IconAccessibilityViewProperty = DependencyProperty.Register("IconAccessibilityView", typeof(AccessibilityView), typeof(SimplePageHeader), new PropertyMetadata((object)(AccessibilityView)0));

	private const double IconHeight = 40.0;

	private const double IconRightMargin = 10.0;

	public string Title
	{
		get
		{
			return (string)((DependencyObject)this).GetValue(TitleProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(TitleProperty, (object)value);
		}
	}

	public string TooltipText
	{
		get
		{
			return (string)((DependencyObject)this).GetValue(TooltipTextProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(TooltipTextProperty, (object)value);
		}
	}

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

	private TextBlock? PartTitleTextBlock { get; set; }

	private IconWithBadge? PartIconWithBadge { get; set; }

	private Grid? PartIconWithBadgeGrid { get; set; }

	private ToolTip? PartTitleTooltip { get; set; }

	public SimplePageHeader()
	{
		DefaultStyleKey = typeof(SimplePageHeader);
		((UIElement)this).IsTabStop = false;
	}

	protected override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		PartTitleTextBlock = null;
		PartIconWithBadge = null;
		PartIconWithBadgeGrid = null;
		PartTitleTooltip = null;
		TryUpdateTitle();
		TryUpdateToolTip();
		TryUpdateIcon();
		TryUpdateBadgeIcon();
	}

	private void OnTitleChanged()
	{
		TryUpdateTitle();
		TryUpdateToolTip();
	}

	private void OnTooltipTextChanged()
	{
		TryUpdateToolTip();
	}

	private void OnIconChanged()
	{
		TryUpdateIcon();
	}

	private void OnBadgeIconChanged()
	{
		TryUpdateBadgeIcon();
	}

	private void TryUpdateTitle()
	{
		if (PartTitleTextBlock == null)
		{
			DependencyObject templateChild = GetTemplateChild("PartTitleTextBlock");
			TextBlock val = (PartTitleTextBlock = (TextBlock?)(object)((templateChild is TextBlock) ? templateChild : null));
		}
		if (PartTitleTextBlock != null)
		{
			PartTitleTextBlock.Text = Title;
		}
	}

	private void TryUpdateIcon()
	{
		if (PartIconWithBadge == null)
		{
			IconWithBadge iconWithBadge = (PartIconWithBadge = GetTemplateChild("PartIconWithBadge") as IconWithBadge);
		}
		if (PartIconWithBadge != null)
		{
			PartIconWithBadge.Icon = Icon;
		}
		if (PartIconWithBadgeGrid == null)
		{
			DependencyObject templateChild = GetTemplateChild("PartIconWithBadgeGrid");
			Grid val = (PartIconWithBadgeGrid = (Grid?)(object)((templateChild is Grid) ? templateChild : null));
		}
		if (PartIconWithBadgeGrid != null)
		{
			((FrameworkElement)PartIconWithBadgeGrid).Height = ((Icon != (IconSource)null) ? 40.0 : 0.0);
			((FrameworkElement)PartIconWithBadgeGrid).Margin = ((Icon != (IconSource)null) ? new Thickness(0.0, 0.0, 10.0, 0.0) : new Thickness(0.0));
		}
	}

	private void TryUpdateBadgeIcon()
	{
		if (PartIconWithBadge == null)
		{
			IconWithBadge iconWithBadge = (PartIconWithBadge = GetTemplateChild("PartIconWithBadge") as IconWithBadge);
		}
		if (PartIconWithBadge != null)
		{
			PartIconWithBadge.BadgeIcon = BadgeIcon;
		}
	}

	private void TryUpdateToolTip()
	{
		if (PartTitleTooltip == null)
		{
			DependencyObject templateChild = GetTemplateChild("PartTitleTooltip");
			ToolTip val = (PartTitleTooltip = (ToolTip?)(object)((templateChild is ToolTip) ? templateChild : null));
		}
		if (PartTitleTooltip != null)
		{
			((ContentControl)PartTitleTooltip).Content = (string.IsNullOrEmpty(TooltipText) ? Title : TooltipText);
		}
	}
}
