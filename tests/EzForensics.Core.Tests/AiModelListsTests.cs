using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class AiModelListsTests
{
    [Fact]
    public void Parses_ids_from_openai_and_openrouter_bodies()
    {
        var ids = OpenAiCompatibleModelClient.ParseModelIds("{\"data\":[{\"id\":\"gpt-4o\"},{\"id\":\"anthropic/claude-3.5-sonnet\",\"name\":\"x\"},{\"name\":\"no id\"},{\"id\":\"\"}]}");
        ids.Should().Equal("gpt-4o", "anthropic/claude-3.5-sonnet");
        OpenAiCompatibleModelClient.ParseModelIds("{}").Should().BeEmpty();
        var act = () => OpenAiCompatibleModelClient.ParseModelIds("<html>");
        act.Should().Throw<AssistantModelException>();
    }

    [Fact]
    public void Keeps_only_sorted_chat_models_for_openai()
    {
        var ids = new[] { "text-embedding-3-small", "whisper-1", "o3-mini", "gpt-4o-mini", "gpt-4o", "gpt-4o-audio-preview", "dall-e-3", "chatgpt-4o-latest", "gpt-image-1", "tts-1", "gpt-4o" };
        AiModelLists.FilterOpenAiChat(ids).Should().Equal("chatgpt-4o-latest", "gpt-4o", "gpt-4o-mini", "o3-mini");
    }

    [Fact]
    public void Choose_prefers_saved_model_else_first_else_empty()
    {
        var list = new[] { "gpt-4o", "gpt-4o-mini" };
        AiModelLists.Choose("GPT-4O-mini", list).Should().Be("gpt-4o-mini");
        AiModelLists.Choose("deepseek:r1:8b", list).Should().Be("gpt-4o");
        AiModelLists.Choose("gpt-4o", Array.Empty<string>()).Should().Be("");
    }

    [Fact]
    public void Switching_provider_never_carries_the_old_model_and_restores_per_provider()
    {
        var mem = new Dictionary<string, string>();
        AiModelLists.Switch(mem, "ollama", "deepseek:r1:8b", "openai").Should().Be("");
        AiModelLists.Switch(mem, "openai", "gpt-4o", "openrouter").Should().Be("");
        AiModelLists.Switch(mem, "openrouter", "openai/gpt-4o", "ollama").Should().Be("deepseek:r1:8b");
        AiModelLists.Switch(mem, "ollama", "deepseek:r1:8b", "openai").Should().Be("gpt-4o");
    }

    [Fact]
    public void Factory_builds_openrouter_and_needs_a_key()
    {
        using var http = new HttpClient();
        AssistantModelFactory.Create("openrouter", "openai/gpt-4o", null, "k", http).Should().NotBeNull();
        var act = () => AssistantModelFactory.Create("openrouter", "m", null, null, http);
        act.Should().Throw<AssistantModelException>();
    }
}
