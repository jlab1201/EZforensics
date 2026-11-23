using System.Globalization;
using System.Text.Json.Nodes;
using EzForensics.Core.Timeline;

namespace EzForensics.Core.Spectre;

/// <summary>Which grouping the timeline swimlanes use.</summary>
public enum TimelineLaneMode { Tactic, Host, Artifact }

/// <summary>One finding on the timeline (<c>spectre_timeline</c> row). <see cref="Start"/>/<see cref="End"/> are UTC, null when absent or unparseable.</summary>
public sealed record SpectreTimelineFinding(
    string Id, string FindingId, string Title, string Severity, string? Confidence, string? Tactic, string? Technique, string? Host,
    string? ArtifactType, DateTimeOffset? Start, DateTimeOffset? End, int OccurrenceCount, IReadOnlyList<string> ClusterIds,
    string? Approval, bool IncludeInReport, bool Edited, string KeyMoment, string? SourceFile, string? SourceRecordId)
{
    /// <summary>Case-relative path of the evidence file (forward slashes), when the engine could map it.</summary>
    public string? CasePath { get; init; }

    /// <summary>The records behind the finding (engine <c>evidence</c>; empty for older engines) and how many there are in all.</summary>
    public IReadOnlyList<SpectreEvidenceRef> Evidence { get; init; } = Array.Empty<SpectreEvidenceRef>();
    public int EvidenceTotal { get; init; }

    /// <summary>Title without file paths, in plain words (engine <c>plain_title</c>; derived from <see cref="Title"/> for older engines).</summary>
    public string PlainTitle { get; init; } = "";

    /// <summary>Phase ("Initial Access", ...): the engine's <c>phase</c>, else the glossary name of <see cref="Tactic"/>.</summary>
    public string? Phase { get; init; }

    /// <summary>End of the range, or <see cref="Start"/> for a point in time.</summary>
    public DateTimeOffset? EffectiveEnd => End ?? Start;
    public bool IsRange => Start != null && End != null && End > Start;
    public int SeverityRank => SpectreFinding.Rank(Severity);

    /// <summary>Lane name for the given mode; "(none)" when the field is empty.</summary>
    public string Lane(TimelineLaneMode mode)
    {
        var v = mode switch { TimelineLaneMode.Tactic => Tactic, TimelineLaneMode.Host => Host, _ => ArtifactType };
        return string.IsNullOrWhiteSpace(v) ? SpectreTimeline.NoLane : v;
    }

    public static SpectreTimelineFinding From(JsonObject o)
    {
        var findingId = SpectreJson.Str(o, "finding_id") ?? SpectreJson.Str(o, "id") ?? "";
        return new SpectreTimelineFinding(
            SpectreJson.Str(o, "id") ?? findingId, findingId, SpectreJson.Str(o, "title") ?? "", SpectreJson.Str(o, "severity") ?? "Info",
            SpectreJson.Str(o, "confidence"), SpectreJson.Str(o, "tactic"), SpectreJson.Str(o, "technique"), SpectreJson.Str(o, "host"),
            SpectreJson.Str(o, "artifact_type"), SpectreJson.Time(o, "start"), SpectreJson.Time(o, "end"),
            Math.Max(1, SpectreJson.Int(o, "occurrence_count") ?? 1), SpectreJson.StrList(o, "cluster_ids"),
            SpectreJson.Str(o, "approval"), SpectreJson.Bool(o, "include_in_report") ?? true, SpectreJson.Bool(o, "edited") ?? false,
            SpectreJson.Str(o, "key_moment") ?? "auto", SpectreJson.Str(o, "source_file"), SpectreJson.Str(o, "source_record_id"))
        {
            CasePath = SpectreJson.Str(o, "case_path"),
            Evidence = SpectreEvidenceRef.ListFrom(o), EvidenceTotal = Math.Max(SpectreJson.Int(o, "evidence_total") ?? 0, SpectreEvidenceRef.ListFrom(o).Count),
            PlainTitle = Nz(SpectreJson.Str(o, "plain_title")) ?? PlainLanguage.TitleWithoutPaths(SpectreJson.Str(o, "title") ?? ""),
            Phase = PlainLanguage.MigratePhase(Nz(SpectreJson.Str(o, "phase"))) ?? PlainLanguage.Phase(SpectreJson.Str(o, "tactic"), SpectreJson.Str(o, "technique")),
        };
    }

    internal static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>A temporal or IOC cluster: a shaded band over <see cref="Start"/>..<see cref="End"/>.</summary>
public sealed record SpectreCluster(string Id, string Kind, DateTimeOffset? Start, DateTimeOffset? End, IReadOnlyList<string> FindingIds, string? Label)
{
    public static SpectreCluster From(JsonObject o) =>
        new(SpectreJson.Str(o, "id") ?? "", SpectreJson.Str(o, "kind") ?? "temporal", SpectreJson.Time(o, "start"), SpectreJson.Time(o, "end"),
            SpectreJson.StrList(o, "finding_ids"), SpectreJson.Str(o, "label"));
}

/// <summary>A key event (numbered pin). <see cref="Rank"/> is the engine's importance (1 = most important); <see cref="Number"/> is the position in time order.</summary>
public sealed record SpectreMoment(
    string Id, int Rank, DateTimeOffset? Time, string? PrimaryFindingId, IReadOnlyList<string> FindingIds, string Headline,
    string? ReasonKind, string? Reason, string? Caption, bool Pinned, double Score)
{
    /// <summary>Position in time order, 1 = earliest (ties by <see cref="Rank"/>; undated moments come last, by rank). Set by <see cref="SpectreTimeline.From"/>.</summary>
    public int Number { get; init; }

    /// <summary>End of a moment that spans a period; null for a point in time.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Headline in plain words without file paths.</summary>
    public string PlainTitle { get; init; } = "";

    /// <summary>Why this moment matters, one sentence, as the data says it (engine <c>why</c>; falls back to <see cref="Reason"/>).</summary>
    public string Why { get; init; } = "";

    /// <summary>What the data cannot show, when the engine says so; null otherwise.</summary>
    public string? CantTell { get; init; }

    /// <summary>Short line naming the evidence ("Windows security log, 3 events").</summary>
    public string EvidenceLine { get; init; } = "";

    /// <summary>Why Spectre promoted this flag to a key event (engine <c>promoted_reason</c>); empty when the engine does not say.</summary>
    public string PromotedReason { get; init; } = "";

    /// <summary>Plain-language phase of the story ("Initial Access", ..., "Unmapped").</summary>
    public string Phase { get; init; } = "";

    public bool IsRange => Time != null && End != null && End > Time;

    public static SpectreMoment From(JsonObject o) =>
        new(SpectreJson.Str(o, "id") ?? "", SpectreJson.Int(o, "rank") ?? 0, SpectreJson.Time(o, "time"), SpectreJson.Str(o, "primary_finding_id"),
            SpectreJson.StrList(o, "finding_ids"), SpectreJson.Str(o, "headline") ?? "", SpectreJson.Str(o, "reason_kind"), SpectreJson.Str(o, "reason"),
            SpectreJson.Str(o, "caption"), SpectreJson.Bool(o, "pinned") ?? false, SpectreJson.Double(o, "score") ?? 0)
        {
            End = SpectreJson.Time(o, "end"),
            PlainTitle = SpectreTimelineFinding.Nz(SpectreJson.Str(o, "plain_title")) ?? "",
            Why = SpectreTimelineFinding.Nz(SpectreJson.Str(o, "why")) ?? "",
            CantTell = SpectreTimelineFinding.Nz(SpectreJson.Str(o, "cant_tell")),
            EvidenceLine = SpectreTimelineFinding.Nz(SpectreJson.Str(o, "evidence_line")) ?? "",
            PromotedReason = SpectreTimelineFinding.Nz(SpectreJson.Str(o, "promoted_reason")) ?? "",
            Phase = PlainLanguage.MigratePhase(SpectreTimelineFinding.Nz(SpectreJson.Str(o, "phase"))) ?? "",
        };

    /// <summary>Fills what an older engine did not send, from the moment's own fields and its primary finding.</summary>
    internal SpectreMoment WithFallbacks(IReadOnlyDictionary<string, SpectreTimelineFinding> byId)
    {
        SpectreTimelineFinding? f = null;
        if (PrimaryFindingId != null) byId.TryGetValue(PrimaryFindingId, out f);
        f ??= FindingIds.Select(i => byId.TryGetValue(i, out var x) ? x : null).FirstOrDefault(x => x != null);
        var evidence = EvidenceLine;
        if (evidence.Length == 0 && f != null)
        {
            evidence = PlainLanguage.Artifact(f.ArtifactType);
            if (f.OccurrenceCount > 1) evidence += $", {f.OccurrenceCount:N0} events";
            if (!string.IsNullOrWhiteSpace(f.Host)) evidence += $" on {f.Host}";
        }
        return this with
        {
            End = End ?? (f?.End is { } fe && Time != null && fe > Time ? fe : null),
            PlainTitle = PlainTitle.Length > 0 ? PlainTitle : PlainLanguage.TitleWithoutPaths(Headline),
            Why = Why.Length > 0 ? Why : Reason ?? "",
            EvidenceLine = evidence,
            Phase = Phase.Length > 0 ? Phase : PlainLanguage.Phase(f?.Tactic, f?.Technique),
        };
    }
}

/// <summary>The opening facts of the story: one sentence and the counts behind it (all times UTC).</summary>
public sealed record SpectreTimelineSummary(
    string Sentence, int Moments, DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen, int Hosts, int NeedReview, int Undated, int Minor)
{
    internal static SpectreTimelineSummary? From(JsonObject? o)
    {
        if (o == null) return null;
        int Count(string key) => o[key] switch { JsonArray a => a.Count, _ => SpectreJson.Int(o, key) ?? 0 };
        return new SpectreTimelineSummary(SpectreJson.Str(o, "sentence") ?? "", Count("moments"), SpectreJson.Time(o, "first_seen"), SpectreJson.Time(o, "last_seen"),
            Count("hosts"), Count("need_review"), Count("undated"), Count("minor"));
    }
}

/// <summary>Reply of <c>spectre_moment_explain</c>: Spectre's short read with the finding ids it cites. <see cref="Cached"/> = served from the case cache.</summary>
public sealed record SpectreMomentExplanation(string MomentId, string Text, IReadOnlyList<string> Cites, bool Cached)
{
    public static SpectreMomentExplanation From(JsonObject? o, string fallbackMomentId) =>
        new(SpectreJson.Str(o, "moment_id") ?? fallbackMomentId, SpectreJson.Str(o, "text") ?? "", SpectreJson.StrList(o, "cites"),
            SpectreJson.Bool(o, "cached") ?? false);
}

/// <summary>Reply of <c>spectre_timeline</c>. All times are UTC (<see cref="TimeBasis"/>).</summary>
public sealed record SpectreTimeline(
    string? Case, DateTimeOffset? GeneratedAt, string TimeBasis,
    IReadOnlyList<string> TacticLanes, IReadOnlyList<string> HostLanes, IReadOnlyList<string> ArtifactLanes,
    IReadOnlyList<SpectreTimelineFinding> Findings, IReadOnlyList<SpectreTimelineFinding> Undated,
    IReadOnlyList<SpectreCluster> Clusters, IReadOnlyList<SpectreMoment> Moments)
{
    public const string NoLane = "(none)";

    /// <summary>Sentence and counts for the top of the story. Set by <see cref="From"/> (computed here when an older engine sends none).</summary>
    public SpectreTimelineSummary Summary { get; init; } = new("", 0, null, null, 0, 0, 0, 0);

    /// <summary>True when the engine (or, for older engines, this check) says the dates look wrong and the analysis should be run again.</summary>
    public bool AnalysisStale { get; init; }

    /// <summary>Why <see cref="AnalysisStale"/> is true; empty otherwise.</summary>
    public string StaleReason { get; init; } = "";

    public static SpectreTimeline Empty { get; } = new(null, null, "UTC", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<SpectreTimelineFinding>(), Array.Empty<SpectreTimelineFinding>(), Array.Empty<SpectreCluster>(), Array.Empty<SpectreMoment>());

    /// <summary>Lane names for the mode, in the engine's order (tactics in ATT&amp;CK order); lanes only seen on findings are appended, "(none)" last.</summary>
    public IReadOnlyList<string> LanesFor(TimelineLaneMode mode)
    {
        var declared = mode switch { TimelineLaneMode.Tactic => TacticLanes, TimelineLaneMode.Host => HostLanes, _ => ArtifactLanes };
        var lanes = new List<string>(declared.Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        bool none = false;
        foreach (var f in Findings)
        {
            var l = f.Lane(mode);
            if (l == NoLane) { none = true; continue; }
            if (!lanes.Contains(l, StringComparer.OrdinalIgnoreCase)) lanes.Add(l);
        }
        if (none) lanes.Add(NoLane);
        return lanes;
    }

    public static SpectreTimeline From(JsonObject? o)
    {
        if (o == null) return Empty;
        var lanes = o["lanes"] as JsonObject;
        static IReadOnlyList<SpectreTimelineFinding> Rows(JsonNode? n) =>
            (n as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(SpectreTimelineFinding.From).ToList();
        var findings = Rows(o["findings"]);
        var undated = Rows(o["undated"]);
        var all = findings.Concat(undated).ToList();
        var byId = new Dictionary<string, SpectreTimelineFinding>();
        foreach (var f in all)
        {
            byId.TryAdd(f.FindingId, f);
            byId.TryAdd(f.Id, f);
        }
        var moments = (o["moments"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(SpectreMoment.From)
            .Select(m => m.WithFallbacks(byId)).ToList();
        moments = moments.OrderBy(m => m.Time == null ? 1 : 0).ThenBy(m => m.Time).ThenBy(m => m.Rank)
            .Select((m, i) => m with { Number = i + 1 }).ToList();

        var summary = SpectreTimelineSummary.From(o["summary"] as JsonObject);
        if (summary == null || summary.Sentence.Length == 0)
        {
            var times = moments.Where(m => m.Time != null).SelectMany(m => new[] { m.Time, m.End }).Where(t => t != null).Select(t => t!.Value).ToList();
            DateTimeOffset? first = summary?.FirstSeen ?? (times.Count > 0 ? times.Min() : null);
            DateTimeOffset? last = summary?.LastSeen ?? (times.Count > 0 ? times.Max() : null);
            int hosts = summary?.Hosts is > 0 ? summary.Hosts : all.Select(f => f.Host).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int need = summary?.NeedReview is > 0 ? summary.NeedReview : all.Count(f => string.IsNullOrWhiteSpace(f.Approval) || f.Approval.Equals("PENDING", StringComparison.OrdinalIgnoreCase));
            int und = summary?.Undated is > 0 ? summary.Undated : undated.Count;
            var inMoments = new HashSet<string>(moments.SelectMany(m => m.FindingIds));
            int minor = summary?.Minor is > 0 ? summary.Minor : all.Count(f => !inMoments.Contains(f.FindingId) && !inMoments.Contains(f.Id));
            summary = new SpectreTimelineSummary(PlainLanguage.SummarySentence(moments.Count, first, last, hosts, und), moments.Count, first, last, hosts, need, und, minor);
        }

        bool stale;
        string reason = SpectreJson.Str(o, "stale_reason") ?? "";
        if (o["analysis_stale"] != null) stale = SpectreJson.Bool(o, "analysis_stale") ?? false;
        else
        {
            stale = all.Count > 0 && all.All(f => f.Start == null && string.Equals(f.ArtifactType, "logfile_record", StringComparison.OrdinalIgnoreCase));
            if (stale && reason.Length == 0)
                reason = "Every finding is undated and comes from log entries, so the dates may not have been read. Run the analysis again after updating Spectre.";
        }

        return new SpectreTimeline(
            SpectreJson.Str(o, "case"), SpectreJson.Time(o, "generated_at"), SpectreJson.Str(o, "time_basis") ?? "UTC",
            SpectreJson.StrList(lanes, "tactic"), SpectreJson.StrList(lanes, "host"), SpectreJson.StrList(lanes, "artifact"),
            findings, undated,
            (o["clusters"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(SpectreCluster.From).ToList(),
            moments)
        {
            Summary = summary, AnalysisStale = stale, StaleReason = stale ? reason : "",
        };
    }
}

internal static class SpectreTime
{
    /// <summary>ISO-8601 to UTC. Anything that is not clearly ISO (US dates, free text) gives null: a time is never guessed.</summary>
    public static DateTimeOffset? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (TimelineTimestamp.TryParse(s, out var utc, strict: true) == TimestampParseResult.Ok)
            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        if (s.Length >= 10 && s[4] == '-' && s[7] == '-' && char.IsAsciiDigit(s[0]) &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) && dto.Year > 1601)
            return dto.ToUniversalTime();
        return null;
    }
}
