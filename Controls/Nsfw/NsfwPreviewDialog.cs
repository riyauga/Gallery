using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gallery.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gallery.Controls.Nsfw;

/// <summary>
/// 重判定预览：跑一遍模型但不落库，列出「旧分级 → 新分级」，由用户决定是否应用。
/// 对齐 galleryold 的换模型流程：先看差异，再决定要不要覆盖。
/// </summary>
public sealed class NsfwPreviewDialog
{
    private static readonly (string Key, string Text, Windows.UI.Color Fill)[] Levels =
    {
        ("g", "普通", ColorHelper.FromArgb(0xFF, 0x2E, 0x2E, 0x2E)),
        ("s", "性感", ColorHelper.FromArgb(0xFF, 0x5A, 0x44, 0x1F)),
        ("q", "暗示", ColorHelper.FromArgb(0xFF, 0x6A, 0x33, 0x1F)),
        ("e", "露骨", ColorHelper.FromArgb(0xFF, 0x7A, 0x22, 0x28)),
    };

    public static async Task<bool> ShowAsync(XamlRoot root, NsfwService service, CancellationToken token = default)
    {
        var status = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Text = "正在识别…",
            TextWrapping = TextWrapping.Wrap,
        };

        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 4 };
        var list = new StackPanel { Spacing = 2 };
        var scroller = new ScrollViewer
        {
            Height = 320,
            Width = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // 浮层滚动条会盖住行右端的分数与勾选框，行的右侧留 16（UI_CONVENTIONS.md R1）。
            Padding = new Thickness(0, 0, 16, 0),
            Content = list,
        };

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(progress);
        body.Children.Add(status);
        body.Children.Add(scroller);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "重新识别预览",
            Content = body,
            PrimaryButtonText = "应用这些结果",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };

        var rows = new List<NsfwService.NsfwPreviewRow>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task scan = Task.CompletedTask;

        async Task LoadPreviewAsync()
        {
            var reporter = new Progress<(int Done, int Total)>(p =>
            {
                progress.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                status.Text = $"正在识别 {p.Done}/{p.Total}…";
            });

            (string error, List<NsfwService.NsfwPreviewRow> result) response;
            try { response = await service.PreviewAsync(500, reporter, cts.Token); }
            catch (OperationCanceledException) { return; }
            var (error, result) = response;

            if (error.Length > 0)
            {
                status.Text = error;
                progress.Value = 0;
                return;
            }

            rows.AddRange(result);
            if (rows.Count == 0)
            {
                status.Text = "没有可预览的图片（可能还没识别过图库）。";
                return;
            }

            var changed = rows.Count(r => r.Changed);
            var toNsfw = rows.Count(r => r.Changed && r.IsNsfw);
            var toSafe = rows.Count(r => r.Changed && !r.IsNsfw);
            status.Text = $"共 {rows.Count} 张 · 分级会变 {changed} 张（变敏感 {toNsfw} · 变普通 {toSafe}）" +
                          $" · 模型 {rows[0].ModelName}";
            dialog.IsPrimaryButtonEnabled = changed > 0;

            foreach (var row in rows.OrderByDescending(r => r.Probs.NsfwProb).Take(200))
                list.Children.Add(BuildRow(row));

            if (rows.Count > 200)
                list.Children.Add(new TextBlock
                {
                    Text = $"…另外 {rows.Count - 200} 张按概率排序省略",
                    Margin = new Thickness(0, 6, 0, 0),
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                });
        }
        dialog.Opened += (_, _) => scan = LoadPreviewAsync();
        using var registration = token.Register(() => root.Content.DispatcherQueue.TryEnqueue(() => dialog.Hide()));

        dialog.CloseButtonClick += (_, _) => cts.Cancel();

        var ok = await Gallery.Services.DialogService.ShowAsync(dialog);
        cts.Cancel();
        await scan;
        if (ok != ContentDialogResult.Primary || rows.Count == 0) return false;

        var tagIt = AppService.Settings.Settings.auto_nsfw_tag;
        service.ApplyRows(rows, tagIt);
        return true;
    }

    private static UIElement BuildRow(NsfwService.NsfwPreviewRow row)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(2, 3, 2, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = System.IO.Path.GetFileName(row.Path),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var oldBadge = Badge(row.OldRating, "无");
        Grid.SetColumn(oldBadge, 1);
        grid.Children.Add(oldBadge);

        var arrow = new TextBlock
        {
            Text = "→",
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);

        var newBadge = Badge(row.Rating, row.Rating);
        Grid.SetColumn(newBadge, 3);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(newBadge);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(4),
            Background = row.Changed
                ? new SolidColorBrush(ColorHelper.FromArgb(0x22, 0xFF, 0xC0, 0x4D))
                : new SolidColorBrush(Colors.Transparent),
        };
    }

    private static Border Badge(string key, string fallback)
    {
        var level = Levels.FirstOrDefault(l => l.Key == key);
        var text = key.Length == 0 ? fallback : level.Text ?? key;
        return new Border
        {
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(level.Key is null
                ? ColorHelper.FromArgb(0xFF, 0x33, 0x33, 0x33)
                : level.Fill),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.White),
            },
        };
    }
}
