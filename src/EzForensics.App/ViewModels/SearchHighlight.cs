using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>A piece of cell text, and whether it is a search hit.</summary>
public readonly record struct HitSegment(string Text, bool IsMatch);

/// <summary>Pure helpers for highlighting search hits in grid cells.</summary>
public static class SearchHighlight
{
    /// <summary>Positive words/phrases of a search-box query (no NOT terms). Empty for an empty or invalid query.</summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();
        return SearchQuery.TryParse(query, out var q, out _) && q != null ? q.PositiveTerms() : Array.Empty<string>();
    }

    /// <summary>Splits <paramref name="text"/> into plain and matching pieces (case-insensitive, overlapping hits merged). One plain segment when nothing matches.</summary>
    public static IReadOnlyList<HitSegment> Segments(string? text, IReadOnlyList<string> terms)
    {
        text ??= "";
        if (text.Length == 0) return Array.Empty<HitSegment>();
        var spans = new List<(int Start, int End)>();
        foreach (var t in terms)
        {
            if (string.IsNullOrEmpty(t)) continue;
            int at = 0;
            while (at <= text.Length - t.Length)
            {
                int i = text.IndexOf(t, at, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                spans.Add((i, i + t.Length));
                at = i + t.Length;
            }
        }
        if (spans.Count == 0) return new[] { new HitSegment(text, false) };
        spans.Sort();
        var merged = new List<(int Start, int End)>();
        foreach (var s in spans)
        {
            if (merged.Count > 0 && s.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, s.End));
            else merged.Add(s);
        }
        var result = new List<HitSegment>();
        int pos = 0;
        foreach (var (st, en) in merged)
        {
            if (st > pos) result.Add(new HitSegment(text[pos..st], false));
            result.Add(new HitSegment(text[st..en], true));
            pos = en;
        }
        if (pos < text.Length) result.Add(new HitSegment(text[pos..], false));
        return result;
    }

    public static bool HasMatch(IReadOnlyList<HitSegment> segs) { foreach (var s in segs) if (s.IsMatch) return true; return false; }
}
