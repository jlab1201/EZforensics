using EzForensics.Core.Triage;

namespace EzForensics.Core.Advisories;

/// <summary>One column-header marker: severity (null = reliable) and tooltip.</summary>
public sealed record ColumnMarker(string Column, AdvisorySeverity? Severity, string Tip);

/// <summary>What the banner shows for a report (pure shaping, no UI types).</summary>
public sealed record BannerShape
{
    public bool Show { get; init; }
    /// <summary>True when only Info advisories exist: a subtle one-line "N notes" strip.</summary>
    public bool IsSubtle { get; init; }
    public AdvisorySeverity Severity { get; init; }
    public string Headline { get; init; } = "";
    /// <summary>Copy-detection evidence sentence (may be empty).</summary>
    public string Summary { get; init; } = "";
    public int Count { get; init; }
}

public static class AdvisoryPresentation
{
    public static BannerShape ShapeBanner(AdvisoryReport? report)
    {
        if (report == null || report.Advisories.Count == 0) return new BannerShape();
        var sev = report.HighestSeverity ?? AdvisorySeverity.Info;
        if (sev == AdvisorySeverity.Info)
            return new BannerShape
            {
                Show = true, IsSubtle = true, Severity = sev, Count = report.Advisories.Count,
                Headline = report.Advisories.Count == 1 ? "1 note about this artifact" : $"{report.Advisories.Count} notes about this artifact",
            };
        return new BannerShape
        {
            Show = true, Severity = sev, Count = report.Advisories.Count,
            Headline = report.Advisories.First(a => a.Severity == sev).Title,
            Summary = report.Copy.Summary ?? "",
        };
    }

    /// <summary>Header markers for every column named by an advisory, plus a subtle "reliable" marker for the rest.</summary>
    public static IReadOnlyList<ColumnMarker> ColumnMarkers(AdvisoryReport? report, IReadOnlyList<string> headers)
    {
        var list = new List<ColumnMarker>();
        if (report == null || report.Advisories.Count == 0) return list;
        var sevs = report.ColumnSeverities;
        var reliable = new HashSet<string>(report.Advisories.SelectMany(a => a.ReliableColumns), StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            if (sevs.TryGetValue(h, out var s))
            {
                var tip = string.Join("\n\n", report.ForColumn(h).Select(a => $"{a.Title}\n{Short(a.Message)}"));
                list.Add(new ColumnMarker(h, s, tip));
            }
            else if (reliable.Contains(h))
                list.Add(new ColumnMarker(h, null, "Embedded timestamp – reliable"));
        }
        return list;
    }

    private static string Short(string m)
    {
        m = m.Trim();
        int dot = m.IndexOf(". ", StringComparison.Ordinal);
        var first = dot > 0 ? m[..(dot + 1)] : m;
        return first.Length > 220 ? first[..217] + "..." : first;
    }
}
