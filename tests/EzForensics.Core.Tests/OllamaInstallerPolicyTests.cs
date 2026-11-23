using EzForensics.Core.Assistant;
using EzForensics.Core.Setup;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class OllamaInstallerPolicyTests
{
    [Theory]
    [InlineData("https://ollama.com/download/OllamaSetup.exe", true)]
    [InlineData("https://github.com/ollama/ollama/releases/download/v1/OllamaSetup.exe", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://ollama.com/download/OllamaSetup.exe", false)]
    [InlineData("https://evil.example/ollama.com", false)]
    [InlineData("https://ollama.com.evil.example/x", false)]
    [InlineData("https://notollama.com/x", false)]
    public void Host_check(string url, bool ok) =>
        OllamaInstallerPolicy.IsAllowedDownloadHost(new Uri(url)).Should().Be(ok);

    [Fact] public void Null_uri_is_refused() => OllamaInstallerPolicy.IsAllowedDownloadHost(null).Should().BeFalse();

    [Theory]
    [InlineData("CN=Ollama Inc., O=Ollama Inc., C=US", true)]
    [InlineData("O=Ollama, L=Palo Alto", true)]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation", false)]
    [InlineData("CN=Evil, OU=Ollama", false)]
    public void Signer_check(string subject, bool ok) => OllamaInstallerPolicy.SignerIsOllama(subject).Should().Be(ok);

    [Fact]
    public void Signature_policy()
    {
        OllamaInstallerPolicy.SignatureProblem(new ScriptSignature("Valid", "CN=Ollama Inc.")).Should().BeNull();
        OllamaInstallerPolicy.SignatureProblem(new ScriptSignature("NotSigned", null)).Should().NotBeNull();
        OllamaInstallerPolicy.SignatureProblem(new ScriptSignature("HashMismatch", "CN=Ollama Inc.")).Should().NotBeNull();
        OllamaInstallerPolicy.SignatureProblem(new ScriptSignature("Valid", "CN=Other")).Should().NotBeNull();
    }
}
