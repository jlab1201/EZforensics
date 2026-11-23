using System.IO;
using System.Text.Json.Nodes;
using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The Findings Story view and the walk-through slides over the fake Spectre engine.</summary>
/// <remarks>In the WpfApp collection: page tools Invoke on Application.Current's dispatcher, which a smoke test's STA thread owns
/// while it runs; running beside it in parallel deadlocks.</remarks>
[Collection("WpfApp")]
public sealed class WalkthroughViewModelTests : IDisposable
{
    private readonly string _dir = FindingsViewModelTests.TempDir();
    private readonly FindingsViewModelTests.FakeEngine _engine = new();

    public WalkthroughViewModelTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private async Task<FindingsViewModel> OpenAsync()
    {
        SpectreCaseLink.Save(_dir, new SpectreCaseLink("case1", 5, DateTime.UtcNow));
        var vm = new FindingsViewModel(() => _dir, new SpectreFindingsClient(_engine, () => _dir, null), _ => Task.FromResult(true)) { Ask = (_, _) => "my rationale" };
        await vm.OpenCaseAsync(_dir);
        return vm;
    }

    // rank order (M-1 first) differs from time order (M-2 first)
    private const string RankNotTimeJson = """
        {"case":"case1","time_basis":"UTC",
         "findings":[{"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High","tactic":"Credential Access","start":"2026-01-01T10:00:00Z"},
                     {"id":"F-2","finding_id":"F-2","title":"Other","severity":"Low","tactic":"Execution","start":"2019-01-01T09:00:00Z"}],
         "undated":[{"id":"F-3","finding_id":"F-3","title":"No date","severity":"Low"}],"clusters":[],
         "moments":[{"id":"M-1","rank":1,"time":"2026-01-01T10:00:00Z","primary_finding_id":"F-1","finding_ids":["F-1"],"headline":"High: Mimikatz"},
                    {"id":"M-2","rank":2,"time":"2019-01-01T09:00:00Z","primary_finding_id":"F-2","finding_ids":["F-2"],"headline":"First observed Execution"}]}
        """;

    [Fact]
    public async Task Slides_are_the_overview_then_each_moment_in_time_order_then_the_summary()
    {
        _engine.TimelineReply = RankNotTimeJson;
        _engine.ListReply = """{"findings":[{"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High"},{"id":"F-2","finding_id":"F-2","title":"Other","severity":"Low"},{"id":"F-3","finding_id":"F-3","title":"No date","severity":"Low"}],"unregistered":[]}""";
        var vm = await OpenAsync();
        var w = vm.Walkthrough;
        w.Open();
        w.IsOpen.Should().BeTrue();
        w.Slides.Select(s => s.Kind).Should().Equal(SlideKind.Overview, SlideKind.Moment, SlideKind.Moment, SlideKind.Summary);
        w.Slides.Where(s => s.Card != null).Select(s => s.Card!.Id).Should().Equal("M-2", "M-1");
        w.Dots.Should().HaveCount(4);
        w.IsOverview.Should().BeTrue();
        w.FocusMomentId.Should().BeNull();
        w.UndatedText.Should().StartWith("1 finding has no time");
        vm.StoryMoments.Select(m => m.Id).Should().Equal("M-2", "M-1");
    }

    private const string ManyHostsJson = """
        {"case":"case1","time_basis":"UTC",
         "findings":[{"id":"F-1","finding_id":"F-1","title":"A","severity":"Low","tactic":"Execution","host":"HOST-A","start":"2026-01-01T09:00:00Z"},
                     {"id":"F-2","finding_id":"F-2","title":"B","severity":"High","tactic":"Credential Access","host":"HOST-B","start":"2026-01-01T10:00:00Z"},
                     {"id":"F-3","finding_id":"F-3","title":"C","severity":"High","tactic":"Credential Access","host":"HOST-B","start":"2026-01-01T11:00:00Z"},
                     {"id":"F-4","finding_id":"F-4","title":"D","severity":"Critical","tactic":"Credential Access","host":"HOST-B","start":"2026-01-01T12:00:00Z"},
                     {"id":"F-5","finding_id":"F-5","title":"E","severity":"Medium","tactic":"Execution","host":"HOST-A","start":"2026-01-01T13:00:00Z"}],
         "undated":[],"clusters":[],
         "moments":[{"id":"M-1","rank":1,"time":"2026-01-01T09:00:00Z","primary_finding_id":"F-1","finding_ids":["F-1"],"headline":"Low: A"},
                    {"id":"M-2","rank":2,"time":"2026-01-01T10:00:00Z","primary_finding_id":"F-2","finding_ids":["F-2"],"headline":"High: B"}]}
        """;

    [Fact]
    public async Task Summary_line_has_severity_counts_most_severe_first_without_zeros()
    {
        _engine.TimelineReply = ManyHostsJson;
        _engine.ListReply = """{"findings":[{"id":"F-1","finding_id":"F-1","title":"A","severity":"Low"},{"id":"F-2","finding_id":"F-2","title":"B","severity":"High"},{"id":"F-3","finding_id":"F-3","title":"C","severity":"High"},{"id":"F-4","finding_id":"F-4","title":"D","severity":"Critical"},{"id":"F-5","finding_id":"F-5","title":"E","severity":"Medium"}],"unregistered":[]}""";
        var vm = await OpenAsync();
        var w = vm.Walkthrough;
        w.Open();
        w.SeveritySummary.Select(s => s.Text).Should().Equal("1 Critical", "2 High", "1 Medium", "1 Low");
        w.SeveritySummary.Select(s => s.Severity).Should().Equal("Critical", "High", "Medium", "Low");
        w.SeveritySummary.Select(s => s.Count).Should().Equal(1, 2, 1, 1);
    }

    [Fact]
    public async Task Summary_line_skips_severities_with_no_findings()
    {
        var vm = await OpenAsync();     // the default case: one High, one Low
        var w = vm.Walkthrough;
        w.Open();
        w.SeveritySummary.Select(s => s.Severity).Should().Equal("High", "Low");
        w.SeveritySummary.Should().OnlyContain(s => s.Count > 0);
    }

    [Fact]
    public async Task Computers_is_one_name_or_many_and_the_current_slides_computer_is_marked()
    {
        var vm = await OpenAsync();
        var w = vm.Walkthrough;
        w.Open();
        w.ComputerLabel.Should().Be("Host");
        w.Computers.Select(c => c.Name).Should().Equal("H1");
        w.Computers.Should().OnlyContain(c => !c.IsCurrent);
        w.Next();
        w.Computers.Single().IsCurrent.Should().BeTrue();
        w.Last();
        w.Computers.Should().OnlyContain(c => !c.IsCurrent);
    }

    [Fact]
    public async Task Computers_lists_every_host_and_the_mark_follows_the_slide()
    {
        _engine.TimelineReply = ManyHostsJson;
        var vm = await OpenAsync();
        var w = vm.Walkthrough;
        w.Open();
        w.ComputerLabel.Should().Be("Hosts");
        w.Computers.Select(c => c.Name).Should().BeEquivalentTo("HOST-A", "HOST-B");
        w.Computers.Should().OnlyContain(c => !c.IsCurrent);
        w.Next();
        w.Computers.Where(c => c.IsCurrent).Select(c => c.Name).Should().Equal(vm.CardHost(w.Card!));
        w.Next();
        w.Computers.Where(c => c.IsCurrent).Select(c => c.Name).Should().Equal(vm.CardHost(w.Card!));
        w.Computers.Count(c => c.IsCurrent).Should().Be(1);
        w.Computers.Select(c => c.Suffix).Should().Equal(", ", "");
    }

    [Fact]
    public async Task Next_and_Back_stop_at_the_ends_and_select_the_slide_moment()
    {
        var vm = await OpenAsync();
        var w = vm.Walkthrough;
        w.Open();
        w.Back();
        w.Index.Should().Be(0);
        w.CanBack.Should().BeFalse();
        w.Next();
        w.IsMoment.Should().BeTrue();
        w.Card!.Id.Should().Be("M-1");
        w.FocusMomentId.Should().Be("M-1");
        w.MomentHeader.Should().Be("Key event 1 of 2");
        vm.SelectedMoment!.Id.Should().Be("M-1");
        for (int i = 0; i < 10; i++) w.Next();
        w.IsSummary.Should().BeTrue();
        w.Index.Should().Be(3);
        w.CanNext.Should().BeFalse();
        w.Dots.Count(d => d.IsCurrent).Should().Be(1);
        w.Dots[3].IsCurrent.Should().BeTrue();
        w.Close();
        w.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_on_a_slide_updates_the_card_and_the_counts()
    {
        var vm = await OpenAsync();
        var w = vm.Walkthrough;
        w.Open();
        vm.NeedReviewCount.Should().Be(2);
        w.GoToMoment("M-2").Should().BeTrue();   // F-1, the finding the fake engine keeps the approval of
        w.OpenCount.Should().Be(2);
        await w.ConfirmCommand.ExecuteAsync(null);
        vm.Analysis!.Items.Single(i => i.Source.SpectreFindingId == "F-1").Review.Should().Be(ReviewState.Confirmed);
        _engine.Calls.Should().NotContain(c => c.Tool == "spectre_finding_approve");
        w.Card!.StatusText.Should().Be("Confirmed");
        w.ConfirmedCount.Should().Be(1);
        w.OpenCount.Should().Be(1);
        vm.NeedReviewCount.Should().Be(1);
        w.Last();
        w.ConfirmedText.Should().Be("1 of 2 key events confirmed by you.");
        w.OpenText.Should().Be("1 still needs your review.");
        w.MakeReportCommand.Execute(null);
        w.IsOpen.Should().BeFalse();
        vm.IsReportOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Confirmed_only_keeps_approved_key_events_in_both_views_and_the_slide_index_valid()
    {
        var vm = await OpenAsync();
        var w = vm.Walkthrough;
        w.Open();
        w.GoToMoment("M-2").Should().BeTrue();
        await w.ConfirmCommand.ExecuteAsync(null);
        w.Last();
        vm.ConfirmedOnly = true;
        w.Slides.Select(s => s.Kind).Should().Equal(SlideKind.Overview, SlideKind.Moment, SlideKind.Summary);
        w.Slides[1].Card!.Id.Should().Be("M-2");
        w.Index.Should().Be(2);                                  // still on the summary
        vm.AttackFlowNodes.Select(n => n.Id).Should().Equal("M-2");
        vm.HostTimeline.Moments.Select(m => m.Id).Should().Equal("M-2");
        vm.ShowNoConfirmed.Should().BeFalse();
        w.GoToMoment("M-2").Should().BeTrue();
        vm.ConfirmedOnly = false;
        w.Slides.Count(s => s.Kind == SlideKind.Moment).Should().Be(2);
        w.Card!.Id.Should().Be("M-2");                           // the slide shown is kept
        vm.AttackFlowNodes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Confirmed_only_with_nothing_confirmed_says_so()
    {
        var vm = await OpenAsync();
        var w = vm.Walkthrough;
        w.Open();
        w.Last();
        vm.ConfirmedOnly = true;
        vm.ShowNoConfirmed.Should().BeTrue();
        vm.AttackFlowNodes.Should().BeEmpty();
        w.Slides.Select(s => s.Kind).Should().Equal(SlideKind.Overview, SlideKind.Summary);
        w.Index.Should().Be(1);
        w.OverviewHint.Should().Be("No confirmed key events yet.");
    }

    [Fact]
    public async Task Arrow_keys_move_the_timeline_slides_but_not_the_attack_flow()
    {
        var vm = await OpenAsync();
        var w = vm.Walkthrough;
        w.Open();
        w.HandleKey(System.Windows.Input.Key.Right).Should().BeTrue();
        w.Index.Should().Be(1);
        w.HandleKey(System.Windows.Input.Key.Left).Should().BeTrue();
        w.Index.Should().Be(0);
        w.HandleKey(System.Windows.Input.Key.End).Should().BeTrue();
        w.IsSummary.Should().BeTrue();
        w.HandleKey(System.Windows.Input.Key.A).Should().BeFalse();
        vm.SelectedStoryView = StoryView.AttackFlow;
        w.HandleKey(System.Windows.Input.Key.Left).Should().BeFalse();
        w.IsSummary.Should().BeTrue();
        vm.SelectedStoryView = StoryView.Timeline;
        w.Leave();
        w.HandleKey(System.Windows.Input.Key.Left).Should().BeFalse();
    }

    [Fact]
    public async Task The_chat_moves_the_open_slides()
    {
        var vm = await OpenAsync();
        vm.Walkthrough.Open();
        vm.Walkthrough.IsOpen.Should().BeTrue();
        (await FindingsViewModelTests.Run(vm, "findings_select_moment", new JsonObject { ["moment"] = "2" })).IsError.Should().BeFalse();
        vm.Walkthrough.Card!.Id.Should().Be("M-2");
        vm.AssistantStateCore().Should().Contain("Showing: the walk-through slides (Slide 3 of 4)");
    }

    [Fact]
    public async Task Overview_has_the_headline_tiles_and_plain_cards()
    {
        var vm = await OpenAsync();
        vm.SelectedTab.Should().Be(FindingsTab.Overview);
        vm.ShowOverview.Should().BeTrue();
        vm.ShowStory.Should().BeFalse();
        vm.HeadlineText.Should().StartWith("Spectre found 2 key events").And.Contain("(UTC)");
        vm.KeyMomentsTile.Should().Be("2");
        vm.FirstSignTile.Should().Be("1 Jan 2026, 09:00 UTC");
        vm.LastSignTile.Should().Be("1 Jan 2026, 10:00 UTC");
        vm.ShowStoryTimeline.Should().BeTrue();
        var card = vm.StoryMoments[1];
        card.Severity.Should().Be("High");
        card.CountLine.Should().Be("1 Jan 2026, 10:00 UTC");
        card.TechnicalText.Should().Contain("Credential Access").And.Contain("Rule: Mimikatz");
        vm.ShowDetails = true;
        vm.SelectedTab.Should().Be(FindingsTab.Details);
        vm.Selected.Should().BeNull();
        vm.EditMomentCommand.Execute(card);   // Edit on a card: the Details tab, with that finding selected
        vm.SelectedTab.Should().Be(FindingsTab.Details);
        vm.Selected!.Id.Should().Be(card.PrimaryFindingId);
        vm.ShowDetails = false;
        vm.SelectedTab.Should().Be(FindingsTab.Overview);
        vm.Walkthrough.IsOpen.Should().BeFalse();
        vm.EditMomentCommand.Execute(card);
        vm.SelectedTab.Should().Be(FindingsTab.Details);
    }

    [Fact]
    public async Task The_slides_are_the_Story_tab_and_stop_when_it_is_left()
    {
        var vm = await OpenAsync();
        vm.Walkthrough.IsOpen.Should().BeFalse();
        vm.SelectedTab = FindingsTab.Story;
        vm.Walkthrough.IsOpen.Should().BeTrue();
        vm.ShowStory.Should().BeTrue();
        vm.Walkthrough.Next();
        vm.SelectedTab = FindingsTab.Details;
        vm.Walkthrough.IsOpen.Should().BeFalse();
        vm.ShowStory.Should().BeFalse();
        vm.SelectedTab = FindingsTab.Story;   // back on the slide it was on
        vm.Walkthrough.IsOpen.Should().BeTrue();
        vm.Walkthrough.Index.Should().Be(1);
        vm.Walkthrough.Open();                // Start: the Story tab at slide 0
        vm.Walkthrough.Index.Should().Be(0);
        vm.Walkthrough.Close();
        vm.SelectedTab.Should().Be(FindingsTab.Overview);
        vm.Walkthrough.IsOpen.Should().BeFalse();
        vm.SelectedTabIndex = 1;   // Overview, Details, Story
        vm.ShowDetails.Should().BeTrue();
    }

    [Fact]
    public async Task Rerun_card_shows_for_an_old_or_undated_analysis_and_Details_labels_the_time_basis()
    {
        var vm = await OpenAsync();
        vm.ShowRerunCard.Should().BeFalse();
        File.Delete(CaseAnalysisStore.PathFor(_dir));   // a case opened fresh with the stale analysis
        _engine.TimelineReply = """
            {"case":"case1","time_basis":"UTC","analysis_stale":true,"findings":[],"clusters":[],"moments":[],
             "undated":[{"id":"F-1","finding_id":"F-1","title":"x","severity":"Low","artifact_type":"logfile_record"}]}
            """;
        vm = await OpenAsync();
        vm.ShowRerunCard.Should().BeTrue();
        FindingsViewModel.RerunTitle.Should().Be("Re-run Spectre to put this case on a timeline");
        vm.Rows.Single(r => r.Id == "F-1").IsDated.Should().BeFalse();

        static FindingRowViewModel Row(string artifact, string time) =>
            new(SpectreFinding.From(new JsonObject { ["id"] = "F-9", ["title"] = "t", ["artifact_type"] = artifact, ["event_time"] = time }));
        var log = Row("logfile_record", "2026-01-01T10:00:00");
        log.Time.Should().Be("2026-01-01 10:00:00 (local clock)");
        log.SetDated(true);
        log.Time.Should().Be("2026-01-01 10:00:00 UTC");
        Row("evtx", "2026-01-01T10:00:00Z").Time.Should().Be("2026-01-01 10:00:00 UTC");
        Row("evtx", "2026-01-01T10:00:00").Time.Should().EndWith("(zone not stated)");
        Row("evtx", "").Time.Should().BeEmpty();
    }

    [Fact]
    public async Task Show_the_evidence_opens_the_panel_and_its_Analyze_link_hands_over_the_rows_of_every_finding()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "EventLogs"));
        var sec = Path.Combine(_dir, "EventLogs", "Security.csv");
        var other = Path.Combine(_dir, "EventLogs", "Other.csv");
        File.WriteAllText(sec, "A,B\n1,2\n3,4\n5,6\n");
        File.WriteAllText(other, "A,B\n1,2\n");
        _engine.ListReply = """
            {"findings":[
              {"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High","evidence_total":3,
               "evidence":[{"case_path":"EventLogs/Security.csv","row":0},{"case_path":"EventLogs/Security.csv","row":2},{"case_path":"EventLogs/Other.csv","row":0}]},
              {"id":"F-2","finding_id":"F-2","title":"Other","severity":"Low","case_path":"EventLogs/Security.csv","source_record_id":"2"},
              {"id":"F-3","finding_id":"F-3","title":"Bare","severity":"Low"}],"unregistered":[]}
            """;
        _engine.TimelineReply = """
            {"case":"case1","time_basis":"UTC","findings":[{"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High","start":"2026-01-01T10:00:00Z"}],"undated":[],"clusters":[],
             "moments":[{"id":"M-1","rank":1,"time":"2026-01-01T10:00:00Z","primary_finding_id":"F-1","finding_ids":["F-1","F-2"],"headline":"High: Mimikatz","plain_title":"Credential tool ran"},
                        {"id":"M-2","rank":2,"time":"2026-01-02T10:00:00Z","primary_finding_id":"F-3","finding_ids":["F-3"],"headline":"Bare"}]}
            """;
        var vm = await OpenAsync();
        var calls = new List<(string Dir, IReadOnlyList<EvidenceTarget> Plan, string Title)>();
        vm.EvidenceOpener = (d, p, t) => calls.Add((d, p, t));

        // Show the evidence opens the panel on this page (not Analyze); its Open in Analyze link hands Analyze the same plan
        await vm.ShowMomentEvidenceCommand.ExecuteAsync(vm.StoryMoments.Single(m => m.Id == "M-1"));
        calls.Should().BeEmpty();
        var panel = vm.EvidencePanel;
        panel.IsOpen.Should().BeTrue();
        panel.Title.Should().Be("Credential tool ran");
        panel.Files.Select(f => f.Name).Should().Equal("Security.csv", "Other.csv");
        panel.CurrentFile!.SpectreRows.Should().Equal(0, 2);   // F-1's rows 0 and 2 (a key event is one item; F-2 is a flag of its own)
        panel.OpenInAnalyzeCommand.Execute(null);
        panel.IsOpen.Should().BeFalse();
        var call = calls.Should().ContainSingle().Subject;
        call.Title.Should().Be("Credential tool ran");
        call.Plan.Select(p => Path.GetFileName(p.Csv)).Should().Equal("Security.csv", "Other.csv");
        call.Plan[0].Rows.Should().Equal(0, 2);          // F-1's engine rows
        call.Plan[0].RecordIds.Should().BeEmpty();
        call.Plan[1].Rows.Should().Equal(0);

        // a finding the engine gave no file for: nothing to open on Analyze, the editor's Evidence tab opens instead
        await vm.ShowMomentEvidenceCommand.ExecuteAsync(vm.StoryMoments.Single(m => m.Id == "M-2"));
        calls.Should().HaveCount(1);
        vm.SelectedTab.Should().Be(FindingsTab.Details);
        vm.SelectedDetailTab.Should().Be(FindingsViewModel.TabEvidence);
    }

    [Fact]
    public async Task Stale_banner_shows_only_for_an_old_analysis()
    {
        var vm = await OpenAsync();
        vm.ShowStaleBanner.Should().BeFalse();

        File.Delete(CaseAnalysisStore.PathFor(_dir));
        _engine.TimelineReply = """
            {"case":"case1","time_basis":"UTC","analysis_stale":true,"stale_reason":"old adapter",
             "findings":[],"undated":[{"id":"F-1","finding_id":"F-1","title":"x","severity":"Low","artifact_type":"logfile_record"}],"clusters":[],"moments":[]}
            """;
        vm = await OpenAsync();
        vm.ShowStaleBanner.Should().BeTrue();
        vm.StaleDetail.Should().Be("old adapter");
        FindingsViewModel.StaleText.Should().Be("Analysed with an older Spectre. Re-run to get dates and plain names.");
        vm.ShowStoryTimeline.Should().BeFalse();
        vm.ShowStoryNoDates.Should().BeTrue();
    }

    [Theory]
    [InlineData("engine error: {\"code\":500}")]
    [InlineData("{\"error\":\"raw\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("Traceback: KeyError ['x'] in {module}")]
    public void Friendly_error_text_never_shows_raw_replies(string message)
    {
        SpectreErrors.Friendly(new SpectreToolException("spectre_timeline", message)).Should().NotContain("{").And.NotContain("[");
        SpectreErrors.Friendly(new IOException(message)).Should().NotContain("{");
        SpectreErrors.Friendly(new Exception(message)).Should().NotContain("{");
        FindingsViewModel.TimelineErrorText(new SpectreToolException("spectre_timeline", message)).Should().NotContain("{");
    }

    [Fact]
    public async Task Page_errors_show_the_friendly_text()
    {
        _engine.TimelineFails = true;
        var vm = await OpenAsync();
        vm.TimelineError.Should().NotBeEmpty().And.NotContain("{");
        vm.ShowError("Could not load", new Exception("bad reply {\"x\":1}"));
        vm.Banner!.Message.Should().NotContain("{").And.StartWith("Could not load: bad reply");
    }
}
