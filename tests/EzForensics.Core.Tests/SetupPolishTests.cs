using System.Text;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Setup wizard polish: weighted / creeping progress, elapsed time, WSL import fallback and wsl.exe output decoding.</summary>
public class SetupProgressTests
{
    private sealed class Exec : ISetupExecutor
    {
        public Func<SetupStepContext, SetupStepResult> Body = _ => SetupStepResult.Ok("ok");
        public Task<SetupStepResult> ExecuteAsync(SetupStepContext c, CancellationToken ct) => Task.FromResult(Body(c));
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T>
    {
        public void Report(T value) => a(value);
    }

    private static SetupStep S(string id, int seconds) => new() { Id = id, Kind = SetupStepKind.DownloadEzTools, Title = id, EstimatedSeconds = seconds };

    [Fact]
    public async Task Overall_progress_is_weighted_by_the_estimated_duration()
    {
        var plan = new SetupPlan { Selection = new SetupSelection(), Steps = new[] { S("small", 10), S("big", 90) } };
        var reports = new List<SetupRunProgress>();
        await new SetupRunner(new Exec()).RunAsync(plan, new SyncProgress<SetupRunProgress>(reports.Add));
        // after the small step: 10 of 100 s, not 1 of 2 steps
        reports.First(r => r.StepsFinished == 1).Fraction.Should().BeApproximately(0.1, 0.001);
        reports.Last().Fraction.Should().Be(1);
    }

    [Fact]
    public async Task Log_lines_move_the_bar_for_steps_that_report_no_fraction()
    {
        var plan = new SetupPlan { Selection = new SetupSelection(), Steps = new[] { S("pip", 100) } };
        var reports = new List<double>();
        var exec = new Exec
        {
            Body = c =>
            {
                for (int i = 0; i < 40; i++) c.Log("Collecting package " + i);
                return SetupStepResult.Ok("ok");
            },
        };
        await new SetupRunner(exec).RunAsync(plan, new SyncProgress<SetupRunProgress>(r => reports.Add(r.Fraction)));
        var during = reports.Take(reports.Count - 1).ToList();
        during.Max().Should().BeGreaterThan(0.4).And.BeLessThan(StepProgressEstimate.CreepCap);
        during.Should().BeInAscendingOrder("the bar never goes backwards");
    }

    [Fact]
    public async Task Step_bar_creeps_for_quiet_steps_and_never_reads_full_while_running()
    {
        var plan = new SetupPlan { Selection = new SetupSelection(), Steps = new[] { S("pip", 100), S("download", 100) } };
        var whileRunning = new Dictionary<string, List<double?>> { ["pip"] = new(), ["download"] = new() };
        foreach (var s in plan.Steps)
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SetupStep.Progress) && s.State == SetupStepState.Running) whileRunning[s.Id].Add(s.Progress);
            };
        var exec = new Exec
        {
            Body = c =>
            {
                if (c.Step.Id == "pip")
                    for (int i = 0; i < 200; i++) c.Log("Collecting package " + i);
                else
                {
                    c.Report(0.25);
                    for (int i = 0; i < 50; i++) c.Log("line " + i);
                    c.Report(1); // the last file arrived; the step still rescans afterwards
                }
                return SetupStepResult.Ok("ok");
            },
        };
        await new SetupRunner(exec).RunAsync(plan);

        var pip = whileRunning["pip"];
        pip.Should().NotBeEmpty("the estimate is published into the step's own bar");
        pip.Should().OnlyContain(p => p != null && p > 0 && p <= StepProgressEstimate.CreepCap);
        pip.Should().BeInAscendingOrder();
        pip.Max().Should().BeGreaterThan(0.5);
        whileRunning["download"].Should().Equal(new double?[] { 0.25, SetupStepContext.MaxRunningProgress }, "a real fraction is kept, never 1 while running");
        plan.Steps.Should().OnlyContain(s => s.State == SetupStepState.Done && s.Progress == 1);
    }

    [Fact]
    public void Estimate_follows_reported_fractions_creeps_between_them_and_never_goes_back()
    {
        var e = new StepProgressEstimate(60);
        e.Value.Should().Be(0);
        e.Elapsed(TimeSpan.FromSeconds(60));
        e.Value.Should().BeApproximately(StepProgressEstimate.CreepCap * (1 - Math.Exp(-1)), 0.001, "63% of the gap after the estimated duration");
        var before = e.Value;
        e.Report(0.2, TimeSpan.FromSeconds(60)); // a real fraction below the creep: the bar does not jump back
        e.Value.Should().Be(before);
        e.Report(0.8, TimeSpan.FromSeconds(61));
        e.Value.Should().Be(0.8);
        for (int i = 0; i < 1000; i++) e.LogLine();
        e.Value.Should().BeLessThanOrEqualTo(StepProgressEstimate.CreepCap + 1e-9).And.BeGreaterThan(0.8);
        e.Report(null, TimeSpan.FromSeconds(62));
        e.Value.Should().BeGreaterThan(0.8, "an indeterminate report changes nothing");
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(7, "00:07")]
    [InlineData(247, "04:07")]
    [InlineData(3599, "59:59")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "00:00")]
    public void Elapsed_is_mm_ss_then_h_mm_ss(int seconds, string expected) =>
        SetupPlan.FormatElapsed(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}

public class WslImportFallbackTests
{
    [Fact]
    public async Task Wsl2_success_needs_no_fallback()
    {
        var versions = new List<int>();
        int cleanups = 0;
        var r = await EzDistro.ImportWithFallbackAsync((v, _) => { versions.Add(v); return Task.FromResult(new WslImportAttempt(true, "")); }, () => cleanups++, _ => { });
        r.Should().Be(new WslImportOutcome(true, 2, r.Message));
        versions.Should().Equal(2);
        cleanups.Should().Be(0);
    }

    [Fact]
    public async Task Wsl2_failure_cleans_up_and_retries_with_wsl1_logging_why()
    {
        var versions = new List<int>();
        var log = new List<string>();
        int cleanups = 0;
        var r = await EzDistro.ImportWithFallbackAsync((v, _) =>
        {
            versions.Add(v);
            return Task.FromResult(v == 2 ? new WslImportAttempt(false, "Exited with code -1: HCS_E_HYPERV_NOT_INSTALLED") : new WslImportAttempt(true, ""));
        }, () => cleanups++, log.Add);
        r.Ok.Should().BeTrue();
        r.WslVersion.Should().Be(1);
        versions.Should().Equal(2, 1);
        cleanups.Should().Be(1, "the partial WSL 2 install is removed before the retry");
        log.Should().Contain(l => l.Contains("HCS_E_HYPERV_NOT_INSTALLED"));
        log.Should().Contain(l => l.Contains("WSL 1") && l.Contains("because WSL 2 could not be used"));
    }

    [Fact]
    public async Task Both_failing_reports_both_reasons()
    {
        int cleanups = 0;
        var r = await EzDistro.ImportWithFallbackAsync((v, _) => Task.FromResult(new WslImportAttempt(false, $"reason {v}")), () => cleanups++, _ => { });
        r.Ok.Should().BeFalse();
        r.Message.Should().Contain("reason 2").And.Contain("reason 1");
        cleanups.Should().Be(2);
    }

    [Fact]
    public async Task A_cancelled_import_is_not_retried()
    {
        var versions = new List<int>();
        var r = await EzDistro.ImportWithFallbackAsync((v, _) => { versions.Add(v); return Task.FromResult(new WslImportAttempt(false, "Cancelled by user", Cancelled: true)); }, () => { }, _ => { });
        r.Ok.Should().BeFalse();
        versions.Should().Equal(2);
    }

    [Fact]
    public void Import_command_line_can_ask_for_wsl1()
    {
        EzDistro.ImportArgs(@"C:\d", @"C:\r.tar.gz", 1).Should().EndWith(new[] { "--version", "1" });
        EzDistro.ImportArgs(@"C:\d", @"C:\r.tar.gz").Should().EndWith(new[] { "--version", "2" });
        EzDistro.ImportStep(@"C:\Windows\System32\wsl.exe", @"C:\d", @"C:\r.tar.gz", 1).Label.Should().Contain("WSL 1");
    }

    [Fact]
    public void Failure_reason_includes_what_wsl_printed()
    {
        var log = new[] { "# EzHub job 1", "# Command: wsl.exe --import ...", "Import in progress, this may take a few minutes.", "",
            "W\0S\0L\0 \0e\0r\0r\0", "Error code: Wsl/Service/RegisterDistro/CreateVm/HCS/HCS_E_HYPERV_NOT_INSTALLED", "# Exit code: -1" };
        var reason = EzDistro.DescribeFailure("Exited with code -1", log);
        reason.Should().StartWith("Exited with code -1. wsl.exe: ");
        reason.Should().Contain("WSL err").And.Contain("HCS_E_HYPERV_NOT_INSTALLED").And.NotContain("#").And.NotContain("\0");
        EzDistro.DescribeFailure("Exited with code -1: Error code: X", new[] { "Error code: X" }).Should().Be("Exited with code -1: Error code: X");
        EzDistro.DescribeFailure(null, null).Should().Be("it failed");
    }

    [Fact]
    public void Summary_shows_the_wsl_version()
    {
        new EzDistroInfo(true, true, 1, "Stopped", true, "ubuntu-base 24.04.5 / schema 1", @"C:\d", null, "wsl.exe").Summary
            .Should().Be("Installed (ubuntu-base 24.04.5 / schema 1, WSL 1)");
        new EzDistroInfo(true, true, 2, "Stopped", true, "u", @"C:\d", 300L << 20, "wsl.exe").Summary.Should().Be("Installed (u, WSL 2, 300 MB)");
    }
}

public class WslOutputEncodingTests
{
    /// <summary>A stream that hands out one byte per read (the sniffing decoder must buffer).</summary>
    private sealed class Trickle(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0) return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private const string Text = "The operation could not be started because a required feature is not installed.\r\nError code: Wsl/Service/CreateInstance/0x80370114\r\n";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Utf16le_and_utf8_output_both_decode(bool utf16, bool trickle)
    {
        var bytes = (utf16 ? new UnicodeEncoding(false, false) : (Encoding)new UTF8Encoding(false)).GetBytes(Text + "é");
        using var r = new StreamReader(trickle ? new Trickle(bytes) : new MemoryStream(bytes), WslOutputEncoding.Instance, detectEncodingFromByteOrderMarks: true);
        r.ReadToEnd().Should().Be(Text + "é");
    }

    [Fact]
    public void Detection_rules()
    {
        WslOutputEncoding.LooksLikeUtf16Le(new byte[] { (byte)'W', 0 }).Should().BeTrue();
        WslOutputEncoding.LooksLikeUtf16Le(new byte[] { 0xFF, 0xFE }).Should().BeTrue();
        WslOutputEncoding.LooksLikeUtf16Le("Wsl"u8).Should().BeFalse();
        WslOutputEncoding.LooksLikeUtf16Le(ReadOnlySpan<byte>.Empty).Should().BeNull();
        WslOutputEncoding.Decode(Encoding.Unicode.GetBytes("Error")).Should().Be("Error");
        WslOutputEncoding.IsWslExe(@"C:\Windows\System32\WSL.EXE").Should().BeTrue();
        WslOutputEncoding.IsWslExe(@"C:\x\wslhost.exe").Should().BeFalse();
    }
}
