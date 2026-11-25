using EzForensics.Core.Settings;
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
        public string? Exe;
        public string? FindExe() => Exe;
        public Task<OllamaState> ProbeAsync(CancellationToken ct) => Task.FromResult(State);
        public IReadOnlyList<string> ApplyTuning() { Calls.Add("tune"); return Enumerable.Repeat("X", TunedCount).ToList(); }
        public Task StartServerAsync(CancellationToken ct) { Calls.Add("start"); State = State with { Running = true }; return Task.CompletedTask; }
        public Task PullModelAsync(string model, Action<LocalAiProgress> p, CancellationToken ct) { Calls.Add("pull:" + model); State = State with { Models = new[] { model } }; return Task.CompletedTask; }
        public void UseModel(string model) => Calls.Add("use:" + model);
    }

    [Fact]
    public async Task Not_installed_throws_the_note_and_changes_nothing()
    {
        var ops = new FakeOps();
        var act = () => LocalAiSetup.RunAsync("qwen3:8b", ops);
        var ex = await act.Should().ThrowAsync<OllamaNotInstalledException>();
        ex.Which.Message.Should().Contain("ollama.com");
        ops.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Exe_on_disk_counts_as_installed_and_gets_started()
    {
        var ops = new FakeOps { Exe = "ollama.exe" };
        await LocalAiSetup.RunAsync("qwen3:8b", ops);
        ops.Calls.Should().Equal("tune", "start", "pull:qwen3:8b", "use:qwen3:8b");
    }

    [Fact]
    public async Task Running_api_counts_as_installed_and_is_never_restarted()
    {
        var ops = new FakeOps { State = new(false, true, Array.Empty<string>()), TunedCount = 2 };
        var r = await LocalAiSetup.RunAsync("qwen3:8b", ops);
        ops.Calls.Should().Equal("tune", "pull:qwen3:8b", "use:qwen3:8b");
        r.Summary.Should().Contain("next time Ollama starts");
    }

    [Fact]
    public async Task Summary_mentions_the_speed_settings_when_they_were_changed()
    {
        var ops = new FakeOps { State = new(true, true, new[] { "qwen3:8b" }), TunedCount = 2 };
        var r = await LocalAiSetup.RunAsync("qwen3:8b", ops);
        r.Summary.Should().Contain("flash attention").And.Contain("8-bit cache");
        var quiet = await LocalAiSetup.RunAsync("qwen3:8b", new FakeOps { State = new(true, true, new[] { "qwen3:8b" }) });
        quiet.Summary.Should().NotContain("flash attention");
    }

    [Fact]
    public async Task Started_here_means_no_restart_note()
    {
        var ops = new FakeOps { State = new(true, true, new[] { "qwen3:8b" }) };
        var r = await LocalAiSetup.RunAsync("qwen3:8b", ops, tunedBefore: new[] { "OLLAMA_FLASH_ATTENTION" }, startedHere: true);
        r.Summary.Should().Contain("flash attention").And.NotContain("next time Ollama starts");
    }

    [Fact]
    public async Task Everything_present_only_switches_the_assistant_over()
    {
        var ops = new FakeOps { State = new(true, true, new[] { "qwen3:8b" }) };
        var r = await LocalAiSetup.RunAsync("qwen3:8b", ops);
        ops.Calls.Should().Equal("tune", "use:qwen3:8b");
        r.Skipped.Should().HaveCount(2);
    }

    [Fact]
    public async Task Cancellation_stops_before_the_next_step()
    {
        var ops = new FakeOps { State = new(true, true, Array.Empty<string>()) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
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

public class WizardHasNoLocalAiTests
{
    [Theory]
    [InlineData(SetupProfile.Minimal)]
    [InlineData(SetupProfile.Everything)]
    public void Profiles_never_contain_local_ai(SetupProfile profile)
    {
        SetupSelection.ForProfile(profile, SetupCatalog.Default, SetupCatalogTests.Sys()).Items.Should().NotContain("local-ai");
        SetupCatalog.Default.Find("local-ai").Should().BeNull();
    }

    [Fact]
    public void A_saved_resume_list_with_local_ai_loads_and_plans_no_ai_step()
    {
        var sel = SetupWizardPolicy.FromResume(new SetupResumeState { Items = new List<string> { "local-ai", "ai", SetupIds.ExploreDie }, SavedUtc = DateTime.UtcNow });
        var plan = SetupPlanner.Plan(SetupCatalog.Default, sel, SetupCatalogTests.Sys());
        plan.Steps.Should().NotContain(s => s.ItemId == "local-ai" || s.Id == "local-ai");
        plan.Selection.Items.Should().NotContain("local-ai");
    }
}
