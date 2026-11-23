using System.Text.Json.Nodes;

namespace EzForensics.Core.Assistant;

/// <summary>
/// An action on a page other than the grid (go to a page, retry a job, install a tool, change a safe setting, start an ingest).
/// The app fills in how to run it (and undo it, when it can be undone); like every proposal it shows as a card.
/// <paramref name="AutoAllowed"/> false = auto-mode never applies it: it always waits for the examiner's Apply click.
/// </summary>
/// <paramref name="AfterApply"/>: what the model is told after auto-mode applied it (e.g. what a scan found); null = just "applied".
public sealed record PageActionProposal(string ToolCallId, string Title, string Detail, Func<Task<string?>> Run,
    Func<Task<string?>>? Undo = null, bool AutoAllowed = true, Func<string?>? AfterApply = null) : AssistantProposal(ToolCallId);

/// <summary>
/// Names, descriptions and argument schemas of the page action tools. The app binds them to its view models; the skills registry and
/// the Spectre engine skill (SKILL.md) list them, so what Spectre says it can do and what it can do come from one place.
/// </summary>
public static class SpectrePageTools
{
    public const string GoToPage = "app_go_to_page";
    public const string JobsRetry = "jobs_retry";
    public const string JobsCancel = "jobs_cancel";
    public const string ToolsInstall = "tools_install";
    public const string OptionsSet = "options_set";
    public const string IngestStart = "ingest_start";
    public const string IngestSelectSource = "ingest_select_source";
    public const string ExploreSearch = "explore_search";
    public const string ExploreOpen = "explore_open";
    public const string FindingsSelected = "findings_selected";
    public const string FindingsListView = "findings_list_view";
    public const string FindingsSelect = "findings_select";
    public const string FindingsProposeEdit = "findings_propose_edit";
    public const string FindingsTimelineView = "findings_timeline_view";
    public const string FindingsSelectMoment = "findings_select_moment";
    public const string FindingsShowEvidence = "findings_show_evidence";
    public const string KeyEventsList = "key_events_list";
    public const string KeyEventsGet = "key_events_get";

    /// <summary>
    /// Actions offered on every page, not only their own: the tool opens its page first, so the examiner sees it happen
    /// ("scan D:\evidence" works from Analyze). Grid tools stay on Analyze.
    /// </summary>
    public static readonly IReadOnlySet<string> CrossPage = new HashSet<string>(StringComparer.Ordinal)
        { IngestSelectSource, IngestStart, ExploreSearch, ExploreOpen, FindingsSelect, JobsRetry, JobsCancel, ToolsInstall, OptionsSet };

    /// <summary>The examiner's own words ask to start the jobs ("ingest X with all jobs", "start the ingest", "run them"), not only to scan.</summary>
    public static bool AsksToStart(string? question) => !string.IsNullOrWhiteSpace(question) && StartWords.IsMatch(question) && !ScanOnly.IsMatch(question);

    private static readonly System.Text.RegularExpressions.Regex StartWords = new(@"\b(start|run|launch|begin|kick off|process|ingest)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ScanOnly = new(@"\b(only|just)\s+(scan|detect|look)|\b(don't|do not|without)\s+(start|run)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Pages app_go_to_page accepts.</summary>
    public static readonly string[] Pages = { "Ingest", "Jobs", "Analyze", "Explore", "Review", "AI", "To review", "Inbox", "Tools", "Options" };

    /// <summary>
    /// The only settings options_set may change, with the values it accepts. Everything else (AI provider, API keys, cloud consent,
    /// turning auto-mode on, flags, uninstall) stays the examiner's: Spectre explains it and can open the page.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> SafeSettings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["theme"] = new[] { "Dark", "Light" },
        ["analyze_hints"] = new[] { "on", "off" },
        ["tool_logging"] = new[] { "Normal", "Debug" },
        ["auto_mode"] = new[] { "off" },
    };

    private static JsonObject Str(string description, IEnumerable<string>? values = null)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (values != null) { var e = new JsonArray(); foreach (var v in values) e.Add(v); o["enum"] = e; }
        return o;
    }

    public static ToolSpec GoToPageSpec { get; } = AssistantGridTools.Spec(GoToPage,
        "Propose opening a page of EZ Forensics (the examiner clicks Apply, or auto-mode opens it). Use it when they ask to go somewhere or after explaining a setting that lives on another page.",
        ToolKind.Proposal, new JsonObject { ["page"] = Str("The page.", Pages) }, "page");

    public static ToolSpec JobsRetrySpec { get; } = AssistantGridTools.Spec(JobsRetry,
        "Propose running a finished job again (a failed or cancelled one). job = its number from the Jobs list in <app_state>. Always waits for the examiner's Apply click.",
        ToolKind.Proposal, new JsonObject { ["job"] = new JsonObject { ["type"] = "integer", ["description"] = "Job number as listed in <app_state>." } }, "job");

    public static ToolSpec JobsCancelSpec { get; } = AssistantGridTools.Spec(JobsCancel,
        "Propose stopping a running or queued job. job = its number from the Jobs list in <app_state>. Always waits for the examiner's Apply click.",
        ToolKind.Proposal, new JsonObject { ["job"] = new JsonObject { ["type"] = "integer", ["description"] = "Job number as listed in <app_state>." } }, "job");

    public static ToolSpec ToolsInstallSpec { get; } = AssistantGridTools.Spec(ToolsInstall,
        "Propose installing or updating a tool listed on the Tools page (name as listed in <app_state>). It runs the normal install (it may ask for Administrator). Always waits for the examiner's Apply click.",
        ToolKind.Proposal, new JsonObject { ["tool"] = Str("Tool name as listed in <app_state>.") }, "tool");

    public static ToolSpec OptionsSetSpec { get; } = AssistantGridTools.Spec(OptionsSet,
        "Propose changing one safe setting and saving Options: theme (Dark/Light), analyze_hints (on/off), tool_logging (Normal/Debug), auto_mode (off only). " +
        "Anything else (AI provider, API key, turning auto-mode on, flags) the examiner changes in Options: explain the clicks instead.",
        ToolKind.Proposal, new JsonObject
        {
            ["setting"] = Str("The setting.", SafeSettings.Keys),
            ["value"] = Str("The new value, e.g. Dark, on, Debug, off."),
        }, "setting", "value");

    public static ToolSpec IngestStartSpec { get; } = AssistantGridTools.Spec(IngestStart,
        "Propose pressing Start on the Ingest page: runs the planned jobs that are ticked for the selected evidence. Always waits for the examiner's Apply click.",
        ToolKind.Proposal, new JsonObject());

    public static ToolSpec IngestSelectSourceSpec { get; } = AssistantGridTools.Spec(IngestSelectSource,
        "Open the Ingest page, set the evidence (a file or folder path the examiner named) and scan it: detects the artifacts and plans the jobs. " +
        "It does NOT start the jobs (ingest_start does); the result lists what was found and planned. Use it when asked to scan, look at or ingest evidence.",
        ToolKind.Proposal, new JsonObject { ["path"] = Str("Full path of the evidence file or folder, exactly as the examiner gave it.") }, "path");

    public static ToolSpec ExploreSearchSpec { get; } = AssistantGridTools.Spec(ExploreSearch,
        "Search files on disk with Explore's Advanced search: opens the Advanced search window, fills it in and runs it; returns the count and the first paths found. " +
        "Use it when asked to find or search files (by name, by text inside them, by date). For rows of case CSVs use case_search / csv_query instead.",
        ToolKind.Read, new JsonObject
        {
            ["look_in"] = Str("Folders to search, separated by ;. Empty = the folder open in Explore (or the case)."),
            ["name"] = Str("File name pattern, e.g. *.ps1;*.bat (wildcards), or part of a name. Empty = any name."),
            ["name_mode"] = Str("How name matches.", new[] { "wildcard", "contains", "regex" }),
            ["text"] = Str("Text inside the files: words are AND'ed; AND, OR, NOT (uppercase), parentheses and quotes. Empty = do not look inside."),
            ["modified_after"] = Str("Only files modified on or after this date, yyyy-MM-dd."),
            ["modified_before"] = Str("Only files modified on or before this date, yyyy-MM-dd."),
        });

    public static ToolSpec ExploreOpenSpec { get; } = AssistantGridTools.Spec(ExploreOpen,
        "Open a folder or a file in Explore (a file is selected so its Overview, Preview, Hex, Strings and PE tabs show).",
        ToolKind.Proposal, new JsonObject { ["path"] = Str("Full path of the folder or file.") }, "path");

    public static ToolSpec FindingsSelectedSpec { get; } = AssistantGridTools.Spec(FindingsSelected,
        "Read the finding selected on the Review page: Spectre's values, the examiner's edits, the approval and any pending suggestion. Call it before judging or proposing a change to a finding.",
        ToolKind.Read, new JsonObject());

    public static ToolSpec FindingsListViewSpec { get; } = AssistantGridTools.Spec(FindingsListView,
        "List the findings the Review page shows now (after its filters): id, title, severity, MITRE, approval, edited, in_key_event, origin (you, spectre or both), review and in_report. Compact and capped; use it to find a finding id.",
        ToolKind.Read, new JsonObject());

    public static ToolSpec FindingsSelectSpec { get; } = AssistantGridTools.Spec(FindingsSelect,
        "Open the Review page and select a finding by its id (e.g. F-003), so the examiner sees the one you talk about. Changes nothing in the finding.",
        ToolKind.Read, new JsonObject { ["finding_id"] = Str("Finding id as listed by findings_list_view or spectre findings tools.") }, "finding_id");

    public static ToolSpec FindingsTimelineViewSpec { get; } = AssistantGridTools.Spec(FindingsTimelineView,
        "Read the Report timeline as the examiner sees it: the visible time window (UTC), the lane grouping, how many findings are in view or have no time, " +
        "and the key events in time order (moment number, id, importance rank, time UTC, headline, reason, finding ids, pinned, in view), plus the selected moment. Call it before explaining the timeline.",
        ToolKind.Read, new JsonObject());

    public static ToolSpec FindingsSelectMomentSpec { get; } = AssistantGridTools.Spec(FindingsSelectMoment,
        "Select a key event on the Review page (its card opens, the timeline and the editor follow), so the examiner sees the one you talk about. Changes nothing.",
        ToolKind.Read, new JsonObject { ["moment"] = Str("The moment number (time order, 1 = earliest; the number field) or its id, as listed by findings_timeline_view.") }, "moment");

    public static ToolSpec KeyEventsListSpec { get; } = AssistantGridTools.Spec(KeyEventsList,
        "List the active key events on the Review page: id, title, severity, time (UTC), analyst (you, spectre or both) and the ids of the findings each holds. Read only; use it to find a key event id.",
        ToolKind.Read, new JsonObject());

    public static ToolSpec KeyEventsGetSpec { get; } = AssistantGridTools.Spec(KeyEventsGet,
        "Read one key event: its effective title, time, severity, MITRE ids, notes, analyst and the ids and titles of its findings. Read only.",
        ToolKind.Read, new JsonObject { ["key_event_id"] = Str("Key event id as listed by key_events_list. Empty = the key event selected in the Key events list.") });

    public static ToolSpec FindingsShowEvidenceSpec { get; } = AssistantGridTools.Spec(FindingsShowEvidence,
        "Open the evidence panel on the Review page for a finding: the table of its evidence file, filtered to the rows behind it, where the examiner can flag rows. " +
        "Changes nothing; the examiner sets any flags.",
        ToolKind.Read, new JsonObject { ["finding_id"] = Str("Finding id as listed by findings_list_view. Empty = the selected finding.") });

    public static ToolSpec FindingsProposeEditSpec { get; } = AssistantGridTools.Spec(FindingsProposeEdit,
        "Suggest a change to a finding: it shows as a yellow bar on the finding with your reason, and the examiner accepts, edits or dismisses it. Nothing is saved, approved or rejected by this call. " +
        "Give only the fields you want changed.",
        ToolKind.Read, new JsonObject
        {
            ["finding_id"] = Str("Finding id."),
            ["changes"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "The proposed values; leave out fields that stay as they are.",
                ["properties"] = new JsonObject
                {
                    ["title"] = Str("New title."),
                    ["description"] = Str("New description."),
                    ["severity"] = Str("New severity.", new[] { "Info", "Low", "Medium", "High", "Critical" }),
                    ["mitre_attack"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "MITRE ATT&CK technique ids, e.g. T1003.001. Only ids you are sure exist." },
                    ["notes"] = Str("Examiner notes."),
                },
            },
            ["why"] = Str("One or two sentences: the evidence or reasoning behind the change."),
        }, "finding_id", "changes", "why");

    /// <summary>The step line shown under a reply for one tool call: what was done, in a few words. Only a path, page, file or keyword is shown, shortened.</summary>
    public static string Describe(ToolCall call, ToolOutcome outcome)
    {
        string Arg(string name) => call.Input[name] is JsonValue v && v.TryGetValue<string>(out var s) ? Short(s) : "";
        var what = call.Name switch
        {
            IngestSelectSource => $"Ingest: scan {Arg("path")}",
            IngestStart => "Ingest: start the jobs",
            ExploreSearch => "Advanced search" + With(" for ", Arg("name")) + With(" containing ", Arg("text")) + With(" in ", Arg("look_in")),
            ExploreOpen => $"Explore: open {Arg("path")}",
            FindingsSelected => "Read the selected finding",
            FindingsListView => "Read the findings list",
            FindingsSelect => $"Review: select {Arg("finding_id")}",
            FindingsProposeEdit => $"Review: suggest a change to {Arg("finding_id")}",
            FindingsTimelineView => "Read the timeline",
            FindingsSelectMoment => $"Review: select key event {Arg("moment")}",
            KeyEventsList => "Read the key events",
            KeyEventsGet => "Read a key event" + With(" ", Arg("key_event_id")),
            FindingsShowEvidence => $"Review: show the evidence" + With(" of ", Arg("finding_id")),
            GoToPage => $"Open {Arg("page")}",
            JobsRetry => "Retry a job",
            JobsCancel => "Stop a job",
            ToolsInstall => $"Install {Arg("tool")}",
            OptionsSet => $"Set {Arg("setting")}",
            "case_search" => "Searched the case" + With(" for ", Arg("query")),
            "case_list_files" => "Listed the case files",
            "csv_query" or "csv_get_rows" or "csv_column_values" or "csv_group_by" => "Read" + With(" ", Arg("file")),
            "skill_help" => "Read the help",
            "kb_search" => "Searched the knowledge base" + With(" for ", Arg("query")),
            "kb_get_note" => "Read the knowledge note" + With(" ", Arg("title")),
            "kb_related" => "Looked up notes related to" + With(" ", Arg("title")),
            _ when call.Name.StartsWith("spectre_", StringComparison.Ordinal) => DescribeEngine(call, outcome),
            _ => call.Name.Replace('_', ' '),
        };
        return outcome.IsError ? what + " (failed)" : what;

        static string With(string prefix, string value) => value.Length > 0 ? prefix + value : "";
        static string Short(string s) { s = s.ReplaceLineEndings(" ").Trim(); return s.Length > 80 ? s[..77] + "..." : s; }
    }

    /// <summary>Step line for the Spectre engine's own tools: plain words, never the JSON they return.</summary>
    internal static string DescribeEngine(ToolCall call, ToolOutcome outcome)
    {
        JsonNode? result = null;
        if (!outcome.IsError) { try { result = JsonNode.Parse(outcome.Text); } catch (System.Text.Json.JsonException) { } }
        int? Count() => result switch
        {
            JsonArray a => a.Count,
            JsonObject o when o["findings"] is JsonArray f => f.Count,
            JsonObject o when o["events"] is JsonArray e => e.Count,
            JsonObject o when o["count"] is JsonValue v && v.TryGetValue<int>(out var n) => n,
            _ => null,
        };
        switch (call.Name)
        {
            case "spectre_run":
                return "Ran the Spectre engine" + (call.Input["through_phase"] is JsonValue p ? $" (phases 1-{p})" : "");
            case "spectre_open_ezf_case": return "Opened the case in the Spectre engine";
            case "spectre_status": return "Checked the Spectre engine";
            case "spectre_findings_list": return Count() is int f ? $"Listed {f} finding{(f == 1 ? "" : "s")}" : "Listed the findings";
            case "spectre_finding_get": return "Read a finding";
            case "spectre_events_query": return "Searched the case events" + (Count() is int n ? $" ({n} found)" : "");
            case "spectre_submit_explanation": return "Sent an explanation to the Spectre engine";
            case "spectre_verify": return "Verified the Spectre engine's results";
            case "spectre_report": return "Built the Spectre report";
            case "spectre_kb_search": return "Searched the Spectre knowledge base";
            case "spectre_ioc_lookup": return "Looked up an indicator";
            case "spectre_open_current_case": return "Opened the current case in the Spectre engine";
            default: return call.Name.Replace('_', ' ');
        }
    }

    /// <summary>Checks an options_set request against <see cref="SafeSettings"/>; returns the canonical value or an error for the model.</summary>
    public static (string? Value, string? Error) CheckSetting(string? setting, string? value)
    {
        if (string.IsNullOrWhiteSpace(setting) || !SafeSettings.TryGetValue(setting.Trim(), out var allowed))
            return (null, $"'{setting}' cannot be changed by Spectre. Safe settings: {string.Join(", ", SafeSettings.Keys)}. For others, tell the examiner the clicks in Options.");
        var v = allowed.FirstOrDefault(a => string.Equals(a, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return v == null
            ? (null, $"{setting} accepts {string.Join(" or ", allowed)}" + (setting.Trim().Equals("auto_mode", StringComparison.OrdinalIgnoreCase) ? " (only the examiner turns auto-mode on)." : "."))
            : (v, null);
    }

    /// <summary>A Proposal tool whose card the app builds from the arguments (<paramref name="build"/> returns the proposal or an error for the model).</summary>
    public static IAssistantTool Create(ToolSpec spec, IAssistantHost host, Func<string, JsonObject, PageActionProposal?> build, Func<JsonObject, string?>? check = null) =>
        new PageTool(spec, host, build, check);

    private sealed class PageTool : IAssistantTool
    {
        private readonly IAssistantHost _host;
        private readonly Func<string, JsonObject, PageActionProposal?> _build;
        private readonly Func<JsonObject, string?>? _check;
        public PageTool(ToolSpec spec, IAssistantHost host, Func<string, JsonObject, PageActionProposal?> build, Func<JsonObject, string?>? check)
        { Spec = spec; _host = host; _build = build; _check = check; }
        public ToolSpec Spec { get; }

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            if (_check?.Invoke(call.Input) is { } err) return new ToolOutcome(err, true);
            var p = _build(call.Id, call.Input);
            if (p == null) return new ToolOutcome($"{Spec.Name}: nothing to do with these arguments.", true);
            return new ToolOutcome(await _host.HandleProposalAsync(p).ConfigureAwait(false), false);
        }
    }
}
