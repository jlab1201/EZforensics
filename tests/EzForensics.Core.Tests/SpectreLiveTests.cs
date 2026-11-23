using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Opt-in: talks to a real <c>spectre-mcp</c>. Set <c>EZFORENSICS_SPECTRE_MCP</c> to its path (e.g. a dev venv's
/// <c>Scripts\spectre-mcp.exe</c>); without it every test returns at once.
/// </summary>
public class SpectreLiveTests
{
    private static readonly TimeSpan T = TimeSpan.FromSeconds(60);

    private static string? Exe => Environment.GetEnvironmentVariable("EZFORENSICS_SPECTRE_MCP") is { Length: > 0 } e ? e : null;

    private static async Task<McpClient> ConnectAsync(string exe, string casesDir)
    {
        var (file, args, cancel) = SpectreMcp.DevCommand(exe, new[] { "--cases-dir", casesDir, "--no-journal-anchor" });
        var host = StdioProcessHost.Start(file, args, null, cancel, null);
        return await McpClient.ConnectAsync(host, T, default);
    }

    [Fact(Timeout = 120000)]
    public async Task Lists_the_spectre_tools_with_no_approve_tool()
    {
        if (Exe is not { } exe) return;
        await using var c = await ConnectAsync(exe, Directory.CreateTempSubdirectory().FullName);
        var tools = await c.ListToolsAsync(default);
        tools.Select(t => t.Name).Should().Contain(new[] { "spectre_findings_list", "spectre_kb_search", "spectre_ioc_lookup", "spectre_open_ezf_case" });
        tools.Should().NotContain(t => t.Name.Contains("approve", StringComparison.OrdinalIgnoreCase));
        tools.Where(t => !t.ReadOnly).Select(t => t.Name).Should().BeEquivalentTo("spectre_open_ezf_case", "spectre_run", "spectre_submit_explanation");
    }

    [Fact(Timeout = 120000)]
    public async Task Kb_search_and_bad_case_id_answer()
    {
        if (Exe is not { } exe) return;
        await using var c = await ConnectAsync(exe, Directory.CreateTempSubdirectory().FullName);
        var kb = await c.CallToolAsync("spectre_kb_search", new JsonObject { ["query"] = "prefetch", ["limit"] = 5 }, T, default);
        kb.IsError.Should().BeFalse(kb.Text);
        kb.Text.Should().Contain("prefetch");
        var bad = await c.CallToolAsync("spectre_findings_list", new JsonObject { ["case_id"] = "../etc" }, T, default);
        bad.IsError.Should().BeTrue();
    }

    [Fact(Timeout = 120000)]
    public async Task Assistant_session_runs_a_real_spectre_tool()
    {
        if (Exe is not { } exe) return;
        await using var c = await ConnectAsync(exe, Directory.CreateTempSubdirectory().FullName);
        var tools = McpAssistantTools.Create(c, await c.ListToolsAsync(default));
        var model = new OneToolModel(new ToolCall("1", "spectre_kb_search", new JsonObject { ["query"] = "lnk" }));
        var said = await new AssistantSession(model, tools, new NullHost()).AskAsync("what proves lnk execution?", default);
        said.Should().Equal("done");
        model.Result!.IsError.Should().BeFalse(model.Result.Text);
        model.Result.Text.Should().Contain("lnk");
    }

    /// <summary>Also needs <c>EZFORENSICS_OLLAMA_MODEL</c> (e.g. qwen3:8b) and Ollama running on its default port.</summary>
    [Fact(Timeout = 600000)]
    public async Task Local_model_chats_through_real_spectre_tools()
    {
        if (Exe is not { } exe || Environment.GetEnvironmentVariable("EZFORENSICS_OLLAMA_MODEL") is not { Length: > 0 } modelId) return;
        await using var c = await ConnectAsync(exe, Directory.CreateTempSubdirectory().FullName);
        var calls = new List<string>();
        var tools = McpAssistantTools.Create(c, await c.ListToolsAsync(default)).Select(t => (IAssistantTool)new Recording(t, calls)).ToList();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var model = new OpenAiCompatibleModelClient(http, new Uri(OllamaDefaults.BaseUrl), modelId, null, AssistantPrompt.System);
        var said = await new AssistantSession(model, tools, new NullHost(), maxToolRounds: 5)
            .AskAsync("Use spectre_kb_search to look up Prefetch, then tell me in one sentence what Prefetch proves.", default);
        calls.Should().Contain("spectre_kb_search");
        string.Join(" ", said).Should().NotBeNullOrWhiteSpace();
        Console.WriteLine("MODEL SAID: " + string.Join(" | ", said));
    }

    private sealed class Recording(IAssistantTool inner, List<string> calls) : IAssistantTool
    {
        public ToolSpec Spec => inner.Spec;
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) { lock (calls) calls.Add(call.Name); return inner.RunAsync(call, ct); }
    }

    private sealed class OneToolModel : IModelClient
    {
        private readonly ToolCall _call;
        public ToolOutcome? Result { get; private set; }
        public OneToolModel(ToolCall call) => _call = call;
        public Task<ModelStep> SendUserAsync(string text, IReadOnlyList<ToolSpec> tools, CancellationToken ct) =>
            Task.FromResult(new ModelStep(Array.Empty<string>(), new[] { _call }, ModelStopReason.ToolUse));
        public Task<ModelStep> SendToolResultsAsync(IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> results, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
        {
            Result = results.Single().Outcome;
            return Task.FromResult(new ModelStep(new[] { "done" }, Array.Empty<ToolCall>(), ModelStopReason.EndTurn));
        }
    }

    private sealed class NullHost : IAssistantHost
    {
        public void ShowProposal(AssistantProposal proposal) { }
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }
}
