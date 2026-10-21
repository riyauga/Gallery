using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gallery.Controls.Review;

/// <summary>本地导入选项：归档时移动还是复制，以及是否包含子文件夹。</summary>
public sealed record ImportOptions(bool KeepSource, bool Recursive);

/// <summary>选中目录或拖入目录后弹出的居中确认框。</summary>
public static class ImportDialog
{
    public static async Task<ImportOptions?> ShowAsync(XamlRoot root, IReadOnlyList<string> sources)
    {
        var keep = new RadioButton { Content = "复制并保留原文件", GroupName = "ImportMode", IsChecked = true };
        var move = new RadioButton { Content = "移动原文件", GroupName = "ImportMode" };
        var recursive = new CheckBox { Content = "包含子文件夹", IsChecked = true };

        var body = new StackPanel
        {
            Spacing = 14,
            Width = 420,
            Children =
            {
                new TextBlock
                {
                    Text = sources.Count == 1
                        ? Path.GetFileName(sources[0].TrimEnd(Path.DirectorySeparatorChar))
                        : $"{sources.Count} 个位置",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = "归档时怎么处理原文件",
                    FontSize = 12,
                    Opacity = .7,
                },
                keep,
                move,
                recursive,
                new TextBlock
                {
                    Text = "已在待审核队列或图库里的图片会跳过",
                    FontSize = 12,
                    Opacity = .6,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var dialog = new ContentDialog
        {
            Title = "导入到待审核",
            Content = body,
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        if (await Gallery.Services.DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        return new ImportOptions(keep.IsChecked == true, recursive.IsChecked == true);
    }
}
