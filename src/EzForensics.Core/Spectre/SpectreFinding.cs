using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzForensics.Core.Spectre;

/// <summary>One evidence row the engine names: a case CSV (<c>case_path</c>, forward slashes), its 0-based data row and the record id it saw.</summary>
public sealed record SpectreEvidenceRef(string CasePath, long Row, string? RecordId)
{
    /// <summary>Who named the row: "spectre" (or absent, older engines) or "analyst" (a row the examiner added).</summary>
    public string? By { get; init; }
    /// <summary>The examiner's label on the row (<see cref="EvidenceFlagLabels"/>), when set.</summary>
    public string? AnalystLabel { get; init; }
    /// <summary>The row was added by the examiner, not named by Spectre.</summary>
    public bool IsAnalyst => string.Equals(By, "analyst", StringComparison.OrdinalIgnoreCase);

    /// <summary>The engine's <c>evidence</c> list (absent on older engines: empty). Entries without a path or a row are skipped.</summary>
    internal static IReadOnlyList<SpectreEvidenceRef> ListFrom(JsonObject o)
    {
        if (o["evidence"] is not JsonArray a) return Array.Empty<SpectreEvidenceRef>();
        var list = new List<SpectreEvidenceRef>();
        foreach (var e in a.OfType<JsonObject>())
        {
            var path = SpectreJson.Str(e, "case_path");
            var row = SpectreJson.Long(e, "row");
            if (string.IsNullOrWhiteSpace(path) || row is null or < 0) continue;
            list.Add(new SpectreEvidenceRef(path.Trim(), row.Value, SpectreJson.Str(e, "record_id"))
            {
                By = SpectreJson.Str(e, "by"), AnalystLabel = SpectreJson.Str(e, "analyst_label"),
            });
        }
        return list;
    }
}

/// <summary>Tolerant readers for the engine's JSON: a missing or oddly typed field gives a default, never an exception.</summary>
internal static class SpectreJson
{
    public static JsonObject? Parse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static string? Str(JsonNode? n, string key) => n is JsonObject o && o[key] is JsonValue v ? Text(v) : null;

    public static string Text(JsonValue v) =>
        v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"');

    public static bool? Bool(JsonNode? n, string key)
    {
        if (n is not JsonObject o || o[key] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<int>(out var i)) return i != 0;
        return v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) ? p : null;
    }

    public static int? Int(JsonNode? n, string key)
    {
        if (n is not JsonObject o || o[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null;
    }

    public static long? Long(JsonNode? n, string key)
    {
        if (n is not JsonObject o || o[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : null;
    }

    public static double? Double(JsonNode? n, string key)
    {
        if (n is not JsonObject o || o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    /// <summary>An ISO-8601 time as UTC; null when absent or not clearly ISO.</summary>
    public static DateTimeOffset? Time(JsonNode? n, string key) => SpectreTime.Parse(Str(n, key));

    /// <summary>A list given as an array, or as one string ("T1059, T1003.001").</summary>
    public static IReadOnlyList<string> StrList(JsonNode? n, string key)
    {
        if (n is not JsonObject o) return Array.Empty<string>();
        var node = o[key];
        if (node is JsonArray a)
            return a.OfType<JsonValue>().Select(Text).Where(s => s.Length > 0).ToList();
        if (node is JsonValue v)
            return Text(v).Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return Array.Empty<string>();
    }
}

/// <summary>The examiner's overrides of one finding (the engine's <c>analyst</c> block). A null field means "not overridden".</summary>
public sealed record SpectreAnalystEdit(string? Title, string? Description, string? Severity, IReadOnlyList<string>? MitreAttack, string? Notes,
    bool? IncludeInReport, string? EditedBy, string? EditedAt, int Revision)
{
    /// <summary>Whether the finding is a key event: "pin", "unpin" or "auto" (null = not set, same as auto).</summary>
    public string? KeyMoment { get; init; }
    /// <summary>The examiner's caption for the finding's key event.</summary>
    public string? MomentCaption { get; init; }

    public static SpectreAnalystEdit? From(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        return new SpectreAnalystEdit(
            SpectreJson.Str(o, "title"), SpectreJson.Str(o, "description"), SpectreJson.Str(o, "severity"),
            o["mitre_attack"] is null ? null : SpectreJson.StrList(o, "mitre_attack"),
            SpectreJson.Str(o, "notes"), SpectreJson.Bool(o, "include_in_report"),
            SpectreJson.Str(o, "edited_by"), SpectreJson.Str(o, "edited_at"), SpectreJson.Int(o, "revision") ?? 0)
        {
            KeyMoment = SpectreJson.Str(o, "key_moment"), MomentCaption = SpectreJson.Str(o, "moment_caption"),
        };
    }

    public bool HasOverride => Title != null || Description != null || Severity != null || MitreAttack != null || Notes != null || IncludeInReport != null
        || KeyMoment != null || MomentCaption != null;
}

/// <summary>One finding as the engine reports it, plus the examiner's overrides. The UI shows <c>Effective*</c> and keeps the raw values for comparison.</summary>
public sealed record SpectreFinding(
    string Id, string FindingId, string Title, string Severity, string? Confidence, IReadOnlyList<string> MitreAttack, string? ArtifactType,
    string? Status, string Description, string? Approval, bool IncludeInReport, bool Edited,
    string? EventTime, string? FirstSeen, string? LastSeen, SpectreAnalystEdit? Analyst, IReadOnlyList<string> FkCaveats)
{
    /// <summary>Evidence file the finding came from (case-relative or engine path) and the record in it; null when the engine did not say.</summary>
    public string? SourceFile { get; init; }
    public string? SourceRecordId { get; init; }
    /// <summary>The same file as the app's case-relative path (forward slashes), when the engine could map it; preferred over <see cref="SourceFile"/> to open the record.</summary>
    public string? CasePath { get; init; }
    /// <summary>The records behind the finding (engine <c>evidence</c>; empty for older engines) and how many there are in all (<c>evidence_total</c>; the list may be cut).</summary>
    public IReadOnlyList<SpectreEvidenceRef> Evidence { get; init; } = Array.Empty<SpectreEvidenceRef>();
    public int EvidenceTotal { get; init; }
    /// <summary>The examiner's flags on the evidence rows (engine <c>analyst_flags</c>; empty for older engines).</summary>
    public IReadOnlyList<SpectreEvidenceFlag> AnalystFlags { get; init; } = Array.Empty<SpectreEvidenceFlag>();
    /// <summary>"analyst" for a finding the examiner created; null (or "spectre") for Spectre's own.</summary>
    public string? Origin { get; init; }
    /// <summary>"flag" or "key_event" (newer engines); null when the engine does not say (the app derives it).</summary>
    public string? Stage { get; init; }
    /// <summary>suspicious, malicious or interesting (newer engines); null when absent.</summary>
    public string? FlagLabel { get; init; }
    public bool IsAnalystFinding => string.Equals(Origin, "analyst", StringComparison.OrdinalIgnoreCase);

    public const string Approved = "APPROVED", Rejected = "REJECTED", Superseded = "SUPERSEDED";
    public static readonly IReadOnlyList<string> Severities = new[] { "Info", "Low", "Medium", "High", "Critical" };

    public string EffectiveTitle => Analyst?.Title ?? Title;
    public string EffectiveDescription => Analyst?.Description ?? Description;
    public string EffectiveSeverity => Analyst?.Severity ?? Severity;
    public IReadOnlyList<string> EffectiveMitreAttack => Analyst?.MitreAttack ?? MitreAttack;
    public string? Notes => Analyst?.Notes;
    public bool IsIncluded => Analyst?.IncludeInReport ?? IncludeInReport;
    public bool IsEdited => Edited || Analyst?.HasOverride == true;

    /// <summary>0 (Info) to 4 (Critical); -1 for an unknown label. For sorting.</summary>
    public int SeverityRank => Rank(EffectiveSeverity);
    public static int Rank(string? severity) =>
        Severities.ToList().FindIndex(s => string.Equals(s, severity, StringComparison.OrdinalIgnoreCase));

    /// <summary>Approval for display: the decision, or "Pending" when none was made.</summary>
    public string ApprovalText => Approval ?? "Pending";

    public static SpectreFinding From(JsonObject o)
    {
        var findingId = SpectreJson.Str(o, "finding_id") ?? SpectreJson.Str(o, "id") ?? "";
        return new SpectreFinding(
            SpectreJson.Str(o, "id") ?? findingId, findingId,
            SpectreJson.Str(o, "title") ?? "", SpectreJson.Str(o, "severity") ?? "Info", SpectreJson.Str(o, "confidence"),
            SpectreJson.StrList(o, "mitre_attack"), SpectreJson.Str(o, "artifact_type"), SpectreJson.Str(o, "status"),
            SpectreJson.Str(o, "description") ?? "", SpectreJson.Str(o, "approval"),
            SpectreJson.Bool(o, "include_in_report") ?? true, SpectreJson.Bool(o, "edited") ?? false,
            SpectreJson.Str(o, "event_time"), SpectreJson.Str(o, "first_seen"), SpectreJson.Str(o, "last_seen"),
            SpectreAnalystEdit.From(o["analyst"]), SpectreJson.StrList(o, "fk_caveats"))
        {
            SourceFile = SpectreJson.Str(o, "source_file"), SourceRecordId = SpectreJson.Str(o, "source_record_id"),
            CasePath = SpectreJson.Str(o, "case_path"),
            Evidence = SpectreEvidenceRef.ListFrom(o), EvidenceTotal = Math.Max(SpectreJson.Int(o, "evidence_total") ?? 0, SpectreEvidenceRef.ListFrom(o).Count),
            AnalystFlags = SpectreEvidenceFlag.ListFrom(o["analyst_flags"] as JsonArray, findingId), Origin = SpectreJson.Str(o, "origin"),
            Stage = SpectreJson.Str(o, "stage"), FlagLabel = SpectreJson.Str(o, "flag_label"),
        };
    }
}

/// <summary>Result of <c>spectre_findings_list</c>. <paramref name="Unregistered"/> counts findings the engine could not register (0 when absent).</summary>
public sealed record SpectreFindingList(IReadOnlyList<SpectreFinding> Findings, int Unregistered);

/// <summary>One audit row of a finding's history.</summary>
public sealed record SpectreHistoryRow(string Ts, string Action, string? Tool, string? Examiner, JsonNode? Params)
{
    public static SpectreHistoryRow From(JsonObject o) =>
        new(SpectreJson.Str(o, "ts") ?? "", SpectreJson.Str(o, "action") ?? "", SpectreJson.Str(o, "tool"), SpectreJson.Str(o, "examiner"),
            o["params"]?.DeepClone());
}

/// <summary>Reply of <c>spectre_finding_edit</c>. <paramref name="Ok"/> false is a refusal by the engine (see <paramref name="Reason"/>), not a transport error.</summary>
public sealed record SpectreEditResult(bool Ok, string? Reason, string? FindingId, int Revision, string? Sha256);

/// <summary>Reply of <c>spectre_finding_approve</c>.</summary>
public sealed record SpectreApproveResult(bool Ok, string? Reason, string? FindingId, string? Decision, string? PreviousApproval, string? Sha256);

/// <summary>Reply of <c>spectre_report_generate</c>. Paths are already mapped to Windows paths (<c>Pdf</c>/<c>Docx</c> null when not produced).</summary>
public sealed record SpectreReportResult(bool Ok, string Markdown, string? ReportMd, string? Pdf, string? Docx,
    IReadOnlyDictionary<string, string> Skipped, int FindingsIncluded, int FindingsExcluded);

/// <summary>Free-form engine reply (run, verify): the usual flags plus the raw text for display.</summary>
public sealed record SpectreToolReply(bool? Ok, string Text, JsonObject? Data)
{
    public static SpectreToolReply From(string text)
    {
        var o = SpectreJson.Parse(text);
        return new SpectreToolReply(SpectreJson.Bool(o, "ok") ?? SpectreJson.Bool(o, "verified") ?? SpectreJson.Bool(o, "valid"), text, o);
    }
}

/// <summary>Reply of <c>spectre_events_query</c>: the event rows (a bare array, or an object with <c>events</c>/<c>rows</c>).</summary>
public sealed record SpectreEvents(IReadOnlyList<JsonObject> Rows, string Text);

/// <summary>Engine availability from <c>spectre_status</c>.</summary>
public sealed record SpectreStatus(string? Version, JsonObject? Data, string Text)
{
    public static SpectreStatus From(string text)
    {
        var o = SpectreJson.Parse(text);
        return new SpectreStatus(SpectreJson.Str(o, "version") ?? SpectreJson.Str(o, "spectre_version"), o, text);
    }
}

/// <summary>The MCP tool answered with isError (bad case id, engine refusal, ...). <see cref="Exception.Message"/> is the engine's text.</summary>
public sealed class SpectreToolException : Exception
{
    public string Tool { get; }

    /// <summary>The engine's raw reply when it could not be read. For the log only; never show it.</summary>
    public string? RawReply { get; }

    public SpectreToolException(string tool, string message, string? rawReply = null) : base(message) { Tool = tool; RawReply = rawReply; }
}
