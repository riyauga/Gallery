namespace Gallery.Services;

/// <summary>
/// 搜索框历史（存 settings.json 的 <c>search_history</c>）：回车提交时记录一条，
/// 同一条查询只保留一份并挪到最前，最多 <see cref="Limit"/> 条。
/// 只维护列表内容，落盘由调用方决定。
/// </summary>
public static class SearchHistory
{
    public const int Limit = 20;

    /// <summary>记录一次查询；空白查询忽略。</summary>
    public static void Remember(AppSettings settings, string? text)
    {
        var query = (text ?? string.Empty).Trim();
        if (query.Length == 0) return;

        var list = settings.search_history;
        list.RemoveAll(item => string.Equals(item, query, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, query);
        if (list.Count > Limit) list.RemoveRange(Limit, list.Count - Limit);
    }
}
