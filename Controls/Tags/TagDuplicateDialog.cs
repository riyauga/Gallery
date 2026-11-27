using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace Gallery.Controls.Tags;

/// <summary>
/// 自动检测重复标签的处理弹窗。条件之间是「且」：勾两个就必须同时满足才成组，
/// 所以条件越多结果越严。同分类的组可以直接合并（保留选中的那个，另一个连图片、别名、显示名一起并过来）；
/// 分类不同的组不允许合并（跨分类合并会改掉图片的标签语义），只提供「保留一个、删掉其余」。
/// </summary>
public sealed class TagDuplicateDialog : ContentDialog
{
    private sealed record Condition(string Key, string Label);

    private static readonly Condition[] Conditions =
    [
        new("display", "显示名相同"),
        new("identity", "同身份"),
        new("alias", "别名互命"),
        new("contains", "名称互相包含"),
    ];

    private readonly StackPanel _groups = new() { Spacing = 12 };
    private readonly TextBlock _status = new() { Opacity = .7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.Ordinal);
    private bool _busy;

    public bool Changed { get; private set; }

    public TagDuplicateDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "重复标签";
        CloseButtonText = "关闭";
        DefaultButton = ContentDialogButton.None;
        Content = BuildContent();

        foreach (var condition in Conditions)
            _checks[condition.Key].IsChecked = condition.Key == "display";
        Recheck();
    }

    private UIElement BuildContent()
    {
        var columns = XamlRoot.Size.Width < 760 ? 2 : 4;
        var bar = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        for (var column = 0; column < columns; column++) bar.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < (Conditions.Length + columns - 1) / columns; row++) bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var condition in Conditions)
        {
            var check = new CheckBox { Content = condition.Label, MinWidth = 0 };
            check.Checked += (_, _) => Recheck();
            check.Unchecked += (_, _) => Recheck();
            _checks[condition.Key] = check;
            Grid.SetColumn(check, bar.Children.Count % columns);
            Grid.SetRow(check, bar.Children.Count / columns);
            bar.Children.Add(check);
        }

        return new StackPanel
        {
            Spacing = 12,
            Width = Math.Max(280, Math.Min(620, XamlRoot.Size.Width - 112)),
            Children =
            {
                new TextBlock
                {
                    Text = "勾选的条件需要同时满足才算重复。合并会保留关联图片最多的那个。",
                    TextWrapping = TextWrapping.Wrap, Opacity = .72,
                },
                bar,
                _status,
                new ScrollViewer
                {
                    MaxHeight = Math.Max(160, Math.Min(420, XamlRoot.Size.Height - 300)),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(0, 0, 16, 0),
                    Content = _groups,
                },
            },
        };
    }

    private void Recheck()
    {
        var conditions = _checks.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToArray();
        _groups.Children.Clear();
        if (conditions.Length == 0)
        {
            _status.Text = "至少勾一个条件";
            return;
        }
        List<TagManageService.DuplicateGroup> found;
        try { found = TagManageService.FindDuplicates(conditions); }
        catch (Exception ex)
        {
            _status.Text = "检测失败：" + ex.Message;
            return;
        }
        _status.Text = found.Count == 0 ? "没有发现重复" : $"发现 {found.Count} 组重复";
        for (var i = 0; i < found.Count; i++)
            _groups.Children.Add(BuildGroup(found[i], i));
    }

    private UIElement BuildGroup(TagManageService.DuplicateGroup group, int index)
    {
        var members = new StackPanel { Spacing = 2 };
        var groupName = "dup-" + index;
        var radios = new List<RadioButton>();
        for (var i = 0; i < group.Members.Count; i++)
        {
            var member = group.Members[i];
            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var values = new[] { TagManageService.Label(member.Category), member.Name, member.Zh.Length > 0 ? member.Zh : "—", $"{member.Usage} 张" };
            for (var column = 0; column < values.Length; column++)
            {
                var text = new TextBlock { Text = values[column], TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center, Opacity = column == 1 ? 1 : .72, FontSize = column == 1 ? 14 : 12 };
                ToolTipService.SetToolTip(text, values[column]);
                Grid.SetColumn(text, column);
                content.Children.Add(text);
            }
            var radio = new RadioButton
            {
                GroupName = groupName,
                IsChecked = i == 0,
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = content,
            };
            radio.Tag = member;
            radios.Add(radio);
            members.Children.Add(radio);
        }

        var sameCategory = group.Members.Select(m => m.Category).Distinct(StringComparer.Ordinal).Count() == 1;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var confirmHost = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };

        if (sameCategory)
        {
            var merge = new Button { Content = "合并" };
            merge.Click += (_, _) => ShowMergeConfirm(group, radios, confirmHost);
            actions.Children.Add(merge);
        }
        var prune = new Button { Content = "保留选中，删除其余" };
        prune.Click += (_, _) => ShowPruneConfirm(radios, confirmHost);
        actions.Children.Add(prune);

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"),
            Background = Brush("CardBackgroundFillColorDefaultBrush"),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"组 {index + 1} · {group.Members.Sum(m => m.Usage)} 张 · 命中：{group.Reason}" +
                               (sameCategory ? "" : " · 分类不同，只能保留一个"),
                        FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap,
                    },
                    members,
                    actions,
                    confirmHost,
                },
            },
        };
    }

    private void ShowMergeConfirm(TagManageService.DuplicateGroup group, List<RadioButton> radios,
        StackPanel host)
    {
        var winner = radios.FirstOrDefault(r => r.IsChecked == true)?.Tag as TagManageService.TagRow;
        if (winner is null) return;
        var losers = group.Members.Where(m => m.Id != winner.Id).ToArray();
        host.Children.Clear();
        host.Visibility = Visibility.Visible;
        host.Children.Add(new TextBlock
        {
            Text = $"将把 {string.Join("、", losers.Select(l => l.Name))} 并入 {winner.Name}，并删除被并入的标签。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .85,
        });
        var preview = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75, IsTextSelectionEnabled = true,
        };
        host.Children.Add(preview);
        var confirm = new Button { Content = "确认合并", IsEnabled = false };
        host.Children.Add(confirm);

        _ = Task.Run(() =>
        {
            try
            {
                var report = RunPlan(winner, losers, apply: false);
                DispatcherQueue.TryEnqueue(() =>
                {
                    preview.Text = report;
                    confirm.IsEnabled = true;
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => preview.Text = "预演失败：" + ex.Message);
            }
        });

        confirm.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            confirm.IsEnabled = false;
            try
            {
                await Task.Run(() => RunPlan(winner, losers, apply: true));
                Changed = true;
                AppService.NotifyPackRemoved();
                Recheck();
            }
            catch (Exception ex)
            {
                preview.Text = "合并失败：" + ex.Message;
                confirm.IsEnabled = true;
            }
            finally { _busy = false; }
        };
    }

    private void ShowPruneConfirm(List<RadioButton> radios, StackPanel host)
    {
        var keep = radios.FirstOrDefault(r => r.IsChecked == true)?.Tag as TagManageService.TagRow;
        if (keep is null) return;
        var others = radios.Select(r => r.Tag).OfType<TagManageService.TagRow>()
            .Where(m => m.Id != keep.Id).ToArray();
        host.Children.Clear();
        host.Visibility = Visibility.Visible;
        host.Children.Add(new TextBlock
        {
            Text = $"将删除 {others.Length} 个标签及其图片关联，只保留 {keep.Name}。图片本身不会删除。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .85,
        });
        var confirm = new Button { Content = "确认删除" };
        confirm.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            confirm.IsEnabled = false;
            try
            {
                if (AppService.Store is not { } store) return;
                await Task.Run(() => TagManageService.Delete(store, others.Select(o => o.Id).ToArray()));
                Changed = true;
                AppService.NotifyPackRemoved();
                Recheck();
            }
            catch (Exception ex)
            {
                host.Children.Add(new TextBlock { Text = "删除失败：" + ex.Message, Foreground = Brush("SystemFillColorCriticalBrush") });
                confirm.IsEnabled = true;
            }
            finally { _busy = false; }
        };
        host.Children.Add(confirm);
    }

    private static string RunPlan(TagManageService.TagRow winner, TagManageService.TagRow[] losers, bool apply)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "tag-merge-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var store = new StoreService(AppPaths.DbPath);
            return TagMergeService.Run(store, WritePlan(path, winner, losers), apply);
        }
        finally { System.IO.File.Delete(path); }
    }

    /// <summary>合并服务走的还是命令行那份 JSON 计划，这里把界面上选的保留项翻译成计划。</summary>
    private static string WritePlan(string path, TagManageService.TagRow winner, TagManageService.TagRow[] losers)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var plan = losers.Select(l => new
        {
            loser = new[] { l.Category, l.Name },
            winner = new[] { winner.Category, winner.Name },
            zh = winner.Zh,
        }).ToArray();
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(plan));
        return path;
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
