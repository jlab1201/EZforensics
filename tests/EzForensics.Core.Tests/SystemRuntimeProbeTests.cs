using System.Diagnostics;
using EzForensics.Core.Runtimes;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class SystemRuntimeProbeTests
{
    [Fact]
    public void Run_returns_when_a_left_over_child_keeps_the_output_pipe_open()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        // cmd exits at once (exit 0); the background ping inherits its stdout pipe and keeps it open for ~15 s.
        var sw = Stopwatch.StartNew();
        var output = SystemRuntimeProbe.Instance.Run(cmd, new[] { "/c", "start /b ping -n 15 127.0.0.1 >nul & echo started" }, TimeSpan.FromSeconds(60));
        sw.Stop();
        output.Should().NotBeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Run_returns_the_output_of_a_normal_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        SystemRuntimeProbe.Instance.Run(cmd, new[] { "/c", "echo hello" }, TimeSpan.FromSeconds(30)).Should().Contain("hello");
        SystemRuntimeProbe.Instance.Run(cmd, new[] { "/c", "exit 3" }, TimeSpan.FromSeconds(30)).Should().BeNull();
    }
}
