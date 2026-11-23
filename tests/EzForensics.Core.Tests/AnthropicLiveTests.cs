using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Opt-in: talks to the real Anthropic API. Set <c>EZFORENSICS_ANTHROPIC_KEY</c> (read from the environment only, never printed);
/// optional <c>EZFORENSICS_ANTHROPIC_MODEL</c>. Without the key the test returns at once.
/// </summary>
public class AnthropicLiveTests
{
    private static string? Key => Environment.GetEnvironmentVariable("EZFORENSICS_ANTHROPIC_KEY") is { Length: > 0 } k ? k : null;

    [Fact(Timeout = 180000)]
    public async Task Assistant_session_calls_a_trivial_tool_through_anthropic()
    {
        if (Key is not { } key) return;
        var model = Environment.GetEnvironmentVariable("EZFORENSICS_ANTHROPIC_MODEL");
        var tool = new SecretNumberTool();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new AnthropicModelClient(key, model, AssistantPrompt.System, http: http);
        var said = await new AssistantSession(client, new IAssistantTool[] { tool }, new NullHost(), maxToolRounds: 4)
            .AskAsync("Call the secret_number tool and tell me the number it returns.", default);
        tool.Runs.Should().BeGreaterThan(0);
        string.Join(" ", said).Should().Contain("4242");
    }

    private sealed class SecretNumberTool : IAssistantTool
    {
        public int Runs;
        public ToolSpec Spec { get; } = new("secret_number", "Returns the secret number.", JsonNode.Parse("""{"type":"object","properties":{}}""")!.AsObject(), ToolKind.Read);
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) { Runs++; return Task.FromResult(new ToolOutcome("4242", false)); }
    }

    private sealed class NullHost : IAssistantHost
    {
        public void ShowProposal(AssistantProposal proposal) { }
        public Task<bool> ConfirmWriteAsync(ToolCall call) => Task.FromResult(false);
        public void Status(string text) { }
    }
}
