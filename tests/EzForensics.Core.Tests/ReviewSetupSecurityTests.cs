using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Fixes from the 1.0.0 setup / security review.</summary>
public sealed class ReviewSetupSecurityTests
{
    [Theory]
    [InlineData("https://dot.net/v1/dotnet-install.ps1", true)]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.ps1", true)]
    [InlineData("https://microsoft.com/x.ps1", true)]
    [InlineData("http://dot.net/v1/dotnet-install.ps1", false)]
    [InlineData("https://evildot.net/v1/dotnet-install.ps1", false)]
    [InlineData("https://notmicrosoft.com/dotnet-install.ps1", false)]
    [InlineData("https://dot.net.evil.example/dotnet-install.ps1", false)]
    public void Dotnet_install_script_only_from_https_dot_net_or_microsoft(string url, bool ok) =>
        DefaultSetupExecutor.IsTrustedDotnetScriptUri(new Uri(url)).Should().Be(ok);

    [Fact]
    public void Dotnet_install_script_without_a_final_uri_is_refused() =>
        DefaultSetupExecutor.IsTrustedDotnetScriptUri(null).Should().BeFalse();

    [Fact]
    public void Elevated_helper_never_truncates_or_overwrites_an_existing_log_or_result_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-elev-safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = Path.Combine(dir, "enable-wsl.log");
            var result = Path.Combine(dir, "enable-wsl.result.json");
            File.WriteAllText(log, "precious");
            File.WriteAllText(result, "precious");
            LineLogFile.TryOpen(log).Should().BeNull();
            int code = ElevatedSetupHelper.RunCommandLine(new[] { ElevatedSetupHelper.EnableWsl1Task, "--result", result, "--log", log }, null,
                (_, _, _) => 0);
            code.Should().Be(1, "the result could not be written");
            File.ReadAllText(log).Should().Be("precious");
            File.ReadAllText(result).Should().Be("precious");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public sealed class ReviewZipCentralDirectoryTests
{
    private static byte[] Eocd(uint cdSize, uint cdOffset, ushort count = 1)
    {
        var b = new byte[22];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, 0x06054b50);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), cdSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), cdOffset);
        return b;
    }

    [Theory]
    [InlineData(0x7FFFFFF0u, 0u)]
    [InlineData(100u, 0xFFFFFF00u)]
    public void A_central_directory_that_does_not_fit_in_the_file_is_not_allocated(uint size, uint offset)
    {
        using var ms = new MemoryStream(Eocd(size, offset));
        long before = GC.GetAllocatedBytesForCurrentThread();
        EzForensics.Core.Archives.ZipExtraFieldReader.Read(ms).Should().BeEmpty();
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(1 << 20);
    }

    [Fact]
    public void A_zip64_locator_pointing_outside_the_file_is_ignored()
    {
        var locator = new byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), 0xFFFFFFFFFFFFFFF0UL);
        using var ms = new MemoryStream(locator.Concat(Eocd(0, 0)).ToArray());
        EzForensics.Core.Archives.ZipExtraFieldReader.Read(ms).Should().BeEmpty();
    }
}

public sealed class ReviewDotnetProbeTests
{
    [Fact]
    public void Dotnet_is_found_only_in_fully_qualified_path_folders()
    {
        var dir = Path.Combine(Environment.CurrentDirectory, "ezf-dotnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "dotnet.exe"), "");
            var sep = Path.PathSeparator;
            EzForensics.Core.Tools.RuntimeProbe.FindDotnetOnPath($"relative{sep}.{sep}\"{dir}\"").Should().Be(Path.Combine(dir, "dotnet.exe"));
            var rel = Path.GetRelativePath(Environment.CurrentDirectory, dir);
            EzForensics.Core.Tools.RuntimeProbe.FindDotnetOnPath($"{rel}{sep}{sep}nothing-here").Should().BeNull("relative PATH entries are never searched");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public sealed class ReviewPowerShellQuoteTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("it’s; calc", "'it’’s; calc'")]
    [InlineData("‘a‚b‛", "'‘‘a‚‚b‛‛'")]
    [InlineData("$MFT", "'$MFT'")]
    public void Single_quotes_of_every_kind_are_doubled(string arg, string expected) =>
        EzForensics.Core.Commands.BuiltCommand.QuotePowerShell(arg).Should().Be(expected);
}
