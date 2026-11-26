using Gallery.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Gallery.Controls.Catalog;

internal static class CatalogImportDialog
{
    public static Task<bool> ReviewAsync(DispatcherQueue dispatcher, XamlRoot root, TagCatalogService.CatalogImportBatch batch)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try { result.SetResult(await ShowAsync(root, batch)); }
            catch (Exception ex) { result.SetException(ex); }
        })) result.SetException(new InvalidOperationException("窗口已关闭，导入已取消"));
        return result.Task;
    }

    private static async Task<bool> ShowAsync(XamlRoot root, TagCatalogService.CatalogImportBatch batch)
    {
        var width = Math.Max(280, Math.Min(660, root.Size.Width - 96));
        var body = new StackPanel { Spacing = 16, Width = width };
        body.Children.Add(new TextBlock { Text = "确认合并对象和采用的内容。别名、登场作品合并去重，空缺补齐；未采用的内容不保留。", TextWrapping = TextWrapping.Wrap });
        var bulk = new ComboBox { Header = "批量选择内容基准", ItemsSource = new[] { "全部以本库为准", "全部以导入资料为准" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(bulk);
        var bases = new List<ComboBox>();
        var previews = new List<Task>();
        foreach (var choice in batch.Choices)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = choice.Incoming.Name, TextWrapping = TextWrapping.Wrap, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var options = choice.Candidates.Select(c => "合并到：" + c.Name).ToList();
            if (!choice.SameIdentity) options.Add("不是同一对象，作为新资料保留");
            var target = new ComboBox { Header = choice.SameIdentity ? "已识别为同一资料，内容存在差异" : "找到疑似重复，请确认", ItemsSource = options,
                SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            choice.TargetEntityId = choice.Candidates[0].EntityId;
            panel.Children.Add(target);
            var baseline = new ComboBox { Header = "内容基准", ItemsSource = new[] { "以本库为准", "以导入资料为准" }, SelectedIndex = choice.PreferIncoming ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            bases.Add(baseline);
            baseline.SelectionChanged += (_, _) => choice.PreferIncoming = baseline.SelectedIndex == 1;
            panel.Children.Add(baseline);
            var columns = new Grid { ColumnSpacing = 16 };
            columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
            var current = new StackPanel { Spacing = 6 };
            var incoming = new StackPanel { Spacing = 6 };
            Grid.SetColumn(incoming, 1); columns.Children.Add(current); columns.Children.Add(incoming);
            Task Describe(StackPanel host, string label, TagCatalogService.CatalogRecord record) => DescribeAsync(host, label, record, batch);
            previews.Add(Describe(current, "本库资料", choice.Candidates[0]));
            previews.Add(Describe(incoming, "导入资料", choice.Incoming));
            target.SelectionChanged += async (_, _) =>
            {
                var index = target.SelectedIndex;
                var separate = index >= choice.Candidates.Length;
                choice.TargetEntityId = separate ? null : choice.Candidates[index].EntityId;
                baseline.IsEnabled = !separate;
                current.Children.Clear();
                if (!separate) await Describe(current, "本库资料", choice.Candidates[index]);
            };
            panel.Children.Add(columns);
            body.Children.Add(new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Child = panel });
        }
        bulk.SelectionChanged += (_, _) => { foreach (var box in bases) box.SelectedIndex = bulk.SelectedIndex; };
        var scroll = new ScrollViewer { Content = body, MaxHeight = Math.Max(180, Math.Min(540, root.Size.Height - 200)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 16, 0) };
        var bar = new ContinuousScrollBar { Step = 0, HorizontalAlignment = HorizontalAlignment.Right };
        var host = new Grid { Children = { scroll, bar } };
        var dialog = new ContentDialog { XamlRoot = root, Title = batch.Kind == "work" ? "确认作品导入" : "确认角色导入",
            PrimaryButtonText = "确认并继续", CloseButtonText = "取消导入", DefaultButton = ContentDialogButton.Close,
            Content = host };
        dialog.Opened += (_, _) => bar.BindVerticalViewport(scroll);
        dialog.Resources["ContentDialogMaxWidth"] = width + 64;
        await Task.WhenAll(previews);
        return await DialogService.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private static async Task DescribeAsync(StackPanel host, string label, TagCatalogService.CatalogRecord record, TagCatalogService.CatalogImportBatch batch)
    {
        string Work(string? id) => id is null ? "无" : batch.WorkNames.GetValueOrDefault(id, "未命名作品");
        var text = $"{label}\n中文名：{record.Zh}\n英文名：{record.En}\n别名：{string.Join("、", record.Aliases)}";
        if (record.Kind == "character") text += $"\n原作：{Work(record.Origin)}\n登场：{string.Join("、", record.Appearances.Select(w => Work(w)))}";
        host.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (record.CoverHash is { } hash && batch.Images.TryGetValue(hash, out var bytes))
        {
            var image = new Image { Height = 110, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
            host.Children.Add(image);
            var bitmap = new BitmapImage();
            using var buffer = new MemoryStream(bytes, writable: false);
            using var stream = buffer.AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        else host.Children.Add(new TextBlock { Text = "无背景图", Opacity = .6 });
    }
}
