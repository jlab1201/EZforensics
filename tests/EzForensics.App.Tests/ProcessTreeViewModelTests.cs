using EzForensics.App.ViewModels;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Path view (Trace up) of a PID click, the parent's "N children" pill, Show whole tree and the details card rows, on a hand-built graph.</summary>
public sealed class ProcessTreeViewModelTests
{
    // roots: A(1), B(2), C(3); A: A1(10), A2(11); A2: A2a(20), A2b(21)
    private static async Task<ProcessTreeViewModel> LoadAsync()
    {
        var g = new ProcessGraph();
        ProcessNode N(int pid, string name, ProcessNode? parent)
        {
            var n = new ProcessNode { Pid = pid, Ppid = parent?.Pid ?? 0, Name = name, CreateTime = new DateTime(2026, 1, 1, 0, 0, pid, DateTimeKind.Utc), Parent = parent };
            parent?.Children.Add(n);
            g.AllNodes.Add(n);
            if (parent == null) g.Roots.Add(n);
            return n;
        }
        var a = N(1, "a.exe", null); N(2, "b.exe", null); N(3, "c.exe", null);
        N(10, "a1.exe", a);
        var a2 = N(11, "a2.exe", a);
        N(20, "a2a.exe", a2); N(21, "a2b.exe", a2);
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        await vm.ShowGraphAsync(g, CancellationToken.None);
        return vm;
    }

    private static IProcessGraphSource Src(ProcessTreeViewModel vm) => vm;
    private static IEnumerable<ProcessNodeViewModel> All(IEnumerable<ProcessNodeViewModel> nodes) => nodes.SelectMany(r => All(r.AllChildren).Prepend(r));
    private static ProcessNodeViewModel Node(ProcessTreeViewModel vm, int pid) => All(vm.VisibleRoots).First(n => n.Pid == pid);
    private static string[] Names(IEnumerable<object> items) => items.Select(i => i switch
    {
        ProcessNodeViewModel n => n.Name,
        _ => i.GetType().Name,
    }).ToArray();

    [Fact]
    public async Task Pid_click_opens_the_trace_up_chain_with_the_node_collapsed()
    {
        var vm = await LoadAsync();
        vm.SelectPid(11, null, null, null).Should().Be(PidResolutionKind.Found);
        vm.IsFocusMode.Should().BeTrue();
        vm.Selected!.Pid.Should().Be(11);
        Names(Src(vm).LayoutRoots()).Should().Equal("a.exe");
        var a = Node(vm, 1);
        a.IsExpanded.Should().BeFalse("the parent starts collapsed, with its normal expand pill");
        a.ChildrenText.Should().Be("2 children");
        Names(Src(vm).LayoutChildren(a)).Should().Equal("a2.exe");
        var a2 = Node(vm, 11);
        a2.HasChildren.Should().BeTrue();
        a2.IsExpanded.Should().BeFalse();
        Src(vm).LayoutChildren(a2).Should().BeEmpty();
    }

    [Fact]
    public async Task Toggling_the_node_lays_out_its_children_and_selecting_one_stays_in_focus()
    {
        var vm = await LoadAsync();
        vm.OpenPath(Node(vm, 11));
        var a2 = Node(vm, 11);
        a2.ToggleCommand.Execute(null);
        Names(Src(vm).LayoutChildren(a2)).Should().Equal("a2a.exe", "a2b.exe");
        vm.RevealAndSelect(Node(vm, 20));
        vm.IsFocusMode.Should().BeTrue();
        vm.Selected!.Pid.Should().Be(20);
    }

    [Fact]
    public async Task Parent_children_pill_shows_all_its_children_in_the_path_view()
    {
        var vm = await LoadAsync();
        vm.OpenPath(Node(vm, 11));
        var a = Node(vm, 1);
        a.ToggleCommand.Execute(null);
        vm.IsFocusMode.Should().BeTrue();
        Src(vm).LayoutRoots();   // a new layout pass
        Names(Src(vm).LayoutChildren(a)).Should().Equal("a1.exe", "a2.exe");
        // a sibling is drawn, so selecting it does not leave Focus
        vm.RevealAndSelect(Node(vm, 10));
        vm.IsFocusMode.Should().BeTrue();
        a.ToggleCommand.Execute(null);
        Src(vm).LayoutRoots();
        Names(Src(vm).LayoutChildren(a)).Should().Equal("a2.exe");
        vm.IsFocusMode.Should().BeTrue();
    }

    private static TreeLayoutResult<object> FullLayout(ProcessTreeViewModel vm) =>
        ProcessTreeLayout.Compute(Src(vm).LayoutRoots(), Src(vm).LayoutChildren, 165, 150, 33, 36, 18);

    /// <summary>What the canvas does on an expand / collapse while <see cref="IProcessGraphSource.LayoutGeneration"/> stays the same.</summary>
    private static TreeLayoutResult<object> Incremental(ProcessTreeViewModel vm, TreeLayoutResult<object> prev) =>
        ProcessTreeLayout.Update(Src(vm).LayoutRoots(), Src(vm).LayoutChildren, prev.Positions, 198, 186, 18);

    [Fact]
    public async Task Expanding_children_keeps_every_drawn_item_in_place_and_only_explicit_actions_relayout()
    {
        var vm = await LoadAsync();
        var a2 = Node(vm, 11);
        a2.IsExpanded = false;
        Src(vm).LayoutRoots();
        var before = FullLayout(vm);
        int gen = Src(vm).LayoutGeneration;

        a2.ToggleCommand.Execute(null);
        Src(vm).LayoutGeneration.Should().Be(gen, "an expand is not a re-layout");
        var after = Incremental(vm, before);
        // the expanded item and everything above it stay; items below only move down (a2b pushes the later roots b, c by one row)
        foreach (var (item, p) in before.Positions)
        {
            after.Positions[item].X.Should().Be(p.X);
            after.Positions[item].Y.Should().BeGreaterOrEqualTo(p.Y);
        }
        foreach (var pid in new[] { 1, 10, 11 }) after.Positions[Node(vm, pid)].Should().Be(before.Positions[Node(vm, pid)]);
        after.Positions[Node(vm, 2)].Y.Should().Be(before.Positions[Node(vm, 2)].Y + 186);
        after.Added.Cast<ProcessNodeViewModel>().Select(n => n.Pid).Should().Equal(20, 21);

        vm.FitCommand.Execute(null);
        Src(vm).LayoutGeneration.Should().NotBe(gen, "Fit lays the graph out again");
        gen = Src(vm).LayoutGeneration;
        vm.ShowExited = false;
        Src(vm).LayoutGeneration.Should().NotBe(gen, "Show exited lays the graph out again");
    }

    [Fact]
    public async Task Pid_click_draws_the_process_and_its_parent_and_expand_up_adds_one_ancestor_in_place()
    {
        var vm = await LoadAsync();
        vm.OpenPath(Node(vm, 20));
        Drawn(vm).Should().BeEquivalentTo(new[] { 11, 20 }, "only the parent and the process");
        Names(Src(vm).LayoutRoots()).Should().Equal("a2.exe");
        var a2 = Node(vm, 11);
        Names(Src(vm).LayoutChildren(a2)).Should().Equal("a2a.exe");
        a2.CanExpandUp.Should().BeTrue();
        a2.ExpandUpText.Should().Be("↑ 1 more");
        a2.ExpandUpToolTip.Should().Be("Show the parent of a2.exe (PID 11)");
        Node(vm, 20).CanExpandUp.Should().BeFalse();
        var before = FullLayout(vm);
        int gen = Src(vm).LayoutGeneration;

        a2.ExpandUpCommand.Execute(null);
        Src(vm).LayoutGeneration.Should().Be(gen, "expand up is not a re-layout");
        Drawn(vm).Should().BeEquivalentTo(new[] { 1, 11, 20 });
        Names(Src(vm).LayoutRoots()).Should().Equal("a.exe");
        a2.CanExpandUp.Should().BeFalse();
        Node(vm, 1).CanExpandUp.Should().BeFalse("a.exe is a root");
        var after = Incremental(vm, before);
        foreach (var (item, p) in before.Positions) after.Positions[item].Should().Be(p);
        after.Positions[Node(vm, 1)].X.Should().BeLessThan(after.Positions[a2].X);
        vm.IsFocusMode.Should().BeTrue();
    }

    [Fact]
    public async Task Parent_link_above_the_drawn_path_reveals_it_without_leaving_the_path_view()
    {
        var vm = await LoadAsync();
        vm.OpenPath(Node(vm, 20));
        vm.RevealAndSelect(Node(vm, 1));
        vm.IsFocusMode.Should().BeTrue();
        Drawn(vm).Should().Contain(new[] { 1, 11, 20 });
        vm.Selected!.Pid.Should().Be(1);
    }

    [Fact]
    public async Task Show_whole_tree_returns_to_tree_mode()
    {
        var vm = await LoadAsync();
        vm.CanShowWholeTree.Should().BeFalse();
        vm.OpenPath(Node(vm, 11));
        vm.CanShowWholeTree.Should().BeTrue();
        vm.ShowWholeTreeCommand.Execute(null);
        vm.IsFocusMode.Should().BeFalse();
        vm.CanShowWholeTree.Should().BeFalse();
        Names(Src(vm).LayoutRoots()).Should().Equal("a.exe", "b.exe", "c.exe");
    }

    [Fact]
    public async Task Open_path_opens_the_path_view_of_the_selection()
    {
        var vm = await LoadAsync();
        vm.SelectNode(Node(vm, 11));
        vm.OpenPath(Node(vm, 11));
        vm.IsFocusMode.Should().BeTrue();
        vm.CanShowWholeTree.Should().BeTrue();
    }

    [Fact]
    public async Task Other_scopes_draw_the_chain_without_an_others_pill()
    {
        var vm = await LoadAsync();
        vm.EnterFocus(Node(vm, 11), FocusScope.Focus);
        Names(Src(vm).LayoutRoots()).Should().Equal("a.exe");
        Names(Src(vm).LayoutChildren(Node(vm, 1))).Should().Equal("a2.exe");
    }

    // ---- Navigator groups from the path view

    // roots: A(1), B(2), C(3); A: A1(10), A2(11); A2: cmd(20), A2b(21); B: cmd(30). Roles from roles.json (ProcessIndicators.Annotate).
    private static async Task<ProcessTreeViewModel> LoadWithRolesAsync()
    {
        var g = new ProcessGraph();
        ProcessNode N(int pid, string name, ProcessNode? parent)
        {
            var n = new ProcessNode { Pid = pid, Ppid = parent?.Pid ?? 0, Name = name, CreateTime = new DateTime(2026, 1, 1, 0, 0, pid, DateTimeKind.Utc), Parent = parent };
            parent?.Children.Add(n);
            g.AllNodes.Add(n);
            if (parent == null) g.Roots.Add(n);
            return n;
        }
        var a = N(1, "a.exe", null); var b = N(2, "b.exe", null); N(3, "c.exe", null);
        N(10, "a1.exe", a);
        var a2 = N(11, "a2.exe", a);
        N(20, "cmd.exe", a2); N(21, "a2b.exe", a2);
        N(30, "cmd.exe", b);
        ProcessIndicators.Annotate(g);
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        await vm.ShowGraphAsync(g, CancellationToken.None);
        return vm;
    }

    /// <summary>Every process the canvas would draw (a full layout pass).</summary>
    private static HashSet<int> Drawn(ProcessTreeViewModel vm)
    {
        var pids = new HashSet<int>();
        var stack = new Stack<object>(Src(vm).LayoutRoots());
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item is ProcessNodeViewModel n) pids.Add(n.Pid);
            foreach (var c in Src(vm).LayoutChildren(item)) stack.Push(c);
        }
        return pids;
    }

    private static NavigatorGroupViewModel ShellsGroup(ProcessTreeViewModel vm) =>
        vm.NavigatorSections.Single(s => s.Section == NavigatorSection.Roles).Groups.Single(g => g.Title == "Shells & scripts");

    [Fact]
    public async Task Navigator_group_from_the_path_view_draws_every_member()
    {
        var vm = await LoadWithRolesAsync();
        vm.OpenPath(Node(vm, 11));
        vm.IsFocusMode.Should().BeTrue();
        Drawn(vm).Should().NotContain(new[] { 20, 30 });

        var group = ShellsGroup(vm);
        group.Activate();
        vm.SelectedGroup.Should().BeSameAs(group);
        vm.IsFocusMode.Should().BeFalse("the path view does not draw the members");
        Drawn(vm).Should().Contain(new[] { 20, 30 });
        Node(vm, 20).IsGroupMember.Should().BeTrue();
        Node(vm, 30).IsGroupMember.Should().BeTrue();
        Node(vm, 1).IsDimmed.Should().BeTrue();
    }

    [Fact]
    public async Task Navigator_member_click_selects_it_and_a_second_group_click_clears_the_group()
    {
        var vm = await LoadWithRolesAsync();
        vm.OpenPath(Node(vm, 11));
        var group = ShellsGroup(vm);
        group.Activate();
        var member = group.Members.Single(m => m.Node.Pid == 30);
        member.Activate();
        vm.Selected!.Pid.Should().Be(30);
        Drawn(vm).Should().Contain(30);

        group.Activate();
        vm.SelectedGroup.Should().BeNull();
        group.IsSelected.Should().BeFalse();
        vm.NavigatorSections.SelectMany(s => s.Groups).SelectMany(g => g.Nodes).Should().OnlyContain(n => !n.IsGroupMember && !n.IsDimmed);
    }

    private static ProcessDetailViewModel Detail(ProcessNode n, Action<string>? copyText = null)
    {
        var vm = new ProcessNodeViewModel(n, null, _ => { });
        return new ProcessDetailViewModel(vm, null, (_, _) => { }, _ => { }, copyText);
    }

    [Fact]
    public void Memory_only_node_shows_user_integrity_and_memory_rows_and_hints_for_empty_ones()
    {
        var n = new ProcessNode
        {
            Pid = 4, Name = "x.exe", User = @"NT AUTHORITY\SYSTEM", UserSource = "getsids", Integrity = "System", SessionId = 0, Wow64 = true, Offset = "0xabc",
            CreateTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Sources = ProcessSources.PsList, FileVersion = "1.0", Hash = "SHA256=ff", HashSource = "Sysmon 1",
        };
        var f = Detail(n).Fields;
        var ids = f.Select(x => x.Id).ToList();
        ids.Should().NotContain(new[] { "ProcessName", "Pid", "FileVersion", "HashSource", "UserName", "Domain", "FirstSeen", "LastSeen" });
        f.Single(x => x.Id == "User").Value.Should().Be(@"NT AUTHORITY\SYSTEM (getsids)");
        f.Single(x => x.Id == "Integrity").Value.Should().Be("System");
        f.Single(x => x.Id == "Memory").Value.Should().Be("Session 0 · WoW64 (32-bit) · offset 0xabc");
        f.Single(x => x.Id == "Hash").Value.Should().Be("SHA256=ff (Sysmon 1)");
        f.Single(x => x.Id == "EndTime").Value.Should().Be("No exit time (in the active process list at capture)");
        ids.Should().NotContain("Signature").And.NotContain("Activity");
        var cmd = f.Single(x => x.Id == "CmdLine");
        cmd.Value.Should().Be(ProcessDetailField.None);
        cmd.Hint.Should().NotBeEmpty();
        f.Single(x => x.Id == "Path").Hint.Should().NotBeEmpty();
        f.Single(x => x.Id == "StartTime").Hint.Should().BeEmpty();
    }

    [Fact]
    public void Rail_keeps_overview_network_files_registry_modules_identity()
    {
        ProcessDetailViewModel.RailTabs.Select(t => t.Tab).Should().Equal(
            ProcessTab.Overview, ProcessTab.Network, ProcessTab.Files, ProcessTab.Registry, ProcessTab.Modules, ProcessTab.Identity);
    }

    [Fact]
    public async Task Field_copy_command_copies_the_value_flips_to_a_check_mark_and_back()
    {
        var copied = new List<string>();
        var f = Detail(new ProcessNode { Pid = 5, Name = "y.exe", Path = "C:/x/y.exe" }, copied.Add).Fields;
        var path = f.Single(x => x.Id == "Path");
        path.CopyTip.Should().Be("Copy Process Path");
        path.CopiedFor = TimeSpan.FromMilliseconds(30);
        path.CopyCommand.Execute(null);
        copied.Should().Equal("C:/x/y.exe");
        path.IsCopied.Should().BeTrue();
        await Task.Delay(300);
        path.IsCopied.Should().BeFalse();

        var empty = f.Single(x => x.Id == "CmdLine");
        empty.CanCopy.Should().BeFalse("a field without data has nothing to copy");
        empty.CopyCommand.Execute(null);
        copied.Should().HaveCount(1);
    }

    [Fact]
    public void Absent_optional_rows_are_omitted()
    {
        var f = Detail(new ProcessNode { Pid = 5, Name = "y.exe" }).Fields;
        f.Select(x => x.Id).Should().Equal("StartTime", "Path", "CmdLine", "Parent");
    }

    [Fact]
    public void Navigator_tree_rows_know_their_position_for_the_guide_lines()
    {
        NavigatorGroupViewModel Group(NavigatorSection s, string key, int members)
        {
            var nodes = Enumerable.Range(0, members).Select(i => new ProcessNodeViewModel(new ProcessNode { Pid = 100 + i, Name = "p.exe" }, null, _ => { })).ToList();
            var g = new NavigatorGroup(s, key, key, nodes.Select(n => new NavigatorMember(n.Node, "")).ToList(), 0);
            return new NavigatorGroupViewModel(g, nodes, nodes.Select(_ => "").ToList(), _ => { }, _ => { });
        }
        var roles = new NavigatorSectionViewModel(NavigatorSection.Roles, "Roles", new[] { Group(NavigatorSection.Roles, "A", 3), Group(NavigatorSection.Roles, "B", NavigatorGroupViewModel.MaxListedMembers + 2) }, true);
        var empty = new NavigatorSectionViewModel(NavigatorSection.DataGaps, "Data gaps", Array.Empty<NavigatorGroupViewModel>(), false);
        var sections = new[] { roles, empty };
        NavigatorSectionViewModel.Link(sections);

        sections.Select(s => (s.IsTopLevel, s.IsFirst, s.IsLast)).Should().Equal((true, true, false), (true, false, true));
        var groups = roles.Children.Cast<NavigatorGroupViewModel>().ToList();
        groups.Select(g => (g.IsTopLevel, g.IsFirst, g.IsLast)).Should().Equal((false, true, false), (false, false, true));
        var a = groups[0].Children;
        a.Should().HaveCount(3);
        a.Select(m => (m.IsTopLevel, m.IsFirst, m.IsLast)).Should().Equal((false, true, false), (false, false, false), (false, false, true));
        // the "more" line is the disabled last child
        var b = groups[1].Children;
        b.Should().HaveCount(NavigatorGroupViewModel.MaxListedMembers + 1);
        b[^1].IsNote.Should().BeTrue();
        b[^1].IsLast.Should().BeTrue();
        b[^2].IsLast.Should().BeFalse();
        // an empty section carries one disabled note
        empty.Children.Should().ContainSingle().Which.Should().Match<NavigatorTreeItem>(n => n.IsNote && n.IsFirst && n.IsLast);
        groups[0].Children[0].Children.Should().BeEmpty();
    }

    [Fact]
    public void Long_or_multiline_field_values_offer_more_and_toggle()
    {
        new ProcessDetailField("Command line", "cmd /c dir", id: "CmdLine").IsLong.Should().BeFalse();
        new ProcessDetailField("Command line", ProcessDetailField.None, id: "CmdLine").IsLong.Should().BeFalse();
        new ProcessDetailField("Command line", new string('x', ProcessDetailField.LongLength + 1), id: "CmdLine").IsLong.Should().BeTrue();
        var multi = new ProcessDetailField("Path", "a\nb", id: "Path");
        multi.IsLong.Should().BeTrue();
        multi.IsExpanded.Should().BeFalse();
        multi.MoreText.Should().Be("more…");
        multi.ToggleExpandedCommand.Execute(null);
        multi.IsExpanded.Should().BeTrue();
        multi.MoreText.Should().Be("less");
    }

    [Fact]
    public async Task Trail_follows_the_origin_up_through_selecting_and_expand_up_and_never_down()
    {
        var vm = await LoadAsync();
        var raised = 0;
        vm.TrailChanged += () => raised++;
        vm.OpenPath(Node(vm, 20));
        vm.TrailNodes.Select(n => n.Pid).Should().Equal(20);
        vm.SelectNode(Node(vm, 11));
        vm.TrailNodes.Select(n => n.Pid).Should().Equal(20, 11);
        Node(vm, 11).ExpandUpCommand.Execute(null);
        vm.TrailNodes.Select(n => n.Pid).Should().Equal(20, 11, 1);
        vm.SelectNode(Node(vm, 20));
        vm.TrailNodes.Select(n => n.Pid).Should().Equal(20, 11, 1);
        raised.Should().BeGreaterThan(0);
        vm.OpenPath(Node(vm, 11));
        vm.TrailNodes.Select(n => n.Pid).Should().Equal(11);
        vm.ShowWholeTreeCommand.Execute(null);
        vm.TrailNodes.Should().BeEmpty();
    }
}
