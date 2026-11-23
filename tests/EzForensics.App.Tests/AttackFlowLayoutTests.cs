using EzForensics.App.Controls;
using FluentAssertions;

namespace EzForensics.App.Tests;

public sealed class AttackFlowLayoutTests
{
    private static FlowNode N(string id, int num, int minute, string tactic, string host = "PC1") =>
        new(id, num, "t" + id, new DateTimeOffset(2026, 1, 1, 10, minute, 0, TimeSpan.Zero), tactic, "High", "", host);

    [Fact]
    public void Columns_follow_kill_chain_order_and_only_present_tactics()
    {
        var lay = AttackFlowGraph.Build(new[] { N("a", 1, 1, "Exfiltration"), N("b", 2, 2, "Initial Access"), N("c", 3, 3, "Execution") }, false);
        lay.Columns.Should().Equal("Initial Access", "Execution", "Exfiltration");
        lay.Items.Single(i => i.Node.Id == "b").Column.Should().Be(0);
        lay.Items.Single(i => i.Node.Id == "a").Column.Should().Be(2);
    }

    [Fact]
    public void Rows_are_time_order_and_edges_are_n_minus_one()
    {
        var lay = AttackFlowGraph.Build(new[] { N("late", 3, 30, "Impact"), N("early", 1, 1, "Execution"), N("mid", 2, 10, "Execution") }, false);
        lay.Items.Select(i => i.Node.Id).Should().Equal("early", "mid", "late");
        lay.Items.Select(i => i.Row).Should().Equal(0, 1, 2);
        lay.Items[1].Bounds.Y.Should().BeGreaterThan(lay.Items[0].Bounds.Y);
        lay.Edges.Should().Equal((0, 1), (1, 2));
    }

    [Fact]
    public void Empty_and_single_have_no_edges()
    {
        AttackFlowGraph.Build(Array.Empty<FlowNode>(), false).Edges.Should().BeEmpty();
        AttackFlowGraph.Build(new[] { N("a", 1, 1, "Execution") }, false).Edges.Should().BeEmpty();
    }

    [Fact]
    public void Cards_are_taller_with_the_computer_shown_and_unknown_tactics_go_last()
    {
        var plain = AttackFlowGraph.Build(new[] { N("a", 1, 1, "Execution") }, false);
        var host = AttackFlowGraph.Build(new[] { N("a", 1, 1, "Execution") }, true);
        host.Items[0].Bounds.Height.Should().BeGreaterThan(plain.Items[0].Bounds.Height);
        AttackFlowGraph.Build(new[] { N("a", 1, 1, "weird"), N("b", 2, 2, "Impact") }, false).Columns.Should().Equal("Impact", "Unmapped");
    }

    [Fact]
    public void Time_text_is_utc_labelled() =>
        AttackFlowGraph.TimeText(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2))).Should().Be("2026-03-04 03:06:07 UTC");
}
