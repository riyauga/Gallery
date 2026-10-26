using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Views;

public sealed partial class SettingsPage
{
    private FolderMap _directoryMap = new();
    private static readonly (string Key, string Label)[] DirectoryCategories =
    [
        ("work", "作品"), ("character", "角色"), ("artist", "画师"),
        ("kind", "类型"), ("custom", "自定义"), ("rating", "分级"),
    ];

    private void RefreshDirectoryRules()
    {
        _directoryMap = FolderMap.Load(AppPaths.FolderMapPath);
        DirectoryRulesList.Children.Clear();
        DirectoryRulesStatus.Visibility = Visibility.Collapsed;
        DirectoryRuleAddButton.IsEnabled = _directoryMap.LoadError.Length == 0;
        if (_directoryMap.LoadError.Length > 0)
        {
            DirectoryRuleStatus("无法读取目录配置：" + _directoryMap.LoadError, true);
            return;
        }
        if (_directoryMap.RoutingRules.Count == 0)
            DirectoryRulesList.Children.Add(new TextBlock { Text = "暂无标签分流规则，使用作品／画师目录。", Opacity = .6 });
        for (var i = 0; i < _directoryMap.RoutingRules.Count; i++)
        {
            var index = i;
            var rule = _directoryMap.RoutingRules[i];
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new());
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(enabled, "启用 " + rule.Name);
            enabled.Checked += (_, _) => ToggleDirectoryRule(index, true);
            enabled.Unchecked += (_, _) => ToggleDirectoryRule(index, false);
            row.Children.Add(enabled);
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            var category = DirectoryCategories.First(c => c.Key == rule.Category).Label;
            text.Children.Add(new TextBlock { Text = $"{index + 1}. {category}：{rule.Name}", TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = rule.Folder + (rule.SplitRating ? $" · 分级 {rule.SfwFolder} / {rule.NsfwFolder}" : "")
                    + (rule.AppendArtist ? " · 按画师细分" : ""),
                TextTrimming = TextTrimming.CharacterEllipsis, Opacity = .6, FontSize = 12,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            void Action(string label, Action action, bool available = true)
            {
                var button = new Button { Content = label, IsEnabled = available, MinWidth = 32, Padding = new Thickness(8, 4, 8, 4) };
                button.Click += (_, _) => { if (!_packBusy) action(); };
                actions.Children.Add(button);
            }
            Action("↑", () => MoveDirectoryRule(index, -1), index > 0);
            Action("↓", () => MoveDirectoryRule(index, 1), index + 1 < _directoryMap.RoutingRules.Count);
            Action("编辑", () => _ = EditDirectoryRuleAsync(index));
            Action("删除", () => SaveDirectoryRules(map => map.RoutingRules.RemoveAt(index)));
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            DirectoryRulesList.Children.Add(new Border
            {
                Padding = new Thickness(8), CornerRadius = new CornerRadius(4), Child = row,
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            });
        }
    }

    private void DirectoryRuleStatus(string message, bool error)
    {
        DirectoryRulesStatus.Text = message;
        DirectoryRulesStatus.Foreground = (Brush)Application.Current.Resources[error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        DirectoryRulesStatus.Visibility = Visibility.Visible;
    }

    private void SaveDirectoryRules(Action<FolderMap> edit)
    {
        try
        {
            var candidate = FolderMap.Parse(_directoryMap.ToJson());
            edit(candidate);
            candidate.Save(AppPaths.FolderMapPath);
            RefreshDirectoryRules();
            DirectoryRuleStatus("已保存，之后的目录计算将使用这些规则；已有图片未移动。", false);
        }
        catch (Exception ex)
        {
            RefreshDirectoryRules();
            DirectoryRuleStatus("保存失败：" + ex.Message, true);
            AppService.DebugLog("directory rules save failed: " + ex);
        }
    }

    private void ToggleDirectoryRule(int index, bool enabled)
    {
        if (_packBusy) { RefreshDirectoryRules(); return; }
        SaveDirectoryRules(map => map.RoutingRules[index] = map.RoutingRules[index] with { Enabled = enabled });
    }

    private void MoveDirectoryRule(int index, int delta)
        => SaveDirectoryRules(map => (map.RoutingRules[index], map.RoutingRules[index + delta])
            = (map.RoutingRules[index + delta], map.RoutingRules[index]));

    private async void DirectoryRuleAdd_Click(object sender, RoutedEventArgs e) => await EditDirectoryRuleAsync(-1);

    private async Task EditDirectoryRuleAsync(int index)
    {
        if (_packBusy || _directoryMap.LoadError.Length > 0) return;
        _packBusy = true;
        try
        {
            var original = index < 0 ? new FolderMap.RoutingRule(true, "custom", "", "", false, "", "", false)
                : _directoryMap.RoutingRules[index];
            var category = new ComboBox { Header = "标签类型", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var entry in DirectoryCategories)
            {
                var item = new ComboBoxItem { Content = entry.Label, Tag = entry.Key };
                category.Items.Add(item);
                if (entry.Key == original.Category) category.SelectedItem = item;
            }
            var name = new TextBox { Header = "标签名", Text = original.Name, PlaceholderText = "填写标签原名" };
            var folder = new TextBox { Header = "目标目录（相对图库根目录）", Text = original.Folder, PlaceholderText = "例如：壁纸/收藏" };
            var split = new CheckBox { Content = "按 SFW / NSFW 细分", IsChecked = original.SplitRating };
            var sfw = new TextBox { Header = "SFW 子目录名", Text = original.SfwFolder, IsEnabled = original.SplitRating };
            var nsfw = new TextBox { Header = "NSFW 子目录名", Text = original.NsfwFolder, IsEnabled = original.SplitRating };
            split.Checked += (_, _) => { sfw.IsEnabled = nsfw.IsEnabled = true; };
            split.Unchecked += (_, _) => { sfw.IsEnabled = nsfw.IsEnabled = false; };
            var author = new CheckBox { Content = "再按画师细分（有画师标签时）", IsChecked = original.AppendArtist };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Spacing = 12, MinWidth = 340, MaxWidth = 540 };
            foreach (var control in new UIElement[] { category, name, folder, split, sfw, nsfw, author,
                new TextBlock { Text = "启用分级细分时，未分级图片只进入目标目录。", TextWrapping = TextWrapping.Wrap, Opacity = .6 }, error })
                content.Children.Add(control);
            FolderMap.RoutingRule? replacement = null;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = index < 0 ? "添加目录规则" : "编辑目录规则",
                Content = new ScrollViewer { Content = content, MaxHeight = 600 },
                PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    replacement = new(original.Enabled, (string)((ComboBoxItem)category.SelectedItem).Tag,
                        name.Text.Trim(), folder.Text.Trim(), split.IsChecked == true, sfw.Text.Trim(), nsfw.Text.Trim(), author.IsChecked == true);
                    var candidate = FolderMap.Parse(_directoryMap.ToJson());
                    if (index < 0) candidate.RoutingRules.Add(replacement); else candidate.RoutingRules[index] = replacement;
                    candidate.Validate();
                }
                catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            if (await DialogService.ShowAsync(dialog) == ContentDialogResult.Primary && replacement is not null)
                SaveDirectoryRules(map => { if (index < 0) map.RoutingRules.Add(replacement); else map.RoutingRules[index] = replacement; });
        }
        catch (Exception ex) { DirectoryRuleStatus("无法编辑目录规则：" + ex.Message, true); }
        finally { _packBusy = false; }
    }
}
