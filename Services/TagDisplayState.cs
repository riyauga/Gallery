using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Gallery.Services;

/// <summary>
/// 标签的人工状态 / 显示名设置。存在 meta 表里，不动旧库结构：
///   meta['tag_state']       = { "标签id": { status, identityKey, originGame, displayMode, displayName, note } }
///   meta['tag_display_mode']= zh_full | zh_short | alias | original | canonical
/// status: "" | manual | resolved | external | original。
/// </summary>
public static class TagDisplayState
{
    public const string StateKey = "tag_state";
    public const string GlobalModeKey = "tag_display_mode";
    public const string DefaultGlobalMode = "zh_full";

    public sealed record State(
        string Status = "",
        string IdentityKey = "",
        string OriginGame = "",
        string DisplayMode = "",
        string DisplayName = "",
        string Note = "")
    {
        [JsonIgnore]
        public bool BlocksAutoAlign =>
            Status is "manual" or "resolved" or "external" or "original";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static Dictionary<int, State> Load(SqliteConnection conn)
    {
        var result = new Dictionary<int, State>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", StateKey);
            var json = cmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(json)) return result;
            var map = JsonSerializer.Deserialize<Dictionary<string, State>>(json, JsonOptions);
            if (map is null) return result;
            foreach (var (key, value) in map)
                if (int.TryParse(key, out var id) && value is not null)
                    result[id] = value;
        }
        catch
        {
            // 坏 JSON 不影响主流程；下一次修复会重写。
        }
        return result;
    }

    public static void Save(SqliteConnection conn, IReadOnlyDictionary<int, State> states,
        SqliteTransaction? tx = null)
    {
        var map = states
            .OrderBy(p => p.Key)
            .ToDictionary(p => p.Key.ToString(), p => p.Value);
        var json = JsonSerializer.Serialize(map, JsonOptions);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO meta(key,value) VALUES($k,$v) " +
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", StateKey);
        cmd.Parameters.AddWithValue("$v", json);
        cmd.ExecuteNonQuery();
    }

    public static string GlobalMode(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", GlobalModeKey);
            var value = cmd.ExecuteScalar() as string;
            return string.IsNullOrWhiteSpace(value) ? DefaultGlobalMode : value.Trim();
        }
        catch
        {
            return DefaultGlobalMode;
        }
    }

    public static void SetGlobalMode(SqliteConnection conn, string mode)
    {
        var clean = (mode ?? "").Trim();
        if (clean.Length == 0) clean = DefaultGlobalMode;
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO meta(key,value) VALUES($k,$v) " +
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", GlobalModeKey);
        cmd.Parameters.AddWithValue("$v", clean);
        cmd.ExecuteNonQuery();
    }

}
