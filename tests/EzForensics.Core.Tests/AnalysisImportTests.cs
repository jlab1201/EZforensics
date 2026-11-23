using System.Text.Json.Nodes;
using EzForensics.Core.Analysis;
using FluentAssertions;
using static EzForensics.Core.Tests.AT;

namespace EzForensics.Core.Tests;

public class AnalysisImportTests
{


    [Fact]
    public void Analyst_origin_findings_are_not_mapped_as_spectre_items()
    {
        var a = new CaseAnalysis();
        AT.Import(a, new[] { Finding("F-9", origin: "analyst", title: "Mine"), Finding("F-1") });
        a.Items.Where(i => i.Origin == ItemOrigin.Spectre).Should().ContainSingle();
        a.Items.Single(i => i.Origin == ItemOrigin.Analyst).Title.Should().Be("Mine");   // migrated into the workspace
    }



    [Fact]
    public void Import_never_touches_review_duplicates_row_edits_keep_dismissed_or_edited_fields()
    {
        var a = new CaseAnalysis();
        AT.Import(a, new[] { Finding("F-1", evidence: new[] { ("PC1/a.csv", 1L), ("PC1/a.csv", 2L) }) });
        var i = SpItem(a, "F-1");
        AnalysisEdits.SetTitle(i, "Edited", Now);
        AnalysisEdits.SetReview(i, ReviewState.Confirmed, Now);
        AnalysisEdits.RemoveRow(i, R("PC1/a.csv", 2), Now);
        AnalysisEdits.AddRow(i, R("PC1/z.csv", 8), Now);
        i.KeptByUser = true; i.DuplicateOf = "X"; i.MergedInto = null;
        i.DupDecisions.Add(new DupDecision { OtherId = "Y", RowSetHash = "h" });
        var protectedBefore = (i.Review, i.DuplicateOf, i.KeptByUser, i.Dismissed, string.Join(",", i.EditedFields), AT.Json(new CaseAnalysis { Items = { new AnalysisItem { RowEdits = i.RowEdits, DupDecisions = i.DupDecisions } } }));

        AT.Import(a, new[] { Finding("F-1", title: "Spectre retitled", sev: "Critical", evidence: new[] { ("PC1/a.csv", 1L), ("PC1/a.csv", 2L), ("PC1/a.csv", 3L) }) });

        i.Title.Should().Be("Edited");
        i.Severity.Should().Be("Critical");
        i.SourceRows.Should().HaveCount(3);
        var protectedAfter = (i.Review, i.DuplicateOf, i.KeptByUser, i.Dismissed, string.Join(",", i.EditedFields), AT.Json(new CaseAnalysis { Items = { new AnalysisItem { RowEdits = i.RowEdits, DupDecisions = i.DupDecisions } } }));
        protectedAfter.Should().Be(protectedBefore);
        a.EffectiveRows(i).Select(r => r.Row).Should().BeEquivalentTo(new long[] { 1, 3, 8 });   // row 2 stays removed
    }

    [Fact]
    public void Import_twice_is_idempotent()
    {
        var a = new CaseAnalysis();
        var f = new[] { Finding("F-1"), Finding("F-2", analyst: new JsonObject { ["title"] = "x" }, approval: "APPROVED") };
        AT.Import(a, f);
        var before = AT.Json(a);
        AT.Import(a, f).AnyChange.Should().BeFalse();
        AT.Json(a).Should().Be(before);
    }

    [Fact]
    public void Gone_finding_is_marked_never_deleted_and_returns_to_current_when_it_reappears()
    {
        var a = new CaseAnalysis();
        AT.Import(a, new[] { Finding("F-1"), Finding("F-2") });
        var res = AT.Import(a, new[] { Finding("F-2") });
        var i = SpItem(a, "F-1");
        i.SourceStatus.Should().Be(SourceStatus.RemovedAtSource);
        res.RemovedAtSource.Should().Be(1);
        a.Items.Should().HaveCount(2);
        AT.Import(a, new[] { Finding("F-1"), Finding("F-2") }).Restored.Should().Be(1);
        i.SourceStatus.Should().Be(SourceStatus.Current);
    }

    [Fact]
    public void Removed_spectre_item_is_a_tombstone_and_a_later_import_does_not_recreate_it()
    {
        var a = new CaseAnalysis();
        AT.Import(a, new[] { Finding("F-1") });
        AnalysisEdits.Remove(a, SpItem(a, "F-1"), now: Now);
        AT.Import(a, new[] { Finding("F-1", title: "changed") }).Created.Should().Be(0);
        a.Items.Should().ContainSingle().Which.Dismissed.Should().BeTrue();
        a.Items[0].Title.Should().Be("Odd thing");
        AT.Import(a, Array.Empty<Core.Spectre.SpectreFinding>());   // a dismissed item is not marked gone either
        a.Items[0].SourceStatus.Should().Be(SourceStatus.Current);
    }

    [Fact]
    public void An_analyst_made_finding_is_confirmed_and_a_spectre_one_waits_for_review()
    {
        var a = new CaseAnalysis();
        var w = a.AddFinding("KE", "High", null, null, Rows("PC1/a.csv", 1), Now);
        AnalysisSync.Sync(a, Opt());
        var mine = ItemOfWorkspace(a, w.Id);
        mine.Origin.Should().Be(ItemOrigin.Analyst);
        AnalysisView.ReviewOf(mine).Should().Be(ReviewState.Confirmed);
        AT.Import(a, new[] { Finding("F-9") });
        AnalysisView.ReviewOf(SpItem(a, "F-9")).Should().Be(ReviewState.ToReview);
        AnalysisEdits.SetReview(mine, ReviewState.Rejected, Now);   // still changeable
        AnalysisView.ReviewOf(mine).Should().Be(ReviewState.Rejected);
    }

    [Fact]
    public void Evidence_and_notes_round_trip_through_the_one_notes_field()
    {
        var s = FindingNotes.Compose(" a \r\n\r\n b ", "  my notes ");
        s.Should().Be("Evidence:\na\nb\n\nmy notes");
        FindingNotes.Split(s).Should().Be(("a\nb", "my notes"));
        FindingNotes.Split("plain notes").Should().Be(("", "plain notes"));
        FindingNotes.Compose("", "n").Should().Be("n");
        FindingNotes.Split(FindingNotes.Compose("x", "")).Should().Be(("x", ""));
    }

    [Fact]
    public void Evidence_line_takes_time_host_and_key_columns()
    {
        var h = new[] { "TimeCreated", "Computer", "Junk", "EventId", "Message" };
        FindingNotes.EvidenceLine(h, new[] { "2026-01-02 08:16:00", "PC1", "zzz", "4688", "cmd.exe /c whoami" })
            .Should().Be("2026-01-02 08:16:00 | PC1 | 4688 | cmd.exe /c whoami | zzz");
        FindingNotes.EvidenceLine(new[] { "A", "B", "C", "D", "E" }, new[] { "1", "", "3", "4", "5" }).Should().Be("1 | 3 | 4");
    }

    [Fact]
    public void Row_removed_on_the_report_page_stays_removed_after_sync_and_import()
    {
        var a = new CaseAnalysis();
        var w = a.AddFinding("KE", "High", null, null, Rows("PC1/a.csv", 1, 2, 3), Now);
        AnalysisSync.Sync(a, Opt());
        var ke = ItemOfWorkspace(a, w.Id);
        AnalysisEdits.RemoveRow(ke, R("PC1/a.csv", 2), Now);
        AnalysisSync.Sync(a, Opt());
        AT.Import(a, new[] { Finding("F-1", evidence: new[] { ("PC1/a.csv", 1L), ("PC1/a.csv", 2L) }) });
        AnalysisSync.Sync(a, Opt());

        a.EffectiveRows(ke).Select(r => r.Row).Should().BeEquivalentTo(new long[] { 1, 3 });
        var sp = SpItem(a, "F-1");
        AnalysisEdits.RemoveRow(sp, R("PC1/a.csv", 2), Now);
        AT.Import(a, new[] { Finding("F-1", evidence: new[] { ("PC1/a.csv", 1L), ("PC1/a.csv", 2L) }) });
        a.EffectiveRows(sp).Select(r => r.Row).Should().Equal(1);
    }

    [Fact]
    public void Merge_then_reimport_keeps_the_loser_hidden_and_adds_its_new_rows_to_A()
    {
        var a = new CaseAnalysis();
        AT.Import(a, new[] { Finding("F-1", stage: "key_event", evidence: new[] { ("PC1/a.csv", 1L), ("PC1/a.csv", 2L) }), Finding("F-2", stage: "key_event", evidence: new[] { ("PC1/a.csv", 2L), ("PC1/b.csv", 5L) }) });
        var A = SpItem(a, "F-1"); var B = SpItem(a, "F-2");
        DuplicateDetector.FindPairs(a).Should().ContainSingle();
        DuplicateDetector.Resolve(a, DuplicateResolution.Merge, A.Id, B.Id, Now);
        a.EffectiveRows(A).Select(r => r.Row).Should().BeEquivalentTo(new long[] { 1, 2, 5 });

        AT.Import(a, new[] { Finding("F-1", stage: "key_event", evidence: new[] { ("PC1/a.csv", 1L), ("PC1/a.csv", 2L) }), Finding("F-2", stage: "key_event", evidence: new[] { ("PC1/a.csv", 2L), ("PC1/b.csv", 5L), ("PC1/b.csv", 6L) }) });
        B.MergedInto.Should().Be(A.Id);
        AnalysisView.IsActive(a, B).Should().BeFalse();
        a.EffectiveRows(A).Select(r => r.Row).Should().BeEquivalentTo(new long[] { 1, 2, 5, 6 });
        DuplicateDetector.FindPairs(a).Should().BeEmpty();
    }

    [Fact]
    public void Keep_B_then_Spectre_drops_B_raises_the_pair_again_and_shows_both()
    {
        var a = new CaseAnalysis();
        AT.Import(a, new[] { Finding("F-1", stage: "key_event", evidence: new[] { ("PC1/a.csv", 1L) }), Finding("F-2", stage: "key_event", evidence: new[] { ("PC1/a.csv", 1L) }) });
        var A = SpItem(a, "F-1"); var B = SpItem(a, "F-2");
        DuplicateDetector.Resolve(a, DuplicateResolution.KeepB, A.Id, B.Id, Now);
        AnalysisView.IsActive(a, A).Should().BeFalse();
        DuplicateDetector.FindReraised(a).Should().BeEmpty();

        AT.Import(a, new[] { Finding("F-1", stage: "key_event", evidence: new[] { ("PC1/a.csv", 1L) }) });   // Spectre drops B
        B.SourceStatus.Should().Be(SourceStatus.RemovedAtSource);
        DuplicateDetector.FindReraised(a).Should().ContainSingle().Which.Should().Be(new ReraisedPair(B.Id, A.Id));
        AnalysisView.IsActive(a, A).Should().BeTrue();              // A is shown again while the question is open
        AnalysisView.RemovedAtSourceItems(a).Should().Contain(B);    // B stays visible with its chip

        AnalysisEdits.KeepAnyway(B, Now);                            // "Keep B anyway"
        DuplicateDetector.FindReraised(a).Should().BeEmpty();
        AnalysisView.IsActive(a, A).Should().BeFalse();
        AnalysisView.IsActive(a, B).Should().BeTrue();
    }

    [Fact]
    public void Restore_A_answers_a_reraised_pair()
    {
        var a = new CaseAnalysis();
        AT.Import(a, new[] { Finding("F-1", stage: "key_event"), Finding("F-2", stage: "key_event") });
        var A = SpItem(a, "F-1"); var B = SpItem(a, "F-2");
        DuplicateDetector.Resolve(a, DuplicateResolution.KeepB, A.Id, B.Id, Now);
        AT.Import(a, new[] { Finding("F-1", stage: "key_event") });
        AnalysisEdits.RestoreHiddenBy(a, B, Now).Should().Be(1);
        A.DuplicateOf.Should().BeNull();
        DuplicateDetector.FindReraised(a).Should().BeEmpty();
        AnalysisView.IsActive(a, A).Should().BeTrue();
    }

    // ------------------------------------------------------------------ migration


    [Fact]
    public void Migrated_analyst_finding_is_not_imported_a_second_time()
    {
        var a = new CaseAnalysis();
        var mine = Finding("F-A1", title: "My finding", origin: "analyst");
        AT.Import(a, new[] { mine });
        a.Meta.EngineMigrated = false;           // even if the flag were lost
        AT.Import(a, new[] { mine });
        AT.Import(a, new[] { mine });
        a.Items.Should().ContainSingle();
        a.Workspace.Findings.Should().ContainSingle();
        AnalysisSync.Sync(a, Opt());
        a.Items.Should().ContainSingle();

        // and a removed migrated item is not brought back
        AnalysisEdits.Remove(a, a.Items[0], now: Now);
        a.Meta.EngineMigrated = false;
        AT.Import(a, new[] { mine });
        a.Items.Should().ContainSingle().Which.Dismissed.Should().BeTrue();
    }

    [Fact]
    public void Migrated_analyst_item_is_not_marked_gone_by_a_later_import()
    {
        var a = new CaseAnalysis();
        AT.Import(a, new[] { Finding("F-A1", origin: "analyst") });
        AT.Import(a, Array.Empty<Core.Spectre.SpectreFinding>());
        a.Items.Single().SourceStatus.Should().Be(SourceStatus.Current);
    }

    private static (CaseAnalysis a, Core.Spectre.SpectreFinding[] f, Core.Spectre.SpectreTimeline tl) Moment()
    {
        var a = new CaseAnalysis();
        var f = new[] { Finding("F-1", evidence: new[] { ("PC1/a.csv", 1L) }), Finding("F-2", evidence: new[] { ("PC1/a.csv", 2L) }) };
        var tl = AT.TL(new[] { "F-1" }, "PC1", "F-1", "F-2");
        return (a, f, tl);
    }

    [Fact]
    public void Import_MomentBecomesKeyEvent_WithMappedFindingIds()
    {
        var (a, f, tl) = Moment();
        AT.Import(a, f, tl);
        var k = a.KeyEvents.Should().ContainSingle().Subject;
        k.Source.SpectreMomentId.Should().Be("M0");
        k.FindingIds.Should().Equal(SpItem(a, "F-1").Id);
        a.Items.Should().HaveCount(2);   // every finding is an item, key event or not
        var before = Json(a);
        AT.Import(a, f, tl);
        Json(a).Should().Be(before);
    }

    [Fact]
    public void Import_AfterMerge_KeyEventStaysOnTheKeptFinding()
    {
        var (a, f, tl) = Moment();
        AT.Import(a, f, tl);
        var k = a.KeyEvents.Single();
        var loser = SpItem(a, "F-1");
        var kept = a.Items.First(i => i.Id != loser.Id);
        DuplicateDetector.Resolve(a, DuplicateResolution.Merge, kept.Id, loser.Id, Now);
        k.FindingIds.Should().Equal(kept.Id);
        AT.Import(a, f, tl);   // the re-import must not point back at the hidden finding
        k.FindingIds.Should().Equal(kept.Id);
        k.Title.Should().NotBeNullOrEmpty();
        AnalysisView.ActiveKeyEvents(a).Should().ContainSingle();
    }

    [Fact]
    public void Import_MomentGone_RemovedAtSource_Back_Current()
    {
        var (a, f, tl) = Moment();
        AT.Import(a, f, tl);
        AT.Import(a, f, AT.TL(Array.Empty<string>(), "PC1", "F-1", "F-2"));
        a.KeyEvents[0].SourceStatus.Should().Be(SourceStatus.RemovedAtSource);
        AT.Import(a, f, tl);
        a.KeyEvents[0].SourceStatus.Should().Be(SourceStatus.Current);
        a.KeyEvents.Should().ContainSingle();
    }

    [Fact]
    public void Import_EditedFindingIds_Untouched()
    {
        var (a, f, tl) = Moment();
        AT.Import(a, f, tl);
        var k = a.KeyEvents[0];
        KeyEventOps.AddFinding(a, k.Id, SpItem(a, "F-2").Id, Now);
        AT.Import(a, f, tl);
        k.FindingIds.Should().Equal(SpItem(a, "F-1").Id, SpItem(a, "F-2").Id);
    }

    [Fact]
    public void Import_FindingAlreadyInAnalystKeyEvent_GoesToSkipped()
    {
        var (a, f, tl) = Moment();
        AT.Import(a, f, AT.TL(Array.Empty<string>(), "PC1", "F-1", "F-2"));   // findings only
        var mine = KeyEventOps.Group(a, new[] { SpItem(a, "F-1").Id }, Now);
        AT.Import(a, f, tl);
        a.KeyEvents.Should().ContainSingle();                // no second pin: the moment has nothing left to take
        mine.FindingIds.Should().Equal(SpItem(a, "F-1").Id);
        // moment with two findings, one of them already grouped by the analyst
        var tl2 = Core.Spectre.SpectreTimeline.From(new System.Text.Json.Nodes.JsonObject
        {
            ["findings"] = new System.Text.Json.Nodes.JsonArray(),
            ["moments"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                { ["id"] = "M9", ["rank"] = 1, ["primary_finding_id"] = "F-1", ["headline"] = "h", ["finding_ids"] = new System.Text.Json.Nodes.JsonArray("F-1", "F-2") }),
        });
        AT.Import(a, f, tl2);
        var k = a.KeyEvents.Single(x => x.Source.SpectreMomentId == "M9");
        k.FindingIds.Should().Equal(SpItem(a, "F-2").Id);
        k.SkippedFindingIds.Should().Equal(SpItem(a, "F-1").Id);
        mine.FindingIds.Should().Equal(SpItem(a, "F-1").Id);
    }
}
