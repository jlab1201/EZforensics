using System.Text.Json.Nodes;
using EzForensics.App.Controls;
using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The timeline's zoom limit and the text of its hover cards.</summary>
public sealed class StoryTimelineWheelAndHoverTests
{
    private static JsonObject F(string id, string tactic, string sev) => new() { ["id"] = id, ["title"] = "t " + id, ["severity"] = sev, ["tactic"] = tactic };

    private static (SpectreMoment M, IReadOnlyDictionary<string, SpectreTimelineFinding> ById) One(string? time, params JsonObject[] findings)
    {
        var u = new JsonArray(); foreach (var f in findings) u.Add(f);
        var ids = new JsonArray(); foreach (var f in findings) ids.Add(f["id"]!.GetValue<string>());
        var m = new JsonObject { ["id"] = "m1", ["rank"] = 1, ["number"] = 1, ["headline"] = "Headline", ["plain_title"] = "Service installed", ["primary_finding_id"] = findings[0]["id"]!.GetValue<string>(), ["finding_ids"] = ids };
        if (time != null) m["time"] = time;
        var tl = SpectreTimeline.From(new JsonObject { ["undated"] = u, ["moments"] = new JsonArray(m) });
        return (tl.Moments[0], StoryGraphics.ById(tl.Undated));
    }

    [Fact]
    public void Max_zoom_scales_with_the_span_and_keeps_the_old_limit_for_short_stories()
    {
        StoryTimeline.MaxZoomFor(TimeSpan.FromMinutes(5)).Should().Be(20);
        StoryTimeline.MaxZoomFor(TimeSpan.FromHours(10)).Should().Be(600);
        StoryTimeline.MaxZoomFor(TimeSpan.FromDays(3)).Should().Be(4320);
        StoryTimeline.MaxZoomFor(TimeSpan.FromDays(3)).Should().BeGreaterThan(StoryTimeline.MaxZoomFor(TimeSpan.FromHours(10)));
        StoryTimeline.MaxZoomFor(TimeSpan.FromDays(400)).Should().Be(5000);   // capped
    }

    [Fact]
    public void Card_lines_are_title_utc_time_and_tactic_in_that_order()
    {
        var (m, by) = One("2026-01-02T08:16:05Z", F("a", "execution", "High"), F("b", "execution", "High"), F("c", "execution", "High"), F("d", "execution", "Low"));
        StoryTimeline.HoverLines(m, by).Should().Equal("Service installed", "2026-01-02 08:16:05 UTC", "Execution");
    }

    [Fact]
    public void Hover_lines_show_an_unknown_tactic_plainly()
    {
        var (m, by) = One("2026-01-02T08:16:05Z", F("a", "", "Medium"));
        StoryTimeline.HoverLines(m, by).Should().Equal("Service installed", "2026-01-02 08:16:05 UTC", PlainLanguage.OtherActivity);
        var (m2, by2) = One(null, F("a", "persistence", "Low"));
        StoryTimeline.HoverLines(m2, by2)[1].Should().Be("No time");
    }

    private static (SpectreMoment M, IReadOnlyDictionary<string, SpectreTimelineFinding> ById) Rich(bool extras)
    {
        var f = F("F-7", "execution", "High");
        if (extras) { f["technique"] = "T1059.001"; f["approval"] = "APPROVED"; f["host"] = "WS01"; }
        var f2 = F("F-8", "execution", "Low");
        var m = new JsonObject
        {
            ["id"] = "m1", ["rank"] = 1, ["headline"] = "Headline", ["plain_title"] = "Service installed", ["time"] = "2026-01-02T08:16:05Z",
            ["primary_finding_id"] = "F-7", ["finding_ids"] = new JsonArray("F-7", "F-8"),
        };
        if (extras) { m["why"] = "A new service was registered."; m["cant_tell"] = "Who asked for it."; m["evidence_line"] = "Windows system log, 2 events"; }
        var tl = SpectreTimeline.From(new JsonObject { ["undated"] = new JsonArray(f, f2), ["moments"] = new JsonArray(m) });
        return (tl.Moments[0], StoryGraphics.ById(tl.Undated));
    }

    [Fact]
    public void Detail_lines_list_only_what_the_card_does_not_show()
    {
        var (m, by) = Rich(extras: true);
        var lines = MomentDetailLines.Build(m, by);
        lines.Should().Equal(
            "A new service was registered.",
            "What we can't tell: Who asked for it.",
            "Technique: T1059.001 Command and Scripting Interpreter: PowerShell",
            "High severity · Confirmed",
            "Evidence: Windows system log, 2 events",
            "Computer: WS01",
            "Finding F-7 · 2 findings in this event");
        lines.Should().NotContain(l => l.Contains("Service installed") || l.Contains("UTC") || l.Contains("Execution"));
    }

    [Fact]
    public void Detail_lines_skip_empty_fields_and_the_page_review_state_wins()
    {
        var (m, by) = Rich(extras: false);
        var lines = MomentDetailLines.Build(m, by, approval: "REJECTED");
        lines.Should().Equal("High severity · Rejected", "Evidence: Other source", "Finding F-7 · 2 findings in this event");
        MomentDetailLines.Build(m, by)[0].Should().Be("High severity · Needs your review");
    }
}
