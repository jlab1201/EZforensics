using System.Text.RegularExpressions;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Opt-in: needs Ollama running and <c>EZFORENSICS_OLLAMA_MODEL</c> (e.g. qwen3:8b). Without it every test returns at once.</summary>
public sealed class SpectreChatLiveTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezlive-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_case, true); } catch (IOException) { } }

    private sealed class NullHost : IAssistantHost
    {
        public void ShowProposal(AssistantProposal proposal) { }
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }

    private async Task<string> AskAsync(string modelId, string question, List<string> calls)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var model = new OpenAiCompatibleModelClient(http, new Uri(OllamaDefaults.BaseUrl), modelId, null, AssistantPrompt.System, AssistantModelFactory.ChatMaxTokens);
        using var caseTools = new AssistantCaseTools(() => _case, new NullHost(), canOpenFile: true);
        var tools = caseTools.Tools.Select(t => (IAssistantTool)new Recording(t, calls)).ToList();
        // the same shape the app sends: <app_state> first, then the examiner's text
        var state = "Page: Analyze\nCase: live (" + _case + ")\n" + CaseInventory.AppStateText(_case);
        var said = await new AssistantSession(model, tools, new NullHost(), maxToolRounds: 6)
            .AskAsync($"<app_state>\n{state}\n</app_state>\n{question}", default);
        return string.Join("\n", said);
    }

    [Fact(Timeout = 900000)]
    public async Task Local_model_answers_from_the_case_briefly()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_OLLAMA_MODEL") is not { Length: > 0 } modelId) return;
        CaseInventoryTests.BuildCase(_case);
        var calls = new List<string>();

        var executed = await AskAsync(modelId, "I want to know the executed files. which file should i check?", calls);
        Console.WriteLine("EXECUTED> " + executed + "\n  (tools: " + string.Join(",", calls) + ")");
        var general = await AskAsync(modelId, "what is prefetch?", new List<string>());
        Console.WriteLine("GENERIC> " + general);
        var vague = await AskAsync(modelId, "anything suspicious?", new List<string>());
        Console.WriteLine("AMBIGUOUS> " + vague);

        executed.Should().MatchRegex("(?i)prefetch|amcache|shimcache|EventLogs");
        Words(executed).Should().BeLessThanOrEqualTo(120);
        executed.Should().NotMatchRegex(@"(?m)^\s*#{1,6}\s");
        general.Should().NotBeNullOrWhiteSpace();
        vague.Should().NotBeNullOrWhiteSpace();
    }

    private static int Words(string s) => Regex.Matches(s, @"\S+").Count;

    private sealed class Recording(IAssistantTool inner, List<string> calls) : IAssistantTool
    {
        public ToolSpec Spec => inner.Spec;
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) { lock (calls) calls.Add(call.Name); return inner.RunAsync(call, ct); }
    }
}
