using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Follow-ups of the memory E2E run: wait state, progress log throttle, CSV record counts, unsupported dump types.</summary>
public class MemoryFollowUpTests
{
    private static JobQueue NewQueue(int parallel)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new JobQueue(new FakeLocator(FakeToolPaths.Exe), parallel, TimeSpan.FromSeconds(60)); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    private static JobSpec Spec(string dir, string id, Func<JobPostProcessContext, CancellationToken, Task>? pre = null, bool outside = false) =>
        new(id, "EzForensics.FakeTool.exe", new[] { "--lines", "1" }, Path.Combine(dir, "out", id), id, "Test", PreProcess: pre, PrepareOutsideSlot: outside);

    // ---- wait state

    [Fact]
    public async Task Job_blocked_in_preprocess_shows_waiting_frees_its_slot_and_starts_its_clock_afterwards()
    {
        using var t = new TempDir();
        var q = NewQueue(1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = q.Enqueue(Spec(t.Path, "W", (_, ct) => release.Task.WaitAsync(ct), outside: true), t.Path);
        var other = q.Enqueue(Spec(t.Path, "O"), t.Path);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !other.IsFinished) await Task.Delay(25);
        other.State.Should().Be(JobState.Succeeded, "the waiting job does not hold the only queue slot");
        waiting.State.Should().Be(JobState.Queued);
        waiting.StatusReason.Should().Be("Waiting…");
        waiting.StartUtc.Should().BeNull();

        await Task.Delay(300);
        var releasedAt = DateTime.UtcNow;
        release.SetResult();
        await q.WhenAllAsync();

        waiting.State.Should().Be(JobState.Succeeded, waiting.StatusReason);
        waiting.StartUtc.Should().BeOnOrAfter(releasedAt, "the duration is measured from the real process start");
        waiting.StatusReason.Should().NotBe("Waiting…");
        File.ReadAllText(waiting.LogPath!).Should().Contain("# Started (UTC):");
    }

    [Fact]
    public async Task Cancelling_a_waiting_job_does_not_leak_a_queue_slot()
    {
        using var t = new TempDir();
        var q = NewQueue(1);
        var never = new TaskCompletionSource();
        var waiting = q.Enqueue(Spec(t.Path, "W", (_, ct) => never.Task.WaitAsync(ct), outside: true), t.Path);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && waiting.StatusReason != "Waiting…") await Task.Delay(25);
        waiting.Cancel();
        var next = q.Enqueue(Spec(t.Path, "N"), t.Path);
        await q.WhenAllAsync();
        waiting.State.Should().Be(JobState.Cancelled);
        next.State.Should().Be(JobState.Succeeded);
    }

    [Fact]
    public async Task Preprocess_stays_inside_the_slot_for_other_jobs()
    {
        using var t = new TempDir();
        var q = NewQueue(1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = q.Enqueue(Spec(t.Path, "A", (_, ct) => release.Task.WaitAsync(ct)), t.Path);
        var b = q.Enqueue(Spec(t.Path, "B"), t.Path);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && a.State != JobState.Running) await Task.Delay(25);
        a.State.Should().Be(JobState.Running);
        await Task.Delay(300);
        b.State.Should().Be(JobState.Queued);
        release.SetResult();
        await q.WhenAllAsync();
        b.State.Should().Be(JobState.Succeeded);
    }

    // ---- progress throttle

    [Fact]
    public void Progress_lines_are_logged_on_stage_change_five_percent_steps_or_two_seconds()
    {
        var now = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var th = new VolatilityProgressThrottle(() => now);
        th.ShouldLog("Progress:  0.00\t\tReading Symbol layer").Should().BeTrue("first line");
        th.ShouldLog("Progress:  0.40\t\tReading Symbol layer").Should().BeFalse();
        th.ShouldLog("Progress:  4.99\t\tReading Symbol layer").Should().BeFalse();
        th.ShouldLog("Progress:  5.00\t\tReading Symbol layer").Should().BeTrue("advanced by 5");
        th.ShouldLog("Progress:  5.10\t\tScanning layer").Should().BeTrue("stage changed");
        th.ShouldLog("Progress:  5.20\t\tScanning layer").Should().BeFalse();
        now += TimeSpan.FromSeconds(2.5);
        th.ShouldLog("Progress:  5.30\t\tScanning layer").Should().BeTrue("2 s since the last written line");
        th.ShouldLog("Progress: 100.00\t\tScanning layer").Should().BeTrue("the end");
        th.ShouldLog("Progress:  1.00\t\tScanning layer").Should().BeTrue("percent went back (a new pass)");
    }

    [Fact]
    public void Other_lines_pass_and_blank_lines_never_do()
    {
        var th = new VolatilityProgressThrottle();
        th.ShouldLog("Unsatisfied requirement plugins.PsList.kernel").Should().BeTrue();
        th.ShouldLog("").Should().BeFalse();
        th.ShouldLog("   ").Should().BeFalse();
        VolatilityProgressThrottle.IsBlank(null).Should().BeTrue();
    }

    [Fact]
    public void Tens_of_thousands_of_progress_lines_produce_a_short_log()
    {
        var th = new VolatilityProgressThrottle(() => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        int kept = 0;
        for (int i = 0; i < 70000; i++)
            if (th.ShouldLog($"Progress: {(i / 700.0):0.00}\t\tReading Symbol layer")) kept++;
        kept.Should().BeLessThan(40);
    }

    // ---- CSV records

    [Fact]
    public void Csv_rows_are_records_not_lines_quoted_line_breaks_and_quotes_included()
    {
        using var d = new TempDir();
        var csv = Path.Combine(d.Path, "malfind.csv");
        File.WriteAllText(csv,
            "PID,Hexdump,Note\r\n" +
            "1,\"4d 5a 90\r\n00 03 00\r\n04 00 00\",ok\r\n" +
            "2,\"a \"\"quoted\"\" word\nline two\",\"x,y\"\r\n" +
            "3,plain,\r\n");
        JobOutcomeClassifier.CountCsvRows(csv).Should().Be(3);
    }

    [Fact]
    public void Csv_count_handles_header_only_blank_lines_and_no_final_newline()
    {
        using var d = new TempDir();
        var a = Path.Combine(d.Path, "a.csv"); File.WriteAllText(a, "h1,h2\r\n");
        var b = Path.Combine(d.Path, "b.csv"); File.WriteAllText(b, "h1,h2\r\n1,\"x\r\ny\"");
        var c = Path.Combine(d.Path, "c.csv"); File.WriteAllText(c, "h\n1\n\n2\n\n");
        var e = Path.Combine(d.Path, "e.csv"); File.WriteAllBytes(e, Array.Empty<byte>());
        JobOutcomeClassifier.CountCsvRows(a).Should().Be(0);
        JobOutcomeClassifier.CountCsvRows(b).Should().Be(1);
        JobOutcomeClassifier.CountCsvRows(c).Should().Be(2);
        JobOutcomeClassifier.CountCsvRows(e).Should().Be(0);
    }

    [Fact]
    public void Csv_count_is_correct_across_read_buffer_boundaries_and_fast_on_large_files()
    {
        using var d = new TempDir();
        var csv = Path.Combine(d.Path, "big.csv");
        const int rows = 300_000;
        using (var w = new StreamWriter(csv, false, new UTF8Encoding(false)))
        {
            w.Write("A,B\r\n");
            for (int i = 0; i < rows; i++) w.Write($"{i},\"multi\r\nline \"\"{i}\"\"\"\r\n");
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        JobOutcomeClassifier.CountCsvRows(csv).Should().Be(rows);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // ---- cancelled before any rows

    [Fact]
    public void Header_only_partials_are_dropped_and_partials_with_rows_are_kept()
    {
        using var d = new TempDir();
        var empty = Path.Combine(d.Path, "PsScan.csv");
        File.WriteAllText(empty, "Volatility 3 Framework 2.28.2\r\n\r\nTreeDepth,PID,PPID\r\n");
        var bannerOnly = Path.Combine(d.Path, "Banner.csv");
        File.WriteAllText(bannerOnly, "Volatility 3 Framework 2.28.2\r\n");
        var rows = Path.Combine(d.Path, "PsList.csv");
        File.WriteAllText(rows, "Volatility 3 Framework 2.28.2\r\n\r\nTreeDepth,PID,PPID\r\n0,4,0\r\n");
        var log = new List<string>();

        var dropped = MemoryJobs.DropEmptyPartialCsvs(new[] { empty, bannerOnly, rows }, log.Add);

        dropped.Should().BeEquivalentTo(new[] { empty, bannerOnly });
        File.Exists(empty).Should().BeFalse();
        File.Exists(bannerOnly).Should().BeFalse();
        File.Exists(rows).Should().BeTrue();
        log.Should().Contain(l => l.Contains("cancelled before any rows were produced") && l.Contains("PsScan.csv"));
    }

    // ---- unsupported dump types

    private static MemoryImage Dump(DumpKind kind, string? note = null) =>
        new(Path.Combine(Path.GetTempPath(), "x.dmp"), 4096, MemoryFormat.WindowsCrashDump, MemoryOs.Windows, kind, 1, note);

    [Theory]
    [InlineData(DumpKind.Kernel)]
    [InlineData(DumpKind.Active)]
    public void Kernel_and_active_dumps_plan_no_volatility_jobs_and_say_why(DumpKind kind)
    {
        using var t = new TempDir();
        var notes = new List<string>();
        var install = new VolatilityInstall(FakeToolPaths.Exe, "python.exe", "2.28.2", t.Path, Path.Combine(t.Path, "symbols"), Path.Combine(t.Path, "cache"));
        var plan = MemoryTriage.Plan(new[] { Dump(kind) }, Path.Combine(t.Path, "case"), install, notes: notes, hostHint: (_, _) => null);
        plan.Should().BeEmpty();
        notes.Should().Contain(n => n.Contains("Volatility 3 cannot read this dump type (kernel/active memory dump). Re-acquire a full or bitmap dump, or a raw image."));
    }

    [Theory]
    [InlineData(DumpKind.Full)]
    [InlineData(DumpKind.Bitmap)]
    [InlineData(DumpKind.BitmapFull)]
    [InlineData(DumpKind.BitmapKernel)]
    public void Full_bitmap_and_bitmap_kernel_dumps_are_still_planned(DumpKind kind)
    {
        var plugins = MemoryTriage.PluginsFor(Dump(kind), null, false, out var note);
        plugins.Should().NotBeEmpty();
        note.Should().BeNull();
    }
}
