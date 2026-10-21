using Gallery.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gallery.Controls.Review;

/// <summary>清理历史的选项：只删队列记录，不删磁盘文件。</summary>
public sealed record HistoryCleanupChoice(int Days, bool DeleteAll, bool IncludeExistingSources);

/// <summary>「清理历史」对话框：保留天数 + 是否连原文件仍存在的记录一起删。</summary>
public static class HistoryCleanupDialog
{
    private static readonly (int Days, string Text)[] Presets =
    [
        (0, "永久（不清理）"),
        (7, "最近 7 天"),
        (30, "最近 30 天"),
        (90, "最近 90 天"),
        (180, "最近 180 天"),
        (365, "最近 365 天"),
    ];

    public static async Task<HistoryCleanupChoice?> ShowAsync(XamlRoot root, int defaultDays)
    {
        var keep = new SelectionDropDown { MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (days, text) in Presets)
            keep.Items.Add(new ComboBoxItem { Content = text, Tag = days.ToString() });
        var selected = Array.FindIndex(Presets, preset => preset.Days == defaultDays);
        keep.SelectedIndex = selected >= 0 ? selected : 0;

        var deleteAll = new CheckBox { Content = "清理全部历史（忽略上面的保留天数）" };
        var includeExisting = new CheckBox { Content = "也包括原文件仍然存在的已归档记录" };
        var hint = new TextBlock
        {
            FontSize = 12,
            Opacity = .7,
            TextWrapping = TextWrapping.Wrap,
            Text = "只删除队列记录，不删除磁盘文件。原文件仍在的已归档记录默认保留，避免同一路径再次导入生成副本。",
        };

        var body = new StackPanel { Spacing = 12, Width = 440 };
        body.Children.Add(new TextBlock
        {
            Text = "删除已归档、已忽略记录中早于保留天数的部分；待归档和文件丢失记录不受影响。",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(keep);
        body.Children.Add(deleteAll);
        body.Children.Add(includeExisting);
        body.Children.Add(hint);

        var dialog = new ContentDialog
        {
            Title = "清理历史",
            Content = body,
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };

        deleteAll.Checked += (_, _) => keep.IsEnabled = false;
        deleteAll.Unchecked += (_, _) => keep.IsEnabled = true;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (deleteAll.IsChecked != true && Days() <= 0)
            {
                hint.Text = "当前是永久保留：请选择具体天数，或勾选「清理全部历史」。";
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return new HistoryCleanupChoice(Days(), deleteAll.IsChecked == true, includeExisting.IsChecked == true);

        int Days()
            => keep.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var days) ? days : 0;
    }
}
