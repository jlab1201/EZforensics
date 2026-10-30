using EzForensics.Core.Case;
using EzForensics.Core.Results;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>The <c>_Case</c> folder holds results whose source host could not be identified (RITA, plaso): a case-level node, never a host.</summary>
public sealed class CaseWideFolderTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezcw_" + Guid.NewGuid().ToString("N"));

    public CaseWideFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_case, "_Case", "Network"));
        Directory.CreateDirectory(Path.Combine(_case, "HOST1", "Network"));
        File.WriteAllText(Path.Combine(_case, "_Case", "Network", "Rita_Beacons.csv"), "a,b\n1,2\n");
        File.WriteAllText(Path.Combine(_case, "HOST1", "Network", "Zeek_Conn.csv"), "a,b\n1,2\n");
    }

    public void Dispose() { try { Directory.Delete(_case, true); } catch (IOException) { } }

    [Fact]
    public void FromPath_CaseWide_Has_No_Host()
    {
        var (layout, host, category, _) = CaseIndex.FromPath(@"_Case\Network\Rita_Beacons.csv");
        layout.Should().Be(CaseLayoutVersion.HostArtifact);
        host.Should().BeNull();
        category.Should().Be("Network");
    }

    [Fact]
    public void Index_Does_Not_Count_CaseWide_As_Host()
    {
        var idx = CaseIndex.Load(_case);
        idx.Hosts.Should().Equal("HOST1");
        idx.LayoutVersion.Should().Be(CaseLayoutVersion.HostArtifact);
        var p = idx.Describe(Path.Combine(_case, "_Case", "Network", "Rita_Beacons.csv"));
        p.IsCaseWide.Should().BeTrue();
        p.Host.Should().BeNull();
        idx.Describe(Path.Combine(_case, "HOST1", "Network", "Zeek_Conn.csv")).IsCaseWide.Should().BeFalse();
    }

    [Fact]
    public void Tree_Order_Puts_CaseWide_After_Hosts()
    {
        var items = new[] { ("_Case", true), ("Zed", true), ("HOST1", true), ("a.csv", false) };
        CaseTreeOrder.Sort(items, i => i.Item1, i => i.Item2).Select(i => i.Item1)
            .Should().Equal("HOST1", "Zed", "_Case", "a.csv");
    }

    [Fact]
    public void CaseWide_Node_Is_Named_Unattributed()
    {
        CaseTreeOrder.CaseWideDisplayName.Should().Be("Case-wide (unattributed)");
        CaseTreeOrder.CaseWideTooltip.Should().Be("Results whose source host could not be identified");
    }

    [Fact]
    public void Legacy_Case_Level_Timeline_Goes_Under_The_Only_Host_Never_Into_Case()
    {
        CaseTreeOrder.LegacyTimelineHost(new[] { "WS01" }).Should().Be("WS01");
        CaseTreeOrder.LegacyTimelineHost(new[] { "WS01", "DC01" }).Should().BeNull("with several hosts it stays at the top, not under _Case");
        CaseTreeOrder.LegacyTimelineHost(Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void Per_Host_And_Old_Case_Timelines_Are_Described()
    {
        // a host's timeline folder and an old _Case timeline from an earlier run both stay where they are and keep their host (or none)
        Directory.CreateDirectory(Path.Combine(_case, "HOST1", "Timeline"));
        Directory.CreateDirectory(Path.Combine(_case, "_Case", "Timeline"));
        File.WriteAllText(Path.Combine(_case, "HOST1", "Timeline", "SuperTimeline_plaso_A.csv"), "a\n1\n");
        File.WriteAllText(Path.Combine(_case, "_Case", "Timeline", "SuperTimeline_plaso_B.csv"), "a\n1\n");
        var idx = CaseIndex.Load(_case);
        idx.Hosts.Should().Equal("HOST1");
        CaseTreeOrder.IsSuperTimeline("Timeline", true).Should().BeTrue();
        CaseTreeOrder.Sort(new[] { ("Network", true), ("Timeline", true) }, i => i.Item1, i => i.Item2).Select(i => i.Item1)
            .Should().Equal("Timeline", "Network");
    }

    [Theory]
    [InlineData("_Case")]
    [InlineData("_case")]
    public void Host_Named_CaseWide_Is_Uniquified(string name)
    {
        HostDiscovery.Sanitize(name).Should().NotBe("_Case").And.NotBeEquivalentTo("_Case");
        TriagePlanner.HostFolder(name).Should().NotBeEquivalentTo("_Case");
    }
}
