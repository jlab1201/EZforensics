using EzForensics.Core.Diagnostics;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class ExitWatchdogTests
{
    [Fact]
    public void Logs_and_exits_with_the_code_when_the_grace_period_passes()
    {
        var done = new ManualResetEventSlim();
        var logs = new List<string>();
        int? code = null;
        using var w = ExitWatchdog.Start(TimeSpan.FromMilliseconds(50), logs.Add, c => { code = c; done.Set(); }, 7);
        done.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        code.Should().Be(7);
        logs.Should().ContainSingle().Which.Should().Contain("still alive");
    }

    [Fact]
    public void Does_not_exit_when_disposed_first()
    {
        var called = false;
        var w = ExitWatchdog.Start(TimeSpan.FromMilliseconds(300), _ => { }, _ => called = true, 0);
        w.Dispose();
        Thread.Sleep(600);
        called.Should().BeFalse();
    }
}
