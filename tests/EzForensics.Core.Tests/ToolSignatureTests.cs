using EzForensics.Core.Tools;
using Xunit;

namespace EzForensics.Core.Tests;

public class ToolSignatureTests
{
    private sealed class Fake(PeSignature sig) : IPeSignatureVerifier
    {
        public PeSignature Verify(string path) => sig;
    }

    [Fact]
    public void Elevation_allowed_for_trusted_signature_and_logs_signer()
    {
        var log = new List<string>();
        var p = ToolSignaturePolicy.ElevationProblem(new Fake(new(true, "CN=ZimTech LLC", null)), @"C:\t\RegistryExplorer.exe", "RegistryExplorer", log.Add);
        Assert.Null(p);
        Assert.Contains(log, l => l.Contains("ZimTech LLC"));
    }

    [Theory]
    [InlineData("it is not signed")]
    [InlineData("WinVerifyTrust returned 0x80096010")]
    public void Elevation_refused_for_untrusted_signature(string why)
    {
        var p = ToolSignaturePolicy.ElevationProblem(new Fake(new(false, null, why)), @"C:\t\X.exe", "X");
        Assert.Equal("X is not signed or its signature is invalid; refusing to run it as administrator.", p);
    }

    [Fact]
    public void GuiLauncher_does_not_start_untrusted_tool_as_admin()
    {
        var old = GuiLauncher.Verifier;
        GuiLauncher.Verifier = new Fake(new(false, null, "it is not signed"));
        try
        {
            Assert.Throws<GuiLauncher.UntrustedToolException>(() => GuiLauncher.Launch(@"C:\does\not\matter.exe", null, runAsAdmin: true));
        }
        finally { GuiLauncher.Verifier = old; }
    }

    [Fact]
    public void Real_verifier_rejects_missing_file()
    {
        Assert.False(WinTrustSignatureVerifier.Instance.Verify(@"C:\definitely\missing.exe").Trusted);
    }

    [Fact]
    public void Real_ez_exe_passes_when_installed_on_this_machine()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EzForensicsData", "Tools", "net9", "MFTECmd.exe");
        if (!File.Exists(exe)) return; // only meaningful where the tool is installed
        var sig = WinTrustSignatureVerifier.Instance.Verify(exe);
        Assert.True(sig.Trusted, sig.Problem);
        Assert.Null(ToolSignaturePolicy.EzSignerProblem(sig));
    }

    [Theory]
    [InlineData("CN=ZimTech LLC, O=ZimTech LLC, S=Indiana, C=US", true)]
    [InlineData("CN=Eric Zimmerman, O=Eric Zimmerman, C=US", true)]
    [InlineData("CN=Not ZimTech LLC Ltd, O=Evil", false)]
    [InlineData("CN=Microsoft Corporation", false)]
    public void Ez_signer_subject_check(string subject, bool ok)
    {
        Assert.Equal(ok, ToolSignaturePolicy.EzSignerProblem(new PeSignature(true, subject, null)) is null);
    }

    [Fact]
    public void Ez_signer_check_requires_valid_signature()
    {
        Assert.NotNull(ToolSignaturePolicy.EzSignerProblem(new PeSignature(false, "CN=ZimTech LLC", "bad")));
        Assert.NotNull(ToolSignaturePolicy.EzSignerProblem(new PeSignature(true, null, null)));
    }
}
