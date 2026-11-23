using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Assistant;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

public sealed partial class FindingsViewModel
{
    private const int AssistantMaxRows = 40;
    private static readonly Regex Technique = new(@"^T\d{4}(\.\d{3})?$", RegexOptions.Compiled);

    /// <summary>Opens Spectre's chat and asks it a question (set by the shell); null in tests = nothing happens.</summary>
    public Action<string>? AskSpectreHandler { get; set; }

    /// <summary>The message "Ask Spectre about this finding" sends: the id and title, so the model reads it with findings_selected.</summary>
    internal static string AskText(FindingRowViewModel row) =>
        $"Look at finding {row.Id} \"{row.Title}\" (call findings_selected). Does the evidence hold up, and do its severity and MITRE ids look right?";

    [RelayCommand]
    private void AskSpectre()
    {
        if (Selected is { } row) AskSpectreHandler?.Invoke(AskText(row));
    }

    /// <summary>Step, counts and the selected finding, sent to Spectre in &lt;app_state&gt;.</summary>
    public override string? AssistantState()
    {
        if (!HasCase) return "No case is open.";
        return OnUi(AssistantStateCore);
    }

    /// <summary>The state text itself, on the calling thread (tests call this; the dispatcher a WPF smoke test leaves behind may not pump).</summary>
    internal string AssistantStateCore()
    {
        {
            var sb = new StringBuilder();
            sb.Append("Case: ").AppendLine(CaseLabel);
            sb.Append("Status: ").AppendLine(StatusLine.Length > 0 ? StatusLine : HasAnalysis ? AnalysedText(AnalysedUtc, FindingCount, Moments.Count, PendingApprovals, DateTime.UtcNow) : "");
            sb.Append("Showing: ").AppendLine(IsReportOpen ? "the report"
                : Walkthrough.IsOpen ? $"the walk-through slides ({Walkthrough.ProgressText})"
                : ShowDetails ? "the details (the list of all findings and the editor)" : "the overview (summary, review status, severity, activity by tactic, key events)");
            if (SelectedMoment is { } m) sb.Append("Selected moment: ").Append(m.Number).Append(" - ").Append(m.Headline).Append(" (").Append(m.TimeText).AppendLine(")");
            if (_all.Count > 0)
                sb.Append("Findings: ").Append(_all.Count).Append(" total, ").Append(Rows.Count).Append(" shown, ").Append(PendingApprovals).Append(" pending approval, ")
                  .Append(_all.Count(r => r.IsEdited)).Append(" edited, ").Append(_all.Count(r => r.Item != null && !InReport(r))).AppendLine(" key events not in the report");
            if (Selected is { } s) sb.Append("Selected: ").Append(s.Id).Append(" - ").Append(s.Title).Append(" [").Append(s.Severity).AppendLine("]");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>findings_selected / findings_list_view / findings_select / findings_propose_edit / findings_show_evidence. None of them calls the engine (the panel reads the case files).</summary>
    public override IEnumerable<IAssistantTool> AssistantTools(IAssistantHost host)
    {
        foreach (var t in base.AssistantTools(host)) yield return t;
        yield return new FuncTool(SpectrePageTools.FindingsSelectedSpec, _ => SelectedText());
        yield return new FuncTool(SpectrePageTools.FindingsListViewSpec, _ => ListText());
        yield return new FuncTool(SpectrePageTools.FindingsSelectSpec, input =>
        {
            var id = input["finding_id"]?.ToString().Trim() ?? "";
            return SelectFinding(id) ? new ToolOutcome($"Selected {id} on the Review page.", false)
                : new ToolOutcome($"No finding {id} in the loaded list. Use findings_list_view for the ids.", true);
        });
        yield return new FuncTool(SpectrePageTools.FindingsProposeEditSpec, ProposeEdit);
        yield return new AsyncFuncTool(SpectrePageTools.FindingsShowEvidenceSpec, ShowEvidenceTool);
        yield return new FuncTool(SpectrePageTools.FindingsTimelineViewSpec, _ => OnUi(TimelineViewText));
        yield return new FuncTool(SpectrePageTools.KeyEventsListSpec, _ => KeyEventsListText());
        yield return new FuncTool(SpectrePageTools.KeyEventsGetSpec, input => KeyEventsGetText(input["key_event_id"]?.ToString().Trim() ?? ""));
        yield return new FuncTool(SpectrePageTools.FindingsSelectMomentSpec, input =>
        {
            var key = input["moment"]?.ToString().Trim() ?? "";
            return key.Length > 0 && SelectMoment(key) ? new ToolOutcome($"Selected key event {key} on the Review page.", false)
                : new ToolOutcome($"No key event {key}. Use findings_timeline_view for the moments.", true);
        });
    }

    /// <summary>key_events_list: the active key events with effective values and the ids of their findings (read only).</summary>
    internal ToolOutcome KeyEventsListText() => OnUi(() =>
    {
        if (KeyEventRows.Count == 0) return new ToolOutcome("There are no key events.", false);
        var arr = new JsonArray();
        foreach (var k in KeyEventRows.Take(AssistantMaxRows)) arr.Add(KeyEventJson(k, false));
        var o = new JsonObject { ["total"] = KeyEventRows.Count, ["key_events"] = arr };
        if (KeyEventRows.Count > AssistantMaxRows) o["note"] = $"Only the first {AssistantMaxRows} are listed.";
        return new ToolOutcome(o.ToJsonString(), false);
    });

    /// <summary>key_events_get: one key event in full (effective values, notes, findings with ids and titles).</summary>
    internal ToolOutcome KeyEventsGetText(string id) => OnUi(() =>
    {
        var k = id.Length == 0 ? SelectedKeyEvent : KeyEventRows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        return k == null ? new ToolOutcome(id.Length == 0 ? "No key event is selected. Use key_events_list for the ids." : $"No key event {id}. Use key_events_list for the ids.", true)
            : new ToolOutcome(KeyEventJson(k, true).ToJsonString(), false);
    });

    private JsonObject KeyEventJson(KeyEventRowViewModel k, bool full)
    {
        var ids = new JsonArray();
        foreach (var itemId in k.KeyEvent.FindingIds)
        {
            var row = _all.FirstOrDefault(r => r.Item?.Id == itemId);
            if (!full) ids.Add(row?.Id ?? itemId);
            else ids.Add(new JsonObject { ["id"] = row?.Id ?? itemId, ["title"] = row?.Title ?? itemId, ["severity"] = row?.Severity, ["review"] = row == null ? null : ReviewName(row) });
        }
        var o = new JsonObject
        {
            ["id"] = k.Id, ["title"] = k.Title, ["severity"] = k.Severity,
            ["time_utc"] = k.Time?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["origin"] = k.OriginText.ToLowerInvariant(), ["finding_ids"] = ids, ["edited"] = k.IsEdited,
        };
        if (full)
        {
            o["mitre_attack"] = Arr(k.Values.Mitre); o["tactic"] = k.Values.Tactic; o["notes"] = k.KeyEvent.Notes;
            o["finding_count"] = k.FindingCount;
        }
        return o;
    }

    private ToolOutcome ProposeEdit(JsonObject input)
    {
        var id = input["finding_id"]?.ToString().Trim() ?? "";
        var (changes, error) = ParseChanges(input["changes"] as JsonObject);
        if (error != null) return new ToolOutcome(error, true);
        var why = input["why"]?.ToString().Trim() ?? "";
        if (why.Length == 0) return new ToolOutcome("Give a short reason in why.", true);
        return AddSuggestion(id, changes!, why)
            ? new ToolOutcome("Suggestion shown to the user; they will accept or dismiss it.", false)
            : new ToolOutcome($"No finding {id} in the loaded list. Use findings_list_view for the ids.", true);
    }

    /// <summary>The fields of a findings_propose_edit call, checked; an error for the model when something is wrong or nothing is proposed.</summary>
    internal static (SpectreFindingChanges? Changes, string? Error) ParseChanges(JsonObject? c)
    {
        if (c == null) return (null, "changes must be an object with the fields to change.");
        var ch = new SpectreFindingChanges();
        foreach (var (key, value) in c)
        {
            if (value == null) continue;
            switch (key)
            {
                case "title": ch.Title(value.ToString().Trim()); break;
                case "description": ch.Description(value.ToString().Trim()); break;
                case "notes": ch.Notes(value.ToString().Trim()); break;
                case "severity":
                    var sev = SpectreFinding.Severities.FirstOrDefault(x => string.Equals(x, value.ToString().Trim(), StringComparison.OrdinalIgnoreCase));
                    if (sev == null) return (null, "severity must be one of: " + string.Join(", ", SpectreFinding.Severities) + ".");
                    ch.Severity(sev); break;
                case "mitre_attack":
                    var raw = value is JsonArray arr ? arr.Select(x => x?.ToString() ?? "") : value.ToString().Split(',', ';', ' ');
                    var ids = raw.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToList();
                    var bad = ids.FirstOrDefault(x => !Technique.IsMatch(x));
                    if (bad != null) return (null, $"'{bad}' is not a MITRE technique id (like T1003 or T1003.001).");
                    ch.MitreAttack(ids); break;
                default: return (null, $"Unknown field '{key}'. Fields: title, description, severity, mitre_attack, notes.");
            }
        }
        return ch.IsEmpty ? (null, "changes is empty: give at least one field.") : (ch, null);
    }

    private static string KindName(FindingRowViewModel r) => "finding";
    private static string? ReviewName(FindingRowViewModel r) => r.Item is { } i
        ? AnalysisView.ReviewOf(i) switch { ReviewState.Confirmed => "confirmed", ReviewState.Rejected => "rejected", _ => "to_review" } : null;
    /// <summary>The item is in the built report: an active key event that is confirmed or to review.</summary>
    private bool InReport(FindingRowViewModel r) => r.Item != null && Analysis != null && AnalysisView.ReportKeyEvents(Analysis).Any(k => k.FindingIds.Contains(r.Item.Id));

    /// <summary>What the store knows about the selected item: stage (flag or key_event), origin, review, flag label, duplicates, source status and Spectre's reason.</summary>
    private JsonObject ItemInfo(FindingRowViewModel row)
    {
        var i = row.Item;
        var o = new JsonObject { ["stage"] = KindName(row), ["kind"] = KindName(row), ["review"] = ReviewName(row), ["in_report"] = InReport(row) };
        if (i == null) return o;
        o["item_id"] = i.Id; o["origin"] = row.OriginText.ToLowerInvariant(); o["in_key_event"] = row.IsKeyEvent; o["flag_label"] = i.FlagLabel;
        o["duplicate_of"] = i.DuplicateOf; o["merged_into"] = i.MergedInto; o["removed_at_source"] = i.SourceStatus == SourceStatus.RemovedAtSource;
        if (i.Source.SpectreFindingId is { } sid && _enginePlain.TryGetValue(sid, out var p) && p.PromotedReason.Length > 0) o["promoted_reason"] = p.PromotedReason;
        return o;
    }

    private ToolOutcome SelectedText() => OnUi(() =>
    {
        if (Selected is not { } row) return new ToolOutcome("No finding is selected. Use findings_list_view, then findings_select.", true);
        var f = row.Finding;
        var a = f.Analyst;
        var o = new JsonObject
        {
            ["id"] = f.Id,
            ["spectre"] = new JsonObject
            {
                ["title"] = f.Title, ["severity"] = f.Severity, ["confidence"] = f.Confidence, ["mitre_attack"] = Arr(f.MitreAttack),
                ["description"] = f.Description, ["artifact_type"] = f.ArtifactType, ["status"] = f.Status, ["event_time"] = f.EventTime ?? f.FirstSeen,
                ["caveats"] = Arr(f.FkCaveats),
            },
            ["item"] = ItemInfo(row),
            ["examiner_edits"] = a is { HasOverride: true } ? new JsonObject
            {
                ["title"] = a.Title, ["description"] = a.Description, ["severity"] = a.Severity,
                ["mitre_attack"] = a.MitreAttack == null ? null : Arr(a.MitreAttack), ["notes"] = a.Notes,
                ["edited_by"] = a.EditedBy, ["revision"] = a.Revision,
            } : null,
            ["effective"] = new JsonObject { ["title"] = f.EffectiveTitle, ["severity"] = f.EffectiveSeverity, ["mitre_attack"] = Arr(f.EffectiveMitreAttack), ["in_report"] = InReport(row) },
            ["approval"] = f.ApprovalText,
        };
        if (row.PendingSuggestion is { } s) o["pending_suggestion"] = new JsonObject { ["changes"] = s.Changes.ToJson(), ["why"] = s.Why };
        return new ToolOutcome(o.ToJsonString(), false);
    });

    private ToolOutcome ListText() => OnUi(() =>
    {
        if (_all.Count == 0) return new ToolOutcome("No findings are loaded.", false);
        var arr = new JsonArray();
        foreach (var r in Rows.Take(AssistantMaxRows))
            arr.Add(new JsonObject
            {
                ["id"] = r.Id, ["title"] = r.Title, ["severity"] = r.Severity, ["mitre_attack"] = r.Mitre, ["approval"] = r.Approval,
                ["edited"] = r.IsEdited, ["kind"] = KindName(r), ["origin"] = r.OriginText.ToLowerInvariant(), ["in_key_event"] = r.IsKeyEvent, ["review"] = ReviewName(r), ["in_report"] = InReport(r), ["suggestion_pending"] = r.HasSuggestion,
            });
        var o = new JsonObject { ["shown"] = Rows.Count, ["total"] = _all.Count, ["findings"] = arr };
        if (Rows.Count > AssistantMaxRows) o["note"] = $"Only the first {AssistantMaxRows} are listed.";
        return new ToolOutcome(o.ToJsonString(), false);
    });

    /// <summary>findings_timeline_view: the visible window, the lane grouping and the key events in order (UTC).</summary>
    internal ToolOutcome TimelineViewText()
    {
        var tl = Timeline;
        if (tl.Findings.Count == 0 && tl.Undated.Count == 0 && Moments.Count == 0)
            return new ToolOutcome(HasAnalysis ? "The timeline is empty or could not be read. " + TimelineError : "Spectre has not analysed this case yet.", false);
        var view = TimelineView ?? FindingsTimelineLayout.FitRange(tl);
        static string? Utc(DateTimeOffset? t) => t?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        bool InView(DateTimeOffset? t) => view != null && t is { } x && x >= view.From && x <= view.To;
        var moments = new JsonArray();
        foreach (var m in Moments)
            moments.Add(new JsonObject
            {
                ["number"] = m.Number, ["importance_rank"] = m.Importance, ["id"] = m.Id, ["time_utc"] = Utc(m.Moment.Time), ["headline"] = m.Headline, ["reason"] = m.Moment.Reason,
                ["primary_finding_id"] = m.PrimaryFindingId, ["finding_ids"] = Arr(m.Moment.FindingIds), ["pinned"] = m.Pinned, ["in_view"] = InView(m.Moment.Time),
            });
        var o = new JsonObject
        {
            ["time_basis"] = "UTC",
            ["visible_from_utc"] = Utc(view?.From), ["visible_to_utc"] = Utc(view?.To),
            ["lanes"] = LaneMode.ToString(),
            ["findings_dated"] = tl.Findings.Count,
            ["findings_in_view"] = tl.Findings.Count(f => InView(f.Start) || (f.Start != null && view != null && f.Start <= view.To && (f.EffectiveEnd ?? f.Start) >= view.From)),
            ["findings_without_time"] = tl.Undated.Count,
            ["clusters"] = tl.Clusters.Count,
            ["key_moments"] = moments,
            ["selected_moment"] = SelectedMoment?.Number,
            ["selected_finding"] = Selected?.Id,
            ["note"] = "Order is when things were observed, not cause and effect.",
        };
        return new ToolOutcome(o.ToJsonString(), false);
    }

    private static JsonArray Arr(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    private sealed class FuncTool : IAssistantTool
    {
        private readonly Func<JsonObject, ToolOutcome> _run;
        public FuncTool(ToolSpec spec, Func<JsonObject, ToolOutcome> run) { Spec = spec; _run = run; }
        public ToolSpec Spec { get; }
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) => Task.FromResult(_run(call.Input));
    }

    private sealed class AsyncFuncTool : IAssistantTool
    {
        private readonly Func<JsonObject, Task<ToolOutcome>> _run;
        public AsyncFuncTool(ToolSpec spec, Func<JsonObject, Task<ToolOutcome>> run) { Spec = spec; _run = run; }
        public ToolSpec Spec { get; }
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) => _run(call.Input);
    }

    /// <summary>findings_show_evidence: opens the evidence panel (on the UI thread) for a finding id, or the selected finding.</summary>
    private async Task<ToolOutcome> ShowEvidenceTool(JsonObject input)
    {
        var id = input["finding_id"]?.ToString().Trim();
        var d = System.Windows.Application.Current?.Dispatcher;
        var error = d == null || d.CheckAccess() || !d.Thread.IsAlive || d.HasShutdownStarted
            ? await ShowEvidenceForChatAsync(id)
            : await d.InvokeAsync(() => ShowEvidenceForChatAsync(id)).Task.Unwrap();
        return error == null
            ? new ToolOutcome($"Opened the evidence panel for {EvidencePanel.Title}: {EvidencePanel.FileLine}. The examiner can flag rows there.", false)
            : new ToolOutcome(error, true);
    }
}
