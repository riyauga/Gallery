using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls.Tags;

/// <summary>
/// 标签管理里点一行打开的详情弹窗：显示基本信息，可改显示名、改分类、增删别名、删除该标签。
/// 内部名（tags.name）只读——它是识别结果与身份对齐的键，改了会断掉已有图片的对应关系。
/// 写操作在这里直接落库（后台线程），调用方按 <see cref="Outcome"/> 刷新列表即可。
/// </summary>
public sealed class TagManageDialog : ContentDialog
{
    public sealed record Outcome(bool Deleted, int Id, string Category, string Zh, string En);

    public Outcome? Result { get; private set; }

    private readonly TagManageService.TagDetail _detail;
    private readonly ComboBox _category = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _zh = new() { Header = "显示名（中）", PlaceholderText = "留空则只显示内部名" };
    private readonly TextBox _en = new() { Header = "显示名（英）" };
    private readonly TextBox _newAlias = new() { PlaceholderText = "输入别名后回车" };
    private readonly StackPanel _aliasHost = new() { Spacing = 4 };
    private readonly StackPanel _deleteConfirm = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly List<string> _aliases;

    public TagManageDialog(XamlRoot root, TagManageService.TagDetail detail)
    {
        XamlRoot = root;
        _detail = detail;
        _aliases = [.. detail.Aliases];

        Title = "标签";
        PrimaryButtonText = "保存";
        CloseButtonText = "取消";
        SecondaryButtonText = "删除";
        DefaultButton = ContentDialogButton.Primary;
        var content = BuildContent();
        var scroll = new ScrollViewer
        {
            Content = content, Padding = new Thickness(0, 0, 16, 0),
            MaxHeight = Math.Max(180, Math.Min(540, root.Size.Height - 220)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var bar = new ContinuousScrollBar { Step = 0, HorizontalAlignment = HorizontalAlignment.Right };
        Content = new Grid { Children = { scroll, bar } };
        Opened += (_, _) => bar.BindVerticalViewport(scroll);

        PrimaryButtonClick += OnSave;
        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            _deleteConfirm.Visibility = Visibility.Visible;
        };
        RefreshAliases();
    }

    private UIElement BuildContent()
    {
        foreach (var (key, label) in TagManageService.AllCategories)
        {
            // 只给人工可维护的分类；分辨率与分级是识别流程写的，已有标签照原样显示但不让改。
            if (!TagManageService.ManualCategories.Contains(key) && key != _detail.Category) continue;
            _category.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        }
        foreach (var item in _category.Items.OfType<ComboBoxItem>())
            if ((string)item.Tag == _detail.Category) _category.SelectedItem = item;
        if (_category.SelectedIndex < 0)
        {
            var frozen = new ComboBoxItem
            {
                Content = TagManageService.Label(_detail.Category) + "（识别流程写入，不在此修改）",
                Tag = _detail.Category,
            };
            _category.Items.Add(frozen);
            _category.SelectedItem = frozen;
            _category.IsEnabled = false;
        }
        _category.Header = "分类";
        _zh.Text = _detail.Zh;
        _en.Text = _detail.En;

        _newAlias.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            AddAlias(_newAlias.Text);
        };

        var aliasBox = new Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("ControlStrokeColorDefaultBrush"),
            Background = Brush("ControlFillColorDefaultBrush"),
            Child = _aliasHost,
        };

        _deleteConfirm.Children.Add(new TextBlock
        {
            Text = "删除后这个标签会从所有图片上移除，图片本身不动。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = .8,
        });
        var confirmDelete = new Button { Content = "确定删除" };
        confirmDelete.Click += OnDelete;
        _deleteConfirm.Children.Add(confirmDelete);

        var facts = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Row("内部名", _detail.Name, mono: true),
                Row("关联图片", $"{_detail.Usage} 张"),
                Row("身份键", _detail.Identity.Length == 0 ? "未对齐" : _detail.Identity, mono: true),
            },
        };

        return new StackPanel
        {
            Spacing = 12,
            Width = Math.Max(280, Math.Min(440, XamlRoot.Size.Width - 112)),
            Children =
            {
                facts,
                new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush") },
                _category,
                _zh,
                _en,
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "别名", Opacity = .75, FontSize = 12 },
                        aliasBox,
                        _newAlias,
                    },
                },
                _deleteConfirm,
                _error,
            },
        };
    }

    private static Grid Row(string label, string value, bool mono = false)
    {
        var text = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (mono) text.FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI");
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, Opacity = .65, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private void RefreshAliases()
    {
        _aliasHost.Children.Clear();
        if (_aliases.Count == 0)
        {
            _aliasHost.Children.Add(new TextBlock { Text = "暂无别名", Opacity = .5, FontSize = 12 });
            return;
        }
        foreach (var alias in _aliases.ToArray())
        {
            var text = new TextBlock { Text = alias, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var remove = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 11 },
                Padding = new Thickness(6, 2, 6, 2),
                MinWidth = 0,
                MinHeight = 0,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(remove, $"移除别名 {alias}");
            remove.Click += (_, _) =>
            {
                _aliases.Remove(alias);
                RefreshAliases();
            };
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(text);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _aliasHost.Children.Add(row);
        }
    }

    private void AddAlias(string raw)
    {
        var alias = raw.Trim();
        if (alias.Length == 0) return;
        if (_aliases.Any(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)))
        {
            _newAlias.Text = "";
            return;
        }
        _aliases.Add(alias);
        _newAlias.Text = "";
        RefreshAliases();
    }

    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        try
        {
            if (AppService.Store is not { } store) return;
            var category = (_category.SelectedItem as ComboBoxItem)?.Tag as string ?? _detail.Category;
            var zh = _zh.Text.Trim();
            var en = _en.Text.Trim();
            AddAlias(_newAlias.Text);
            var aliases = _aliases.ToArray();
            await Task.Run(() =>
            {
                TagManageService.Update(store, _detail.Id, category, zh, en);
                TagManageService.SetAliases(store, _detail.Id, aliases);
            });
            Result = new(false, _detail.Id, category, zh, en);
            args.Cancel = false;
        }
        catch (Exception ex)
        {
            AppService.DebugLog("tag-manage-save failed: " + ex);
            ShowError("保存失败：" + ex.Message);
        }
        finally { deferral.Complete(); }
    }

    private async void OnDelete(object sender, RoutedEventArgs args)
    {
        try
        {
            if (AppService.Store is not { } store) return;
            await Task.Run(() => TagManageService.Delete(store, [_detail.Id]));
            Result = new(true, _detail.Id, _detail.Category, _detail.Zh, _detail.En);
            AppService.NotifyPackRemoved();
            Hide();
        }
        catch (Exception ex)
        {
            AppService.DebugLog("tag-manage-delete failed: " + ex);
            ShowError("删除失败：" + ex.Message);
        }
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Foreground = Brush("SystemFillColorCriticalBrush");
        _error.Visibility = Visibility.Visible;
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
