using Gallery.Controls;
using Gallery.Controls.Tags;
using Gallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Views;

public sealed partial class SettingsPage
{
    private FolderMap _directoryMap = new();

    private static readonly string[] DirectoryLevelTitles = ["一级目录", "二级目录", "三级目录", "四级目录", "五级目录", "六级目录", "七级目录", "八级目录"];
    private static readonly (string Key, string Label)[] TagOrders = [("input", "填写顺序"), ("text", "文字顺序")];
    private static readonly (string Key, string Label)[] DirectoryCategories =
    [
        ("work", "作品"), ("character", "角色"), ("artist", "作者"),
        ("kind", "类型"), ("rating", "分级"), ("custom", "自定义"),
    ];
    private static readonly (string Key, string Label)[] LevelKinds =
    [
        ("fixed", "自定义名称"), ("tag", "按标签"), ("source", "按来源站点"), ("date", "按导入时间"),
    ];
    private static readonly (string Key, string Label)[] DateParts =
    [
        ("year", "年"), ("month", "月"), ("day", "日"),
    ];

    private void RefreshDirectoryRules()
    {
        _directoryMap = FolderMap.Load(AppPaths.FolderMapPath);
        DirectoryRulesList.Children.Clear();
        DirectoryOverridesList.Children.Clear();
        var subordinateTextStyle = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        DirectoryRulesStatus.Visibility = Visibility.Collapsed;
        DirectoryRulesBody.Visibility = Visibility.Visible;
        DirectoryOverrideAddButton.IsEnabled = _directoryMap.LoadError.Length == 0;
        DirectoryRuleAddButton.IsEnabled = _directoryMap.LoadError.Length == 0 && _directoryMap.Levels.Count < FolderMap.MaxLevels;
        ArchiveRootValue.Text = _directoryMap.Root.Length > 0 ? _directoryMap.Root : "未选择（沿用上次归档的图库）";
        if (_directoryMap.LoadError.Length > 0)
        {
            DirectoryRuleStatus("无法读取目录配置：" + _directoryMap.LoadError, true);
            return;
        }
        for (var i = 0; i < _directoryMap.Levels.Count; i++)
        {
            var index = i;
            var level = _directoryMap.Levels[i];
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new());
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = $"{DirectoryLevelTitles[i]}：{LevelSummary(level)}", Style = subordinateTextStyle, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = LevelDetail(level), TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = .6, FontSize = 12,
            });
            row.Children.Add(text);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            void Action(string label, Action action, bool available = true)
            {
                var button = new Button { Content = label, IsEnabled = available, MinWidth = 32, Padding = new Thickness(8, 4, 8, 4) };
                button.Click += (_, _) => { if (!_packBusy) action(); };
                actions.Children.Add(button);
            }
            Action("↑", () => MoveDirectoryRule(index, -1), index > 0);
            Action("↓", () => MoveDirectoryRule(index, 1), index + 1 < _directoryMap.Levels.Count);
            Action("编辑", () => _ = EditDirectoryRuleAsync(index));
            Action("删除", () => SaveDirectoryRules(map => map.Levels.RemoveAt(index)));
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            DirectoryRulesList.Children.Add(new Border
            {
                Padding = new Thickness(8), CornerRadius = new CornerRadius(4), Child = row,
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            });
        }
        for (var i = 0; i < _directoryMap.Overrides.Count; i++)
        {
            var index = i;
            var rule = _directoryMap.Overrides[i];
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(new TextBlock { Text = $"匹配标签：{DirectoryCategories.First(c => c.Key == rule.Category).Label} / {string.Join("、", rule.Tags)}", Style = subordinateTextStyle, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"{DirectoryLevelTitles[rule.Position]}：{LevelSummary(rule.Directory)}", Style = subordinateTextStyle, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = rule.ReplaceTail
                ? "后续路径：" + (rule.Children.Count == 0 ? "到此结束" : string.Join(" → ", rule.Children.Select(LevelSummary)))
                : "后续沿用默认下级", FontSize = 12, Opacity = .6 });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            void AddAction(string label, Action action, bool enabled = true)
            {
                var button = new Button { Content = label, IsEnabled = enabled, MinWidth = 32, Padding = new Thickness(8, 4, 8, 4) };
                button.Click += (_, _) => { if (!_packBusy) action(); };
                actions.Children.Add(button);
            }
            AddAction("↑", () => SaveDirectoryRules(m => (m.Overrides[index], m.Overrides[index - 1]) = (m.Overrides[index - 1], m.Overrides[index])), index > 0);
            AddAction("↓", () => SaveDirectoryRules(m => (m.Overrides[index], m.Overrides[index + 1]) = (m.Overrides[index + 1], m.Overrides[index])), index + 1 < _directoryMap.Overrides.Count);
            AddAction("编辑", () => _ = EditDirectoryOverrideAsync(index));
            AddAction("删除", () => SaveDirectoryRules(m => m.Overrides.RemoveAt(index)));
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(content); Grid.SetColumn(actions, 1); row.Children.Add(actions);
            DirectoryOverridesList.Children.Add(new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = row });
        }

    }

    private static string LevelSummary(FolderMap.FolderLevel level) => level.Kind switch
    {
        "fixed" => "自定义名称「" + level.Fixed + "」",
        "source" => "来源站点",
        "date" => $"导入时间（{DateParts.First(p => p.Key == level.DatePart).Label}）",
        _ => $"标签：{DirectoryCategories.First(c => c.Key == level.Category).Label}",
    };

    private static string LevelDetail(FolderMap.FolderLevel level)
    {
        var fallback = level.Fallback.Length > 0 ? level.Fallback : FolderMap.DefaultFallback;
        return level.Kind switch
        {
            "fixed" => "",
            "source" => $"识别站点前缀，识别不出时用域名；兜底 {fallback}",
            "date" => $"兜底 {fallback}",
            _ => (level.UseTagName ? "用标签名作目录名" + (level.Category == "rating" ? "" : "；" + TagOrders.First(o => o.Key == level.TagOrder).Label)
                : "固定目录：" + level.Fixed) + "；无标签兜底 " + fallback,
        };
    }

    private void DirectoryRuleStatus(string message, bool error)
    {
        DirectoryRulesBody.Visibility = Visibility.Visible;
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
            RefreshSettingsSummaries();
        }
        catch (Exception ex)
        {
            RefreshDirectoryRules();
            DirectoryRuleStatus("保存失败：" + ex.Message, true);
            AppService.DebugLog("directory rules save failed: " + ex);
        }
    }

    private void MoveDirectoryRule(int index, int delta)
        => SaveDirectoryRules(map => (map.Levels[index], map.Levels[index + delta])
            = (map.Levels[index + delta], map.Levels[index]));

    private async void ArchiveRootPick_Click(object sender, RoutedEventArgs e)
    {
        if (_packBusy || _directoryMap.LoadError.Length > 0) return;
        var folder = await PickFolderAsync();
        if (folder is null) return;
        if (AppService.Archiver?.ResolveRoot(folder.Path).RootId is not >= 0)
        {
            DirectoryRuleStatus("请选择已登记图库内的目录；新图库请先在「图库」中添加。", true);
            return;
        }
        SaveDirectoryRules(map => map.SetRoot(folder.Path));
    }

    private async void DirectoryRuleAdd_Click(object sender, RoutedEventArgs e) => await EditDirectoryRuleAsync(-1);

    private async Task EditDirectoryRuleAsync(int index)
    {
        if (_packBusy || _directoryMap.LoadError.Length > 0) return;
        _packBusy = true;
        try
        {
            var original = index < 0 ? new FolderMap.FolderLevel("tag", "work", Fallback: FolderMap.DefaultFallback, DatePart: "year")
                : _directoryMap.Levels[index];
            var editor = CreateDirectoryLevelEditor(original);

            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Spacing = 12, MinWidth = 360, MaxWidth = 540 };
            content.Children.Add(editor.View);
            content.Children.Add(error);
            FolderMap.FolderLevel? replacement = null;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = index < 0 ? "添加一级" : "编辑" + DirectoryLevelTitles[index],
                Content = new ScrollViewer { Content = content, MaxHeight = 600, Padding = new Thickness(0, 0, 16, 0) },
                PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            };
            ConfigureDirectoryDialogInput(dialog);
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    replacement = editor.Read();
                    var candidate = FolderMap.Parse(_directoryMap.ToJson());
                    if (index < 0) candidate.Levels.Add(replacement); else candidate.Levels[index] = replacement;
                    candidate.Validate();
                }
                catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            if (await DialogService.ShowAsync(dialog) == ContentDialogResult.Primary && replacement is not null)
                SaveDirectoryRules(map => { if (index < 0) map.Levels.Add(replacement); else map.Levels[index] = replacement; });
        }
        catch (Exception ex) { DirectoryRuleStatus("无法编辑目录级：" + ex.Message, true); }
        finally { _packBusy = false; }
    }

    // 默认级、条件当前级和条件下级共用同一命名编辑器。
    private static (StackPanel View, Func<FolderMap.FolderLevel> Read) CreateDirectoryLevelEditor(FolderMap.FolderLevel original)
    {
        var kind = DropDown(LevelKinds, original.Kind);
        var category = DropDown(DirectoryCategories, original.Category.Length == 0 ? "work" : original.Category);
        var date = DropDown(DateParts, original.DatePart.Length == 0 ? "year" : original.DatePart);
        var order = DropDown(TagOrders, original.TagOrder);
        var useTagName = new CheckBox { Content = "以标签名作为目录名", IsChecked = original.UseTagName };
        var fixedName = new TextBox { Text = original.Fixed, PlaceholderText = "请输入目录名称" };
        var fallback = new TextBox { Text = original.Fallback, PlaceholderText = FolderMap.DefaultFallback };
        var categoryRow = Labeled("标签类型", category);
        var orderRow = Labeled("标签选取方式", order);
        var fixedRow = Labeled("目录名称", fixedName);
        var dateRow = Labeled("精确到", date);
        var fallbackRow = Labeled("无数据时兜底目录名", fallback);
        var detail = new StackPanel { Spacing = 12, Children = { categoryRow, orderRow, useTagName, fixedRow, dateRow, fallbackRow } };
        void Show()
        {
            var selected = (string)((ComboBoxItem)kind.SelectedItem!).Tag;
            categoryRow.Visibility = orderRow.Visibility = useTagName.Visibility = selected == "tag" ? Visibility.Visible : Visibility.Collapsed;
            fixedRow.Visibility = selected is "tag" or "fixed" ? Visibility.Visible : Visibility.Collapsed;
            dateRow.Visibility = selected == "date" ? Visibility.Visible : Visibility.Collapsed;
            fallbackRow.Visibility = selected == "fixed" ? Visibility.Collapsed : Visibility.Visible;
            fixedName.IsEnabled = selected == "fixed" || useTagName.IsChecked != true;
        }
        kind.SelectionChanged += (_, _) => Show();
        useTagName.Checked += (_, _) => fixedName.IsEnabled = false;
        useTagName.Unchecked += (_, _) => fixedName.IsEnabled = true;
        Show();
        var view = new StackPanel { Spacing = 12 };
        view.Children.Add(Labeled("目录命名", kind));
        view.Children.Add(detail);
        FolderMap.FolderLevel Read() => new((string)((ComboBoxItem)kind.SelectedItem!).Tag,
            (string)((ComboBoxItem)category.SelectedItem!).Tag, useTagName.IsChecked == true,
            fixedName.Text.Trim(), fallback.Text.Trim(), (string)((ComboBoxItem)date.SelectedItem!).Tag,
            (string)((ComboBoxItem)order.SelectedItem!).Tag);
        return (view, Read);
    }

    private async void DirectoryOverrideAdd_Click(object sender, RoutedEventArgs e) => await EditDirectoryOverrideAsync(-1);

    private sealed record DirectoryTagChoice(string Name, string Display)
    {
        public override string ToString() => Display;
    }

    private async Task EditDirectoryOverrideAsync(int index)
    {
        if (_packBusy || _directoryMap.LoadError.Length > 0) return;
        _packBusy = true;
        var open = true;
        try
        {
            var original = index < 0 ? new FolderMap.PathOverride("work", [], 0,
                new("fixed"), false, []) : _directoryMap.Overrides[index];
            var category = DropDown(DirectoryCategories, original.Category);
            var positions = DirectoryLevelTitles.Select((title, i) => (i.ToString(), title)).ToArray();
            var position = DropDown(positions, original.Position.ToString());
            var tail = DropDown([("default", "沿用默认下级"), ("custom", "自定义下级")], original.ReplaceTail ? "custom" : "default");
            var selectedTags = original.Tags.ToList();
            var chips = new TagWrapPanel();
            var search = new SearchInputBox { PlaceholderText = "搜索标签，选择或按回车添加", EnableSuggestionTabNavigation = true };
            var error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            var searchVersion = 0;
            void RefreshTags()
            {
                chips.Children.Clear();
                foreach (var name in selectedTags)
                {
                    var chip = new TagChip((string)((ComboBoxItem)category.SelectedItem!).Tag, name,
                        () => { selectedTags.Remove(name); RefreshTags(); });
                    chip.Label.IsTextSelectionEnabled = true;
                    chips.Children.Add(chip);
                }
            }
            RefreshTags();
            category.SelectionChanged += (_, _) => { selectedTags.Clear(); RefreshTags(); search.Text = ""; search.ItemsSource = null; searchVersion++; };
            search.TextChanged += async (_, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                var version = ++searchVersion;
                var query = search.Text.Trim();
                if (query.Length == 0) { search.ItemsSource = null; return; }
                var key = (string)((ComboBoxItem)category.SelectedItem!).Tag;
                await Task.Delay(160);
                if (!open || version != searchVersion) return;
                try
                {
                    var hits = await Task.Run(() =>
                    {
                        lock (AppService.DbLock)
                            return TagSearchService.Search(AppService.Store!, key, query)
                                .Select(h => new DirectoryTagChoice(h.Name, h.SearchDisplay)).ToList();
                    });
                    if (open && version == searchVersion) search.ItemsSource = hits;
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) { if (open) { error.Text = "标签搜索失败：" + ex.Message; error.Visibility = Visibility.Visible; } }
            };
            search.QuerySubmitted += (_, args) =>
            {
                var name = args.ChosenSuggestion is DirectoryTagChoice hit ? hit.Name : args.QueryText.Trim();
                if (name.Length > 0 && !selectedTags.Contains(name, StringComparer.OrdinalIgnoreCase)) selectedTags.Add(name);
                RefreshTags(); search.Text = ""; search.ItemsSource = null;
            };
            var directory = CreateDirectoryLevelEditor(original.Directory);
            var childrenPanel = new StackPanel { Spacing = 8 };
            var children = original.Children.Select(CreateDirectoryLevelEditor).ToList();
            var addChild = new Button { Content = "添加一级", HorizontalAlignment = HorizontalAlignment.Right };
            var customTail = new StackPanel { Spacing = 8 };
            customTail.Children.Add(addChild); customTail.Children.Add(childrenPanel);
            void RefreshChildren()
            {
                foreach (var old in childrenPanel.Children.OfType<Expander>())
                    ((StackPanel)old.Content).Children.Clear();
                childrenPanel.Children.Clear();
                var start = int.Parse((string)((ComboBoxItem)position.SelectedItem!).Tag);
                addChild.IsEnabled = start + 1 + children.Count < FolderMap.MaxLevels;
                for (var i = 0; i < children.Count; i++)
                {
                    var at = i;
                    var content = new StackPanel { Spacing = 8 };
                    content.Children.Add(children[i].View);
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    void AddAction(string label, Action action, bool enabled = true)
                    {
                        var b = new Button { Content = label, IsEnabled = enabled };
                        b.Click += (_, _) => { action(); RefreshChildren(); }; actions.Children.Add(b);
                    }
                    AddAction("↑", () => (children[at], children[at - 1]) = (children[at - 1], children[at]), at > 0);
                    AddAction("↓", () => (children[at], children[at + 1]) = (children[at + 1], children[at]), at + 1 < children.Count);
                    AddAction("删除", () => children.RemoveAt(at));
                    content.Children.Add(actions);
                    childrenPanel.Children.Add(new Expander { Header = start + i + 1 < DirectoryLevelTitles.Length
                        ? DirectoryLevelTitles[start + i + 1] : "超出八级目录", Content = content,
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
            }
            addChild.Click += (_, _) =>
            {
                children.Add(CreateDirectoryLevelEditor(new("tag", "artist", Fallback: FolderMap.DefaultFallback)));
                RefreshChildren();
                ((Expander)childrenPanel.Children.Last()).IsExpanded = true;
            };
            position.SelectionChanged += (_, _) => RefreshChildren();
            tail.SelectionChanged += (_, _) => customTail.Visibility = (string)((ComboBoxItem)tail.SelectedItem!).Tag == "custom" ? Visibility.Visible : Visibility.Collapsed;
            customTail.Visibility = original.ReplaceTail ? Visibility.Visible : Visibility.Collapsed;
            RefreshChildren();
            var content = new StackPanel { Spacing = 16, MinWidth = 360, MaxWidth = 540 };
            content.Children.Add(Labeled("匹配标签", category)); content.Children.Add(chips); content.Children.Add(search);
            content.Children.Add(Labeled("作用位置", position)); content.Children.Add(directory.View);
            content.Children.Add(Labeled("后续路径", tail)); content.Children.Add(customTail);
            var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var previewPanel = new StackPanel { Spacing = 6 };
            previewPanel.Children.Add(new TextBlock { Text = "路径预览", FontSize = 12, Opacity = .6 });
            previewPanel.Children.Add(preview);
            content.Children.Add(new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = previewPanel });
            content.Children.Add(error);
            void RefreshPreview()
            {
                var start = int.Parse((string)((ComboBoxItem)position.SelectedItem!).Tag);
                string Segment(FolderMap.FolderLevel l) => l.Kind == "fixed" ? (l.Fixed.Length > 0 ? l.Fixed : "目录名称") : "〈" + LevelSummary(l) + "〉";
                var path = _directoryMap.Levels.Take(start).Select(Segment).Append(Segment(directory.Read()));
                path = path.Concat((string)((ComboBoxItem)tail.SelectedItem!).Tag == "custom"
                    ? children.Select(c => Segment(c.Read())) : _directoryMap.Levels.Skip(start + 1).Select(Segment));
                preview.Text = Path.Join(new[] { _directoryMap.Root.Length > 0 ? _directoryMap.Root : "归档根目录" }
                    .Concat(path).ToArray()).Replace('/', '\\');
            }
            content.LostFocus += (_, _) => RefreshPreview();
            position.SelectionChanged += (_, _) => RefreshPreview();
            tail.SelectionChanged += (_, _) => RefreshPreview();
            RefreshPreview();
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = index < 0 ? "添加条件覆盖" : "编辑条件覆盖",
                Content = new ScrollViewer { Content = content, MaxHeight = 600, Padding = new Thickness(0, 0, 16, 0) },
                PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            ConfigureDirectoryDialogInput(dialog);
            FolderMap.PathOverride? replacement = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    var replaceTail = (string)((ComboBoxItem)tail.SelectedItem!).Tag == "custom";
                    replacement = new((string)((ComboBoxItem)category.SelectedItem!).Tag, selectedTags.ToList(),
                        int.Parse((string)((ComboBoxItem)position.SelectedItem!).Tag), directory.Read(), replaceTail,
                        replaceTail ? children.Select(c => c.Read()).ToList() : []);
                    var candidate = FolderMap.Parse(_directoryMap.ToJson());
                    if (index < 0) candidate.Overrides.Add(replacement); else candidate.Overrides[index] = replacement;
                    candidate.Validate();
                }
                catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            if (await DialogService.ShowAsync(dialog) == ContentDialogResult.Primary && replacement is not null)
                SaveDirectoryRules(m => { if (index < 0) m.Overrides.Add(replacement); else m.Overrides[index] = replacement; });
        }
        catch (Exception ex) { DirectoryRuleStatus("无法编辑条件覆盖：" + ex.Message, true); }
        finally { open = false; _packBusy = false; }
    }

    private static void ConfigureDirectoryDialogInput(ContentDialog dialog)
    {
        dialog.IsTabStop = true;
        dialog.AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
            MoveSettingsFocusToBackground(dialog, dialog, args)), true);
    }

    private static Grid Labeled(string header, FrameworkElement control)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static SelectionDropDown DropDown((string Key, string Label)[] options, string selected)
    {
        var box = new SelectionDropDown { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var entry in options)
        {
            var item = new ComboBoxItem { Content = entry.Label, Tag = entry.Key };
            box.Items.Add(item);
            if (entry.Key == selected) box.SelectedItem = item;
        }
        return box;
    }
}
