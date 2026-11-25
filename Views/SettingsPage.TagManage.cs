using Gallery.Controls;
using Gallery.Controls.Catalog;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Gallery.Views;

/// <summary>设置页「标签管理」面板：查看/搜索、改显示名、多选批量删除、合并。</summary>
public sealed partial class SettingsPage
{
    private readonly LocalSelectionController _tagManageSelection = new();
    private SearchInputBox? _tagManageSearch;
    private ListView? _tagManageList;
    private TextBlock? _tagManageStatus;
    private Button? _tagManageRename;
    private Button? _tagManageDelete;
    private Button? _tagManageMerge;
    private Button? _tagManageSelect;
    private List<TagManageService.TagRow> _tagManageRows = [];
    private TagManageService.TagRow[] _tagManageVisible = [];

    private void EnsureTagManageUI()
    {
        if (_tagManageList is not null) return;

        var search = _tagManageSearch = new SearchInputBox
        {
            PlaceholderText = "搜索标签名、显示名或身份", Height = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        search.TextChanged += (_, _) => { _tagManageSelection.Exit(); ApplyTagManageFilter(); };
        Grid.SetRow(search, 0);
        TagManageHost.Children.Add(search);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _tagManageRename = new Button { Content = "改名" };
        _tagManageRename.Click += (_, _) => _ = RenameTagAsync();
        _tagManageDelete = new Button { Content = "删除" };
        _tagManageDelete.Click += (_, _) => _ = DeleteTagsAsync();
        _tagManageMerge = new Button { Content = "合并" };
        _tagManageMerge.Click += (_, _) => _ = MergeTagsAsync();
        var select = _tagManageSelect = new Button { Content = "选择" };
        select.Click += (_, _) => { if (!_tagManageSelection.Exit()) _tagManageSelection.Enter(); };
        _tagManageStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = .7 };
        bar.Children.Add(select);
        bar.Children.Add(_tagManageRename);
        bar.Children.Add(_tagManageDelete);
        bar.Children.Add(_tagManageMerge);
        bar.Children.Add(_tagManageStatus);
        Grid.SetRow(bar, 1);
        TagManageHost.Children.Add(bar);

        // 面板内列表：滚动条是浮层，内容右侧显式留 16（UI 规范「滚动条与内容必须分离」）。
        _tagManageList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            Padding = new Thickness(0, 0, 16, 0),
        };
        var template = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid ColumnSpacing="10">
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="Auto"/>
                  <ColumnDefinition Width="260"/>
                  <ColumnDefinition Width="*"/>
                  <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <CheckBox IsHitTestVisible="False" IsTabStop="False" MinWidth="0" Padding="0"
                          Visibility="{Binding SelectVisible}" IsChecked="{Binding Selected, Mode=TwoWay}"/>
                <TextBlock Grid.Column="1" Text="{Binding Display}" TextTrimming="CharacterEllipsis"
                           VerticalAlignment="Center"/>
                <TextBlock Grid.Column="2" Text="{Binding Identity}" TextTrimming="CharacterEllipsis"
                           Opacity=".6" FontSize="12" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="3" Text="{Binding UsageText}" Opacity=".7" FontSize="12"
                           VerticalAlignment="Center"/>
              </Grid>
            </DataTemplate>
            """);
        _tagManageList.ItemTemplate = template;
        _tagManageList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not TagManageViewItem item) return;
            if (!_tagManageSelection.IsActive) return;
            _tagManageSelection.Toggle(item.Id.ToString(),
                IsCtrlDown(), IsShiftDown(), _tagManageVisible.Select(t => t.Id.ToString()).ToArray());
        };
        _tagManageSelection.Changed += RefreshTagManageUi;
        Grid.SetRow(_tagManageList, 2);
        TagManageHost.Children.Add(_tagManageList);
        RefreshTagManageUi();
    }

    private sealed class TagManageViewItem
    {
        public int Id { get; init; }
        public string Display { get; init; } = "";
        public string Identity { get; init; } = "";
        public string UsageText { get; init; } = "";
        public Visibility SelectVisible { get; init; } = Visibility.Collapsed;
        public bool Selected { get; set; }
    }

    private static bool IsCtrlDown() => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
        Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    private static bool IsShiftDown() => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
        Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void LoadTagManage()
    {
        EnsureTagManageUI();
        _tagManageRows = TagManageService.List(_tagManageSearch?.Text ?? "");
        ApplyTagManageFilter();
    }

    private void ApplyTagManageFilter()
    {
        if (_tagManageList is null) return;
        _tagManageVisible = [.. _tagManageRows];
        RenderTagManageList();
    }

    private void RenderTagManageList()
    {
        if (_tagManageList is null) return;
        _tagManageList.ItemsSource = _tagManageVisible.Select(t => new TagManageViewItem
        {
            Id = t.Id,
            Display = string.IsNullOrWhiteSpace(t.Zh) ? t.Name : $"{t.Zh}（{t.Name}）",
            Identity = t.Identity,
            UsageText = $"{t.Usage} 张",
            SelectVisible = _tagManageSelection.IsActive ? Visibility.Visible : Visibility.Collapsed,
            Selected = _tagManageSelection.Contains(t.Id.ToString()),
        }).ToList();
    }

    private void RefreshTagManageUi()
    {
        if (_tagManageStatus is null) return;
        var count = _tagManageSelection.Count;
        _tagManageStatus.Text = count == 0 ? "" : $"已选择 {count} 个标签";
        // 只改文字：文字变「取消选择」就说明已进入选择模式，一项未选也看得出来。
        if (_tagManageSelect is not null)
            _tagManageSelect.Content = _tagManageSelection.IsActive ? "取消选择" : "选择";
        if (_tagManageRename is not null) _tagManageRename.IsEnabled = count == 1;
        if (_tagManageDelete is not null) _tagManageDelete.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_tagManageMerge is not null) _tagManageMerge.IsEnabled = count == 2;
        RenderTagManageList();
    }

    private async Task RenameTagAsync()
    {
        var row = _tagManageVisible.FirstOrDefault(t => _tagManageSelection.Contains(t.Id.ToString()));
        if (row is null) return;
        var zhBox = new TextBox { Text = row.Zh, PlaceholderText = "中文显示名" };
        var enBox = new TextBox { Text = row.En, PlaceholderText = "英文显示名" };
        var panel = new StackPanel { Spacing = 8, Children = { zhBox, enBox } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "修改显示名", Content = panel,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Task.Run(() => TagManageService.RenameDisplay(row.Id, zhBox.Text.Trim(), enBox.Text.Trim()));
        _tagManageSelection.Exit();
        LoadTagManage();
    }

    private async Task DeleteTagsAsync()
    {
        var rows = _tagManageVisible.Where(t => _tagManageSelection.Contains(t.Id.ToString())).ToArray();
        if (rows.Length == 0) return;
        var total = rows.Sum(t => t.Usage);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "删除标签",
            Content = new TextBlock { Text = $"将删除 {rows.Length} 个标签，并移除 {total} 条图片标签关联。图片本身不会删除。" },
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.None,
        };
        DialogService.UseDestructivePrimary(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var removed = await Task.Run(() => TagManageService.Delete(rows.Select(t => t.Id).ToArray()));
        _tagManageSelection.Exit();
        LoadTagManage();
        AppService.NotifyPackRemoved();
    }

    private async Task MergeTagsAsync()
    {
        var rows = _tagManageVisible.Where(t => _tagManageSelection.Contains(t.Id.ToString())).ToArray();
        if (rows.Length != 2) return;
        var box = new ComboBox { Header = "保留哪一个", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var row in rows)
            box.Items.Add(new ComboBoxItem { Content = $"{row.Category}/{row.Name}（{row.Usage} 张）", Tag = row });
        box.SelectedIndex = rows[0].Usage >= rows[1].Usage ? 0 : 1;
        var panel = new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { Text = "另一个标签的图片、别名与显示名将并入保留项，之后被合并的名字会被删除。", TextWrapping = TextWrapping.Wrap },
            box,
        } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "合并标签", Content = panel,
            PrimaryButtonText = "合并", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            (box.SelectedItem as ComboBoxItem)?.Tag is not TagManageService.TagRow winner) return;
        var loser = rows.First(r => r.Id != winner.Id);
        var plan = new[]
        {
            new { loser = new[] { loser.Category, loser.Name }, winner = new[] { winner.Category, winner.Name }, zh = winner.Zh },
        };
        var planPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "tag_manage_merge.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(planPath)!);
        System.IO.File.WriteAllText(planPath, System.Text.Json.JsonSerializer.Serialize(plan));

        var preview = await Task.Run(() =>
        {
            using var store = new StoreService(AppPaths.DbPath);
            return TagMergeService.Run(store, planPath, apply: false);
        });
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "合并影响",
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = preview }, MaxHeight = 360, Padding = new Thickness(0, 0, 16, 0),
            },
            PrimaryButtonText = "确认合并", CloseButtonText = "取消", DefaultButton = ContentDialogButton.None,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        var report = await Task.Run(() =>
        {
            using var store = new StoreService(AppPaths.DbPath);
            return TagMergeService.Run(store, planPath, apply: true);
        });
        _tagManageSelection.Exit();
        LoadTagManage();
        AppService.NotifyPackRemoved();
        _ = report;
    }
}
