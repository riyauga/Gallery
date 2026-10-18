using System;
using System.Collections.Generic;
using System.Linq;
using Gallery.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls.Catalog;

/// <summary>
/// 歧义标签消歧：同名实体有多个时，让人挑一个身份（或跳过）。点候选即写 tag_profiles，
/// 不做任何猜测。
/// </summary>
public sealed class AlignAmbiguousDialog
{
    private readonly TagCatalogService _catalog;
    private readonly List<TagCatalogService.AmbiguousTag> _pending;
    private readonly TextBlock _progress = new();
    private readonly StackPanel _list = new();
    private int _resolved;

    public AlignAmbiguousDialog(TagCatalogService catalog, List<TagCatalogService.AmbiguousTag> pending)
    {
        _catalog = catalog;
        _pending = pending;
    }

    public async System.Threading.Tasks.Task ShowAsync(XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "消歧：同名多个身份",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };

        var body = new StackPanel { Spacing = 10, Width = 560 };

        _progress.FontSize = 12;
        _progress.Opacity = 0.7;
        _progress.Text = $"共 {_pending.Count} 个标签待消歧";
        body.Children.Add(_progress);

        var scroll = new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        };
        body.Children.Add(scroll);

        if (_pending.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "没有需要消歧的标签。",
                FontSize = 13,
                Opacity = 0.7,
            });
        }

        foreach (var tag in _pending) _list.Children.Add(BuildRow(tag));

        dialog.Content = body;
        await dialog.ShowAsync();
    }

    private UIElement BuildRow(TagCatalogService.AmbiguousTag tag)
    {
        var row = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = tag.TagName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var state = new TextBlock
        {
            Text = $"{tag.Candidates.Count} 个候选",
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        head.Children.Add(state);
        row.Children.Add(head);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var wrap = new StackPanel { Spacing = 6 };
        foreach (var candidate in tag.Candidates)
        {
            var label = string.IsNullOrEmpty(candidate.Zh)
                ? (string.IsNullOrEmpty(candidate.En) ? candidate.Key : candidate.En)
                : candidate.Zh;
            if (!string.IsNullOrEmpty(candidate.Game)) label += " · " + candidate.Game;

            var button = new Button
            {
                Content = label,
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4),
                Tag = candidate.Key,
            };
            AutomationProperties.SetName(button, "消歧 " + tag.TagName + " → " + label);
            button.Click += (sender, _) =>
            {
                var key = (string)((Button)sender).Tag;
                try
                {
                    _catalog.LinkTagToEntity(tag.TagId, key);
                    _resolved++;
                    _progress.Text = $"共 {_pending.Count} 个标签待消歧 · 已处理 {_resolved}";
                    chips.IsHitTestVisible = false;
                    chips.Opacity = 0.4;
                    state.Text = "已关联 → " + label;
                    state.Opacity = 0.9;
                }
                catch (Exception ex)
                {
                    state.Text = "失败：" + ex.Message;
                }
            };
            chips.Children.Add(button);
        }

        wrap.Children.Add(chips);
        row.Children.Add(wrap);
        return row;
    }
}
