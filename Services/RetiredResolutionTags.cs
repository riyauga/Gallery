using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gallery.Services;

/// <summary>仅用于旧数据清理与兼容读取，不提供标签生成功能。</summary>
internal static class RetiredResolutionTags
{
    private static string Esc(string value) => value.Replace("'", "''");
    public static bool IsRaw(string value) => value.Trim().StartsWith("分辨率/", StringComparison.Ordinal);

    public static string CleanPayload(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return json;
        return CleanKnownFields(root) ? root.ToJsonString() : json;
    }

    private static bool CleanKnownFields(JsonObject root)
    {
        var changed = CleanEntry(root);
        if (root["catalog"] is JsonObject catalog) changed |= CleanEntry(catalog);
        foreach (var name in new[] { "basic_tags", "tags", "review_tags" })
            if (root[name] is JsonObject tags) changed |= tags.Remove("resolution");
        return changed;
    }

    public static void CleanCurrentPersonalRules()
    {
        try { CleanFile(TagCatalogService.PersonalRulesPath, CleanKnownFields); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { AppService.DebugLog("v12 personal rules cleanup pending: " + ex.Message); }
    }

    private static void CleanFile(string path, Func<JsonObject, bool> clean)
    {
        if (!File.Exists(path)) return;
        var original = File.ReadAllBytes(path);
        var node = JsonNode.Parse(original) as JsonObject ?? throw new JsonException("JSON 根节点不是对象");
        if (!clean(node)) return;
        var attributes = File.GetAttributes(path);
        var backup = path + ".pre-v12.bak";
        if (!File.Exists(backup)) File.WriteAllBytes(backup, original);
        var temp = path + ".v12.tmp";
        try
        {
            File.WriteAllText(temp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            File.SetAttributes(path, attributes);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool CleanEntry(JsonObject entry)
    {
        var changed = false;
        foreach (var field in new[] { "tags", "manual_categories", "classified_tags", "tag_states", "tag_merges" })
        {
            if (entry[field] is not JsonArray values) continue;
            for (var i = values.Count - 1; i >= 0; i--)
            {
                var value = values[i];
                var retired = value is JsonValue text && text.TryGetValue<string>(out var raw)
                    && (field == "manual_categories" ? raw == "resolution" : IsRaw(raw));
                if (value is JsonObject tag)
                {
                    retired |= tag["category"]?.GetValue<string>() == "resolution";
                    retired |= tag["loser"] is JsonArray { Count: > 0 } loser && loser[0]?.GetValue<string>() == "resolution";
                    retired |= tag["winner"] is JsonArray { Count: > 0 } winner && winner[0]?.GetValue<string>() == "resolution";
                }
                if (retired) { values.RemoveAt(i); changed = true; }
            }
        }
        if (entry["aliases"] is JsonObject aliases)
            foreach (var key in aliases.Select(p => p.Key).Where(IsRaw).ToArray()) changed |= aliases.Remove(key);
        return changed;
    }

    public static void CleanPendingSidecars(StoreService store)
    {
        string[] roots;
        lock (store.SyncRoot)
        {
            using var reader = new Db(store.DbPath, readOnly: true);
            using var cmd = reader.Connection.CreateCommand();
            cmd.CommandText = "SELECT root_path FROM retired_tag_sidecars";
            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            roots = list.ToArray();
        }
        foreach (var root in roots)
        {
            if (AppService.IsShuttingDown) return;
            var failed = false;
            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.TryPop(out var directory))
            {
                if (AppService.IsShuttingDown) return;
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) directories.Push(child);
                    var path = Path.Combine(directory, StoreService.SidecarName);
                    lock (store.SyncRoot)
                    {
                        CleanFile(path, node =>
                        {
                            var changed = false;
                            if (node["files"] is JsonObject files)
                                foreach (var file in files.Select(p => p.Value).OfType<JsonObject>()) changed |= CleanEntry(file);
                            return changed;
                        });
                        store.Exec($"DELETE FROM sync_errors WHERE path='{Esc(directory)}' AND message LIKE 'v12:%'");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    failed = true;
                    lock (store.SyncRoot)
                        store.Exec($"INSERT INTO sync_errors(path,message,updated_at) VALUES('{Esc(directory)}','{Esc("v12: " + ex.Message)}',strftime('%s','now')) "
                            + "ON CONFLICT(path) DO UPDATE SET message=excluded.message,updated_at=excluded.updated_at");
                    AppService.DebugLog("v12 sidecar cleanup: " + directory + ": " + ex.Message);
                }
            }
            if (!failed)
                lock (store.SyncRoot) store.Exec($"DELETE FROM retired_tag_sidecars WHERE root_path='{Esc(root)}'");
        }
    }
}
