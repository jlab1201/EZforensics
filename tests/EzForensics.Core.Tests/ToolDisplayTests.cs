using EzForensics.Core.Tools;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ToolDisplayTests
{
    [Theory]
    [InlineData("JLECmd.zip", "JLECmd")]
    [InlineData("JLECmd.ZIP", "JLECmd")]
    [InlineData("JLECmd", "JLECmd")]
    [InlineData("", "")]
    public void StripZip_RemovesOnlyTheExtension(string input, string expected) =>
        ToolDisplay.StripZip(input).Should().Be(expected);

    [Theory]
    [InlineData(false, false, "Not installed")]
    [InlineData(false, true, "Not installed")]
    [InlineData(true, false, "Installed")]
    [InlineData(true, true, "Update available")]
    public void StatusOf_MapsStates(bool installed, bool update, string expected) =>
        ToolDisplay.StatusOf(installed, update).Should().Be(expected);

    [Theory]
    [InlineData("2026.5.0.0", "2026.5.0")]
    [InlineData("2026.5.0+bfc7f47c", "2026.5.0")]
    [InlineData("1.5.2.1", "1.5.2.1")]
    [InlineData("14.3", "14.3")]
    [InlineData(null, "")]
    public void FormatVersion_MatchesTheEzSite(string? input, string expected) =>
        ToolDisplay.FormatVersion(input).Should().Be(expected);
}
