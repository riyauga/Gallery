using System.Text.RegularExpressions;

namespace Gallery.Services;

/// <summary>
/// 文件名自然排序，逐行移植 galleryold 的 <c>app/name_sort.py</c>：
/// ASCII 字母分组，组内忽略大小写（casefold）并按数字段数值比较；
/// 非 ASCII 首字符一律归「#」桶。
/// </summary>
public static partial class NaturalSort
{
    [GeneratedRegex(@"(\d+)")]
    private static partial Regex DigitSplit();

    /// <summary>对应 name_group：首字符 ∈ A-Za-z → 大写首字母，否则 '#'。</summary>
    public static string NameGroup(string name)
    {
        var first = string.IsNullOrEmpty(name) ? '\0' : name[0];
        return char.IsAsciiLetter(first)
            ? char.ToUpperInvariant(first).ToString()
            : "#";
    }

    /// <summary>
    /// 对应 natural_key：casefold 后按 (\d+) 切分（保留空段），
    /// 数字段 → (1, 数值)，其余（含空段）→ (0, 文本)。
    /// </summary>
    public static IReadOnlyList<Part> NaturalKey(string name)
    {
        var folded = Casefold(name ?? "");
        var pieces = DigitSplit().Split(folded);
        var parts = new List<Part>(pieces.Length);
        foreach (var seg in pieces)
        {
            if (IsDecimal(seg))
                parts.Add(new Part(1, null, ParseDecimal(seg)));
            else
                parts.Add(new Part(0, seg, 0));
        }
        return parts;
    }

    /// <summary>对应 compare_names：返回 -1/0/1。</summary>
    public static int CompareNames(string? a, string? b)
    {
        var ka = NaturalKey(a ?? "");
        var kb = NaturalKey(b ?? "");
        int n = Math.Min(ka.Count, kb.Count);
        for (int i = 0; i < n; i++)
        {
            int c = ka[i].CompareTo(kb[i]);
            if (c != 0) return c;
        }
        return ka.Count.CompareTo(kb.Count);
    }

    /// <summary>Python str.casefold：简单小写 + 少数 full-folding 特例（ß→ss 等）。</summary>
    public static string Casefold(string s)
    {
        var lowered = s.ToLowerInvariant();
        foreach (var (src, dst) in FullFolds)
        {
            if (lowered.Contains(src)) lowered = lowered.Replace(src, dst);
        }
        return lowered;
    }

    // Full Case Folding 中「简单小写覆盖不到」的常见项（Unicode CaseFolding C+F）。
    private static readonly (string, string)[] FullFolds =
    {
        ("ß", "ss"), ("ﬀ", "ff"), ("ﬁ", "fi"), ("ﬂ", "fl"), ("ﬃ", "ffi"),
        ("ﬄ", "ffl"), ("ﬅ", "st"), ("ﬆ", "st"), ("ﬓ", "մն"), ("ﬔ", "մե"),
        ("ﬕ", "մի"), ("ﬖ", "վն"), ("ﬗ", "մխ"), ("ς", "σ"), ("ẛ", "ṡ"),
        ("ŉ", "ʼn"), ("ǰ", "ǰ"), ("ΐ", "ΐ"), ("ΰ", "ΰ"),
    };

    public readonly record struct Part(int Kind, string? Text, long Number) : IComparable<Part>
    {
        public int CompareTo(Part other)
        {
            // Python 元组比较：先比 kind（数字段 kind=1 排在文本段 kind=0 之后）
            if (Kind != other.Kind) return Kind.CompareTo(other.Kind);
            if (Kind == 1) return Number.CompareTo(other.Number);
            return string.CompareOrdinal(Text ?? "", other.Text ?? "");
        }
    }

    private static bool IsDecimal(string s)
    {
        if (s.Length == 0) return false;
        foreach (var ch in s)
            if (!char.IsDigit(ch)) return false; // char.IsDigit = Unicode Nd，与 str.isdecimal 同类
        return true;
    }

    private static long ParseDecimal(string s)
    {
        // ASCII 快路径；非 ASCII 十进制数字按 Unicode 数值逐位合成（对应 Python int()）
        long value = 0;
        foreach (var ch in s)
        {
            if (ch is >= '0' and <= '9')
                value = value * 10 + (ch - '0');
            else
                value = value * 10 + System.Globalization.CharUnicodeInfo.GetDecimalDigitValue(ch);
        }
        return value;
    }
}
