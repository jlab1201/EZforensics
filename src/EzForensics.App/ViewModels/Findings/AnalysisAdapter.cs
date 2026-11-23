using System.Globalization;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>What the engine said in plain words about a finding (its latest timeline); used for Spectre items so they keep their plain_language text.</summary>
internal sealed record EnginePlain(string PlainTitle, string Why, string? CantTell, string EvidenceLine, string Phase, string PromotedReason, string? MomentId = null, string? RawHost = null, string? ArtifactType = null);

/// <summary>A small label on an item ("You", "Duplicate of ...", "Removed in Analyze", ...). <see cref="Kind"/> names the chip for automation and styling.</summary>
public sealed record ItemChip(string Kind, string Text)
{
    public const string Origin = "origin", Duplicate = "duplicate", InKeyEvent = "in_key_event", AlsoFlagged = "also_flagged", Removed = "removed",
        Stale = "stale", NoLongerHighlights = "no_longer_highlights", Hidden = "hidden", Reraised = "reraised";
}

/// <summary>The store's items in the shapes the page already draws: engine-style findings, a timeline with key events as moments, and the chips.</summary>
internal sealed class AdaptedCase
{
    public IReadOnlyList<SpectreFinding> Findings { get; init; } = Array.Empty<SpectreFinding>();
    public SpectreTimeline Timeline { get; init; } = SpectreTimeline.Empty;
    public IReadOnlyDictionary<string, AnalysisItem> Items { get; init; } = new Dictionary<string, AnalysisItem>();
    public IReadOnlyDictionary<string, IReadOnlyList<ItemChip>> Chips { get; init; } = new Dictionary<string, IReadOnlyList<ItemChip>>();
    public IReadOnlyList<DuplicatePair> Pairs { get; init; } = Array.Empty<DuplicatePair>();
    public IReadOnlySet<string> Active { get; init; } = new HashSet<string>();
    /// <summary>The key event behind each timeline moment, by moment id.</summary>
    public IReadOnlyDictionary<string, KeyEvent> MomentKeyEvents { get; init; } = new Dictionary<string, KeyEvent>();
    /// <summary>The effective severity of each timeline moment (an edited key event severity wins over its findings').</summary>
    public IReadOnlyDictionary<string, string> MomentSeverities { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The one adapter between the case analysis store and the Findings page view models (design section 7). Items become <see cref="SpectreFinding"/>s
/// (every item that is not a tombstone) and a <see cref="SpectreTimeline"/> (heatmap items as findings, timeline items as moments). Analyst items fall back
/// cleanly: plain title from the title, why from the notes, the stage from the tactic. Spectre items keep the engine's plain text when it is known.
/// </summary>
internal static class AnalysisAdapter
{
    public const string MomentPrefix = "M-";

    /// <summary>The engine's approval word for an item's review state; null while the item still needs a look (and for every flag).</summary>
    public static string? ApprovalOf(AnalysisItem i)
    {
        return AnalysisView.ReviewOf(i) switch
        {
            ReviewState.Confirmed => SpectreFinding.Approved,
            ReviewState.Rejected => SpectreFinding.Rejected,
            _ => null,
        };
    }

    public static string TimeText(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string TacticOf(AnalysisItem i) => !string.IsNullOrWhiteSpace(i.Tactic) ? i.Tactic! : AnalysisItem.TacticOfMitre(i.Mitre) ?? "";

    public static AdaptedCase Build(CaseAnalysis a, IReadOnlyDictionary<string, EnginePlain>? plain = null, Func<string, string?>? hostOf = null)
    {
        plain ??= new Dictionary<string, EnginePlain>();
        var pairs = DuplicateDetector.FindPairs(a);
        var items = a.Items.Where(i => !i.Dismissed).ToDictionary(SourceId);
        var active = items.Values.Where(i => AnalysisView.IsActive(a, i)).Select(SourceId).ToHashSet();

        var findings = new List<SpectreFinding>();
        foreach (var i in AnalysisView.DetailItems(a)) findings.Add(ToFinding(a, i));

        var chips = new Dictionary<string, IReadOnlyList<ItemChip>>();
        foreach (var i in items.Values) chips[SourceId(i)] = ChipsOf(a, i, pairs);

        var keyEvents = new Dictionary<string, KeyEvent>(StringComparer.Ordinal);
        var severities = new Dictionary<string, string>(StringComparer.Ordinal);
        var timeline = BuildTimeline(a, plain, keyEvents, severities);
        return new AdaptedCase { Findings = findings, Timeline = timeline, Items = items, Chips = chips, Pairs = pairs, Active = active, MomentKeyEvents = keyEvents, MomentSeverities = severities };
    }

    // ------------------------------------------------------------ findings

    public static SpectreFinding ToFinding(CaseAnalysis a, AnalysisItem i)
    {
        var rows = a.EffectiveRows(i);
        var evidence = EvidenceOf(i, rows);
        var first = i.FirstSeen is { } f ? TimeText(f) : null;
        var last = i.LastSeen is { } l ? TimeText(l) : first;
        var notes = i.Notes;
        var analyst = notes.Length > 0 ? new SpectreAnalystEdit(null, null, null, null, notes, null, null, null, 0) : null;
        var desc = i.Description.Length > 0 || i.Origin == ItemOrigin.Spectre ? i.Description : "";
        return new SpectreFinding(SourceId(i), SourceId(i), i.Title, i.Severity, null, i.Mitre.ToList(), null, null, desc, ApprovalOf(i), true, i.EditedFields.Count > 0,
            first, first, last, analyst, Array.Empty<string>())
        {
            SourceFile = rows.FirstOrDefault()?.CsvRelPath, CasePath = rows.FirstOrDefault()?.CsvRelPath,
            Evidence = evidence, EvidenceTotal = rows.Count, AnalystFlags = FlagsOf(i),
            Origin = null, Stage = "key_event", FlagLabel = i.FlagLabel,
        };
    }

    /// <summary>The id the chat and the user know the item by: the engine's finding id for a Spectre item, else the item id.</summary>
    public static string SourceId(AnalysisItem i) => i.Source.SpectreFindingId is { Length: > 0 } s ? s : i.Id;

    private static IReadOnlyList<SpectreEvidenceRef> EvidenceOf(AnalysisItem i, IReadOnlyList<AnalysisRow> effective)
    {
        var added = i.RowEdits.Added.Select(r => r.Key).ToHashSet();
        var list = new List<SpectreEvidenceRef>();
        var seen = new HashSet<RowKey>();
        foreach (var r in effective)
        {
            seen.Add(r.Key);
            bool analyst = i.Origin == ItemOrigin.Analyst || added.Contains(r.Key);
            list.Add(new SpectreEvidenceRef(r.CsvRelPath, r.Row, null) { By = analyst ? "analyst" : "spectre" });
        }
        // removed rows stay in the panel (marked not relevant) so the examiner can restore them
        foreach (var r in i.RowEdits.Removed)
            if (seen.Add(r.Key)) list.Add(new SpectreEvidenceRef(r.CsvRelPath, r.Row, null) { By = i.Origin == ItemOrigin.Analyst ? "analyst" : "spectre" });
        return list;
    }

    private static IReadOnlyList<SpectreEvidenceFlag> FlagsOf(AnalysisItem i)
    {
        var sid = SourceId(i);
        var list = new List<SpectreEvidenceFlag>();
        string? who = null;
        foreach (var r in i.RowEdits.Removed)
            list.Add(new SpectreEvidenceFlag(sid, r.CsvRelPath, r.Row, EvidenceFlagLabels.NotRelevant, null, who, null));
        foreach (var n in i.RowEdits.PerRow)
        {
            if (n.Confirmed == true) list.Add(new SpectreEvidenceFlag(sid, n.CsvRelPath, n.Row, EvidenceFlagLabels.Confirmed, null, who, null));
            if (!string.IsNullOrWhiteSpace(n.Note)) list.Add(new SpectreEvidenceFlag(sid, n.CsvRelPath, n.Row, EvidenceFlagLabels.Note, n.Note, who, null));
        }
        return list;
    }

    // ------------------------------------------------------------ chips

    private static IReadOnlyList<ItemChip> ChipsOf(CaseAnalysis a, AnalysisItem i, IReadOnlyList<DuplicatePair> pairs)
    {
        var list = new List<ItemChip> { new(ItemChip.Origin, FindingsViewModel.OriginWord(AnalysisView.OriginOf(a, i))) };
        string Name(string id) => a.Find(id) is { } o ? o.Title : id;
        if (i.DuplicateOf != null) list.Add(new ItemChip(ItemChip.Hidden, $"Duplicate of {Name(i.DuplicateOf)}"));
        else if (i.MergedInto != null) list.Add(new ItemChip(ItemChip.Hidden, $"Merged into {Name(i.MergedInto)}"));
        foreach (var p in pairs.Where(p => p.AId == i.Id || p.BId == i.Id))
            list.Add(new ItemChip(ItemChip.Duplicate, (p.Match == DuplicateMatch.SameEvidence ? "Same evidence as " : "Possible duplicate of ") + Name(p.AId == i.Id ? p.BId : p.AId)));
        foreach (var r in DuplicateDetector.FindReraised(a).Where(r => r.KeptId == i.Id))
            list.Add(new ItemChip(ItemChip.Reraised, $"{i.Title} is no longer reported. Keep it anyway, or restore {Name(r.HiddenId)}?"));
        if (KeyEventOps.KeyEventOf(a, i.Id) is { } ke) list.Add(new ItemChip(ItemChip.InKeyEvent, $"In key event {KeyEventOps.Effective(a, ke).Title}"));
        if (i.SourceStatus == SourceStatus.RemovedAtSource && !i.KeptByUser)
            list.Add(new ItemChip(ItemChip.Removed, i.Origin == ItemOrigin.Analyst ? "Removed in Analyze" : "No longer reported by Spectre"));
        if (i.SourceRows.Any(r => r.Stale) || a.EffectiveRows(i).Any(r => r.Stale)) list.Add(new ItemChip(ItemChip.Stale, "Some rows changed in the CSV"));
        return list;
    }

    // ------------------------------------------------------------ timeline

    private static EnginePlain? PlainOf(AnalysisItem i, IReadOnlyDictionary<string, EnginePlain> plain) =>
        i.Source.SpectreFindingId is { } s && plain.TryGetValue(s, out var p) ? p : null;

    private static string EvidenceLineOf(CaseAnalysis a, AnalysisItem i)
    {
        var rows = a.EffectiveRows(i);
        if (rows.Count == 0) return "";
        var files = rows.Select(r => Path.GetFileName(r.CsvRelPath)).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var where = files.Count switch { 0 => "", 1 => " in " + files[0], var n => $" in {n} files" };
        return $"{rows.Count:N0} row{(rows.Count == 1 ? "" : "s")}{where}";
    }

    /// <summary>The moment id of a key event: Spectre's moment id when it came from one, else the key event id.</summary>
    public static string MomentIdOf(KeyEvent k) => k.Source.SpectreMomentId is { Length: > 0 } m ? m : k.Id;

    private static SpectreTimeline BuildTimeline(CaseAnalysis a, IReadOnlyDictionary<string, EnginePlain> plain, Dictionary<string, KeyEvent> keyEvents, Dictionary<string, string> severities)
    {
        var heat = AnalysisView.HeatmapItems(a);
        var tfs = heat.Select(i => ToTimelineFinding(a, i, plain)).ToList();
        var dated = tfs.Where(t => t.Start != null).ToList();
        var undated = tfs.Where(t => t.Start == null).ToList();

        var moments = new List<SpectreMoment>();
        foreach (var k in AnalysisView.ActiveKeyEvents(a))   // one moment per active key event, in time order (no time last)
        {
            var m = ToMoment(a, k, plain);
            if (m == null) continue;
            keyEvents[m.Id] = k;
            severities[m.Id] = KeyEventOps.Effective(a, k).Severity;
            moments.Add(m with { Number = moments.Count + 1 });
        }

        var times = moments.Where(m => m.Time != null).SelectMany(m => new[] { m.Time, m.End }).Where(t => t != null).Select(t => t!.Value).ToList();
        DateTimeOffset? first = times.Count > 0 ? times.Min() : null, last = times.Count > 0 ? times.Max() : null;
        int hosts = tfs.Select(t => t.Host).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int need = AnalysisView.ReviewTileCounts(a).ToReview;
        var inMoments = moments.SelectMany(m => m.FindingIds).ToHashSet();
        int minor = tfs.Count(t => !inMoments.Contains(t.FindingId));
        var summary = new SpectreTimelineSummary(PlainLanguage.SummarySentence(moments.Count, first, last, hosts, undated.Count), moments.Count, first, last, hosts, need, undated.Count, minor);

        return new SpectreTimeline(null, DateTimeOffset.UtcNow, "UTC", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            dated, undated, Array.Empty<SpectreCluster>(), moments) { Summary = summary };
    }

    private static SpectreTimelineFinding ToTimelineFinding(CaseAnalysis a, AnalysisItem i, IReadOnlyDictionary<string, EnginePlain> plain)
    {
        var rows = a.EffectiveRows(i);
        var p = PlainOf(i, plain);
        var tactic = TacticOf(i);
        var end = i.LastSeen is { } l && i.FirstSeen is { } f && l > f ? l : (DateTimeOffset?)null;
        return new SpectreTimelineFinding(SourceId(i), SourceId(i), i.Title, i.Severity, null, tactic.Length > 0 ? tactic : null,
            i.Mitre.Count > 0 ? string.Join(", ", i.Mitre) : null, p?.RawHost is { Length: > 0 } rh ? rh : i.Host, p?.ArtifactType, i.FirstSeen, end, Math.Max(1, rows.Count), Array.Empty<string>(),
            ApprovalOf(i), true, i.EditedFields.Count > 0, "auto", rows.FirstOrDefault()?.CsvRelPath, null)
        {
            CasePath = rows.FirstOrDefault()?.CsvRelPath,
            Evidence = EvidenceOf(i, rows), EvidenceTotal = rows.Count,
            PlainTitle = p?.PlainTitle is { Length: > 0 } pt ? pt : i.Title,
            Phase = p?.Phase is { Length: > 0 } ph ? ph : PlainLanguage.Phase(tactic, i.Mitre.FirstOrDefault()),
        };
    }

    /// <summary>A key event as a timeline moment: its effective title, time and severity; the first of its shown findings is the primary one.</summary>
    private static SpectreMoment? ToMoment(CaseAnalysis a, KeyEvent k, IReadOnlyDictionary<string, EnginePlain> plain)
    {
        var e = KeyEventOps.Effective(a, k);
        var all = k.FindingIds.Select(a.Find).Where(i => i != null).Select(i => i!).ToList();
        var shown = all.Where(i => AnalysisView.IsActive(a, i) && AnalysisView.ReviewOf(i) != ReviewState.Rejected).ToList();
        var i = shown.FirstOrDefault() ?? all.FirstOrDefault();
        if (i == null) return null;
        var p = PlainOf(i, plain);
        var tactic = e.Tactic ?? TacticOf(i);
        var promoted = k.PromotedReason ?? p?.PromotedReason ?? "";
        var why = k.Notes.Length > 0 ? k.Notes : p?.Why is { Length: > 0 } w ? w : i.Notes.Length > 0 ? i.Notes : i.Description;
        var reason = promoted.Length > 0 ? promoted : why;
        var lasts = shown.Where(f => f.LastSeen != null).Select(f => f.LastSeen!.Value).ToList();
        var end = e.Time is { } t && lasts.Count > 0 && lasts.Max() > t ? lasts.Max() : (DateTimeOffset?)null;
        var ids = shown.Select(SourceId).ToList();
        return new SpectreMoment(MomentIdOf(k), 5 - Math.Max(0, SpectreFinding.Rank(e.Severity)), e.Time, SourceId(i), ids, e.Title, null, reason, null, false, 0)
        {
            End = end,
            PlainTitle = e.Title,
            Why = why,
            CantTell = p?.CantTell,
            EvidenceLine = shown.Count > 1 ? $"{shown.Count} findings" : p?.EvidenceLine is { Length: > 0 } el ? el : EvidenceLineOf(a, i),
            Phase = p?.Phase is { Length: > 0 } ph ? ph : PlainLanguage.Phase(tactic, e.Mitre.FirstOrDefault()),
            PromotedReason = promoted,
        };
    }

    /// <summary>Plain-language text from an engine timeline, keyed by the engine's finding id (kept in memory for Spectre items).</summary>
    public static Dictionary<string, EnginePlain> PlainFrom(SpectreTimeline tl)
    {
        var d = new Dictionary<string, EnginePlain>(StringComparer.Ordinal);
        foreach (var f in tl.Findings.Concat(tl.Undated))
            d[f.FindingId] = new EnginePlain(f.PlainTitle, "", null, "", f.Phase ?? "", "", null, f.Host, f.ArtifactType);
        foreach (var m in tl.Moments)
        {
            var ids = m.PrimaryFindingId != null ? new[] { m.PrimaryFindingId } : m.FindingIds.Take(1).ToArray();
            foreach (var id in ids)
            {
                d.TryGetValue(id, out var prev);
                d[id] = new EnginePlain(m.PlainTitle, m.Why, m.CantTell, m.EvidenceLine, m.Phase, m.PromotedReason, m.Id, prev?.RawHost, prev?.ArtifactType);
            }
        }
        return d;
    }
}
