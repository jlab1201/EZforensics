using System.Globalization;

namespace EzForensics.Core.Results;

/// <summary>Pure helpers of the Analyze case tree: search filter, hit badges, tool column aggregation, range selection.</summary>
public static class CaseTreeFilter
{
    /// <summary>Content search runs from this many characters on (shorter queries filter by file name only).</summary>
    public const int MinContentQueryLength = 2;

    /// <summary>Hit badge: "1 hit", "1,234 hits", "10,000+ hits"; "" for 0 (a file-name-only match).</summary>
    public static string FormatHits(int count, bool capped, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        if (count <= 0) return "";
        if (capped || count >= CsvKeywordSearch.MaxCount) return CsvKeywordSearch.MaxCount.ToString("N0", culture) + "+ hits";
        return count == 1 ? "1 hit" : count.ToString("N0", culture) + " hits";
    }

    /// <summary>Compact hit pill text ("413", "1,234", "10,000+"); "" for 0. <see cref="FormatHits"/> is its tooltip.</summary>
    public static string FormatHitsBadge(int count, bool capped, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        if (count <= 0) return "";
        if (capped || count >= CsvKeywordSearch.MaxCount) return CsvKeywordSearch.MaxCount.ToString("N0", culture) + "+";
        return count.ToString("N0", culture);
    }

    /// <summary>Case-insensitive file-name match of the search box ("" matches everything).</summary>
    public static bool NameMatches(string name, string? query) =>
        string.IsNullOrWhiteSpace(query) || name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Nodes that stay visible under a filter: every node for which <paramref name="isMatch"/> is true, plus all its ancestors.
    /// </summary>
    public static HashSet<T> Visible<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> children, Func<T, bool> isMatch) where T : notnull
    {
        var visible = new HashSet<T>();
        bool Walk(T node)
        {
            bool any = isMatch(node);
            foreach (var c in children(node)) any |= Walk(c);
            if (any) visible.Add(node);
            return any;
        }
        foreach (var r in roots) Walk(r);
        return visible;
    }

    /// <summary>
    /// Tool column of a folder: the one tool of its files, "" when none is known, "Mixed (n)" for several distinct tools.
    /// </summary>
    public static string AggregateTool(IEnumerable<string?> tools)
    {
        var set = tools.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return set.Count switch { 0 => "", 1 => set[0], _ => $"Mixed ({set.Count})" };
    }

    /// <summary>Shift+click: the items from <paramref name="anchor"/> to <paramref name="target"/> (inclusive) in visible order.</summary>
    public static List<T> Range<T>(IReadOnlyList<T> visibleOrder, T? anchor, T target)
    {
        int b = IndexOf(visibleOrder, target);
        if (b < 0) return new List<T>();
        int a = anchor == null ? -1 : IndexOf(visibleOrder, anchor);
        if (a < 0) return new List<T> { target };
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        return visibleOrder.Skip(lo).Take(hi - lo + 1).ToList();
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T item)
    {
        var eq = EqualityComparer<T>.Default;
        for (int i = 0; i < list.Count; i++) if (eq.Equals(list[i], item)) return i;
        return -1;
    }
}
