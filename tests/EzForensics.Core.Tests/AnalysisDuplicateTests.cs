using EzForensics.Core.Analysis;
using FluentAssertions;
using static EzForensics.Core.Tests.AT;

namespace EzForensics.Core.Tests;

public class AnalysisDuplicateTests
{
    [Fact]
    public void Same_evidence_equal_and_contained_sets_are_detected()
    {
        var a = new CaseAnalysis();
        KeyEvent(a, "A", "a", Rows("x.csv", 1, 2));
        KeyEvent(a, "B", "b", Rows("x.csv", 1, 2));
        KeyEvent(a, "C", "c", Rows("x.csv", 2));
        var p = DuplicateDetector.FindPairs(a);
        p.Should().HaveCount(3).And.OnlyContain(x => x.Match == DuplicateMatch.SameEvidence);
        p.Single(x => x.AId == "A" && x.BId == "B").Reason.Should().Be("Same rows");
    }

    [Fact]
    public void Possible_by_shared_rows_and_by_technique_host_within_15_minutes()
    {
        var a = new CaseAnalysis();
        KeyEvent(a, "A", "a", Rows("x.csv", 1, 2));
        KeyEvent(a, "B", "b", Rows("x.csv", 2, 3));
        var p = DuplicateDetector.FindPairs(a).Single();
        p.Match.Should().Be(DuplicateMatch.Possible); p.SharedRows.Should().Be(1);

        var t = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var b = new CaseAnalysis();
        KeyEvent(b, "A", "a", Rows("x.csv", 1), "PC1", t, new[] { "T1059" });
        KeyEvent(b, "B", "b", Rows("y.csv", 1), "pc1", t.AddMinutes(15), new[] { "T1059" });
        KeyEvent(b, "C", "c", Rows("z.csv", 1), "PC1", t.AddMinutes(16), new[] { "T1059" });
        KeyEvent(b, "D", "d", Rows("w.csv", 1), "PC2", t, new[] { "T1059" });
        KeyEvent(b, "E", "e", Rows("v.csv", 1), "PC1", t, new[] { "T1003" });
        var pairs = DuplicateDetector.FindPairs(b);
        pairs.Should().Contain(x => x.AId == "A" && x.BId == "B" && x.Match == DuplicateMatch.Possible);
        pairs.Should().Contain(x => x.AId == "B" && x.BId == "C");       // 1 minute apart
        pairs.Should().NotContain(x => x.AId == "A" && x.BId == "C");    // 16 minutes
        pairs.Should().NotContain(x => x.AId == "A" && x.BId == "D");    // other host
        pairs.Should().NotContain(x => x.AId == "A" && x.BId == "E");    // other technique
    }

    [Fact]
    public void Keep_A_and_Keep_B_hide_the_other_and_nothing_is_automatic()
    {
        var a = new CaseAnalysis();
        KeyEvent(a, "A", "a", Rows("x.csv", 1)); KeyEvent(a, "B", "b", Rows("x.csv", 1));
        DuplicateDetector.FindPairs(a).Should().ContainSingle();
        DuplicateDetector.Resolve(a, DuplicateResolution.KeepA, "A", "B", Now);
        a.Find("B")!.DuplicateOf.Should().Be("A");
        DuplicateDetector.FindPairs(a).Should().BeEmpty();
        AnalysisView.IsActive(a, a.Find("B")!).Should().BeFalse();

        a.Find("B")!.DuplicateOf = null;
        DuplicateDetector.Resolve(a, DuplicateResolution.KeepB, "A", "B", Now);
        a.Find("A")!.DuplicateOf.Should().Be("B");
    }

    [Fact]
    public void Keep_both_is_not_raised_again_until_a_row_set_changes()
    {
        var a = new CaseAnalysis();
        var A = KeyEvent(a, "A", "a", Rows("x.csv", 1, 2)); var B = KeyEvent(a, "B", "b", Rows("x.csv", 2, 3));
        DuplicateDetector.Resolve(a, DuplicateResolution.KeepBoth, "A", "B", Now);
        DuplicateDetector.FindPairs(a).Should().BeEmpty();
        A.DupDecisions.Single().OtherId.Should().Be("B");

        AnalysisSync.Sync(a, Opt());                   // sync and import leave decisions alone
        DuplicateDetector.FindPairs(a).Should().BeEmpty();

        AnalysisEdits.AddRow(B, R("x.csv", 9), Now);                           // a row set changes
        DuplicateDetector.FindPairs(a).Should().ContainSingle();
        DuplicateDetector.Resolve(a, DuplicateResolution.KeepBoth, "A", "B", Now);
        DuplicateDetector.FindPairs(a).Should().BeEmpty();
        A.DupDecisions.Should().ContainSingle();                               // replaced, not stacked
        AnalysisEdits.RemoveRow(A, R("x.csv", 1), Now);
        DuplicateDetector.FindPairs(a).Should().ContainSingle();
    }

    [Fact]
    public void Merge_folds_the_loser_into_A_and_is_not_raised_again()
    {
        var a = new CaseAnalysis();
        var A = KeyEvent(a, "A", "a", Rows("x.csv", 1, 2)); var B = KeyEvent(a, "B", "b", Rows("x.csv", 2, 3));
        DuplicateDetector.Resolve(a, DuplicateResolution.Merge, "A", "B", Now);
        B.MergedInto.Should().Be("A");
        a.EffectiveRows(A).Select(r => r.Row).Should().BeEquivalentTo(new long[] { 1, 2, 3 });
        DuplicateDetector.FindPairs(a).Should().BeEmpty();
        A.History.Should().Contain(h => h.What.Contains("merged B"));
    }

    [Fact]
    public void Removing_a_kept_item_can_restore_what_it_hid()
    {
        var a = new CaseAnalysis();
        var A = KeyEvent(a, "A", "a", Rows("x.csv", 1)); var B = KeyEvent(a, "B", "b", Rows("x.csv", 1)); var C = KeyEvent(a, "C", "c", Rows("y.csv", 1));
        DuplicateDetector.Resolve(a, DuplicateResolution.KeepA, "A", "B", Now);
        DuplicateDetector.Resolve(a, DuplicateResolution.Merge, "A", "C", Now);
        AnalysisEdits.HiddenBy(a, A).Should().HaveCount(2);
        AnalysisEdits.Remove(a, A, restoreHidden: true, now: Now);
        B.DuplicateOf.Should().BeNull(); C.MergedInto.Should().BeNull();
        AnalysisView.IsActive(a, B).Should().BeTrue();
    }

    [Fact]
    public void Hidden_removed_dismissed_and_stale_source_items_take_no_part_in_pairs()
    {
        var a = new CaseAnalysis();
        KeyEvent(a, "A", "a", Rows("x.csv", 1));
        var gone = KeyEvent(a, "B", "b", Rows("x.csv", 1)); gone.SourceStatus = SourceStatus.RemovedAtSource;
        var dis = KeyEvent(a, "C", "c", Rows("x.csv", 1)); dis.Dismissed = true;
        DuplicateDetector.FindPairs(a).Should().BeEmpty();
        gone.KeptByUser = true;
        DuplicateDetector.FindPairs(a).Should().ContainSingle();
    }

    private static AnalysisItem F(CaseAnalysis a, string id, params long[] rows) => KeyEvent(a, id, "T-" + id, Rows("x.csv", rows));

    [Fact]
    public void Resolve_Merge_RepointsKeyEventToWinner()
    {
        var a = new CaseAnalysis();
        F(a, "A", 1); F(a, "B", 1);
        var k = KeyEventOps.Group(a, new[] { "B" }, Now);
        DuplicateDetector.Resolve(a, DuplicateResolution.Merge, "A", "B", Now);
        k.FindingIds.Should().Equal("A");
        k.Dismissed.Should().BeFalse();
        AnalysisView.ActiveKeyEvents(a).Should().ContainSingle();
    }

    [Fact]
    public void Resolve_KeepA_WhenAInOtherKeyEvent_RemovesLoser()
    {
        var a = new CaseAnalysis();
        F(a, "A", 1); F(a, "B", 1); F(a, "C", 2);
        var ka = KeyEventOps.Group(a, new[] { "A" }, Now);
        var kb = KeyEventOps.Group(a, new[] { "B", "C" }, Now);
        DuplicateDetector.Resolve(a, DuplicateResolution.KeepA, "A", "B", Now);
        ka.FindingIds.Should().Equal("A");
        kb.FindingIds.Should().Equal("C");
        var a2 = new CaseAnalysis();
        F(a2, "A", 1); F(a2, "B", 1);
        var k1 = KeyEventOps.Group(a2, new[] { "A" }, Now); var k2 = KeyEventOps.Group(a2, new[] { "B" }, Now);
        DuplicateDetector.Resolve(a2, DuplicateResolution.KeepA, "A", "B", Now);
        k2.Dismissed.Should().BeTrue(); k1.FindingIds.Should().Equal("A");
    }
}
