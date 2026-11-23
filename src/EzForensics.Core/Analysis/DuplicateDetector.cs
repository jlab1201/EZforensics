namespace EzForensics.Core.Analysis;

public enum DuplicateMatch
{
    /// <summary>The row sets are equal, or one is contained in the other.</summary>
    SameEvidence,
    /// <summary>Some rows are shared, or the same technique on the same host within 15 minutes.</summary>
    Possible,
}

public enum DuplicateResolution { KeepA, KeepB, Merge, KeepBoth }

/// <summary>Two findings that may be the same thing. Both are shown until the user resolves the pair.</summary>
public sealed record DuplicatePair(string AId, string BId, DuplicateMatch Match, int SharedRows, string Reason);

/// <summary>A kept item went away (removed at source, not kept by the user): "B is no longer reported. Keep B anyway, or restore A?"</summary>
public sealed record ReraisedPair(string KeptId, string HiddenId);

/// <summary>An info chip that needs no decision ("12 rows already in key event X", "also flagged by Spectre").</summary>
public sealed record OverlapChip(string Kind, string Text, string OtherId, int Rows)
{
    public const string InKeyEvent = "in_key_event", AlsoFlagged = "also_flagged";
}

/// <summary>
/// Duplicates are looked for between findings (design section 4). Key events are not checked.
/// Nothing is resolved automatically: the result is a list for the user. Pure over <see cref="CaseAnalysis"/>; no I/O.
/// </summary>
public static class DuplicateDetector
{
    public static readonly TimeSpan PossibleWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Open pairs among the findings that count (not dismissed, not hidden, current or kept). With <paramref name="involving"/> only pairs
    /// that include one of those items. Pairs the user kept as two (with an unchanged row-set hash) are left out.
    /// </summary>
    public static IReadOnlyList<DuplicatePair> FindPairs(CaseAnalysis a, ISet<string>? involving = null)
    {
        var cands = a.Items.Where(i => !i.Dismissed && !i.IsHidden &&
            (i.SourceStatus == SourceStatus.Current || i.KeptByUser)).ToList();
        var keys = cands.ToDictionary(i => i.Id, i => a.EffectiveKeys(i));
        var pairs = new List<DuplicatePair>();
        for (int x = 0; x < cands.Count; x++)
            for (int y = x + 1; y < cands.Count; y++)
            {
                var A = cands[x]; var B = cands[y];
                if (involving != null && !involving.Contains(A.Id) && !involving.Contains(B.Id)) continue;
                var m = Compare(A, B, keys[A.Id], keys[B.Id]);
                if (m == null) continue;
                if (IsDecided(A, B, keys[A.Id], keys[B.Id])) continue;
                pairs.Add(m);
            }
        return pairs;
    }

    private static DuplicatePair? Compare(AnalysisItem A, AnalysisItem B, HashSet<RowKey> sa, HashSet<RowKey> sb)
    {
        int shared = sa.Count == 0 || sb.Count == 0 ? 0 : sa.Count(sb.Contains);
        if (shared > 0)
        {
            if (sa.IsSubsetOf(sb) || sb.IsSubsetOf(sa))
                return new DuplicatePair(A.Id, B.Id, DuplicateMatch.SameEvidence, shared, sa.SetEquals(sb) ? "Same rows" : "One is contained in the other");
            return new DuplicatePair(A.Id, B.Id, DuplicateMatch.Possible, shared, $"{shared:N0} shared row(s)");
        }
        var tech = A.Mitre.Intersect(B.Mitre, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (tech != null && !string.IsNullOrWhiteSpace(A.Host) && string.Equals(A.Host, B.Host, StringComparison.OrdinalIgnoreCase) &&
            A.FirstSeen is { } fa && B.FirstSeen is { } fb && (fa - fb).Duration() <= PossibleWindow)
            return new DuplicatePair(A.Id, B.Id, DuplicateMatch.Possible, 0, $"{tech} on {A.Host} within {PossibleWindow.TotalMinutes:0} minutes");
        return null;
    }

    /// <summary>Hash of the pair's row sets (ids in order), stored with a keep-both decision.</summary>
    public static string PairHash(CaseAnalysis a, AnalysisItem x, AnalysisItem y)
    {
        var (p, q) = string.CompareOrdinal(x.Id, y.Id) <= 0 ? (x, y) : (y, x);
        return PairHash(a.EffectiveKeys(p), a.EffectiveKeys(q));
    }

    private static string PairHash(HashSet<RowKey> first, HashSet<RowKey> second) =>
        CaseAnalysis.HashRows(first) + "|" + CaseAnalysis.HashRows(second);

    private static bool IsDecided(AnalysisItem A, AnalysisItem B, HashSet<RowKey> sa, HashSet<RowKey> sb)
    {
        var h = string.CompareOrdinal(A.Id, B.Id) <= 0 ? PairHash(sa, sb) : PairHash(sb, sa);
        bool Has(AnalysisItem i, string other) => i.DupDecisions.Any(d => d.OtherId == other && d.Decision == "keep_both" && d.RowSetHash == h);
        return Has(A, B.Id) || Has(B, A.Id);
    }

    /// <summary>Items kept over a hidden duplicate / merged item, where the kept one has since vanished at its source and the user has not said keep.</summary>
    public static IReadOnlyList<ReraisedPair> FindReraised(CaseAnalysis a)
    {
        var list = new List<ReraisedPair>();
        foreach (var h in a.Items.Where(i => i.IsHidden && !i.Dismissed))
        {
            var k = a.Find(h.DuplicateOf ?? h.MergedInto);
            if (k != null && IsOpenReraise(k)) list.Add(new ReraisedPair(k.Id, h.Id));
        }
        return list;
    }

    /// <summary>The kept item went away and nobody answered: its hidden items must be shown again.</summary>
    public static bool IsOpenReraise(AnalysisItem kept) =>
        kept.SourceStatus == SourceStatus.RemovedAtSource && !kept.KeptByUser && !kept.Dismissed;

    /// <summary>Applies the user's choice for a pair. <c>KeepA</c>/<c>KeepB</c> hide the other with <c>duplicateOf</c>; <c>Merge</c> folds B into A; <c>KeepBoth</c> records the decision.</summary>
    public static void Resolve(CaseAnalysis a, DuplicateResolution op, string aId, string bId, DateTimeOffset? now = null)
    {
        var A = a.Find(aId) ?? throw new ArgumentException("Unknown item " + aId, nameof(aId));
        var B = a.Find(bId) ?? throw new ArgumentException("Unknown item " + bId, nameof(bId));
        var t = now ?? DateTimeOffset.UtcNow;
        switch (op)
        {
            case DuplicateResolution.KeepA:
                Repoint(a, B, A, t);
                B.DuplicateOf = A.Id; B.Log($"duplicate of {A.Id}, kept {A.Id}", t); A.Log($"kept over duplicate {B.Id}", t); break;
            case DuplicateResolution.KeepB:
                Repoint(a, A, B, t);
                A.DuplicateOf = B.Id; A.Log($"duplicate of {B.Id}, kept {B.Id}", t); B.Log($"kept over duplicate {A.Id}", t); break;
            case DuplicateResolution.Merge:
                Repoint(a, B, A, t);
                B.MergedInto = A.Id; B.Log($"merged into {A.Id}", t); A.Log($"merged {B.Id} into this item", t); break;
            case DuplicateResolution.KeepBoth:
                var h = PairHash(a, A, B);
                foreach (var (x, y) in new[] { (A, B), (B, A) })
                {
                    x.DupDecisions.RemoveAll(d => d.OtherId == y.Id);
                    x.DupDecisions.Add(new DupDecision { OtherId = y.Id, Decision = "keep_both", RowSetHash = h });
                    x.Log($"kept both with {y.Id}", t);
                }
                break;
        }
    }

    /// <summary>
    /// The loser is hidden, so a key event must not keep pointing at it. The key event points at the winner instead; if the winner is already in a
    /// key event, the loser is just removed from its own.
    /// </summary>
    private static void Repoint(CaseAnalysis a, AnalysisItem loser, AnalysisItem winner, DateTimeOffset t)
    {
        var k = KeyEventOps.KeyEventOf(a, loser.Id);
        if (k == null) return;
        if (KeyEventOps.KeyEventOf(a, winner.Id) != null) { KeyEventOps.RemoveFinding(a, k.Id, loser.Id, t); return; }
        var ix = k.FindingIds.IndexOf(loser.Id);
        k.FindingIds[ix] = winner.Id;
        k.MarkEdited(KeyEventFields.Findings);
        k.Log($"finding {loser.Id} replaced by {winner.Id}", t);
        KeyEventOps.RefreshDefaults(a, k);
    }
}
