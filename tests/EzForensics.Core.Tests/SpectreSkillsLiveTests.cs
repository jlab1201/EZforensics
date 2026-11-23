using EzForensics.Core.Assistant;
using FluentAssertions;
using Xunit.Abstractions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Opt-in: needs Ollama running and <c>EZFORENSICS_OLLAMA_MODEL</c> (e.g. qwen3:8b). Without it the test returns at once.
/// Real client + session with the page-scoped tool set and the feature index, as the app sends them. Checks that how-to questions are
/// answered from the skills and that grid actions are still called (regression guard). Each ask runs twice.
/// </summary>
public sealed class SpectreSkillsLiveTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezskillslive-" + Guid.NewGuid().ToString("N"));
    public SpectreSkillsLiveTests(ITestOutputHelper output) => _out = output;
    public void Dispose() { try { Directory.Delete(_case, true); } catch (IOException) { } }

    private sealed class Host : IAssistantHost
    {
        public List<AssistantProposal> Proposals { get; } = new();
        public void ShowProposal(AssistantProposal p) => Proposals.Add(p);
        public Task<string> HandleProposalAsync(AssistantProposal p) { Proposals.Add(p); return Task.FromResult(AssistantGridTools.AppliedText); }
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }

    private sealed class Recording(IAssistantTool inner, List<string> log) : IAssistantTool
    {
        public ToolSpec Spec => inner.Spec;
        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var o = await inner.RunAsync(call, ct);
            lock (log) log.Add($"{call.Name}({call.Input.ToJsonString()})");
            return o;
        }
    }

    private async Task<(Host Host, List<string> Calls, string Said, int ToolCount)> AskAsync(string modelId, SpectrePage page, string ask)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var model = new OpenAiCompatibleModelClient(http, new Uri(OllamaDefaults.BaseUrl), modelId, null, AssistantPrompt.System, AssistantModelFactory.ChatMaxTokens);
        var grid = new SpectreGridLiveTests.FakeGrid();
        var host = new Host();
        var calls = new List<string>();
        var all = new List<IAssistantTool>();
        if (page == SpectrePage.Analyze)
            all.AddRange(AssistantGridTools.Create(grid.Context, () => SpectreGridLiveTests.FakeGrid.FlagDefs.Select(f => f.Id).ToList(), host,
                () => SpectreGridLiveTests.FakeGrid.FlagDefs.Select(f => (f.Id, f.Label)).ToList()));
        using var caseTools = new AssistantCaseTools(() => _case, host, canOpenFile: page == SpectrePage.Analyze);
        all.AddRange(caseTools.Tools);
        all.Add(SpectreSkills.CreateHelpTool());
        var tools = SpectreSkills.ForPage(page, all).Select(t => (IAssistantTool)new Recording(t, calls)).ToList();
        var state = $"Page: {page}\nCase: live ({_case})\n" + CaseInventory.AppStateText(_case);
        var view = page == SpectrePage.Analyze ? $"<current_view>\n{grid.Context().Describe(flags: SpectreGridLiveTests.FakeGrid.FlagDefs.Select(f => (f.Id, f.Label)).ToList())}\n</current_view>\n" : "";
        var said = await new AssistantSession(model, tools, host, maxToolRounds: 6)
            .AskAsync($"<app_state>\n{state}\n</app_state>\n<app_features>\n{SpectreSkills.Index(page)}\n</app_features>\n{view}{SpectreSkills.HelpBlock(SpectreSkills.MatchHowTo(ask, page))}\n{ask}", default); // help attached as the app does
        return (host, calls, string.Join("\n", said), tools.Count);
    }

    private async Task<int> RunAsync(string modelId, SpectrePage page, string ask, Func<Host, List<string>, string, bool> ok)
    {
        int good = 0;
        for (int run = 1; run <= 2; run++)
        {
            var (host, calls, said, count) = await AskAsync(modelId, page, ask);
            var pass = ok(host, calls, said);
            if (pass) good++;
            _out.WriteLine($"[{page}: {ask}] run {run} ({count} tools): {(pass ? "OK" : "FAIL")}\n  tools: {(calls.Count == 0 ? "(none)" : string.Join("\n         ", calls))}\n  said: {said.ReplaceLineEndings(" ")}");
        }
        return good;
    }

    [Fact(Timeout = 1800000)]
    public async Task Local_model_answers_how_to_questions_from_the_skills()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_OLLAMA_MODEL") is not { Length: > 0 } modelId) return;
        CaseInventoryTests.BuildCase(_case);

        var save = await RunAsync(modelId, SpectrePage.Analyze, "how do I save this filter?", (h, c, s) =>
            c.Any(x => x.StartsWith("skill_help")) || s.Contains("Save filter", StringComparison.OrdinalIgnoreCase));
        var filter = await RunAsync(modelId, SpectrePage.Analyze, "filter to event 4688", (h, c, s) =>
            h.Proposals.OfType<FilterProposal>().Any(p => p.Display.Contains("4688")));
        var chef = await RunAsync(modelId, SpectrePage.Explore, "how do I decode this base64?", (h, c, s) =>
            s.Contains("CyberChef", StringComparison.OrdinalIgnoreCase));

        _out.WriteLine($"save-filter {save}/2, filter 4688 {filter}/2, cyberchef {chef}/2");
        save.Should().BeGreaterThanOrEqualTo(1);
        filter.Should().Be(2);
        chef.Should().BeGreaterThanOrEqualTo(1);
    }
}
