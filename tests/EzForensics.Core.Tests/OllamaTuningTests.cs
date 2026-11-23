using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class OllamaTuningTests
{
    [Fact]
    public void Apply_sets_missing_variables_and_keeps_the_users_own()
    {
        var env = new Dictionary<string, string> { ["OLLAMA_KV_CACHE_TYPE"] = "f16" };
        string? Get(string n) => env.TryGetValue(n, out var v) ? v : null;
        OllamaTuning.IsApplied(Get).Should().BeFalse();

        var set = OllamaTuning.Apply(Get, (n, v) => env[n] = v);

        set.Should().Equal("OLLAMA_FLASH_ATTENTION");
        env["OLLAMA_FLASH_ATTENTION"].Should().Be("1");
        env["OLLAMA_KV_CACHE_TYPE"].Should().Be("f16");
        OllamaTuning.IsApplied(Get).Should().BeTrue();
        OllamaTuning.Apply(Get, (_, _) => throw new InvalidOperationException("nothing to set")).Should().BeEmpty();
    }
}
