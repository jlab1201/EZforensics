using EzForensics.Core.Analysis;
using EzForensics.Core.Results;
using FluentAssertions;
using static EzForensics.Core.Tests.AT;

namespace EzForensics.Core.Tests;

public class AnalysisSyncTests
{
    private static (CaseAnalysis a, WorkspaceFinding w) Setup()
    {
        var a = new CaseAnalysis();
        var w = a.AddFinding("Credential dump", "High", new[] { "T1003" }, "lsass", Rows("A/x.csv", 1, 2).Concat(Rows("B/y.csv", 7)), Now);
        return (a, w);
    }


    [Fact]
    public void Sync_twice_is_idempotent()
    {
        var (a, _) = Setup();
        var flags = new[] { Flags("A/x.csv", "suspicious", 3, 4), Flags("C/z.csv", "malicious", 0) };
        AnalysisSync.Sync(a, Opt());
        var before = Json(a);
        var res = AnalysisSync.Sync(a, Opt());
        res.AnyChange.Should().BeFalse();
        res.ChangedItemIds.Should().BeEmpty();
        Json(a).Should().Be(before);
    }

    [Fact]
    public void Report_edits_win_field_by_field_and_untouched_fields_follow_Analyze()
    {
        var (a, w) = Setup();
        AnalysisSync.Sync(a, Opt());
        var ke = ItemOfWorkspace(a, w.Id);
        AnalysisEdits.SetTitle(ke, "My title", Now);
        AnalysisEdits.RemoveRow(ke, R("A/x.csv", 2), Now);

        w.Title = "Analyze title"; w.Severity = "Critical"; w.Notes = "new notes";
        w.Rows.Add(R("A/x.csv", 50));
        AnalysisSync.Sync(a, Opt());

        ke.Title.Should().Be("My title");
        ke.Severity.Should().Be("Critical");
        ke.Notes.Should().Be("new notes");
        a.EffectiveRows(ke).Select(r => r.Row).Should().BeEquivalentTo(new long[] { 1, 7, 50 });   // 2 stays removed, 50 is new
        ke.SourceRows.Should().HaveCount(4);
    }

    [Fact]
    public void Removed_workspace_key_event_marks_the_item_and_never_deletes_it_and_it_can_come_back()
    {
        var (a, w) = Setup();
        AnalysisSync.Sync(a, Opt());
        a.RemoveFinding(w.Id);
        var res = AnalysisSync.Sync(a, Opt());
        var ke = ItemOfWorkspace(a, w.Id);
        ke.SourceStatus.Should().Be(SourceStatus.RemovedAtSource);
        res.RemovedAtSource.Should().Be(1);
        a.Items.Should().ContainSingle();

        a.Workspace.Findings.Add(w);
        var back = AnalysisSync.Sync(a, Opt());
        ke.SourceStatus.Should().Be(SourceStatus.Current);
        back.Restored.Should().Be(1);
    }








    [Fact]
    public void Sync_reports_duplicate_pairs_for_changed_items()
    {
        var (a, w) = Setup();
        a.AddFinding("Same stuff", "High", null, null, Rows("A/x.csv", 1, 2), Now);
        var res = AnalysisSync.Sync(a, Opt());
        res.Duplicates.Should().ContainSingle().Which.Match.Should().Be(DuplicateMatch.SameEvidence);
        AnalysisSync.Sync(a, Opt()).Duplicates.Should().BeEmpty();   // nothing changed, nothing new to check
    }

    private sealed class Reader : IAnalysisRowReader
    {
        public AnalysisRowInfo? Read(AnalysisRow r) => new(new DateTimeOffset(2026, 11, 1, 10, (int)r.Row, 0, TimeSpan.Zero), r.CsvRelPath.StartsWith("A") ? "PC-A" : null);
    }

    [Fact]
    public void Host_and_times_of_analyst_items_come_from_the_rows()
    {
        var (a, w) = Setup();
        AnalysisSync.Sync(a, Opt(new Reader()));
        var ke = ItemOfWorkspace(a, w.Id);
        ke.Host.Should().Be("PC-A");
        ke.FirstSeen.Should().Be(new DateTimeOffset(2026, 11, 1, 10, 1, 0, TimeSpan.Zero));
        ke.LastSeen.Should().Be(new DateTimeOffset(2026, 11, 1, 10, 7, 0, TimeSpan.Zero));

        AnalysisSync.Sync(a, Opt(new Reader(), p => p.StartsWith("B") ? "CASE-HOST-B" : null));
        ke.Host.Should().Be("PC-A");   // two rows from A (reader host) vs one from B (case host)
    }

    [Fact]
    public void Preview_counts_changes_without_touching_the_data()
    {
        var (a, _) = Setup();
        var before = Json(a);
        var res = AnalysisSync.Preview(a, Opt());
        res.Created.Should().Be(1);
        Json(a).Should().Be(before);
    }

}
