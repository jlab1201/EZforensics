using EzForensics.Core.Plaso;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>The one completion message of an Ingest run's per-host super timelines.</summary>
public class SupertimelineSummaryTests
{
    [Fact]
    public void Several_built_timelines_are_summed_up_per_host()
    {
        var (text, ok) = SupertimelineSummary.Describe(new[]
        {
            new SupertimelineOutcome("WS01", SupertimelineState.Built, 12345, @"C:\case\WS01\Timeline\SuperTimeline_plaso_A.csv"),
            new SupertimelineOutcome("DC01", SupertimelineState.Built, 678),
        });
        ok.Should().BeTrue();
        text.Should().StartWith("Super timelines built for 2 hosts (WS01 12,345 rows; DC01 678 rows).").And.EndWith("Open the Analyze page to review the results.");
    }

    [Fact]
    public void One_built_timeline_names_its_file_and_failures_are_named()
    {
        var (text, ok) = SupertimelineSummary.Describe(new[]
        {
            new SupertimelineOutcome("WS01", SupertimelineState.Built, 5, @"C:\case\WS01\Timeline\SuperTimeline_plaso_A.csv"),
            new SupertimelineOutcome("DC01", SupertimelineState.Failed, Error: "psort: exit 1"),
            new SupertimelineOutcome("unattributed evidence", SupertimelineState.Cancelled),
        });
        ok.Should().BeFalse();
        text.Should().Contain("Super timeline built for WS01: 5 rows (SuperTimeline_plaso_A.csv).")
            .And.Contain("The plaso super timeline of DC01 failed: psort: exit 1")
            .And.Contain("The super timeline of unattributed evidence was cancelled.")
            .And.Contain("Click its Result");
    }

    [Fact]
    public void Nothing_built_has_no_analyze_hint()
    {
        var (text, ok) = SupertimelineSummary.Describe(new[] { new SupertimelineOutcome("WS01", SupertimelineState.NotStarted, Error: "Not started: plaso is not installed.") });
        ok.Should().BeFalse();
        text.Should().Be("The super timeline of WS01 was not built. Not started: plaso is not installed.");
    }
}
