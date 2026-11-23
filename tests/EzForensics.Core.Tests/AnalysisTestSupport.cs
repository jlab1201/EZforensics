using System.Text.Json.Nodes;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.Core.Tests;

/// <summary>Builders shared by the Analysis tests.</summary>
internal static class AT
{
    public static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    public static SyncOptions Opt(IAnalysisRowReader? reader = null, Func<string, string?>? host = null) => new() { Now = Now, RowReader = reader, HostForCasePath = host };

    public static AnalysisRow R(string csv, long row) => new(csv, row, "fp");
    public static AnalysisRow[] Rows(string csv, params long[] rows) => rows.Select(r => R(csv, r)).ToArray();

    public static FlagCsv Flags(string csv, string label, params long[] rows) => new(csv, label, rows.Select(r => R(csv, r)).ToList());

    public static string Json(CaseAnalysis a) => CaseAnalysisStore.ToJson(a);

    public static AnalysisItem ItemOfWorkspace(CaseAnalysis a, string wid) => a.Items.Single(i => i.Source.WorkspaceId == wid);
    public static AnalysisItem SpItem(CaseAnalysis a, string fid) => a.Items.Single(i => i.Source.SpectreFindingId == fid);

    /// <summary>An engine finding as JSON. <paramref name="evidence"/> are (case_path,row) pairs.</summary>
    public static SpectreFinding Finding(string id, string title = "Odd thing", string sev = "Medium", string[]? mitre = null, (string path, long row)[]? evidence = null,
        string? stage = null, string? flagLabel = null, string? origin = null, string? approval = null, JsonObject? analyst = null, JsonArray? analystFlags = null,
        string? first = "2026-11-01T10:00:00Z", string? last = null, string desc = "D", JsonArray? evidenceJson = null)
    {
        var o = new JsonObject
        {
            ["finding_id"] = id, ["id"] = id, ["title"] = title, ["severity"] = sev, ["description"] = desc,
            ["mitre_attack"] = new JsonArray((mitre ?? new[] { "T1059" }).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["evidence"] = evidenceJson ?? new JsonArray((evidence ?? new[] { ("PC1/a.csv", 1L) }).Select(e => (JsonNode)new JsonObject { ["case_path"] = e.path, ["row"] = e.row }).ToArray()),
        };
        if (stage != null) o["stage"] = stage;
        if (flagLabel != null) o["flag_label"] = flagLabel;
        if (origin != null) o["origin"] = origin;
        if (approval != null) o["approval"] = approval;
        if (analyst != null) o["analyst"] = analyst;
        if (analystFlags != null) o["analyst_flags"] = analystFlags;
        if (first != null) o["first_seen"] = first;
        if (last != null) o["last_seen"] = last;
        return SpectreFinding.From(o);
    }

    /// <summary>A timeline with one entry per finding id (host given) and key moments over the listed ids.</summary>
    public static SpectreTimeline TL(string[]? momentIds = null, string? host = "PC1", params string[] findingIds)
    {
        var o = new JsonObject
        {
            ["findings"] = new JsonArray(findingIds.Select(f => (JsonNode)new JsonObject
                { ["finding_id"] = f, ["id"] = f, ["title"] = "t", ["severity"] = "Medium", ["host"] = host, ["start"] = "2026-11-01T10:00:00Z" }).ToArray()),
            ["moments"] = new JsonArray((momentIds ?? Array.Empty<string>()).Select((m, i) => (JsonNode)new JsonObject
                { ["id"] = "M" + i, ["rank"] = i + 1, ["primary_finding_id"] = m, ["finding_ids"] = new JsonArray(m), ["headline"] = "h", ["time"] = "2026-11-01T10:00:00Z" }).ToArray()),
        };
        return SpectreTimeline.From(o);
    }

    public static SyncResult Import(CaseAnalysis a, SpectreFinding[] f, SpectreTimeline? t = null) =>
        SpectreImport.Import(a, f, t, new ImportOptions { Now = Now });

    public static AnalysisItem KeyEvent(CaseAnalysis a, string id, string title, IEnumerable<AnalysisRow> rows, string? host = null, DateTimeOffset? first = null, string[]? mitre = null)
    {
        var i = new AnalysisItem
        {
            Id = id, Origin = ItemOrigin.Analyst, Title = title, Review = ReviewState.ToReview,
            SourceRows = rows.ToList(), Host = host, FirstSeen = first, Mitre = mitre?.ToList() ?? new(),
        };
        a.Items.Add(i);
        return i;
    }
}
