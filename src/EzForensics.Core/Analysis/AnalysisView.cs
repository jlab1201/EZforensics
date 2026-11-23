namespace EzForensics.Core.Analysis;

/// <summary>Review tile counts: findings by review state.</summary>
public sealed record ReviewCounts(int ToReview, int Confirmed, int Rejected)
{
    public int Total => ToReview + Confirmed + Rejected;
}

/// <summary>Filters of the Details list; null means "any".</summary>
public sealed record DetailFilter(ItemOrigin? Origin = null, ReviewState? Review = null, bool? Duplicate = null, bool? RemovedAtSource = null);

/// <summary>
/// The one rule for what shows where (design section 7), as selections the Report page binds to. Pure over <see cref="CaseAnalysis"/>.
/// </summary>
public static class AnalysisView
{
    /// <summary>The review state that counts: a key event without a stored value reads as to_review.</summary>
    public static ReviewState ReviewOf(AnalysisItem i) => i.Review ?? ReviewState.ToReview;

    /// <summary>
    /// Active: not dismissed, not hidden as a duplicate or merged (except while a re-raised pair is open, when both are shown),
    /// and the source is current or the user kept the item.
    /// </summary>
    public static bool IsActive(CaseAnalysis a, AnalysisItem i)
    {
        if (i.Dismissed) return false;
        if (!(i.SourceStatus == SourceStatus.Current || i.KeptByUser)) return false;
        if (!i.IsHidden) return true;
        var target = a.Find(i.DuplicateOf ?? i.MergedInto);
        return target != null && DuplicateDetector.IsOpenReraise(target);
    }

    /// <summary>Active findings (the Review list), in time order.</summary>
    public static IReadOnlyList<AnalysisItem> ActiveFindings(CaseAnalysis a) =>
        Sorted(a.Items.Where(i => IsActive(a, i)));

    /// <summary>Overview heatmap: active findings that are not rejected.</summary>
    public static IReadOnlyList<AnalysisItem> HeatmapItems(CaseAnalysis a) =>
        Sorted(a.Items.Where(i => IsActive(a, i) && ReviewOf(i) != ReviewState.Rejected));

    /// <summary>
    /// The timeline: key events that are not dismissed, are current or kept, and have at least one active, non-rejected finding
    /// (a key event whose findings are all rejected drops out, but is not dismissed, so un-rejecting brings it back). Time order, no time last.
    /// </summary>
    public static IReadOnlyList<KeyEvent> ActiveKeyEvents(CaseAnalysis a) =>
        a.KeyEvents.Where(k => !k.Dismissed && (k.SourceStatus == SourceStatus.Current || k.KeptByUser) &&
                k.FindingIds.Select(a.Find).Any(f => f != null && IsActive(a, f) && ReviewOf(f) != ReviewState.Rejected))
            .Select(k => (k, e: KeyEventOps.Effective(a, k)))
            .OrderBy(x => x.e.Time == null).ThenBy(x => x.e.Time).ThenBy(x => x.e.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.k.Id, StringComparer.Ordinal)
            .Select(x => x.k).ToList();

    /// <summary>Report body: the same key events as the timeline.</summary>
    public static IReadOnlyList<KeyEvent> ReportKeyEvents(CaseAnalysis a) => ActiveKeyEvents(a);

    /// <summary>Report appendix: active, confirmed findings that sit in no key event.</summary>
    public static IReadOnlyList<AnalysisItem> AppendixFindings(CaseAnalysis a) =>
        Sorted(a.Items.Where(i => IsActive(a, i) && ReviewOf(i) == ReviewState.Confirmed && KeyEventOps.KeyEventOf(a, i.Id) == null));

    /// <summary>Who found it: Both when the finding plus the findings merged into it cover both producers.</summary>
    public static DisplayOrigin OriginOf(CaseAnalysis a, AnalysisItem i)
    {
        bool you = i.Origin == ItemOrigin.Analyst, sp = i.Origin == ItemOrigin.Spectre;
        foreach (var m in a.Items.Where(x => x.MergedInto == i.Id)) { you |= m.Origin == ItemOrigin.Analyst; sp |= m.Origin == ItemOrigin.Spectre; }
        return you && sp ? DisplayOrigin.Both : sp ? DisplayOrigin.Spectre : DisplayOrigin.You;
    }

    /// <summary>Origin of a key event, over its findings.</summary>
    public static DisplayOrigin OriginOf(CaseAnalysis a, KeyEvent k)
    {
        bool you = false, sp = false;
        foreach (var f in k.FindingIds.Select(a.Find).Where(f => f != null))
            switch (OriginOf(a, f!)) { case DisplayOrigin.You: you = true; break; case DisplayOrigin.Spectre: sp = true; break; default: you = sp = true; break; }
        if (!you && !sp) sp = k.Source.SpectreMomentId != null;
        return you && sp ? DisplayOrigin.Both : sp ? DisplayOrigin.Spectre : DisplayOrigin.You;
    }

    /// <summary>Review tiles and ring: every active finding counted by review (rejected included); duplicates, merged and removed ones are left out.</summary>
    public static ReviewCounts ReviewTileCounts(CaseAnalysis a)
    {
        int t = 0, c = 0, r = 0;
        foreach (var i in a.Items.Where(i => IsActive(a, i)))
            switch (ReviewOf(i))
            {
                case ReviewState.ToReview: t++; break;
                case ReviewState.Confirmed: c++; break;
                case ReviewState.Rejected: r++; break;
            }
        return new ReviewCounts(t, c, r);
    }

    /// <summary>Details list and editor: every item that is not a tombstone (including hidden duplicates and removed-at-source ones, which keep their chip).</summary>
    public static IReadOnlyList<AnalysisItem> DetailItems(CaseAnalysis a, DetailFilter? f = null)
    {
        var q = a.Items.Where(i => !i.Dismissed);
        if (f != null)
        {
            if (f.Origin != null) q = q.Where(i => i.Origin == f.Origin);
            if (f.Review != null) q = q.Where(i => ReviewOf(i) == f.Review);
            if (f.Duplicate != null) q = q.Where(i => i.IsHidden == f.Duplicate);
            if (f.RemovedAtSource != null) q = q.Where(i => (i.SourceStatus == SourceStatus.RemovedAtSource) == f.RemovedAtSource);
        }
        return Sorted(q);
    }

    /// <summary>Items that need a decision about a source that went away: "Keep anyway" or "Remove".</summary>
    public static IReadOnlyList<AnalysisItem> RemovedAtSourceItems(CaseAnalysis a) =>
        a.Items.Where(i => !i.Dismissed && i.SourceStatus == SourceStatus.RemovedAtSource && !i.KeptByUser).ToList();

    /// <summary>The empty state of the Report page: no items yet.</summary>
    public static bool IsEmpty(CaseAnalysis a) => a.Items.Count == 0;

    /// <summary>"Use my analysis" is offered when the workspace has findings.</summary>
    public static bool CanUseMyAnalysis(CaseAnalysis a) => a.Workspace.Findings.Count > 0;

    private static IReadOnlyList<AnalysisItem> Sorted(IEnumerable<AnalysisItem> q) =>
        q.OrderBy(i => i.FirstSeen == null).ThenBy(i => i.FirstSeen).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
}
