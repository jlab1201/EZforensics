using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Memory plugin jobs never wait for ever on the symbol gate, and say what they wait for (status reason + log lines).
/// One test per path where the image's windows.info job might never decide the gate.
/// </summary>
public class MemoryGateSafeguardTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private sealed class Fx : IDisposable
    {
        public readonly TempDir Temp = new();
        public string Case => Path.Combine(Temp.Path, "case");
        public string OutputDir => Path.Combine(Case, "HOST1", "Memory");
        public string Staging => Path.Combine(Case, OutputPlan.StagingFolderName, "HOST1_memory_memory");
        public MemoryImage Image { get; }
        public VolatilityInstall Install { get; }
        public MemorySymbolGate Gate { get; } = new() { PollInterval = TimeSpan.FromMilliseconds(50), OrphanGrace = TimeSpan.FromMilliseconds(400) };

        public Fx()
        {
            Directory.CreateDirectory(Case);
            var img = Path.Combine(Temp.Path, "memory.dmp");
            File.WriteAllBytes(img, Encoding.ASCII.GetBytes("PAGEDU64" + new string('\0', 4088)));
            Image = new MemoryImage(img, new FileInfo(img).Length, MemoryFormat.WindowsCrashDump, MemoryOs.Windows, DumpKind.Full, 1000, null);
            Install = new VolatilityInstall(FakeToolPaths.Exe, "python.exe", "2.28.2", Temp.Path, Path.Combine(Temp.Path, "symbols"), Path.Combine(Temp.Path, "cache"));
        }

        public JobSpec Spec(string pluginId, MemorySymbolGate? gate = null, IReadOnlyDictionary<string, string>? env = null)
        {
            var spec = MemoryJobs.CreateSpec(Install, Image, MemoryPlugins.Find(pluginId)!, "HOST1", OutputDir, Staging, gate ?? Gate, offline: false);
            if (env == null) return spec;
            var merged = new Dictionary<string, string>(spec.Environment!);
            foreach (var (k, v) in env) merged[k] = v;
            return spec with { Environment = merged };
        }

        /// <summary>The info spec, held in its preparation step until <paramref name="release"/> completes (it stays queued and undecided).</summary>
        public JobSpec HeldInfo(Task release, Func<Task>? before = null)
        {
            var spec = Spec("info");
            var inner = spec.PreProcess!;
            return spec with
            {
                PreProcess = async (ctx, ct) =>
                {
                    if (before != null) await before();
                    await release.WaitAsync(ct);
                    await inner(ctx, ct);
                },
            };
        }

        public void Dispose() => Temp.Dispose();
    }

    private static JobQueue Queue(int parallel = 4)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new JobQueue(new FakeLocator(FakeToolPaths.Exe), parallel, TimeSpan.FromMinutes(2)); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    private static async Task Until(Func<bool> cond, string because)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            await Task.Delay(20);
        }
    }

    private static string Log(Job j) => File.ReadAllText(j.LogPath!);

    // ---- UX: waiting reasons and the "wait ended" log line

    [Fact]
    public async Task Waiting_plugin_says_what_it_waits_for_and_logs_when_the_wait_ends()
    {
        using var f = new Fx();
        var q = Queue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = q.Enqueue(f.HeldInfo(release.Task), f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);

        await Until(() => ps.StatusReason?.StartsWith(MemorySymbolGate.WaitingForInfoReason) == true, "the plugin shows the symbol-check wait");
        ps.State.Should().Be(JobState.Queued);
        ps.StatusReason.Should().Be(MemorySymbolGate.WaitingForInfoReason + ": windows.info is queued");
        release.SetResult();
        await q.WhenAllAsync().WaitAsync(Limit);

        info.State.Should().Be(JobState.Succeeded, info.StatusReason);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
        var log = Log(ps);
        log.Should().Contain("# Waiting for the windows.info job of this image (symbol check)…");
        log.Should().MatchRegex(@"# windows\.info finished — starting windows\.pslist \(waited \d+ s\)\.");
        log.IndexOf("starting windows.pslist", StringComparison.Ordinal).Should().BeLessThan(log.IndexOf("# Started (UTC):", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Waiting_plugin_shows_the_info_jobs_volatility_stage()
    {
        using var f = new Fx();
        var q = Queue();
        var info = q.Enqueue(f.Spec("info", env: new Dictionary<string, string> { ["FAKE_VOL_HANG"] = "1" }), f.Case);   // info stays running
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await Until(() => ps.StatusReason?.Contains("finding the kernel 100%") == true, "the info job's progress reaches the waiting plugin: " + ps.StatusReason);
        info.State.Should().Be(JobState.Running);

        info.Cancel();
        await q.WhenAllAsync().WaitAsync(Limit);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
        Log(ps).Should().Contain("# windows.info was cancelled; starting without the symbol check — starting windows.pslist (waited");
    }

    [Fact]
    public async Task Failed_symbol_check_is_logged_with_the_wait_time()
    {
        using var f = new Fx();
        var q = Queue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = f.HeldInfo(release.Task);
        info = info with { Environment = new Dictionary<string, string>(info.Environment!) { ["FAKE_VOL_NOSYMBOLS"] = "1" } };
        q.Enqueue(info, f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await Until(() => ps.StatusReason?.StartsWith(MemorySymbolGate.WaitingForInfoReason) == true, "waiting");
        release.SetResult();
        await q.WhenAllAsync().WaitAsync(Limit);
        ps.State.Should().Be(JobState.Failed);
        ps.StatusReason.Should().Contain("could not find the symbols");
        Log(ps).Should().MatchRegex(@"# Symbol check failed after waiting \d+ s: Volatility could not find the symbols");
    }

    [Fact]
    public async Task Job_that_finished_preparing_waits_for_a_queue_slot_with_its_own_reason()
    {
        using var t = new TempDir();
        var q = Queue(1);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobSpec S(string id, Func<JobPostProcessContext, CancellationToken, Task>? pre, bool outside) =>
            new(id, "EzForensics.FakeTool.exe", new[] { "--lines", "1" }, Path.Combine(t.Path, "out", id), id, "Test", PreProcess: pre, PrepareOutsideSlot: outside);
        var w = q.Enqueue(S("W", (_, ct) => go.Task.WaitAsync(ct), true), t.Path);
        await Until(() => w.StatusReason == JobQueue.WaitingReason, "waiting in preparation");
        var blocker = q.Enqueue(S("B", (_, ct) => hold.Task.WaitAsync(ct), false), t.Path);
        await Until(() => blocker.State == JobState.Running, "blocker holds the only slot");
        go.SetResult();
        await Until(() => w.StatusReason == JobQueue.WaitingForSlotReason, "waiting for a queue slot");
        w.State.Should().Be(JobState.Queued);
        hold.SetResult();
        await q.WhenAllAsync().WaitAsync(Limit);
        w.State.Should().Be(JobState.Succeeded, w.StatusReason);
    }

    // ---- hang audit: every way the info job might never decide the gate

    [Fact]
    public async Task No_info_job_queued_the_plugin_opens_the_gate_after_the_grace_and_runs()
    {
        using var f = new Fx();
        var q = Queue();
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);      // info planned but unticked / filtered out / skipped: never enqueued
        await q.WhenAllAsync().WaitAsync(Limit);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
        f.Gate.IsOpen.Should().BeTrue();
        f.Gate.DecisionNote.Should().Be(MemorySymbolGate.NoInfoJobNote);
        Log(ps).Should().Contain("# " + MemorySymbolGate.NoInfoJobNote + " — starting windows.pslist");
    }

    [Fact]
    public async Task Info_job_enqueued_after_the_plugin_is_still_waited_for()
    {
        using var f = new Fx();
        var q = Queue();
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await Task.Delay(100);                               // within the grace
        var info = q.Enqueue(f.Spec("info"), f.Case);
        await q.WhenAllAsync().WaitAsync(Limit);
        info.State.Should().Be(JobState.Succeeded, info.StatusReason);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
        f.Gate.DecisionNote.Should().Be("windows.info finished");
    }

    [Fact]
    public async Task Info_cancelled_while_still_queued_releases_the_plugins()
    {
        using var f = new Fx();
        using var t = new TempDir();
        var q = Queue(1);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = q.Enqueue(new JobSpec("B", "EzForensics.FakeTool.exe", new[] { "--lines", "1" }, Path.Combine(t.Path, "out"), "B", "Test",
            PreProcess: (_, ct) => hold.Task.WaitAsync(ct)), t.Path);
        await Until(() => blocker.State == JobState.Running, "blocker holds the only slot");
        var info = q.Enqueue(f.Spec("info"), f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        info.Cancel();                                       // never started
        await Until(() => info.IsFinished, "the info job ends");
        info.State.Should().Be(JobState.Cancelled);
        f.Gate.IsDecided.Should().BeTrue("the info job's Finally decides the gate even when it never started");
        hold.SetResult();
        await q.WhenAllAsync().WaitAsync(Limit);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
    }

    [Fact]
    public async Task Info_preparation_throwing_releases_the_plugins()
    {
        using var f = new Fx();
        var q = Queue();
        var info = q.Enqueue(f.HeldInfo(Task.CompletedTask, () => throw new IOException("boom")), f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await q.WhenAllAsync().WaitAsync(Limit);
        info.State.Should().Be(JobState.Failed);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
        f.Gate.DecisionNote.Should().StartWith("windows.info failed (Preparation failed: boom)");
    }

    [Fact]
    public async Task Info_with_volatility_missing_releases_the_plugins()
    {
        using var f = new Fx();
        var q = Queue();
        var info = q.Enqueue(f.Spec("info") with { ExePath = Path.Combine(f.Temp.Path, "gone", "vol.exe") }, f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await q.WhenAllAsync().WaitAsync(Limit);
        info.State.Should().Be(JobState.Failed);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
    }

    [Fact]
    public async Task Registered_info_job_that_ends_without_deciding_does_not_strand_the_plugins()
    {
        using var f = new Fx();
        using var t = new TempDir();
        var q = Queue();
        // Any job registered as the gate's info job that ends without opening or failing it (the "info job is gone" case).
        var fakeInfo = q.Enqueue(new JobSpec("X", "EzForensics.FakeTool.exe", new[] { "--lines", "1" }, Path.Combine(t.Path, "out"), "X", "Test",
            Enqueued: f.Gate.RegisterInfo), t.Path);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await q.WhenAllAsync().WaitAsync(Limit);
        fakeInfo.State.Should().Be(JobState.Succeeded);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
        f.Gate.DecisionNote.Should().Be(MemorySymbolGate.InfoEndedNote);
    }

    [Fact]
    public async Task Planned_info_left_out_of_the_run_does_not_hang_the_planned_plugin()
    {
        using var t = new TempDir();
        var img = Path.Combine(t.Path, "m.dmp");
        File.WriteAllBytes(img, Encoding.ASCII.GetBytes("PAGEDU64" + new string('\0', 4088)));
        var image = new MemoryImage(img, 4096, MemoryFormat.WindowsCrashDump, MemoryOs.Windows, DumpKind.Full, 1, null);
        var install = new VolatilityInstall(FakeToolPaths.Exe, "python.exe", "2.28.2", t.Path, Path.Combine(t.Path, "symbols"), Path.Combine(t.Path, "cache"));
        var plan = MemoryTriage.Plan(new[] { image }, Path.Combine(t.Path, "case"), install, selectedPluginIds: new[] { "info", "pslist" }, hostHint: (_, _) => null);
        plan.Should().HaveCount(2);
        var q = Queue();
        var ps = q.Enqueue(plan.Single(p => p.Spec.Provenance!.ArtifactId == "memory-pslist").Spec, Path.Combine(t.Path, "case"));   // info unticked
        await q.WhenAllAsync().WaitAsync(Limit);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
        Log(ps).Should().Contain(MemorySymbolGate.NoInfoJobNote);
    }

    [Fact]
    public async Task Replan_gives_new_gates_while_old_jobs_finish_on_theirs()
    {
        using var f = new Fx();
        var q = Queue();
        var oldGate = f.Gate;
        var newGate = new MemorySymbolGate { PollInterval = TimeSpan.FromMilliseconds(50), OrphanGrace = TimeSpan.FromMilliseconds(400) };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldInfo = q.Enqueue(f.HeldInfo(release.Task), f.Case);
        var oldPs = q.Enqueue(f.Spec("pslist"), f.Case);
        var newNet = q.Enqueue(f.Spec("netscan", newGate), f.Case);   // re-planned row run alone (its info not queued)
        await Until(() => newNet.IsFinished, "the re-planned plugin ends on its own gate");
        newNet.State.Should().Be(JobState.Succeeded, "the new gate has no info job, so its plugin does not wait for the old one");
        oldPs.State.Should().Be(JobState.Queued, "the old plugin waits for its own (live) info job");
        release.SetResult();
        await q.WhenAllAsync().WaitAsync(Limit);
        oldInfo.State.Should().Be(JobState.Succeeded);
        oldPs.State.Should().Be(JobState.Succeeded, oldPs.StatusReason);
        oldGate.DecisionNote.Should().Be("windows.info finished");
    }

    [Fact]
    public async Task Retried_plugin_of_a_finished_run_starts_without_waiting()
    {
        using var f = new Fx();
        var q = Queue();
        q.Enqueue(f.Spec("info"), f.Case);
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await q.WhenAllAsync().WaitAsync(Limit);
        var retry = q.Enqueue(ps.Spec, f.Case);              // Jobs page Retry: same spec, same (decided) gate
        await q.WhenAllAsync().WaitAsync(Limit);
        retry.State.Should().Be(JobState.Succeeded, retry.StatusReason);
        Log(retry).Should().NotContain("Waiting for the windows.info");
    }

    [Fact]
    public async Task Retried_info_rechecks_and_plugins_queued_after_it_wait_for_it()
    {
        using var f = new Fx();
        var q = Queue();
        var info = q.Enqueue(f.Spec("info"), f.Case);
        await q.WhenAllAsync().WaitAsync(Limit);
        f.Gate.IsOpen.Should().BeTrue();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var info2 = q.Enqueue(f.HeldInfo(release.Task), f.Case);
        f.Gate.IsDecided.Should().BeFalse("enqueueing an info job re-arms the gate at once (no race with the plugins queued after it)");
        var ps = q.Enqueue(f.Spec("pslist"), f.Case);
        await Until(() => ps.StatusReason?.StartsWith(MemorySymbolGate.WaitingForInfoReason) == true, "waits for the new check");
        release.SetResult();
        await q.WhenAllAsync().WaitAsync(Limit);
        info2.State.Should().Be(JobState.Succeeded);
        ps.State.Should().Be(JobState.Succeeded, ps.StatusReason);
    }

    [Fact]
    public async Task A_stale_info_job_cannot_decide_a_newer_check()
    {
        using var t = new TempDir();
        var q = Queue();
        Job Any(string id) => q.Enqueue(new JobSpec(id, "x.exe", Array.Empty<string>(), t.Path, id, "T", ExePath: Path.Combine(t.Path, "missing.exe")), t.Path);
        var gate = new MemorySymbolGate();
        var info1 = Any("i1");
        gate.RegisterInfo(info1);
        gate.Open(info1, "first");
        var info2 = Any("i2");
        gate.RegisterInfo(info2);
        gate.IsDecided.Should().BeFalse();
        gate.Open(info1, "late Finally of the first run");
        gate.IsDecided.Should().BeFalse("info1 is older than the check info2 armed");
        gate.Fail("x", info2);
        gate.Failed.Should().BeTrue();
        await q.WhenAllAsync();
    }

    [Theory]
    [InlineData("Progress:   45.12\t\tScanning memory_layer using PdbSignatureScanner", "finding the kernel 45%")]
    [InlineData("Progress:  100.00\t\tPDB scanning finished", "finding the kernel 100%")]
    [InlineData("Progress:   12.00\t\tDownloading https://msdl.microsoft.com/download/symbols/ntkrnlmp.pdb/X/ntkrnlmp.pd_", "downloading symbols 12%")]
    [InlineData("Progress:    0.00\t\tReading Symbol layer", "reading symbols 0%")]
    [InlineData("Unsatisfied requirement plugins.Info.kernel", null)]
    public void Volatility_progress_maps_to_a_plain_stage(string line, string? stage) => MemorySymbolGate.StageOf(line).Should().Be(stage);
}
