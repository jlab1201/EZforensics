using EzForensics.Core.Assistant;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class OllamaEndpointTests
{
    [Theory]
    [InlineData("ollama", "http://gpu-box:11434/v1/", null, "http://gpu-box:11434")]
    [InlineData("ollama", "", "10.0.0.5:9000", "http://10.0.0.5:9000")]
    [InlineData("ollama", null, null, "http://localhost:11434")]
    [InlineData("anthropic", "https://api.anthropic.com/v1", null, "http://localhost:11434")]
    [InlineData("anthropic", "https://x/v1", "box", "http://box:11434")]
    [InlineData("ollama", "not a url", "http://h:1/", "http://h:1")]
    public void Resolve_picks_settings_then_env_then_default(string provider, string? baseUrl, string? env, string expected)
        => OllamaEndpoint.Resolve(provider, baseUrl, env).Should().Be(expected);

    [Theory]
    [InlineData("0.0.0.0", "http://localhost:11434")]
    [InlineData("0.0.0.0:8080", "http://localhost:8080")]
    [InlineData(":11500", "http://localhost:11500")]
    [InlineData("https://ollama.lan", "https://ollama.lan:11434")]
    [InlineData("  ", null)]
    public void OllamaHost_is_normalised(string v, string? expected) => OllamaEndpoint.FromHostVariable(v).Should().Be(expected);

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://192.168.1.4:11434", false)]
    [InlineData("http://gpu-box:11434", false)]
    public void Loopback_only_for_this_pc(string root, bool expected) => OllamaEndpoint.IsLoopback(root).Should().Be(expected);

    [Fact]
    public async Task Ops_do_not_start_a_remote_server()
    {
        using var ops = new OllamaOps(() => new HttpClient(), new AppSettings(), () => { }, "http://gpu-box:11434");
        ops.IsLocalServer.Should().BeFalse();
        var act = () => ops.StartServerAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void FindOllamaExe_also_checks_program_files()
        => AiHardware.FindOllamaExe(p => p.StartsWith(@"C:\PF"), @"C:\U", "", @"C:\PF").Should().Be(@"C:\PF\Ollama\ollama.exe");

    [Theory]
    [InlineData("nomic-embed-text:latest", false)]
    [InlineData("all-minilm:latest", false)]
    [InlineData("bge-m3", false)]
    [InlineData("bge:large", false)]
    [InlineData("mxbai-embed-large", false)]
    [InlineData("snowflake-arctic-embed2", false)]
    [InlineData("granite-embedding:278m", false)]
    [InlineData("hf.co/x/bge-small", false)]
    [InlineData("qwen3:8b", true)]
    [InlineData("llama3.1:8b", true)]
    [InlineData("", false)]
    public void IsChatModel_excludes_embedding_models(string name, bool expected) => LocalAiAdvisor.IsChatModel(name).Should().Be(expected);

    [Fact]
    public void PickExistingChatModel_prefers_the_biggest_chat_model()
    {
        LocalAiAdvisor.PickExistingChatModel(new[] { "nomic-embed-text", "llama3.2:3b", "gemma3:12b", "qwen3:4b" }).Should().Be("gemma3:12b");
        LocalAiAdvisor.PickExistingChatModel(new[] { "nomic-embed-text", "mistral:latest" }).Should().Be("mistral:latest");
        LocalAiAdvisor.PickExistingChatModel(new[] { "nomic-embed-text" }).Should().BeNull();
    }

    [Fact]
    public void Chat_card_text_says_so_when_the_pc_suits_a_cloud_ai()
    {
        LocalAiAdvisor.ChatCardText(LocalAiChoice.Local, "base").Should().Be("base");
        LocalAiAdvisor.ChatCardText(LocalAiChoice.Cloud, "base").Should().Contain("cloud AI");
        LocalAiAdvisor.ChatCardText(LocalAiChoice.LocalSlow, "base").Should().Contain("cloud AI");
    }
}
