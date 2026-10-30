using System.Globalization;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class CaseTreeFilterTests
{
    private sealed record N(string Name, bool File, params N[] Kids);

    [Fact]
    public void FormatHitsBadge_is_compact()
    {
        CaseTreeFilter.FormatHitsBadge(413, false, CultureInfo.InvariantCulture).Should().Be("413");
        CaseTreeFilter.FormatHitsBadge(0, false, CultureInfo.InvariantCulture).Should().Be("");
        CaseTreeFilter.FormatHitsBadge(1, true, CultureInfo.InvariantCulture).Should().EndWith("+");
    }

    [Theory]
    [InlineData(0, false, "")]
    [InlineData(1, false, "1 hit")]
    [InlineData(1234, false, "1,234 hits")]
    [InlineData(10_000, true, "10,000+ hits")]
    [InlineData(10_000, false, "10,000+ hits")]
    public void FormatHits(int n, bool capped, string expected) =>
        CaseTreeFilter.FormatHits(n, capped, CultureInfo.InvariantCulture).Should().Be(expected);

    [Fact]
    public void Visible_MatchesPlusAncestors()
    {
        var hit = new N("EventLogs.csv", true);
        var miss = new N("MFT.csv", true);
        var events = new N("EventLogs", false, hit);
        var fs = new N("FileSystem", false, miss);
        var host = new N("HOST1", false, events, fs);
        var other = new N("HOST2", false, new N("Prefetch.csv", true));

        var vis = CaseTreeFilter.Visible(new[] { host, other }, n => n.Kids, n => n.File && n.Name.Contains("Event"));

        vis.Should().BeEquivalentTo(new[] { hit, events, host });
    }

    [Fact]
    public void NameMatches_CaseInsensitive_EmptyMatchesAll()
    {
        CaseTreeFilter.NameMatches("EventLogs.csv", "eventlog").Should().BeTrue();
        CaseTreeFilter.NameMatches("EventLogs.csv", "  ").Should().BeTrue();
        CaseTreeFilter.NameMatches("MFT.csv", "event").Should().BeFalse();
    }

    [Fact]
    public void AggregateTool()
    {
        CaseTreeFilter.AggregateTool(new string?[] { null, "" }).Should().Be("");
        CaseTreeFilter.AggregateTool(new[] { "PECmd", "pecmd" }).Should().Be("PECmd");
        CaseTreeFilter.AggregateTool(new[] { "PECmd", "MFTECmd", null }).Should().Be("Mixed (2)");
    }

    [Fact]
    public void Range_InVisibleOrder_EitherDirection()
    {
        var order = new[] { "a", "b", "c", "d", "e" };
        CaseTreeFilter.Range(order, "b", "d").Should().Equal("b", "c", "d");
        CaseTreeFilter.Range(order, "d", "b").Should().Equal("b", "c", "d");
        CaseTreeFilter.Range(order, null, "c").Should().Equal("c");
        CaseTreeFilter.Range(order, "zz", "c").Should().Equal("c");
        CaseTreeFilter.Range(order, "a", "zz").Should().BeEmpty();
    }

    [Theory]
    [InlineData("logs", true)]
    [InlineData("SystemInfo", true)]
    [InlineData(".ezstaging", true)]
    [InlineData(".ezcache", true)]
    [InlineData("HOST1", false)]
    [InlineData("Timeline", false)]
    public void HiddenFolders(string name, bool hidden) => CaseTreeOrder.IsHiddenFolder(name).Should().Be(hidden);
}
