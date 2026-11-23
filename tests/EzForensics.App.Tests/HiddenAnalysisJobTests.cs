using System.IO;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Execution;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;
using EzForensics.Core.Tools;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The Spectre analysis job runs in the queue but is not in the Jobs list; Findings shows its progress, Cancel and failure.</summary>
[Collection("WpfApp")]
public sealed class HiddenAnalysisJobTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-hidden-" + Guid.NewGuid().ToString("N")[..8]);
    public HiddenAnalysisJobTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class NullLocator : IToolLocator
    {
        public void Rescan(string toolsRoot) { }
        public InstalledTool? Resolve(string exeName, BuildFlavor? preferred = null) => null;
        public IReadOnlyList<InstalledTool> All => Array.Empty<InstalledTool>();
    }

    private static JobQueue NewQueue()
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new JobQueue(new NullLocator(), 2, TimeSpan.FromSeconds(60)); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    private JobSpec Spec(Func<JobPostProcessContext, CancellationToken, Task> body) =>
        new(SpectreAnalysisJob.ToolId, "", Array.Empty<string>(), Path.Combine(_dir, ".ezassistant", "spectre-job"), SpectreAnalysisJob.Label,
            SpectreAnalysisJob.Category, Exclusive: true, InProcess: body, Hidden: true);

    private static async Task WaitFor(Func<bool> cond)
    {
        for (int i = 0; i < 300 && !cond(); i++) await Task.Delay(50);
        cond().Should().BeTrue();
    }

    private FindingsViewModel Vm(JobQueue q, Func<JobSpec> spec)
    {
        SpectreCaseLink.Save(_dir, new SpectreCaseLink("case1", 1, DateTime.UtcNow));
        return new FindingsViewModel(() => _dir, new SpectreFindingsClient(new FindingsViewModelTests.FakeEngine(), () => _dir, null), _ => Task.FromResult(true),
            queue: () => q, enqueue: dir => Task.FromResult<(Job?, string?)>((q.Enqueue(spec(), dir), null)));
    }

    [Fact]
    public void Analysis_spec_is_hidden()
    {
        var spec = SpectreAnalysisJob.CreateSpec(_dir, new SpectreFindingsClient(new FindingsViewModelTests.FakeEngine(), () => _dir, null));
        spec.Hidden.Should().BeTrue();
        spec.Exclusive.Should().BeTrue();
    }

    [Fact]
    public async Task Hidden_job_is_not_in_the_Jobs_list()
    {
        var prev = AppServices.Current;
        AppServices.Current = new AppServices(new JsonSettingsStore(Path.Combine(_dir, "settings.json")), new AppSettings());
        try
        {
            var s = AppServices.Current;
            var gate = new TaskCompletionSource();
            var hidden = s.Queue.Enqueue(Spec(async (_, ct) => await gate.Task.WaitAsync(ct)), _dir);
            var shown = s.Queue.Enqueue(new JobSpec("t", "", Array.Empty<string>(), _dir, "Visible", "X", InProcess: (_, _) => Task.CompletedTask), _dir);
            var list = new JobListViewModel((_, _) => { });
            await Task.Delay(1500);   // the list's safety-net timer also adds rows
            list.Rows.Select(r => r.Job).Should().Contain(shown).And.NotContain(hidden);
            hidden.Hidden.Should().BeTrue();
            hidden.Cancel();
            await s.Queue.WhenAllAsync();
        }
        finally { AppServices.Current = prev; }
    }

    [Fact]
    public async Task Cancel_stops_the_analysis_job()
    {
        var q = NewQueue();
        var running = new TaskCompletionSource();
        var vm = Vm(q, () => Spec(async (_, ct) => { running.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }));
        await vm.OpenCaseAsync(_dir);
        await vm.AnalyseCommand.ExecuteAsync(null);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitFor(() => vm.IsAnalysing);
        vm.CancelAnalysisCommand.Execute(null);
        await q.WhenAllAsync();
        await WaitFor(() => !vm.IsAnalysing);
        vm.RunResult.Should().Contain("cancelled");
    }

    [Fact]
    public async Task Failure_shows_on_Findings()
    {
        var q = NewQueue();
        var vm = Vm(q, () => Spec((_, _) => throw new InvalidOperationException("phase 3 broke")));
        await vm.OpenCaseAsync(_dir);
        await vm.AnalyseCommand.ExecuteAsync(null);
        await q.WhenAllAsync();
        await WaitFor(() => vm.RunResult.Length > 0);
        vm.RunResult.Should().Contain("did not finish").And.Contain("phase 3 broke");
        vm.Banner!.Message.Should().Contain("phase 3 broke");
    }
}
