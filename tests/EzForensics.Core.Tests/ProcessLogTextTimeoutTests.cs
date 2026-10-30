using System.Diagnostics;
using EzForensics.Core.Processes;
using Xunit;

namespace EzForensics.Core.Tests;

public class ProcessLogTextTimeoutTests
{
    [Fact]
    public void Hostile_cells_do_not_hang_or_throw()
    {
        var sw = Stopwatch.StartNew();
        var spaces = new string(' ', 300_000) + "x";
        var pids = string.Concat(Enumerable.Repeat("(PID: ", 100_000));
        ProcessLogText.KeyValues(spaces, pids);
        ProcessLogText.SplitTrailingPid(pids);
        ProcessLogText.StripLabel(new string('a', 100_000));
        ProcessLogText.ExePath(string.Concat(Enumerable.Repeat("a.exe", 50_000)) + "!");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), sw.Elapsed.ToString());
    }

    [Fact]
    public void Normal_cells_still_parse()
    {
        var kv = ProcessLogText.KeyValues("Parent Process: C:\\x.exe, PID: 0x1a");
        Assert.Equal(26, ProcessLogText.ParsePid(kv["pid"]));
        Assert.Equal(("proc", (int?)420), ProcessLogText.SplitTrailingPid("proc (PID: 420)"));
        Assert.Equal(@"C:\Windows\a.exe", ProcessLogText.ExpandEnvironmentTokens("%windir%\\a.exe"));
    }
}
