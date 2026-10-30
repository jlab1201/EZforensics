using EzForensics.Core.SystemInfo;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ComputerDetailsPresentationTests
{
    private static DetailField F(string c, string f, string v) => new(c, f, v, "src");

    private static SystemDetails Sample() => new()
    {
        Fields =
        {
            F("System", "Computer name", "WS01"),
            F("System", "Host name (TCP/IP)", "ws01"),
            F("System", "Current control set", "ControlSet001"),
            F("System", "Last logged-on user", "CORP\\alice"),
            F("System", "Domain / workgroup (Winlogon)", "CORP"),
            F("Operating system", "Product name", "Windows 10 Pro"),
            F("Operating system", "Operating system (derived)", "Windows 11 Pro"),
            F("Operating system", "Version", "22H2"),
            F("Operating system", "Build", "22621.1"),
            F("Operating system", "Product ID", "00330-xxxxx"),
            F("Operating system", "Registered organization", "-"),
            F("Operating system", "Install date (UTC)", "2024-01-01"),
            F("Time", "Time zone", "Pacific Standard Time"),
            F("Network: Intel NIC", "Interface GUID", "{abc}"),
            F("Network: Intel NIC", "IP address (DHCP)", "10.0.0.5"),
            F("Network: Intel NIC", "DNS servers", "10.0.0.1"),
            F("Network: Wi-Fi", "IP address (static)", "192.168.1.9, 10.0.0.5"),
        }
    };

    [Fact]
    public void KeyFacts_ordered_and_deduplicated()
    {
        var k = ComputerDetailsPresentation.KeyFacts(Sample());
        k.Select(x => x.Label).Should().Equal("Computer name", "OS", "IP addresses", "Time zone", "Last logged-on user", "Domain / workgroup");
        k[1].Value.Should().Be("Windows 11 Pro 22H2 (build 22621.1)");
        k[2].Value.Should().Be("10.0.0.5, 192.168.1.9");
    }

    [Fact]
    public void KeyFacts_omits_missing_values()
    {
        var d = new SystemDetails { Fields = { F("System", "Computer name", "A"), F("Time", "Time zone", "-") } };
        ComputerDetailsPresentation.KeyFacts(d).Should().ContainSingle().Which.Label.Should().Be("Computer name");
    }

    [Fact]
    public void Detailed_drops_trivia_unknowns_and_duplicates()
    {
        var names = ComputerDetailsPresentation.DetailedFields(Sample()).Select(f => f.Field).ToList();
        names.Should().NotContain(new[] { "Product ID", "Registered organization", "Current control set", "Interface GUID", "Product name", "Host name (TCP/IP)" });
        names.Should().Contain(new[] { "Install date (UTC)", "DNS servers", "Operating system (derived)" });
    }
}
