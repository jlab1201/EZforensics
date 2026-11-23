using EzForensics.Core.Analysis;
using FluentAssertions;
using static EzForensics.Core.Tests.AT;

namespace EzForensics.Core.Tests;

public class ReportHtmlRendererTests
{
    private static AnalysisItem Fi(CaseAnalysis a, string id, string title, DateTimeOffset? t, string sev = "High")
    {
        var i = new AnalysisItem { Id = id, Review = ReviewState.Confirmed, Title = title, Severity = sev, FirstSeen = t, Mitre = new() { "T1059.001" }, SourceRows = { new AnalysisRow("a/x.csv", 1) } };
        a.Items.Add(i); return i;
    }

    private static CaseAnalysis Sample(out string html)
    {
        var a = new CaseAnalysis();
        var late = new DateTimeOffset(2026, 3, 14, 15, 0, 0, TimeSpan.Zero);
        var early = new DateTimeOffset(2026, 3, 14, 9, 0, 0, TimeSpan.Zero);
        Fi(a, "late", "Late <script>alert(1)</script> event", late);
        Fi(a, "early", "Early event", early, "Low");
        Fi(a, "none", "Undated event", null, "Medium");
        // created out of order on purpose: late, none, early
        var k1 = KeyEventOps.Group(a, new[] { "late" }, Now);
        KeyEventOps.Group(a, new[] { "none" }, Now);
        KeyEventOps.Group(a, new[] { "early" }, Now);
        KeyEventEdits.SetNotes(k1, "Notes <script>alert(2)</script> and [click](https://evil.example/x) <img src=x onerror=alert(3)>", Now);
        k1.PromotedReason = "reason \"quoted\" <b>x</b>";
        html = ReportHtmlRenderer.Render(a, "Case <i>1</i>", DateTimeOffset.UtcNow);
        return a;
    }

    [Fact]
    public void Escapes_CaseText_InTitleNotesAndReason()
    {
        Sample(out var html);
        html.Should().NotContain("<script").And.NotContain("<img").And.NotContain("<i>1</i>").And.NotContain("<b>x</b>");
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;").And.Contain("&lt;script&gt;alert(2)&lt;/script&gt;");
        html.Should().Contain("<span class=\"pill high\">High</span>");
    }

    [Fact]
    public void NoExternalReferences()
    {
        Sample(out var html);
        html.Should().NotContain("http:").And.NotContain("https:").And.NotContain("href=");
        html.Should().Contain("click");   // link label kept as text
    }

    [Fact]
    public void Walkthrough_InTimeOrder_UndatedLast()
    {
        Sample(out var html);
        var w = html.IndexOf("Walk-through</h2>", StringComparison.Ordinal);
        w.Should().BeGreaterThan(0);
        var tail = html[w..];
        var iEarly = tail.IndexOf("1. Early event", StringComparison.Ordinal);
        var iLate = tail.IndexOf("2. Late", StringComparison.Ordinal);
        var iNone = tail.IndexOf("3. Undated event", StringComparison.Ordinal);
        iEarly.Should().BeGreaterThan(-1);
        iLate.Should().BeGreaterThan(iEarly);
        iNone.Should().BeGreaterThan(iLate);
        tail.Should().Contain("2026-03-14 09:00:00 UTC").And.Contain("T1059.001");
    }

    [Fact]
    public void Walkthrough_UsesGivenSteps_AndMarkdownPathEscapes()
    {
        var steps = new[] { new WalkthroughStep(null, "<b>One</b>", "plain <script>x</script>", "T1", "Critical"), new WalkthroughStep(null, "Two", "", "", "weird") };
        var html = ReportHtmlRenderer.RenderMarkdown("# Hi <script>x</script>\n\n[a](javascript:alert(1))", "c", DateTimeOffset.UtcNow, steps);
        html.Should().NotContain("<script").And.NotContain("<b>One").And.NotContain("javascript:");
        html.Should().Contain("&lt;b&gt;One&lt;/b&gt;").And.Contain("pill critical").And.Contain("pill info");
        html.IndexOf("1. ", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("2. Two", StringComparison.Ordinal));
    }
}
