using System.Collections.Concurrent;
using EzForensics.Core.Execution;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Install / update processes run on a private queue (not the Jobs page queue), with their output streamed and logged.</summary>
public class MaintenanceRunnerTests
{
    [Fact]
    public async Task Runs_on_its_own_queue_streams_output_and_writes_the_job_log()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezf-maint-" + Guid.NewGuid().ToString("N"));
        try
        {
            var jobsPage = new JobQueue(new FakeLocator(FakeToolPaths.Exe), 2, TimeSpan.FromMinutes(1));
            var m = new MaintenanceRunner(new JobQueue(new FakeLocator(FakeToolPaths.Exe), 2, TimeSpan.FromMinutes(1)), root);
            var logDir = m.NewLogDir("pip");
            Path.GetDirectoryName(logDir).Should().Be(root);
            var lines = new ConcurrentQueue<string>();
            var spec = new JobSpec("setup-test", "EzForensics.FakeTool.exe", new[] { "--lines", "3", "--delay", "1" }, Path.Combine(logDir, "out"),
                "pip install (test)", "Maintenance", ExePath: FakeToolPaths.Exe, Timeout: TimeSpan.FromMinutes(1));

            var job = await m.RunAsync(spec, lines.Enqueue, default, logDir);

            job.State.Should().Be(JobState.Succeeded);
            lines.Count.Should().BeGreaterThanOrEqualTo(3);
            m.Queue.Jobs.Should().ContainSingle();
            jobsPage.Jobs.Should().BeEmpty("maintenance never shows on the Jobs page");
            job.LogPath.Should().StartWith(Path.Combine(logDir, "logs"));
            File.Exists(job.LogPath).Should().BeTrue();
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Cancel_stops_the_job_and_throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezf-maint-" + Guid.NewGuid().ToString("N"));
        try
        {
            var m = new MaintenanceRunner(new JobQueue(new FakeLocator(FakeToolPaths.Exe), 1, TimeSpan.FromMinutes(1)), root);
            var spec = new JobSpec("setup-test", "EzForensics.FakeTool.exe", new[] { "--lines", "200", "--delay", "50" }, Path.Combine(root, "out"),
                "slow", "Maintenance", ExePath: FakeToolPaths.Exe, Timeout: TimeSpan.FromMinutes(1));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var act = () => m.RunAsync(spec, null, cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
            m.Queue.Jobs.Should().ContainSingle(j => j.State == JobState.Cancelled);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
