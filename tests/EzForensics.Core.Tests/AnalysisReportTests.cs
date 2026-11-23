using EzForensics.Core.Analysis;
using FluentAssertions;
using static EzForensics.Core.Tests.AT;

namespace EzForensics.Core.Tests;

public class AnalysisReportTests
{
    private static AnalysisItem Fi(CaseAnalysis a, string id, ReviewState? r, Action<AnalysisItem>? f = null)
    {
        var i = new AnalysisItem { Id = id, Review = r, Title = "T-" + id, SourceRows = { new AnalysisRow("a/x.csv", 3) } };
        f?.Invoke(i); a.Items.Add(i); return i;
    }

    [Fact]
    public void Report_BodyFromKeyEvents_AppendixConfirmedLoose_NoRejected()
    {
        var a = new CaseAnalysis();
        Fi(a, "ok", ReviewState.Confirmed, i => { i.Severity = "High"; i.Mitre = new() { "T1003" }; i.FirstSeen = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero); });
        Fi(a, "tr", null);
        Fi(a, "rej", ReviewState.Rejected);
        Fi(a, "dup", ReviewState.Confirmed, i => i.DuplicateOf = "ok");
        Fi(a, "loose", ReviewState.Confirmed, i => { i.Severity = "Low"; i.Origin = ItemOrigin.Spectre; });
        Fi(a, "looseTr", ReviewState.ToReview);
        var k = KeyEventOps.Group(a, new[] { "ok", "tr", "rej" }, Now);
        KeyEventEdits.SetNotes(k, "k-notes", Now);
        k.PromotedReason = "because LSASS";
        var r = AnalysisReport.Build(a, "Case", DateTimeOffset.UtcNow);
        r.Included.Should().Be(1);
        r.Markdown.Should().Contain("T-ok").And.Contain("T-tr").And.Contain("2026-01-02 03:04:05 UTC").And.Contain("k-notes").And.Contain("T1003").And.Contain("because LSASS");
        r.Markdown.Should().Contain("## Other confirmed findings").And.Contain("- Low: T-loose (Spectre)");
        r.Markdown.Should().NotContain("T-rej").And.NotContain("T-dup").And.NotContain("T-looseTr");
    }
}
