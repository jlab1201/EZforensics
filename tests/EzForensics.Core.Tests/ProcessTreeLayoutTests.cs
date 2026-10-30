using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ProcessTreeLayoutTests
{
    private sealed class N
    {
        public N(string name, params N[] kids) { Name = name; Kids = kids; }
        public string Name { get; }
        public IReadOnlyList<N> Kids { get; }
        public override string ToString() => Name;
    }

    private static TreeLayoutResult<N> Lay(params N[] roots) =>
        ProcessTreeLayout.Compute(roots, n => n.Kids, nodeWidth: 100, nodeHeight: 20, horizontalGap: 50, verticalGap: 10);

    [Fact]
    public void ColumnsFollowDepth_LeavesStack_ParentCentred()
    {
        var a = new N("a"); var b = new N("b"); var c = new N("c");
        var p = new N("p", a, b, c);
        var r = Lay(p);
        r.Positions[p].X.Should().Be(0);
        r.Positions[a].X.Should().Be(150);
        r.Positions.Values.Select(v => v.Y).Distinct().Should().HaveCount(3);
        r.Positions[a].Y.Should().Be(0);
        r.Positions[b].Y.Should().Be(30);
        r.Positions[c].Y.Should().Be(60);
        r.Positions[p].Y.Should().Be(30);
        r.Edges.Should().HaveCount(3).And.OnlyContain(e => e.Parent == p);
        r.Width.Should().Be(250);
        r.Height.Should().Be(80);
    }

    [Fact]
    public void SubtreesNeverOverlap()
    {
        var deep = new N("x", new N("x1", new N("x11"), new N("x12")), new N("x2"));
        var other = new N("y", new N("y1"));
        var r = Lay(new N("root", deep, other), new N("root2"));
        var byColumn = r.Positions.GroupBy(kv => kv.Value.X);
        foreach (var col in byColumn)
        {
            var ys = col.Select(kv => kv.Value.Y).OrderBy(y => y).ToList();
            for (int i = 1; i < ys.Count; i++) (ys[i] - ys[i - 1]).Should().BeGreaterOrEqualTo(30);
        }
    }

    [Fact]
    public void CollapsedChildrenAreNotPlaced_AndDeepChainsDoNotOverflow()
    {
        var leaf = new N("leaf");
        var r = ProcessTreeLayout.Compute(new[] { new N("p", leaf) }, _ => Array.Empty<N>(), 100, 20, 50, 10);
        r.Positions.Should().ContainSingle();
        r.Positions.ContainsKey(leaf).Should().BeFalse();

        N chain = new("end");
        for (int i = 0; i < 20000; i++) chain = new N("c" + i, chain);
        var big = Lay(chain);
        big.Positions.Should().HaveCount(20001);
    }

    // ---- incremental Update: drawn items move only down, only to avoid an overlap

    private static TreeLayoutResult<N> Up(IEnumerable<N> roots, Func<N, IReadOnlyList<N>> kids, TreeLayoutResult<N> prev, double rootGap = 0) =>
        ProcessTreeLayout.Update(roots, kids, prev.Positions, columnStep: 150, rowStep: 30, rootGap: rootGap);

    private static void NoOverlap(TreeLayoutResult<N> r)
    {
        foreach (var col in r.Positions.GroupBy(kv => kv.Value.X))
        {
            var ys = col.Select(kv => kv.Value.Y).OrderBy(y => y).ToList();
            for (int i = 1; i < ys.Count; i++) (ys[i] - ys[i - 1]).Should().BeGreaterOrEqualTo(30);
        }
    }

    [Fact]
    public void Update_ExpandingPushesLaterSiblingsDown_AndNeverMovesAnythingUp()
    {
        var a1 = new N("a1"); var a2 = new N("a2"); var a3 = new N("a3");
        var b1 = new N("b1"); var b2 = new N("b2");
        var a = new N("a", a1, a2, a3); var b = new N("b", b1, b2);
        var p = new N("p", a, b);
        var open = new HashSet<N> { p };
        IReadOnlyList<N> Kids(N n) => open.Contains(n) ? n.Kids : Array.Empty<N>();
        var before = ProcessTreeLayout.Compute(new[] { p }, Kids, 120, 20, 30, 10);
        double rowA = before.Positions[a].Y, rowB = before.Positions[b].Y;
        (rowB - rowA).Should().Be(30);

        open.Add(a);   // a's 3 children: a stays, b goes down by 2 steps
        var expA = Up(new[] { p }, Kids, before);
        expA.Positions[a].Should().Be(before.Positions[a]);
        expA.Positions[p].Should().Be(before.Positions[p]);
        expA.Positions[a1].Y.Should().Be(rowA);
        expA.Positions[a3].Y.Should().Be(rowA + 60);
        expA.Positions[b].Should().Be(new TreeLayoutPoint(before.Positions[b].X, rowB + 60));
        expA.Added.Should().Equal(a1, a2, a3);
        NoOverlap(expA);

        open.Add(b);   // b's children start at b's new row, below a's subtree
        var expB = Up(new[] { p }, Kids, expA);
        foreach (var (item, pos) in expA.Positions) expB.Positions[item].Should().Be(pos);
        expB.Positions[b1].Y.Should().Be(rowB + 60);
        expB.Positions[b2].Y.Should().Be(rowB + 90);
        NoOverlap(expB);

        open.Remove(a);   // collapse a: gaps stay, b does not move up
        var colA = Up(new[] { p }, Kids, expB);
        colA.Positions.Should().NotContainKey(a1);
        colA.Positions[b].Should().Be(expB.Positions[b]);
        colA.Positions[b1].Should().Be(expB.Positions[b1]);
    }

    [Fact]
    public void Group_start_root_gets_the_extra_gap_and_keeps_it_across_updates()
    {
        var a = new N("a"); var r1 = new N("r1", a); var o1 = new N("o1"); var o2 = new N("o2");
        var open = new HashSet<N> { r1 };
        IReadOnlyList<N> Kids(N n) => open.Contains(n) ? n.Kids : Array.Empty<N>();
        bool Start(N n) => n == o1;
        var full = ProcessTreeLayout.Compute(new[] { r1, o1, o2 }, Kids, 120, 20, 30, 10, rootGap: 5, groupStart: Start, groupGap: 30);
        full.Positions[o1].Y.Should().Be(30 + 5 + 30, "below r1's row + the root gap + the group gap");
        full.Positions[o2].Y.Should().Be(65 + 30 + 5, "later roots in the group only take the root gap");
        open.Remove(r1);
        var collapsed = ProcessTreeLayout.Update(new[] { r1, o1, o2 }, Kids, full.Positions, 150, 30, 5, null, Start, 30);
        collapsed.Positions[o1].Should().Be(full.Positions[o1], "a collapse leaves the group where it was");
        var again = ProcessTreeLayout.Update(new[] { r1, o1, o2 }, Kids, collapsed.Positions, 150, 30, 5, null, Start, 30);
        again.Positions.Should().Equal(collapsed.Positions);
    }

    [Fact]
    public void Update_PushPropagatesToLaterRoots()
    {
        var a1 = new N("a1"); var a2 = new N("a2"); var a = new N("a", a1, a2);
        var c = new N("c"); var r2 = new N("r2", c); var r1 = new N("r1", a);
        var open = new HashSet<N> { r1, r2 };
        IReadOnlyList<N> Kids(N n) => open.Contains(n) ? n.Kids : Array.Empty<N>();
        var before = ProcessTreeLayout.Compute(new[] { r1, r2 }, Kids, 120, 20, 30, 10, rootGap: 5);
        before.Positions[r2].Y.Should().Be(35);

        open.Add(a);
        var after = Up(new[] { r1, r2 }, Kids, before, rootGap: 5);
        after.Positions[r1].Should().Be(before.Positions[r1]);
        after.Positions[a].Should().Be(before.Positions[a]);
        after.Positions[a2].Y.Should().Be(30);
        after.Positions[r2].Y.Should().Be(65, "a later root starts below the previous root's subtree + a row step + the root gap");
        after.Positions[c].Y.Should().Be(65);
        NoOverlap(after);
    }

    [Fact]
    public void Update_PutsANewAncestorOneColumnLeftOfTheDrawnChild()
    {
        var node = new N("node"); var parent = new N("parent", node); var grand = new N("grand", parent);
        var before = ProcessTreeLayout.Compute(new[] { parent }, n => n.Kids, 100, 20, 50, 10);
        var after = Up(new[] { grand }, n => n.Kids, before);
        after.Positions[parent].Should().Be(before.Positions[parent]);
        after.Positions[node].Should().Be(before.Positions[node]);
        after.Added.Should().Equal(grand);
        after.Positions[grand].Should().Be(new TreeLayoutPoint(before.Positions[parent].X - 150, before.Positions[parent].Y));
    }

    [Fact]
    public void EmptyInput_GivesEmptyLayout()
    {
        var r = Lay();
        r.Positions.Should().BeEmpty();
        r.Width.Should().Be(0);
        r.Height.Should().Be(0);
    }

    /// <summary>Update the way the canvas does: base rows = the full layout's rows, plus the first placement of anything added later.</summary>
    private static TreeLayoutResult<N> UpBase(IEnumerable<N> roots, Func<N, IReadOnlyList<N>> kids, TreeLayoutResult<N> prev, Dictionary<N, double> baseRows)
    {
        var r = ProcessTreeLayout.Update(roots, kids, prev.Positions, columnStep: 150, rowStep: 30, baseRows: baseRows);
        foreach (var (n, pos) in r.Positions) baseRows.TryAdd(n, pos.Y);
        return r;
    }

    [Fact]
    public void Update_CollapseUndoesTheExpand_ChildrenAndStack()
    {
        // children: expand a (pushes b down), collapse it: every position is the original again
        var a = new N("a", new N("a1"), new N("a2"), new N("a3")); var b = new N("b", new N("b1"));
        var p = new N("p", a, b);
        var open = new HashSet<N> { p, b };
        IReadOnlyList<N> Kids(N n) => open.Contains(n) ? n.Kids : Array.Empty<N>();
        var orig = ProcessTreeLayout.Compute(new[] { p }, Kids, 120, 20, 30, 10);
        var baseRows = orig.Positions.ToDictionary(kv => kv.Key, kv => kv.Value.Y);
        open.Add(a);
        var expanded = UpBase(new[] { p }, Kids, orig, baseRows);
        expanded.Positions[b].Y.Should().BeGreaterThan(orig.Positions[b].Y);
        open.Remove(a);
        var collapsed = UpBase(new[] { p }, Kids, expanded, baseRows);
        collapsed.Positions.Should().BeEquivalentTo(orig.Positions);

        // stack: s stands for m1..m3; expanding replaces it by the members, collapsing brings it back on its row
        var s = new N("s"); var m = new[] { new N("m1"), new N("m2"), new N("m3") }; var c = new N("c");
        var stackOpen = false;
        var root = new N("r");
        IReadOnlyList<N> Kids2(N n) => n != root ? Array.Empty<N>() : stackOpen ? m.Append(c).ToList() : new[] { s, c };
        var orig2 = ProcessTreeLayout.Compute(new[] { root }, Kids2, 120, 20, 30, 10);
        var base2 = orig2.Positions.ToDictionary(kv => kv.Key, kv => kv.Value.Y);
        stackOpen = true;
        var exp2 = UpBase(new[] { root }, Kids2, orig2, base2);
        exp2.Positions[c].Y.Should().BeGreaterThan(orig2.Positions[c].Y);
        stackOpen = false;
        var col2 = UpBase(new[] { root }, Kids2, exp2, base2);
        col2.Positions.Should().BeEquivalentTo(orig2.Positions);
        NoOverlap(col2);
    }
}
