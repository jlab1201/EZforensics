using EzForensics.App.ViewModels;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>A same-name stack must stay one stack through any expand / collapse / click cycle.</summary>
public sealed class ProcessStackStabilityTests
{
    // p(1): svchost x3 (10, 11, 12), other.exe (13)
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
        var p = N(1, "p.exe", null);
        N(10, "svchost.exe", p); N(11, "svchost.exe", p); N(12, "svchost.exe", p); N(13, "other.exe", p);
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        await vm.ShowGraphAsync(g, CancellationToken.None);
        return vm;
    }

    private static IEnumerable<ProcessNodeViewModel> All(IEnumerable<ProcessNodeViewModel> nodes) => nodes.SelectMany(r => All(r.AllChildren).Prepend(r));
    private static IReadOnlyList<object> Row(ProcessTreeViewModel vm)
    {
        IProcessGraphSource s = vm;
        return s.LayoutChildren(s.LayoutRoots()[0]);
    }

    private static void AssertOneStackOfThree(ProcessTreeViewModel vm)
    {
        var row = Row(vm);
        row.OfType<ProcessStackViewModel>().Should().ContainSingle().Which.Count.Should().Be(3);
        row.OfType<ProcessNodeViewModel>().Select(n => n.Pid).Should().Equal(13);
    }

    [Fact]
    public async Task Stack_stays_x3_through_expand_collapse_and_member_clicks()
    {
        var vm = await LoadAsync();
        AssertOneStackOfThree(vm);
        foreach (var pick in new[] { 10, 11, 12, 13, 12, 10 })
        {
            Row(vm).OfType<ProcessStackViewModel>().Single().ToggleCommand.Execute(null);   // expand
            Row(vm).OfType<ProcessNodeViewModel>().Count(n => n.Pid != 13).Should().Be(3);
            vm.SelectNode(All(vm.VisibleRoots).First(n => n.Pid == pick));                 // click a member (or the other process)
            Row(vm).OfType<ProcessStackViewModel>().Single().ToggleCommand.Execute(null);   // collapse
            AssertOneStackOfThree(vm);
        }
    }
}
