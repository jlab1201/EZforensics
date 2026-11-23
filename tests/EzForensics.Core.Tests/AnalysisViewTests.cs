using EzForensics.Core.Analysis;
using FluentAssertions;
using static EzForensics.Core.Tests.AT;

namespace EzForensics.Core.Tests;

public class AnalysisViewTests
{
    private static CaseAnalysis Sample(out Dictionary<string, AnalysisItem> m)
    {
        var a = new CaseAnalysis();
        var dict = new Dictionary<string, AnalysisItem>(); m = dict;
        AnalysisItem Add(string id, string k, ReviewState? r, Action<AnalysisItem>? f = null)
        {
            var i = new AnalysisItem { Id = id, Review = r, Title = id, SourceRows = Rows("x.csv", id.GetHashCode() & 0xff).ToList() };
            f?.Invoke(i); a.Items.Add(i); dict[id] = i; return i;
        }
        Add("ke-review", "ke", ReviewState.ToReview);
        Add("ke-null", "ke", null);                                 // reads as to_review
        Add("ke-ok", "ke", ReviewState.Confirmed);
        Add("ke-rej", "ke", ReviewState.Rejected);
        Add("ke-ni", "ke", ReviewState.Rejected);
        Add("ke-dup", "ke", ReviewState.Confirmed, i => i.DuplicateOf = "ke-ok");
        Add("ke-gone", "ke", ReviewState.Confirmed, i => i.SourceStatus = SourceStatus.RemovedAtSource);
        Add("ke-kept", "ke", ReviewState.Confirmed, i => { i.SourceStatus = SourceStatus.RemovedAtSource; i.KeptByUser = true; });
        Add("ke-dis", "ke", ReviewState.Confirmed, i => i.Dismissed = true);
        Add("fl", "fl", null);
        Add("fl-rejected-review", "fl", ReviewState.Rejected);          // review is ignored for flags
        Add("fl-gone", "fl", null, i => i.SourceStatus = SourceStatus.RemovedAtSource);
        return a;
    }

    [Fact]
    public void Active_follows_one_rule_for_every_kind()
    {
        var a = Sample(out var m);
        var active = a.Items.Where(i => AnalysisView.IsActive(a, i)).Select(i => i.Id);
        active.Should().BeEquivalentTo("ke-review", "ke-null", "ke-ok", "ke-rej", "ke-ni", "ke-kept", "fl", "fl-rejected-review");
    }

    [Fact]
    public void Active_key_events_exclude_rejected()
    {
        var a = Sample(out _);
        foreach (var i in a.Items.Where(i => i.Id.StartsWith("ke-")).ToList())
            a.KeyEvents.Add(new KeyEvent { Id = "K-" + i.Id, FindingIds = { i.Id } });   // one key event per ke- finding
        AnalysisView.ActiveKeyEvents(a).Select(k => k.FindingIds[0]).Should().BeEquivalentTo("ke-review", "ke-null", "ke-ok", "ke-kept");
    }

    [Fact]
    public void Heatmap_and_active_findings_selections()
    {
        var a = Sample(out _);
        AnalysisView.ActiveFindings(a).Should().HaveCount(8);
        AnalysisView.HeatmapItems(a).Select(i => i.Id).Should().BeEquivalentTo("ke-review", "ke-null", "ke-ok", "ke-kept", "fl");
    }

    [Fact]
    public void ReviewTileCounts_CountsFindings()
    {
        var a = Sample(out _);
        AnalysisView.ReviewTileCounts(a).Should().Be(new ReviewCounts(3, 2, 3));
    }

    [Fact]
    public void Details_list_shows_everything_but_tombstones_with_filters()
    {
        var a = Sample(out _);
        AnalysisView.DetailItems(a).Should().HaveCount(11);
        AnalysisView.DetailItems(a, new DetailFilter(Duplicate: true)).Select(i => i.Id).Should().Equal("ke-dup");
        AnalysisView.DetailItems(a, new DetailFilter(RemovedAtSource: true)).Select(i => i.Id).Should().BeEquivalentTo("ke-gone", "ke-kept", "fl-gone");
        AnalysisView.RemovedAtSourceItems(a).Select(i => i.Id).Should().BeEquivalentTo("ke-gone", "fl-gone");
    }

    [Fact]
    public void Empty_state_and_use_my_analysis_availability()
    {
        var a = new CaseAnalysis();
        AnalysisView.IsEmpty(a).Should().BeTrue();
        AnalysisView.CanUseMyAnalysis(a).Should().BeFalse();
        a.AddFinding("t", "Low", null, null, Rows("x.csv", 1));
        AnalysisView.CanUseMyAnalysis(a).Should().BeTrue();
        a.Items.Add(new AnalysisItem { Id = "i" });
        AnalysisView.IsEmpty(a).Should().BeFalse();
    }

    private static AnalysisItem F(CaseAnalysis a, string id, ReviewState? r = null, ItemOrigin o = ItemOrigin.Analyst, DateTimeOffset? first = null)
    {
        var i = new AnalysisItem { Id = id, Title = "T-" + id, Origin = o, Review = r, FirstSeen = first, SourceRows = Rows("x.csv", id.GetHashCode() & 0xffff).ToList() };
        a.Items.Add(i); return i;
    }

    [Fact]
    public void OriginOf_MergedYouAndSpectre_IsBoth()
    {
        var a = new CaseAnalysis();
        var you = F(a, "Y"); var sp = F(a, "S", o: ItemOrigin.Spectre);
        AnalysisView.OriginOf(a, you).Should().Be(DisplayOrigin.You);
        AnalysisView.OriginOf(a, sp).Should().Be(DisplayOrigin.Spectre);
        DuplicateDetector.Resolve(a, DuplicateResolution.Merge, "Y", "S", Now);
        AnalysisView.OriginOf(a, you).Should().Be(DisplayOrigin.Both);
        var k = KeyEventOps.Group(a, new[] { "Y" }, Now);
        AnalysisView.OriginOf(a, k).Should().Be(DisplayOrigin.Both);
    }

    [Fact]
    public void ActiveKeyEvents_AllFindingsRejected_Excluded_NotDismissed()
    {
        var a = new CaseAnalysis();
        var f1 = F(a, "A", first: Now); var f2 = F(a, "B", first: Now.AddHours(1));
        var k = KeyEventOps.Group(a, new[] { "A", "B" }, Now);
        AnalysisView.ActiveKeyEvents(a).Should().ContainSingle();
        AnalysisEdits.SetReview(f1, ReviewState.Rejected, Now);
        AnalysisView.ActiveKeyEvents(a).Should().ContainSingle();
        AnalysisEdits.SetReview(f2, ReviewState.Rejected, Now);
        AnalysisView.ActiveKeyEvents(a).Should().BeEmpty();
        AnalysisView.ReportKeyEvents(a).Should().BeEmpty();
        k.Dismissed.Should().BeFalse();
        AnalysisEdits.SetReview(f2, ReviewState.ToReview, Now);
        AnalysisView.ActiveKeyEvents(a).Should().ContainSingle();
    }

    [Fact]
    public void ActiveKeyEvents_SortedByTime_NoTimeLast_AndAppendixIsConfirmedLoose()
    {
        var a = new CaseAnalysis();
        F(a, "L", first: Now.AddHours(2)); F(a, "E", first: Now); F(a, "N");
        F(a, "C", ReviewState.Confirmed); F(a, "R", ReviewState.Rejected); F(a, "CK", ReviewState.Confirmed);
        KeyEventOps.Group(a, new[] { "N" }, Now); KeyEventOps.Group(a, new[] { "L" }, Now); KeyEventOps.Group(a, new[] { "E" }, Now);
        KeyEventOps.Group(a, new[] { "CK" }, Now);
        AnalysisView.ActiveKeyEvents(a).Select(k => k.FindingIds[0]).Should().Equal("E", "L", "CK", "N");
        AnalysisView.AppendixFindings(a).Select(i => i.Id).Should().Equal("C");
    }
}
