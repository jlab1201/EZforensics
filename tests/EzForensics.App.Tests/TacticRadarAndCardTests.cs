using System.Reflection;
using System.Windows;
using EzForensics.App.Controls;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The radar's geometry and the timeline's lost phase band and taller cards.</summary>
public sealed class TacticRadarAndCardTests
{
    [Fact]
    public void Radar_values_use_a_square_root_scale_of_the_largest_axis()
    {
        TacticRadar.Scale(0, 9).Should().Be(0);
        TacticRadar.Scale(9, 9).Should().Be(1);
        TacticRadar.Scale(1, 4).Should().BeApproximately(0.5, 1e-9);
        TacticRadar.Scale(4, 9).Should().BeApproximately(2.0 / 3, 1e-9);
        TacticRadar.Scale(5, 0).Should().Be(0);
        TacticRadar.Scale(12, 9).Should().Be(1);   // never past the outer ring
    }

    [Fact]
    public void Radar_axes_run_clockwise_from_the_top_in_equal_steps()
    {
        TacticRadar.AngleOf(0, 12).Should().BeApproximately(-Math.PI / 2, 1e-9);
        TacticRadar.AngleOf(3, 12).Should().BeApproximately(0, 1e-9);                // a quarter turn: to the right
        TacticRadar.AngleOf(6, 12).Should().BeApproximately(Math.PI / 2, 1e-9);       // down
        var top = TacticRadar.VertexAt(new Point(100, 100), 50, 0, 12, 9, 9);
        top.X.Should().BeApproximately(100, 1e-6);
        top.Y.Should().BeApproximately(50, 1e-6);
        var none = TacticRadar.VertexAt(new Point(100, 100), 50, 5, 12, 0, 9);
        none.Should().Be(new Point(100, 100));
    }

    [Fact]
    public void The_phase_band_under_the_axis_is_gone_from_the_timeline()
    {
        typeof(StoryTimeline).GetField("BandH", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public).Should().BeNull();
        var kinds = typeof(StoryTimeline).GetNestedType("HitKind", BindingFlags.NonPublic)!.GetEnumNames();
        kinds.Should().NotContain("Band").And.Contain(new[] { "Card", "Marker", "Cluster", "Break" });
    }

    private static StoryTimelineLayout Layout()
    {
        var tl = SpectreTimeline.From(System.Text.Json.Nodes.JsonNode.Parse(FindingsSnapshotTests.SampleTimeline())!.AsObject());
        return StoryTimelineLayout.Build(tl, new StoryTimelineOptions(Width: 1000, CardWidth: StoryTimeline.CardW, CardHeight: StoryTimeline.CardH, EdgePad: 64));
    }

    [Fact]
    public void Timeline_height_is_the_cards_the_axis_tick_row_and_nothing_for_a_band()
    {
        var lay = Layout();
        StoryTimeline.HeightFor(lay).Should().Be(StoryTimeline.TopH + lay.CalloutAreaHeight + StoryTimeline.ConnectorGap + StoryTimeline.TickRowH + StoryTimeline.Bottom + 4);
    }

    [Fact]
    public void Cards_are_tall_enough_for_four_lines_and_never_overlap()
    {
        StoryTimeline.CardH.Should().BeGreaterThanOrEqualTo(88, "title (two lines), time, tactic and severity");
        var lay = Layout();
        lay.Callouts.Should().NotBeEmpty();
        lay.Callouts.Should().OnlyContain(c => c.Rect.Height == StoryTimeline.CardH);
        for (int i = 0; i < lay.Callouts.Count; i++)
            for (int j = i + 1; j < lay.Callouts.Count; j++)
            {
                var a = lay.Callouts[i].Rect; var b = lay.Callouts[j].Rect;
                bool overlap = a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
                overlap.Should().BeFalse($"cards {lay.Callouts[i].MomentId} and {lay.Callouts[j].MomentId} must not collide");
            }
    }
}
