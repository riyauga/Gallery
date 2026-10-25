using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gallery.Services;

public sealed record CharacterSkin(string SkinKey, string IdentityKey, string GameKey,
    string CharacterTag, string Zh, string En, string SourceTag)
{
    public string Display => Zh.Length > 0 ? Zh : En + "（待译）";
}

public sealed partial class StoreService
{
    private static readonly Regex SkinTagParts = new(@"^(?<base>[^()]+?)(?<qualifiers>(?:_\([^()]+\))+)$", RegexOptions.Compiled);

    private CharacterSkin? ReadSkin(string key, bool sourceTag = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT s.skin_key,s.identity_key,s.game_key,s.character_tag,s.zh,s.en,m.tag " +
            "FROM character_skins s JOIN skin_source_tags m ON m.skin_key=s.skin_key " +
            (sourceTag ? "WHERE m.source='danbooru' AND m.tag=$k" : "WHERE s.skin_key=$k") + " ORDER BY m.tag";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)) : null;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<CharacterSkin> ReviewSkins(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("review_skins", out var skins) || skins.ValueKind != JsonValueKind.Array) return [];
        return skins.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => ReadSkin(v.GetString()!)).OfType<CharacterSkin>().DistinctBy(s => s.SkinKey).ToList();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<CharacterSkin> SkinsForCharacter(string payload, string name)
        => ReviewSkins(payload).Where(s => SkinOwner(name, s.GameKey) == s.IdentityKey).ToList();

    // 身份已关联时优先使用它；否则只接受词库唯一命中，不按相似名或括号内容猜身份。
    private string SkinOwner(string character, string game)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT ti.identity_key FROM tags t JOIN tag_identities ti ON ti.tag_id=t.id " +
            "JOIN identities i ON i.identity_key=ti.identity_key WHERE t.category='character' AND t.name=$n COLLATE NOCASE AND ti.link_status='linked' " +
            "AND (EXISTS(SELECT 1 FROM catalog_entities e WHERE e.identity_key=i.identity_key AND e.game_key=$g) " +
            "OR NOT EXISTS(SELECT 1 FROM catalog_entities e WHERE e.identity_key=i.identity_key))";
        cmd.Parameters.AddWithValue("$n", character);
        cmd.Parameters.AddWithValue("$g", game);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        using (var r = cmd.ExecuteReader()) while (r.Read()) identities.Add(r.GetString(0));
        if (identities.Count > 0) return identities.Count == 1 ? identities.Single() : "";
        cmd.CommandText = "SELECT 1 FROM tags t JOIN tag_identities ti ON ti.tag_id=t.id WHERE t.category='character' " +
            "AND t.name=$n COLLATE NOCASE AND (ti.link_status='independent' OR ti.locked=1)";
        cmd.Parameters.RemoveAt("$g");
        if (cmd.ExecuteScalar() is not null) return "";
        cmd.CommandText = "SELECT DISTINCT e.identity_key FROM catalog_names n JOIN catalog_entities e ON e.entity_key=n.entity_key " +
            "JOIN identities i ON i.identity_key=e.identity_key WHERE n.term=$term AND e.kind='character' AND e.game_key=$game";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$term", TagCatalogService.Norm(TagCatalogService.BaseName(character)));
        cmd.Parameters.AddWithValue("$game", game);
        using (var r = cmd.ExecuteReader()) while (r.Read()) identities.Add(r.GetString(0));
        return identities.Count == 1 ? identities.Single() : "";
    }

    /// <summary>来源原文保持不变；已登记的皮肤直接复用，新皮肤只在明确换装上下文和本体/变体配对时登记。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public string NormalizeSkinPayload(string payload)
    {
        var root = JsonNode.Parse(payload) as JsonObject ?? throw new JsonException("下载记录不是对象");
        var isDanbooru = root["site"]?.ToString() == "danbooru"
            || Uri.TryCreate(root["page_url"]?.ToString(), UriKind.Absolute, out var page)
                && (page.Host == "danbooru.donmai.us" || page.Host.EndsWith(".danbooru.donmai.us", StringComparison.OrdinalIgnoreCase));
        if (!isDanbooru && root["review_skins"] is null) return payload;
        var tags = IngestReview.Tags(payload, IngestReview.RatingOf(payload));
        var characters = tags["character"];
        var original = characters.ToArray();
        var games = tags["work"];
        var general = (root["tags"] as JsonObject)?["general"] as JsonArray;
        var costume = general?.Any(t => t?.ToString() == "official_alternate_costume") == true;
        using var tx = Transaction();
        var skins = ReviewSkins(payload).Where(s => characters.Any(c => SkinOwner(c, s.GameKey) == s.IdentityKey)).ToList();
        foreach (var tag in original)
        {
            var skin = ReadSkin(tag, sourceTag: true);
            if (skin is null && isDanbooru && costume && games.Count > 0)
            {
                var match = SkinTagParts.Match(tag);
                if (!match.Success) continue;
                var stem = match.Groups["base"].Value;
                var qualifiers = Regex.Matches(match.Groups["qualifiers"].Value, @"_\(([^()]+)\)")
                    .Select(m => m.Groups[1].Value).ToList();
                // 作品限定不属于皮肤；仅接受本体或本体_(作品)后多出的一个限定。
                var workSuffix = qualifiers.Count > 0 && games.Any(g => TagCatalogService.Norm(qualifiers[^1]) == TagCatalogService.Norm(g));
                var matchingGames = games.Where(g => !workSuffix || TagCatalogService.Norm(g) == TagCatalogService.Norm(qualifiers[^1])).ToArray();
                var parent = stem + (workSuffix ? "_(" + qualifiers[^1] + ")" : "");
                if (workSuffix) qualifiers.RemoveAt(qualifiers.Count - 1);
                if (qualifiers.Count != 1 || !original.Contains(parent, StringComparer.OrdinalIgnoreCase)) continue;
                var owners = matchingGames.Select(g => (Game: g, Identity: SkinOwner(parent, g)))
                    .Where(o => o.Identity.Length > 0).Distinct().ToArray();
                if (owners.Length != 1) continue;
                var (game, owner) = owners[0];
                var key = "danbooru:" + tag;
                var en = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(qualifiers[0].Replace('_', ' '));
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO character_skins(skin_key,identity_key,game_key,character_tag,zh,en) " +
                    "VALUES($k,$i,$g,$c,'',$e) ON CONFLICT(skin_key) DO NOTHING; " +
                    "INSERT INTO skin_source_tags(source,tag,skin_key) VALUES('danbooru',$t,$k) ON CONFLICT(source,tag) DO NOTHING";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$i", owner);
                cmd.Parameters.AddWithValue("$g", game);
                cmd.Parameters.AddWithValue("$c", parent);
                cmd.Parameters.AddWithValue("$e", en);
                cmd.Parameters.AddWithValue("$t", tag);
                cmd.ExecuteNonQuery();
                skin = ReadSkin(key);
            }
            if (skin is null || SkinOwner(skin.CharacterTag, skin.GameKey) != skin.IdentityKey) continue;
            characters.RemoveAll(c => string.Equals(c, tag, StringComparison.OrdinalIgnoreCase));
            if (!characters.Any(c => SkinOwner(c, skin.GameKey) == skin.IdentityKey)) characters.Add(skin.CharacterTag);
            if (skins.All(s => s.SkinKey != skin.SkinKey)) skins.Add(skin);
        }
        if (skins.Count == 0 && root["review_skins"] is null) { tx.Success = true; return payload; }
        root["review_tags"] = JsonSerializer.SerializeToNode(tags);
        root["review_skins"] = JsonSerializer.SerializeToNode(skins.Select(s => s.SkinKey));
        tx.Success = true;
        return root.ToJsonString();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void NormalizeIngestSkins(int id)
    {
        var row = IngestRow(id);
        if (row is not { State: "pending" }) return;
        using var tx = Transaction();
        var payload = NormalizeSkinPayload(row.Payload);
        if (payload != row.Payload)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE ingest_queue SET payload=$p WHERE id=$i AND state='pending'";
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.Parameters.AddWithValue("$i", id);
            cmd.ExecuteNonQuery();
        }
        tx.Success = true;
    }

    public void NormalizePendingSkins()
    {
        foreach (var row in IngestRows("pending", int.MaxValue))
        {
            try { NormalizeIngestSkins(row.Id); }
            catch (JsonException ex) { AppService.DebugLog($"skin ingest {row.Id}: {ex.Message}"); }
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetSkinChineseName(string key, string chinese)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE character_skins SET zh=$z WHERE skin_key=$k";
        cmd.Parameters.AddWithValue("$z", TagCatalogService.CanonicalName(chinese));
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
        using var images = _conn.CreateCommand();
        images.CommandText = "SELECT image_id FROM image_skins WHERE skin_key=$k";
        images.Parameters.AddWithValue("$k", key);
        var ids = new List<int>();
        using (var r = images.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt32(0));
        SyncSidecars(ids);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ApplyImageSkins(int imageId, string payload)
    {
        var skins = ReviewSkins(payload);
        using var tx = Transaction();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM image_skins WHERE image_id=$i";
        cmd.Parameters.AddWithValue("$i", imageId);
        cmd.ExecuteNonQuery();
        var characters = CategorizedTags(imageId).Where(t => t.Category == "character").Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var skin in skins)
        {
            var character = characters.FirstOrDefault(c => SkinOwner(c, skin.GameKey) == skin.IdentityKey);
            if (character is null) continue;
            var tagId = EnsureTag(character, "character");
            using var state = _conn.CreateCommand();
            state.CommandText = "SELECT 1 FROM tag_identities WHERE tag_id=$t AND link_status='pending' AND locked=0";
            state.Parameters.AddWithValue("$t", tagId);
            if (state.ExecuteScalar() is not null) SetTagIdentity(tagId, skin.IdentityKey, "linked", source: "skin_owner");
            cmd.CommandText = "INSERT INTO image_skins(image_id,skin_key) VALUES($i,$k) ON CONFLICT DO NOTHING";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$i", imageId);
            cmd.Parameters.AddWithValue("$k", skin.SkinKey);
            cmd.ExecuteNonQuery();
        }
        tx.Success = true;
        tx.Dispose();
        if (_inTransaction == 0) SyncSidecars([imageId]);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<CharacterSkin> ImageSkins(int imageId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT skin_key FROM image_skins WHERE image_id=$i ORDER BY skin_key";
        cmd.Parameters.AddWithValue("$i", imageId);
        var keys = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) keys.Add(r.GetString(0));
        return keys.Select(k => ReadSkin(k)).OfType<CharacterSkin>().ToList();
    }

    private object ExportImageSkins(int imageId) => ImageSkins(imageId).Select(s => new
    {
        skin_key = s.SkinKey, identity_key = s.IdentityKey, game_key = s.GameKey,
        character_tag = s.CharacterTag, zh = s.Zh, en = s.En, source_tag = s.SourceTag,
    }).ToArray();

    // sidecar 携带稳定键与名称，不把皮肤塞回角色别名；旧 sidecar 没有此字段时保持现有关联。
    private void ImportImageSkins(int imageId, JsonElement element)
    {
        if (!element.TryGetProperty("character_skins", out var values) || values.ValueKind != JsonValueKind.Array) return;
        var imported = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) continue;
            string Read(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : "";
            var key = Read("skin_key");
            var owner = Read("identity_key");
            var parent = Read("character_tag");
            var source = Read("source_tag");
            var en = Read("en");
            if (key.Length == 0 || owner.Length == 0 || parent.Length == 0 || source.Length == 0 || en.Length == 0) continue;
            var known = ReadSkin(source, true);
            var byKey = ReadSkin(key);
            if (byKey is not null && (byKey.IdentityKey != owner || byKey.CharacterTag != parent))
                throw new JsonException("皮肤稳定键与已有所属角色冲突，未覆盖已有映射");
            if (known is not null && (known.SkinKey != key || known.IdentityKey != owner))
                throw new JsonException("皮肤来源标签与已有身份冲突，未覆盖已有映射");
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO identities(identity_key,kind,en) VALUES($i,'character',$c) ON CONFLICT(identity_key) DO NOTHING; " +
                "INSERT INTO character_skins(skin_key,identity_key,game_key,character_tag,zh,en) VALUES($k,$i,$g,$c,$z,$e) ON CONFLICT(skin_key) DO UPDATE SET zh=CASE WHEN character_skins.zh='' THEN excluded.zh ELSE character_skins.zh END; " +
                "INSERT INTO skin_source_tags(source,tag,skin_key) VALUES('danbooru',$t,$k) ON CONFLICT(source,tag) DO NOTHING";
            cmd.Parameters.AddWithValue("$i", owner);
            cmd.Parameters.AddWithValue("$c", parent);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$g", Read("game_key"));
            cmd.Parameters.AddWithValue("$z", Read("zh"));
            cmd.Parameters.AddWithValue("$e", en);
            cmd.Parameters.AddWithValue("$t", source);
            cmd.ExecuteNonQuery();
            var tagId = EnsureTag(parent, "character");
            using var state = _conn.CreateCommand();
            state.CommandText = "SELECT identity_key,link_status,locked FROM tag_identities WHERE tag_id=$t";
            state.Parameters.AddWithValue("$t", tagId);
            var linkOwner = true;
            using (var r = state.ExecuteReader())
            {
                if (r.Read())
                {
                    linkOwner = r.GetString(1) == "pending" && r.GetInt32(2) == 0;
                    if (r.GetString(1) == "independent" || r.GetString(0).Length > 0 && r.GetString(0) != owner || r.GetInt32(2) != 0 && r.GetString(0) != owner)
                        throw new JsonException("sidecar 皮肤所属角色与现有标签身份冲突");
                }
            }
            if (linkOwner) SetTagIdentity(tagId, owner, "linked", source: "sidecar_skin");
            using var link = _conn.CreateCommand();
            link.CommandText = "INSERT INTO image_tags(image_id,tag_id) VALUES($i,$t) ON CONFLICT DO NOTHING";
            link.Parameters.AddWithValue("$i", imageId);
            link.Parameters.AddWithValue("$t", tagId);
            link.ExecuteNonQuery();
            imported.Add(key);
        }
        using var clear = _conn.CreateCommand();
        clear.CommandText = "DELETE FROM image_skins WHERE image_id=$i";
        clear.Parameters.AddWithValue("$i", imageId);
        clear.ExecuteNonQuery();
        foreach (var key in imported.Distinct(StringComparer.Ordinal))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO image_skins(image_id,skin_key) VALUES($i,$k) ON CONFLICT DO NOTHING";
            cmd.Parameters.AddWithValue("$i", imageId);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
    }
}
