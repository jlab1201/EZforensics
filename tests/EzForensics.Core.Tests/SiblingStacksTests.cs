using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Same-name sibling stacks and the Focus projection (pure view constructs over the real parent links).</summary>
public sealed class SiblingStacksTests
{
    private sealed class Item
    {
        public Item(string name, bool pinned = false) { Name = name; Pinned = pinned; }
        public string Name { get; }
        public bool Pinned { get; }
        public Item? Parent { get; set; }
        public List<Item> Kids { get; } = new();
        public override string ToString() => Name;
    }

    private static IReadOnlyList<SiblingItem<Item>> Group(IReadOnlyList<Item> s, Func<string, bool>? expanded = null) =>
        SiblingStacks.Group(s, i => i.Name, i => i.Pinned, expanded);

    [Fact]
    public void Three_same_name_siblings_stack_case_insensitively_two_do_not()
    {
        var a = new Item("svchost.exe"); var b = new Item("SVCHOST.EXE"); var c = new Item("svchost.exe"); var d = new Item("lsass.exe");
        var r = Group(new[] { d, a, b, c });
        r.Should().HaveCount(2);
        r[0].Single.Should().BeSameAs(d);
        r[1].IsStack.Should().BeTrue();
        r[1].Members.Should().Equal(a, b, c);
        r[1].Name.Should().Be("svchost.exe");

        var two = Group(new[] { new Item("x.exe"), new Item("x.exe"), new Item("y.exe") });
        two.Should().OnlyContain(i => !i.IsStack);
    }

    [Fact]
    public void Pinned_siblings_are_never_in_a_stack_and_keep_their_place()
    {
        var a = new Item("svchost.exe"); var flagged = new Item("svchost.exe", pinned: true); var b = new Item("svchost.exe"); var c = new Item("svchost.exe");
        var r = Group(new[] { a, flagged, b, c });
        r.Should().HaveCount(2);
        r[0].Members.Should().Equal(a, b, c);
        r[1].Single.Should().BeSameAs(flagged);
        r.Where(i => i.IsStack).SelectMany(i => i.Members).Should().NotContain(i => i.Pinned);

        // pinned count toward the threshold, but a stack needs two unpinned members
        var p1 = new Item("cmd.exe", true); var p2 = new Item("cmd.exe", true); var u = new Item("cmd.exe");
        Group(new[] { p1, p2, u }).Should().OnlyContain(i => !i.IsStack);
        var u2 = new Item("cmd.exe");
        var r2 = Group(new[] { p1, u, p2, u2 });
        r2.Should().HaveCount(3);
        r2[0].Single.Should().BeSameAs(p1);
        r2[1].Members.Should().Equal(u, u2);
        r2[2].Single.Should().BeSameAs(p2);
    }

    [Fact]
    public void Root_level_unnamed_and_expanded_siblings()
    {
        // root level works the same (the caller passes the roots as siblings)
        var roots = Enumerable.Range(0, 5).Select(_ => new Item("csrss.exe")).Append(new Item("")).Append(new Item("")).Append(new Item("")).ToList();
        var r = Group(roots);
        r.Should().HaveCount(4);
        r[0].Members.Should().HaveCount(5);
        r.Skip(1).Should().OnlyContain(i => !i.IsStack && i.Single!.Name == "");   // empty name (ghost) is never stacked

        Group(roots, n => n.Equals("CSRSS.EXE", StringComparison.OrdinalIgnoreCase)).Should().OnlyContain(i => !i.IsStack);
        SiblingStacks.StackContaining(roots, roots[2], i => i.Name, i => i.Pinned).Should().HaveCount(5);
        SiblingStacks.StackContaining(roots, roots[6], i => i.Name, i => i.Pinned).Should().BeEmpty();
    }

    // ------------------------------------------------------------ focus

    private static (Item Root, Item Mid, Item Node, Item Child, Item Grand, Item[] Roots) Tree()
    {
        var root = new Item("System"); var other = new Item("Registry");
        var mid = new Item("smss.exe"); var midSib = new Item("csrss.exe"); var midSib2 = new Item("wininit.exe");
        var node = new Item("winlogon.exe"); var nodeSib = new Item("fontdrvhost.exe");
        var child = new Item("userinit.exe"); var grand = new Item("explorer.exe");
        void Link(Item p, params Item[] kids) { foreach (var k in kids) { k.Parent = p; p.Kids.Add(k); } }
        Link(root, mid, midSib, midSib2);
        Link(mid, node, nodeSib);
        Link(node, child);
        Link(child, grand);
        return (root, mid, node, child, grand, new[] { root, other });
    }

    [Fact]
    public void Focus_shows_ancestors_node_and_subtree_with_sibling_counts()
    {
        var t = Tree();
        var f = FocusProjection.Compute(t.Node, i => i.Parent, i => i.Kids, t.Roots, FocusScope.Focus);
        f.Ancestors.Should().Equal(t.Root, t.Mid);
        f.Others.Should().Equal(1, 2, 1);   // Registry; csrss + wininit; fontdrvhost
        f.Items.Should().BeEquivalentTo(new[] { t.Root, t.Mid, t.Node, t.Child, t.Grand });
        f.IncludesSubtree.Should().BeTrue();
        f.ExpandsSubtree.Should().BeFalse();
        f.Breadcrumb.Should().Equal(t.Root, t.Mid);
    }

    [Fact]
    public void Trace_up_down_and_lineage_variants()
    {
        var t = Tree();
        var up = FocusProjection.Compute(t.Node, i => i.Parent, i => i.Kids, t.Roots, FocusScope.TraceUp);
        up.Items.Should().BeEquivalentTo(new[] { t.Root, t.Mid, t.Node });
        up.IncludesSubtree.Should().BeFalse();

        var down = FocusProjection.Compute(t.Node, i => i.Parent, i => i.Kids, t.Roots, FocusScope.TraceDown);
        down.Ancestors.Should().BeEmpty();
        down.Others.Should().BeEmpty();
        down.Items.Should().BeEquivalentTo(new[] { t.Node, t.Child, t.Grand });
        down.ExpandsSubtree.Should().BeTrue();
        down.Breadcrumb.Should().Equal(t.Root, t.Mid);   // the breadcrumb always names the real chain

        var lin = FocusProjection.Compute(t.Node, i => i.Parent, i => i.Kids, t.Roots, FocusScope.Lineage);
        lin.Items.Should().HaveCount(5);
        lin.ExpandsSubtree.Should().BeTrue();

        var rootFocus = FocusProjection.Compute(t.Root, i => i.Parent, i => i.Kids, t.Roots, FocusScope.TraceUp);
        rootFocus.Ancestors.Should().BeEmpty();
        rootFocus.Others.Should().Equal(1);
    }

    [Fact]
    public void OtherSiblings_lists_the_left_out_children_per_chain_level_in_real_order()
    {
        var t = Tree();
        Func<Item, IReadOnlyList<Item>> kids = i => i.Kids;
        var up = FocusProjection.Compute(t.Node, i => i.Parent, kids, t.Roots, FocusScope.TraceUp);
        FocusProjection.OtherSiblings(up, 0, kids, t.Roots).Select(i => i.Name).Should().Equal("Registry");
        FocusProjection.OtherSiblings(up, 1, kids, t.Roots).Select(i => i.Name).Should().Equal("csrss.exe", "wininit.exe");
        FocusProjection.OtherSiblings(up, 2, kids, t.Roots).Select(i => i.Name).Should().Equal("fontdrvhost.exe");
        for (int i = 0; i < up.Others.Count; i++) FocusProjection.OtherSiblings(up, i, kids, t.Roots).Should().HaveCount(up.Others[i]);
        FocusProjection.OtherSiblings(up, 3, kids, t.Roots).Should().BeEmpty();
        FocusProjection.OtherSiblings(up, -1, kids, t.Roots).Should().BeEmpty();

        var down = FocusProjection.Compute(t.Node, i => i.Parent, kids, t.Roots, FocusScope.TraceDown);
        FocusProjection.OtherSiblings(down, 0, kids, t.Roots).Should().BeEmpty();
    }
}
