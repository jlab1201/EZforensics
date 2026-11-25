using System.IO;
using System.Net.Http;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Assistant;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;
using Xunit;

namespace EzForensics.App.Tests;

public class LocalAiServiceTests
{
    private sealed class FakeOps : ILocalAiOps
    {
        public OllamaState State = new(true, true, Array.Empty<string>());
        public List<string> Calls = new();
        public TaskCompletionSource? PullGate;
        public Task<OllamaState> ProbeAsync(CancellationToken ct) => Task.FromResult(State);
        public string? FindExe() => null;
        public int Tuned;
        public IReadOnlyList<string> ApplyTuning() => Enumerable.Repeat("X", Tuned).ToList();
        public Task StartServerAsync(CancellationToken ct) { Calls.Add("start"); State = State with { Running = true }; return Task.CompletedTask; }
        public async Task PullModelAsync(string model, Action<LocalAiProgress> p, CancellationToken ct)
        {
            Calls.Add("pull:" + model);
            if (PullGate != null) await PullGate.Task.WaitAsync(ct);
            State = State with { Models = new[] { model } };
        }
        public void UseModel(string model) => Calls.Add("use:" + model);
    }

    private sealed class ThrowingOps : ILocalAiOps
    {
        public Task<OllamaState> ProbeAsync(CancellationToken ct) => Task.FromResult(new OllamaState(true, true, Array.Empty<string>()));
        public IReadOnlyList<string> ApplyTuning() => Array.Empty<string>();
        public Task StartServerAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PullModelAsync(string model, Action<LocalAiProgress> p, CancellationToken ct) => throw new InvalidOperationException("first line\nsecond line");
        public void UseModel(string model) { }
    }

    private static LocalAiRecommendation Rec(bool local = true) =>
        new(local ? LocalAiChoice.Local : LocalAiChoice.Cloud, local ? "qwen3:8b" : null, 5.2, "Small PC.");

    private static readonly Func<LocalAiService.Ask, LocalAiService.Answer> Yes = _ => LocalAiService.Answer.Download;
    private static readonly Func<LocalAiService.Ask, LocalAiService.Answer> No = _ => LocalAiService.Answer.Cancel;
    private static readonly Func<LocalAiService.Ask, LocalAiService.Answer> NoDialog = _ => throw new InvalidOperationException("no dialog expected");

    private static LocalAiService Svc(ILocalAiOps ops, Func<LocalAiService.Ask, LocalAiService.Answer>? confirm = null) =>
        new(() => new HttpClient(), new AppSettings(), () => { }, confirm ?? Yes, a => a(), (_, _) => ops, () => Rec(), _ => { });

    [Fact]
    public async Task Second_start_while_running_returns_the_same_task_and_pulls_once()
    {
        var ops = new FakeOps { PullGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var svc = Svc(ops);
        var t1 = svc.StartAsync();
        while (!ops.Calls.Contains("pull:qwen3:8b")) await Task.Delay(10);
        Assert.True(svc.IsRunning);
        var t2 = svc.StartAsync();
        Assert.Same(t1, t2);
        ops.PullGate.SetResult();
        Assert.NotNull(await t1);
        Assert.Equal(1, ops.Calls.Count(c => c.StartsWith("pull:")));
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public async Task Cancel_ends_the_run_with_a_cancelled_status()
    {
        var ops = new FakeOps { PullGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var svc = Svc(ops);
        bool? finished = null;
        svc.Finished += ok => finished = ok;
        var t = svc.StartAsync();
        while (!ops.Calls.Contains("pull:qwen3:8b")) await Task.Delay(10);
        svc.Cancel();
        Assert.Null(await t);
        Assert.False(svc.IsRunning);
        Assert.Equal("Cancelled.", svc.Status);
        Assert.False(finished);
    }

    [Fact]
    public async Task Declining_the_dialog_starts_nothing()
    {
        var ops = new FakeOps();
        var svc = Svc(ops, No);
        bool raised = false;
        svc.Finished += _ => raised = true;
        Assert.Null(await svc.StartAsync());
        Assert.Empty(ops.Calls);
        Assert.False(svc.IsRunning);
        Assert.False(raised);
    }

    [Fact]
    public async Task Model_already_present_needs_no_dialog()
    {
        var ops = new FakeOps { State = new(true, true, new[] { "qwen3:8b" }) };
        var svc = Svc(ops, NoDialog);
        Assert.NotNull(await svc.StartAsync());
        Assert.Contains("use:qwen3:8b", ops.Calls);
    }

    [Fact]
    public async Task Ollama_not_installed_shows_the_note_and_downloads_nothing()
    {
        var ops = new FakeOps { State = OllamaState.None };
        var svc = Svc(ops, NoDialog);
        Assert.Null(await svc.StartAsync());
        Assert.True(svc.OllamaMissing);
        Assert.Contains("ollama.com", svc.Status);
        Assert.Empty(ops.Calls);
    }

    [Fact]
    public async Task Installed_but_stopped_tries_to_start_ollama()
    {
        var ops = new FakeOps { State = new(true, false, Array.Empty<string>()) };
        var svc = Svc(ops);
        Assert.NotNull(await svc.StartAsync());
        Assert.Equal(new[] { "start", "pull:qwen3:8b", "use:qwen3:8b" }, ops.Calls);
    }

    [Fact]
    public async Task Installed_stopped_with_the_model_present_starts_ollama_with_no_dialog_and_no_pull()
    {
        var ops = new FakeOps { State = new(true, false, new[] { "qwen3:8b" }) };
        var svc = Svc(ops, NoDialog);
        Assert.NotNull(await svc.StartAsync());
        Assert.Equal(new[] { "start", "use:qwen3:8b" }, ops.Calls);
    }

    [Fact]
    public async Task Cancel_works_during_the_first_check()
    {
        var gate = new TaskCompletionSource<OllamaState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ops = new SlowProbeOps(gate);
        var svc = Svc(ops, NoDialog);
        var t = svc.StartAsync();
        while (!ops.Probing) await Task.Delay(10);
        Assert.True(svc.IsRunning);
        svc.Cancel();
        Assert.Null(await t);
        Assert.Equal("Cancelled.", svc.Status);
        Assert.False(svc.IsRunning);
    }

    private sealed class SlowProbeOps : ILocalAiOps
    {
        private readonly TaskCompletionSource<OllamaState> _gate;
        public volatile bool Probing;
        public SlowProbeOps(TaskCompletionSource<OllamaState> gate) { _gate = gate; }
        public async Task<OllamaState> ProbeAsync(CancellationToken ct) { Probing = true; return await _gate.Task.WaitAsync(ct); }
        public IReadOnlyList<string> ApplyTuning() => Array.Empty<string>();
        public Task StartServerAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PullModelAsync(string model, Action<LocalAiProgress> p, CancellationToken ct) => Task.CompletedTask;
        public void UseModel(string model) { }
    }

    [Fact]
    public async Task Existing_chat_model_is_offered_and_can_be_used_without_a_download()
    {
        var ops = new FakeOps { State = new(true, true, new[] { "nomic-embed-text:latest", "llama3.1:8b" }) };
        LocalAiService.Ask? seen = null;
        var svc = Svc(ops, a => { seen = a; return LocalAiService.Answer.UseExisting; });
        Assert.NotNull(await svc.StartAsync());
        Assert.Equal("llama3.1:8b", seen!.ExistingModel);
        Assert.Equal("qwen3:8b", seen.DownloadModel);
        Assert.Contains("llama3.1:8b", seen.Text);
        Assert.DoesNotContain(ops.Calls, c => c.StartsWith("pull:"));
        Assert.Contains("use:llama3.1:8b", ops.Calls);
    }

    [Fact]
    public async Task Embedding_models_alone_are_not_offered_and_download_still_works()
    {
        var ops = new FakeOps { State = new(true, true, new[] { "nomic-embed-text:latest" }) };
        LocalAiService.Ask? seen = null;
        var svc = Svc(ops, a => { seen = a; return LocalAiService.Answer.Download; });
        Assert.NotNull(await svc.StartAsync());
        Assert.Null(seen!.ExistingModel);
        Assert.Contains("pull:qwen3:8b", ops.Calls);
    }

    [Fact]
    public async Task The_service_hands_the_live_settings_and_save_to_the_machine_operations()
    {
        var live = new AppSettings();
        var saved = 0;
        AppSettings? gotSettings = null; Action? gotSave = null;
        var ops = new FakeOps { State = new(true, true, new[] { "qwen3:8b" }) };
        var svc = new LocalAiService(() => new HttpClient(), live, () => saved++, Yes, a => a(), (st, sv) => { gotSettings = st; gotSave = sv; return ops; }, () => Rec(), _ => { });
        Assert.NotNull(await svc.StartAsync());
        Assert.Same(live, gotSettings);
        gotSave!();
        Assert.Equal(1, saved);
    }

    [Fact]
    public async Task A_throwing_finished_handler_does_not_turn_success_into_failure()
    {
        var ops = new FakeOps { State = new(true, true, new[] { "qwen3:8b" }) };
        var svc = Svc(ops, NoDialog);
        var second = false;
        svc.Finished += _ => throw new InvalidOperationException("boom");
        svc.Finished += ok => second = ok;
        Assert.NotNull(await svc.StartAsync());
        Assert.True(second);
        Assert.Null(svc.LastError);
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public async Task Options_is_told_to_reload_from_the_live_settings_when_a_run_finishes()
    {
        var live = new AppSettings();
        var ops = new FakeOps { State = new(true, true, new[] { "qwen3:8b" }) };
        var svc = new LocalAiService(() => new HttpClient(), live, () => { }, Yes, a => a(), (_, _) => ops, () => Rec(), _ => { });
        var vm = new AiSettingsViewModel(() => new AppSettings(), new ApiKeyStore(Path.Combine(Path.GetTempPath(), "ezlocalai-keys2")), () => new HttpClient(new ChatHandler(), false), svc);
        var raised = false;
        vm.LiveSettingsChanged += () => raised = true;
        Assert.NotNull(await svc.StartAsync());
        Assert.True(raised);
    }

    [Fact]
    public void Confirm_text_mentions_the_speed_settings_and_the_existing_model()
    {
        var t = LocalAiService.BuildConfirmText("qwen3:8b", 5.2, true, null, "llama3.1:8b");
        Assert.Contains("Also turns on two Ollama speed settings for your user: flash attention and an 8-bit cache.", t);
        Assert.Contains("llama3.1:8b", t);
        Assert.DoesNotContain("speed settings", LocalAiService.BuildConfirmText("qwen3:8b", 5.2, true, null, null, tuningPending: false));
    }

    [Fact]
    public async Task Summary_mentions_the_speed_settings_on_the_no_dialog_path()
    {
        var ops = new FakeOps { State = new(true, true, new[] { "qwen3:8b" }), Tuned = 2 };
        var svc = Svc(ops, NoDialog);
        var summary = await svc.StartAsync();
        Assert.Contains("flash attention", summary);
    }

    [Fact]
    public async Task Failure_shows_the_first_line_and_the_log_link()
    {
        var svc = Svc(new ThrowingOps());
        Assert.Null(await svc.StartAsync());
        Assert.Equal("Local AI setup failed: first line", svc.Status);
        Assert.True(svc.ShowOpenLog);
    }

    [Fact]
    public void Confirm_text_has_the_model_size_and_a_cloud_note_only_when_not_recommended()
    {
        var ok = LocalAiService.BuildConfirmText("qwen3:8b", 5.2, true);
        Assert.Contains("qwen3:8b", ok);
        Assert.Contains("5.2 GB", ok);
        Assert.Contains("several minutes", ok);
        Assert.DoesNotContain("cloud", ok);
        Assert.Contains("cloud", LocalAiService.BuildConfirmText("qwen3:4b", 2.6, false, "Only 8 GB of memory."));
    }

    [Fact]
    public async Task Finished_true_hides_the_chat_card()
    {
        var handler = new ChatHandler();
        var ops = new FakeOps { State = new(true, true, Array.Empty<string>()) };
        var svc = Svc(ops);
        var vm = new AssistantChatViewModel(new AppSettings { AiProvider = "ollama" }, new ApiKeyStore(Path.Combine(Path.GetTempPath(), "ezlocalai-keys")),
            () => new HttpClient(handler, false), () => null, null, null, (_, _) => false, a => a(), (_, _) => Task.FromResult<AssistantExtraTools?>(null),
            localAi: svc);
        await vm.CheckProviderAsync();
        Assert.True(vm.ShowLocalAiCard);
        handler.Body = "{\"data\":[{\"id\":\"qwen3:8b\"}]}";
        Assert.NotNull(await svc.StartAsync());   // started elsewhere (Options / Tools)
        Assert.NotNull(vm.LocalAiRecheck);
        await vm.LocalAiRecheck!;
        Assert.False(vm.ShowLocalAiCard);
    }

    private sealed class ChatHandler : HttpMessageHandler
    {
        public string Body = "{\"data\":[]}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Body) });
    }
}
