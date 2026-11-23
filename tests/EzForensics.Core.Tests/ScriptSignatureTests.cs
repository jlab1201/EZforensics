using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ScriptSignatureTests
{
    private sealed class FakeVerifier(ScriptSignature result) : IScriptSignatureVerifier
    {
        public string? Checked;
        public ScriptSignature Verify(string path) { Checked = path; return result; }
    }

    private const string Microsoft = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [Theory]
    [InlineData("NotSigned", null)]
    [InlineData("HashMismatch", Microsoft)]
    [InlineData("UnknownError", Microsoft)]
    [InlineData("Valid", "CN=Evil Corp, O=Evil Corp")]
    [InlineData("Valid", "CN=Not Microsoft Corporation Ltd")]
    [InlineData("Valid", null)]
    public void Dotnet_script_is_refused_and_deleted_unless_validly_signed_by_Microsoft(string status, string? subject)
    {
        using var t = new TempDir();
        var script = Path.Combine(t.Path, "dotnet-install.ps1");
        File.WriteAllText(script, "Write-Host hi");
        var v = new FakeVerifier(new ScriptSignature(status, subject));

        var r = DefaultSetupExecutor.VerifyDotnetScript(v, script, _ => { });

        v.Checked.Should().Be(script);
        r.Should().NotBeNull();
        r!.State.Should().Be(SetupStepState.Failed);
        r.Message.Should().Contain("not run");
        File.Exists(script).Should().BeFalse();
    }

    [Fact]
    public void Valid_Microsoft_signature_passes()
    {
        using var t = new TempDir();
        var script = Path.Combine(t.Path, "dotnet-install.ps1");
        File.WriteAllText(script, "Write-Host hi");
        DefaultSetupExecutor.VerifyDotnetScript(new FakeVerifier(new ScriptSignature("Valid", Microsoft)), script, _ => { }).Should().BeNull();
        File.Exists(script).Should().BeTrue();
    }

    [Fact]
    public void PowerShell_verifier_reports_an_unsigned_script_as_not_valid()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var t = new TempDir();
        var script = Path.Combine(t.Path, "unsigned it's.ps1");
        File.WriteAllText(script, "Write-Host hi");
        var sig = PowerShellSignatureVerifier.Instance.Verify(script);
        sig.Status.Should().Be("NotSigned");
        ScriptSignaturePolicy.MicrosoftSignedProblem(sig).Should().NotBeNull();
    }
}
