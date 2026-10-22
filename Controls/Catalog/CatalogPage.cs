using System;
using System.Collections.Generic;
using System.Linq;
using Gallery.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls.Catalog;

/// <summary>
/// 资料库页（对应 galleryold 的词库 / 资料库）：左边列出词库实体，右边编辑中文名、英文名
/// 与别名。保存写 catalog_entities，并把同名标签接进 tag_profiles（人工资料优先，词库更新不覆盖）。
/// </summary>
public sealed class CatalogPage : Grid
{
    private readonly TagCatalogService _catalog;
    private readonly SearchInputBox _search = new();
    private readonly ListView _list = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _status = new();
    private readonly TextBox _key = new();
    private readonly TextBox _zh = new();
    private readonly TextBox _en = new();
    private readonly TextBox _aliases = new();
    private readonly TextBlock _meta = new();
    private readonly TextBlock _alignStatus = new();
    private DispatcherQueueTimer? _debounce;
    private string _selectedKey = "";

    public CatalogPage(TagCatalogService catalog)
    {
        _catalog = catalog;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Children.Add(BuildHeader());

        var body = new Grid
        {
            Margin = new Thickness(32, 8, 32, 24),
            ColumnSpacing = 12,
        };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        body.Children.Add(BuildListPane());
        var editor = BuildEditorPane();
        Grid.SetColumn(editor, 1);
        body.Children.Add(editor);

        Grid.SetRow(body, 1);
        Children.Add(body);

        Loaded += (_, _) => Reload();
    }

    private FrameworkElement BuildHeader()
    {
        var panel = new Grid
        {
            Padding = new Thickness(32, 24, 32, 8),
            ColumnSpacing = 12,
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "资料库",
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(title);

        _summary.FontSize = 12;
        _summary.Opacity = 0.66;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Margin = new Thickness(12, 6, 0, 0);
        Grid.SetColumn(_summary, 1);
        panel.Children.Add(_summary);

        var alignRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ambiguousButton = new Button { Content = "待消歧标签", MinWidth = 108 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ambiguousButton, "待消歧标签");
        ambiguousButton.Click += async (_, _) => await OpenAmbiguous();
        alignRow.Children.Add(ambiguousButton);

        var alignButton = new Button { Content = "自动对齐标签", MinWidth = 108 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(alignButton, "自动对齐标签");
        alignButton.Click += (_, _) => AlignTags();
        alignRow.Children.Add(alignButton);
        _alignStatus.FontSize = 12;
        _alignStatus.Opacity = 0.7;
        _alignStatus.VerticalAlignment = VerticalAlignment.Center;
        alignRow.Children.Add(_alignStatus);

        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(alignRow, 2);
        panel.Children.Add(alignRow);

        Grid.SetRow(panel, 0);
        return panel;
    }

    private FrameworkElement BuildListPane()
    {
        var pane = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 12, 12, 8),
        };

        // 搜索行按内容测量，列表仅占剩余视口；StackPanel 会向列表提供无限高度。
        var stack = new Grid { RowSpacing = 8 };
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _search.PlaceholderText = "搜索角色 / 作品 / 备选名";
        _search.TextChanged += (_, _) => QueueReload();
        stack.Children.Add(_search);

        _list.SelectionMode = ListViewSelectionMode.Single;
        _list.IsItemClickEnabled = false;
        _list.ItemTemplate = RowTemplate();
        _list.SelectionChanged += (_, _) => ShowSelection();
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(_list, ScrollMode.Disabled);
        // 浮层滚动条会盖住条目右列的「次数」，条目整体右缩 16（UI_CONVENTIONS.md R5）。
        _list.Padding = new Thickness(0, 0, 16, 0);
        Grid.SetRow(_list, 1);
        stack.Children.Add(_list);

        pane.Child = stack;
        return pane;
    }

    private FrameworkElement BuildEditorPane()
    {
        var pane = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
        };

        var stack = new StackPanel { Spacing = 12 };

        _key.Header = "身份键（只读）";
        _key.IsReadOnly = true;
        stack.Children.Add(_key);

        _zh.Header = "中文名";
        _zh.PlaceholderText = "例如：刻晴";
        stack.Children.Add(_zh);

        _en.Header = "英文名";
        _en.PlaceholderText = "例如：Keqing";
        stack.Children.Add(_en);

        _aliases.Header = "别名（用 、或逗号分隔）";
        _aliases.PlaceholderText = "例如：刻晴、Keqing、玉衡星";
        _aliases.AcceptsReturn = true;
        _aliases.TextWrapping = TextWrapping.Wrap;
        _aliases.Height = 72;
        stack.Children.Add(_aliases);

        _meta.FontSize = 12;
        _meta.Opacity = 0.66;
        stack.Children.Add(_meta);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var save = new Button
        {
            Content = "保存",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 96,
        };
        save.Click += (_, _) => Save();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(save, "保存资料库条目");
        buttons.Children.Add(save);

        _status.FontSize = 12;
        _status.Opacity = 0.7;
        _status.VerticalAlignment = VerticalAlignment.Center;
        buttons.Children.Add(_status);
        stack.Children.Add(buttons);

        var hint = new TextBlock
        {
            Text = "保存后，名称与别名会用于标签展示；同名标签会接到这个身份上（人工资料优先于词库）。",
            FontSize = 12,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        stack.Children.Add(hint);

        pane.Child = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // 父 Border 的 16 在滚动区外面，挡不住浮层滚动条，滚动区内部再留一次（UI_CONVENTIONS.md R2）。
            Padding = new Thickness(0, 0, 16, 0),
        };
        return pane;
    }

    private static DataTemplate RowTemplate() => (DataTemplate)XamlReader.Load(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid Padding="0,6" ColumnSpacing="12">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="*"/>
              <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <StackPanel Spacing="2">
              <TextBlock Text="{Binding Title}" FontSize="14" TextTrimming="CharacterEllipsis"/>
              <TextBlock Text="{Binding Subtitle}" FontSize="12" Opacity="0.66" TextTrimming="CharacterEllipsis"/>
            </StackPanel>
            <TextBlock Grid.Column="1" Text="{Binding UsageText}" FontSize="12" Opacity="0.55"
                       VerticalAlignment="Center"/>
          </Grid>
        </DataTemplate>
        """);

    /// <summary>列表行视图模型（XAML 反射绑定需要公开属性）。</summary>
    public sealed record RowView(string Title, string Subtitle, string UsageText,
        TagCatalogService.CatalogRow Row);

    private void QueueReload()
    {
        if (_debounce is null)
        {
            _debounce = DispatcherQueue.CreateTimer();
            _debounce.Interval = TimeSpan.FromMilliseconds(220);
            _debounce.IsRepeating = false;
            _debounce.Tick += (_, _) => Reload();
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private void Reload()
    {
        List<TagCatalogService.CatalogRow> rows;
        List<(string Kind, int Count)> kinds;
        var total = 0;
        try
        {
            rows = _catalog.Browse(_search.Text, out kinds, out total);
        }
        catch (Exception ex)
        {
            _status.Text = "资料库读取失败：" + ex.Message;
            return;
        }

        var views = rows.Select(r => new RowView(
            Display(r),
            Subtitle(r),
            r.Usage > 0 ? $"已用 {r.Usage}" : "未关联",
            r)).ToList();
        _list.ItemsSource = views;

        var linked = rows.Count(r => r.TagId > 0);
        _summary.Text = $"共 {total} 条身份 · 显示 {views.Count} · 已关联标签 {linked} · 类型 "
                        + (kinds.Count == 0 ? "—" : string.Join(" / ", kinds.Take(4).Select(k => $"{k.Kind} {k.Count}")));

        if (_selectedKey.Length > 0)
        {
            var again = views.FirstOrDefault(v => v.Row.EntityKey == _selectedKey);
            if (again is not null) _list.SelectedItem = again;
        }
    }

    private static string Display(TagCatalogService.CatalogRow r)
    {
        if (r.Zh.Length > 0) return r.Zh;
        if (r.En.Length > 0) return r.En;
        return r.EntityKey;
    }

    private static string Subtitle(TagCatalogService.CatalogRow r)
    {
        var parts = new List<string>();
        if (r.Zh.Length > 0 && r.En.Length > 0) parts.Add(r.En);
        if (r.Kind.Length > 0) parts.Add(r.Kind);
        if (r.TagName.Length > 0 && !string.Equals(r.TagName, Display(r), StringComparison.OrdinalIgnoreCase))
            parts.Add("标签 " + r.TagName);
        if (r.GameKey.Length > 0) parts.Add(r.GameKey);
        return string.Join(" · ", parts);
    }

    private void ShowSelection()
    {
        if (_list.SelectedItem is not RowView view) return;
        var row = view.Row;
        _selectedKey = row.EntityKey;
        _key.Text = row.EntityKey;
        _zh.Text = row.Zh;
        _en.Text = row.En;
        _aliases.Text = row.Aliases.Replace("、", "、");
        _meta.Text = row.TagId > 0
            ? $"关联标签：{row.TagName}（已用 {row.Usage} 张）"
            : "尚未关联到任何标签；保存后会把同名标签接上。";
    }

    private async System.Threading.Tasks.Task OpenAmbiguous()
    {
        _alignStatus.Text = "";
        List<TagCatalogService.AmbiguousTag> pending;
        try
        {
            pending = _catalog.AmbiguousTags();
        }
        catch (Exception ex)
        {
            _alignStatus.Text = "读取失败：" + ex.Message;
            return;
        }

        var dialog = new AlignAmbiguousDialog(_catalog, pending);
        await dialog.ShowAsync(XamlRoot);
        Reload();
    }

    private void AlignTags()
    {
        _alignStatus.Text = "对齐中…";
        try
        {
            var report = _catalog.AlignTags();
            _alignStatus.Text = $"新关联 {report.Matched} · 已有 {report.Existing} · 同名多实体 {report.Ambiguous} · 作品冲突 {report.Conflicting} · 无对应 {report.Unmatched} · 外部/原创 {report.External} · 人工/已解析 {report.Manual}";
            Reload();
        }
        catch (Exception ex)
        {
            _alignStatus.Text = "对齐失败：" + ex.Message;
        }
    }

    private void Save()
    {
        _status.Text = "";
        if (_selectedKey.Length == 0)
        {
            _status.Text = "先选一条身份";
            return;
        }

        var aliases = _aliases.Text
            .Split(['、', ',', '，', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .ToList();

        try
        {
            var synced = _catalog.UpdateEntity(_selectedKey, _zh.Text, _en.Text, aliases);
            _status.Text = synced > 0 ? $"已保存 · 同步 {synced} 个标签" : "已保存";
            Reload();
        }
        catch (Exception ex)
        {
            _status.Text = "保存失败：" + ex.Message;
        }
    }
}
