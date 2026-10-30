using EzForensics.Core.Execution;
using EzForensics.Core.Tools;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ExecutionLiveSettingsTests
{
    private static JobSpec Spec(string dir, string id, string[] args)
        => new(id, "EzForensics.FakeTool.exe", args, Path.Combine(dir, "out", id), id, "Test");

    [Fact]
    public async Task MaxParallel_and_Timeout_can_change_at_runtime()
    {
        using var t = new TempDir();
        var q = new JobQueue(new FakeLocator(FakeToolPaths.Exe), 1, TimeSpan.FromSeconds(60));
        q.MaxParallel.Should().Be(1);
        q.MaxParallel = 3;
        q.Timeout = TimeSpan.FromSeconds(30);
        q.MaxParallel.Should().Be(3);
        q.Timeout.Should().Be(TimeSpan.FromSeconds(30));

        var jobs = Enumerable.Range(0, 3).Select(i => q.Enqueue(Spec(t.Path, "J" + i, new[] { "--lines", "1", "--delay", "300" }), t.Path)).ToList();
        await q.WhenAllAsync();
        jobs.Should().OnlyContain(j => j.State == JobState.Succeeded);
    }

    [Fact]
    public async Task Raising_MaxParallel_unblocks_queued_jobs()
    {
        using var t = new TempDir();
        var q = new JobQueue(new FakeLocator(FakeToolPaths.Exe), 1, TimeSpan.FromSeconds(60));
        var a = q.Enqueue(Spec(t.Path, "A", new[] { "--hang" }), t.Path);
        var b = q.Enqueue(Spec(t.Path, "B", new[] { "--lines", "1" }), t.Path);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && a.State != JobState.Running) await Task.Delay(25);
            a.State.Should().Be(JobState.Running);
            await Task.Delay(300);
            b.State.Should().Be(JobState.Queued);
            q.MaxParallel = 2;
            deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !b.IsFinished) await Task.Delay(25);
            b.IsFinished.Should().BeTrue();
            a.State.Should().Be(JobState.Running, "B finished while A still held its slot, so raising the limit freed it");
        }
        finally
        {
            q.CancelAll();
            await q.WhenAllAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task Job_raises_PropertyChanged_for_CommandLine_and_IsFinished()
    {
        using var t = new TempDir();
        var q = new JobQueue(new FakeLocator(FakeToolPaths.Exe), 1, TimeSpan.FromSeconds(60));
        var job = q.Enqueue(Spec(t.Path, "P", new[] { "--lines", "1" }), t.Path);
        var names = new List<string?>();
        job.PropertyChanged += (_, e) => { lock (names) names.Add(e.PropertyName); };
        await q.WhenAllAsync();
        await Task.Delay(100);
        lock (names) names.Should().Contain(new[] { nameof(Job.CommandLine), nameof(Job.IsFinished), nameof(Job.State) });
    }

    [Fact]
    public async Task RuntimeProbe_async_and_cached_agree()
    {
        var v = await RuntimeProbe.HasNet9Async();
        RuntimeProbe.IsKnown.Should().BeTrue();
        RuntimeProbe.Cached.Should().Be(v);
        RuntimeProbe.HasNet9().Should().Be(v);
    }
}
