using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gallery.Services;

/// <summary>
/// 用户设置，读写 <c>%APPDATA%\Gallery\settings.json</c>。
/// 键集与 galleryold <c>app/config.py:Settings</c> 一致（galleryold 可直接读回）。
/// 新增约定：关窗行为沿用键 <c>minimize_to_tray</c>（true=收托盘，默认）。
/// </summary>
public sealed class AppSettings
{
    public List<string> created_folders { get; set; } = [];
    public List<string> roots { get; set; } = [];
    public string sort_key { get; set; } = "added";
    public bool sort_desc { get; set; } = true;
    public string tag_filter_mode { get; set; } = "or";
    // galleryold 语义：缩略图目标行高。默认取 280（= 图库“中等”档的河流布局目标行高），
    // 与迁移前的默认尺寸一致；旧配置里的 160 仍按“小”档还原。
    public int thumbbar_size { get; set; } = 280;
    public Dictionary<string, JsonElement> layout_options { get; set; } = [];
    public int button_height { get; set; } = 40;
    public string gallery_mode { get; set; } = "justified";
    public string media_filter { get; set; } = "all";   // all | photos | videos（图库筛选器落盘）
    public bool sidebar_collapsed { get; set; }
    public int sidebar_width { get; set; } = 280;
    public bool recursive { get; set; } = true;
    public string open_mode { get; set; } = "photos";
    public string open_custom_exe { get; set; } = "";
    public bool auto_tag_from_folder { get; set; }
    public int auto_tag_depth { get; set; } = 1;
    public bool hide_sidecar { get; set; } = true;
    public bool watch_filesystem { get; set; } = true;
    public int thumb_quality { get; set; } = 82;
    public string inbox_root { get; set; } = "";
    public string inbox_folder { get; set; } = "_inbox";
    public bool bridge_enabled { get; set; } = true;
    public int bridge_port { get; set; } = 47821;
    public string bridge_token { get; set; } = "";
    public string kind_whitelist_path { get; set; } = "";
    public bool auto_open_review { get; set; }
    public int review_poll_seconds { get; set; } = 5;
    public bool blur_nsfw { get; set; } = true;
    public string nsfw_model_dir { get; set; } = "";
    public bool auto_nsfw_tag { get; set; }
    public string nsfw_model_signature { get; set; } = "";
    public string nsfw_display { get; set; } = "normal";
    public int nsfw_blur_strength { get; set; } = 100;
    public bool readonly_index { get; set; } = true;
    public bool sidecar_enabled { get; set; } = true;
    public bool minimize_to_tray { get; set; } = true;
    public string theme { get; set; } = "system";
    public string settings_section { get; set; } = "general";
    public bool auto_archive { get; set; } = true;
    public bool start_at_login { get; set; } = true;
    public bool notify_exceptions { get; set; } = true;
    public bool setup_completed { get; set; }

    /// <summary>上次归档用的根目录与子目录（映射记忆，下次默认带上）。</summary>
    public string archive_root { get; set; } = "";
    public string archive_sub { get; set; } = "";
}

public sealed class AppPaths
{
    /// <summary>测试沙箱重定向（--dataroot）；null = 用户真实数据目录。</summary>
    public static string? Sandbox { get; set; }

    public static string AppDataDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gallery");
    public static string LocalDataDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    public static string FolderMapPath => Path.Combine(AppDataDir, "folder_map.json");
    public static string KindWhitelistPath => Path.Combine(AppDataDir, "kind_whitelist.json");
    public static string DbPath => Path.Combine(LocalDataDir, "library.db");
    public static string ThumbCacheDir => Path.Combine(LocalDataDir, "thumbs");

    /// <summary>重定向到沙箱目录（真实数据零接触）。须在任何使用前调用。</summary>
    public static void ApplySandbox(string dir)
    {
        Sandbox = dir;
        AppDataDir = Path.Combine(dir, "config");
        LocalDataDir = Path.Combine(dir, "local");
    }

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LocalDataDir);
    }
}

/// <summary>settings.json / folder_map.json / kind_whitelist.json 读写。</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AppSettings Settings { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        AppPaths.EnsureDirs();
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(AppPaths.SettingsPath));
                if (loaded is not null) Settings = loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 读取失败保持默认值，不阻塞启动
        }
        Normalize();
    }

    public void Save()
    {
        Normalize();
        var tmp = AppPaths.SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, JsonOpts));
        File.Move(tmp, AppPaths.SettingsPath, overwrite: true);
        Changed?.Invoke();
    }

    private void Normalize()
    {
        var s = Settings;
        s.nsfw_blur_strength = Math.Clamp(s.nsfw_blur_strength, 0, 100);
        s.settings_section = s.settings_section is "general" or "library" or "archive" or "tags" or "sensitive" or "connection"
            ? s.settings_section : "general";
        s.sort_key = SortKeys.IsValid(s.sort_key) ? s.sort_key : "name";
        s.tag_filter_mode = s.tag_filter_mode is "or" or "and" ? s.tag_filter_mode : "or";
        s.sidebar_width = Math.Clamp(s.sidebar_width, 180, 500);
        s.thumbbar_size = Math.Clamp(s.thumbbar_size, 64, 512);
        s.gallery_mode = s.gallery_mode is "square" or "justified" ? s.gallery_mode : "justified";
        s.media_filter = s.media_filter is "photos" or "videos" or "all" ? s.media_filter : "all";
        s.created_folders = s.created_folders.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p).Distinct().ToList();
        s.roots = s.roots.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => FileIdentity.PathKey(p) is { } k ? Path.GetFullPath(p) : p)
            .Distinct(new PathKeyComparer()).ToList();
    }

    private sealed class PathKeyComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) =>
            FileIdentity.PathKey(x ?? "") == FileIdentity.PathKey(y ?? "");
        public int GetHashCode(string obj) => FileIdentity.PathKey(obj).GetHashCode();
    }
}

public static class SortKeys
{
    // galleryold 的 8 个排序键（config.py + main_window.py 排序弹窗）
    public static readonly string[] All =
        ["name", "modified", "taken", "created", "added", "size", "rating", "random"];

    public static bool IsValid(string key) => Array.IndexOf(All, key) >= 0;
}
