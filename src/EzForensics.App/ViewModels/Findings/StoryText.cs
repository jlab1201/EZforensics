using System.Globalization;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>
/// Plain wording shared by the story timeline, the moment cards and the slides. Times are UTC and labelled so; a missing time is said
/// plainly, never guessed. Nothing here claims that one thing caused another.
/// </summary>
public static class StoryText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"High", "Medium", ...; unknown values read "Info".</summary>
    public static string SeverityWord(string? severity)
    {
        var r = SpectreFinding.Rank(severity);
        return r >= 0 ? SpectreFinding.Severities[r] : "Info";
    }

    /// <summary>"3 Mar 2021, 14:02 UTC", "2019 to 2021", "3 Mar to 9 Apr 2021 (UTC)", or "No time recorded".</summary>
    public static string When(DateTimeOffset? time, DateTimeOffset? end = null)
    {
        if (time is not { } t) return "No time recorded";
        var s = t.UtcDateTime;
        if (end is not { } e0 || e0 <= t) return s.ToString("d MMM yyyy, HH:mm", Inv) + " UTC";
        var e = e0.UtcDateTime;
        if (s.Year != e.Year) return $"{s.Year.ToString(Inv)} to {e.Year.ToString(Inv)}";
        if (s.Date != e.Date) return $"{s.ToString("d MMM", Inv)} to {e.ToString("d MMM yyyy", Inv)} (UTC)";
        return $"{s.ToString("d MMM yyyy, HH:mm", Inv)} to {e.ToString("HH:mm", Inv)} UTC";
    }

    /// <summary>"×6, 2019 to 2021"; the count is left out when it is one.</summary>
    public static string CountLine(int count, DateTimeOffset? time, DateTimeOffset? end) =>
        (count > 1 ? $"×{count.ToString("N0", Inv)}, " : "") + When(time, end);

    /// <summary>
    /// The two ends of the activity span, short, for "First → Last (UTC)": "2 Jan" and "5 Mar 2026" in one year, "28 Dec 2025" and
    /// "5 Jan 2026" across years, "2 Jan 2026, 08:14" and "11:30" on one day, and the single time with an empty end when there is one.
    /// </summary>
    public static (string First, string Last) SpanParts(DateTimeOffset? first, DateTimeOffset? last)
    {
        if (first is not { } f0) return ("", "");
        var f = f0.UtcDateTime;
        if (last is not { } l0 || l0 <= f0) return (f.ToString("d MMM yyyy, HH:mm", Inv), "");
        var l = l0.UtcDateTime;
        if (f.Date == l.Date) return (f.ToString("d MMM yyyy, HH:mm", Inv), l.ToString("HH:mm", Inv));
        if (f.Year == l.Year) return (f.ToString("d MMM", Inv), l.ToString("d MMM yyyy", Inv));
        return (f.ToString("d MMM yyyy", Inv), l.ToString("d MMM yyyy", Inv));
    }

    /// <summary>Date for a tile: "3 Mar 2021, 14:02 UTC", or "Not known" when there is none.</summary>
    public static string Tile(DateTimeOffset? t) => t is { } x ? x.UtcDateTime.ToString("d MMM yyyy, HH:mm", Inv) + " UTC" : "Not known";

    /// <summary>The moment's main finding (its primary, else the first one listed that is known).</summary>
    public static SpectreTimelineFinding? Primary(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId)
    {
        if (m.PrimaryFindingId != null && byId.TryGetValue(m.PrimaryFindingId, out var f)) return f;
        foreach (var id in m.FindingIds) if (byId.TryGetValue(id, out var g)) return g;
        return null;
    }

    /// <summary>The highest severity among the moment's findings (Info when none is known).</summary>
    public static string Severity(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId)
    {
        var best = -1;
        foreach (var id in m.FindingIds.Append(m.PrimaryFindingId ?? ""))
            if (byId.TryGetValue(id, out var f)) best = Math.Max(best, SpectreFinding.Rank(f.Severity));
        return best >= 0 ? SpectreFinding.Severities[best] : "Info";
    }

    /// <summary>How many times the moment was seen: the main finding's count, else the number of findings in it.</summary>
    public static int Count(SpectreMoment m, SpectreTimelineFinding? primary) =>
        primary?.OccurrenceCount > 1 ? primary.OccurrenceCount : Math.Max(1, m.FindingIds.Count);

    /// <summary>Findings by id and by finding id.</summary>
    public static Dictionary<string, SpectreTimelineFinding> Index(SpectreTimeline tl)
    {
        var d = new Dictionary<string, SpectreTimelineFinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in tl.Findings.Concat(tl.Undated))
        {
            d.TryAdd(f.Id, f);
            if (f.FindingId.Length > 0) d.TryAdd(f.FindingId, f);
        }
        return d;
    }

    /// <summary>
    /// The three lines every timeline card shows, in this order: title, "yyyy-MM-dd HH:mm:ss UTC" (or "No time") and the tactic name (the
    /// primary finding's, in plain words). The severity is the card's colour, so it is not repeated as text.
    /// </summary>
    public static IReadOnlyList<string> CardLines(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId)
    {
        return new[]
        {
            m.PlainTitle.Length > 0 ? m.PlainTitle : m.Headline,
            m.Time is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", Inv) + " UTC" : "No time",
            PlainLanguage.Tactic(Primary(m, byId)?.Tactic),
        };
    }

    /// <summary>Last part of a path, either slash.</summary>
    public static string BaseName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.TrimEnd('/', '\\');
        var i = p.LastIndexOfAny(new[] { '/', '\\' });
        return i >= 0 ? p[(i + 1)..] : p;
    }
}
