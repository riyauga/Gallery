namespace Gallery.Services;

/// <summary>分级显示文案：内部键仍是 g/s/q/e，界面上只出现中文。</summary>
public static class RatingText
{
    public static string Level(string? key) => key switch
    {
        "g" => "普通",
        "s" => "性感",
        "q" => "暗示",
        "e" => "露骨",
        _ => "未分级",
    };

    public static string Badge(string? key) => key is "g" or "s" ? "SFW"
        : key is "q" or "e" ? "NSFW"
        : "未分级";

    public static string Tooltip(string? key) => key switch
    {
        "g" => "普通 · SFW（安全内容）",
        "s" => "性感 · SFW（安全内容）",
        "q" => "暗示 · NSFW（敏感内容）",
        "e" => "露骨 · NSFW（敏感内容）",
        _ => "未分级",
    };
}
