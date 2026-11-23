using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Assistant;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;
using Xunit;

namespace EzForensics.App.Tests;

public sealed class AssistantChatViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezchat-" + Guid.NewGuid().ToString("N"));

    public AssistantChatViewModelTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class FakeModel : IModelClient
    {
        public int Calls;
        public List<string> Sent = new();
        public IReadOnlyList<ToolSpec> LastTools = Array.Empty<ToolSpec>();
        public Func<CancellationToken, Task<ModelStep>> OnUser = _ => Task.FromResult(Say("ok"));
        public Task<ModelStep> SendUserAsync(string text, IReadOnlyList<ToolSpec> tools, CancellationToken ct) { Calls++; Sent.Add(text); LastTools = tools; return OnUser(ct); }
        public List<string> ToolResults = new();
        public Task<ModelStep> SendToolResultsAsync(IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> results, IReadOnlyList<ToolSpec> tools, CancellationToken ct) =>
            Task.FromResult(Say("done" + AddResults(results)));
        private string AddResults(IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> r) { ToolResults.AddRange(r.Select(x => x.Outcome.Text)); return ""; }
    }

    private static ModelStep Say(string t) => new(new[] { t }, Array.Empty<ToolCall>(), ModelStopReason.EndTurn);

    private sealed class FakeGrid : PageViewModel, IAssistantGridTarget
    {
        public override string Title => "Analyze";
        public override SpectrePage AssistantPage => SpectrePage.Analyze;
        public override string? AssistantState() => "Case: demo";
        public string? CurrentCsv { get; set; } = @"C:\case\evtx.csv";
        public List<FilterGroup> Filters = new();
        public List<(IReadOnlyList<long> Rows, string? Flag)> Flags = new();
        public string? FlagError;
        public string? OpenError;
        public List<(string Path, FilterGroup? Filter)> Opened = new();
        public Task<string?> OpenFileAsync(string path, FilterGroup? filter) { if (OpenError == null) Opened.Add((path, filter)); return Task.FromResult(OpenError); }
        public GridContext GetContext() => new(CurrentCsv, new[] { "EventId", "Payload" }, 100, 100, "", Array.Empty<GridSampleRow>());
        public IReadOnlyList<string> FlagIds { get; } = new[] { "red", "blue" };
        public string FlagLabel(string flagId) => flagId.ToUpperInvariant();
        public void ApplyFilter(FilterGroup filter) { Filters.Add(filter); CurrentFilter = filter; }
        public string? ApplyFlag(IReadOnlyList<long> rows, string? flagId)
        {
            if (FlagError != null) return FlagError;
            Flags.Add((rows, flagId));
            foreach (var r in rows) { if (flagId == null) FlagState.Remove(r); else FlagState[r] = flagId; }
            return null;
        }
        public Dictionary<long, string> FlagState = new();
        public FilterGroup? CurrentFilter;
        public int Cleared;
        public (string? Column, bool Desc) Sort;
        public long? WentTo;
        public object CaptureFilter() => CurrentFilter ?? new FilterGroup();
        public void RestoreFilter(object snapshot) { CurrentFilter = (FilterGroup)snapshot; ShownFlag = null; }
        public string? ShownFlag;
        public void ShowFlag(string flagId) => ShownFlag = flagId;
        public void ClearFilter() { Cleared++; CurrentFilter = null; }
        public IReadOnlyList<string?> GetFlags(IReadOnlyList<long> rows) => rows.Select(r => FlagState.TryGetValue(r, out var f) ? f : null).ToList();
        public (string? Column, bool Descending) GetSort() => Sort;
        public Task<string?> SortAsync(string? column, bool descending) { Sort = (column, descending); return Task.FromResult<string?>(null); }
        public string? GoToRow(long row) { WentTo = row; return null; }
    }

    private AssistantChatViewModel Chat(FakeModel model, AppSettings? settings = null, string? caseDir = null, PageViewModel? grid = null,
        Func<string, string, bool>? confirm = null, Func<PageViewModel?>? page = null) =>
        new(settings ?? new AppSettings(), new ApiKeyStore(Path.Combine(_dir, "keys")), () => new HttpClient(), () => caseDir, page ?? (() => grid),
            () => model, confirm ?? ((_, _) => false), a => a(), (_, _) => Task.FromResult<AssistantExtraTools?>(null));

    /// <summary>A page without a grid: its own Spectre page, name, state and tools.</summary>
    private sealed class FakePage : PageViewModel
    {
        public FakePage(SpectrePage page, string title) { AssistantPage = page; Title = title; }
        public override string Title { get; }
        public override SpectrePage AssistantPage { get; }
        public string? State;
        public List<IAssistantTool> Tools = new();
        public override string? AssistantState() => State;
        public override IEnumerable<IAssistantTool> AssistantTools(IAssistantHost host) => Tools;
    }

    private static async Task Send(AssistantChatViewModel vm, string text)
    {
        vm.InputText = text;
        await vm.SendCommand.ExecuteAsync(null);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public bool Down; public string Body = "{\"data\":[]}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("connection refused");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Body) });
        }
    }

    [Fact]
    public async Task Local_ai_card_shows_when_ollama_is_down_or_empty_and_hides_when_a_model_is_ready()
    {
        var h = new StubHandler { Down = true };
        var vm = new AssistantChatViewModel(new AppSettings { AiProvider = "ollama" }, new ApiKeyStore(Path.Combine(_dir, "keys")), () => new HttpClient(h, false), () => null,
            null, null, (_, _) => false, a => a(), (_, _) => Task.FromResult<AssistantExtraTools?>(null));
        Assert.False(vm.ShowLocalAiCard);
        await vm.CheckProviderAsync();
        Assert.True(vm.ShowLocalAiCard);   // not running
        h.Down = false;
        await vm.CheckProviderAsync();
        Assert.True(vm.ShowLocalAiCard);   // running, no models
        h.Body = "{\"data\":[{\"id\":\"qwen3:8b\"}]}";
        await vm.CheckProviderAsync();
        Assert.False(vm.ShowLocalAiCard);  // ready
    }

    [Fact]
    public async Task Local_ai_card_button_runs_the_setup_and_hides_the_card_once_ready()
    {
        var h = new StubHandler();
        var vm = new AssistantChatViewModel(new AppSettings { AiProvider = "ollama" }, new ApiKeyStore(Path.Combine(_dir, "keys")), () => new HttpClient(h, false), () => null,
            null, null, (_, _) => false, a => a(), (_, _) => Task.FromResult<AssistantExtraTools?>(null));
        await vm.CheckProviderAsync();
        Assert.True(vm.ShowLocalAiCard);
        vm.SetUpLocalAiOverride = () => { h.Body = "{\"data\":[{\"id\":\"qwen3:8b\"}]}"; return Task.FromResult("Local AI is ready."); };
        await vm.SetUpLocalAiCommand.ExecuteAsync(null);
        Assert.Equal("Local AI is ready.", vm.LocalAiStatus);
        Assert.False(vm.ShowLocalAiCard);
    }

    [Fact]
    public async Task Starter_chips_send_their_prompt_and_hide_after_the_first_message()
    {
        var model = new FakeModel();
        var vm = Chat(model);
        Assert.True(vm.ShowStarters);
        Assert.Equal(new[] { "Any encoded PowerShell?", "Summarize this case", "What ran at logon?" }, AssistantChatViewModel.StarterPrompts);
        await vm.SendStarterCommand.ExecuteAsync("Summarize this case");
        Assert.Contains("Summarize this case", Assert.Single(model.Sent));
        Assert.Contains(vm.Messages, m => m.Role == ChatRole.You && m.Text == "Summarize this case");
        Assert.False(vm.ShowStarters);
        vm.NewChatCommand.Execute(null);
        Assert.True(vm.ShowStarters);
    }

    [Fact]
    public async Task A_page_switch_mid_conversation_asks_and_going_back_removes_the_question()
    {
        var analyze = new FakePage(SpectrePage.Analyze, "Analyze");
        var explore = new FakePage(SpectrePage.Ingest, "Ingest");   // Analyze/Explore/Findings share one conversation, so use another page
        PageViewModel current = analyze;
        var vm = Chat(new FakeModel(), page: () => current);

        current = explore; vm.OnPageChanged();           // empty conversation: no question, it moves along
        Assert.False(vm.HasPageSwitchNotice);
        await Send(vm, "hello");                          // conversation now belongs to Explore

        current = analyze; vm.OnPageChanged();
        Assert.Equal("You switched to Analyze. Start a new conversation?", vm.PageSwitchNotice);
        current = explore; vm.OnPageChanged();           // straight back: question gone
        Assert.False(vm.HasPageSwitchNotice);

        current = analyze; vm.OnPageChanged();
        vm.KeepConversationCommand.Execute(null);         // keep: conversation moves to Analyze, history stays
        Assert.False(vm.HasPageSwitchNotice);
        Assert.Equal(2, vm.Messages.Count);
        current = explore; vm.OnPageChanged();
        Assert.True(vm.HasPageSwitchNotice);
        vm.NewChatCommand.Execute(null);                  // new conversation: history cleared, notice gone
        Assert.False(vm.HasPageSwitchNotice);
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task Moving_between_Analyze_Explore_and_Findings_keeps_the_conversation_without_asking()
    {
        var pages = new[] { SpectrePage.Analyze, SpectrePage.Explore, SpectrePage.Findings }.Select(p => new FakePage(p, p.ToString())).ToArray();
        PageViewModel current = pages[0];
        var vm = Chat(new FakeModel(), page: () => current);
        await Send(vm, "hello");
        foreach (var from in pages)
            foreach (var to in pages.Where(p => p != from))
            {
                current = from; vm.OnPageChanged();
                current = to; vm.OnPageChanged();
                Assert.False(vm.HasPageSwitchNotice, $"{from.Title} -> {to.Title}");
            }
        // from there to another page it still asks, and back to the review pages removes the question
        current = new FakePage(SpectrePage.Tools, "Tools"); vm.OnPageChanged();
        Assert.True(vm.HasPageSwitchNotice);
        current = pages[2]; vm.OnPageChanged();
        Assert.False(vm.HasPageSwitchNotice);
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task Ask_opens_the_chat_and_sends_the_text()
    {
        var settings = new AppSettings { AssistantExpanded = false };
        var vm = Chat(new FakeModel(), settings);
        vm.Ask("Look at finding F-1");
        for (var i = 0; i < 100 && vm.IsBusy; i++) await Task.Delay(20);
        Assert.True(vm.IsExpanded);
        Assert.Contains(vm.Messages, m => m.Role == ChatRole.You && m.Text == "Look at finding F-1");
    }

    [Fact]
    public async Task Sending_while_the_page_switch_question_shows_keeps_the_conversation()
    {
        var analyze = new FakePage(SpectrePage.Analyze, "Analyze");
        PageViewModel current = analyze;
        var vm = Chat(new FakeModel(), page: () => current);
        await Send(vm, "hello");
        current = new FakePage(SpectrePage.Tools, "Tools"); vm.OnPageChanged();
        Assert.True(vm.HasPageSwitchNotice);
        await Send(vm, "and now?");
        Assert.False(vm.HasPageSwitchNotice);
        Assert.Equal(4, vm.Messages.Count);
    }

    [Fact]
    public async Task Send_adds_both_messages_and_clears_input()
    {
        var vm = Chat(new FakeModel());
        await Send(vm, "hello");
        Assert.Equal(new[] { ChatRole.You, ChatRole.Assistant }, vm.Messages.Select(m => m.Role));
        Assert.Equal("ok", vm.Messages[1].Text);
        Assert.Equal("", vm.InputText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task The_current_view_goes_with_the_message_once_then_only_as_unchanged()
    {
        var model = new FakeModel();
        var vm = Chat(model, grid: new FakeGrid());
        await Send(vm, "anything odd here?");
        await Send(vm, "and now?");
        Assert.Contains("<current_view>", model.Sent[0]);
        Assert.Contains("evtx.csv", model.Sent[0]);
        Assert.EndsWith("anything odd here?", model.Sent[0]);
        Assert.Contains("Same as in my previous message", model.Sent[1]);
        Assert.Equal("anything odd here?", vm.Messages[0].Text); // the chat shows only what the examiner typed
    }

    [Fact]
    public async Task Without_a_grid_only_the_feature_index_precedes_the_message()
    {
        var model = new FakeModel();
        var vm = Chat(model);
        await Send(vm, "hello");
        await Send(vm, "again");
        Assert.StartsWith("<app_features>", model.Sent[0]);
        Assert.DoesNotContain("<current_view>", model.Sent[0]);
        Assert.EndsWith("hello", model.Sent[0]);
        Assert.Equal("again", model.Sent[1]);
    }

    [Fact]
    public async Task Cloud_provider_asks_once_per_case_and_remembers()
    {
        var model = new FakeModel(); int asked = 0;
        var vm = Chat(model, new AppSettings { AiProvider = "anthropic" }, _dir, confirm: (_, msg) => { asked++; Assert.Contains("Anthropic", msg); return true; });
        await Send(vm, "one");
        await Send(vm, "two");
        Assert.Equal(1, asked);
        Assert.Equal(2, model.Calls);
        Assert.True(AssistantConsent.Has(_dir, "anthropic"));
    }

    [Fact]
    public async Task Declined_consent_sends_nothing_and_suggests_local()
    {
        var model = new FakeModel();
        var vm = Chat(model, new AppSettings { AiProvider = "openai" }, _dir, confirm: (_, _) => false);
        await Send(vm, "secret case question");
        Assert.Equal(0, model.Calls);
        Assert.False(AssistantConsent.Has(_dir, "openai"));
        var line = Assert.Single(vm.Messages);
        Assert.Equal(ChatRole.Status, line.Role);
        Assert.Contains("Local", line.Text);
        Assert.Equal("secret case question", vm.InputText); // kept so nothing is lost
    }

    [Fact]
    public async Task Local_provider_never_asks()
    {
        var vm = Chat(new FakeModel(), new AppSettings { AiProvider = "ollama" }, _dir, confirm: (_, _) => throw new InvalidOperationException("asked"));
        await Send(vm, "hi");
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task Model_error_becomes_a_status_line()
    {
        var model = new FakeModel { OnUser = _ => throw new AssistantModelException("Ollama isn't running") };
        var vm = Chat(model);
        await Send(vm, "hi");
        var last = vm.Messages.Last();
        Assert.Equal(ChatRole.Status, last.Role);
        Assert.Equal("Ollama isn't running", last.Text);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Missing_key_is_a_friendly_status()
    {
        var vm = new AssistantChatViewModel(new AppSettings { AiProvider = "anthropic" }, new ApiKeyStore(Path.Combine(_dir, "keys")), () => new HttpClient(),
            () => null, null, null, (_, _) => true, a => a(), (_, _) => Task.FromResult<AssistantExtraTools?>(null));
        await Send(vm, "hi");
        Assert.Equal(ChatRole.Status, vm.Messages.Last().Role);
        Assert.Contains("API key", vm.Messages.Last().Text);
    }

    [Fact]
    public async Task Stop_cancels_the_request()
    {
        var started = new TaskCompletionSource();
        var model = new FakeModel { OnUser = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Say("never"); } };
        var vm = Chat(model);
        vm.InputText = "long one";
        var run = vm.SendCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsBusy);
        vm.StopCommand.Execute(null);
        await run;
        Assert.False(vm.IsBusy);
        Assert.Equal("Stopped.", vm.Messages.Last().Text);
    }

    [Fact]
    public async Task New_chat_clears_messages()
    {
        var vm = Chat(new FakeModel());
        await Send(vm, "hi");
        vm.NewChatCommand.Execute(null);
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public void Model_label_shows_provider_and_model()
    {
        var vm = Chat(new FakeModel(), new AppSettings { AiProvider = "ollama", AiModel = "qwen3:8b" });
        Assert.Equal("Local · qwen3:8b", vm.ModelLabel);
    }

    // ------------------------------------------------------------ proposals

    private static FakeModel ProposingModel(string tool, JsonObject input)
    {
        int n = 0;
        return new FakeModel { OnUser = _ => Task.FromResult(n++ == 0
            ? new ModelStep(Array.Empty<string>(), new[] { new ToolCall("c1", tool, input) }, ModelStopReason.ToolUse)
            : Say("proposed")) };
    }

    [Fact]
    public async Task Filter_tool_call_adds_a_card_and_applies_nothing()
    {
        var grid = new FakeGrid();
        var vm = Chat(ProposingModel("grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" }), grid: grid);
        await Send(vm, "find process creation");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        Assert.Equal("Filter", card.Title);
        Assert.Contains("EventId", card.Detail);
        Assert.Empty(grid.Filters);
        Assert.True(card.IsPending);
    }

    [Fact]
    public async Task Filter_card_applies_only_after_click_and_only_once()
    {
        var grid = new FakeGrid();
        var vm = Chat(ProposingModel("grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" }), grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        card.ApplyCommand.Execute(null);
        Assert.Single(grid.Filters);
        Assert.Equal(ProposalState.Applied, card.State);
        Assert.False(card.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Filter_card_refuses_when_another_csv_is_open()
    {
        var grid = new FakeGrid();
        var vm = Chat(ProposingModel("grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" }), grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        grid.CurrentCsv = @"C:\case\other.csv";
        card.ApplyCommand.Execute(null);
        Assert.Empty(grid.Filters);
        Assert.True(card.IsPending);
        Assert.Contains("evtx.csv", card.Note);
        grid.CurrentCsv = @"c:\CASE\evtx.csv"; // same file, different case
        card.ApplyCommand.Execute(null);
        Assert.Single(grid.Filters);
    }

    [Fact]
    public async Task Flag_card_shows_reason_and_count_and_guards_the_csv()
    {
        var grid = new FakeGrid();
        var vm = Chat(ProposingModel("grid_flag_rows", new JsonObject
        {
            ["rows"] = new JsonArray(3, 5), ["flag"] = "red", ["reason"] = "encoded powershell",
        }), grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        Assert.Equal("Flag 2 rows as RED and show them", card.Title);
        Assert.Equal("encoded powershell", card.Reason);
        grid.CurrentCsv = null;
        card.ApplyCommand.Execute(null);
        Assert.Empty(grid.Flags);
        grid.CurrentCsv = @"C:\case\evtx.csv";
        card.ApplyCommand.Execute(null);
        var applied = Assert.Single(grid.Flags);
        Assert.Equal(new long[] { 2, 4 }, applied.Rows);   // the model said rows 3 and 5 as shown (1-based)
        Assert.Equal("red", applied.Flag);
    }

    [Fact]
    public void Dismiss_never_touches_the_grid()
    {
        var grid = new FakeGrid();
        var card = new ProposalCardViewModel(new FlagProposal("c", grid.CurrentCsv, new long[] { 1 }, "red", "why"), grid);
        card.DismissCommand.Execute(null);
        card.ApplyCommand.Execute(null);
        Assert.Empty(grid.Flags);
        Assert.Equal(ProposalState.Dismissed, card.State);
    }

    [Fact]
    public void Apply_error_from_the_grid_keeps_the_card_pending()
    {
        var grid = new FakeGrid { FlagError = "Unknown flag 'red'." };
        var card = new ProposalCardViewModel(new FlagProposal("c", grid.CurrentCsv, new long[] { 1 }, "red", "why"), grid);
        card.ApplyCommand.Execute(null);
        Assert.True(card.IsPending);
        Assert.Equal("Unknown flag 'red'.", card.Note);
    }

    [Fact]
    public async Task App_state_goes_with_the_message_only_when_it_changes()
    {
        var model = new FakeModel();
        var page = new FakePage(SpectrePage.Analyze, "Analyze") { State = "Case CSV files: 12 (use case_list_files)" };
        var vm = Chat(model, page: () => page);
        await Send(vm, "one");
        await Send(vm, "two");
        page.State = "Case CSV files: 13";
        await Send(vm, "three");
        Assert.StartsWith("<app_state>\nPage: Analyze\nCase CSV files: 12", model.Sent[0]);
        Assert.Contains("case_list_files", model.Sent[0]);
        Assert.DoesNotContain("<app_state>", model.Sent[1]);   // (the bundled knowledge base may add a <knowledge> block for a word like "two")
        Assert.EndsWith("two", model.Sent[1]);
        Assert.StartsWith("<app_state>\nPage: Analyze\nCase CSV files: 13", model.Sent[2]);
    }

    [Fact]
    public async Task App_state_and_features_follow_the_current_page()
    {
        var model = new FakeModel();
        PageViewModel page = new FakePage(SpectrePage.Ingest, "Ingest") { State = "Selected: 2 sources" };
        var vm = Chat(model, page: () => page);
        await Send(vm, "one");
        await Send(vm, "two");                         // same page, same state: nothing repeated
        page = new FakePage(SpectrePage.Tools, "Tools");
        await Send(vm, "three");                       // moved: new Page line and the new page's feature index
        page = new FakePage(SpectrePage.ProcessTree, "Process tree view");
        await Send(vm, "four");
        Assert.Contains("<app_state>\nPage: Ingest\nSelected: 2 sources\n</app_state>", model.Sent[0]);
        Assert.Contains("<app_features>", model.Sent[0]);
        Assert.DoesNotContain("<app_state>", model.Sent[1]);
        Assert.DoesNotContain("<app_features>", model.Sent[1]);
        Assert.EndsWith("two", model.Sent[1]);
        Assert.StartsWith("<app_state>\nPage: Tools\n</app_state>", model.Sent[2]);
        Assert.Contains("<app_features>", model.Sent[2]);        // re-sent for the new page
        Assert.Contains("Page: Process tree\n", model.Sent[3]);
        Assert.Equal(3, model.Sent.Count(s => s.Contains("<app_features>")));
        vm.NewChatCommand.Execute(null);
        await Send(vm, "again");
        Assert.Contains("<app_state>", model.Sent[4]);          // a new chat has seen nothing
        Assert.Contains("<app_features>", model.Sent[4]);
    }

    [Fact]
    public async Task Tools_offered_follow_the_page()
    {
        var analyze = new FakeModel();
        var grid = new FakeGrid();
        var vm = Chat(analyze, caseDir: _dir, page: () => grid);
        await Send(vm, "x");
        Assert.Contains(analyze.LastTools, t => t.Name == "grid_apply_filter");
        Assert.Contains(analyze.LastTools, t => t.Name == "analyze_open_file");

        // same conversation, now on Ingest: the grid tools are gone, case tools stay, the page's own tool is added
        var ingest = new FakePage(SpectrePage.Ingest, "Ingest");
        ingest.Tools.Add(new PageProbeTool());
        PageViewModel current = grid;
        var model = new FakeModel();
        var chat = Chat(model, caseDir: _dir, page: () => current);
        await Send(chat, "on analyze");
        Assert.Contains(model.LastTools, t => t.Name == "grid_get_context");
        current = ingest;
        await Send(chat, "on ingest");
        Assert.DoesNotContain(model.LastTools, t => t.Name.StartsWith("grid_"));
        Assert.DoesNotContain(model.LastTools, t => t.Name == "analyze_open_file");
        Assert.Contains(model.LastTools, t => t.Name == "csv_query");
        Assert.Contains(model.LastTools, t => t.Name == "page_probe");
        Assert.Contains(model.LastTools, t => t.Name == "skill_help");
        current = grid;
        await Send(chat, "back");
        Assert.Contains(model.LastTools, t => t.Name == "grid_get_context");
        Assert.DoesNotContain(model.LastTools, t => t.Name == "page_probe");
    }

    private sealed class PageProbeTool : IAssistantTool
    {
        public ToolSpec Spec { get; } = new("page_probe", "A page tool.", new JsonObject { ["type"] = "object" }, ToolKind.Read);
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) => Task.FromResult(new ToolOutcome("ok", false));
    }

    [Fact]
    public async Task Without_a_case_no_case_tools_are_offered()
    {
        var model = new FakeModel();
        await Send(Chat(model, grid: new FakeGrid()), "x");
        Assert.DoesNotContain(model.LastTools, t => t.Name == "csv_query");
        Assert.Contains(model.LastTools, t => t.Name == "grid_get_context");
    }

    [Fact]
    public async Task The_grid_is_the_current_pages_not_a_fixed_one()
    {
        var model = new FakeModel();
        PageViewModel page = new FakePage(SpectrePage.Explore, "Explore");
        var vm = Chat(model, page: () => page);
        await Send(vm, "one");
        Assert.DoesNotContain("<current_view>", model.Sent[0]);
        page = new FakeGrid();
        await Send(vm, "two");
        Assert.Contains("<current_view>", model.Sent[1]);
        Assert.Contains("evtx.csv", model.Sent[1]);
    }

    [Fact]
    public void Mode_migrates_from_the_old_floating_flag_and_old_files_still_load()
    {
        Assert.Equal("popup", new AppSettings().AssistantMode);
        Assert.False(new AppSettings().AssistantExpanded);
        Assert.Equal("docked", new AppSettings { AssistantFloating = false }.AssistantMode);
        Assert.Equal("popup", new AppSettings { AssistantFloating = true }.AssistantMode);
        Assert.Equal("window", new AppSettings { AssistantFloating = false, AssistantMode = "window" }.AssistantMode);
        Assert.Equal("popup", new AppSettings { AssistantMode = "junk" }.AssistantMode);

        var dir = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "old.json");
        File.WriteAllText(path, "{ \"assistantFloating\": false, \"assistantOpenAnalyze\": true, \"assistantOpenExplore\": true, \"assistantDockWidth\": 420 }");
        var old = new JsonSettingsStore(path).Load();
        Assert.Equal("docked", old.AssistantMode);
        Assert.Equal(420, old.AssistantDockWidth);
        File.WriteAllText(path, "{ \"assistantMode\": \"window\", \"assistantExpanded\": true, \"assistantPopupWidth\": 400, \"assistantPopupHeight\": 500 }");
        var cur = new JsonSettingsStore(path).Load();
        Assert.Equal("window", cur.AssistantMode);
        Assert.True(cur.AssistantExpanded);
        Assert.Equal((400, 500), (cur.AssistantPopupWidth, cur.AssistantPopupHeight));
        new JsonSettingsStore(path).Save(old);
        Assert.Equal("docked", new JsonSettingsStore(path).Load().AssistantMode);
    }

    [Fact]
    public void Mode_and_expanded_round_trip_through_the_view_model()
    {
        var s = new AppSettings();
        var vm = Chat(new FakeModel(), s);
        Assert.True(vm.IsPopupMode);
        Assert.False(vm.IsExpanded);
        vm.SetModeCommand.Execute("docked");
        Assert.Equal("docked", s.AssistantMode);
        Assert.True(s.AssistantExpanded);                 // choosing a mode opens Spectre
        Assert.True(vm.IsDockedMode);
        Assert.True(vm.CanMinimize);
        vm.SetModeCommand.Execute("window");
        Assert.True(vm.IsWindowMode);
        Assert.False(vm.CanMinimize);                     // the window has its own close
        vm.SetModeCommand.Execute("nonsense");
        Assert.Equal("window", s.AssistantMode);
        vm.MinimizeCommand.Execute(null);
        Assert.False(s.AssistantExpanded);
        Assert.Equal("window", s.AssistantMode);         // collapsing keeps the mode
        vm.ToggleExpandedCommand.Execute(null);
        Assert.True(vm.IsExpanded);
    }

    [Fact]
    public async Task Case_tools_are_offered_and_open_file_only_with_a_grid()
    {
        var withGrid = new FakeModel();
        await Send(Chat(withGrid, caseDir: _dir, grid: new FakeGrid()), "x");
        Assert.Contains(withGrid.LastTools, t => t.Name == "case_list_files");
        Assert.Contains(withGrid.LastTools, t => t.Name == "csv_query" && t.Kind == ToolKind.Read);
        Assert.Contains(withGrid.LastTools, t => t.Name == "analyze_open_file" && t.Kind == ToolKind.Proposal);
        var noGrid = new FakeModel();
        await Send(Chat(noGrid, caseDir: _dir, page: () => new FakePage(SpectrePage.Explore, "Explore")), "x");
        Assert.Contains(noGrid.LastTools, t => t.Name == "csv_query");
        Assert.DoesNotContain(noGrid.LastTools, t => t.Name == "analyze_open_file");
        Assert.DoesNotContain(noGrid.LastTools, t => t.Name.StartsWith("grid_"));
        Assert.Contains(noGrid.LastTools, t => t.Name == "skill_help");
        Assert.Contains(withGrid.LastTools, t => t.Name == "grid_apply_filter");
        Assert.Contains(withGrid.LastTools, t => t.Name == "skill_help");
    }

    [Fact]
    public async Task Feature_index_goes_with_the_first_message_only()
    {
        var model = new FakeModel();
        var vm = Chat(model, grid: new FakeGrid());
        await Send(vm, "one");
        await Send(vm, "two");
        Assert.Contains("<app_features>", model.Sent[0]);
        Assert.Contains("analyze-saved-filters", model.Sent[0]);
        Assert.DoesNotContain("<app_features>", model.Sent[1]);
    }

    private string MakeCaseCsv()
    {
        var csv = Path.Combine(_dir, "other.csv");
        File.WriteAllText(csv, "A,B\n1,2\n");
        return csv;
    }

    [Fact]
    public async Task Open_file_card_opens_only_after_click_with_its_filter()
    {
        var csv = MakeCaseCsv();
        var grid = new FakeGrid();
        var vm = Chat(ProposingModel("analyze_open_file", new JsonObject { ["file"] = "other.csv", ["filter"] = "[A] = '1'", ["reason"] = "look" }), caseDir: _dir, grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        Assert.Equal("Open other.csv", card.Title);
        Assert.Contains("A", card.Detail);
        Assert.Empty(grid.Opened);
        await card.ApplyCommand.ExecuteAsync(null);
        var opened = Assert.Single(grid.Opened);
        Assert.Equal(csv, opened.Path);
        Assert.NotNull(opened.Filter);
        Assert.Equal(ProposalState.Applied, card.State);
    }

    [Fact]
    public async Task Open_file_card_refuses_when_the_file_is_gone_or_the_grid_fails()
    {
        var csv = MakeCaseCsv();
        var grid = new FakeGrid();
        var card = new ProposalCardViewModel(new OpenFileProposal("c", csv, null, "", "r"), grid);
        File.Delete(csv);
        await card.ApplyCommand.ExecuteAsync(null);
        Assert.True(card.IsPending);
        Assert.Contains("no longer", card.Note);
        Assert.Empty(grid.Opened);

        File.WriteAllText(csv, "A\n1\n");
        grid.OpenError = "Unknown column(s) in the open CSV: Z";
        await card.ApplyCommand.ExecuteAsync(null);
        Assert.True(card.IsPending);
        Assert.Contains("Z", card.Note);
        card.DismissCommand.Execute(null);
        Assert.Equal(ProposalState.Dismissed, card.State);
    }

    [Fact]
    public async Task Open_file_card_without_a_grid_says_where_to_apply_it()
    {
        var card = new ProposalCardViewModel(new OpenFileProposal("c", MakeCaseCsv(), null, "", "r"), null);
        await card.ApplyCommand.ExecuteAsync(null);
        Assert.True(card.IsPending);
        Assert.Contains("Analyze", card.Note);
    }

    [Theory]
    [InlineData(@"C:\a\b.csv", @"c:\A\B.csv", true)]
    [InlineData(@"C:\a\b.csv", @"C:\a\c.csv", false)]
    [InlineData(null, @"C:\a\b.csv", false)]
    [InlineData(@"C:\a\b.csv", null, false)]
    public void SameCsv_compares_paths(string? a, string? b, bool expected) => Assert.Equal(expected, ProposalCardViewModel.SameCsv(a, b));

    // ------------------------------------------------------------ auto-mode

    private static AppSettings Auto(bool on = true) => new() { AssistantAutoMode = on };

    [Fact]
    public async Task Auto_mode_is_off_by_default_and_leaves_a_pending_card()
    {
        Assert.False(new AppSettings().AssistantAutoMode);
        var model = ProposingModel("grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" });
        var grid = new FakeGrid();
        var vm = Chat(model, grid: grid);
        await Send(vm, "x");
        Assert.True(vm.Messages.Single(m => m.IsProposal).Card!.IsPending);
        Assert.Empty(grid.Filters);
        Assert.Contains("NOT applied", model.ToolResults.Single());
    }

    [Fact]
    public async Task Auto_mode_applies_at_once_and_tells_the_model()
    {
        var model = ProposingModel("grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" });
        var grid = new FakeGrid();
        var vm = Chat(model, Auto(), grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        Assert.Equal(ProposalState.Applied, card.State);
        Assert.StartsWith("Applied: ", card.Note);
        Assert.True(card.CanUndo);
        Assert.Single(grid.Filters);
        Assert.Equal(AssistantGridTools.AppliedText, model.ToolResults.Single());
    }

    [Fact]
    public async Task Undo_of_an_auto_filter_restores_the_previous_filter()
    {
        var before = new FilterGroup();
        var grid = new FakeGrid { CurrentFilter = before };
        var vm = Chat(ProposingModel("grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" }), Auto(), grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        Assert.NotSame(before, grid.CurrentFilter);
        await card.UndoCommand.ExecuteAsync(null);
        Assert.Same(before, grid.CurrentFilter);
        Assert.Equal(ProposalState.Undone, card.State);
        Assert.False(card.CanUndo);
    }

    [Fact]
    public async Task Undo_of_auto_flags_restores_each_rows_previous_flag()
    {
        var grid = new FakeGrid();
        grid.FlagState[3] = "blue"; // source row 3 was blue, source row 5 was not flagged
        var vm = Chat(ProposingModel("grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(4, 6), ["flag"] = "red", ["reason"] = "r" }), Auto(), grid: grid);
        await Send(vm, "x");
        Assert.Equal("red", grid.FlagState[3]);
        Assert.Equal("red", grid.FlagState[5]);
        await vm.Messages.Single(m => m.IsProposal).Card!.UndoCommand.ExecuteAsync(null);
        Assert.Equal("blue", grid.FlagState[3]);
        Assert.False(grid.FlagState.ContainsKey(5));
    }

    [Fact]
    public async Task Undo_is_refused_while_another_csv_is_open()
    {
        var grid = new FakeGrid();
        var vm = Chat(ProposingModel("grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" }), Auto(), grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        grid.CurrentCsv = @"C:\case\other.csv";
        await card.UndoCommand.ExecuteAsync(null);
        Assert.Equal(ProposalState.Applied, card.State);
        Assert.Contains("evtx.csv", card.Note);
    }

    [Fact]
    public async Task Auto_mode_guard_failure_is_not_applied_and_the_model_is_told()
    {
        var model = ProposingModel("grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(1), ["flag"] = "red", ["reason"] = "r" });
        var grid = new FakeGrid { FlagError = "Unknown flag 'red'." };
        var vm = Chat(model, Auto(), grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        Assert.True(card.IsPending);
        Assert.False(card.CanUndo);
        Assert.StartsWith("Not applied: ", card.Note);
        Assert.Contains("NOT applied", model.ToolResults.Single());
    }

    [Fact]
    public async Task Auto_apply_refuses_when_another_csv_is_open()
    {
        var grid = new FakeGrid { CurrentCsv = @"C:\case\other.csv" };
        var card = new ProposalCardViewModel(new FilterProposal("c", new FilterGroup(), "f", @"C:\case\evtx.csv"), grid);
        Assert.Contains("evtx.csv", await card.AutoApplyAsync());
        Assert.True(card.IsPending);
        Assert.Empty(grid.Filters);
    }

    [Fact]
    public async Task Auto_mode_open_file_undo_reopens_the_previous_csv_and_filter()
    {
        var csv = MakeCaseCsv();
        var before = new FilterGroup();
        var grid = new FakeGrid { CurrentFilter = before };
        var vm = Chat(ProposingModel("analyze_open_file", new JsonObject { ["file"] = "other.csv", ["reason"] = "look" }), Auto(), caseDir: _dir, grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        Assert.Equal(csv, Assert.Single(grid.Opened).Path);
        Assert.True(card.CanUndo);
        grid.CurrentCsv = csv; // what the real grid shows after opening
        await card.UndoCommand.ExecuteAsync(null);
        Assert.Equal(2, grid.Opened.Count);
        Assert.Equal(@"C:\case\evtx.csv", grid.Opened[1].Path);
        Assert.Same(before, grid.CurrentFilter);
    }

    [Fact]
    public async Task Auto_mode_runs_the_new_grid_actions_and_sort_undo_restores_the_sort()
    {
        var grid = new FakeGrid { Sort = ("Payload", true) };
        var vm = Chat(ProposingModel("grid_sort", new JsonObject { ["column"] = "EventId" }), Auto(), grid: grid);
        await Send(vm, "x");
        Assert.Equal(("EventId", false), grid.Sort);
        await vm.Messages.Single(m => m.IsProposal).Card!.UndoCommand.ExecuteAsync(null);
        Assert.Equal(("Payload", true), grid.Sort);

        var g2 = new FakeGrid();
        await Send(Chat(ProposingModel("grid_go_to_row", new JsonObject { ["row"] = 43 }), Auto(), grid: g2), "x");   // as shown
        Assert.Equal(42, g2.WentTo);
        var g3 = new FakeGrid();
        await Send(Chat(ProposingModel("grid_clear_filter", new JsonObject()), Auto(), grid: g3), "x");
        Assert.Equal(1, g3.Cleared);
    }

    [Fact]
    public void Auto_mode_never_skips_the_write_confirmation()
    {
        var asked = 0;
        var vm = Chat(new FakeModel(), Auto(), confirm: (_, _) => { asked++; return false; });
        Assert.False(vm.ConfirmWriteAsync(new ToolCall("c", "t", new JsonObject())).Result);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void Auto_mode_setting_round_trips_through_the_view_model()
    {
        var s = new AppSettings();
        var vm = Chat(new FakeModel(), s);
        vm.AutoMode = true;
        Assert.True(s.AssistantAutoMode);
        s.AssistantAutoMode = false;
        Assert.False(vm.AutoMode);
    }

    [Fact]
    public async Task Page_buttons_drive_the_card_by_key_and_ignore_unknown_ones()
    {
        var grid = new FakeGrid();
        var vm = Chat(ProposingModel("grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" }), grid: grid);
        await Send(vm, "x");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        vm.HandleCardAction("apply", "nope");
        vm.HandleCardAction("shout", card.Key);
        Assert.True(card.IsPending);
        vm.HandleCardAction("undo", card.Key);   // nothing to undo yet
        Assert.True(card.IsPending);
        vm.HandleCardAction("apply", card.Key);
        Assert.Equal(ProposalState.Applied, card.State);
        Assert.Single(grid.Filters);
    }

    [Fact]
    public async Task Apply_all_applies_the_pending_cards_of_one_reply_in_order()
    {
        var grid = new FakeGrid();
        var model = new FakeModel
        {
            OnUser = _ => Task.FromResult(new ModelStep(Array.Empty<string>(), new[]
            {
                new ToolCall("c1", "grid_clear_filter", new JsonObject()),
                new ToolCall("c2", "grid_clear_filter", new JsonObject()),
            }, ModelStopReason.ToolUse)),
        };
        var vm = Chat(model, grid: grid);
        await Send(vm, "x");
        var cards = vm.Messages.Where(m => m.IsProposal).Select(m => m.Card!).ToList();
        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.True(c.IsPending));
        cards[1].DismissCommand.Execute(null);                  // a dismissed card is not applied
        vm.HandleCardAction("applyAll", "nope");
        await vm.ApplyAllAsync("nope");
        Assert.True(cards[0].IsPending);
        await vm.ApplyAllAsync(cards[0].Key);
        Assert.Equal(ProposalState.Applied, cards[0].State);
        Assert.Equal(ProposalState.Dismissed, cards[1].State);
        Assert.Equal(1, grid.Cleared);
    }

    // ------------------------------------------------------------ end to end: tool call -> card -> page -> grid

    private static FakeModel ProposingEachTime(string tool, JsonObject input) => new()
    {
        OnUser = _ => Task.FromResult(new ModelStep(Array.Empty<string>(), new[] { new ToolCall("c1", tool, (JsonObject)input.DeepClone()) }, ModelStopReason.ToolUse)),
    };

    [Fact]
    public async Task Auto_mode_switched_at_runtime_counts_from_the_next_proposal()
    {
        var settings = new AppSettings();
        var model = ProposingEachTime("grid_apply_filter", new JsonObject { ["filter"] = "[EventId] = '4688'" });
        var grid = new FakeGrid();
        var vm = Chat(model, settings, grid: grid);
        await Send(vm, "filter to 4688");
        Assert.True(vm.Messages.Last(m => m.IsProposal).Card!.IsPending);
        Assert.Empty(grid.Filters);

        settings.AssistantAutoMode = true;   // what the panel switch and Options > AI both write
        await Send(vm, "filter to 4688");
        var card = vm.Messages.Last(m => m.IsProposal).Card!;
        Assert.Equal(ProposalState.Applied, card.State);
        Assert.True(card.CanUndo);
        Assert.Single(grid.Filters);
        Assert.Equal(new[] { AssistantGridTools.ProposedText, AssistantGridTools.AppliedText }, model.ToolResults);

        vm.AutoMode = false;
        await Send(vm, "filter to 4688");
        Assert.True(vm.Messages.Last(m => m.IsProposal).Card!.IsPending);
        Assert.Single(grid.Filters);
    }

    [Fact]
    public async Task Proposals_from_a_background_thread_wait_for_the_ui_queue()
    {
        // the real ui callback queues work on the dispatcher; the tool result must wait for the card (and auto-apply) to happen there
        var queue = new System.Collections.Concurrent.BlockingCollection<Action>();
        var grid = new FakeGrid();
        var model = ProposingModel("grid_flag_rows", new JsonObject { ["rows"] = new JsonArray(4), ["flag"] = "RED" });   // a label; row 4 as shown = source 3
        var vm = new AssistantChatViewModel(Auto(), new ApiKeyStore(Path.Combine(_dir, "keys")), () => new HttpClient(), () => null, () => grid,
            () => model, (_, _) => false, queue.Add, (_, _) => Task.FromResult<AssistantExtraTools?>(null));
        vm.InputText = "flag row 3 as red";
        var send = Task.Run(() => vm.SendCommand.ExecuteAsync(null));
        while (!send.IsCompleted) { if (queue.TryTake(out var a, 20)) a(); }
        await send;
        while (queue.TryTake(out var rest)) rest();
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        Assert.Equal(ProposalState.Applied, card.State);
        Assert.Equal("red", grid.FlagState[3]);
        Assert.Equal(AssistantGridTools.AppliedText, model.ToolResults.Single());
    }

    [Fact]
    public async Task Page_click_with_the_card_json_id_applies_the_card()
    {
        var grid = new FakeGrid();
        var vm = Chat(ProposingModel("grid_sort", new JsonObject { ["column"] = "eventid", ["descending"] = true }), grid: grid);
        await Send(vm, "sort by event id, largest first");
        var card = vm.Messages.Single(m => m.IsProposal).Card!;
        // what SpectreTranscript posts to the page, and what the page's button posts back: {type, id} with the same id
        var json = System.Text.Json.JsonDocument.Parse(ChatHtml.CardJson("add", card.Key, card.Title, card.Detail, card.Reason, "pending", card.Note, card.CanUndo));
        var id = json.RootElement.GetProperty("id").GetString()!;
        var action = ChatHtml.ParseAction(new JsonObject { ["type"] = "apply", ["id"] = id }.ToJsonString());
        Assert.NotNull(action);
        vm.HandleCardAction(action.Value.Type, action.Value.Id);
        Assert.Equal(ProposalState.Applied, card.State);
        Assert.Equal(("EventId", true), grid.Sort);
    }

    [Fact]
    public async Task The_view_lists_the_flags_by_id_and_label()
    {
        var model = new FakeModel();
        await Send(Chat(model, grid: new FakeGrid()), "x");
        Assert.Contains("Flags (id = label): red = RED, blue = BLUE", model.Sent[0]);
    }

    [Fact]
    public async Task AskAbout_opens_with_the_draft_and_attaches_the_notes_to_the_next_request_only()
    {
        var model = new FakeModel();
        var settings = new AppSettings { AssistantExpanded = false };
        IReadOnlyList<long>? requested = null;
        var vm = new AssistantChatViewModel(settings, new ApiKeyStore(Path.Combine(_dir, "keys")), () => new HttpClient(), () => null, () => null,
            () => model, (_, _) => false, a => a(), (_, _) => Task.FromResult<AssistantExtraTools?>(null),
            pinnedKnowledge: ids => { requested = ids; return "<knowledge>\n[Prefetch notes]\nPrefetch shows execution.\n</knowledge>\n"; });

        vm.AskAbout("Tell me about Prefetch notes", new long[] { 7, 9 });

        Assert.True(vm.IsExpanded);
        Assert.Equal("Tell me about Prefetch notes", vm.InputText);
        Assert.Equal(0, model.Calls); // the examiner sends it

        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 7, 9 }, requested);
        Assert.Contains("[Prefetch notes]", model.Sent[0]);
        Assert.EndsWith("Tell me about Prefetch notes", model.Sent[0]);

        requested = null;
        await Send(vm, "and what else?");
        Assert.Null(requested);
        Assert.DoesNotContain("[Prefetch notes]", model.Sent[1]);
    }
}
