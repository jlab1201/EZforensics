using EzForensics.Core.Execution;
using EzForensics.Core.Settings;
using EzForensics.Core.Sysinternals;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Tool logging Debug (Options > Installation): the "# Debug:" lines every job writes, and their formatting.</summary>
public class ToolDebugLogTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData(@"C:\Temp\x.txt", @"C:\Temp\x.txt")]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\Program Files\a b", "\"C:\\Program Files\\a b\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void Arguments_are_quoted_like_createprocess_reads_them(string arg, string expected) =>
        ToolDebugLog.Quote(arg).Should().Be(expected);

    [Fact]
    public void Start_lines_name_the_command_folder_elevation_and_tool()
    {
        var tool = new ToolFileFacts(@"C:\Tools\EvtxECmd.exe", true, "14.3", 1234567, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "valid, CN=Microsoft Corporation");
        var lines = ToolDebugLog.StartLines(@"C:\Tools\EvtxECmd.exe", new[] { "-f", @"C:\Temp\EzForensics shadow\Windows", "-a", "*" }, @"C:\Tools", true, tool,
            new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" }, @"C:\case\out.txt").ToList();
        lines.Should().OnlyContain(l => l.StartsWith(ToolDebugLog.Prefix));
        lines[0].Should().Be(ToolDebugLog.Prefix + "command line: C:\\Tools\\EvtxECmd.exe -f \"C:\\Temp\\EzForensics shadow\\Windows\" -a *");
        lines[1].Should().Be(ToolDebugLog.Prefix + @"working directory: C:\Tools");
        lines[2].Should().Be(ToolDebugLog.Prefix + "elevated (Administrator): yes");
        lines[3].Should().Be(ToolDebugLog.Prefix + @"tool: C:\Tools\EvtxECmd.exe, version 14.3, 1,234,567 bytes, modified 2026-01-02 03:04:05 UTC, signature: valid, CN=Microsoft Corporation");
        lines[4].Should().Be(ToolDebugLog.Prefix + "environment: A=1, B=2");
        lines[5].Should().Contain(@"stdout is written to C:\case\out.txt");
        ToolDebugLog.FormatTool(new ToolFileFacts(@"C:\x.exe", false, null, 0, null, "-")).Should().Be(@"C:\x.exe (not found)");
    }

    [Fact]
    public void End_lines_give_exit_code_duration_and_output_tails()
    {
        var stdout = Enumerable.Range(1, 30).Select(i => "out " + i).ToList();
        var lines = ToolDebugLog.EndLines(-1, false, false, TimeSpan.FromMilliseconds(1534), stdout, Array.Empty<string>(), tail: 3).ToList();
        lines[0].Should().Be(ToolDebugLog.Prefix + "exit code -1 (0xFFFFFFFF), duration 1.534 s");
        lines[1].Should().Be(ToolDebugLog.Prefix + "stdout: last 3 of 30 line(s)");
        lines.Skip(2).Take(3).Should().Equal(ToolDebugLog.Prefix + "stdout| out 28", ToolDebugLog.Prefix + "stdout| out 29", ToolDebugLog.Prefix + "stdout| out 30");
        lines[5].Should().Be(ToolDebugLog.Prefix + "stderr: (empty)");
        ToolDebugLog.EndLines(0, true, false, TimeSpan.FromMinutes(61), Array.Empty<string>(), new[] { "e" }).First().Should().Contain("timed out").And.Contain("1h 01m 00s");
        ToolDebugLog.FormatDuration(TimeSpan.FromSeconds(75.5)).Should().Be("1m 15.5s");
    }

    [Fact]
    public void Inspect_reports_a_missing_tool_and_a_real_one()
    {
        ToolDebugLog.Inspect(@"C:\nope\none.exe").Exists.Should().BeFalse();
        var f = ToolDebugLog.Inspect(FakeToolPaths.Exe, new StubVerifier());
        f.Exists.Should().BeTrue();
        f.Size.Should().BeGreaterThan(0);
        f.Signature.Should().Be("not valid (unsigned test build)");
    }

    private sealed class StubVerifier : ISignatureVerifier
    {
        public SignatureCheck Verify(string file) => new(false, false, null, null, "unsigned test build");
    }

    [Fact]
    public void Tool_log_level_is_saved_with_the_settings()
    {
        using var t = new TempDir();
        var store = new JsonSettingsStore(Path.Combine(t.Path, "settings.json"));
        new AppSettings().ToolLogLevel.Should().Be(ToolLogLevel.Normal);
        store.Save(new AppSettings { ToolLogLevel = ToolLogLevel.Debug });
        store.Load().ToolLogLevel.Should().Be(ToolLogLevel.Debug);
        File.ReadAllText(store.Path).Should().Contain("\"toolLogLevel\": \"debug\"");
    }

    private static JobQueue NewQueue(ToolLogLevel level)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new JobQueue(new FakeLocator(FakeToolPaths.Exe), 1, TimeSpan.FromSeconds(60)) { LogLevel = level }; }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    [Theory]
    [InlineData(ToolLogLevel.Debug)]
    [InlineData(ToolLogLevel.Normal)]
    public async Task Every_job_writes_the_debug_lines_only_in_debug(ToolLogLevel level)
    {
        using var t = new TempDir();
        var q = NewQueue(level);
        bool? seen = null;
        var spec = new JobSpec("X", "EzForensics.FakeTool.exe", new[] { "--lines", "3", "--exit", "2" }, Path.Combine(t.Path, "out"), "x", "X",
            PostProcess: (ctx, _) => { seen = ctx.Debug; return Task.CompletedTask; });
        var job = q.Enqueue(spec, t.Path);
        await q.WhenAllAsync();
        var log = File.ReadAllText(job.LogPath!);
        job.DebugLogging.Should().Be(level == ToolLogLevel.Debug);
        seen.Should().Be(level == ToolLogLevel.Debug);
        if (level == ToolLogLevel.Normal) { log.Should().NotContain(ToolDebugLog.Prefix); return; }
        log.Should().Contain(ToolDebugLog.Prefix + "command line: ").And.Contain("--lines 3 --exit 2")
            .And.Contain(ToolDebugLog.Prefix + "working directory: ")
            .And.Contain(ToolDebugLog.Prefix + "elevated (Administrator): ")
            .And.Contain(ToolDebugLog.Prefix + "tool: " + FakeToolPaths.Exe)
            .And.Contain(ToolDebugLog.Prefix + "exit code 2 (0x00000002), duration ")
            .And.Contain(ToolDebugLog.Prefix + "stdout: last 3 of 3 line(s)")
            .And.Contain(ToolDebugLog.Prefix + "stderr: (empty)");
    }
}
