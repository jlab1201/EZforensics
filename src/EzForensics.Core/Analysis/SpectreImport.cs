using EzForensics.Core.Spectre;

namespace EzForensics.Core.Analysis;

public sealed class ImportOptions
{
    public DateTimeOffset? Now { get; init; }
    /// <summary>The case's evidence systems, to place findings on computers (see <see cref="StoryHosts"/>); null uses the engine's own host.</summary>
    public CaseHostInfo? Hosts { get; init; }
}

/// <summary>
/// Spectre import: one finding per engine finding (matched by <c>source.spectreFindingId</c>) and one key event per key moment. It only ever replaces
/// the source rows and the fields the user has not edited; things that vanish are marked, never deleted. The first import also migrates the engine's analyst edits, approvals, evidence flags and analyst-created findings once.
/// </summary>
public static class SpectreImport
{
    /// <summary>
    /// Imports a complete engine result. Pass <paramref name="findings"/> only from a successful <c>spectre_findings_list</c> (an empty list means
    /// "Spectre reports nothing" and marks every Spectre item removed at source); do not call it when the engine failed.
    /// </summary>
    public static SyncResult Import(CaseAnalysis a, IReadOnlyList<SpectreFinding> findings, SpectreTimeline? timeline = null, ImportOptions? options = null)
    {
        var o = options ?? new ImportOptions();
        var now = o.Now ?? DateTimeOffset.UtcNow;
        var before = AnalysisSync.Snapshot(a);
        var res = new SyncResult();
        bool migrating = !a.Meta.EngineMigrated;

        if (migrating)
            foreach (var f in findings.Where(f => f.IsAnalystFinding && f.FindingId.Length > 0))
                MigrateAnalystFinding(a, f, now, res);

        var tl = timeline ?? SpectreTimeline.Empty;
        var tfById = new Dictionary<string, SpectreTimelineFinding>(StringComparer.Ordinal);
        foreach (var t in tl.Findings.Concat(tl.Undated)) tfById.TryAdd(t.FindingId, t);
        var hosts = o.Hosts != null ? StoryHosts.Resolve(tl.Findings.Concat(tl.Undated), o.Hosts) : null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            if (f.IsAnalystFinding || f.FindingId.Length == 0) continue;
            seen.Add(f.FindingId);
            tfById.TryGetValue(f.FindingId, out var tf);
            var item = a.Items.FirstOrDefault(i => i.Source.SpectreFindingId == f.FindingId);
            if (item is { Dismissed: true }) continue;
            bool created = item == null;
            if (item == null)
            {
                item = new AnalysisItem
                {
                    Id = AnalysisItem.NewId(), Origin = ItemOrigin.Spectre, 
                    Review = ReviewState.ToReview, Source = { SpectreFindingId = f.FindingId },
                };
            }
            var sig = AnalysisSync.Sig(item);
            bool wasRemoved = item.SourceStatus == SourceStatus.RemovedAtSource;
            Apply(item, f, tf, hosts);
            if (created) { item.Log("imported from Spectre", now); a.Items.Add(item); res.Created++; }
            else
            {
                if (wasRemoved) { item.SourceStatus = SourceStatus.Current; item.Log("Spectre reports this again", now); res.Restored++; }
                if (AnalysisSync.Sig(item) != sig) item.Log("updated from Spectre", now);
            }
            if (migrating) MigrateEdits(item, f, now);
        }

        foreach (var i in a.Items.Where(i => i.Origin == ItemOrigin.Spectre && i.Source.SpectreFindingId != null && !i.Dismissed && !seen.Contains(i.Source.SpectreFindingId)))
            if (i.SourceStatus == SourceStatus.Current)
            {
                i.SourceStatus = SourceStatus.RemovedAtSource;
                i.Log("no longer reported by Spectre", now);
                res.RemovedAtSource++;
            }

        if (timeline != null) ImportMoments(a, tl, now, res);

        if (migrating) a.Meta.EngineMigrated = true;
        AnalysisSync.Finish(a, before, res, now, touchMeta: false);
        if (res.AnyChange || migrating) a.Meta.LastImportedAt = now;
        return res;
    }

    /// <summary>Every key moment becomes a key event (matched by moment id). Edited fields and the user's own grouping are never overwritten.</summary>
    /// <summary>The finding a hidden one was merged into or kept over (followed to the end), so a re-import never points a key event back at a hidden finding.</summary>
    private static AnalysisItem KeptFor(CaseAnalysis a, AnalysisItem i)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while ((i.MergedInto ?? i.DuplicateOf) is { } next && seen.Add(i.Id) && a.Find(next) is { } n) i = n;
        return i;
    }

    private static void ImportMoments(CaseAnalysis a, SpectreTimeline tl, DateTimeOffset now, SyncResult res)
    {
        var byFinding = new Dictionary<string, AnalysisItem>(StringComparer.Ordinal);
        foreach (var i in a.Items.Where(i => i.Source.SpectreFindingId != null && !i.Dismissed)) byFinding[i.Source.SpectreFindingId!] = i;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in tl.Moments)
        {
            if (m.Id.Length == 0) continue;
            seen.Add(m.Id);
            var k = a.KeyEvents.FirstOrDefault(x => x.Source.SpectreMomentId == m.Id);
            if (k is { Dismissed: true }) continue;
            var ids = (m.FindingIds.Count > 0 ? m.FindingIds : m.PrimaryFindingId != null ? new[] { m.PrimaryFindingId } : Array.Empty<string>())
                .Select(f => byFinding.GetValueOrDefault(f)).Where(i => i != null).Select(i => KeptFor(a, i!).Id).Distinct().ToList();
            var title = m.PlainTitle.Length > 0 ? m.PlainTitle : m.Headline;
            if (k == null)
            {
                var free = ids.Where(id => KeyEventOps.KeyEventOf(a, id) == null).ToList();
                if (free.Count == 0) continue;   // everything is already in the analyst's own key events: no second pin
                k = new KeyEvent { Id = KeyEvent.NewId(), Source = { SpectreMomentId = m.Id }, PromotedReason = m.Why };
                k.FindingIds.AddRange(free);
                k.SkippedFindingIds.AddRange(ids.Except(free));
                k.Log("imported from Spectre", now);
                a.KeyEvents.Add(k);
                res.Created++;
            }
            else
            {
                bool wasRemoved = k.SourceStatus == SourceStatus.RemovedAtSource;
                if (wasRemoved) { k.SourceStatus = SourceStatus.Current; k.Log("Spectre reports this again", now); res.Restored++; }
                k.PromotedReason = m.Why;
                if (!k.IsEdited(KeyEventFields.Findings))
                {
                    var own = ids.Where(id => k.FindingIds.Contains(id) || KeyEventOps.KeyEventOf(a, id) == null).ToList();
                    k.FindingIds = own;
                    k.SkippedFindingIds = ids.Except(own).ToList();
                }
            }
            KeyEventOps.RefreshDefaults(a, k);
            if (!k.IsEdited(KeyEventFields.Title) && title.Length > 0) k.Title = title;
            if (!k.IsEdited(KeyEventFields.Time) && m.Time != null) k.Time = m.Time;
        }
        foreach (var k in a.KeyEvents.Where(k => k.Source.SpectreMomentId != null && !k.Dismissed && !seen.Contains(k.Source.SpectreMomentId)))
            if (k.SourceStatus == SourceStatus.Current)
            {
                k.SourceStatus = SourceStatus.RemovedAtSource;
                k.Log("no longer reported by Spectre", now);
                res.RemovedAtSource++;
            }
    }

    private static void Apply(AnalysisItem item, SpectreFinding f, SpectreTimelineFinding? tf, StoryHosts? hosts)
    {
        if (!item.IsEdited(ItemFields.Title)) item.Title = f.Title;
        if (!item.IsEdited(ItemFields.Severity)) item.Severity = f.Severity;
        if (!item.IsEdited(ItemFields.Mitre)) { item.Mitre = f.MitreAttack.ToList(); item.Tactic = AnalysisItem.TacticOfMitre(item.Mitre) ?? tf?.Tactic; }
        if (!item.IsEdited(ItemFields.Description)) item.Description = f.Description;
        item.FlagLabel = f.FlagLabel is { Length: > 0 } fl ? fl.ToLowerInvariant() : FlagLabelOf(f.Severity);
        item.SourceRows = f.Evidence.Where(e => !e.IsAnalyst).Select(e => new AnalysisRow(e.CasePath, e.Row)).ToList();

        string? host = tf != null && hosts != null ? hosts.ComputerOf(tf) : tf?.Host;
        if (host == StoryGraphics.NoHost) host = null;
        if (!string.IsNullOrWhiteSpace(host)) item.Host = host;
        var first = SpectreTime.Parse(f.FirstSeen) ?? tf?.Start ?? SpectreTime.Parse(f.EventTime);
        var last = SpectreTime.Parse(f.LastSeen) ?? tf?.EffectiveEnd ?? first;
        if (first != null) { item.FirstSeen = first; item.LastSeen = last; }
    }

    /// <summary>Fallback when the engine sends no <c>flag_label</c>: malicious for Critical/High, suspicious for Medium, interesting for Low, informational for Info.</summary>
    public static string FlagLabelOf(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" or "high" => "malicious",
        "medium" => "suspicious",
        "low" => "interesting",
        _ => "informational",
    };

    // ------------------------------------------------------------------ one-time migration

    private static void MigrateAnalystFinding(CaseAnalysis a, SpectreFinding f, DateTimeOffset now, SyncResult res)
    {
        var wid = "W-m-" + f.FindingId;
        if (a.Items.Any(i => i.MigratedFrom == f.FindingId) || a.Workspace.Findings.Any(k => k.Id == wid)) return;
        var w = new WorkspaceFinding
        {
            Id = wid, Title = f.EffectiveTitle, Severity = f.EffectiveSeverity, Mitre = f.EffectiveMitreAttack.ToList(),
            Notes = !string.IsNullOrWhiteSpace(f.Notes) ? f.Notes! : f.EffectiveDescription, UpdatedAt = now,
        };
        var seen = new HashSet<RowKey>();
        foreach (var e in f.Evidence) { var r = new AnalysisRow(e.CasePath, e.Row); if (seen.Add(r.Key)) w.Rows.Add(r); }
        a.Workspace.Findings.Add(w);
        AnalysisSync.SyncKeyEvents(a, new SyncOptions { Now = now }, now, new SyncResult(), only: w);
        var item = a.Items.First(i => i.Source.WorkspaceId == wid);
        item.MigratedFrom = f.FindingId;
        item.Review = f.Approval switch { SpectreFinding.Approved => ReviewState.Confirmed, SpectreFinding.Rejected => ReviewState.Rejected, _ => ReviewState.ToReview };
        item.Log($"migrated from engine finding {f.FindingId}", now);
        res.Migrated++;
    }

    /// <summary>Engine analyst edits, approval and evidence flags become the item's starting values (edits marked as edited).</summary>
    private static void MigrateEdits(AnalysisItem item, SpectreFinding f, DateTimeOffset now)
    {
        bool any = false;
        var an = f.Analyst;
        if (an != null)
        {
            if (an.Title != null) { item.Title = an.Title; item.MarkEdited(ItemFields.Title); any = true; }
            if (an.Description != null) { item.Description = an.Description; item.MarkEdited(ItemFields.Description); any = true; }
            if (an.Severity != null) { item.Severity = an.Severity; item.MarkEdited(ItemFields.Severity); any = true; }
            if (an.MitreAttack != null) { item.Mitre = an.MitreAttack.ToList(); item.Tactic = AnalysisItem.TacticOfMitre(item.Mitre); item.MarkEdited(ItemFields.Mitre); any = true; }
            if (an.Notes != null) { item.Notes = an.Notes; item.MarkEdited(ItemFields.Notes); any = true; }
        }
        var decision = f.Approval switch { SpectreFinding.Approved => ReviewState.Confirmed, SpectreFinding.Rejected => ReviewState.Rejected, _ => (ReviewState?)null };
        if (decision != null) { item.Review = decision; any = true; }
        // "not in the report" was the old Not important: real, but off the timeline (spec: not_important -> confirmed, never rejected)
        else if (an?.IncludeInReport == false && item.Review == null) { item.Review = ReviewState.Confirmed; any = true; }

        foreach (var fl in f.AnalystFlags)
        {
            var row = new AnalysisRow(fl.CasePath, fl.Row);
            switch (fl.Label)
            {
                case EvidenceFlagLabels.NotRelevant:
                    if (!item.RowEdits.Removed.Any(r => r.Key == row.Key)) item.RowEdits.Removed.Add(row);
                    any = true; break;
                case EvidenceFlagLabels.Added:
                    if (!item.RowEdits.Added.Any(r => r.Key == row.Key)) item.RowEdits.Added.Add(row);
                    any = true; break;
            }
            bool confirmed = fl.Label == EvidenceFlagLabels.Confirmed;
            if (confirmed || !string.IsNullOrWhiteSpace(fl.Note))
            {
                var n = item.RowEdits.PerRow.FirstOrDefault(p => p.Key == row.Key);
                if (n == null) item.RowEdits.PerRow.Add(n = new RowNote { CsvRelPath = row.CsvRelPath, Row = row.Row });
                if (confirmed) n.Confirmed = true;
                if (!string.IsNullOrWhiteSpace(fl.Note)) n.Note = fl.Note;
                any = true;
            }
        }
        // Rows the examiner added show in the engine's evidence list too; keep them as added rows, not as Spectre's.
        foreach (var e in f.Evidence.Where(e => e.IsAnalyst))
        {
            var row = new AnalysisRow(e.CasePath, e.Row);
            if (!item.RowEdits.Added.Any(r => r.Key == row.Key) && !item.RowEdits.Removed.Any(r => r.Key == row.Key)) { item.RowEdits.Added.Add(row); any = true; }
        }
        if (any) item.Log("migrated the engine's analyst edits", now);
    }
}
