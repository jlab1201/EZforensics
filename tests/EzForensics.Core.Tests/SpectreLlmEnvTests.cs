using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class SpectreLlmEnvTests
{
    [Fact]
    public void DeepSeek_uses_the_engines_deepseek_provider()
    {
        var env = SpectreLlmEnv.For("deepseek", "deepseek-chat", null, "sk-1");
        env["SPECTRE_LLM_PROVIDER"].Should().Be("deepseek");
        env["DEEPSEEK_API_KEY"].Should().Be("sk-1");
        env["SPECTRE_DEEPSEEK_URL"].Should().Be("https://api.deepseek.com/v1/chat/completions");
        env["SPECTRE_LLM_MODEL"].Should().Be("deepseek-chat");
    }

    [Fact]
    public void Anthropic_uses_its_own_provider()
    {
        var env = SpectreLlmEnv.For("anthropic", "", null, "k");
        env["SPECTRE_LLM_PROVIDER"].Should().Be("anthropic");
        env["ANTHROPIC_API_KEY"].Should().Be("k");
        env.Should().NotContainKey("SPECTRE_LLM_MODEL");
    }

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1/chat/completions")]
    [InlineData("gemini", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    [InlineData("openrouter", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("zai", "https://api.z.ai/api/paas/v4/chat/completions")]
    public void Openai_compatible_vendors_go_through_the_openai_provider(string provider, string url)
    {
        var env = SpectreLlmEnv.For(provider, "m", null, "k");
        env["SPECTRE_LLM_PROVIDER"].Should().Be("openai");
        env["OPENAI_API_KEY"].Should().Be("k");
        env["SPECTRE_OPENAI_URL"].Should().Be(url);
    }

    [Fact]
    public void Ollama_and_keyless_custom_use_local()
    {
        SpectreLlmEnv.For("ollama", "qwen3:8b", null, null)["SPECTRE_LLM_PROVIDER"].Should().Be("local");
        var c = SpectreLlmEnv.For(AiProviders.Custom, "m", "http://localhost:1234/v1", null);
        c["SPECTRE_LLM_PROVIDER"].Should().Be("local");
        c["SPECTRE_LOCAL_LLM_URL"].Should().Be("http://localhost:1234/v1/chat/completions");
    }

    [Fact]
    public void Nothing_without_a_needed_key_or_url()
    {
        SpectreLlmEnv.For("deepseek", "m", null, null).Should().BeEmpty();
        SpectreLlmEnv.For(AiProviders.Custom, "m", "", null).Should().BeEmpty();
        SpectreLlmEnv.For("nope", "m", null, "k").Should().BeEmpty();
    }

    [Fact]
    public void Wslenv_names_every_variable_and_keeps_the_existing_one()
    {
        var env = SpectreLlmEnv.For("deepseek", "m", null, "k");
        var all = SpectreLlmEnv.WithWslEnv(env, "FOO/p");
        all["WSLENV"].Should().StartWith("FOO/p:").And.Contain("DEEPSEEK_API_KEY").And.Contain("SPECTRE_LLM_PROVIDER");
        SpectreLlmEnv.WithWslEnv(new Dictionary<string, string>(), "X").Should().BeEmpty();
    }

    [Fact]
    public void Signature_changes_with_the_key_but_does_not_contain_it()
    {
        var a = SpectreLlmEnv.Signature(SpectreLlmEnv.For("deepseek", "m", null, "secret-1"));
        var b = SpectreLlmEnv.Signature(SpectreLlmEnv.For("deepseek", "m", null, "secret-2"));
        a.Should().NotBe(b).And.NotContain("secret");
        SpectreLlmEnv.Signature(new Dictionary<string, string>()).Should().BeEmpty();
    }
}
