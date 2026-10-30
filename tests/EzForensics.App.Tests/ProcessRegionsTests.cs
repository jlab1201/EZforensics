using EzForensics.App.Controls;
using FluentAssertions;

namespace EzForensics.App.Tests;

public sealed class ProcessRegionsTests
{
    private sealed class T
    {
        public T(string name, params T[] kids) { Name = name; Kids = kids; }
        public string Name { get; }
        public IReadOnlyList<T> Kids { get; }
        public override string ToString() => Name;
    }

    private static IReadOnlyList<T> Kids(T t) => t.Kids;

    [Fact]
    public void Boot_chain_needs_a_boot_name_and_a_boot_chain_parent()
    {
        var hostedExplorer = new T("explorer.exe");
        var svc = new T("svchost.exe", hostedExplorer, new T("a.exe"));
        var services = new T("SERVICES.EXE", svc, new T("spoolsv.exe"));
        var wininit = new T("wininit.exe", services, new T("lsass.exe"));
        var stray = new T("stray.exe", new T("csrss.exe"));
        var boot = ProcessRegions.BootChain(new[] { wininit, stray }, Kids, t => t.Name);
        boot.Select(b => b.Name).Should().BeEquivalentTo("wininit.exe", "SERVICES.EXE", "lsass.exe");
        boot.Should().NotContain(hostedExplorer, "its parent (svchost) is not boot chain");
    }

    [Fact]
    public void Without_boot_names_the_top_three_layers_are_the_chain()
    {
        var deep = new T("d3.exe");
        var root = new T("a.exe", new T("b.exe", new T("c.exe", deep)));
        ProcessRegions.BootChain(new[] { root }, Kids, t => t.Name).Select(b => b.Name).Should().BeEquivalentTo("a.exe", "b.exe", "c.exe");
    }

    [Fact]
    public void Regions_group_the_non_boot_subtrees_per_boot_parent()
    {
        var svc = new T("svchost.exe", new T("a.exe"), new T("b.exe"));
        var services = new T("services.exe", svc, new T("spoolsv.exe"));
        var lone = new T("lsass.exe", new T("one.exe"));
        var wininit = new T("wininit.exe", services, lone);
        var boot = ProcessRegions.BootChain(new[] { wininit }, Kids, t => t.Name);
        var regions = ProcessRegions.Build(new[] { wininit }, Kids, boot, t => t.Name == "spoolsv.exe" ? 3 : 1);
        regions.Should().ContainSingle("lsass.exe has one process under it: no region");
        regions[0].Parent.Should().BeSameAs(services);
        regions[0].Processes.Should().Be(6, "svchost + 2 children + a stack-like item weighing 3");
        regions[0].Items.Should().HaveCount(4);
    }

    [Fact]
    public void Dominant_picks_the_most_common_role()
    {
        var services = new RegionRole("Services", "Services", 60);
        var system = new RegionRole("System foundation", "System", 20);
        ProcessRegions.Dominant(new[] { services, system }).Select(r => r.Key).Should().Equal("Services");
    }

    [Fact]
    public void Boot_spacing_is_the_largest_scale_at_which_no_two_pills_overlap()
    {
        ProcessGraphCanvas.BootSpacing(new[] { new System.Windows.Point(0, 0) }).Should().Be(double.PositiveInfinity);
        // dx 100 / 197 = 0.508 beats dy 31 / 62 = 0.5: the pair clears sideways from that scale on
        ProcessGraphCanvas.BootSpacing(new[] { new System.Windows.Point(0, 0), new System.Windows.Point(100, 31), new System.Windows.Point(0, 500) })
            .Should().BeApproximately(100 / ProcessGraphCanvas.PillWidth, 1e-9);
    }

    [Fact]
    public void Orphaned_trees_share_one_region_and_are_ordered_after_the_boot_tree()
    {
        var wininit = new T("wininit.exe", new T("services.exe", new T("svchost.exe"), new T("spoolsv.exe")));
        var o1 = new T("ghost", new T("x.exe", new T("y.exe")));
        var o2 = new T("z.exe");
        var roots = ProcessRegions.BootFirst(new[] { o1, wininit, o2 }, r => ProcessRegions.IsBootName(r.Name));
        roots.Should().Equal(wininit, o1, o2);
        var boot = ProcessRegions.BootChain(roots, Kids, t => t.Name);
        var regions = ProcessRegions.Build(roots, Kids, boot, _ => 1);
        var orphan = regions.Should().ContainSingle(r => r.Orphaned).Subject;
        orphan.Groups.Should().HaveCount(2, "one group per orphaned tree");
        orphan.Processes.Should().Be(4);
        ProcessRegions.BootFirst(new[] { o1, o2 }, r => ProcessRegions.IsBootName(r.Name)).Should().Equal(o1, o2);
    }
}