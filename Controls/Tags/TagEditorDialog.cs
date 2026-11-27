// 标签编辑器（阶段 C）：单图/多图统一——给选中的图片增删标签。
// 分组方式照待审核页（Controls/Review/ReviewPage.cs）：一行一类，左边分类名，
// 右边该类已有标签 + 该类自己的输入框（回车即在该类下新增，候选来自标签库与角色词库）；
// 保存才写库。
// 标签编辑方式与归档页一致：每个标签左边一个叉号（悬停亮起，点击即取消该标签），不再有勾/叉三态。
// 分级同归档页：左边显示的那一个 SFW/NSFW 由右侧四级（g/s/q/e）自动算出，不能直接编辑分级标签。
// AI 分析逐图更新草稿，保存才写库。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
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
    /// <summary>逐图记录原有角色：只有本次手工新增的角色才顺带补作品。</summary>
    private readonly Dictionary<int, HashSet<string>> _initialCharacters = new();
    private readonly Dictionary<string, string> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, PendingRating> _pendingRatings = new();
    private readonly List<PhotoItem> _items;
    private readonly StackPanel _rowsHost = new() { Spacing = 12 };
    private readonly ContentControl _rowsGate = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    /// <summary>每类一行的芯片容器（行只建一次：改标签时只重填芯片，输入框不失焦）。</summary>
    private readonly Dictionary<string, Panel> _rowChips = new(StringComparer.Ordinal);
    private readonly TextBox _pageUrl = new() { PlaceholderText = "https://来源页面/帖子地址", Width = 420, FontSize = 13 };
    private readonly TextBox _sourceUrl = new() { PlaceholderText = "https://原图地址", Width = 420, FontSize = 13 };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.6, Visibility = Visibility.Collapsed };
    private readonly TextBlock _modelHint = new() { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
    private readonly Button _analyzeButton = new() { Content = "AI分析", HorizontalAlignment = HorizontalAlignment.Right };
    private ContentDialog? _dialog;
    private readonly Dictionary<int, List<(string Category, string Name)>> _drafts = new();
    private readonly Dictionary<string, Entry> _manualChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _ratingDrafts = new();
    private readonly Dictionary<int, string> _manualRatings = new();
    private readonly Action<string, ToastKind, string?>? _toast;

    private CancellationTokenSource? _runCts;
    private bool _running;
    private bool _closed;

    /// <summary>分级当前显示的等级（g/s/q/e）；空串表示没有可显示的统一分级。</summary>
    private string _rating = "";
    /// <summary>所选图片的分级不一致（有的有、有的没有，或各不相同）。</summary>
    private bool _ratingMixed;
    private static readonly string[] RatingLevels = ["g", "s", "q", "e"];

    private TagEditorDialog(IReadOnlyList<PhotoItem> items, Action<string, ToastKind, string?>? toast)
    {
        _items = items.ToList();
        _toast = toast;
        _rowsGate.Content = _rowsHost;

        _analyzeButton.Click += (_, _) => _ = AnalyzeAsync();
        ToolTipService.SetToolTip(_analyzeButton, "角色、作品、分级非空覆盖；类型去重追加。保存后写入");

    }

    /// <summary>弹出编辑器；返回是否保存成功。</summary>
    public static async Task<bool> ShowAsync(
        XamlRoot root, IReadOnlyList<PhotoItem> items, Action<string, ToastKind, string?>? toast = null)
    {
        if (items.Count == 0 || DialogService.IsShowing || AppService.IsShuttingDown) return false;
        var editor = new TagEditorDialog(items, toast);
        var dialog = new ContentDialog
        {
            Title = editor.BuildTitle(items.Count == 1 ? "编辑标签" : $"编辑标签（{items.Count} 张）"),
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            Content = editor.Build(root),
        };
        editor._dialog = dialog;
        dialog.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Escape) return;
            args.Handled = true;
            dialog.Hide();
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

    private UIElement BuildTitle(string title)
    {
        var header = new Grid { Width = 460, ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_analyzeButton, 1);
        header.Children.Add(_analyzeButton);
        return header;
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
                _rowsGate,
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
            // 浮层滚动条会盖住标签行与输入框的右缘，正文右侧留 16（UI_INTERACTION.md R1）。
            Padding = new Thickness(0, 0, 16, 0),
            Content = body,
        };
    }

    private void LoadCurrent()
    {
        _entries.Clear();
        _initialCharacters.Clear();
        _pendingRatings.Clear();
        _rating = "";
        _ratingMixed = false;
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
        // 并集仅用于展示；保存按每张图自己的草稿和明确的手工增删计算。
        foreach (var id in ids)
        {
            _drafts[id] = details.TryGetValue(id, out var list)
                ? list.Where(t => t.Category != "rating").Select(t => (t.Category, t.Name)).ToList() : [];
            _initialCharacters[id] = _drafts[id].Where(t => t.Category == "character")
                .Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        // 分级不进 _entries：它由四级自动算出（同归档页），只走 _rating。
        foreach (var list in details.Values)
            foreach (var tag in list)
                if (EditableCategories.Contains(tag.Category) && tag.Category != "rating")
                {
                    _entries.TryAdd(tag.Name, new Entry
                    {
                        Category = tag.Category,
                        Name = tag.Name,
                        Display = tag.Display,
                    });
                }

        LoadRating(store, ids);
        RebuildChips();
    }

    /// <summary>分级用 images.nsfw 的四级：全部一致才显示那一个，否则算「不一致」。</summary>
    private void LoadRating(StoreService? store, List<int> ids)
    {
        var levels = store is null ? new Dictionary<int, string>() : store.NsfwRatings(ids);
        foreach (var id in ids) _ratingDrafts[id] = ArchiveService.NormalizeRating(levels.GetValueOrDefault(id));
        var seen = ids
            .Select(id => _ratingDrafts[id])
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
        _pendingRatings.Clear();
        foreach (var id in _ratingDrafts.Keys.ToArray())
        {
            _ratingDrafts[id] = level;
            _manualRatings[id] = level;
        }
        RebuildChips();
    }

    /// <summary>可手工编辑类别，顺序沿用 Taxonomy。</summary>
    private static readonly string[] EditableCategories =
        Taxonomy.Categories;

    private static string CategoryTitle(string category)
    {
        var index = Array.IndexOf(Taxonomy.Categories, category);
        return index >= 0 ? Taxonomy.CategoryTitles[index] : category;
    }

    /// <summary>一行一类：左侧分类名，右侧该类标签和输入框。</summary>
    private UIElement BuildCategoryRow(string category)
    {
        var title = CategoryTitle(category);
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12, Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0),
        });

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
            EnableSuggestionTabNavigation = true,
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
    private UIElement BuildChip(Entry entry)
    {
        var chip = new TagChip(entry.Category, entry.Display.Length > 0 ? entry.Display : entry.Name, () =>
        {
            SetState(entry.Category, entry.Name, TagState.Exclude, entry.Display);
        });
        chip.Label.IsTextSelectionEnabled = true;
        return chip;
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
        _manualChanges[clean] = _entries[clean];
        RebuildChips();
    }

    // ------------------------------------------------------------ 主动 AI 分析

    private async Task AnalyzeAsync()
    {
        if (_running || _closed) return;
        var targets = _items.Where(i => i.ImageId > 0 && !string.IsNullOrEmpty(i.FullPath)).ToList();
        if (targets.Count == 0) return;
        _running = true;
        _analyzeButton.IsEnabled = false;
        _rowsGate.IsEnabled = false;
        if (_dialog is not null) _dialog.IsPrimaryButtonEnabled = false;
        var cts = new CancellationTokenSource();
        _runCts = cts;
        _modelHint.Text = "正在分析…";
        try
        {
            var results = await Task.Run(() =>
            {
                var scorer = PixaiScorer.TryCreate(AppService.Settings.Settings.nsfw_model_dir, out var error)
                    ?? throw new InvalidOperationException(error);
                var scores = new List<(int Id, PixaiScore Score)>();
                var failures = new List<string>();
                foreach (var item in targets)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try { scores.Add((item.ImageId, scorer.Score(item.FullPath, cts.Token))); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failures.Add(item.Name + ": " + ex.Message); }
                }
                return (Scores: scores, Failures: failures,
                    Whitelist: Taxonomy.LoadKindWhitelist(AppService.Settings.Settings.kind_whitelist_path));
            }, cts.Token);
            if (_closed) return;
            foreach (var (id, score) in results.Scores)
            {
                var tags = EditableCategories.ToDictionary(c => c,
                    c => _drafts[id].Where(t => t.Category == c).Select(t => t.Name).ToList());
                ActiveAiTags.Apply(tags, score, results.Whitelist);
                _drafts[id] = tags.Where(p => p.Key != "rating")
                    .SelectMany(p => p.Value.Select(n => (p.Key, n))).ToList();
                if (score.Rating.Length == 0) continue;
                var probs = score.Probs;
                var (_, confidence, margin) = probs.Verdict();
                _pendingRatings[id] = new(score.Rating, probs.NsfwProb, confidence, margin,
                    JsonSerializer.Serialize(new { g = probs.G, s = probs.S, q = probs.Q, e = probs.E }),
                    score.ModelName, score.ModelHash);
                _manualRatings.Remove(id);
                _ratingDrafts[id] = score.Rating;
            }
            RefreshDraftEntries();
            var levels = _ratingDrafts.Values.Distinct().ToList();
            _rating = levels.Count == 1 ? levels[0] : "";
            _ratingMixed = levels.Count > 1;
            RebuildChips();
            _modelHint.Text = $"已分析 {results.Scores.Count} 张，保存后写入"
                + (results.Failures.Count > 0 ? $"；{results.Failures.Count} 张失败，保留原内容" : "");
            ToolTipService.SetToolTip(_modelHint, string.Join("\n", results.Failures));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed) { _modelHint.Text = "分析失败，保留原内容"; Toast(_modelHint.Text, ToastKind.Info, ex.Message); }
        }
        finally
        {
            _running = false;
            _runCts = null;
            cts.Dispose();
            if (!_closed)
            {
                _analyzeButton.IsEnabled = true;
                _rowsGate.IsEnabled = true;
                if (_dialog is not null) _dialog.IsPrimaryButtonEnabled = true;
            }
        }
    }

    private void RefreshDraftEntries()
    {
        var displays = _entries.ToDictionary(p => p.Key, p => p.Value.Display, StringComparer.OrdinalIgnoreCase);
        _entries.Clear();
        foreach (var (category, name) in _drafts.Values.SelectMany(t => t))
            _entries.TryAdd(name, new Entry { Category = category, Name = name, Display = displays.GetValueOrDefault(name, "") });
        foreach (var (name, entry) in _manualChanges) _entries[name] = entry;
        if (AppService.Store is { } store)
        {
            var labels = store.ResolveTagLabels(_entries.Values.Where(e => e.Category is "work" or "character")
                .Select(e => (e.Category, e.Name)));
            foreach (var entry in _entries.Values)
                if (labels.TryGetValue((entry.Category, entry.Name), out var label)) entry.Display = label;
        }
    }

    private void OnClosed()
    {
        _closed = true;
        _runCts?.Cancel();
    }

    private void Toast(string message, ToastKind kind = ToastKind.Info, string? detail = null)
        => _toast?.Invoke(message, kind, detail);

    private bool Save()
    {
        var ids = _items.Where(i => i.ImageId > 0).Select(i => i.ImageId).ToList();
        if (ids.Count == 0 || AppService.Store is null) return false;
        var store = AppService.Store;

        if (_running) return false;
        var include = _manualChanges.Values
            .Where(e => e.State == TagState.Include)
            .ToList();
        var exclude = _manualChanges.Values.Where(e => e.State == TagState.Exclude).ToList();
        var excludeNames = exclude.Select(e => e.Name).ToList();
        var profiles = new Dictionary<string, string>(_profiles, StringComparer.OrdinalIgnoreCase);
        var item = _items.Count == 1 ? _items[0] : null;
        var pageUrl = item is null ? null : _pageUrl.Text.Trim();
        var sourceUrl = item is null ? null : _sourceUrl.Text.Trim();
        var autoWork = new Dictionary<int, List<string>>();
        try
        {
            lock (AppService.DbLock)
            {
                foreach (var id in ids)
                {
                    var draft = _drafts[id].ToList();
                    foreach (var entry in _manualChanges.Values)
                    {
                        draft.RemoveAll(p => p.Category == entry.Category && string.Equals(p.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                        if (entry.State == TagState.Include) draft.Add((entry.Category, entry.Name));
                    }
                    _drafts[id] = draft;
                }
                autoWork = SuggestWorksForNewCharacters(store, ids, include, excludeNames);
                foreach (var id in ids)
                {
                    var pairs = _drafts[id].ToList();
                    if (autoWork.TryGetValue(id, out var extra))
                        pairs.AddRange(extra.Select(name => ("work", name)));
                    // 分级判定单独保存；此处完整保留每图已有的分级标签。
                    pairs.AddRange(store.CategorizedTags(id).Where(p => p.Category == "rating"));
                    store.SetCategorizedTags([id], pairs);
                }

                // 挂起的模型分级写在标签之后：NSFW/SFW 标签以模型结果为准
                foreach (var (imageId, pending) in _pendingRatings)
                {
                    store.SetNsfwScore(imageId, "model", pending.Rating, "", pending.NsfwProb,
                        pending.Confidence, pending.Margin, pending.ModelName, pending.ModelHash,
                        pending.Detail, tagIt: true);
                }

                foreach (var (id, rating) in _manualRatings)
                    store.SetNsfwScore(id, "manual", rating, tagIt: true);

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
            if (autoWork.Count > 0)
            {
                var added = string.Join("、", autoWork.Values.SelectMany(names => names)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                _toast?.Invoke($"已按角色补上作品：{added}", ToastKind.Success, null);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 本次新加的角色标签 → 顺带补该角色所属作品。逐张图判断：图里已有该身份的任何作品标签就跳过；
    /// 用户刚点掉的标签不补。返回「图片 id → 要补的作品标签名」。
    /// </summary>
    private Dictionary<int, List<string>> SuggestWorksForNewCharacters(
        StoreService store, List<int> ids, List<Entry> include, List<string> excludeNames)
    {
        var added = include
            .Where(e => e.Category == "character")
            .Select(e => (e.Name, Key: _profiles.GetValueOrDefault(e.Name, "")))
            .ToList();
        var result = new Dictionary<int, List<string>>();
        if (added.Count == 0) return result;

        var catalog = new TagCatalogService(store);
        var excluded = new HashSet<string>(excludeNames, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var current = _drafts[id]
                .Where(pair => pair.Category == "work")
                .Select(pair => pair.Name)
                .ToList();
            var add = new List<string>();
            foreach (var (name, key) in added)
            {
                if (_initialCharacters[id].Contains(name)) continue;
                string? work;
                try { work = catalog.SuggestWorkTag(name, key, [.. current, .. add]); }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    AppService.DebugLog("suggest work: " + ex.Message);
                    continue;
                }
                if (string.IsNullOrEmpty(work) || excluded.Contains(work)) continue;
                if (current.Contains(work, StringComparer.OrdinalIgnoreCase)) continue;
                if (add.Contains(work, StringComparer.OrdinalIgnoreCase)) continue;
                add.Add(work);
            }
            if (add.Count > 0) result[id] = add;
        }
        return result;
    }

}
