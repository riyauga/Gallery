// 标签编辑器（阶段 C）：单图/多图统一——给选中的图片增删标签。
// 分组方式照待审核页（Controls/Review/ReviewPage.cs）：一行一类，左边分类名，
// 右边该类已有标签 + 该类自己的输入框（回车即在该类下新增，候选来自标签库与角色词库）；
// 保存才写库。分辨率由图片自动识别，不在这里编辑。
// 三态（PLAN 阶段 C）：每个标签可选「选中(添加) / 排除(移除) / 未选」，
// 分级互斥：同一张图的「分级」只能有一个选中，选新的会自动把旧的标为排除。
// 分级行右侧的循环按钮：用已接入的本地模型重跑分级（结果先挂起，保存才写库）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Controls.Tags;

public sealed class TagEditorDialog
{
    /// <summary>三态：Include=写完要存在；Exclude=写完要移除；Neutral=不关心。</summary>
    private enum TagState { Include, Exclude }

    private sealed class Entry
    {
        public required string Category { get; init; }
        public required string Name { get; init; }
        public TagState State { get; set; } = TagState.Include;

        /// <summary>用户在对话框里主动选过（点芯片/搜索/回车/模型重跑）；加载进来的并集不算。</summary>
        public bool Chosen { get; set; }
    }

    /// <summary>模型跑出来但还没落库的分级（保存才写；取消就丢）。</summary>
    private sealed record PendingRating(
        string Rating, double NsfwProb, double Confidence, double Margin,
        string Detail, string ModelName, string ModelHash);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, PendingRating> _pendingRatings = new();
    private readonly List<PhotoItem> _items;
    private readonly StackPanel _rowsHost = new() { Spacing = 12 };
    /// <summary>每类一行的芯片容器（行只建一次：改标签时只重填芯片，输入框不失焦）。</summary>
    private readonly Dictionary<string, Panel> _rowChips = new(StringComparer.Ordinal);
    private readonly TextBox _pageUrl = new() { PlaceholderText = "https://来源页面/帖子地址", Width = 420, FontSize = 13 };
    private readonly TextBox _sourceUrl = new() { PlaceholderText = "https://原图地址", Width = 420, FontSize = 13 };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.6 };
    private readonly TextBlock _modelHint = new() { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
    private readonly RotateTransform _syncRotate = new();
    private readonly FontIcon _syncIcon = new()
    {
        Glyph = "\uE895",
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
    };
    private readonly Button _syncButton = new();
    private readonly Action<string, ToastKind, string?>? _toast;

    private Storyboard? _spin;
    private CancellationTokenSource? _runCts;
    private bool _running;
    private bool _closed;
    private bool _ratingTouched;

    private TagEditorDialog(IReadOnlyList<PhotoItem> items, Action<string, ToastKind, string?>? toast)
    {
        _items = items.ToList();
        _toast = toast;

        _syncIcon.RenderTransform = _syncRotate;
        _syncButton.Content = _syncIcon;
        _syncButton.Padding = new Thickness(0);
        _syncButton.MinWidth = 0;
        _syncButton.MinHeight = 0;
        _syncButton.Width = 20;
        _syncButton.Height = 20;
        _syncButton.CornerRadius = new CornerRadius(4);
        _syncButton.BorderThickness = new Thickness(0);
        _syncButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _syncButton.VerticalAlignment = VerticalAlignment.Center;
        _syncButton.Click += (_, _) => _ = RerunRatingAsync();
        ToolTipService.SetToolTip(_syncButton, "用分级模型重新判定（保存后写入）");
        AutomationProperties.SetName(_syncButton, "重新判定分级");
    }

    /// <summary>弹出编辑器；返回是否保存成功。</summary>
    public static async Task<bool> ShowAsync(
        XamlRoot root, IReadOnlyList<PhotoItem> items, Action<string, ToastKind, string?>? toast = null)
    {
        if (items.Count == 0) return false;
        var editor = new TagEditorDialog(items, toast);
        var dialog = new ContentDialog
        {
            Title = items.Count == 1 ? "编辑标签" : $"编辑标签（{items.Count} 张）",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            Content = editor.Build(root),
        };
        editor.LoadCurrent();
        var result = await dialog.ShowAsync();
        editor.OnClosed();
        if (result != ContentDialogResult.Primary) return false;
        return editor.Save();
    }

    private UIElement Build(XamlRoot root)
    {
        foreach (var category in EditableCategories)
            _rowsHost.Children.Add(BuildCategoryRow(category));

        var body = new StackPanel
        {
            Spacing = 10,
            Width = 460,
            Children =
            {
                // 「✓ 添加 · ✕ 移除」提示行顶到最上，取代原来的「当前标签…」标题行
                _hint,
                _rowsHost,
                _modelHint,
            },
        };
        if (_items.Count == 1)
        {
            _pageUrl.Text = _items[0].PageUrl ?? "";
            _sourceUrl.Text = _items[0].SourceUrl ?? "";
            // 来源是附加功能：排在标签之后，用一条左右内缩的分割线隔开
            body.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(12, 0, 12, 0),
                Background = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            });
            body.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "图片来源", FontSize = 12, Opacity = 0.55 },
                    new TextBlock { Text = "来源页面", FontSize = 11, Opacity = 0.5 },
                    _pageUrl,
                    new TextBlock { Text = "原始图片地址", FontSize = 11, Opacity = 0.5 },
                    _sourceUrl,
                },
            });
        }
        // 整页一根滚动条：标签往下就是「图片来源」，拖这一次滚动条能从标签一路滑到来源，
        // 不把上下两块拆成各自独立的滚动区。标签区不再限高，内容多了跟着整页一起滚。
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = root.Size.Height > 0 ? Math.Clamp(root.Size.Height - 300, 320, 560) : 520,
            Content = body,
        };
    }

    private void LoadCurrent()
    {
        _entries.Clear();
        _pendingRatings.Clear();
        _modelHint.Text = "";
        var ids = _items.Where(i => i.ImageId > 0).Select(i => i.ImageId).ToList();
        if (ids.Count == 0)
        {
            _hint.Text = "这些图片还没有入库，无法写标签。";
            return;
        }

        var details = AppService.Store is null
            ? []
            : AppService.Store.TagDetailsForImages(ids);
        // 多选时取并集：任一张已有该标签就显示为已选（保存按同一份集合应用到每张）；
        // 分级例外——只有你在对话框里主动选过的分级才会写库（见 Save 的 Chosen 过滤）。
        // 只收可手工编辑的六类：分辨率由图片自动识别，带进来会在多选保存时串到别的图上。
        foreach (var list in details.Values)
            foreach (var tag in list)
                if (EditableCategories.Contains(tag.Category))
                    _entries.TryAdd(tag.Name, new Entry { Category = tag.Category, Name = tag.Name });

        RebuildChips();
        _hint.Text = _items.Count == 1
            ? "✓ 添加 · ✕ 移除 · 未标记的不变"
            : $"✓ 添加 · ✕ 移除 · 未标记的不变（应用到 {_items.Count} 张）";
    }

    /// <summary>把当前三态标签按类重填到各自那一行；输入框与行结构保持不动。</summary>
    private void RebuildChips()
    {
        foreach (var (category, chips) in _rowChips)
        {
            chips.Children.Clear();
            foreach (var entry in _entries.Values
                         .Where(e => e.Category == category)
                         .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                chips.Children.Add(BuildChip(entry));
        }
    }

    /// <summary>可手工编辑的六类：顺序照词库 Taxonomy，分辨率由图片自动识别、不在这里编辑。</summary>
    private static readonly string[] EditableCategories =
        [.. Taxonomy.Categories.Where(c => c != "resolution")];

    private static string CategoryTitle(string category)
    {
        var index = Array.IndexOf(Taxonomy.Categories, category);
        return index >= 0 ? Taxonomy.CategoryTitles[index] : category;
    }

    /// <summary>一行一类：左侧分类名（分级多一个重跑按钮），右侧该类标签 + 该类输入框。</summary>
    private UIElement BuildCategoryRow(string category)
    {
        var title = CategoryTitle(category);
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (category == "rating")
        {
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 4, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            header.Children.Add(new TextBlock { Text = title, FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(_syncButton);
            grid.Children.Add(header);
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        var content = new StackPanel { Spacing = 6 };
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var chips = new TagWrapPanel();
        content.Children.Add(chips);
        _rowChips[category] = chips;
        // 分级只能由芯片（SFW/NSFW）与模型重跑决定，不给手打入口
        if (category != "rating") content.Children.Add(BuildInput(category, title));
        return grid;
    }

    /// <summary>该类自己的输入框：输入或选候选，回车即在该类下新增。</summary>
    private AutoSuggestBox BuildInput(string category, string title)
    {
        var input = new AutoSuggestBox
        {
            PlaceholderText = $"输入或搜索{title}…",
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(input, $"搜索或添加{title}标签");

        var version = 0;
        input.TextChanged += async (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var mine = ++version;
            var query = input.Text.Trim();
            if (query.Length == 0) { input.ItemsSource = null; return; }
            await Task.Delay(160);
            if (_closed || mine != version) return;
            try
            {
                var hits = await Task.Run(() =>
                {
                    if (AppService.Store is not { } store) return [];
                    lock (AppService.DbLock)
                    {
                        var tags = store.SearchTags(query, 200)
                            .Where(t => t.Category == category)
                            .Select(t => new Suggestion(t.Name, t.Display, ""));
                        var entities = new TagCatalogService(store).SearchEntities(query, 30)
                            .Where(e => e.Kind == category)
                            .Select(e => new Suggestion(e.Zh.Length > 0 ? e.Zh : e.En, e.Zh.Length > 0 ? e.Zh : e.En, e.EntityKey))
                            .Where(s => s.Name.Length > 0);
                        return tags.Concat(entities).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(12).ToList();
                    }
                });
                if (!_closed && mine == version) input.ItemsSource = hits;
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
            {
                AppService.DebugLog("tag editor search: " + ex.Message);
            }
        };

        input.QuerySubmitted += (_, args) =>
        {
            var chosen = args.ChosenSuggestion as Suggestion;
            var name = chosen?.Name ?? args.QueryText.Trim();
            if (name.Length == 0) return;
            // 词库候选：记下实体键，保存时把标签绑到词库身份上
            if (chosen is { Key.Length: > 0 }) _profiles[name] = chosen.Key;
            SetState(category, name, TagState.Include);
            input.Text = "";
            input.ItemsSource = null;
        };
        return input;
    }

    /// <summary>输入框候选：Key 非空表示来自角色词库。</summary>
    private sealed record Suggestion(string Name, string Display, string Key)
    {
        public override string ToString() => Display.Length > 0 ? Display : Name;
    }

    /// <summary>三态芯片：✓=添加（正常色）、✕=移除（删除线+红边）、点击循环回未选则整体移除。</summary>
    private UIElement BuildChip(Entry entry)
    {
        bool excluded = entry.State == TagState.Exclude;
        var text = new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = excluded ? 0.55 : 1,
        };
        var icon = new FontIcon
        {
            Glyph = excluded ? "\uE711" : "\uE73E",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chip = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            BorderBrush = excluded
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x99, 0xFF, 0x99, 0xA4))
                : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { icon, text },
            },
        };

        var button = new Button
        {
            Content = chip,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        button.Click += (_, _) =>
        {
            // 添加 → 移除 → 取消改动
            TouchedIfRating(entry.Category);
            if (entry.State == TagState.Include)
            {
                entry.State = TagState.Exclude;
                entry.Chosen = true;
            }
            else
            {
                _entries.Remove(entry.Name);
            }
            RebuildChips();
        };
        return button;
    }

    /// <summary>手动动过分级 → 模型挂起的结果作废（手动优先，保存只按手动选写标签）。</summary>
    private void TouchedIfRating(string category)
    {
        if (category != "rating") return;
        _ratingTouched = true;
        _pendingRatings.Clear();
    }

    /// <summary>写入/更新一个标签的三态；分级互斥在这里处理。</summary>
    private void SetState(string category, string name, TagState state)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) return;
        var cat = category.Length == 0 ? "custom" : category;

        if (state == TagState.Include && cat == "rating")
        {
            // 分级互斥：同分类其他标签标成「移除」
            foreach (var other in _entries.Values.Where(e => e.Category == "rating"))
                other.State = TagState.Exclude;
        }

        if (_entries.TryGetValue(clean, out var existing))
        {
            existing.State = state;
            existing.Chosen = true;
        }
        else
        {
            _entries[clean] = new Entry { Category = cat, Name = clean, State = state, Chosen = true };
        }
        RebuildChips();
    }

    // ------------------------------------------------------------ 模型重跑分级

    /// <summary>用已接入的本地模型重跑选中图片的分级；结果先挂起，保存时才落库。</summary>
    private async Task RerunRatingAsync()
    {
        if (_running || _closed) return;
        var targets = _items
            .Where(i => i.ImageId > 0 && !string.IsNullOrEmpty(i.FullPath))
            .Select(i => (i.ImageId, i.FullPath))
            .ToList();
        if (targets.Count == 0) return;   // 没入库的图：加载时已经写进提示行

        var dir = AppService.Settings.Settings.nsfw_model_dir;
        if (!NsfwScorer.LooksConfigured(dir))
        {
            const string message = "未接入分级模型，请先在设置里选择模型文件夹";
            _modelHint.Text = message;
            Toast(message);
            return;
        }

        var scorer = NsfwScorer.TryCreate(dir, out var error);
        if (scorer is null)
        {
            const string message = "分级模型加载失败";
            _modelHint.Text = message;
            Toast(message, ToastKind.Info, error);
            return;
        }

        _running = true;
        _ratingTouched = false;
        _syncButton.IsEnabled = false;
        StartSpin();

        var cts = new CancellationTokenSource();
        _runCts = cts;
        var token = cts.Token;
        var scored = new List<(int ImageId, NsfwProbs Probs, string Detail)>();
        var gate = new object();
        var failed = 0;
        string? firstError = null;
        var cancelled = false;
        try
        {
            await Task.Run(() =>
            {
                Parallel.ForEach(
                    targets,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                        CancellationToken = token,
                    },
                    item =>
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (!File.Exists(item.FullPath))
                            {
                                lock (gate)
                                {
                                    failed++;
                                    firstError ??= "找不到文件（可能已移动或删除）";
                                }

                                return;
                            }

                            var probs = scorer.Score(item.FullPath, token);
                            var detail = JsonSerializer.Serialize(new
                            {
                                g = Math.Round(probs.G, 4),
                                s = Math.Round(probs.S, 4),
                                q = Math.Round(probs.Q, 4),
                                e = Math.Round(probs.E, 4),
                            });
                            lock (gate) scored.Add((item.ImageId, probs, detail));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // 单张失败不打断整批，但把第一条原因留下来给用户看
                            lock (gate)
                            {
                                failed++;
                                firstError ??= ex.Message;
                            }
                        }
                    });
            }, token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            StopSpin();
            _syncButton.IsEnabled = true;
            _running = false;
            _runCts = null;
            cts.Dispose();
        }

        if (cancelled || _closed) return;   // 关掉对话框 = 取消任务，什么都不留
        ApplyRatings(scorer, scored, failed, firstError);
    }

    /// <summary>把模型结果挂起：整批一致就动芯片，不一致只在提示行写分布。</summary>
    private void ApplyRatings(
        NsfwScorer scorer, List<(int ImageId, NsfwProbs Probs, string Detail)> scored, int failed,
        string? firstError)
    {
        if (_ratingTouched)
        {
            _modelHint.Text = "模型结果未采用（已手动改过分级）";
            Toast("模型结果未采用：跑的过程中手动改过分级");
            return;
        }

        if (scored.Count == 0)
        {
            _modelHint.Text = firstError is null ? "分级没跑出来" : $"分级没跑出来：{firstError}";
            Toast($"分级没跑出来（{failed} 张失败）", ToastKind.Info, firstError);
            return;
        }

        _pendingRatings.Clear();
        foreach (var (imageId, probs, detail) in scored)
        {
            var (_, confidence, margin) = probs.Verdict();
            _pendingRatings[imageId] = new PendingRating(
                probs.Rating, probs.NsfwProb, confidence, margin, detail, scorer.ModelName, scorer.ModelHash);
        }

        var nsfw = scored.Count(s => s.Probs.Rating is "q" or "e");
        var sfw = scored.Count - nsfw;
        if (nsfw == 0 || sfw == 0)
        {
            // 整批同一个分级：照手动点芯片的语义显示（新的 ✓、旧的自动 ✕）
            var tagName = nsfw > 0 ? "NSFW" : "SFW";
            SetState("rating", tagName, TagState.Include);
            var levels = string.Join("、", scored
                .GroupBy(s => s.Probs.Rating)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => scored.Count == 1 ? LevelLabel(g.Key) : $"{LevelLabel(g.Key)} ×{g.Count()}"));
            _modelHint.Text = scored.Count == 1
                ? $"分级模型：{tagName}（{levels}）"
                : $"分级模型：{tagName}（{levels}）· {scored.Count} 张";
        }
        else
        {
            // 结果不一致：芯片表达不了「每张各写各的」，只在提示行写分布
            _modelHint.Text = $"分级模型：SFW {sfw} 张 / NSFW {nsfw} 张（各张按自己的结果写入）";
        }

        var summary = $"分级已重跑：SFW {sfw} 张 / NSFW {nsfw} 张，保存后写入";
        Toast(failed == 0 ? summary : $"{summary}；{failed} 张失败", ToastKind.Info, failed == 0 ? null : firstError);
    }

    private static string LevelLabel(string rating) => rating switch
    {
        "g" => "g 普通",
        "s" => "s 性感",
        "q" => "q 暗示",
        "e" => "e 露骨",
        _ => rating,
    };

    private void StartSpin()
    {
        _spin ??= BuildSpin();
        _spin.Begin();
    }

    private void StopSpin() => _spin?.Stop();

    private Storyboard BuildSpin()
    {
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(spin, _syncRotate);
        Storyboard.SetTargetProperty(spin, "Angle");
        var storyboard = new Storyboard();
        storyboard.Children.Add(spin);
        return storyboard;
    }

    private void OnClosed()
    {
        _closed = true;
        _runCts?.Cancel();
        StopSpin();
    }

    private void Toast(string message, ToastKind kind = ToastKind.Info, string? detail = null)
        => _toast?.Invoke(message, kind, detail);

    private bool Save()
    {
        var ids = _items.Where(i => i.ImageId > 0).Select(i => i.ImageId).ToList();
        if (ids.Count == 0 || AppService.Store is null) return false;
        var store = AppService.Store;

        var include = _entries.Values
            .Where(e => e.State == TagState.Include)
            // 分级互斥：加载时多选取的并集（用户没主动选过）不参与写入，
            // 否则会把 SFW 与 NSFW 同时写进每张图，或给本没有分级的图补上别人的分级
            .Where(e => e.Category != "rating" || e.Chosen)
            .ToList();
        var exclude = _entries.Values.Where(e => e.State == TagState.Exclude).ToList();
        var includePairs = include.Select(e => (e.Category, e.Name)).ToList();
        var excludeNames = exclude.Select(e => e.Name).ToList();
        var profiles = new Dictionary<string, string>(_profiles, StringComparer.OrdinalIgnoreCase);
        var item = _items.Count == 1 ? _items[0] : null;
        var pageUrl = item is null ? null : _pageUrl.Text.Trim();
        var sourceUrl = item is null ? null : _sourceUrl.Text.Trim();
        try
        {
            lock (AppService.DbLock)
            {
                if (excludeNames.Count > 0) store.RemoveTags(ids, excludeNames);
                // 保留每张图原有的其他标签：只对「添加」的集合做按分类覆盖
                foreach (var group in includePairs.GroupBy(p => p.Category))
                {
                    var cat = group.Key;
                    var names = group.Select(p => p.Name).ToList();
                    foreach (var id in ids)
                        store.SetCategorizedTags([id], MergeWithExisting(store, id, cat, names));
                }

                // 挂起的模型分级写在标签之后：NSFW/SFW 标签以模型结果为准
                foreach (var (imageId, pending) in _pendingRatings)
                {
                    store.SetNsfwScore(imageId, "model", pending.Rating, "", pending.NsfwProb,
                        pending.Confidence, pending.Margin, pending.ModelName, pending.ModelHash,
                        pending.Detail, tagIt: true);
                }

                // 用户删掉分级芯片后，图和判定要一起清：图上一个 rating 标签都没有的，
                // 判定就不该留着（否则「没标签却有判定」自相矛盾）；只在本次动过分级时做。
                if (_ratingTouched)
                {
                    foreach (var id in ids)
                    {
                        if (!store.CategorizedTags(id).Any(t => t.Category == "rating"))
                            store.ClearNsfwScore(id);
                    }
                }

                if (profiles.Count > 0)
                {
                    var catalog = new TagCatalogService(store);
                    foreach (var (name, entityKey) in profiles)
                    {
                        var tag = store.AllTags().FirstOrDefault(t =>
                            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (tag is null || tag.EntityKey == entityKey) continue;
                        catalog.SetProfile(tag.Id, tag.ZhName, tag.EnName, entityKey);
                    }
                }

                if (item is not null)
                    store.SetImageOrigins(item.ImageId, pageUrl, sourceUrl);
            }
            if (item is not null)
            {
                item.PageUrl = pageUrl;
                item.SourceUrl = sourceUrl;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把该分类的「要添加的标签」合并进图片现有标签，再整体写回（避免覆盖其他分类）。</summary>
    private static List<(string Category, string Name)> MergeWithExisting(
        StoreService store, int imageId, string category, List<string> addNames)
    {
        var merged = store.CategorizedTags(imageId).ToList();
        merged.RemoveAll(p => p.Category == category);
        foreach (var name in addNames) merged.Add((category, name));
        return merged;
    }
}
