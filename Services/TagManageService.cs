using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// 标签管理的数据层：按分类/关键字查询、单条详情、新建、改显示名与分类、别名替换、批量删除、重复检测。
/// 写操作一律走 <see cref="StoreService"/>：标签与图片的关联改完后必须重写目录里的 .gallery.json，
/// 否则下次扫描会把删掉的标签又读回来（合并服务也是同样的理由）。
/// </summary>
public static class TagManageService
{
    public sealed record TagRow(
        int Id, string Category, string Name, string Zh, string En,
        string Identity, string LinkStatus, int Usage, int AliasCount);

    public sealed record TagDetail(
        int Id, string Category, string Name, string Zh, string En,
        string Identity, string LinkStatus, string Source, int Usage, IReadOnlyList<string> Aliases);

    /// <summary>同一组里的标签被判为重复，Reason 记录命中的条件，用于在界面上说明依据。</summary>
    public sealed record DuplicateGroup(string Reason, IReadOnlyList<TagRow> Members);

    public static readonly (string Key, string Label)[] AllCategories =
    [
        ("character", "角色"), ("work", "作品"), ("artist", "作者"),
        ("kind", "类型"), ("custom", "自定义"), ("rating", "分级"),
    ];

    /// <summary>允许人工新建的分类；分级由四级判定维护。</summary>
    public static readonly string[] ManualCategories = ["character", "work", "artist", "kind", "custom"];

    public static string Label(string category) =>
        AllCategories.FirstOrDefault(c => c.Key == category).Label ?? category;

    private static readonly string SelectColumns = """
        SELECT t.id,t.category,t.name,COALESCE(p.zh,''),COALESCE(p.en,''),
               COALESCE(i.identity_key,''),COALESCE(i.link_status,''),
               (SELECT COUNT(*) FROM image_tags it WHERE it.tag_id=t.id),
               (SELECT COUNT(*) FROM tag_aliases a WHERE a.tag_id=t.id)
        FROM tags t
        LEFT JOIN tag_profiles p ON p.tag_id=t.id
        LEFT JOIN tag_identities i ON i.tag_id=t.id
        """;

    private const string KeywordFilter = """
        ($q='' OR t.name LIKE '%'||$q||'%' OR COALESCE(p.zh,'') LIKE '%'||$q||'%'
         OR COALESCE(p.en,'') LIKE '%'||$q||'%' OR COALESCE(i.identity_key,'') LIKE '%'||$q||'%'
         OR EXISTS(SELECT 1 FROM tag_aliases a2 WHERE a2.tag_id=t.id AND a2.alias LIKE '%'||$q||'%'))
        """;

    /// <summary>列表查询。keyword 为空即全量；category 为空即全部；includeZero=false 时只留有关联图片的。</summary>
    public static List<TagRow> List(string keyword = "", string category = "", bool includeZero = false)
    {
        keyword = keyword.Trim();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SelectColumns}
            WHERE {KeywordFilter}
              AND ($c='' OR t.category=$c)
              AND ($zero=1 OR EXISTS(SELECT 1 FROM image_tags it2 WHERE it2.tag_id=t.id))
            -- 默认按关联图片数降序：入口进来先看到在用的标签，而不是按分类字母序排在前面的作者。
            ORDER BY 8 DESC,t.category,t.name COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$q", keyword);
        cmd.Parameters.AddWithValue("$c", category ?? "");
        cmd.Parameters.AddWithValue("$zero", includeZero ? 1 : 0);
        using var r = cmd.ExecuteReader();
        var rows = new List<TagRow>();
        while (r.Read())
            rows.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetInt32(8)));
        return rows;
    }

    /// <summary>分类计数，用于分类栏上的数字。跟随当前关键字与「是否显示零使用」。</summary>
    public static Dictionary<string, int> Counts(string keyword = "", bool includeZero = false)
    {
        keyword = keyword.Trim();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.category,COUNT(*)
            FROM tags t
            LEFT JOIN tag_profiles p ON p.tag_id=t.id
            LEFT JOIN tag_identities i ON i.tag_id=t.id
            WHERE {KeywordFilter}
              AND ($zero=1 OR EXISTS(SELECT 1 FROM image_tags it2 WHERE it2.tag_id=t.id))
            GROUP BY t.category
            """;
        cmd.Parameters.AddWithValue("$q", keyword);
        cmd.Parameters.AddWithValue("$zero", includeZero ? 1 : 0);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    public static TagDetail? Get(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.id,t.category,t.name,COALESCE(p.zh,''),COALESCE(p.en,''),
                   COALESCE(i.identity_key,''),COALESCE(i.link_status,''),COALESCE(i.source,''),
                   (SELECT COUNT(*) FROM image_tags it WHERE it.tag_id=t.id)
            FROM tags t
            LEFT JOIN tag_profiles p ON p.tag_id=t.id
            LEFT JOIN tag_identities i ON i.tag_id=t.id
            WHERE t.id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var detail = new TagDetail(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8), []);
        r.Close();
        var aliases = new List<string>();
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT alias FROM tag_aliases WHERE tag_id=$id ORDER BY alias COLLATE NOCASE";
        cmd2.Parameters.AddWithValue("$id", id);
        using var r2 = cmd2.ExecuteReader();
        while (r2.Read()) aliases.Add(r2.GetString(0));
        return detail with { Aliases = aliases };
    }

    /// <summary>新建标签。分类限人工可维护的 5 类；同名同分类已存在时返回 0。</summary>
    public static int Create(StoreService store, string category, string name, string zh, string en)
    {
        category = ManualCategories.Contains(category) ? category : "custom";
        name = name.Trim();
        if (name.Length == 0) return 0;
        if (Exists(category, name)) return 0;
        using var tx = store.Transaction();
        var key = "tk_" + Guid.NewGuid().ToString("N")[..16];
        store.Exec($"INSERT INTO tags(name,category,tag_key) VALUES('{Esc(name)}','{Esc(category)}','{key}')");
        var id = (int)store.Scalar("SELECT last_insert_rowid()");
        store.Exec($"INSERT OR IGNORE INTO tag_identities(tag_id,identity_key,link_status,source,updated_at) " +
                   $"VALUES({id},'','pending','manual',{Now()})");
        store.Exec($"INSERT OR IGNORE INTO tag_profiles(tag_id,zh,en) VALUES({id},'{Esc(zh.Trim())}','{Esc(en.Trim())}')");
        tx.Success = true;
        return id;
    }

    /// <summary>只改分类与显示名；内部名是识别与身份对齐的键，不在这里动。</summary>
    public static void Update(StoreService store, int id, string category, string zh, string en)
    {
        using var tx = store.Transaction();
        if (ManualCategories.Contains(category))
            store.Exec($"UPDATE tags SET category='{Esc(category)}' WHERE id={id}");
        store.Exec($"INSERT INTO tag_profiles(tag_id,zh,en) VALUES({id},'{Esc(zh.Trim())}','{Esc(en.Trim())}') " +
                   "ON CONFLICT(tag_id) DO UPDATE SET zh=excluded.zh,en=excluded.en");
        tx.Success = true;
    }

    /// <summary>整体替换别名集合。空串与重名（忽略大小写）会被丢掉。</summary>
    public static void SetAliases(StoreService store, int id, IReadOnlyList<string> aliases)
    {
        var clean = aliases.Select(a => a.Trim()).Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var tx = store.Transaction();
        store.Exec($"DELETE FROM tag_aliases WHERE tag_id={id}");
        foreach (var alias in clean)
            store.Exec($"INSERT OR IGNORE INTO tag_aliases(tag_id,alias) VALUES({id},'{Esc(alias)}')");
        tx.Success = true;
    }

    /// <summary>批量删标签：移除图片关联、重写这些图片的 sidecar，再删标签行。返回移除的关联数。</summary>
    public static int Delete(StoreService store, IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return 0;
        var images = new HashSet<int>();
        var removed = 0;
        using (var conn = Open())
        {
            foreach (var id in ids)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT image_id FROM image_tags WHERE tag_id=$t";
                cmd.Parameters.AddWithValue("$t", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    images.Add(r.GetInt32(0));
                    removed++;
                }
            }
        }
        FolderService.BackupBeforeChange();
        using (var tx = store.Transaction())
        {
            foreach (var id in ids)
            {
                store.Exec($"DELETE FROM image_tags WHERE tag_id={id}");
                store.Exec($"DELETE FROM tags WHERE id={id}");
            }
            tx.Success = true;
        }
        store.SyncSidecars(images.ToArray());
        return removed;
    }

    // ------------------------------------------------------------------ 重复检测

    /// <summary>
    /// 重复检测。conditions 里的条件是**且**关系：勾两个就必须同时满足才成组。
    /// 可选值：identity（同身份键）、display（显示名相同）、alias（一方别名等于另一方名称）、
    /// contains（同分类名称互相包含，仅人工分类且名称 ≥4 字，避免分辨率标签刷屏）。
    /// </summary>
    public static List<DuplicateGroup> FindDuplicates(IReadOnlyCollection<string> conditions, int limit = 100)
    {
        if (conditions.Count == 0) return [];
        var tags = List(includeZero: true);
        var byId = tags.ToDictionary(t => t.Id);
        var pairs = new List<HashSet<(int, int)>>();

        foreach (var condition in conditions)
        {
            var set = new HashSet<(int, int)>();
            switch (condition)
            {
                case "identity":
                    foreach (var group in tags.Where(t => t.Identity.Length > 0)
                                 .GroupBy(t => t.Identity, StringComparer.Ordinal))
                        AddPairs(set, group.Select(t => t.Id));
                    break;
                case "display":
                    foreach (var group in tags.Where(t => t.Zh.Length > 0 || t.En.Length > 0)
                                 .GroupBy(t => t.Zh.Length > 0 ? "zh:" + t.Zh : "en:" + t.En,
                                     StringComparer.OrdinalIgnoreCase))
                        AddPairs(set, group.Select(t => t.Id));
                    break;
                case "alias":
                {
                    var aliases = AliasMap();
                    var names = tags.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Select(t => t.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
                    foreach (var (alias, owners) in aliases)
                        if (names.TryGetValue(alias, out var named))
                            foreach (var owner in owners.Where(byId.ContainsKey))
                                foreach (var other in named)
                                    if (owner != other) set.Add(Ordered(owner, other));
                    break;
                }
                case "contains":
                {
                    var pool = tags.Where(t => ManualCategories.Contains(t.Category) && t.Name.Length >= 4)
                        .GroupBy(t => t.Category, StringComparer.Ordinal);
                    foreach (var group in pool)
                    {
                        var list = group.ToArray();
                        for (var i = 0; i < list.Length; i++)
                            for (var j = i + 1; j < list.Length; j++)
                            {
                                var a = list[i].Name;
                                var b = list[j].Name;
                                if (a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
                                    b.Contains(a, StringComparison.OrdinalIgnoreCase))
                                    set.Add(Ordered(list[i].Id, list[j].Id));
                            }
                    }
                    break;
                }
            }
            if (set.Count == 0) return [];
            pairs.Add(set);
        }

        var matched = pairs[0];
        for (var i = 1; i < pairs.Count; i++) matched.IntersectWith(pairs[i]);
        if (matched.Count == 0) return [];

        // 并查集把互相命中的标签并成一组，组内按关联图片数降序，第一个就是默认保留项。
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            parent.TryAdd(x, x);
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }
        foreach (var (a, b) in matched) Union(a, b);

        var reason = string.Join(" + ", conditions.Select(c => c switch
        {
            "identity" => "同身份",
            "display" => "显示名相同",
            "alias" => "别名互命",
            "contains" => "名称包含",
            _ => c,
        }));
        var groups = new List<DuplicateGroup>();
        foreach (var group in matched.SelectMany(p => new[] { p.Item1, p.Item2 })
                     .Where(byId.ContainsKey).Distinct().GroupBy(Find))
        {
            var members = group.Select(id => byId[id]).OrderByDescending(t => t.Usage)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (members.Length < 2) continue;
            groups.Add(new DuplicateGroup(reason, members));
            if (groups.Count >= limit) break;
        }
        return groups.OrderByDescending(g => g.Members.Sum(m => m.Usage)).ToList();
    }

    private static void AddPairs(HashSet<(int, int)> set, IEnumerable<int> ids)
    {
        var list = ids.ToArray();
        for (var i = 0; i < list.Length; i++)
            for (var j = i + 1; j < list.Length; j++)
                set.Add(Ordered(list[i], list[j]));
    }

    private static (int, int) Ordered(int a, int b) => a < b ? (a, b) : (b, a);

    private static Dictionary<string, List<int>> AliasMap()
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag_id,alias FROM tag_aliases";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var alias = r.GetString(1).Trim();
            if (alias.Length == 0) continue;
            if (!map.TryGetValue(alias, out var list)) map[alias] = list = [];
            list.Add(r.GetInt32(0));
        }
        return map;
    }

    // ------------------------------------------------------------------ 小工具

    private static bool Exists(string category, string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tags WHERE category=$c AND name=$n COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$c", category);
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string Esc(string value) => value.Replace("'", "''");
}
