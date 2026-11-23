using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The hero band's short "first → last (UTC)" span.</summary>
public sealed class StoryHeroTests
{
    private static DateTimeOffset T(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Span_is_short_within_a_year_and_full_across_years()
    {
        StoryText.SpanParts(T("2026-01-02T08:14:00Z"), T("2026-03-05T03:30:00Z")).Should().Be(("2 Jan", "5 Mar 2026"));
        StoryText.SpanParts(T("2025-12-28T08:14:00Z"), T("2026-01-05T03:30:00Z")).Should().Be(("28 Dec 2025", "5 Jan 2026"));
    }

    [Fact]
    public void Span_on_one_day_shows_times_and_a_single_time_has_no_end()
    {
        StoryText.SpanParts(T("2026-01-02T08:14:00Z"), T("2026-01-02T11:30:00Z")).Should().Be(("2 Jan 2026, 08:14", "11:30"));
        StoryText.SpanParts(T("2026-01-02T08:14:00Z"), null).Should().Be(("2 Jan 2026, 08:14", ""));
        StoryText.SpanParts(null, null).Should().Be(("", ""));
    }

    [Fact]
    public void Journey_line_is_solid_only_between_lit_stages_and_the_most_severe_stage_pulses()
    {
        static StageStripItem S(string name, int findings, int rank) => new(new EzForensics.Core.Spectre.StageSummary(name, 0, findings, 0, findings > 0 ? rank : -1), Array.Empty<string>());
        var strip = new[] { S("A", 1, 2), S("B", 2, 3), S("C", 0, 0), new StageStripItem(null, new[] { "D" }), S("E", 1, 4) };
        var chips = StageChipViewModel.ForStrip(strip);
        chips[0].HasLeftLine.Should().BeFalse();
        chips[0].RightDashed.Should().BeFalse();       // A-B both lit
        chips[1].LeftDashed.Should().BeFalse();
        chips[1].RightDashed.Should().BeTrue();        // B-C: C has nothing
        chips[3].LeftDashed.Should().BeTrue();         // the gap
        chips[4].HasRightLine.Should().BeFalse();
        chips.Where(c => c.IsPeak).Should().ContainSingle().Which.Stage.Should().Be("E");
        chips.Select(c => c.DelayMs).Should().BeInAscendingOrder();
        chips[1].DelayMs.Should().BeGreaterThan(chips[1].LeftLineDelayMs);   // pops in as the line reaches it
    }

    [Fact]
    public void Span_is_in_utc_whatever_the_offset()
    {
        StoryText.SpanParts(T("2026-01-02T23:30:00-02:00"), T("2026-01-04T00:00:00Z")).Should().Be(("3 Jan", "4 Jan 2026"));
    }
}
