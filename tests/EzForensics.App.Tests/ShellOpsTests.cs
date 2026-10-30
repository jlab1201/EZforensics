using EzForensics.App.Services;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Opening a file from a case must never execute it: executable-like files are revealed in Explorer.</summary>
public sealed class ShellOpsTests
{
    [Theory]
    [InlineData(@"C:\case\evil.exe")]
    [InlineData(@"C:\case\a.LNK")]
    [InlineData(@"C:\case\a.bat")]
    [InlineData(@"C:\case\noext")]
    public void Executable_like_files_are_revealed_not_opened(string p) => ShellOps.IsExecutableLike(p).Should().BeTrue();

    [Theory]
    [InlineData(@"C:\case\a.csv")]
    [InlineData(@"C:\case\a.log")]
    public void Data_files_open_normally(string p) => ShellOps.IsExecutableLike(p).Should().BeFalse();
}
