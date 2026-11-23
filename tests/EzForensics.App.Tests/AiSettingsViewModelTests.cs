using EzForensics.App.ViewModels;
using EzForensics.Core.Assistant;
using Xunit;

namespace EzForensics.App.Tests;

public class AiSettingsViewModelTests
{
    [Fact]
    public void OllamaDownIsFriendly()
    {
        var msg = AiSettingsViewModel.Friendly("ollama", "http://localhost:11434/v1/", new AssistantModelException("could not reach localhost:11434 (is Ollama running?)"), null);
        Assert.Equal("Ollama isn't running on localhost:11434", msg);
    }

    [Fact]
    public void KeyIsRemovedFromEchoedErrors()
    {
        var msg = AiSettingsViewModel.Friendly("openai", "", new AssistantModelException("api.openai.com: HTTP 401 Incorrect API key provided: sk-SECRET"), "sk-SECRET");
        Assert.DoesNotContain("SECRET", msg);
        Assert.Contains("401", msg);
    }

    [Fact]
    public void PicksAModelWhenNoneIsSet()
    {
        Assert.Equal("llama3:8b", AiSettingsViewModel.PickDefault("ollama", new[] { "qwen3:8b", "llama3:8b" }));
        Assert.Equal(AnthropicDefaults.Model, AiSettingsViewModel.PickDefault("anthropic", new[] { "claude-haiku-5-5", AnthropicDefaults.Model }));
        Assert.Null(AiSettingsViewModel.PickDefault("ollama", Array.Empty<string>()));
    }
}
