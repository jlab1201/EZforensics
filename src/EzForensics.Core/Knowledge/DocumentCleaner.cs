using System.Text.RegularExpressions;

namespace EzForensics.Core.Knowledge;

public sealed record CleanResult(string Text, string Title, double CleanRatio);

/// <summary>Tidies extracted document text before chunking: drops repeating page headers/footers and page numbers, joins hyphenated line breaks.</summary>
public static class DocumentCleaner
{
    private static readonly Regex PageNumber = new(@"^\s*(page\s+)?\d+(\s+of\s+\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Hyphen = new(@"(\p{L})-[ \t]*\r?\n[ \t]*(\p{L})", RegexOptions.CultureInvariant);
    private static readonly Regex BlankRuns = new(@"(\r?\n[ \t]*){3,}", RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(@"^#[ \t]+(.+?)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex PageToken = new(@"\bpage\s+\d+(\s+of\s+\d+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static CleanResult Clean(string text, string fallbackTitle, IReadOnlyList<string>? pages = null)
    {
        text ??= "";
        var title = Heading.Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : fallbackTitle;
        if (text.Trim().Length == 0) return new CleanResult("", title, 0);

        var repeated = RepeatedLines(pages);
        var kept = text.Replace("\r\n", "\n").Split('\n')
            .Where(l => !PageNumber.IsMatch(l) && !(repeated.Count > 0 && repeated.Contains(Key(l))));
        var cleaned = Hyphen.Replace(string.Join("\n", kept), "$1$2");
        cleaned = BlankRuns.Replace(cleaned, "\n\n").Trim();
        return new CleanResult(cleaned, title, (double)cleaned.Length / text.Length);
    }

    /// <summary>Line key with "Page N [of M]" masked, so "Page 1" and "Page 2" count as the same header.</summary>
    private static string Key(string line) => PageToken.Replace(line.Trim(), "#");

    private static HashSet<string> RepeatedLines(IReadOnlyList<string>? pages)
    {
        var result = new HashSet<string>();
        if (pages == null || pages.Count < 3) return result;
        var counts = new Dictionary<string, int>();
        foreach (var page in pages)
            foreach (var key in page.Replace("\r\n", "\n").Split('\n').Select(Key).Where(k => k.Length > 0).Distinct())
                counts[key] = counts.GetValueOrDefault(key) + 1;
        foreach (var (key, n) in counts)
            if (n * 2 > pages.Count) result.Add(key);
        return result;
    }
}
