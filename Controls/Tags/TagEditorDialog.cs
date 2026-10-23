// 标签编辑器（阶段 C）：单图/多图统一——给选中的图片增删标签。
// 分组方式照待审核页（Controls/Review/ReviewPage.cs）：一行一类，左边分类名，
// 右边该类已有标签 + 该类自己的输入框（回车即在该类下新增，候选来自标签库与角色词库）；
// 保存才写库。分辨率由图片自动识别，不在这里编辑。
// 标签编辑方式与归档页一致：每个标签左边一个叉号（悬停亮起，点击即取消该标签），不再有勾/叉三态。
// 分级同归档页：左边显示的那一个 SFW/NSFW 由右侧四级（g/s/q/e）自动算出，不能直接编辑分级标签。
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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Controls.Tags;

public sealed class TagEditorDialog
{
    /// <summary>标签在对话框里的去留：Include=保存后存在；Exclude=保存时删除（点叉号取消的标签）。</summary>
    private enum TagState { Include, Exclude }

    private sealed class Entry
    {
        public required string Category { get; init; }
        public required string Name { get; init; }
        public string Display { get; set; } = "";
        public TagState State { get; set; } = TagState.Include;
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
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.6, Visibility = Visibility.Collapsed };
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

    /// <summary>分级当前显示的等级（g/s/q/e）；空串表示没有可显示的统一分级。</summary>
    private string _rating = "";
    /// <summary>所选图片的分级不一致（有的有、有的没有，或各不相同）。</summary>
    private bool _ratingMixed;
    /// <summary>用户在对话框里点过四级；只有点过才在保存时写分级（避免多选串级）。</summary>
    private bool _ratingChosen;

    private static readonly string[] RatingLevels = ["g", "s", "q", "e"];

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
        ToolTipService.SetToolTip(_syncButton, "自动判断敏感程度（保存后写入）");
        AutomationProperties.SetName(_syncButton, "自动判断敏感程度");
    }

    /// <summary>弹出编辑器；返回是否保存成功。</summary>
    public static async Task<bool> ShowAsync(
        XamlRoot root, IReadOnlyList<PhotoItem> items, Action<string, ToastKind, string?>? toast = null)
    {
        if (items.Count == 0 || DialogService.IsShowing || AppService.IsShuttingDown) return false;
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
        try
        {
            editor.LoadCurrent();
            var result = await DialogService.ShowAsync(dialog);
            if (result != ContentDialogResult.Primary) return false;
            return editor.Save();
        }
        finally { editor.OnClosed(); }
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
                // 只在不入库时用来说一句；平时隐藏，不占提示行
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
            // 浮层滚动条会盖住标签行与输入框的右缘，正文右侧留 16（UI_CONVENTIONS.md R1）。
            Padding = new Thickness(0, 0, 16, 0),
            Content = body,
        };
    }

    private void LoadCurrent()
    {
        _entries.Clear();
        _pendingRatings.Clear();
        _rating = "";
        _ratingMixed = false;
        _ratingChosen = false;
        _ratingTouched = false;
        _modelHint.Text = "";
        _hint.Visibility = Visibility.Collapsed;
        var ids = _items.Where(i => i.ImageId > 0).Select(i => i.ImageId).ToList();
        if (ids.Count == 0)
        {
            _hint.Text = "这些图片还没有入库，无法写标签。";
            _hint.Visibility = Visibility.Visible;
            return;
        }

        var store = AppService.Store;
        var details = store is null ? [] : store.TagDetailsForImages(ids);
        // 多选时取并集：任一张已有该标签就显示（保存按同一份集合应用到每张）。
        // 只收可手工编辑的六类：分辨率由图片自动识别，带进来会在多选保存时串到别的图上。
        // 分级不进 _entries：它由四级自动算出（同归档页），只走 _rating。
        foreach (var list in details.Values)
            foreach (var tag in list)
                if (EditableCategories.Contains(tag.Category) && tag.Category != "rating")
                    _entries.TryAdd(tag.Name, new Entry
                    {
                        Category = tag.Category,
                        Name = tag.Name,
                        Display = tag.Display,
                    });

        LoadRating(store, ids);
        RebuildChips();
    }

    /// <summary>分级用 images.nsfw 的四级：全部一致才显示那一个，否则算「不一致」。</summary>
    private void LoadRating(StoreService? store, List<int> ids)
    {
        var levels = store is null ? new Dictionary<int, string>() : store.NsfwRatings(ids);
        var seen = ids
            .Select(id => ArchiveService.NormalizeRating(levels.GetValueOrDefault(id)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var rated = seen.Where(v => v.Length > 0).ToList();
        _rating = rated.Count == 1 && seen.Count == 1 ? rated[0] : "";
        _ratingMixed = rated.Count > 0 && _rating.Length == 0;
    }

    /// <summary>把当前标签按类重填到各自那一行；输入框与行结构保持不动。</summary>
    private void RebuildChips()
    {
        foreach (var (category, chips) in _rowChips)
        {
            chips.Children.Clear();
            if (category == "rating")
            {
                BuildRatingChips(chips);
                continue;
            }
            foreach (var entry in _entries.Values
                         .Where(e => e.Category == category && e.State == TagState.Include)
                         .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                chips.Children.Add(BuildChip(entry));
        }
    }

    /// <summary>
    /// 分级行照归档页：左边那个标签由四级算出来（g/s→SFW，q/e→NSFW），不能直接编辑；
    /// 右边四个级别互斥，点哪个就把分级改成哪个。
    /// </summary>
    private void BuildRatingChips(Panel chips)
    {
        var label = _rating is "g" or "s" ? "SFW"
            : _rating is "q" or "e" ? "NSFW"
            : _ratingMixed ? "分级不一致" : "未分级";
        chips.Children.Add(new TagChip("rating", label) { IsHitTestVisible = false });

        foreach (var level in RatingLevels)
        {
            var button = new ToggleButton
            {
                Content = RatingText.Level(level),
                IsChecked = _rating == level,
                Padding = new Thickness(9, 4, 9, 4),
                MinWidth = 0,
                MinHeight = 28,
                FontSize = 12,
            };
            ToolTipService.SetToolTip(button, RatingText.Tooltip(level));
            button.Click += (_, _) => ChooseRating(level);
            chips.Children.Add(button);
        }
    }

    private void ChooseRating(string level)
    {
        // 点的就是当前那一级：不重写判定，免得把模型/来源的依据覆盖成「手动」；
        // 重建一次是为了把 ToggleButton 被点掉的勾恢复回来
        if (!_ratingMixed && level == _rating) { RebuildChips(); return; }
        _rating = level;
        _ratingMixed = false;
        _ratingChosen = true;
        TouchedIfRating("rating");
        RebuildChips();
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
        // 分级只能由四级（g/s/q/e）与模型重跑决定，不给手打入口
        if (category != "rating") content.Children.Add(BuildInput(category, title));
        return grid;
    }

    /// <summary>该类自己的输入框：输入或选候选，回车即在该类下新增。</summary>
    private SearchInputBox BuildInput(string category, string title)
    {
        var input = new SearchInputBox
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
                        return TagSearchService.Search(store, category, query)
                            .Select(s => new Suggestion(s.Name, s.Display, s.SearchDisplay, s.EntityKey))
                            .ToList();
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
            SetState(category, name, TagState.Include, chosen?.Display ?? "");
            input.Text = "";
            input.ItemsSource = null;
        };
        return input;
    }

    /// <summary>输入框候选：Key 非空表示来自角色词库；SearchDisplay 只用于候选，Display 仍用于标签贴纸。</summary>
    private sealed record Suggestion(string Name, string Display, string SearchDisplay, string Key)
    {
        public override string ToString() =>
            SearchDisplay.Length > 0 ? SearchDisplay : Display.Length > 0 ? Display : Name;
    }

    /// <summary>标签贴纸：左边一个叉号，点它即取消这个标签（保存时删除）；悬停时光标处的叉号亮起。</summary>
    private UIElement BuildChip(Entry entry) => new TagChip(
        entry.Category, entry.Display.Length > 0 ? entry.Display : entry.Name, () =>
    {
        entry.State = TagState.Exclude;
        RebuildChips();
    });

    /// <summary>手动动过分级 → 模型挂起的结果作废（手动优先，保存只按手动选写标签）。</summary>
    private void TouchedIfRating(string category)
    {
        if (category != "rating") return;
        _ratingTouched = true;
        _pendingRatings.Clear();
    }

    /// <summary>写入一个标签的去留；分级不走这里（由四级单独处理）。</summary>
    private void SetState(string category, string name, TagState state, string display = "")
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) return;
        var cat = category.Length == 0 ? "custom" : category;
        if (display.Length == 0 && (cat is "character" or "work") && AppService.Store is { } store)
            display = store.ResolveTagLabels([(cat, clean)]).GetValueOrDefault((cat, clean), clean);

        if (_entries.TryGetValue(clean, out var existing))
        {
            existing.State = state;
            if (display.Length > 0) existing.Display = display;
        }
        else
        {
            _entries[clean] = new Entry
            {
                Category = cat,
                Name = clean,
                Display = display,
                State = state,
            };
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
        if (!PixaiScorer.LooksConfigured(dir))
        {
            const string message = "还没有选择识别模型";
            _modelHint.Text = message;
            Toast(message);
            return;
        }

        _running = true;
        _syncButton.IsEnabled = false;
        (PixaiScorer? Scorer, string Error) loaded;
        try { loaded = await Task.Run(() => { var model = PixaiScorer.TryCreate(dir, out var message); return (model, message); }); }
        finally { _running = false; _syncButton.IsEnabled = true; }
        if (_closed) return;
        var (scorer, error) = loaded;
        if (scorer is null)
        {
            const string message = "无法加载识别模型";
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
                        MaxDegreeOfParallelism = 1,
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

                            var probs = scorer.Score(item.FullPath, token).Probs;
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
        PixaiScorer scorer, List<(int ImageId, NsfwProbs Probs, string Detail)> scored, int failed,
        string? firstError)
    {
        if (_ratingTouched)
        {
            _modelHint.Text = "未采用自动判断（你手动改过分级）";
            Toast("未采用自动判断");
            return;
        }

        if (scored.Count == 0)
        {
            _modelHint.Text = "自动判断失败";
            ToolTipService.SetToolTip(_modelHint, firstError);
            Toast($"自动判断失败（{failed} 张）", ToastKind.Info, firstError);
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
        var distinct = scored
            .Select(s => ArchiveService.NormalizeRating(s.Probs.Rating))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 1 && distinct[0].Length > 0)
        {
            // 整批同一个等级：左边的分级标签跟着四级一起更新
            _rating = distinct[0];
            _ratingMixed = false;
            _ratingChosen = false;   // 模型结果优先，不再写之前手动点的等级
            RebuildChips();
            var tagName = _rating is "q" or "e" ? "NSFW" : "SFW";
            var levels = string.Join("、", scored
                .GroupBy(s => s.Probs.Rating)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => scored.Count == 1 ? LevelLabel(g.Key) : $"{LevelLabel(g.Key)} ×{g.Count()}"));
            _modelHint.Text = scored.Count == 1
                ? $"自动判断：{tagName}（{levels}）"
                : $"自动判断：{tagName}（{levels}）· {scored.Count} 张";
        }
        else
        {
            // 结果不一致：四级表达不了「每张各写各的」，只在提示行写分布
            _rating = "";
            _ratingMixed = true;
            _ratingChosen = false;
            RebuildChips();
            _modelHint.Text = $"自动判断：SFW {sfw} 张 / NSFW {nsfw} 张";
        }

        var summary = $"已重新判断：SFW {sfw} 张 / NSFW {nsfw} 张，保存后写入";
        Toast(failed == 0 ? summary : $"{summary}；{failed} 张失败", ToastKind.Info, failed == 0 ? null : firstError);
    }

    private static string LevelLabel(string rating) => RatingText.Level(rating);

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

                // 用户点过四级：写判定并顺手同步 SFW/NSFW 标签（与归档页一致，贴纸由等级算出来）
                if (_ratingChosen && _rating.Length > 0)
                {
                    foreach (var id in ids)
                        store.SetNsfwScore(id, "manual", _rating, tagIt: true);
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
            AppService.RequestCatalogAlign();
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
