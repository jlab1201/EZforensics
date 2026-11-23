using EzForensics.Core.Assistant;
using EzForensics.Core.Results;
using Xunit.Abstractions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Opt-in diagnostics on a real case (<c>EZFORENSICS_OLLAMA_MODEL</c>, <c>EZFORENSICS_SPECTRE_CASE</c> = case folder,
/// <c>EZFORENSICS_SPECTRE_CSV</c> = the open CSV): how many prompt tokens the first Spectre message takes with the app's real
/// context, and what the model answers through Ollama's OpenAI endpoint (server-default context) versus the native API with
/// <see cref="AssistantModelFactory.OllamaContextTokens"/>.
/// </summary>
public sealed class SpectreContextLiveTests
{
    private readonly ITestOutputHelper _out;
    public SpectreContextLiveTests(ITestOutputHelper output) => _out = output;

    private sealed class Host : IAssistantHost
    {
        public List<AssistantProposal> Proposals { get; } = new();
        public void ShowProposal(AssistantProposal p) => Proposals.Add(p);
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }

    [Fact(Timeout = 1800000)]
    public async Task Measure_the_first_message_on_a_real_case()
    {
        if (Environment.GetEnvironmentVariable("EZFORENSICS_OLLAMA_MODEL") is not { Length: > 0 } modelId
            || Environment.GetEnvironmentVariable("EZFORENSICS_SPECTRE_CASE") is not { Length: > 0 } caseDir
            || Environment.GetEnvironmentVariable("EZFORENSICS_SPECTRE_CSV") is not { Length: > 0 } csv) return;

        var lines = File.ReadLines(csv).Take(21).ToList();
        var headers = lines[0].Split(',');
        var rows = lines.Skip(1).Select((l, i) => new GridSampleRow(i, l.Split(',').Select(c => c.Length > 200 ? c[..200] : c).ToList())).ToList();
        var total = File.ReadLines(csv).LongCount() - 1;
        var ctx = new GridContext(csv, headers, total, total, "", rows);
        var flags = FlagCatalog.Defaults().Select(f => (f.Id, f.Label)).ToList();
        var state = "Page: Analyze\nCase: " + Path.GetFileName(caseDir) + " (" + caseDir + ")\nOpen CSV: " + AssistantCaseTools.RelativeTo(caseDir, csv) + "\n" + CaseInventory.AppStateText(caseDir);
        const string ask = "are you seeing what I'm seeing now?";
        var message = new AssistantContext().Build(SpectrePage.Analyze, state, ctx.Describe(flags: flags), ask);
        _out.WriteLine($"context chars: {message.Length}, system prompt chars: {AssistantPrompt.System.Length}");

        foreach (var native in new[] { false, true })
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var host = new Host();
            using var caseTools = new AssistantCaseTools(() => caseDir, host, canOpenFile: true);
            var tools = AssistantGridTools.Create(() => ctx, () => flags.Select(f => f.Id).ToList(), host, () => flags, caseTools).ToList();
            tools.AddRange(caseTools.Tools);
            tools.Add(SpectreSkills.CreateHelpTool());
            var offered = SpectreSkills.ForPage(SpectrePage.Analyze, tools);
            var model = new OpenAiCompatibleModelClient(http, new Uri(OllamaDefaults.BaseUrl), modelId, null, AssistantPrompt.System,
                AssistantModelFactory.ChatMaxTokens, native ? AssistantModelFactory.OllamaContextTokens : null);
            var log = new List<string>();
            var said = await new AssistantSession(model, offered, host, maxToolRounds: 8, log: log.Add).AskAsync(message, default, question: ask);
            _out.WriteLine($"--- {(native ? "native num_ctx " + AssistantModelFactory.OllamaContextTokens : "/v1 server default")}: {offered.Count} tools, prompt tokens {model.LastPromptTokens}");
            foreach (var l in log) _out.WriteLine("  " + l);
            _out.WriteLine("  reply: " + string.Join(" | ", said).ReplaceLineEndings(" / "));
        }
    }
}
