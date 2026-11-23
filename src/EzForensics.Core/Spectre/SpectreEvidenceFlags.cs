using System.Text.Json.Nodes;

namespace EzForensics.Core.Spectre;

/// <summary>
/// The labels on evidence rows (docs/evidence-flags-contract.md). <see cref="Evidence"/> is Spectre's (a row of the finding's evidence list,
/// never stored as a flag); the others are the examiner's. A row has at most one of confirmed / not relevant / added, plus an optional note.
/// </summary>
public static class EvidenceFlagLabels
{
    public const string Evidence = "evidence", Confirmed = "confirmed", NotRelevant = "not_relevant", Added = "added", Note = "note", Clear = "clear";

    /// <summary>The examiner's row labels (one at a time per row).</summary>
    public static readonly IReadOnlyList<string> AnalystLabels = new[] { Confirmed, NotRelevant, Added };

    public static bool IsAnalystLabel(string? label) => label is Confirmed or NotRelevant or Added;

    /// <summary>Display text: Spectre's label says it is Spectre's; the examiner's never read as Spectre's conclusion.</summary>
    public static string Text(string? label) => label switch
    {
        Evidence => "Spectre evidence",
        Confirmed => "Confirmed by analyst",
        NotRelevant => "Not relevant (analyst)",
        Added => "Added by analyst",
        Note => "Analyst note",
        _ => "",
    };
}

/// <summary>One examiner flag on an evidence row, as the engine stores it. <paramref name="At"/> is an ISO-8601 UTC time.</summary>
public sealed record SpectreEvidenceFlag(string? FindingId, string CasePath, long Row, string Label, string? Note, string? Analyst, string? At)
{
    public static SpectreEvidenceFlag? From(JsonObject o, string? findingId = null)
    {
        var path = SpectreJson.Str(o, "case_path");
        var row = SpectreJson.Long(o, "row");
        var label = SpectreJson.Str(o, "label");
        if (string.IsNullOrWhiteSpace(path) || row is null or < 0 || string.IsNullOrWhiteSpace(label)) return null;
        return new SpectreEvidenceFlag(SpectreJson.Str(o, "finding_id") ?? findingId, path.Trim(), row.Value, label.Trim().ToLowerInvariant(),
            SpectreJson.Str(o, "note"), SpectreJson.Str(o, "analyst"), SpectreJson.Str(o, "at"));
    }

    /// <summary>A flag list (malformed entries skipped); <paramref name="findingId"/> fills entries that do not name their finding.</summary>
    public static IReadOnlyList<SpectreEvidenceFlag> ListFrom(JsonArray? a, string? findingId = null) =>
        a == null ? Array.Empty<SpectreEvidenceFlag>() : a.OfType<JsonObject>().Select(o => From(o, findingId)).Where(f => f != null).Select(f => f!).ToList();
}

/// <summary>Reply of <c>spectre_evidence_flag</c>. <paramref name="Ok"/> false is a refusal (see <paramref name="Reason"/>).</summary>
public sealed record SpectreFlagResult(bool Ok, string? Reason, string? FindingId, SpectreEvidenceFlag? Flag);

/// <summary>Reply of <c>spectre_finding_create</c>.</summary>
public sealed record SpectreCreateResult(bool Ok, string? Reason, string? FindingId);
