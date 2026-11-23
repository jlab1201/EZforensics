using System.IO;
using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Analysis;
using EzForensics.Core.Execution;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The Findings page over the case analysis store: the empty state, Add my analysis, duplicates, chips, key events, Remove.</summary>
[Collection("WpfApp")]
public sealed class FindingsAnalysisStoreTests : IDisposable
{
    private const string Csv = "PC1/EventLogs/Security.csv";
    private readonly string _dir = FindingsViewModelTests.TempDir();
    private readonly FindingsViewModelTests.FakeEngine _engine = new();

    public FindingsAnalysisStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "PC1", "EventLogs"));
        File.WriteAllText(Path.Combine(_dir, "PC1", "EventLogs", "Security.csv"),
            "Time,EventId,Message\n2026-01-01 10:00:00,4624,logon alice\n2026-01-01 10:01:00,4688,mimikatz.exe started\n2026-01-01 10:02:00,4624,logon bob\n" +
            "2026-01-01 10:03:00,4672,special privileges\n2026-01-01 10:04:00,4634,logoff alice\n");
        // Spectre: F-1 (a key event: it is a moment) on rows 1 and 3, F-2 (a flag) on row 1 only
        _engine.ListReply = $$"""
            {"findings":[
              {"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High","mitre_attack":["T1003"],"evidence":[{"case_path":"{{Csv}}","row":1},{"case_path":"{{Csv}}","row":3}]},
              {"id":"F-2","finding_id":"F-2","title":"Logon","severity":"Low","evidence":[{"case_path":"{{Csv}}","row":1}]}],"unregistered":[]}
            """;
        _engine.TimelineReply = """
            {"case":"case1","time_basis":"UTC","findings":[{"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High","start":"2026-01-01T10:01:00Z"},
                                                           {"id":"F-2","finding_id":"F-2","title":"Logon","severity":"Low","start":"2026-01-01T10:01:00Z"}],"undated":[],"clusters":[],
             "moments":[{"id":"M-1","rank":1,"time":"2026-01-01T10:01:00Z","primary_finding_id":"F-1","finding_ids":["F-1"],"headline":"High: Mimikatz"}]}
            """;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private FindingsViewModel Vm(int? phase, Func<string, Task<(Job? Job, string? Reason)>>? enqueue = null)
    {
        if (phase != null) SpectreCaseLink.Save(_dir, new SpectreCaseLink("case1", phase, DateTime.UtcNow));
        return new FindingsViewModel(() => _dir, new SpectreFindingsClient(_engine, () => _dir, null), _ => Task.FromResult(true), queue: () => null, enqueue: enqueue)
        { Ask = (_, _) => "why", ConfirmRestore = (_, _) => true, ConfirmRemoveFromKeyEvent = (_, _) => true };
    }

    /// <summary>The analyst's key event on rows 1 and 3 of the same file (the same evidence as Spectre's F-1).</summary>
    private void AnalystKeyEvent(params long[] rows)
    {
        var store = CaseAnalysisStore.Open(_dir);
        store.Data.AddFinding("Credential dumping", "High", new[] { "T1003" }, "my notes", rows.Select(r => new AnalysisRow(Csv, r)));
        store.Save();
    }

    private static AnalysisItem Spectre(FindingsViewModel vm, string id) => vm.Analysis!.Items.Single(i => i.Source.SpectreFindingId == id);


    [Fact]
    public async Task Empty_state_Run_Spectre_first_queues_the_analysis()
    {
        string? queued = null;
        var vm = Vm(phase: 1, enqueue: dir => { queued = dir; return Task.FromResult<(Job?, string?)>((null, "test")); });
        await vm.OpenCaseAsync(_dir);
        vm.ShowEmptyState.Should().BeTrue();
        vm.AnalyseButtonText.Should().Be("Run Spectre");
        vm.AnalyseCommand.CanExecute(null).Should().BeTrue();
        await vm.AnalyseCommand.ExecuteAsync(null);
        queued.Should().Be(_dir);
    }

    [Fact]
    public async Task Add_my_analysis_after_the_import_shows_a_duplicate_pair_and_Keep_A_hides_B()
    {
        AnalystKeyEvent(1, 3);
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);                    // Spectre's results are imported on open
        vm.Rows.Select(r => r.Id).Should().BeEquivalentTo("F-1", "F-2");
        var f1 = Spectre(vm, "F-1").Id;
        bool WithF1(DuplicatePair p, string other) => (p.AId == f1 && p.BId == other) || (p.BId == f1 && p.AId == other);
        vm.SyncChangeCount.Should().Be(1);               // the badge: one change since last added

        await vm.AddMyAnalysisCommand.ExecuteAsync(null);
        var mine = vm.Analysis!.Items.Single(i => i.Origin == ItemOrigin.Analyst).Id;
        var pair = vm.DuplicatePairs.Single(p => WithF1(p, mine));
        pair.Match.Should().Be(DuplicateMatch.SameEvidence);
        vm.Rows.Count(r => r.HasDuplicatePair).Should().BeGreaterThanOrEqualTo(2);      // the pair is on both cards
        vm.Moments.Should().ContainSingle();             // only Spectre's moment is a key event; your finding is not on the timeline until you add it
        vm.ReviewBreakdown.Sum(l => l.Count).Should().Be(3);   // nothing is resolved without a click: both are counted

        vm.OpenCompareCommand.Execute(null);
        vm.IsCompareOpen.Should().BeTrue();
        var (a, b) = (pair.AId, pair.BId);
        vm.ResolvePair(pair, DuplicateResolution.KeepA);

        vm.DuplicatePairs.Should().NotContain(p => (p.AId == a && p.BId == b) || (p.AId == b && p.BId == a));
        vm.Analysis!.Find(b)!.DuplicateOf.Should().Be(a);
        vm.Moments.Should().ContainSingle();
        vm.Rows.Single(r => r.Item!.Id == b).IsHiddenDuplicate.Should().BeTrue();   // but Details still lists it, with its chip
        vm.ReviewBreakdown.Sum(l => l.Count).Should().Be(2);   // B is hidden from the counts

        // a later import and sync leave the decision alone
        await vm.LoadFindingsAsync();
        await vm.AddMyAnalysisCommand.ExecuteAsync(null);
        vm.Analysis!.Find(b)!.DuplicateOf.Should().Be(a);
        vm.DuplicatePairs.Should().NotContain(p => p.AId == b || p.BId == b);
    }



    [Fact]
    public async Task Removed_at_source_items_keep_their_chip_until_Keep_anyway_or_Remove()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        _engine.ListReply = """{"findings":[{"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High"}],"unregistered":[]}""";
        await vm.LoadFindingsAsync();                    // F-2 is no longer reported
        var gone = vm.Rows.Single(r => r.Id == "F-2");
        gone.IsRemovedAtSource.Should().BeTrue();
        gone.ChipText.Should().Contain("No longer reported by Spectre");
        vm.ReviewBreakdown.Sum(l => l.Count).Should().Be(1);       // left out of the counts until you decide
        vm.KeepAnywayCommand.Execute(gone);
        vm.Rows.Single(r => r.Id == "F-2").IsRemovedAtSource.Should().BeFalse();
        vm.ReviewBreakdown.Sum(l => l.Count).Should().Be(2);

        vm.RemoveItemCommand.Execute(vm.Rows.Single(r => r.Id == "F-2"));
        vm.Rows.Select(r => r.Id).Should().NotContain("F-2");      // a tombstone: it never comes back
        _engine.ListReply = """{"findings":[{"id":"F-1","finding_id":"F-1","title":"Mimikatz","severity":"High"},{"id":"F-2","finding_id":"F-2","title":"Logon","severity":"Low"}],"unregistered":[]}""";
        await vm.LoadFindingsAsync();
        vm.Rows.Select(r => r.Id).Should().NotContain("F-2");
    }

    [Fact]
    public async Task Spectre_stays_out_of_the_way_of_your_edits_and_the_page_never_writes_to_the_engine()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        vm.Selected = vm.Rows.Single(r => r.Id == "F-1");
        vm.DraftTitle = "My title";
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.LoadFindingsAsync();
        vm.Rows.Single(r => r.Id == "F-1").Title.Should().Be("My title");
        _engine.Calls.Select(c => c.Tool).Should().NotContain(new[] { "spectre_finding_edit", "spectre_finding_approve", "spectre_evidence_flag", "spectre_finding_create" });
    }

    // ------------------------------------------------------------ Review page: findings and key events (Task 5)

    [Fact]
    public async Task AddToTimeline_ShowsKeyEventInListAndTimeline()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        var row = vm.Rows.Single(r => r.Id == "F-2");
        row.IsKeyEvent.Should().BeFalse();
        vm.Moments.Should().ContainSingle();

        vm.AddToTimelineCommand.Execute(row);

        vm.Rows.Single(r => r.Id == "F-2").IsKeyEvent.Should().BeTrue();          // the star
        vm.KeyEventRows.Should().HaveCount(2);
        vm.SelectedKeyEvent!.Title.Should().Be("Logon");
        vm.SelectedKeyEvent.FindingCount.Should().Be(1);
        vm.DetailListTab.Should().Be(FindingsViewModel.ListTabKeyEvents);   // the Key events tab opens on the new key event
        vm.ShowKeyEventEditor.Should().BeTrue();
        vm.ShowFindingEditor.Should().BeFalse();
        vm.Moments.Should().HaveCount(2);
        vm.Moments.Should().Contain(m => m.PrimaryFindingId == "F-2");
    }

    [Fact]
    public async Task GroupTwoFindings_OneKeyEvent_TwoFindings()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        vm.GroupIntoKeyEventCommand.CanExecute(null).Should().BeFalse();
        vm.SetSelectedRows(vm.Rows.Where(r => r.Id is "F-1" or "F-2").ToList());
        vm.GroupIntoKeyEventCommand.CanExecute(null).Should().BeTrue();
        vm.GroupIntoKeyEventCommand.Execute(null);

        var live = vm.Analysis!.KeyEvents.Where(k => !k.Dismissed).ToList();
        live.Should().ContainSingle().Which.FindingIds.Should().HaveCount(2);    // F-1 left Spectre's key event, which is removed (empty)
        vm.KeyEventRows.Should().ContainSingle().Which.FindingCount.Should().Be(2);
        vm.KeyEventRows[0].Severity.Should().Be("High");
        vm.Moments.Should().ContainSingle();
    }

    [Fact]
    public async Task RejectFindingInKeyEvent_Cancel_ChangesNothing()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        string? asked = null;
        vm.ConfirmRemoveFromKeyEvent = (_, m) => { asked = m; return false; };
        vm.Selected = vm.Rows.Single(r => r.Id == "F-1");
        await vm.RejectCommand.ExecuteAsync(null);

        asked.Should().StartWith("Remove it from key event ").And.EndWith(" too?");
        Spectre(vm, "F-1").Review.Should().NotBe(ReviewState.Rejected);
        KeyEventOps.KeyEventOf(vm.Analysis!, Spectre(vm, "F-1").Id).Should().NotBeNull();
        vm.Moments.Should().ContainSingle();
    }

    [Fact]
    public async Task RejectFindingInKeyEvent_Remove_RemovesFromKeyEvent()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        vm.ConfirmRemoveFromKeyEvent = (_, _) => true;
        vm.Selected = vm.Rows.Single(r => r.Id == "F-1");
        await vm.RejectCommand.ExecuteAsync(null);

        Spectre(vm, "F-1").Review.Should().Be(ReviewState.Rejected);
        KeyEventOps.KeyEventOf(vm.Analysis!, Spectre(vm, "F-1").Id).Should().BeNull();
        vm.KeyEventRows.Should().BeEmpty();                                         // its only finding left, so the key event is removed
        vm.Moments.Should().BeEmpty();
    }

    [Fact]
    public async Task KeyEventEditor_EditTime_PersistsAndMarksEdited()
    {
        var vm = Vm(phase: 5);
        await vm.OpenCaseAsync(_dir);
        vm.SelectedKeyEvent = vm.KeyEventRows.Single();
        vm.KeyEventFindings.Select(f => f.Id).Should().Equal("F-1");
        vm.CanSaveKeyEvent.Should().BeFalse();
        vm.KeyEventDraftTime = "not a time";
        vm.KeyEventTimeInvalid.Should().BeTrue();
        vm.CanSaveKeyEvent.Should().BeFalse();
        vm.KeyEventDraftTime = "2026-01-01 09:30:00";
        vm.CanSaveKeyEvent.Should().BeTrue();
        vm.SaveKeyEventCommand.Execute(null);

        var k = CaseAnalysisStore.Open(_dir).Data.KeyEvents.Single(x => !x.Dismissed);
        k.Time.Should().Be(new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero));
        k.IsEdited(KeyEventFields.Time).Should().BeTrue();
        vm.KeyEventRows.Single().TimeText.Should().Be("2026-01-01 09:30:00 UTC");
        vm.Moments.Single().Moment.Time.Should().Be(k.Time);
        vm.KeyEventEditMessage.Should().Be("Saved.");
    }

    [Fact]
    public async Task EmptyState_UseMyAnalysis_EnabledWhenWorkspaceHasFindings()
    {
        AnalystKeyEvent(1, 3);
        var vm = Vm(phase: 1);
        await vm.OpenCaseAsync(_dir);
        vm.ShowEmptyState.Should().BeTrue();
        vm.CanUseMyAnalysis.Should().BeTrue();
    }
}
