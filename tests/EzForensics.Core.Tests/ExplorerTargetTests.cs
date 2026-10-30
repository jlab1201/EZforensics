using EzForensics.Core.Results;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class ExplorerTargetTests
{
    [Fact]
    public void FileIsSelectedWithQuotedPath()
    {
        var p = @"C:\Case\Host 1\Persistence\run keys.csv";
        var t = ExplorerTarget.Resolve(p, f => f == p, _ => false);
        t.Should().NotBeNull();
        t!.Value.Exe.Should().Be("explorer.exe");
        t.Value.Arguments.Should().Be($"/select,\"{p}\"");
    }

    [Fact]
    public void FolderIsOpened()
    {
        var t = ExplorerTarget.Resolve(@"C:\Case\Host 1\", _ => false, d => true);
        t!.Value.Arguments.Should().Be("\"C:\\Case\\Host 1\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\nope\missing.csv")]
    public void MissingPathResolvesToNull(string? p) => ExplorerTarget.Resolve(p, _ => false, _ => false).Should().BeNull();
}
