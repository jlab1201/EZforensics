using System.Diagnostics;
using EzForensics.Core.Execution;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ExecutionProcessRunnerTests
{
    private static Task<(ProcessResult res, List<(bool err, string line)> lines)> Run(string[] args, TimeSpan? timeout = null, CancellationToken ct = default)
        => RunCore(args, timeout ?? TimeSpan.FromSeconds(30), ct);

    private static async Task<(ProcessResult, List<(bool, string)>)> RunCore(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var lines = new List<(bool, string)>();
        var res = await ProcessRunner.RunAsync(FakeToolPaths.Exe, args, FakeToolPaths.Dir, (e, l) => lines.Add((e, l)), timeout, ct);
        return (res, lines);
    }

    [Fact]
    public async Task Output_arrives_in_order_and_exit_code_is_returned()
    {
        var (res, lines) = await Run(new[] { "--lines", "50", "--exit", "3" });
        res.ExitCode.Should().Be(3);
        res.TimedOut.Should().BeFalse();
        lines.Select(l => l.err).Should().AllBeEquivalentTo(false);
        lines.Select(l => l.line).Should().Equal(Enumerable.Range(1, 50).Select(i => $"line {i}"));
    }

    [Fact]
    public async Task Stderr_is_flagged()
    {
        var (_, lines) = await Run(new[] { "--lines", "4", "--stderr" });
        lines.Where(l => l.err).Select(l => l.line).Should().Equal("err 2", "err 4");
    }

    [Fact]
    public async Task Timeout_sets_TimedOut()
    {
        var (res, _) = await Run(new[] { "--hang" }, TimeSpan.FromMilliseconds(800));
        res.TimedOut.Should().BeTrue();
        res.Cancelled.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_kills_the_process_tree()
    {
        using var cts = new CancellationTokenSource();
        int? childPid = null;
        var lines = new List<string>();
        var task = ProcessRunner.RunAsync(FakeToolPaths.Exe, new[] { "--spawn-child" }, FakeToolPaths.Dir, (_, l) =>
        {
            lines.Add(l);
            if (l.StartsWith("CHILD:")) { childPid = int.Parse(l[6..]); cts.Cancel(); }
        }, TimeSpan.FromSeconds(30), cts.Token);
        var res = await task;
        res.Cancelled.Should().BeTrue();
        childPid.Should().NotBeNull();
        await Task.Delay(500);
        var alive = true;
        try { using var p = Process.GetProcessById(childPid!.Value); alive = !p.HasExited; }
        catch (ArgumentException) { alive = false; }
        alive.Should().BeFalse("the child process must be killed with the tree");
    }

    [Fact]
    public async Task Unicode_survives()
    {
        var (_, lines) = await Run(new[] { "--unicode" });
        lines.Should().Contain(l => l.line == "ünïcødé ✓");
    }

    [Fact]
    public void Ansi_is_stripped()
    {
        ProcessRunner.StripAnsi("\x1B[32mgreen\x1B[0m text").Should().Be("green text");
    }
}
