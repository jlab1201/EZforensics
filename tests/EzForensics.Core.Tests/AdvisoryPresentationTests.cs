using EzForensics.Core.Advisories;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class AdvisoryPresentationTests
{
    [Fact]
    public void No_report_hides_banner() => AdvisoryPresentation.ShapeBanner(null).Show.Should().BeFalse();

    [Fact]
    public void Info_only_is_subtle_count()
    {
        var r = new AdvisoryReport { Advisories = new[] { new Advisory { Id = "a", Severity = AdvisorySeverity.Info, Title = "t", Message = "m" }, new Advisory { Id = "b", Severity = AdvisorySeverity.Info, Title = "t2", Message = "m" } } };
        var s = AdvisoryPresentation.ShapeBanner(r);
        s.Show.Should().BeTrue();
        s.IsSubtle.Should().BeTrue();
        s.Headline.Should().Be("2 notes about this artifact");
    }

    [Fact]
    public void Warning_banner_uses_top_title_and_markers()
    {
        var r = new AdvisoryReport
        {
            Copy = new CopyAssessment { Summary = "23 of 25 files copied" },
            Advisories = new[] { new Advisory { Id = "a", Severity = AdvisorySeverity.Warning, Title = "Copied", Message = "First. Second.", AffectedColumns = new[] { "SourceCreated" }, ReliableColumns = new[] { "LastRun" } } },
        };
        var s = AdvisoryPresentation.ShapeBanner(r);
        s.IsSubtle.Should().BeFalse();
        s.Headline.Should().Be("Copied");
        s.Summary.Should().Be("23 of 25 files copied");
        var m = AdvisoryPresentation.ColumnMarkers(r, new[] { "SourceCreated", "LastRun", "Other" });
        m.Should().HaveCount(2);
        m[0].Severity.Should().Be(AdvisorySeverity.Warning);
        m[0].Tip.Should().Contain("First.").And.NotContain("Second");
        m[1].Severity.Should().BeNull();
    }
}
