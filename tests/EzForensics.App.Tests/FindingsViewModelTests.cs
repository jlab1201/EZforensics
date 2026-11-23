using System.IO;
using System.Text.Json.Nodes;
using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Analysis;
using EzForensics.Core.Assistant;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The Findings page logic over a fake Spectre engine: resume, list/filter, save with a reason, approve, chat suggestions, report.</summary>
[Collection("WpfApp")]   // page tools Invoke on the smoke tests' dispatcher; never run beside them
public sealed class FindingsViewModelTests : IDisposable
{
    internal sealed class FakeEngine : ISpectreToolCaller
    {
        public readonly List<(string Tool, JsonObject Args)> Calls = new();
        public string Title = "Mimikatz", Severity = "High";
        public string? Approval;
        public bool RefuseEdit;
        public string? KeyMoment;
        public string ExplainText = "Mimikatz ran right after the new service (F-1).";
        public bool ExplainFails;
        /// <summary>Replaces <see cref="TimelineJson"/> as the spectre_timeline reply.</summary>
        public string? TimelineReply;
        public bool TimelineFails;
        /// <summary>Replaces the default two findings as the spectre_findings_list reply.</summary>
        public string? ListReply;

        public const string TimelineJson = """
            {"case":"case1","time_basis":"UTC","lanes":{"tactic":["Execution","Credential Access"]},
             "findings":[{"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High","tactic":"Credential Access","host":"H1","artifact_type":"evtx","start":"2026-01-01T10:00:00Z"},
                         {"id":"F-2","finding_id":"F-2","title":"Other","severity":"Low","tactic":"Execution","host":"H1","artifact_type":"evtx","start":"2026-01-01T09:00:00Z"}],
             "undated":[],"clusters":[],
             "moments":[{"id":"M-1","rank":1,"time":"2026-01-01T09:00:00Z","primary_finding_id":"F-2","finding_ids":["F-2"],"headline":"First observed Execution","reason":"Earliest observed Execution"},
                        {"id":"M-2","rank":2,"time":"2026-01-01T10:00:00Z","primary_finding_id":"F-1","finding_ids":["F-1"],"headline":"High: Mimikatz","reason":"High severity"}]}
            """;

        private string Finding(string id, string title, string severity)
        {
            var o = new JsonObject
            {
                ["id"] = id, ["finding_id"] = id, ["title"] = title, ["severity"] = severity, ["mitre_attack"] = new JsonArray("T1003"),
                ["artifact_type"] = "evtx", ["description"] = "d", ["approval"] = id == "F-1" ? Approval : null, ["include_in_report"] = true,
            };
            if (KeyMoment != null && id == "F-1") o["analyst"] = new JsonObject { ["key_moment"] = KeyMoment };
            return o.ToJsonString();
        }

        public Task<McpToolResult> CallAsync(string tool, JsonObject args, CancellationToken ct)
        {
            Calls.Add((tool, (JsonObject)args.DeepClone()));
            if (tool == "spectre_moment_explain" && ExplainFails) return Task.FromResult(new McpToolResult(true, "no LLM provider configured"));
            if (tool == "spectre_timeline" && TimelineFails) return Task.FromResult(new McpToolResult(true, "engine error: {\"code\":500,\"detail\":\"boom\"}"));
            var text = tool switch
            {
                "spectre_status" => """{"version":"9.9"}""",
                "spectre_findings_list" when ListReply != null => ListReply,
                "spectre_findings_list" => "{\"findings\":[" + Finding("F-1", Title, Severity) + "," + Finding("F-2", "Other", "Low") + "],\"unregistered\":[]}",
                "spectre_finding_get" => args["finding_id"]!.ToString() == "F-1" ? Finding("F-1", Title, Severity) : Finding("F-2", "Other", "Low"),
                "spectre_finding_edit" when RefuseEdit => """{"ok":false,"reason":"locked"}""",
                "spectre_finding_edit" => Apply(args),
                "spectre_finding_approve" => Approve(args),
                "spectre_report_generate" => """{"ok":true,"markdown":"# Report","report_md":"/x/report.md","skipped":{"pdf":"not installed"},"findings_included":2,"findings_excluded":0}""",
                "spectre_timeline" => TimelineReply ?? TimelineJson,
                "spectre_moment_explain" => new JsonObject { ["moment_id"] = args["moment_id"]!.ToString(), ["text"] = ExplainText, ["cites"] = new JsonArray("F-1") }.ToJsonString(),
                "spectre_finding_history" => """{"rows":[{"ts":"2026-01-01T00:00:00Z","action":"edit","examiner":"me","params":{"reason":"because"}}]}""",
                _ => "{}",
            };
            return Task.FromResult(new McpToolResult(false, text));
        }

        private string Apply(JsonObject a)
        {
            var ch = a["changes"]!.AsObject();
            if (ch["title"] is { } t) Title = t.ToString();
            if (ch["severity"] is { } s) Severity = s.ToString();
            if (ch["key_moment"] is { } k) KeyMoment = k.ToString();
            return """{"ok":true,"revision":1}""";
        }

        private string Approve(JsonObject a) { Approval = a["decision"]!.ToString(); return """{"ok":true}"""; }
    }

    internal static string TempDir() => Path.Combine(Path.GetTempPath(), "ezf-findingsvm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-findingsvm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeEngine _engine = new();

    public FindingsViewModelTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private FindingsViewModel Vm(int? phase = 5, bool available = true)
    {
        if (phase != null) SpectreCaseLink.Save(_dir, new SpectreCaseLink("case1", phase, DateTime.UtcNow));
        return new FindingsViewModel(() => _dir, new SpectreFindingsClient(_engine, () => _dir, null), _ => Task.FromResult(available))
        { Ask = (_, _) => "my rationale", ConfirmRemoveFromKeyEvent = (_, _) => true };
    }

    [Fact]
    public async Task Case_with_finished_analysis_shows_the_story_at_once()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        vm.EngineAvailable.Should().BeTrue();
        vm.EngineText.Should().Contain("9.9");
        vm.HasAnalysis.Should().BeTrue();
        vm.ShowAnalyse.Should().BeTrue();   // Run / Re-run Spectre is always there
        vm.AnalyseButtonText.Should().Be("Re-run Spectre");
        vm.ShowEmptyState.Should().BeFalse();
        vm.Rows.Select(r => r.Id).Should().Equal("F-1", "F-2");   // High before Low
        vm.PendingApprovals.Should().Be(2);
        vm.Moments.Select(m => m.Number).Should().Equal(1, 2);
        vm.Timeline.Findings.Should().HaveCount(2);
        vm.TimelineView.Should().NotBeNull();
        vm.StatusLine.Should().BeEmpty();
        FindingsViewModel.AnalysedText(null, 2, 2, 2, DateTime.UtcNow).Should().EndWith("2 findings · 2 key events · 2 need review");
        vm.ReportButtonText.Should().Be("Build report (2 pending)");
    }

    [Fact]
    public async Task Case_without_finished_analysis_offers_Analyse_and_missing_engine_is_reported()
    {
        var vm = Vm(phase: 1);
        await vm.OpenCaseAsync(_dir);
        vm.HasAnalysis.Should().BeFalse();
        vm.ShowAnalyse.Should().BeTrue();
        vm.AnalyseButtonText.Should().Be("Run Spectre");
        vm.ShowEmptyState.Should().BeTrue();   // the two cards: Use my analysis, Run Spectre first
        vm.Rows.Should().BeEmpty();
        vm.ShowEmptyList.Should().BeTrue();

        var none = Vm(phase: null, available: false);
        await none.OpenCaseAsync(_dir);
        none.EngineAvailable.Should().BeFalse();
        none.EngineText.Should().Contain("Tools");
        none.ShowSetup.Should().BeTrue();
        none.ShowAnalyse.Should().BeFalse();
        none.StatusLine.Should().BeEmpty();   // the header chip says "Spectre: not set up"
    }

    [Fact]
    public async Task Changed_files_hint_shows_once_and_Dismiss_hides_it_until_another_case_opens()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        await Task.Delay(300);   // the background evidence check (no baseline here) settles first
        vm.EvidenceChanged = true; vm.EvidenceChangedText = "The case files changed since the analysis (1 new).";
        vm.ShowEvidenceHint.Should().BeTrue();
        vm.DismissEvidenceHintCommand.Execute(null);
        vm.ShowEvidenceHint.Should().BeFalse();
        vm.EvidenceChanged = true;
        vm.ShowEvidenceHint.Should().BeFalse();   // stays dismissed for this case
    }

    [Fact]
    public void Status_line_counts_and_drops_the_date_today()
    {
        var now = new DateTime(2026, 11, 6, 15, 0, 0, DateTimeKind.Utc);
        FindingsViewModel.AnalysedText(new DateTime(2026, 11, 6, 14, 2, 0, DateTimeKind.Utc), 37, 4, 6, now)
            .Should().Be("Analysed 14:02 UTC · 37 findings · 4 key events · 6 need review");
        FindingsViewModel.AnalysedText(new DateTime(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc), 1, 1, 1, now)
            .Should().Be("Analysed 2026-11-05 09:30 UTC · 1 finding · 1 key event · 1 needs review");
    }

    [Fact]
    public async Task J_and_K_step_through_the_moments_and_keep_timeline_card_and_editor_in_step()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.NextMoment();
        vm.SelectedMoment!.Number.Should().Be(1);
        vm.Selected!.Id.Should().Be("F-2");
        vm.SelectedFindingId.Should().Be("F-2");
        vm.Moments[0].IsSelected.Should().BeTrue();
        vm.NextMoment();
        vm.SelectedMoment!.Number.Should().Be(2);
        vm.Selected!.Id.Should().Be("F-1");
        vm.NextMoment();
        vm.SelectedMoment!.Number.Should().Be(2);   // stays on the last one
        vm.PreviousMoment();
        vm.SelectedMoment!.Number.Should().Be(1);

        // a click on the timeline (the selected finding id) moves the card and the editor
        vm.SelectedFindingId = "F-1";
        vm.SelectedMoment!.Id.Should().Be("M-2");
        vm.DraftTitle.Should().Be("Mimikatz");
        // the list selection moves the timeline
        vm.Selected = vm.Rows.First(r => r.Id == "F-2");
        vm.SelectedFindingId.Should().Be("F-2");
        vm.SelectedMoment!.Id.Should().Be("M-1");

        vm.SelectMomentCore("#2").Should().BeTrue();
        vm.SelectedMoment!.Id.Should().Be("M-2");
        vm.SelectMomentCore("M-1").Should().BeTrue();
        vm.SelectMomentCore("9").Should().BeFalse();
    }

    [Fact]
    public async Task Opening_a_card_loads_Spectre_s_read_once_with_a_friendly_fallback()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.SelectedMoment = vm.Moments[1];
        await Task.Delay(50);
        vm.Moments[1].ReadText.Should().Contain("Mimikatz");
        vm.Moments[1].ReadCites.Should().Be("Cites F-1");
        var calls = _engine.Calls.Count(c => c.Tool == "spectre_moment_explain");
        vm.SelectedMoment = vm.Moments[0];
        vm.SelectedMoment = vm.Moments[1];
        _engine.Calls.Count(c => c.Tool == "spectre_moment_explain").Should().Be(calls + 1);   // only the first card was new

        _engine.ExplainFails = true;
        await vm.LoadReadAsync(vm.Moments[0], refresh: true);
        vm.Moments[0].ReadText.Should().Contain("needs an AI model");
        FindingsViewModel.FriendlyReadError("boom").Should().Contain("not available");
    }

    [Fact]
    public async Task Not_significant_removes_the_key_event_and_it_leaves_the_timeline()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        var card = vm.Moments.Single(m => m.PrimaryFindingId == "F-1");
        await vm.UnpinMomentCommand.ExecuteAsync(card);
        var item = vm.Analysis!.Items.Single(i => i.Source.SpectreFindingId == "F-1");
        item.Review.Should().NotBe(ReviewState.Rejected);                            // the finding itself is not judged
        vm.Analysis.KeyEvents.Should().Contain(k => k.Dismissed && k.FindingIds.Contains(item.Id));
        vm.Moments.Select(m => m.PrimaryFindingId).Should().NotContain("F-1");       // it leaves the timeline and the cards
        vm.Rows.Select(r => r.Id).Should().Contain("F-1");                            // but stays in the findings list
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_finding_edit");
    }

    [Fact]
    public async Task Confirm_stamps_the_card_and_advances_the_review_ring()
    {
        var hold = FindingsViewModel.StampHoldMs;
        FindingsViewModel.StampHoldMs = () => 0;
        try
        {
            var vm = Vm(); await vm.OpenCaseAsync(_dir);
            vm.ReviewText.Should().Be("0 of 2 reviewed");
            vm.AllReviewed.Should().BeFalse();
            var card = vm.Moments.Single(m => m.PrimaryFindingId == "F-1");
            card.IsReviewed.Should().BeFalse();
            await vm.ConfirmMomentCommand.ExecuteAsync(card);
            card = vm.Moments.Single(m => m.PrimaryFindingId == "F-1");
            card.IsReviewed.Should().BeTrue();
            card.StampToken.Should().Be(1);
            card.StampText.Should().Be("Confirmed");
            vm.ReviewedCount.Should().Be(1);
            vm.ReviewText.Should().Be("1 of 2 reviewed");
            (vm.Analysis!.Items.Single(i => i.Source.SpectreFindingId == "F-1").Review).Should().Be(ReviewState.Confirmed);

            await vm.UnpinMomentCommand.ExecuteAsync(vm.Moments.Single(m => m.PrimaryFindingId == "F-2"));
            vm.Moments.Should().ContainSingle();   // Not significant removes the key event from the timeline
            vm.ReviewedCount.Should().Be(1);
        }
        finally { FindingsViewModel.StampHoldMs = hold; }
    }

    [Fact]
    public async Task Editor_changes_are_title_description_severity_mapping_and_notes_only()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.Selected = vm.Rows[0];
        vm.DraftTitle = "Credential dumping"; vm.DraftNotes = "seen twice";
        vm.CurrentChanges().Fields.Should().BeEquivalentTo("title", "notes");
        vm.DraftTitle = "Mimikatz"; vm.DraftNotes = "";
        vm.IsDirty.Should().BeFalse();
    }


    [Fact]
    public async Task Present_opens_the_slides_at_slide_one_and_needs_findings_or_key_events()
    {
        var vm = Vm(phase: 5);
        vm.CanPresent.Should().BeFalse();   // no case opened yet
        vm.PresentCommand.CanExecute(null).Should().BeFalse();
        await vm.OpenCaseAsync(_dir);
        vm.CanPresent.Should().BeTrue();
        vm.PresentCommand.CanExecute(null).Should().BeTrue();
        vm.Walkthrough.Open(); vm.Walkthrough.Next();
        vm.SelectedTab = FindingsTab.Overview;
        vm.PresentCommand.Execute(null);
        vm.SelectedTab.Should().Be(FindingsTab.Story);
        vm.Walkthrough.Index.Should().Be(0);
    }

    [Fact]
    public async Task Details_filters_by_origin_review_duplicate_and_source()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.OriginFilter = FindingsViewModel.OriginYou;
        vm.Rows.Should().BeEmpty();
        vm.OriginFilter = FindingsViewModel.OriginSpectre;
        vm.Rows.Should().HaveCount(2);
        vm.DuplicateFilter = FindingsViewModel.DuplicatesOnly;
        vm.Rows.Should().BeEmpty();
        vm.DuplicateFilter = FindingsViewModel.NoDuplicates;
        vm.SourceFilter = FindingsViewModel.RemovedOnly;
        vm.Rows.Should().BeEmpty();
        vm.ClearFiltersCommand.Execute(null);
        vm.ReviewFilter = "Confirmed";
        vm.BucketFilter.Should().Be(ReviewBucket.Confirmed);
        FindingsViewModel.ReviewFilters.Should().NotContain("Not important");
        vm.ReviewFilter = "Any review";
        vm.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Key_events_only_lists_the_findings_that_are_in_a_key_event()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.Rows.Should().HaveCount(2);
        vm.Selected = vm.Rows.Single(r => r.Id == "F-1");
        vm.ToggleSelectedKeyEventCommand.Execute(null);   // F-1 leaves its key event
        vm.Rows.Single(r => r.Id == "F-1").IsKeyEvent.Should().BeFalse();
        vm.KeyEventsOnly = true;
        vm.Rows.Select(r => r.Id).Should().Equal("F-2");
        vm.ClearFiltersCommand.Execute(null);
        vm.KeyEventsOnly.Should().BeFalse();
        vm.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Technique_edit_is_validated_like_the_engine_and_the_tactic_follows_it()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.Selected = vm.Rows.Single(r => r.Id == "F-1");
        vm.SelectedTactic.Should().Be("Credential Access");   // T1003 from the list
        vm.TechniqueSuggestions.Should().Contain(new[] { "T1003", "T1059.001" });

        vm.DraftMitre = "T1059.001, T1105";
        vm.MitreInvalid.Should().BeFalse();
        vm.SelectedTactic.Should().Be("Execution");
        vm.ReasonText = "wrong technique";
        vm.CanSave.Should().BeTrue();

        vm.DraftMitre = "T1059.001, banana";
        vm.MitreInvalid.Should().BeTrue();
        vm.CanSave.Should().BeFalse();
        vm.DraftMitre = "T123";
        vm.MitreInvalid.Should().BeTrue();

        vm.DraftMitre = "T9999";   // a valid id the list does not know: the finding's tactic on the timeline stands in
        vm.MitreInvalid.Should().BeFalse();
        vm.SelectedTactic.Should().Be("Credential Access");
        vm.CanSave.Should().BeTrue();
    }

    [Fact]
    public async Task Review_breakdown_adds_up_and_a_line_filters_the_Details_list()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.ReviewBreakdown.Select(l => (l.Label, l.Count)).Should().Equal(("Confirmed", 0), ("Rejected", 0), ("To review", 2));

        vm.Selected = vm.Rows.Single(r => r.Id == "F-1");
        await vm.ApproveCommand.ExecuteAsync(null);
        vm.ConfirmRemoveFromKeyEvent = (_, _) => true;
        vm.Selected = vm.Rows.Single(r => r.Id == "F-2");
        await vm.RejectCommand.ExecuteAsync(null);
        vm.ReviewBreakdown.Select(l => (l.Label, l.Count)).Should().Equal(("Confirmed", 1), ("Rejected", 1), ("To review", 0));
        vm.ReviewBreakdown.Sum(l => l.Count).Should().Be(vm.FindingCount);

        vm.FilterByReviewCommand.Execute(vm.ReviewBreakdown[0]);
        vm.SelectedTab.Should().Be(FindingsTab.Details);
        vm.Rows.Select(r => r.Id).Should().Equal("F-1");
        vm.BucketFilterText.Should().Be("Confirmed");
        vm.ClearBucketFilterCommand.Execute(null);
        vm.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Edit_on_a_card_opens_Details_with_that_finding_selected()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.SelectedTab.Should().Be(FindingsTab.Overview);
        vm.EditMomentCommand.Execute(vm.Moments.Single(m => m.Id == "M-1"));
        vm.SelectedTab.Should().Be(FindingsTab.Details);
        vm.Selected!.Id.Should().Be("F-2");
    }

    [Fact]
    public async Task Analyse_reports_why_it_was_not_queued()
    {
        SpectreCaseLink.Save(_dir, new SpectreCaseLink("case1", 1, DateTime.UtcNow));
        var vm = new FindingsViewModel(() => _dir, new SpectreFindingsClient(_engine, () => _dir, null), _ => Task.FromResult(true),
            queue: () => null, enqueue: _ => Task.FromResult<(EzForensics.Core.Execution.Job?, string?)>((null, "engine busy")));
        await vm.OpenCaseAsync(_dir);
        vm.OnNavigatedTo();
        vm.AnalyseCommand.CanExecute(null).Should().BeTrue();
        await vm.AnalyseCommand.ExecuteAsync(null);
        vm.Banner!.Message.Should().Contain("engine busy");
        vm.IsAnalysing.Should().BeFalse();
    }

    [Fact]
    public async Task Filters_narrow_the_list_and_show_the_summary()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.SeverityFilter = "Low";
        vm.Rows.Select(r => r.Id).Should().Equal("F-2");
        vm.ListSummary.Should().Contain("1 of 2");
        vm.SearchText = "nomatch";
        vm.ShowNoMatch.Should().BeTrue();
        vm.ClearFiltersCommand.Execute(null);
        vm.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Save_writes_one_edit_to_the_store_with_history_and_needs_no_reason()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.Selected = vm.Rows[0];
        vm.IsDirty.Should().BeFalse();
        vm.DraftSeverity = "Critical";
        vm.IsDirty.Should().BeTrue();
        vm.CanSave.Should().BeTrue();
        vm.NeedsReason.Should().BeFalse();
        vm.ReasonText = "confirmed with memory";

        await vm.SaveCommand.ExecuteAsync(null);

        var item = vm.Analysis!.Items.Single(i => i.Source.SpectreFindingId == "F-1");
        item.Severity.Should().Be("Critical");
        item.IsEdited(ItemFields.Severity).Should().BeTrue();
        item.History.Select(h => h.What).Should().Contain(new[] { "set severity Critical", "reason: confirmed with memory" });
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_finding_edit");
        vm.Selected!.Severity.Should().Be("Critical");
        vm.Selected.IsEdited.Should().BeTrue();
        vm.IsDirty.Should().BeFalse();
        vm.EditMessage.Should().Be("Saved.");
        // the edit survives a fresh import of the same engine results
        await vm.LoadFindingsAsync();
        vm.Rows.Single(r => r.Id == "F-1").Severity.Should().Be("Critical");
    }

    [Fact]
    public async Task Edit_is_refused_when_the_case_analysis_is_from_a_newer_version()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        File.WriteAllText(CaseAnalysisStore.PathFor(_dir), "{\"version\":99,\"items\":[]}");
        vm.Selected = vm.Rows[0];
        vm.DraftTitle = "New";
        await vm.SaveCommand.ExecuteAsync(null);
        vm.EditMessage.Should().NotBe("Saved.");
        vm.DraftTitle.Should().Be("New");
        vm.Selected!.Title.Should().Be("Mimikatz");
    }

    [Fact]
    public async Task Revert_field_puts_the_engine_value_back_in_the_editor()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.Selected = vm.Rows[0];
        vm.DraftTitle = "Something else";
        vm.TitleDiffers.Should().BeTrue();
        vm.RevertFieldCommand.Execute("title");
        vm.DraftTitle.Should().Be("Mimikatz");
        vm.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Approve_asks_for_a_rationale_and_sets_the_review_in_the_store()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.Selected = vm.Rows[0];
        await vm.ApproveCommand.ExecuteAsync(null);
        var item = vm.Analysis!.Items.Single(i => i.Source.SpectreFindingId == "F-1");
        item.Review.Should().Be(ReviewState.Confirmed);
        item.History.Select(h => h.What).Should().Contain("rationale: my rationale");
        item.History.Should().OnlyContain(h => h.Who.Length > 0);
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_finding_approve");
        vm.Selected!.Approval.Should().Be("APPROVED");
        vm.PendingApprovals.Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_rationale_sends_nothing()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.Ask = (_, _) => null;
        vm.Selected = vm.Rows[0];
        await vm.RejectCommand.ExecuteAsync(null);
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_finding_approve");
    }

    [Fact]
    public async Task Suggestion_is_only_a_bar_until_accepted_then_saved_to_the_store_with_the_standard_reason()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.AddSuggestionCore("F-1", new SpectreFindingChanges().Severity("Critical"), "LSASS dump").Should().BeTrue();
        vm.AddSuggestionCore("nope", new SpectreFindingChanges().Severity("Low"), "x").Should().BeFalse();
        var row = vm.Rows.First(r => r.Id == "F-1");
        row.HasSuggestion.Should().BeTrue();
        row.SuggestionLines.Should().Equal("Severity: High → Critical");
        vm.Analysis!.Items.Single(i => i.Source.SpectreFindingId == "F-1").Severity.Should().Be("High");   // nothing changes until accepted

        await vm.AcceptSuggestionCommand.ExecuteAsync(row);

        var item = vm.Analysis!.Items.Single(i => i.Source.SpectreFindingId == "F-1");
        item.Severity.Should().Be("Critical");
        item.History.Select(h => h.What).Should().Contain(w => w.StartsWith("reason: Suggested by Spectre, accepted by "));
        row.HasSuggestion.Should().BeFalse();
        row.Severity.Should().Be("Critical");
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_finding_edit");
    }

    internal static Task<ToolOutcome> Run(FindingsViewModel vm, string tool, JsonObject input) =>
        vm.AssistantTools(null!).First(t => t.Spec.Name == tool).RunAsync(new ToolCall("c", tool, input), default);

    [Fact]
    public async Task Findings_page_registers_its_chat_tools()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.AssistantTools(null!).Select(t => t.Spec.Name).Should().Contain(new[] { "app_go_to_page", "findings_selected", "findings_list_view", "findings_select", "findings_propose_edit" });
    }

    [Fact]
    public async Task Propose_edit_only_adds_a_suggestion_and_never_calls_the_engine()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        var before = _engine.Calls.Count;
        var ok = await Run(vm, "findings_propose_edit", new JsonObject
        {
            ["finding_id"] = "F-1", ["why"] = "LSASS access from a non-system process",
            ["changes"] = new JsonObject { ["severity"] = "critical", ["mitre_attack"] = new JsonArray("t1003.001") },
        });
        ok.IsError.Should().BeFalse();
        ok.Text.Should().Be("Suggestion shown to the user; they will accept or dismiss it.");
        _engine.Calls.Count.Should().Be(before);   // not even a read
        var row = vm.Rows.First(r => r.Id == "F-1");
        row.PendingSuggestion!.Why.Should().Contain("LSASS");
        row.SuggestionLines.Should().Contain("Severity: High → Critical");
        row.Finding.Approval.Should().BeNull();

        (await Run(vm, "findings_propose_edit", new JsonObject { ["finding_id"] = "nope", ["why"] = "w", ["changes"] = new JsonObject { ["notes"] = "n" } })).IsError.Should().BeTrue();
        (await Run(vm, "findings_propose_edit", new JsonObject { ["finding_id"] = "F-1", ["why"] = "w", ["changes"] = new JsonObject { ["severity"] = "Scary" } })).IsError.Should().BeTrue();
        (await Run(vm, "findings_propose_edit", new JsonObject { ["finding_id"] = "F-1", ["why"] = "w", ["changes"] = new JsonObject { ["mitre_attack"] = new JsonArray("made up") } })).IsError.Should().BeTrue();
        (await Run(vm, "findings_propose_edit", new JsonObject { ["finding_id"] = "F-1", ["why"] = "w", ["changes"] = new JsonObject() })).IsError.Should().BeTrue();
        (await Run(vm, "findings_propose_edit", new JsonObject { ["finding_id"] = "F-1", ["why"] = "", ["changes"] = new JsonObject { ["notes"] = "n" } })).IsError.Should().BeTrue();
        _engine.Calls.Count.Should().Be(before);
    }

    [Fact]
    public async Task Timeline_tools_list_the_moments_in_UTC_and_select_one_by_number()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        var view = JsonNode.Parse((await Run(vm, "findings_timeline_view", new JsonObject())).Text)!;
        view["time_basis"]!.ToString().Should().Be("UTC");
        view["visible_from_utc"]!.ToString().Should().EndWith("Z");
        view["key_moments"]!.AsArray().Select(m => m!["number"]!.GetValue<int>()).Should().Equal(1, 2);
        view["key_moments"]![0]!["time_utc"]!.ToString().Should().Be("2026-01-01T09:00:00Z");

        (await Run(vm, "findings_select_moment", new JsonObject { ["moment"] = "2" })).IsError.Should().BeFalse();
        vm.SelectedMoment!.Id.Should().Be("M-2");
        vm.Selected!.Id.Should().Be("F-1");
        (await Run(vm, "findings_select_moment", new JsonObject { ["moment"] = "7" })).IsError.Should().BeTrue();
        vm.AssistantStateCore().Should().Contain("Selected moment: 2 - High: Mimikatz (2026-01-01 10:00:00 UTC)");   // a Spectre moment keeps its own headline
    }

    [Fact]
    public async Task Select_and_read_tools_follow_the_page()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        (await Run(vm, "findings_selected", new JsonObject())).IsError.Should().BeTrue();   // nothing selected yet

        var sel = await Run(vm, "findings_select", new JsonObject { ["finding_id"] = "F-2" });
        sel.IsError.Should().BeFalse();
        vm.Selected!.Id.Should().Be("F-2");
        (await Run(vm, "findings_select", new JsonObject { ["finding_id"] = "F-9" })).IsError.Should().BeTrue();

        var one = JsonNode.Parse((await Run(vm, "findings_selected", new JsonObject())).Text)!;
        one["id"]!.ToString().Should().Be("F-2");
        one["spectre"]!["severity"]!.ToString().Should().Be("Low");
        one["approval"]!.ToString().Should().Be("Pending");
        one["item"]!["stage"]!.ToString().Should().Be("finding");
        one["item"]!["origin"]!.ToString().Should().Be("spectre");
        one["item"]!["review"]!.ToString().Should().Be("to_review");
        one["item"]!["in_report"]!.GetValue<bool>().Should().BeTrue();

        vm.SeverityFilter = "Low";
        var list = JsonNode.Parse((await Run(vm, "findings_list_view", new JsonObject())).Text)!;
        list["shown"]!.GetValue<int>().Should().Be(1);
        list["total"]!.GetValue<int>().Should().Be(2);
        list["findings"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Selected_finding_shows_its_pending_suggestion_to_the_model()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.SelectFindingCore("F-1");
        await Run(vm, "findings_propose_edit", new JsonObject { ["finding_id"] = "F-1", ["why"] = "w", ["changes"] = new JsonObject { ["notes"] = "check the parent process" } });
        var one = JsonNode.Parse((await Run(vm, "findings_selected", new JsonObject())).Text)!;
        one["pending_suggestion"]!["changes"]!["notes"]!.ToString().Should().Be("check the parent process");
    }

    [Fact]
    public async Task Propose_edit_is_proposal_only_until_accepted_then_goes_through_the_store()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.SelectFindingCore("F-1");
        (await Run(vm, "findings_propose_edit", new JsonObject { ["finding_id"] = "F-1", ["why"] = "w", ["changes"] = new JsonObject { ["include_in_report"] = false } })).IsError.Should().BeTrue();
        await Run(vm, "findings_propose_edit", new JsonObject { ["finding_id"] = "F-1", ["why"] = "w", ["changes"] = new JsonObject { ["notes"] = "n-from-spectre" } });
        var row = vm.Rows.Single(r => r.Id == "F-1");
        row.Item!.Notes.Should().NotBe("n-from-spectre");          // nothing applied yet
        await vm.AcceptSuggestionCommand.ExecuteAsync(row);
        vm.Analysis!.Find(row.Item.Id)!.Notes.Should().Be("n-from-spectre");
        vm.Analysis.Find(row.Item.Id)!.IsEdited("notes").Should().BeTrue();
    }

    [Fact]
    public async Task Assistant_state_has_step_counts_and_the_selection()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.OnNavigatedTo();
        vm.SelectFindingCore("F-1");
        vm.SeverityFilter = "High";
        var state = vm.AssistantStateCore();
        state.Should().Contain("Showing: the overview").And.Contain("2 total, 1 shown, 2 pending approval, 0 edited, 0 key events not in the report").And.Contain("Selected: F-1 - Mimikatz [High]");
    }

    [Fact]
    public async Task Ask_Spectre_sends_the_id_and_title_to_the_chat()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        string? sent = null;
        vm.AskSpectreHandler = t => sent = t;
        vm.AskSpectreCommand.Execute(null);
        sent.Should().BeNull();   // nothing selected
        vm.SelectFindingCore("F-1");
        vm.AskSpectreCommand.Execute(null);
        sent.Should().Contain("F-1").And.Contain("Mimikatz").And.Contain("findings_selected");
    }

    [Fact]
    public async Task Suggestion_edit_fills_the_editor_and_dismiss_clears_it()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.AddSuggestionCore("F-1", new SpectreFindingChanges().Severity("Low").MitreAttack(new[] { "T1055" }), "why");
        var row = vm.Rows.First(r => r.Id == "F-1");
        vm.EditSuggestionCommand.Execute(row);
        vm.Selected.Should().BeSameAs(row);
        vm.DraftSeverity.Should().Be("Low");
        vm.DraftMitre.Should().Be("T1055");
        vm.IsDirty.Should().BeTrue();
        row.HasSuggestion.Should().BeFalse();

        vm.AddSuggestionCore("F-2", new SpectreFindingChanges().Notes("n"), "w");
        var other = vm.Rows.First(r => r.Id == "F-2");
        vm.DismissSuggestionCommand.Execute(other);
        other.HasSuggestion.Should().BeFalse();
    }

    [Fact]
    public async Task Suggestions_survive_a_refresh()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.AddSuggestionCore("F-1", new SpectreFindingChanges().Severity("Low"), "w");
        await vm.LoadFindingsAsync();
        vm.Rows.First(r => r.Id == "F-1").HasSuggestion.Should().BeTrue();
    }

    [Fact]
    public async Task History_tab_shows_the_items_own_history_and_report_builds_from_the_store()
    {
        var vm = Vm(); await vm.OpenCaseAsync(_dir);
        vm.Selected = vm.Rows[0];
        vm.DraftTitle = "Credential dumping"; vm.ReasonText = "because";
        await vm.SaveCommand.ExecuteAsync(null);
        vm.SelectedDetailTab = FindingsViewModel.TabHistory;
        vm.HistoryLines.Should().Contain(l => l.Contains("imported from Spectre"));
        vm.HistoryLines.Should().Contain(l => l.Contains("edited title"));
        vm.HistoryLines.Should().Contain(l => l.Contains("reason: because"));
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_finding_history");

        await vm.GenerateReportCommand.ExecuteAsync(null);
        vm.ReportPreview.Should().Contain("## 1.");
        vm.CanOpenFolder.Should().BeTrue();
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_report_generate");
    }

    [Fact]
    public async Task Stage_strip_lights_the_stages_with_evidence_and_the_cards_are_never_filtered()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        vm.ShowStageStrip.Should().BeTrue();
        var lit = vm.StageStrip.Where(c => c.HasEvidence).ToList();
        lit.Select(c => c.Stage).Should().Equal("Execution", "Credential Access");
        lit[1].Severity.Should().Be("High");
        lit[1].DelayMs.Should().BeGreaterThan(lit[0].DelayMs);   // lit one after another as the journey line draws
        vm.StoryMoments.Should().HaveCount(2);
    }

    // ---- the page-level computer selector

    private async Task<FindingsViewModel> SampleVm()
    {
        _engine.TimelineReply = FindingsSnapshotTests.SampleTimeline();
        _engine.ListReply = FindingsSnapshotTests.SampleList();
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        return vm;
    }

    [Fact]
    public async Task Host_selector_lists_All_computers_then_each_host_and_defaults_to_all()
    {
        var vm = await SampleVm();
        vm.HostChoices.Should().Equal("All computers", "DC-01", "WS-01", "WS-02");
        vm.SelectedHostChoice.Should().Be("All computers");
        vm.HostFilter.Should().BeNull();
        vm.ShowHostPicker.Should().BeTrue();
        vm.ShowHostLabel.Should().BeFalse();
        vm.ShowHostBar.Should().BeTrue();
    }

    [Fact]
    public async Task One_host_shows_a_plain_label_not_a_box()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        vm.HostChoices.Should().Equal("All computers", "H1");
        vm.ShowHostLabel.Should().BeTrue();
        vm.ShowHostPicker.Should().BeFalse();
        vm.SingleHostName.Should().Be("H1");
    }

    [Fact]
    public async Task Choosing_a_host_scopes_the_tiles_the_cards_the_timeline_and_Details_and_All_computers_undoes_it()
    {
        var vm = await SampleVm();
        int allCards = vm.StoryMoments.Count, allRows = vm.Rows.Count, allFindings = vm.ReviewTotal;
        allFindings.Should().Be(7);
        vm.SelectedHostChoice = "WS-01";
        vm.HostFilter.Should().Be("WS-01");
        // tiles: WS-01 has F-1 Medium, F-2 High, F-3 High
        vm.SeverityCounts[SpectreFinding.Rank("High")].Should().Be(2);
        vm.SeverityCounts[SpectreFinding.Rank("Medium")].Should().Be(1);
        vm.SeverityTotal.Should().Be(3);
        vm.SeverityLegend.Single(l => l.Severity == "High").Count.Should().Be(2);
        vm.ReviewTotal.Should().Be(3);
        vm.ReviewBreakdown.Sum(l => l.Count).Should().Be(3);
        vm.ReviewBreakdown.Single(l => l.Bucket == ReviewBucket.Confirmed).Count.Should().Be(1);   // F-1 is approved
        vm.Radar.CountOf("Persistence").Should().Be(1);
        vm.Radar.CountOf("Credential Access").Should().Be(0);   // F-4 is on WS-02
        // key event cards, slides, the Timeline tab's timeline and the Details list
        vm.StoryMoments.Select(m => m.Id).Should().Equal("M-1", "M-2", "M-3");
        vm.AllStoryMoments.Select(m => m.Id).Should().Equal("M-1", "M-2", "M-3");
        vm.HostTimeline.Moments.Select(m => m.Id).Should().Equal("M-1", "M-2", "M-3");
        vm.HostTimeline.Findings.Should().OnlyContain(f => f.Host == "WS-01");
        vm.Rows.Select(r => r.Id).Should().BeEquivalentTo(new[] { "F-1", "F-2", "F-3" });
        vm.ListSummary.Should().StartWith("3 of 3");
        vm.Walkthrough.Open();
        vm.Walkthrough.Slides.Should().HaveCount(5);   // overview, three events, summary

        vm.SelectedHostChoice = "All computers";
        vm.HostFilter.Should().BeNull();
        vm.StoryMoments.Should().HaveCount(allCards);
        vm.Rows.Should().HaveCount(allRows);
        vm.HostTimeline.Should().BeSameAs(vm.Timeline);
        vm.ReviewTotal.Should().Be(allFindings);
    }

    [Fact]
    public async Task Opening_another_case_resets_the_selector_to_All_computers()
    {
        var vm = await SampleVm();
        vm.SelectedHostChoice = "DC-01";
        await vm.OpenCaseAsync(_dir);
        vm.HostFilter.Should().BeNull();
        vm.SelectedHostChoice.Should().Be("All computers");
    }

    // ---- Activity by tactic and Severity tiles

    [Fact]
    public async Task Radar_has_the_12_tactics_in_kill_chain_order_and_counts_follow_the_host()
    {
        var vm = await SampleVm();
        var all = vm.Radar;
        all.Axes.Should().Equal("Initial Access", "Execution", "Persistence", "Privilege Escalation", "Defense Evasion", "Credential Access", "Discovery",
            "Lateral Movement", "Collection", "Command and Control", "Exfiltration", "Impact");
        all.Counts.Should().Equal(1, 1, 2, 0, 1, 1, 0, 1, 0, 0, 0, 0);   // Persistence: F-3 and F-7
        all.Unmapped.Should().Be(0);
        vm.RadarUnmappedText.Should().BeEmpty();
        vm.HostFilter = "WS-02";
        vm.Radar.Axes.Should().Equal(all.Axes);   // same axes on every computer
        vm.Radar.Counts.Should().Equal(0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0);
        vm.HostFilter = "DC-01";
        vm.Radar.Counts.Should().Equal(0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Findings_with_no_tactic_are_counted_as_unmapped_not_as_an_axis()
    {
        var tl = JsonNode.Parse(FindingsSnapshotTests.SampleTimeline())!.AsObject();
        tl["findings"]!.AsArray()[0]!["tactic"] = "";
        _engine.TimelineReply = tl.ToJsonString();
        _engine.ListReply = FindingsSnapshotTests.SampleList();
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        vm.Radar.Unmapped.Should().Be(1);
        vm.Radar.Total.Should().Be(6);
        vm.RadarUnmappedText.Should().Be("1 unmapped");
    }

    [Fact]
    public async Task Slides_mark_the_moments_stage_and_computer_and_autoplay_steps_and_pauses()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        var w = vm.Walkthrough;
        w.Open();
        w.Slides.Should().HaveCount(4);
        w.Computers.Should().OnlyContain(c => !c.IsCurrent);
        w.Next();
        w.SlideLine.Should().NotContain(Environment.NewLine);
        w.Computers.Single().IsCurrent.Should().BeTrue();
        w.IsAutoplay = true;
        w.AutoStep();
        w.Index.Should().Be(2);
        w.AutoStep();
        w.Index.Should().Be(3);
        w.IsAutoplay.Should().BeFalse();             // stops at the end
        w.IsAutoplay = true;                          // from the end it starts over
        w.Index.Should().Be(0);
        w.PauseAutoplay();
        w.IsAutoplay.Should().BeFalse();
        w.Close();
    }

    [Fact]
    public async Task Peek_without_an_evidence_file_says_so_and_toggles()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        var card = vm.Moments[0];
        await vm.TogglePeekCommand.ExecuteAsync(card);
        card.IsPeekOpen.Should().BeTrue();
        card.HasPeekRows.Should().BeFalse();
        card.PeekText.Should().Be("No evidence file to show for this moment.");
        await vm.TogglePeekCommand.ExecuteAsync(card);
        card.IsPeekOpen.Should().BeFalse();
        card.SetPeek(EzForensics.Core.Spectre.EvidencePeek.Build("Security.csv", new[] { "EventId" }, new[] { new[] { "4688" } }, 12));
        card.PeekRows.Should().HaveCount(2);
        card.PeekText.Should().Be("Security.csv · 1 of 12 rows");
    }
}
