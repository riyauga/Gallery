// 标签编辑器（阶段 C）：单图/多图统一——给选中的图片增删标签。
// 交互照 Gallery_Old/app/ui/tag_editor.py：搜索框同时搜「图片标签」与「角色词库」，
// 回车把输入当新标签；当前标签按分类分组，点 × 移除；保存才写库。
// 三态（PLAN 阶段 C）：每个标签可选「选中(添加) / 排除(移除) / 未选」，
// 分级互斥：同一张图的「分级」只能有一个选中，选新的会自动把旧的标为排除。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PhotoItem> _items;
    private readonly StackPanel _chipsHost = new() { Spacing = 8 };
    private readonly StackPanel _resultsHost = new() { Spacing = 2 };
    private readonly TextBox _search = new() { PlaceholderText = "搜索标签或词库，回车直接新建", Width = 420 };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.6 };

    private TagEditorDialog(IReadOnlyList<PhotoItem> items)
    {
        _items = items.ToList();
    }

    /// <summary>弹出编辑器；返回是否保存成功。</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, IReadOnlyList<PhotoItem> items)
    {
        if (items.Count == 0) return false;
        var editor = new TagEditorDialog(items);
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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
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
                _hint,
            },
        };
    }

    private void LoadCurrent()
    {
        _entries.Clear();
        var ids = _items.Where(i => i.ImageId > 0).Select(i => i.ImageId).ToList();
        if (ids.Count == 0)
        {
            _hint.Text = "这些图片还没有入库，无法写标签。";
            return;
        }

        var details = AppService.Store is null
            ? []
            : AppService.Store.TagDetailsForImages(ids);
        // 多选时取并集：任一张已有该标签就显示为已选（保存按同一份集合应用到每张）
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
        _chipsHost.Children.Clear();
        if (_entries.Count == 0)
        {
            _chipsHost.Children.Add(new TextBlock { Text = "还没有标签", Opacity = 0.6, FontSize = 13 });
            return;
        }
        foreach (var group in _entries.Values
                     .GroupBy(p => p.Category)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var row = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock
            {
                Text = CategoryLabel(group.Key),
                FontSize = 12,
                Opacity = 0.6,
            });
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            int count = 0;
            foreach (var entry in group.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
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
            if (entry.State == TagState.Include) entry.State = TagState.Exclude;
            else _entries.Remove(entry.Name);
            RebuildChips();
        };
        return button;
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
        }
        else
        {
            _entries[clean] = new Entry { Category = cat, Name = clean, State = state };
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
                () => SetState(tag.Category, tag.Name, TagState.Include));
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

    private bool Save()
    {
        var ids = _items.Where(i => i.ImageId > 0).Select(i => i.ImageId).ToList();
        if (ids.Count == 0 || AppService.Store is null) return false;
        var store = AppService.Store;

        var include = _entries.Values.Where(e => e.State == TagState.Include).ToList();
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
