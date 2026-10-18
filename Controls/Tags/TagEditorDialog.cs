// 标签编辑器（阶段 C）：单图/多图统一——给选中的图片增删标签。
// 交互照 Gallery_Old/app/ui/tag_editor.py：搜索框同时搜「图片标签」与「角色词库」，
// 回车把输入当新标签；当前标签按分类分组，点 × 移除；保存才写库。
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
    private readonly StackPanel _chipsHost = new() { Spacing = 8 };
    private readonly StackPanel _resultsHost = new() { Spacing = 2 };
    private readonly TextBox _search = new() { PlaceholderText = "搜索标签或词库，回车直接新建", Width = 420 };
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
    private Panel? _syncHost;
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
            Content = editor.Build(),
        };
        editor.LoadCurrent();
        var result = await dialog.ShowAsync();
        editor.OnClosed();
        if (result != ContentDialogResult.Primary) return false;
        return editor.Save();
    }

    private UIElement Build()
    {
        _search.TextChanged += (_, _) => _ = RefreshResultsAsync();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            var text = _search.Text.Trim();
            if (text.Length == 0) return;
            SetState("custom", text, TagState.Include);
            _search.Text = "";
            e.Handled = true;
        };

        var resultsScroll = new ScrollViewer
        {
            MaxHeight = 168,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _resultsHost,
        };

        return new StackPanel
        {
            Spacing = 10,
            Width = 460,
            Children =
            {
                _search,
                resultsScroll,
                new TextBlock { Text = "当前标签（点标签切换：添加 → 移除 → 取消改动）", FontSize = 13, Opacity = 0.6 },
                new ScrollViewer
                {
                    MaxHeight = 240,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _chipsHost,
                },
                _modelHint,
                _hint,
            },
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
        // 分级例外——只有你在对话框里主动选过的分级才会写库（见 Save 的 Chosen 过滤）
        foreach (var list in details.Values)
            foreach (var tag in list)
                _entries.TryAdd(tag.Name, new Entry { Category = tag.Category, Name = tag.Name });

        RebuildChips();
        _hint.Text = _items.Count == 1
            ? "✓ 添加 · ✕ 移除 · 未标记的不变"
            : $"✓ 添加 · ✕ 移除 · 未标记的不变（应用到 {_items.Count} 张）";
    }

    private void RebuildChips()
    {
        // 重跑按钮是复用的同一个实例（旋转动画挂在它身上），先把它从旧表头上摘下来，
        // 否则第二次 Add 会撞「一个 UIElement 只能有一个父级」。
        _syncHost?.Children.Remove(_syncButton);
        _syncHost = null;
        _chipsHost.Children.Clear();
        if (_entries.Count == 0)
            _chipsHost.Children.Add(new TextBlock { Text = "还没有标签", Opacity = 0.6, FontSize = 13 });

        var groups = new List<(string Category, IReadOnlyList<Entry> Entries)>();
        foreach (var group in _entries.Values.GroupBy(p => p.Category))
            groups.Add((group.Key, group.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()));
        // 「分级」那一行始终渲染：右侧挂重跑按钮，还没有分级标签时也要点得到
        if (groups.All(g => g.Category != "rating")) groups.Add(("rating", []));

        foreach (var group in groups.OrderBy(g => g.Category, StringComparer.Ordinal))
        {
            var row = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(BuildCategoryHeader(group.Category));
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            int count = 0;
            foreach (var entry in group.Entries)
            {
                if (count > 0 && count % 4 == 0)
                {
                    row.Children.Add(line);
                    line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                }
                line.Children.Add(BuildChip(entry));
                count++;
            }
            row.Children.Add(line);
            _chipsHost.Children.Add(row);
        }
    }

    /// <summary>分类表头；「分级」右侧多一个重跑按钮。</summary>
    private UIElement BuildCategoryHeader(string category)
    {
        var label = new TextBlock
        {
            Text = CategoryLabel(category),
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (category != "rating") return label;
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        header.Children.Add(label);
        header.Children.Add(_syncButton);
        _syncHost = header;
        return header;
    }

    private static string CategoryLabel(string category) => category switch
    {
        "character" => "角色",
        "work" => "作品",
        "artist" => "作者",
        "kind" => "类型",
        "rating" => "分级",
        "resolution" => "分辨率",
        "custom" => "自定义",
        _ => category,
    };

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

    private async Task RefreshResultsAsync()
    {
        var query = _search.Text.Trim();
        _resultsHost.Children.Clear();
        if (AppService.Store is null) return;
        var store = AppService.Store;
        var catalog = new TagCatalogService(store);

        List<StoreService.TagInfo> tagHits = [];
        List<TagCatalogService.EntityHit> entityHits = [];
        await Task.Run(() =>
        {
            lock (AppService.DbLock)
            {
                try
                {
                    tagHits = store.SearchTags(query, 8);
                    entityHits = query.Length > 0 ? catalog.SearchEntities(query, 5) : [];
                }
                catch { }
            }
        });
        if (query != _search.Text.Trim()) return;   // 输入已变，丢弃旧结果

        foreach (var tag in tagHits)
        {
            if (_entries.ContainsKey(tag.Name)) continue;
            var label = tag.Display == tag.Name ? tag.Name : $"{tag.Display} · {tag.Name}";
            AddResultRow(label, $"{CategoryLabel(tag.Category)} · 已用 {tag.Count}",
                () =>
                {
                    TouchedIfRating(tag.Category);
                    SetState(tag.Category, tag.Name, TagState.Include);
                });
        }
        foreach (var entity in entityHits)
        {
            var name = entity.Zh.Length > 0 ? entity.Zh : entity.En;
            if (name.Length == 0) continue;
            if (_entries.ContainsKey(name)) continue;
            var category = entity.Kind.Length > 0 ? entity.Kind : "character";
            AddResultRow(name, $"词库 · {CategoryLabel(category)} · {entity.GameKey}", () =>
            {
                SetState(category, name, TagState.Include);
                _profiles[name] = entity.EntityKey;
            });
        }
        if (_resultsHost.Children.Count == 0)
            _resultsHost.Children.Add(new TextBlock
            {
                Text = query.Length == 0 ? "输入以搜索标签或角色词库" : "没有匹配项，回车可直接新建",
                FontSize = 12,
                Opacity = 0.6,
            });
    }

    private void AddResultRow(string label, string hint, Action add)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 14 },
                    new TextBlock { Text = hint, FontSize = 12, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Click += (_, _) =>
        {
            add();
            _search.Text = "";
        };
        _resultsHost.Children.Add(button);
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
