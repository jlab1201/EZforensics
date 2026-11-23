using System.Text.Json.Nodes;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class StoryHostsTests
{
    private static SpectreTimelineFinding F(string id, string? host = null, string? casePath = null)
    {
        var o = new JsonObject { ["id"] = id, ["title"] = "t " + id, ["severity"] = "High", ["tactic"] = "Execution" };
        if (host != null) o["host"] = host;
        if (casePath != null) o["case_path"] = casePath;
        return SpectreTimelineFinding.From(o);
    }

    [Fact]
    public void One_system_with_two_record_names_and_hostless_findings_is_one_computer_with_aliases()
    {
        var fl = new[] { F("a", "DESKTOP-DEMO01", "PC1/EventLogs/a.csv"), F("b", "WIN-DEMO02", "PC1/EventLogs/b.csv"), F("c"), F("d", null, "PC1/MFT/m.csv") };
        var r = StoryHosts.Resolve(fl, p => p.StartsWith("PC1/") ? "PC1" : null, singleSystem: "PC1");
        var hosts = StoryGraphics.Hosts(fl, Array.Empty<SpectreMoment>(), r);
        hosts.Should().ContainSingle();
        hosts[0].Host.Should().Be("PC1");
        hosts[0].Findings.Should().Be(4);
        hosts[0].Aliases.Should().Equal("DESKTOP-DEMO01", "WIN-DEMO02");
        hosts[0].AliasLine.Should().Be("Names seen in the records: DESKTOP-DEMO01, WIN-DEMO02");
        StoryGraphics.Matrix(fl, Array.Empty<SpectreMoment>(), r).Hosts.Should().ContainSingle();
    }

    [Fact]
    public void Single_system_applies_to_findings_without_a_case_path()
    {
        var fl = new[] { F("a", "OTHER"), F("b") };
        var r = StoryHosts.Resolve(fl, _ => null, "PC1");
        fl.Select(r.ComputerOf).Should().OnlyContain(c => c == "PC1");
        r.AliasesOf("PC1").Should().Equal("OTHER");
    }

    [Fact]
    public void Two_systems_stay_two()
    {
        var fl = new[] { F("a", "x", "A/EventLogs/a.csv"), F("b", null, "B/MFT/b.csv"), F("c", "B-record", "B/EventLogs/c.csv") };
        var r = StoryHosts.Resolve(fl, p => p[0] == 'A' ? "A" : "B", null);
        StoryGraphics.Hosts(fl, Array.Empty<SpectreMoment>(), r).Select(h => h.Host).Should().BeEquivalentTo("A", "B");
    }

    [Fact]
    public void Without_an_index_record_hosts_are_kept_and_the_rest_is_unknown()
    {
        var fl = new[] { F("a", "DESKTOP-DEMO01"), F("b"), F("c", "WIN-DEMO02") };
        var r = StoryHosts.Resolve(fl, null, null);
        StoryGraphics.Hosts(fl, Array.Empty<SpectreMoment>(), r).Select(h => h.Host)
            .Should().BeEquivalentTo("DESKTOP-DEMO01", "WIN-DEMO02", StoryGraphics.NoHost);
        StoryGraphics.Hosts(fl, Array.Empty<SpectreMoment>()).Should().HaveCount(3);
    }

    [Fact]
    public void Evidence_case_path_is_used_when_the_finding_has_none_and_case_wide_is_skipped()
    {
        var o = new JsonObject
        {
            ["id"] = "e", ["title"] = "t", ["severity"] = "Low",
            ["evidence"] = new JsonArray(new JsonObject { ["case_path"] = "_Case/Network/x.csv", ["row"] = 1L }, new JsonObject { ["case_path"] = "PC1/Logins/y.csv", ["row"] = 2L }),
        };
        var f = SpectreTimelineFinding.From(o);
        StoryHosts.Resolve(new[] { f }, p => p.StartsWith("PC1") ? "PC1" : null, null).ComputerOf(f).Should().Be("PC1");
    }
}
