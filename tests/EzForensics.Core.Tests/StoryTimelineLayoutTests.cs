using System.Text.Json.Nodes;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class StoryTimelineLayoutTests
{
    private static SpectreTimeline Tl(params (string id, string? time, string? end, string? tactic)[] ms)
    {
        var moments = new JsonArray();
        int rank = 1;
        foreach (var m in ms)
        {
            var o = new JsonObject { ["id"] = m.id, ["rank"] = rank++, ["headline"] = "h " + m.id };
            if (m.time != null) o["time"] = m.time;
            if (m.end != null) o["end"] = m.end;
            if (m.tactic != null) { o["phase"] = PlainLanguage.Tactic(m.tactic); }
            moments.Add(o);
        }
        return SpectreTimeline.From(new JsonObject { ["moments"] = moments });
    }

    /// <summary>n moments evenly spaced from a to b (gaps of 1/(n-1) of the span: no breaks for n >= 8).</summary>
    private static SpectreTimeline Even(DateTime a, DateTime b, int n = 10) =>
        Tl(Enumerable.Range(0, n).Select(i => ("e" + i, (string?)(a + (b - a) * i / (n - 1)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), (string?)null, (string?)null)).ToArray());

    private static DateTime U(int y, int mo, int d, int h = 0) => new(y, mo, d, h, 0, 0, DateTimeKind.Utc);

    private static string D(int y, int mo, int d, int h = 0) => new DateTime(y, mo, d, h, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>A timeline with dated findings only (no key moments): the span fallback.</summary>
    private static SpectreTimeline Findings(params string[] starts) => SpectreTimeline.From(new JsonObject
    {
        ["findings"] = new JsonArray(starts.Select((s, i) => (JsonNode)new JsonObject { ["id"] = "f" + i, ["title"] = "t", ["start"] = s }).ToArray()),
    });

    /// <summary>Key moments plus extra dated findings (with optional ends) that are not moments.</summary>
    private static SpectreTimeline WithFindings(SpectreTimeline moments, params (string start, string? end)[] findings)
    {
        var o = new JsonObject
        {
            ["findings"] = new JsonArray(findings.Select((f, i) =>
            {
                var j = new JsonObject { ["id"] = "x" + i, ["title"] = "t", ["start"] = f.start };
                if (f.end != null) j["end"] = f.end;
                return (JsonNode)j;
            }).ToArray()),
            ["moments"] = new JsonArray(moments.Moments.Select(m =>
            {
                var j = new JsonObject { ["id"] = m.Id, ["rank"] = m.Number, ["headline"] = m.Headline };
                if (m.Time is { } t) j["time"] = t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                if (m.End is { } e) j["end"] = e.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                return (JsonNode)j;
            }).ToArray()),
        };
        return SpectreTimeline.From(o);
    }

    private static string S(int y, int mo, int d, int h, int mi, int s = 0) => new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    [Fact]
    public void Span_fallback_a_long_empty_gap_becomes_one_fixed_width_break_with_a_label()
    {
        var tl = Findings(D(2010, 1, 1), D(2010, 1, 2), D(2025, 1, 1), D(2025, 1, 2));
        var l = StoryTimelineLayout.Build(tl, new StoryTimelineOptions(Width: 1000, BreakWidth: 36));
        l.IsStoryScale.Should().BeFalse();
        l.Segments.Should().HaveCount(2);
        l.Breaks.Should().ContainSingle();
        l.Breaks[0].X1.Should().BeApproximately(l.Breaks[0].X0 + 36, 1e-6);
        l.Breaks[0].Label.Should().Be("15 yrs");
        l.Segments[0].Width.Should().BeApproximately(l.Segments[1].Width, 1.0); // both are one day long
        l.X(DateTimeOffset.Parse(D(2025, 1, 1))).Should().BeGreaterThan(l.Breaks[0].X1 - 1e-6);
        var inBreak = (l.Breaks[0].X0 + l.Breaks[0].X1) / 2 - 1;
        l.Time(inBreak).Should().BeOneOf(l.Segments[0].End, l.Segments[1].Start);
    }

    [Fact]
    public void Span_fallback_small_gaps_are_not_compressed()
    {
        var tl = Findings(Enumerable.Range(0, 10).Select(i => D(2026, 1, 1 + i)).ToArray());
        var l = StoryTimelineLayout.Build(tl);
        l.Breaks.Should().BeEmpty();
        l.Segments.Should().ContainSingle();
        l.X(DateTimeOffset.Parse(D(2026, 1, 1))).Should().BeApproximately(0, 1e-6);
        l.X(DateTimeOffset.Parse(D(2026, 1, 10))).Should().BeApproximately(1000, 1e-6);
    }

    [Fact]
    public void Moments_days_apart_stay_apart_even_when_findings_span_fifteen_years()
    {
        var ms = Tl(("a", D(2026, 1, 2, 8), null, null), ("b", D(2026, 1, 5, 8), null, null), ("c", D(2026, 1, 8, 8), null, null));
        var tl = WithFindings(ms, (D(2010, 1, 1), D(2026, 2, 1)), (D(2011, 6, 1), null), (D(2026, 1, 3), null), (D(2027, 1, 1), null));
        var o = new StoryTimelineOptions(Width: 1000);
        var l = StoryTimelineLayout.Build(tl, o);
        l.IsStoryScale.Should().BeTrue();
        l.Clusters.Should().HaveCount(3);
        var xs = l.Callouts.Select(c => c.AnchorX).ToList();
        (xs[1] - xs[0]).Should().BeGreaterThanOrEqualTo(o.MinGap);
        (xs[2] - xs[1]).Should().BeGreaterThanOrEqualTo(o.MinGap);
        l.EarlierCount.Should().Be(2);
        l.LaterCount.Should().Be(1);
        l.Breaks.Should().HaveCount(2).And.OnlyContain(b => b.Label == "3 days" && !b.Squiggle);
        l.Clusters[0].AnchorX.Should().BeApproximately(o.EdgePad, 1e-6);
        l.Clusters[^1].AnchorX.Should().BeApproximately(1000 - o.EdgePad, 1e-6);
    }

    [Fact]
    public void Moments_seconds_apart_share_one_point()
    {
        var tl = Tl(("a", S(2026, 1, 2, 8, 14, 0), null, null), ("b", S(2026, 1, 2, 8, 14, 10), null, null), ("c", D(2026, 1, 9), null, null));
        var l = StoryTimelineLayout.Build(tl);
        l.Clusters.Should().HaveCount(2);
        l.Clusters[0].MomentIds.Should().Equal("a", "b");
        l.Clusters[0].IsGroup.Should().BeTrue();
        l.Callouts[0].AnchorX.Should().Be(l.Callouts[1].AnchorX);
        l.Callouts[0].Rect.Overlaps(l.Callouts[1].Rect).Should().BeFalse();
    }

    [Fact]
    public void Points_too_close_in_pixels_merge_and_split_again_when_wider()
    {
        // many moments minutes apart plus one far away: at a narrow width some share a point, at 20x they do not
        var ms = Enumerable.Range(0, 30).Select(i => ("m" + i, (string?)S(2026, 1, 2, 8 + i / 20, 3 * (i % 20)),(string?)null, (string?)null))
            .Append(("far", D(2026, 6, 1), null, null)).ToArray();
        var narrow = StoryTimelineLayout.Build(Tl(ms), new StoryTimelineOptions(Width: 300));
        var wide = StoryTimelineLayout.Build(Tl(ms), new StoryTimelineOptions(Width: 6000));
        narrow.Clusters.Count.Should().BeLessThan(31);
        wide.Clusters.Should().HaveCount(31);
        for (int i = 1; i < narrow.Clusters.Count; i++)
            (narrow.Clusters[i].AnchorX - narrow.Clusters[i - 1].AnchorX).Should().BeGreaterThanOrEqualTo(10 - 1e-6);
    }

    [Fact]
    public void Story_scale_X_is_monotonic_and_Time_inverts_it_between_points()
    {
        var tl = Tl(("a", D(2020, 1, 1), D(2020, 1, 3), null), ("b", D(2020, 1, 4), null, null), ("c", D(2026, 5, 1), null, null), ("d", D(2026, 5, 6), null, null));
        var l = StoryTimelineLayout.Build(tl);
        var t0 = DateTimeOffset.Parse(D(2019, 6, 1));
        double prev = double.NegativeInfinity;
        for (int i = 0; i < 400; i++)
        {
            var x = l.X(t0.AddDays(i * 7));
            x.Should().BeGreaterThanOrEqualTo(prev);
            prev = x;
        }
        foreach (var t in new[] { D(2020, 1, 1), D(2020, 1, 2, 12), D(2020, 1, 4), D(2026, 5, 3), D(2026, 5, 6) })
        {
            var dto = DateTimeOffset.Parse(t);
            (l.Time(l.X(dto)) - dto).Duration().TotalSeconds.Should().BeLessThan(1);
        }
        l.Time(-50).Should().Be(DateTimeOffset.Parse(D(2020, 1, 1)));
        l.Time(99999).Should().Be(DateTimeOffset.Parse(D(2026, 5, 6)));
        l.Breaks.Should().Contain(b => b.Label == "6 yrs" && b.Squiggle);
        l.Segments.Should().HaveCount(2);
    }

    [Fact]
    public void Longer_gaps_get_more_room_but_not_proportionally()
    {
        var tl = Tl(("a", S(2026, 1, 2, 8, 0), null, null), ("b", S(2026, 1, 2, 8, 10), null, null), ("c", S(2026, 1, 2, 18, 10), null, null), ("d", D(2026, 3, 1), null, null));
        var l = StoryTimelineLayout.Build(tl);
        var x = l.Clusters.Select(c => c.AnchorX).ToArray();
        double g1 = x[1] - x[0], g2 = x[2] - x[1], g3 = x[3] - x[2];
        g2.Should().BeGreaterThan(g1);
        g3.Should().BeGreaterThan(g2);
        (g3 / g1).Should().BeLessThan(10);
    }

    [Fact]
    public void Story_ticks_one_per_point_with_time_of_day_on_the_same_day()
    {
        var tl = Tl(("a", S(2026, 1, 2, 8, 14), null, null), ("b", S(2026, 1, 2, 9, 2), null, null), ("c", S(2026, 3, 3, 22, 41), null, null));
        var l = StoryTimelineLayout.Build(tl, new StoryTimelineOptions(Width: 1400));
        l.Ticks.Select(t => t.Label).Should().Equal("2 Jan 2026 08:14", "09:02", "3 Mar 22:41");
        l.Ticks.Select(t => t.X).Should().Equal(l.Clusters.Select(c => c.AnchorX));
    }

    [Theory]
    [InlineData(400, "1 yr")]
    [InlineData(1095, "3 yrs")]
    [InlineData(95, "3 mo")]
    [InlineData(4, "4 days")]
    [InlineData(1, "1 day")]
    public void Break_labels(double days, string expected) => StoryTimelineLayout.GapLabel(TimeSpan.FromDays(days)).Should().Be(expected);

    [Fact]
    public void Break_labels_for_hours_and_minutes()
    {
        StoryTimelineLayout.GapLabel(TimeSpan.FromHours(6)).Should().Be("6 h");
        StoryTimelineLayout.GapLabel(TimeSpan.FromMinutes(20)).Should().Be("20 min");
    }

    [Fact]
    public void Dense_moments_never_overlap_and_alternate_rows()
    {
        var ms = Enumerable.Range(0, 8).Select(i => ("m" + i, (string?)D(2026, 3, 1, i), (string?)null, (string?)null)).ToArray();
        var l = StoryTimelineLayout.Build(Tl(ms), new StoryTimelineOptions(Width: 600, CardWidth: 180));
        l.Callouts.Should().HaveCount(8);
        for (int i = 0; i < l.Callouts.Count; i++)
            for (int j = i + 1; j < l.Callouts.Count; j++)
                l.Callouts[i].Rect.Overlaps(l.Callouts[j].Rect).Should().BeFalse($"cards {i} and {j}");
        l.Callouts.Select(c => c.Row).Distinct().Count().Should().BeGreaterThan(1);
        l.Callouts.Max(c => c.Row).Should().BeLessThan(3);
        l.Callouts.Should().OnlyContain(c => c.Rect.X >= 0 && c.Rect.Right <= l.TotalWidth + 1e-6);
        l.CalloutAreaHeight.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Range_moment_gets_a_bar_and_anchor_in_its_middle()
    {
        var l = StoryTimelineLayout.Build(Tl(("a", D(2026, 1, 1), D(2026, 1, 2), null), ("b", D(2026, 1, 10), null, null)));
        var a = l.Callouts.Single(c => c.MomentId == "a");
        a.IsRange.Should().BeTrue();
        a.AnchorX.Should().BeApproximately((a.BarX0 + a.BarX1) / 2, 1e-6);
        l.Callouts.Single(c => c.MomentId == "b").IsRange.Should().BeFalse();
    }

    [Fact]
    public void Single_moment_sits_in_the_middle_and_can_be_centred()
    {
        var l = StoryTimelineLayout.Build(Tl(("only", D(2026, 1, 1), null, "Execution")), new StoryTimelineOptions(Width: 800));
        l.Callouts.Should().ContainSingle();
        l.Callouts[0].AnchorX.Should().BeApproximately(400, 1e-6);
        l.Callouts[0].Phase.Should().Be("Execution");
        l.Phases.Should().ContainSingle().Which.Should().Match<PhaseBand>(b => b.X0 == 0 && Math.Abs(b.X1 - 800) < 1e-6);
        var w = l.CenterOn("only", 4);
        w.Width.Should().BeApproximately(200, 1e-6);
        w.Start.Should().BeApproximately(300, 1e-6);
    }

    [Fact]
    public void Equal_timestamps_do_not_divide_by_zero()
    {
        var l = StoryTimelineLayout.Build(Tl(("a", D(2026, 1, 1), null, null), ("b", D(2026, 1, 1), null, null)));
        l.Segments.Should().ContainSingle();
        l.Callouts.Should().HaveCount(2);
        l.Callouts[0].Rect.Overlaps(l.Callouts[1].Rect).Should().BeFalse();
        l.Callouts.Select(c => c.AnchorX).Should().OnlyContain(x => double.IsFinite(x));
        l.Ticks.Should().NotBeEmpty();
    }

    [Fact]
    public void All_undated_gives_an_empty_layout()
    {
        var l = StoryTimelineLayout.Build(Tl(("a", null, null, null), ("b", null, null, null)));
        l.IsEmpty.Should().BeTrue();
        l.Callouts.Should().BeEmpty();
        l.Phases.Should().BeEmpty();
        l.Ticks.Should().BeEmpty();
        l.CenterOn("a", 2).Should().Be(new ViewWindow(0, l.TotalWidth));
    }

    [Fact]
    public void Phase_bands_are_runs_of_the_same_phase_and_cover_the_axis()
    {
        var l = StoryTimelineLayout.Build(Tl(("a", D(2026, 1, 1), null, "Execution"), ("b", D(2026, 1, 2), null, "Execution"),
            ("c", D(2026, 1, 3), null, "Persistence"), ("d", D(2026, 1, 4), null, "Execution")));
        l.Phases.Select(p => p.Phase).Should().Equal("Execution", "Persistence", "Execution");
        l.Phases[0].X0.Should().Be(0);
        l.Phases[^1].X1.Should().BeApproximately(l.TotalWidth, 1e-6);
        for (int i = 1; i < l.Phases.Count; i++) l.Phases[i].X0.Should().BeApproximately(l.Phases[i - 1].X1, 1e-6);
    }

    [Fact]
    public void Center_on_is_clamped_to_the_axis()
    {
        var l = StoryTimelineLayout.Build(Even(U(2026, 1, 1), U(2026, 1, 20), 20), new StoryTimelineOptions(Width: 1000, CardWidth: 100));
        var w = l.CenterOn("e0", 5);
        w.Start.Should().Be(0);
        w.End.Should().BeApproximately(l.TotalWidth / 5, 1e-6);
        var z = l.CenterOn("e19", 5);
        z.End.Should().BeApproximately(l.TotalWidth, 1e-6);
        l.CenterOn("a", 0.2).Width.Should().BeApproximately(l.TotalWidth, 1e-6);
    }

    [Fact]
    public void Span_fallback_ticks_follow_the_segment_span()
    {
        static SpectreTimeline F(DateTime a, DateTime b) =>
            Findings(Enumerable.Range(0, 10).Select(i => (a + (b - a) * i / 9).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")).ToArray());
        StoryTimelineLayout.Build(F(U(2015, 1, 1), U(2026, 1, 1))).Ticks[0].Label.Should().Be("2015");
        StoryTimelineLayout.Build(F(U(2026, 1, 1), U(2026, 5, 1))).Ticks[0].Label.Should().Be("Jan 2026");
        StoryTimelineLayout.Build(F(U(2026, 1, 1), U(2026, 1, 10))).Ticks[0].Label.Should().Be("1 Jan");
        StoryTimelineLayout.Build(F(U(2026, 1, 1, 1), U(2026, 1, 1, 9))).Ticks[0].Label.Should().Be("01:00");
    }

    // ------------------------------------------------------------------ zoom: grouping and time buckets

    private static StoryTimelineOptions Zoomed(double zoom) => new(Width: 1000 * zoom, ClusterSeconds: StoryTimelineLayout.ClusterSecondsFor(zoom), EdgePad: 64);

    private static string Z(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>Moments in bursts: a pair a second apart, a few 40 s apart, then spread over minutes, hours.</summary>
    private static SpectreTimeline Bursts()
    {
        var b = new DateTime(2026, 1, 2, 22, 0, 0, DateTimeKind.Utc);
        var offs = new[] { 0, 1, 40, 41, 90, 400, 405, 900, 3600, 3640, 7200, 20000 };
        return Tl(offs.Select((o, i) => ("m" + i, (string?)Z(b.AddSeconds(o)), (string?)null, (string?)null)).ToArray());
    }

    [Fact]
    public void Clusters_only_split_as_zoom_grows()
    {
        var tl = Bursts();
        int prev = 0; List<HashSet<string>>? prevGroups = null;
        foreach (var z in new[] { 1.0, 1.5, 2, 4, 8, 20, 50, 200, 1000, 5000 })
        {
            var lay = StoryTimelineLayout.Build(tl, Zoomed(z));
            lay.Clusters.Count.Should().BeGreaterThanOrEqualTo(prev, $"zoom {z}");
            var groups = lay.Clusters.Select(c => c.MomentIds.ToHashSet()).ToList();
            if (prevGroups != null)   // every group is inside one of the previous zoom's groups: nothing re-merges
                groups.Should().OnlyContain(g => prevGroups.Any(pg => g.IsSubsetOf(pg)), $"zoom {z}");
            prev = lay.Clusters.Count; prevGroups = groups;
        }
        StoryTimelineLayout.Build(tl, Zoomed(1)).Clusters.Count.Should().BeLessThan(12);
        StoryTimelineLayout.Build(tl, Zoomed(5000)).Clusters.Count.Should().BeGreaterThan(StoryTimelineLayout.Build(tl, Zoomed(1)).Clusters.Count);
    }

    [Fact]
    public void Moments_at_the_same_instant_stay_together_at_any_zoom()
    {
        var tl = Tl(("a", "2026-01-02T22:00:00Z", null, null), ("b", "2026-01-02T22:00:00Z", null, null), ("c", "2026-01-02T22:00:30Z", null, null));
        StoryTimelineLayout.Build(tl, Zoomed(1)).Clusters.Should().ContainSingle();
        var hi = StoryTimelineLayout.Build(tl, Zoomed(5000));
        hi.Clusters.Should().HaveCount(2);
        hi.ClusterOf("a")!.MomentIds.Should().Contain("b");
    }

    [Fact]
    public void Cluster_window_shrinks_with_zoom()
    {
        StoryTimelineLayout.ClusterSecondsFor(1).Should().Be(120);
        StoryTimelineLayout.ClusterSecondsFor(4).Should().Be(30);
        StoryTimelineLayout.ClusterSecondsFor(0.2).Should().Be(120);
    }

    private static double MinorStepSeconds(StoryTimelineLayout lay, double target = 90)
    {
        var t = lay.MinorTicks(target, 0, lay.TotalWidth);
        t.Count.Should().BeGreaterThan(1);
        return t.Zip(t.Skip(1), (a, b) => (b.Time - a.Time).TotalSeconds).Where(d => d > 0).Min();
    }

    [Fact]
    public void Minor_tick_interval_gets_finer_with_zoom()
    {
        var tl = Even(U(2026, 1, 2), U(2026, 1, 2, 12), 4);   // 4h apart
        double prev = double.MaxValue;
        foreach (var z in new[] { 1.0, 4, 20, 100, 1000 })
        {
            var lay = StoryTimelineLayout.Build(tl, Zoomed(z));
            var step = MinorStepSeconds(lay);
            step.Should().BeLessThanOrEqualTo(prev, $"zoom {z}");
            prev = step;
        }
        prev.Should().BeLessThan(3600);
    }

    [Fact]
    public void Minor_ticks_sit_where_the_layout_maps_their_time_and_are_utc()
    {
        var tl = Tl(("a", "2026-01-02T22:00:00Z", null, null), ("b", "2026-01-02T22:40:00Z", null, null), ("c", "2026-01-03T01:30:00Z", null, null));
        var lay = StoryTimelineLayout.Build(tl, Zoomed(8));
        var ticks = lay.MinorTicks(90, 0, lay.TotalWidth);
        ticks.Should().NotBeEmpty();
        foreach (var t in ticks) { lay.X(t.Time).Should().BeApproximately(t.X, 0.01); t.Time.Offset.Should().Be(TimeSpan.Zero); }
        ticks.Should().Contain(t => t.Label.Contains(":"));
    }

    [Fact]
    public void Minor_labels_follow_the_interval_and_show_the_date_on_the_first_label()
    {
        var tl = Tl(("a", "2026-01-02T22:00:00Z", null, null), ("b", "2026-01-02T22:50:00Z", null, null));
        var lay = StoryTimelineLayout.Build(tl, Zoomed(1));
        var ticks = lay.MinorTicks(90, 0, lay.TotalWidth);
        ticks.Should().NotBeEmpty();
        ticks[0].Label.Should().StartWith("2 Jan ");
        ticks.Skip(1).Should().OnlyContain(t => System.Text.RegularExpressions.Regex.IsMatch(t.Label, @"^\d\d:\d\d$"));
        var secs = StoryTimelineLayout.Build(tl, Zoomed(2000)).MinorTicks(90, 0, 1e9);
        secs.Should().Contain(t => System.Text.RegularExpressions.Regex.IsMatch(t.Label, @"(^|\s)\d\d:\d\d:\d\d$"));
        var days = Tl(("a", "2026-01-02T22:00:00Z", null, null), ("b", "2026-01-07T22:00:00Z", null, null));
        StoryTimelineLayout.Build(days, Zoomed(1)).MinorTicks(90, 0, 1e9).Should().OnlyContain(t => System.Text.RegularExpressions.Regex.IsMatch(t.Label, @"^\d{1,2} [A-Z][a-z]{2}$"));
    }

    [Fact]
    public void Minor_labels_never_overlap_each_other_or_the_point_ticks_and_are_bounded()
    {
        foreach (var z in new[] { 1.0, 3, 30, 600, 5000 })
        {
            var lay = StoryTimelineLayout.Build(Bursts(), Zoomed(z));
            var minor = lay.MinorTicks(60, 0, lay.TotalWidth);
            minor.Count.Should().BeLessThanOrEqualTo(200);
            double W(StoryTick t) => t.Label.Length * 5.6 + 10;
            foreach (var (a, b) in minor.Zip(minor.Skip(1)))
                (b.X - W(b) / 2).Should().BeGreaterThanOrEqualTo(a.X + W(a) / 2 - 1e-6, $"zoom {z}");
            foreach (var m in minor)
                foreach (var t in lay.Ticks)
                    (Math.Abs(m.X - t.X) >= (W(m) + W(t)) / 2 - 1e-6).Should().BeTrue($"zoom {z}: {m.Label} vs {t.Label}");
        }
    }

    [Fact]
    public void Minor_ticks_skip_gaps_of_a_month_or_more()
    {
        var tl = Tl(("a", D(2020, 1, 1), null, null), ("b", D(2026, 1, 1), null, null));
        var lay = StoryTimelineLayout.Build(tl, Zoomed(1000));
        lay.MinorTicks(90, 0, lay.TotalWidth).Should().BeEmpty();
    }

    private static string[] AxisLabels(StoryTimelineLayout lay, double zoom)
    {
        double target = 90 / zoom;   // the control's tick spacing in axis units
        var major = lay.MajorTicks(target, 0, lay.TotalWidth);
        var minor = lay.MinorTicks(target, 0, lay.TotalWidth);
        return major.Concat(minor).OrderBy(t => t.X).Select(t => t.Label).ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(20)]
    public void The_axis_uses_one_time_format_and_no_label_twice_on_the_demo_span(double zoom)
    {
        // the demo case's key events: 09:30:12, 09:40:58 (+ the 09:40 and 09:42 neighbours), 09:43:20, 09:47:30
        var tl = Tl(("a", "2026-03-14T09:30:12Z", null, null), ("b", "2026-03-14T09:40:58Z", null, null), ("b2", "2026-03-14T09:40:20Z", null, null),
            ("c", "2026-03-14T09:43:20Z", null, null), ("d", "2026-03-14T09:47:30Z", null, null));
        var lay = StoryTimelineLayout.Build(tl, Zoomed(zoom));
        var labels = AxisLabels(lay, zoom);
        labels.Should().NotBeEmpty();
        labels.Should().OnlyHaveUniqueItems();
        var withSeconds = labels.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\d\d:\d\d:\d\d$"));
        (withSeconds == 0 || withSeconds == labels.Length).Should().BeTrue($"zoom {zoom}: {string.Join(" ", labels)}");
        lay.MinorTicks(90 / zoom, 0, lay.TotalWidth).Should().OnlyContain(t => t.Time.Millisecond == 0);
    }
}
