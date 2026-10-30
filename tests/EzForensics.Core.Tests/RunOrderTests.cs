using System.Collections.ObjectModel;
using EzForensics.Core.Common;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class RunOrderTests
{
    private sealed record Item(string Name, RunPhase Phase, long Seq, long? Finished = null);

    private static List<Item> Sort(IEnumerable<Item> items) => RunOrder.Sort(items, i => i.Phase, i => i.Seq, i => i.Finished);

    [Fact]
    public void Running_then_queued_then_finished_then_inactive()
    {
        var items = new[]
        {
            new Item("inactive", RunPhase.Inactive, 0),
            new Item("done", RunPhase.Finished, 1, 10),
            new Item("queued", RunPhase.Queued, 2),
            new Item("running", RunPhase.Running, 3),
        };
        Sort(items).Select(i => i.Name).Should().Equal("running", "queued", "done", "inactive");
    }

    [Fact]
    public void Queued_and_inactive_follow_the_sequence()
    {
        var items = new[]
        {
            new Item("q3", RunPhase.Queued, 3), new Item("off2", RunPhase.Inactive, 2), new Item("q1", RunPhase.Queued, 1), new Item("off0", RunPhase.Inactive, 0),
        };
        Sort(items).Select(i => i.Name).Should().Equal("q1", "q3", "off0", "off2");
    }

    [Fact]
    public void Finished_items_are_in_finish_order_newest_last()
    {
        var items = new[]
        {
            new Item("first-in-plan-finished-last", RunPhase.Finished, 0, 300),
            new Item("finished-first", RunPhase.Finished, 1, 100),
            new Item("finished-second", RunPhase.Finished, 2, 200),
        };
        Sort(items).Select(i => i.Name).Should().Equal("finished-first", "finished-second", "first-in-plan-finished-last");
    }

    [Fact]
    public void Before_a_run_selected_rows_come_first_in_sequence()
    {
        var items = new[]
        {
            new Item("a", RunPhase.Queued, 0), new Item("b-off", RunPhase.Inactive, 1), new Item("c", RunPhase.Queued, 2), new Item("d-off", RunPhase.Inactive, 3),
        };
        Sort(items).Select(i => i.Name).Should().Equal("a", "c", "b-off", "d-off");
    }

    [Fact]
    public void Equal_keys_keep_their_current_order()
    {
        var items = new[] { new Item("x", RunPhase.Queued, 1), new Item("y", RunPhase.Queued, 1) };
        Sort(items).Select(i => i.Name).Should().Equal("x", "y");
    }

    [Fact]
    public void Apply_moves_items_without_rebuilding_the_collection()
    {
        var a = new Item("a", RunPhase.Finished, 0, 5);
        var b = new Item("b", RunPhase.Running, 1);
        var c = new Item("c", RunPhase.Queued, 2);
        var list = new ObservableCollection<Item> { a, b, c };
        int resets = 0, moves = 0;
        list.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move) moves++;
            else resets++;
        };
        RunOrder.Reorder(list, i => i.Phase, i => i.Seq, i => i.Finished);
        list.Should().Equal(b, c, a);
        resets.Should().Be(0, "only Move is used, so the rows keep their state");
        moves.Should().BeGreaterThan(0);
        RunOrder.Reorder(list, i => i.Phase, i => i.Seq, i => i.Finished).Should().Be(0, "already in order");
    }

    [Fact]
    public void Apply_rejects_a_different_item_set()
    {
        var list = new ObservableCollection<Item> { new("a", RunPhase.Queued, 0) };
        var act = () => RunOrder.Apply(list, new[] { new Item("z", RunPhase.Queued, 0) });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stamps_strictly_increase()
    {
        var a = RunOrder.NextStamp();
        var b = RunOrder.NextStamp();
        b.Should().BeGreaterThan(a);
    }
}
