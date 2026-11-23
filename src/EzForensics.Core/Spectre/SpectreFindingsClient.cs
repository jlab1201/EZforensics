using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Spectre;

/// <summary>Calls one Spectre MCP tool and returns its text. The seam between <see cref="SpectreFindingsClient"/> and the connection (tests fake it).</summary>
public interface ISpectreToolCaller
{
    Task<McpToolResult> CallAsync(string tool, JsonObject args, CancellationToken ct);
}

/// <summary>A plain <see cref="McpClient"/> as caller, with the per-tool timeouts of <see cref="McpAssistantTools"/>.</summary>
public sealed class McpToolCaller : ISpectreToolCaller
{
    private readonly McpClient _client;
    public McpToolCaller(McpClient client) { _client = client; }

    public Task<McpToolResult> CallAsync(string tool, JsonObject args, CancellationToken ct) =>
        _client.CallToolAsync(tool, args, McpAssistantTools.TimeoutFor(tool), ct);
}

/// <summary>
/// Typed access to the Spectre engine's findings, review and report tools. Parses the JSON once so the UI never touches raw MCP text.
/// Engine errors (MCP isError) throw <see cref="SpectreToolException"/>; transport problems surface as IOException / TimeoutException.
/// </summary>
public sealed class SpectreFindingsClient
{
    private readonly ISpectreToolCaller _caller;
    private readonly Func<string?>? _caseDir;
    private readonly string? _wslDistro;

    /// <param name="caseDir">The open EZ case folder; used to find and update <c>.ezassistant\spectre.json</c>.</param>
    /// <param name="wslDistro">Distro that holds the engine's report files (<see cref="EzDistro.Name"/>); null (dev mode, engine on Windows) leaves paths as they are.</param>
    public SpectreFindingsClient(ISpectreToolCaller caller, Func<string?>? caseDir = null, string? wslDistro = EzDistro.Name)
    {
        _caller = caller; _caseDir = caseDir; _wslDistro = wslDistro;
    }

    public async Task<SpectreStatus> StatusAsync(CancellationToken ct) =>
        SpectreStatus.From(await CallAsync("spectre_status", new JsonObject(), ct).ConfigureAwait(false));

    /// <summary>Hands the open case to the engine (<see cref="SpectreCaseHandoff"/>), remembers the link, returns the engine's case id.</summary>
    public async Task<string> OpenCaseAsync(string caseDir, CancellationToken ct)
    {
        var (args, error, handed) = await Task.Run(() => SpectreCaseHandoff.BuildWithFiles(caseDir, ct), ct).ConfigureAwait(false);
        if (args == null) throw new SpectreToolException(SpectreCaseHandoff.EngineTool, error ?? "No case is open.");
        var text = await CallAsync(SpectreCaseHandoff.EngineTool, args, ct).ConfigureAwait(false);
        var o = SpectreJson.Parse(text);
        var id = SpectreJson.Str(o, "case_id") ?? SpectreJson.Str(o, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new SpectreToolException(SpectreCaseHandoff.EngineTool, "The engine did not return a case id: " + text);
        SpectreCaseLink.Save(caseDir, new SpectreCaseLink(id, null, DateTime.UtcNow, handed));
        return id;
    }

    /// <summary>Runs the analysis phases up to <paramref name="throughPhase"/> (long: up to 2 h). Records the phase in the case link on success.</summary>
    public async Task<SpectreToolReply> RunAsync(string caseId, int throughPhase, CancellationToken ct)
    {
        var text = await CallAsync("spectre_run", new JsonObject { ["case_id"] = caseId, ["through_phase"] = throughPhase }, ct).ConfigureAwait(false);
        var reply = SpectreToolReply.From(text);
        if (reply.Ok != false && _caseDir?.Invoke() is { Length: > 0 } dir) SpectreCaseLink.RecordPhase(dir, throughPhase);
        return reply;
    }

    public async Task<SpectreFindingList> ListAsync(string caseId, CancellationToken ct)
    {
        var o = SpectreJson.Parse(await CallAsync("spectre_findings_list", Case(caseId), ct).ConfigureAwait(false));
        var findings = (o?["findings"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(SpectreFinding.From).ToList();
        var unreg = o?["unregistered"] switch { JsonArray a => a.Count, JsonValue v when v.TryGetValue<int>(out var n) => n, _ => 0 };
        return new SpectreFindingList(findings, unreg);
    }

    /// <summary>The case as a story in time: findings by lane, clusters, key events and the undated findings (<c>spectre_timeline</c>).</summary>
    public async Task<SpectreTimeline> TimelineAsync(string caseId, CancellationToken ct)
    {
        var text = await CallAsync("spectre_timeline", Case(caseId), ct).ConfigureAwait(false);
        return SpectreTimeline.From(SpectreJson.Parse(text) ?? throw Unreadable("spectre_timeline", text));
    }

    /// <param name="refresh">True asks the engine to write the read again instead of returning the cached one.</param>
    public async Task<SpectreMomentExplanation> MomentExplainAsync(string caseId, string momentId, bool refresh, CancellationToken ct)
    {
        var args = Case(caseId); args["moment_id"] = momentId; args["refresh"] = refresh;
        var text = await CallAsync("spectre_moment_explain", args, ct).ConfigureAwait(false);
        return SpectreMomentExplanation.From(SpectreJson.Parse(text) ?? throw Unreadable("spectre_moment_explain", text), momentId);
    }

    public async Task<SpectreFinding> GetAsync(string caseId, string findingId, CancellationToken ct)
    {
        var args = Case(caseId); args["finding_id"] = findingId;
        var text = await CallAsync("spectre_finding_get", args, ct).ConfigureAwait(false);
        return SpectreFinding.From(SpectreJson.Parse(text) ?? throw Unreadable("spectre_finding_get", text));
    }

    /// <param name="reason">Short why, required by the engine's audit log.</param>
    public async Task<SpectreEditResult> EditAsync(string caseId, string findingId, string examiner, string reason, SpectreFindingChanges changes, CancellationToken ct)
    {
        var args = Case(caseId);
        args["finding_id"] = findingId; args["examiner"] = examiner; args["reason"] = reason; args["changes"] = changes.ToJson();
        var o = SpectreJson.Parse(await CallAsync("spectre_finding_edit", args, ct).ConfigureAwait(false));
        return new SpectreEditResult(SpectreJson.Bool(o, "ok") ?? false, SpectreJson.Str(o, "reason"), SpectreJson.Str(o, "finding_id"),
            SpectreJson.Int(o, "revision") ?? 0, SpectreJson.Str(o, "sha256"));
    }

    /// <param name="decision"><see cref="SpectreFinding.Approved"/>, <see cref="SpectreFinding.Rejected"/> or <see cref="SpectreFinding.Superseded"/>.</param>
    public async Task<SpectreApproveResult> ApproveAsync(string caseId, string findingId, string examiner, string decision, string rationale,
        string? supersededBy, CancellationToken ct)
    {
        var args = Case(caseId);
        args["finding_id"] = findingId; args["examiner"] = examiner; args["decision"] = decision; args["rationale"] = rationale;
        if (!string.IsNullOrEmpty(supersededBy)) args["superseded_by"] = supersededBy;
        var o = SpectreJson.Parse(await CallAsync("spectre_finding_approve", args, ct).ConfigureAwait(false));
        return new SpectreApproveResult(SpectreJson.Bool(o, "ok") ?? false, SpectreJson.Str(o, "reason"), SpectreJson.Str(o, "finding_id"),
            SpectreJson.Str(o, "decision"), SpectreJson.Str(o, "previous_approval"), SpectreJson.Str(o, "sha256"));
    }

    public async Task<IReadOnlyList<SpectreHistoryRow>> HistoryAsync(string caseId, string findingId, CancellationToken ct)
    {
        var args = Case(caseId); args["finding_id"] = findingId;
        var o = SpectreJson.Parse(await CallAsync("spectre_finding_history", args, ct).ConfigureAwait(false));
        return (o?["rows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(SpectreHistoryRow.From).ToList();
    }

    public async Task<SpectreEvents> EventsAsync(string caseId, string? artifactType, string? contains, int? limit, CancellationToken ct)
    {
        var args = Case(caseId);
        if (!string.IsNullOrEmpty(artifactType)) args["artifact_type"] = artifactType;
        if (!string.IsNullOrEmpty(contains)) args["contains"] = contains;
        if (limit is > 0) args["limit"] = limit;
        var text = await CallAsync("spectre_events_query", args, ct).ConfigureAwait(false);
        JsonNode? root = null;
        try { root = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        var arr = root as JsonArray ?? (root as JsonObject)?["events"] as JsonArray ?? (root as JsonObject)?["rows"] as JsonArray ?? new JsonArray();
        return new SpectreEvents(arr.OfType<JsonObject>().ToList(), text);
    }

    /// <param name="formats">"md", "pdf", "docx".</param>
    public async Task<SpectreReportResult> GenerateReportAsync(string caseId, IEnumerable<string> formats, CancellationToken ct)
    {
        var args = Case(caseId);
        args["formats"] = new JsonArray(formats.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        return ParseReport(await CallAsync("spectre_report_generate", args, ct).ConfigureAwait(false));
    }

    // ---- evidence flags (docs/evidence-flags-contract.md): analyst-only tools, never offered to a model

    /// <summary>The case argument of the evidence-flag tools. The contract names it <c>case_dir</c>; the value is the engine's case id.</summary>
    public const string FlagCaseKey = "case_dir";
    public const string FlagTool = "spectre_evidence_flag", FlagsTool = "spectre_evidence_flags", CreateTool = "spectre_finding_create";

    private static JsonObject FlagCase(string caseId) => new() { [FlagCaseKey] = caseId };

    /// <summary>Sets (or with <see cref="EvidenceFlagLabels.Clear"/> removes) the examiner's label on one evidence row. The engine audits it.</summary>
    /// <param name="casePath">The row's file, relative to the case folder (forward slashes), as in <c>evidence[].case_path</c>.</param>
    /// <param name="row">0-based data row.</param>
    public async Task<SpectreFlagResult> FlagEvidenceAsync(string caseId, string findingId, string casePath, long row, string label, string analyst,
        string? note, string? reason, CancellationToken ct)
    {
        var args = FlagCase(caseId);
        args["finding_id"] = findingId; args["case_path"] = casePath; args["row"] = row; args["label"] = label; args["analyst"] = analyst;
        if (!string.IsNullOrEmpty(note)) args["note"] = note;
        if (!string.IsNullOrEmpty(reason)) args["reason"] = reason;
        var o = SpectreJson.Parse(await CallAsync(FlagTool, args, ct).ConfigureAwait(false));
        var flag = o?["flag"] is JsonObject f ? SpectreEvidenceFlag.From(f, SpectreJson.Str(o, "finding_id") ?? findingId) : null;
        return new SpectreFlagResult(SpectreJson.Bool(o, "ok") ?? false, SpectreJson.Str(o, "reason"), SpectreJson.Str(o, "finding_id"), flag);
    }

    /// <summary>The current examiner flags of the case (of one finding when <paramref name="findingId"/> is given).</summary>
    public async Task<IReadOnlyList<SpectreEvidenceFlag>> EvidenceFlagsAsync(string caseId, string? findingId, CancellationToken ct)
    {
        var args = FlagCase(caseId);
        if (!string.IsNullOrEmpty(findingId)) args["finding_id"] = findingId;
        var o = SpectreJson.Parse(await CallAsync(FlagsTool, args, ct).ConfigureAwait(false));
        return SpectreEvidenceFlag.ListFrom(o?["flags"] as JsonArray, findingId);
    }

    /// <summary>Creates an examiner finding (<c>origin: "analyst"</c>) whose evidence is <paramref name="rows"/> of <paramref name="casePath"/>.</summary>
    /// <param name="severity">critical, high, medium, low or info (sent lower-case).</param>
    public async Task<SpectreCreateResult> CreateFindingAsync(string caseId, string title, string severity, string casePath, IEnumerable<long> rows,
        string analyst, string? note, string? tactic, string? technique, CancellationToken ct)
    {
        var args = FlagCase(caseId);
        args["title"] = title; args["severity"] = severity.ToLowerInvariant(); args["case_path"] = casePath;
        args["rows"] = new JsonArray(rows.Distinct().OrderBy(r => r).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        args["analyst"] = analyst;
        if (!string.IsNullOrEmpty(note)) args["note"] = note;
        if (!string.IsNullOrEmpty(tactic)) args["tactic"] = tactic;
        if (!string.IsNullOrEmpty(technique)) args["technique"] = technique;
        var o = SpectreJson.Parse(await CallAsync(CreateTool, args, ct).ConfigureAwait(false));
        return new SpectreCreateResult(SpectreJson.Bool(o, "ok") ?? false, SpectreJson.Str(o, "reason"), SpectreJson.Str(o, "finding_id"));
    }

    public async Task<SpectreToolReply> VerifyAsync(string caseId, CancellationToken ct) =>
        SpectreToolReply.From(await CallAsync("spectre_verify", Case(caseId), ct).ConfigureAwait(false));

    internal SpectreReportResult ParseReport(string text)
    {
        var o = SpectreJson.Parse(text);
        var skipped = new Dictionary<string, string>();
        if (o?["skipped"] is JsonObject s)
            foreach (var kv in s) skipped[kv.Key] = kv.Value is JsonValue v ? SpectreJson.Text(v) : kv.Value?.ToJsonString() ?? "";
        return new SpectreReportResult(SpectreJson.Bool(o, "ok") ?? false, SpectreJson.Str(o, "markdown") ?? "",
            ToWindowsPath(SpectreJson.Str(o, "report_md"), _wslDistro), ToWindowsPath(SpectreJson.Str(o, "pdf"), _wslDistro), ToWindowsPath(SpectreJson.Str(o, "docx"), _wslDistro),
            skipped, SpectreJson.Int(o, "findings_included") ?? 0, SpectreJson.Int(o, "findings_excluded") ?? 0);
    }

    /// <summary>
    /// A path inside the distro (<c>/var/lib/...</c>) as <c>\\wsl.localhost\&lt;distro&gt;\var\lib\...</c>. Anything else (a Windows path from a dev
    /// engine, empty, or no distro given) is returned unchanged.
    /// </summary>
    public static string? ToWindowsPath(string? path, string? distro)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(distro) || !path.StartsWith('/')) return path;
        return @"\\wsl.localhost\" + distro + path.Replace('/', '\\');
    }

    private static SpectreToolException Unreadable(string tool, string text)
    {
        SpectreErrors.Log?.Invoke($"{tool}: unreadable reply ({text.Length:N0} chars): {(text.Length > 4000 ? text[..4000] + "..." : text)}");
        return new SpectreToolException(tool, "Unreadable reply.", text);
    }

    private static JsonObject Case(string caseId) => new() { ["case_id"] = caseId };

    private async Task<string> CallAsync(string tool, JsonObject args, CancellationToken ct)
    {
        var r = await _caller.CallAsync(tool, args, ct).ConfigureAwait(false);
        if (r.IsError) throw new SpectreToolException(tool, string.IsNullOrWhiteSpace(r.Text) ? tool + " failed" : r.Text);
        return r.Text;
    }
}
