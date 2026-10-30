using System.Text.Json;
using EzForensics.Core.Execution;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ExecutionJobQueueTests
{
    private static JobSpec Spec(string dir, string id, string[] args, bool exclusive = false)
        => new(id, "EzForensics.FakeTool.exe", args, Path.Combine(dir, "out", id), id, "Test", exclusive);

    private static JobQueue NewQueue(int parallel, TimeSpan? timeout = null)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new JobQueue(new FakeLocator(FakeToolPaths.Exe), parallel, timeout ?? TimeSpan.FromSeconds(60)); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    [Fact]
    public async Task Runs_job_writes_log_and_manifest_with_hashes()
    {
        using var t = new TempDir();
        var q = NewQueue(2);
        q.SourcePath = @"C:\src";
        var csvDir = Path.Combine(t.Path, "out", "A");
        var job = q.Enqueue(Spec(t.Path, "A", new[] { "--lines", "3", "--csv", csvDir }), t.Path);
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.Succeeded);
        job.ExitCode.Should().Be(0);
        File.ReadAllText(job.LogPath!).Should().Contain("line 3").And.Contain("# Command:").And.Contain("# Exit code: 0");
        job.LogPath.Should().Be(Path.Combine(t.Path, "logs", $"{job.Id:N}_A.log"));

        var manifestPath = q.GetManifestPath(t.Path)!;
        File.Exists(manifestPath).Should().BeTrue();
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        root.GetProperty("sourcePath").GetString().Should().Be(@"C:\src");
        var j = root.GetProperty("jobs")[0];
        j.GetProperty("exeSha256").GetString().Should().HaveLength(64);
        j.GetProperty("exeSha1").GetString().Should().HaveLength(40);
        j.GetProperty("state").GetString().Should().Be("Succeeded");
        var of = j.GetProperty("outputFiles")[0];
        of.GetProperty("sha256").GetString().Should().Be(ManifestWriter.Hash(of.GetProperty("path").GetString()!, System.Security.Cryptography.SHA256.Create()));
        Path.GetFileName(manifestPath).Should().MatchRegex(@"^EzForensics_manifest_\d{8}T\d{6}Z\.json$");
    }

    [Fact]
    public async Task Recorded_completes_after_the_manifest_record_is_written()
    {
        using var t = new TempDir();
        var q = NewQueue(1);
        var spec = Spec(t.Path, "R", new[] { "--lines", "2", "--delay", "300" }) with { ManifestDetails = new Dictionary<string, string> { ["image.format"] = "Raw" } };
        var job = q.Enqueue(spec, t.Path);
        bool? recordedWhenFinished = null;
        bool? inManifestWhenFinished = null;
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Job.IsFinished) || !job.IsFinished) return;
            recordedWhenFinished = job.Recorded.IsCompleted;
            var path = q.GetManifestPath(t.Path);
            inManifestWhenFinished = path != null && File.Exists(path) && RunManifest.Load(path).Jobs.Any(j => j.JobId == job.Id);
        };
        (await Task.WhenAny(job.Recorded, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(job.Recorded);

        // The state turns final before the record exists: a consumer of the manifest must await Recorded, not IsFinished.
        recordedWhenFinished.Should().BeFalse();
        inManifestWhenFinished.Should().BeFalse();
        var m = RunManifest.Load(q.GetManifestPath(t.Path)!);
        m.Jobs.Should().ContainSingle(j => j.JobId == job.Id).Which.Details!["image.format"].Should().Be("Raw");
    }

    [Fact]
    public async Task Source_path_and_roots_are_recorded_and_a_new_source_path_clears_the_roots()
    {
        using var t = new TempDir();
        var q = NewQueue(1);
        q.SetSource(@"D:\evidence\two.zip", new[] { @"C:\case\.ezstaging\extracted\aaaa1111\one", @"C:\case\.ezstaging\extracted\bbbb2222\two" });
        q.Enqueue(Spec(t.Path, "S", new[] { "--lines", "1" }), t.Path);
        await q.WhenAllAsync();
        var m = RunManifest.Load(q.GetManifestPath(t.Path)!);
        m.SourcePath.Should().Be(@"D:\evidence\two.zip");
        m.SourceRoots.Should().Equal(@"C:\case\.ezstaging\extracted\aaaa1111\one", @"C:\case\.ezstaging\extracted\bbbb2222\two");

        q.SourcePath = @"D:\other";
        q.SourceRoots.Should().BeNull();
        q.Enqueue(Spec(t.Path, "S2", new[] { "--lines", "1" }), t.Path);
        await q.WhenAllAsync();
        m = RunManifest.Load(q.GetManifestPath(t.Path)!);
        m.SourcePath.Should().Be(@"D:\other");
        m.SourceRoots.Should().BeNull();
        File.ReadAllText(q.GetManifestPath(t.Path)!).Should().NotContain("sourceRoots");
    }

    [Fact]
    public async Task Missing_exe_fails_with_message()
    {
        using var t = new TempDir();
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        var q = new JobQueue(new FakeLocator(null), 1, TimeSpan.FromSeconds(10));
        SynchronizationContext.SetSynchronizationContext(prev);
        var job = q.Enqueue(Spec(t.Path, "M", Array.Empty<string>()), t.Path);
        await q.WhenAllAsync();
        job.State.Should().Be(JobState.Failed);
        File.ReadAllText(job.LogPath!).Should().Contain("not found");
    }

    [Fact]
    public async Task Parallel_limit_is_respected()
    {
        using var t = new TempDir();
        var q = NewQueue(2);
        var running = 0; var max = 0;
        var gate = new object();
        var jobs = Enumerable.Range(0, 6).Select(i => q.Enqueue(Spec(t.Path, "P" + i, new[] { "--lines", "1", "--delay", "700" }), t.Path)).ToList();
        var sampler = Task.Run(async () =>
        {
            while (!jobs.All(j => j.IsFinished))
            {
                var r = jobs.Count(j => j.State == JobState.Running);
                lock (gate) { running = r; max = Math.Max(max, r); }
                await Task.Delay(20);
            }
        });
        await q.WhenAllAsync();
        await sampler;
        max.Should().BeLessOrEqualTo(2).And.BeGreaterThan(0);
        jobs.Should().OnlyContain(j => j.State == JobState.Succeeded);
    }

    [Fact]
    public async Task Heavy_jobs_never_overlap_each_other()
    {
        using var t = new TempDir();
        var q = NewQueue(3);
        var h1 = q.Enqueue(Spec(t.Path, "X1", new[] { "--lines", "1", "--delay", "500" }, exclusive: true), t.Path);
        var h2 = q.Enqueue(Spec(t.Path, "X2", new[] { "--lines", "1", "--delay", "300" }, exclusive: true), t.Path);

        await q.WhenAllAsync();

        h1.State.Should().Be(JobState.Succeeded);
        h2.State.Should().Be(JobState.Succeeded);
        (h1.EndUtc <= h2.StartUtc || h2.EndUtc <= h1.StartUtc).Should().BeTrue("two heavy jobs must run one at a time");
    }

    [Fact]
    public async Task Light_jobs_run_alongside_a_heavy_job_within_MaxParallel()
    {
        using var t = new TempDir();
        var q = NewQueue(3);
        var heavy = q.Enqueue(Spec(t.Path, "X", new[] { "--lines", "1", "--delay", "1500" }, exclusive: true), t.Path);
        var l1 = q.Enqueue(Spec(t.Path, "L1", new[] { "--lines", "1", "--delay", "600" }), t.Path);
        var l2 = q.Enqueue(Spec(t.Path, "L2", new[] { "--lines", "1", "--delay", "600" }), t.Path);
        var l3 = q.Enqueue(Spec(t.Path, "L3", new[] { "--lines", "1", "--delay", "100" }), t.Path);

        await q.WhenAllAsync();

        foreach (var j in new[] { heavy, l1, l2, l3 }) j.State.Should().Be(JobState.Succeeded);
        // light jobs overlap the heavy one (it counts toward MaxParallel, so two lights fit beside it)
        (l1.StartUtc < heavy.EndUtc && l1.EndUtc > heavy.StartUtc).Should().BeTrue("a light job runs alongside the heavy one");
        (l2.StartUtc < heavy.EndUtc && l2.EndUtc > heavy.StartUtc).Should().BeTrue();
        // heavy + 2 lights = MaxParallel 3, so the third light waits for a slot
        l3.StartUtc.Should().BeOnOrAfter(l1.EndUtc!.Value.AddMilliseconds(-50));
    }

    [Fact]
    public async Task Waiting_heavy_job_does_not_block_light_jobs_queued_after_it()
    {
        using var t = new TempDir();
        var q = NewQueue(3);
        var h1 = q.Enqueue(Spec(t.Path, "H1", new[] { "--lines", "1", "--delay", "2000" }, exclusive: true), t.Path);
        var h2 = q.Enqueue(Spec(t.Path, "H2", new[] { "--lines", "1", "--delay", "200" }, exclusive: true), t.Path);
        var l1 = q.Enqueue(Spec(t.Path, "L1", new[] { "--lines", "1", "--delay", "100" }), t.Path);

        await q.WhenAllAsync();

        foreach (var j in new[] { h1, h2, l1 }) j.State.Should().Be(JobState.Succeeded);
        l1.StartUtc.Should().BeBefore(h1.EndUtc!.Value, "a light job must not wait behind the queued second heavy job");
        h2.StartUtc.Should().BeOnOrAfter(h1.EndUtc!.Value.AddMilliseconds(-50));
    }

    [Fact]
    public async Task Cancel_one_and_cancel_all()
    {
        using var t = new TempDir();
        var q = NewQueue(1);
        var a = q.Enqueue(Spec(t.Path, "H1", new[] { "--hang" }), t.Path);
        var b = q.Enqueue(Spec(t.Path, "H2", new[] { "--hang" }), t.Path);
        try
        {
            for (int i = 0; i < 300 && a.State != JobState.Running; i++) await Task.Delay(50);
            await Task.Delay(300);
            a.State.Should().Be(JobState.Running);
            b.State.Should().Be(JobState.Queued);
        }
        finally
        {
            // Never leave a hanging FakeTool behind: an orphan keeps the test host's pipes open and hangs `dotnet test`.
            q.CancelAll();
            await q.WhenAllAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        a.State.Should().Be(JobState.Cancelled);
        b.State.Should().Be(JobState.Cancelled);
    }

    [Fact]
    public async Task A_job_whose_log_cannot_be_opened_gives_its_slot_back()
    {
        using var t = new TempDir();
        var bad = Path.Combine(t.Path, "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "logs"), "a file where the logs folder should be");
        var q = NewQueue(1);
        var light = q.Enqueue(Spec(t.Path, "L", new[] { "--lines", "1" }), bad);
        var heavy = q.Enqueue(Spec(t.Path, "H", new[] { "--lines", "1" }, exclusive: true), bad);
        await q.WhenAllAsync().WaitAsync(TimeSpan.FromSeconds(30));
        light.State.Should().Be(JobState.Failed);
        heavy.State.Should().Be(JobState.Failed);

        // Both slots (the only light slot and the heavy slot) must be free again.
        var good = Path.Combine(t.Path, "good");
        var a = q.Enqueue(Spec(t.Path, "A", new[] { "--lines", "1" }), good);
        var b = q.Enqueue(Spec(t.Path, "B", new[] { "--lines", "1" }, exclusive: true), good);
        await q.WhenAllAsync().WaitAsync(TimeSpan.FromSeconds(30));
        a.State.Should().Be(JobState.Succeeded);
        b.State.Should().Be(JobState.Succeeded);
    }

    [Fact]
    public async Task Timeout_marks_job_TimedOut()
    {
        using var t = new TempDir();
        var q = NewQueue(1, TimeSpan.FromMilliseconds(700));
        var a = q.Enqueue(Spec(t.Path, "T", new[] { "--hang" }), t.Path);
        await q.WhenAllAsync();
        a.State.Should().Be(JobState.TimedOut);
    }

    [Fact]
    public async Task ToolDir_token_is_expanded()
    {
        using var t = new TempDir();
        var q = NewQueue(1);
        var job = q.Enqueue(Spec(t.Path, "D", new[] { "--lines", "1", "--csv", "{toolDir}" + Path.DirectorySeparatorChar + "tdtest_" + Guid.NewGuid().ToString("N")[..6] }), t.Path);
        await q.WhenAllAsync();
        var dir = Path.GetDirectoryName(FakeToolPaths.Exe)!;
        job.ResolvedArgs![3].Should().StartWith(dir);
        job.CommandLine.Should().NotContain("{toolDir}");
        try { Directory.Delete(job.ResolvedArgs![3], true); } catch { }
    }
}
