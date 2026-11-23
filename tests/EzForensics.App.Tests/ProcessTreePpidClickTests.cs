using EzForensics.App.ViewModels;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>A PPID click goes straight to the parent (resolved edge, or the one instance alive at the child's start); the chooser only shows for real ambiguity.</summary>
public sealed class ProcessTreePpidClickTests
{
    private static DateTime T(int s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    private static async Task<(ProcessTreeViewModel Vm, ProcessNode Child)> LoadAsync(Action<ProcessGraph, ProcessNode> build)
    {
        var g = new ProcessGraph();
        var child = new ProcessNode { Pid = 50, Ppid = 7, Name = "child.exe", CreateTime = T(100) };
        g.AllNodes.Add(child);
        build(g, child);
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        await vm.ShowGraphAsync(g, CancellationToken.None);
        return (vm, child);
    }

    private static ProcessNode Add(ProcessGraph g, int pid, int create, int? exit, ProcessNode? parent = null)
    {
        var n = new ProcessNode { Pid = pid, Name = $"p{pid}_{create}.exe", CreateTime = T(create), ExitTime = exit is { } e ? T(e) : null, Parent = parent };
        parent?.Children.Add(n);
        g.AllNodes.Add(n);
        if (parent == null) g.Roots.Add(n);
        return n;
    }

    [Fact]
    public async Task Parent_edge_goes_straight_to_the_parent_without_a_chooser()
    {
        var (vm, child) = await LoadAsync((g, c) =>
        {
            var wrong = Add(g, 7, 10, 20);                    // an earlier instance of PID 7 that exited
            var parent = Add(g, 7, 50, null);                 // the real parent (the drawn edge)
            c.Parent = parent; parent.Children.Add(c);
        });
        vm.SelectPid(7, null, null, null, child).Should().Be(PidResolutionKind.Found);
        vm.HasPidChooser.Should().BeFalse();
        vm.Selected!.Node.Should().BeSameAs(child.Parent);
    }

    [Fact]
    public async Task Pid_reuse_with_one_instance_alive_at_the_child_start_goes_direct()
    {
        var (vm, child) = await LoadAsync((g, _) =>
        {
            Add(g, 7, 10, 20);                                // exited before the child started
            Add(g, 7, 30, null);                              // alive at 100
            Add(g, 7, 200, null);                             // started after the child
        });
        vm.SelectPid(7, null, null, null, child).Should().Be(PidResolutionKind.Found);
        vm.HasPidChooser.Should().BeFalse();
        vm.Selected!.Node.CreateTime.Should().Be(T(30));
    }

    [Fact]
    public async Task Two_instances_alive_at_the_child_start_show_the_chooser_with_times_and_the_likely_mark()
    {
        var (vm, child) = await LoadAsync((g, _) =>
        {
            Add(g, 7, 30, null);
            Add(g, 7, 60, 150);
        });
        vm.SelectPid(7, null, null, null, child).Should().Be(PidResolutionKind.Ambiguous);
        vm.HasPidChooser.Should().BeTrue();
        vm.PidCandidates.Should().HaveCount(2);
        vm.PidCandidates.Should().OnlyContain(c => c.IsLikely && c.Text.Contains("exited") && c.Text.Contains("started"));
        vm.PidCandidates.Count(c => c.Text.Contains("most likely")).Should().Be(2);
    }

    [Fact]
    public async Task Plain_pid_click_with_several_instances_still_asks()
    {
        var (vm, _) = await LoadAsync((g, _) => { Add(g, 7, 10, 20); Add(g, 7, 30, null); });
        vm.SelectPid(7, null, null, null).Should().Be(PidResolutionKind.Ambiguous);
        vm.PidCandidates.Should().HaveCount(2);
    }

    [Fact]
    public void Row_child_pid_pairs_the_parent_column_with_the_own_pid_column()
    {
        PidLinks.RowChildPid(new[] { "PID", "PPID" }, new[] { "50", "7" }, 7).Should().Be(50);
        PidLinks.RowChildPid(new[] { "PID", "PPID" }, new[] { "50", "7" }, 50).Should().BeNull();
        PidLinks.RowChildPid(new[] { "PID" }, new[] { "50" }, 50).Should().BeNull();
    }
}
