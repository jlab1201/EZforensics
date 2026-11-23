using EzForensics.Core.Analysis;
using FluentAssertions;

namespace EzForensics.Core.Tests;

internal sealed class AnTempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ezanalysis-" + Guid.NewGuid().ToString("N")[..8]);
    public AnTempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class AnalysisStoreTests
{
    internal static AnalysisRow R(string csv, long row) => new(csv, row, "fp");

    [Fact]
    public void Round_trips_and_sections_stay_independent()
    {
        using var d = new AnTempDir();
        var s = CaseAnalysisStore.Open(d.Path, System.IO.Path.Combine(d.Path, "fb"));
        s.Data.AddFinding("Dump", "High", new[] { "T1003" }, "n", new[] { R("A/x.csv", 1), R("B/y.csv", 2) });
        s.Data.Items.Add(new AnalysisItem
        {
            Id = "I-1", Origin = ItemOrigin.Spectre, Title = "t", SourceRows = { R("A/x.csv", 5) },
            Source = { SpectreFindingId = "F-1" }, SourceStatus = SourceStatus.RemovedAtSource, Review = ReviewState.Rejected,
            DupDecisions = { new DupDecision { OtherId = "I-2", RowSetHash = "h" } }, EditedFields = { "title" },
        });
        s.Data.Items[0].Log("created", DateTimeOffset.UnixEpoch);
        s.Save();

        File.Exists(System.IO.Path.Combine(d.Path, ".ezforensics", "analysis.json")).Should().BeTrue();
        File.ReadAllText(s.Path).Should().Contain("\"removed_at_source\"").And.Contain("\"rejected\"").And.Contain("\"findings\"");

        var s2 = CaseAnalysisStore.Open(d.Path, System.IO.Path.Combine(d.Path, "fb"));
        s2.Data.Workspace.Findings.Should().HaveCount(1);
        s2.Data.Workspace.Findings[0].Rows.Select(r => r.Key).Should().Equal(new RowKey("a/x.csv", 1), new RowKey("b/y.csv", 2));
        var i = s2.Data.Items.Single();
        i.Source.SpectreFindingId.Should().Be("F-1");
        i.SourceStatus.Should().Be(SourceStatus.RemovedAtSource);
        i.Review.Should().Be(ReviewState.Rejected);
        i.DupDecisions.Single().RowSetHash.Should().Be("h");
        i.History.Single().Who.Should().NotBeNullOrEmpty();

        // the two sections do not leak into each other
        s2.Data.Workspace.Findings.Clear();
        s2.Data.Items.Should().HaveCount(1);
        s2.Data.Items.Clear();
        s2.Data.Workspace.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temp_file()
    {
        using var d = new AnTempDir();
        var s = CaseAnalysisStore.Open(d.Path, System.IO.Path.Combine(d.Path, "fb"));
        s.Save(); s.Save();
        Directory.GetFiles(System.IO.Path.Combine(d.Path, ".ezforensics")).Select(System.IO.Path.GetFileName).Should().Equal("analysis.json");
    }

    [Fact]
    public void Newer_version_is_read_only_and_corrupt_file_is_kept()
    {
        using var d = new AnTempDir();
        var path = CaseAnalysisStore.PathFor(d.Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"version\":99,\"items\":[]}");
        var s = CaseAnalysisStore.Open(d.Path, System.IO.Path.Combine(d.Path, "fb"));
        s.ReadOnlyReason.Should().NotBeNull();
        s.Invoking(x => x.Save()).Should().Throw<InvalidOperationException>();

        File.WriteAllText(path, "not json");
        var s2 = CaseAnalysisStore.Open(d.Path, System.IO.Path.Combine(d.Path, "fb"));
        s2.LoadWarning.Should().NotBeNull();
        s2.Data.Items.Should().BeEmpty();
        File.Exists(path + ".corrupt").Should().BeTrue();
    }

    [Fact]
    public void Fallback_path_is_per_folder_hash()
    {
        var a = CaseAnalysisStore.FallbackPathFor(@"C:\cases\one", @"C:\data\analysis");
        var b = CaseAnalysisStore.FallbackPathFor(@"C:\cases\two", @"C:\data\analysis");
        a.Should().NotBe(b).And.EndWith("analysis.json");
        CaseAnalysisStore.FallbackPathFor(@"c:\cases\ONE\", @"C:\data\analysis").Should().Be(a);
    }

    [Fact]
    public void Effective_rows_include_merged_items_minus_removed()
    {
        var a = new CaseAnalysis();
        var main = new AnalysisItem { Id = "A", SourceRows = { R("x.csv", 1), R("x.csv", 2) } };
        var loser = new AnalysisItem { Id = "B", MergedInto = "A", SourceRows = { R("x.csv", 2), R("y.csv", 3) } };
        a.Items.AddRange(new[] { main, loser });
        a.EffectiveRows(main).Select(r => r.Key.ToString()).Should().Equal("x.csv#1", "x.csv#2", "y.csv#3");
        AnalysisEdits.AddRow(main, R("z.csv", 9));
        AnalysisEdits.RemoveRow(main, R("X.CSV", 1));
        a.EffectiveRows(main).Select(r => r.Key.ToString()).Should().Equal("x.csv#2", "z.csv#9", "y.csv#3");
    }

    [Fact]
    public void Identity_defaults_to_user_and_is_injectable()
    {
        var old = CurrentIdentity.Provider;
        try
        {
            CurrentIdentity.Name.Should().Be(Environment.UserName);
            CurrentIdentity.Provider = () => " Examiner Jo ";
            var i = new AnalysisItem();
            i.Log("x", DateTimeOffset.UnixEpoch);
            i.History[0].Who.Should().Be("Examiner Jo");
            CurrentIdentity.Provider = () => "";
            CurrentIdentity.Name.Should().Be(Environment.UserName);
        }
        finally { CurrentIdentity.Provider = old; }
    }

    [Fact]
    public void Removing_the_last_row_deletes_the_workspace_key_event()
    {
        var a = new CaseAnalysis();
        var e = a.AddFinding("t", "Low", null, null, new[] { R("x.csv", 1), R("x.csv", 2) });
        a.RemoveRowsFromFinding(e.Id, new[] { new RowKey("x.csv", 1) }).Should().BeFalse();
        a.RemoveRowsFromFinding(e.Id, new[] { new RowKey("x.csv", 2) }).Should().BeTrue();
        a.Workspace.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Adding_rows_skips_duplicates_and_finds_key_events_by_row()
    {
        var a = new CaseAnalysis();
        var e = a.AddFinding("t", "Low", null, null, new[] { R("x.csv", 1) });
        a.AddRowsToFinding(e.Id, new[] { R("x.csv", 1), R("y.csv", 4) }).Should().Be(1);
        e.Rows.Should().HaveCount(2);
        a.FindingsWithRow(new RowKey("Y.csv", 4)).Should().ContainSingle().Which.Id.Should().Be(e.Id);
        a.FindingsWithRow(new RowKey("y.csv", 5)).Should().BeEmpty();
    }
}
