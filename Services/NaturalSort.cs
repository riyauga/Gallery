using System.Globalization;
using System.Numerics;
using System.Text;

namespace Gallery.Services;

/// <summary>按旧版 Python 的 Unicode casefold、十进制数字和码点顺序比较文件名。</summary>
public static class NaturalSort
{
    private static readonly (Dictionary<int, string> Folds, Dictionary<int, int> Digits) Tables = LoadTables();

    public static string NameGroup(string name)
        => !string.IsNullOrEmpty(name) && char.IsAsciiLetter(name[0])
            ? char.ToUpperInvariant(name[0]).ToString() : "#";

    public static IReadOnlyList<Part> NaturalKey(string name)
    {
        var parts = new List<Part>();
        var text = new StringBuilder();
        var number = BigInteger.Zero;
        bool inNumber = false;
        foreach (var rune in Casefold(name).EnumerateRunes())
        {
            if (Tables.Digits.TryGetValue(rune.Value, out var digit))
            {
                if (!inNumber)
                {
                    parts.Add(new Part(0, text.ToString(), 0));
                    text.Clear();
                    number = 0;
                    inNumber = true;
                }
                number = number * 10 + digit;
            }
            else
            {
                if (inNumber) parts.Add(new Part(1, null, number));
                inNumber = false;
                text.Append(rune.ToString());
            }
        }
        if (inNumber) parts.Add(new Part(1, null, number));
        parts.Add(new Part(0, text.ToString(), 0));
        return parts;
    }

    public static int CompareNames(string? a, string? b)
    {
        var left = NaturalKey(a ?? "");
        var right = NaturalKey(b ?? "");
        for (int i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            int result = left[i].CompareTo(right[i]);
            if (result != 0) return result;
        }
        return left.Count.CompareTo(right.Count);
    }

    public static string Casefold(string value)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
            result.Append(Tables.Folds.TryGetValue(rune.Value, out var folded) ? folded : rune.ToString());
        return result.ToString();
    }

    /// <summary>Python 的字符串比较按 Unicode 码点，不能用 UTF-16 码元顺序替代。</summary>
    private static int CompareText(string left, string right)
    {
        var a = left.EnumerateRunes().GetEnumerator();
        var b = right.EnumerateRunes().GetEnumerator();
        while (true)
        {
            bool hasA = a.MoveNext(), hasB = b.MoveNext();
            if (!hasA || !hasB) return hasA.CompareTo(hasB);
            int result = a.Current.Value.CompareTo(b.Current.Value);
            if (result != 0) return result;
        }
    }

    private static (Dictionary<int, string>, Dictionary<int, int>) LoadTables()
    {
        using var stream = typeof(NaturalSort).Assembly.GetManifestResourceStream("Gallery.Services.NaturalSortUnicode.txt")
            ?? throw new InvalidOperationException("缺少自然排序的 Unicode 数据。");
        using var reader = new StreamReader(stream);
        var folds = new Dictionary<int, string>();
        var digits = new Dictionary<int, int>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#')) continue;
            var fields = line.Split(';');
            int codepoint = int.Parse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (fields[0] == "F")
                folds.Add(codepoint, string.Concat(fields[2].Split(' ').Select(code =>
                    char.ConvertFromUtf32(int.Parse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture)))));
            else digits.Add(codepoint, int.Parse(fields[2], CultureInfo.InvariantCulture));
        }
        return (folds, digits);
    }

    public readonly record struct Part(int Kind, string? Text, BigInteger Number) : IComparable<Part>
    {
        public int CompareTo(Part other)
            => Kind != other.Kind ? Kind.CompareTo(other.Kind)
                : Kind == 1 ? Number.CompareTo(other.Number) : CompareText(Text ?? "", other.Text ?? "");
    }
}
