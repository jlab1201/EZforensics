using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class AssistantModelFactoryTests
{
    private static readonly HttpClient Http = new();

    [Theory]
    [InlineData("anthropic", null)]
    [InlineData("openai", "")]
    public void Cloud_provider_without_key_is_refused(string provider, string? key)
    {
        var act = () => AssistantModelFactory.Create(provider, "m", null, key, Http);
        act.Should().Throw<AssistantModelException>().WithMessage("Add an API key in Options → AI");
    }

    [Fact]
    public void Builds_the_matching_client()
    {
        AssistantModelFactory.Create("ollama", "llama3", null, null, Http).Should().BeOfType<OpenAiCompatibleModelClient>();
        AssistantModelFactory.Create("openai", "gpt", null, "sk-x1234", Http).Should().BeOfType<OpenAiCompatibleModelClient>();
        AssistantModelFactory.Create("anthropic", "", null, "sk-ant-x1234", Http).Should().BeOfType<AnthropicModelClient>();
        // Ollama gets a real context window (native API); OpenAI keeps its own
        ((IModelUsage)AssistantModelFactory.Create("ollama", "llama3", null, null, Http)).ContextTokens.Should().Be(AssistantModelFactory.OllamaContextTokens);
        ((IModelUsage)AssistantModelFactory.Create("openai", "gpt", null, "sk-x1234", Http)).ContextTokens.Should().BeNull();
    }

    [Fact]
    public void Ollama_needs_a_model_and_unknown_provider_is_refused()
    {
        var a = () => AssistantModelFactory.Create("ollama", " ", null, null, Http);
        a.Should().Throw<AssistantModelException>();
        var b = () => AssistantModelFactory.Create("bogus", "m", null, null, Http);
        b.Should().Throw<AssistantModelException>();
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":{\"role\":\"assistant\",\"content\":\"[]\"},\"done_reason\":\"stop\"}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task System_prompt_override_and_no_thinking_reach_the_ollama_request()
    {
        var h = new CaptureHandler();
        var c = AssistantModelFactory.Create("ollama", "qwen3", null, null, new HttpClient(h), "SHORT", disableThinking: true);
        await c.SendUserAsync("hi", Array.Empty<ToolSpec>(), default);
        var j = System.Text.Json.Nodes.JsonNode.Parse(h.Body)!;
        j["messages"]![0]!["content"]!.GetValue<string>().Should().Be("SHORT");
        j["think"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Default_create_keeps_the_spectre_prompt_and_default_thinking()
    {
        var h = new CaptureHandler();
        var c = AssistantModelFactory.Create("ollama", "qwen3", null, null, new HttpClient(h));
        await c.SendUserAsync("hi", Array.Empty<ToolSpec>(), default);
        var j = System.Text.Json.Nodes.JsonNode.Parse(h.Body)!;
        j["messages"]![0]!["content"]!.GetValue<string>().Should().Be(AssistantPrompt.System);
        j.AsObject().ContainsKey("think").Should().BeFalse();
    }

    [Fact]
    public void Knowledge_calls_wait_minutes_not_seconds()
    {
        EzForensics.Core.Knowledge.KnowledgeLabeler.DefaultTimeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5));
    }
}
