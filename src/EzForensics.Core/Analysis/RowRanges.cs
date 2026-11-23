namespace EzForensics.Core.Analysis;

/// <summary>
/// Row lists written like a spreadsheet range: "12-15" or "1, 15, 16, 21-24" (also "21:24"). Numbers are the 1-based row numbers
/// the grid shows; <see cref="Parse"/> and <see cref="Format"/> work on those numbers.
/// </summary>
public static class RowRanges
{
    /// <summary>
    /// The rows in <paramref name="text"/>, sorted and without repeats; null with <paramref name="error"/> set when the text is not a row list.
    /// <paramref name="max"/> (when given) is the highest allowed row number. Empty text is an empty list.
    /// </summary>
    public static List<long>? Parse(string? text, out string? error, long? max = null)
    {
        error = null;
        var set = new SortedSet<long>();
        foreach (var raw in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            int dash = part.IndexOfAny(new[] { '-', ':' }, 1);
            long a, b;
            if (dash < 0)
            {
                if (!long.TryParse(part, out a)) { error = $"\"{part}\" is not a row number."; return null; }
                b = a;
            }
            else if (!long.TryParse(part[..dash], out a) || !long.TryParse(part[(dash + 1)..], out b))
            {
                error = $"\"{part}\" is not a range like 12-15."; return null;
            }
            if (a < 1 || b < 1) { error = "Row numbers start at 1."; return null; }
            if (b < a) { error = $"\"{part}\" runs backwards."; return null; }
            if (max is { } m && b > m) { error = $"The file has {m:N0} rows; {b:N0} is past the end."; return null; }
            if (b - a > 1_000_000) { error = $"\"{part}\" is too many rows."; return null; }
            for (long r = a; r <= b; r++) set.Add(r);
        }
        return set.ToList();
    }

    /// <summary>"1, 15-16, 21-24": runs of two or more rows become a range.</summary>
    public static string Format(IEnumerable<long> rows)
    {
        var list = rows.Distinct().OrderBy(r => r).ToList();
        var parts = new List<string>();
        for (int i = 0; i < list.Count;)
        {
            int j = i;
            while (j + 1 < list.Count && list[j + 1] == list[j] + 1) j++;
            parts.Add(j > i ? $"{list[i]}-{list[j]}" : list[i].ToString());
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
