using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;

namespace Gallery.Services;

public sealed partial class StoreService
{
    private sealed record SourceTagBinding(string Category, string Name, string TagKey,
        string IdentityKey, string EntityKey, string Zh, string En, string[] Aliases);

    private static readonly JsonSerializerOptions SourceTagJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static string SourceTagTerm(string value)
        => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(), @"\s+", "_");

    // 只做精确匹配；同一身份可有多种写法，同名的不同身份仍是歧义。
    private string NormalizePixivTags(string payload)
    {
        if (JsonNode.Parse(payload) is not JsonObject root || root["site"]?.GetValue<string>() != "pixiv"
            || root["source_tags"] is not JsonArray incoming) return payload;

        var known = AllTags();
        var library = new TagCatalogService(this).ReadLibrary();
        var catalogNames = library.Works.Concat(library.Characters)
            .SelectMany(entry => entry.Aliases.Concat([entry.Zh, entry.En])
                .Select(name => (entry.Kind, Name: SourceTagTerm(name), entry.IdentityKey)))
            .ToLookup(value => (value.Kind, value.Name), value => value.IdentityKey);
        var targets = new Dictionary<string, SourceTagBinding>();
        var index = new Dictionary<string, HashSet<string>>();
        void Add(string key, SourceTagBinding target, IEnumerable<string> names)
        {
            targets.TryAdd(key, target);
            foreach (var value in names)
            {
                var term = SourceTagTerm(value);
                if (term.Length == 0) continue;
                if (!index.TryGetValue(term, out var hits)) index[term] = hits = [];
                hits.Add(key);
            }
        }

        foreach (var tag in known.Where(t => t.Category != "rating").OrderByDescending(t => t.Count).ThenBy(t => t.Id))
        {
            var identity = tag.IdentityKey;
            if (identity.Length == 0 && tag.LinkStatus != "independent")
            {
                var identities = catalogNames[(tag.Category, SourceTagTerm(tag.Name))].Distinct().ToArray();
                if (identities.Length == 1) identity = identities[0];
            }
            var key = identity.Length > 0 && tag.LinkStatus != "independent"
                ? tag.Category + ":" + identity : "tag:" + tag.TagKey;
            Add(key, new(tag.Category, tag.Name, tag.TagKey, tag.IdentityKey, tag.EntityKey,
                tag.ZhName, tag.EnName, []), tag.SearchTerms);
        }
        foreach (var entry in library.Works.Concat(library.Characters))
        {
            Add(entry.Kind + ":" + entry.IdentityKey,
                new(entry.Kind, entry.En.Length > 0 ? SourceTagTerm(entry.En) : entry.Name, "", entry.IdentityKey,
                    entry.EntityKey, entry.Zh, entry.En, []),
                entry.Aliases.Concat([entry.Zh, entry.En]));
        }
        // 共享身份的其他资料出处也可能有独有别名，不只读取代表条目的名称。
        using (var names = _conn.CreateCommand())
        {
            names.CommandText = "SELECT kind,identity_key,zh,en,aliases FROM catalog_effective_entities";
            using var rows = names.ExecuteReader();
            while (rows.Read())
            {
                var key = rows.GetString(0) + ":" + rows.GetString(1);
                if (targets.TryGetValue(key, out var target))
                    Add(key, target, new[] { rows.GetString(2), rows.GetString(3) }
                        .Concat(JsonSerializer.Deserialize<string[]>(rows.GetString(4)) ?? []));
            }
        }

        var bindings = new Dictionary<string, SourceTagBinding>();
        foreach (var item in incoming.OfType<JsonObject>())
        {
            var name = item["name"]?.GetValue<string>()?.Trim() ?? "";
            if (name.Length == 0) continue;
            var translations = item["translations"] as JsonObject;
            var aliases = new[] { name }.Concat(translations?.Select(p => p.Value?.GetValue<string>() ?? "") ?? [])
                .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var hits = aliases.Select(SourceTagTerm).Where(index.ContainsKey)
                .SelectMany(term => index[term]).Distinct().ToArray();
            var key = hits.Length == 1 ? hits[0] : "custom:" + SourceTagTerm(name);
            string Translation(string language) => translations?.FirstOrDefault(p =>
                p.Key.Equals(language, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>() ?? "";
            var target = hits.Length == 1 ? targets[hits[0]] : new SourceTagBinding("custom", name, "", "", "", "", "", []);
            if (bindings.TryGetValue(key, out var previous)) target = previous;
            var zh = Translation("zh");
            if (zh.Length == 0) zh = Translation("zh-cn");
            bindings[key] = target with
            {
                Zh = target.Zh.Length > 0 ? target.Zh : zh,
                En = target.En.Length > 0 ? target.En : Translation("en"),
                Aliases = target.Aliases.Concat(aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        // artist 是站点明确提供的作者；来源标签的旧客户端 custom 兜底在此替换。
        var tags = new JsonObject { ["artist"] = root["tags"]?["artist"]?.DeepClone() ?? new JsonArray() };
        foreach (var group in bindings.Values.GroupBy(t => t.Category))
        {
            var values = tags[group.Key] as JsonArray ?? new JsonArray();
            foreach (var binding in group)
                if (!values.Any(v => v?.GetValue<string>() == binding.Name)) values.Add(binding.Name);
            tags[group.Key] = values;
        }
        root["tags"] = tags;
        root["source_tag_bindings"] = JsonSerializer.SerializeToNode(bindings.Values, SourceTagJson);
        return root.ToJsonString();
    }

    // 在最终写入时才创建标签；审核已删掉的项不创建，也不写来源别名。
    private Dictionary<(string Category, string Name), List<int>> PrepareSourceTags(string? payload,
        IReadOnlyCollection<(string Category, string Name)> retained)
    {
        var result = new Dictionary<(string, string), List<int>>();
        if (payload is null || JsonNode.Parse(payload) is not JsonObject root
            || root["source_tag_bindings"] is not JsonArray array) return result;
        var bindings = array.Deserialize<SourceTagBinding[]>(SourceTagJson) ?? [];
        foreach (var binding in bindings.Where(b => retained.Contains((b.Category, b.Name))))
        {
            int id;
            using (var find = _conn.CreateCommand())
            {
                // 稳定键优先；custom 只查本分类，不经旧 EnsureTag 的角色别名重定向。
                find.CommandText = binding.TagKey.Length > 0
                    ? "SELECT id FROM tags WHERE tag_key=$key AND category=$category"
                    : binding.IdentityKey.Length > 0
                        ? "SELECT t.id FROM tags t JOIN tag_identities i ON i.tag_id=t.id WHERE i.identity_key=$key AND t.category=$category AND i.link_status='linked' ORDER BY t.id LIMIT 1"
                        : "SELECT id FROM tags WHERE name=$key COLLATE NOCASE AND category=$category ORDER BY id LIMIT 1";
                find.Parameters.AddWithValue("$key", binding.TagKey.Length > 0 ? binding.TagKey
                    : binding.IdentityKey.Length > 0 ? binding.IdentityKey : binding.Name);
                find.Parameters.AddWithValue("$category", binding.Category);
                id = Convert.ToInt32(find.ExecuteScalar() ?? 0);
            }
            var created = id == 0;
            if (created)
            {
                using var insert = _conn.CreateCommand();
                insert.CommandText = "INSERT INTO tags(name,category,tag_key) VALUES($name,$category,$key); SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("$name", binding.Name);
                insert.Parameters.AddWithValue("$category", binding.Category);
                insert.Parameters.AddWithValue("$key", NewTagKey());
                id = Convert.ToInt32(insert.ExecuteScalar());
                using var identity = _conn.CreateCommand();
                identity.CommandText = "INSERT INTO tag_identities(tag_id,identity_key,link_status,source,locked,note,updated_at) VALUES($id,$key,$status,'pixiv',0,'',$now)";
                identity.Parameters.AddWithValue("$id", id);
                identity.Parameters.AddWithValue("$key", binding.IdentityKey);
                identity.Parameters.AddWithValue("$status", binding.IdentityKey.Length > 0 ? "linked" : "pending");
                identity.Parameters.AddWithValue("$now", Now());
                identity.ExecuteNonQuery();
                if (binding.IdentityKey.Length > 0)
                {
                    using var entity = _conn.CreateCommand();
                    entity.CommandText = "INSERT OR IGNORE INTO identities(identity_key,kind,zh,en,primary_entity_key,created_at,updated_at) VALUES($key,$kind,$zh,$en,$entity,$now,$now)";
                    entity.Parameters.AddWithValue("$key", binding.IdentityKey);
                    entity.Parameters.AddWithValue("$kind", binding.Category);
                    entity.Parameters.AddWithValue("$zh", binding.Zh);
                    entity.Parameters.AddWithValue("$en", binding.En);
                    entity.Parameters.AddWithValue("$entity", binding.EntityKey);
                    entity.Parameters.AddWithValue("$now", Now());
                    entity.ExecuteNonQuery();
                }
            }
            using (var profile = _conn.CreateCommand())
            {
                // 只补缺失译名，已有显示名、人工资料和身份关联保持原值。
                profile.CommandText = """
                    INSERT INTO tag_profiles(tag_id,zh,en,entity_key) VALUES($id,$zh,$en,$entity)
                    ON CONFLICT(tag_id) DO UPDATE SET
                    zh=CASE WHEN COALESCE(tag_profiles.zh,'')='' THEN excluded.zh ELSE tag_profiles.zh END,
                    en=CASE WHEN COALESCE(tag_profiles.en,'')='' THEN excluded.en ELSE tag_profiles.en END
                    """;
                profile.Parameters.AddWithValue("$id", id);
                profile.Parameters.AddWithValue("$zh", binding.Zh);
                profile.Parameters.AddWithValue("$en", binding.En);
                profile.Parameters.AddWithValue("$entity", created ? binding.EntityKey : "");
                profile.ExecuteNonQuery();
            }
            foreach (var alias in binding.Aliases.Where(a => a != binding.Name))
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO tag_aliases(tag_id,alias) VALUES($id,$alias)";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$alias", alias);
                cmd.ExecuteNonQuery();
            }
            var key = (binding.Category, binding.Name);
            if (!result.TryGetValue(key, out var ids)) result[key] = ids = [];
            ids.Add(id);
        }
        return result;
    }

    internal static void ApplySourceTagLabels(string payload, Dictionary<(string Category, string Name), string> labels)
    {
        if (JsonNode.Parse(payload)?["source_tag_bindings"] is not JsonArray array) return;
        foreach (var binding in array.Deserialize<SourceTagBinding[]>(SourceTagJson) ?? [])
        {
            var key = (binding.Category, binding.Name);
            if (labels.TryGetValue(key, out var current) && current != binding.Name) continue;
            labels[key] = binding.Zh.Length > 0 ? binding.Zh : binding.En.Length > 0 ? binding.En : binding.Name;
        }
    }
}
