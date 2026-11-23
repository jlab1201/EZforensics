using System.Net;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class AiProvidersTests
{
    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public string? Auth { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Auth = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public void Registry_ids_are_unique_lowercase_with_valid_urls()
    {
        AiProviders.All.Select(p => p.Id).Should().OnlyHaveUniqueItems();
        foreach (var p in AiProviders.All)
        {
            p.Id.Should().Be(p.Id.ToLowerInvariant());
            if (p.Id == AiProviders.Custom) continue;
            Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u).Should().BeTrue(p.Id);
            if (p.Kind != AiProviderKind.Ollama) u!.Scheme.Should().Be("https", p.Id);
        }
        AiProviders.All.Select(p => p.Name).Should().StartWith(new[] { "Local (Ollama)", "Anthropic", "OpenAI", "Google Gemini", "DeepSeek" });
    }

    [Fact]
    public void Parses_a_bare_array_and_keeps_chat_type()
    {
        var ids = OpenAiCompatibleModelClient.ParseModelIds("[{\"id\":\"a/chat\",\"type\":\"chat\"},{\"id\":\"a/embed\",\"type\":\"embedding\"},{\"id\":\"a/none\"}]");
        ids.Should().Equal("a/chat", "a/none");
    }

    [Fact]
    public void Vendor_filters()
    {
        AiProviders.Find("gemini")!.Filter!(new[] { "models/gemini-2.5-pro", "models/text-embedding-004", "models/gemini-embedding-001", "models/imagen-3", "gemini-2.5-flash" })
            .Should().Equal("gemini-2.5-flash", "gemini-2.5-pro");
        AiProviders.Find("mistral")!.Filter!(new[] { "mistral-large-latest", "mistral-embed", "mistral-moderation-latest", "mistral-ocr-latest" }).Should().Equal("mistral-large-latest");
        AiProviders.Find("groq")!.Filter!(new[] { "llama-3.3-70b", "whisper-large-v3", "distil-whisper-x", "playai-tts", "llama-guard-4" }).Should().Equal("llama-3.3-70b");
        AiProviders.Find("xai")!.Filter!(new[] { "grok-4", "grok-2-image" }).Should().Equal("grok-4");
    }

    [Fact]
    public async Task Fallback_is_used_on_404_but_not_on_401()
    {
        using var nf = new HttpClient(new StubHandler(HttpStatusCode.NotFound, "{}"));
        (await AiProviders.ListModelsAsync(nf, "deepseek", null, "k", default)).Should().Equal("deepseek-chat", "deepseek-reasoner");
        using var ok = new HttpClient(new StubHandler(HttpStatusCode.OK, "{\"data\":[]}"));
        (await AiProviders.ListModelsAsync(ok, "zai", null, "k", default)).Should().Equal("glm-4.6", "glm-4.5");
        using var denied = new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}"));
        var act = () => AiProviders.ListModelsAsync(denied, "deepseek", null, "k", default);
        (await act.Should().ThrowAsync<AssistantModelException>()).Which.StatusCode.Should().Be(401);
        // no fallback defined: a 404 stays an error
        var nofb = () => AiProviders.ListModelsAsync(nf, "groq", null, "k", default);
        await nofb.Should().ThrowAsync<AssistantModelException>();
    }

    [Fact]
    public async Task Custom_needs_a_url_and_sends_no_auth_without_key()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, "{\"data\":[{\"id\":\"m1\"}]}"));
        var act = () => AiProviders.ListModelsAsync(http, "custom", "", null, default);
        await act.Should().ThrowAsync<AssistantModelException>().WithMessage("*Base URL*");
        var h = new StubHandler(HttpStatusCode.OK, "{\"data\":[{\"id\":\"m1\"}]}");
        using var c = new HttpClient(h);
        (await AiProviders.ListModelsAsync(c, "custom", "http://localhost:1234/v1/", null, default)).Should().Equal("m1");
        h.Auth.Should().BeNull();
    }

    [Fact]
    public void Factory_builds_every_provider_and_refuses_missing_key_or_url()
    {
        using var http = new HttpClient();
        foreach (var p in AiProviders.All.Where(p => p.Id != AiProviders.Custom && p.Kind != AiProviderKind.Anthropic))
            AssistantModelFactory.Create(p.Id, "m", null, p.NeedsKey ? "sk-test1234" : null, http).Should().BeOfType<OpenAiCompatibleModelClient>(p.Id);
        AssistantModelFactory.Create("anthropic", "", null, "sk-ant-x", http).Should().BeOfType<AnthropicModelClient>();
        AssistantModelFactory.Create("custom", "m", "http://localhost:1234/v1/", null, http).Should().BeOfType<OpenAiCompatibleModelClient>();
        foreach (var p in AiProviders.All.Where(p => p.NeedsKey && p.Id != AiProviders.Custom))
        {
            var act = () => AssistantModelFactory.Create(p.Id, "m", null, null, http);
            act.Should().Throw<AssistantModelException>(p.Id).WithMessage("Add an API key*");
        }
        var noUrl = () => AssistantModelFactory.Create("custom", "m", "", null, http);
        noUrl.Should().Throw<AssistantModelException>().WithMessage("*Base URL*");
    }
}
