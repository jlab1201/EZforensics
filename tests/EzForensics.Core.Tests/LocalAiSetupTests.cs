using EzForensics.Core.Assistant;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class LocalAiSetupTests
{
    private sealed class FakeOps : ILocalAiOps
    {
        public OllamaState State = OllamaState.None;
        public List<string> Calls = new();
        public int TunedCount = 0;
        public Task<OllamaState> ProbeAsync(CancellationToken ct) => Task.FromResult(State);
        public Task InstallOllamaAsync(Action<LocalAiProgress> p, CancellationToken ct) { Calls.Add("install"); State = new(true, false, State.Models); return Task.CompletedTask; }
        public IReadOnlyList<string> ApplyTuning() { Calls.Add("tune"); return Enumerable.Repeat("X", TunedCount).ToList(); }
        public Task StartServerAsync(bool restart, CancellationToken ct) { Calls.Add(restart ? "restart" : "start"); State = State with { Running = true }; return Task.CompletedTask; }
        public Task PullModelAsync(string model, Action<LocalAiProgress> p, CancellationToken ct) { Calls.Add("pull:" + model); State = State with { Models = new[] { model } }; return Task.CompletedTask; }
        public void UseModel(string model) => Calls.Add("use:" + model);
    }

    [Fact]
    public async Task Fresh_machine_runs_every_step()
    {
        var ops = new FakeOps { TunedCount = 2 };
        var r = await LocalAiSetup.RunAsync("qwen3:8b", ops);
        ops.Calls.Should().Equal("install", "tune", "restart", "pull:qwen3:8b", "use:qwen3:8b");
        r.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task Everything_present_only_switches_the_assistant_over()
    {
        var ops = new FakeOps { State = new(true, true, new[] { "qwen3:8b" }) };
        var r = await LocalAiSetup.RunAsync("qwen3:8b", ops);
        ops.Calls.Should().Equal("tune", "use:qwen3:8b");
        r.Skipped.Should().HaveCount(3);
    }

    [Fact]
    public async Task Installed_but_stopped_starts_the_server_and_pulls_only_the_missing_model()
    {
        var ops = new FakeOps { State = new(true, false, Array.Empty<string>()) };
        await LocalAiSetup.RunAsync("qwen3:4b", ops);
        ops.Calls.Should().Equal("tune", "start", "pull:qwen3:4b", "use:qwen3:4b");
    }

    [Fact]
    public async Task Cancellation_stops_before_the_next_step()
    {
        var ops = new FakeOps();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // The fake ignores the token itself; the setup checks it between steps.
        var act = () => LocalAiSetup.RunAsync("qwen3:8b", ops, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        ops.Calls.Should().NotContain("pull:qwen3:8b");
    }

    [Fact]
    public void ParsePullLine_sums_layers_and_reports_errors()
    {
        var layers = new Dictionary<string, (long, long)>();
        OllamaOps.ParsePullLine("{\"status\":\"pulling manifest\"}", layers).Fraction.Should().BeNull();
        var u = OllamaOps.ParsePullLine("{\"status\":\"pulling a\",\"digest\":\"a\",\"total\":1000,\"completed\":250}", layers);
        u.Fraction.Should().BeApproximately(0.25, 1e-9);
        OllamaOps.ParsePullLine("{\"error\":\"nope\"}", layers).Error.Should().Be("nope");
    }
}

public class LocalAiPlanTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static SystemCheckResult Pc(double vramGb, double ramGb = 16) =>
        SetupCatalogTests.Sys() with { TotalRamBytes = (long)(ramGb * Gb), Gpus = vramGb > 0 ? new[] { new GpuInfo("NVIDIA RTX 3070", (long)(vramGb * Gb)) } : Array.Empty<GpuInfo>() };

    [Theory]
    [InlineData(8, true)]
    [InlineData(4, false)]
    [InlineData(0, false)]
    public void Local_ai_is_preselected_only_when_the_pc_suits_it(double vram, bool selected)
    {
        SetupSelection.ForProfile(SetupProfile.Minimal, SetupCatalog.Default, Pc(vram)).Has(SetupIds.LocalAi).Should().Be(selected);
        SetupSelection.ForProfile(SetupProfile.Everything, SetupCatalog.Default, Pc(vram)).Has(SetupIds.LocalAi).Should().BeTrue("Everything means everything");
    }

    [Fact]
    public void Plan_has_an_install_step_with_the_model()
    {
        var sel = new SetupSelection();
        sel.Items.Add(SetupIds.LocalAi);
        var plan = SetupPlanner.Plan(SetupCatalog.Default, sel, Pc(8));
        var step = plan.Steps.Single(s => s.Kind == SetupStepKind.InstallLocalAi);
        step.Args["model"].Should().Be("qwen3:8b");
        step.RequiresAdmin.Should().BeFalse();
    }
}
