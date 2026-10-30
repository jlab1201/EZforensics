using EzForensics.Core.Network;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class PcapHostMatcherTests
{
    private static readonly PcapHostCandidate Ws01 = new("WS01", @"X:\src\WS01\C", new[] { "10.0.0.5" });
    private static readonly PcapHostCandidate Ws02 = new("WS02", @"X:\src\WS02\C", new[] { "10.0.0.6" });
    private static readonly PcapHostCandidate Old = new("FINANCE-LAPTOP-0042", null, new[] { "10.0.0.7" });

    private static PcapEvidence Ev(string? dom = null, double share = 0, double second = 0)
    {
        var e = new PcapEvidence { DominantIp = dom, DominantShare = share, SecondShare = second, IpPacketCount = dom == null ? 0 : 100 };
        return e;
    }

    private static PcapHostMatch M(PcapEvidence? e, string path = @"X:\src\captures\a.pcap", params PcapHostCandidate[] hosts) =>
        PcapHostMatcher.Match(path, e, hosts.Length == 0 ? new[] { Ws01, Ws02, Old } : hosts);

    [Fact]
    public void Location_wins_and_no_hosts_or_no_evidence_is_case_wide()
    {
        var r = M(Ev("10.0.0.6", 1.0), @"X:\src\WS01\C\Users\a\cap.pcap");
        r.Host.Should().Be(Ws01);
        r.Method.Should().Be("matched by capture location");
        PcapHostMatcher.Match("a.pcap", Ev("10.0.0.5", 1), Array.Empty<PcapHostCandidate>()).Host.Should().BeNull();
        M(null).Host.Should().BeNull();
        M(new PcapEvidence()).Method.Should().Be(PcapHostMatch.NotIdentified);
    }

    [Fact]
    public void Single_host_is_not_assumed_without_evidence()
    {
        M(new PcapEvidence(), hosts: Ws01).Host.Should().BeNull();
    }

    [Fact]
    public void Dominant_ip_needs_a_clear_share_and_one_host()
    {
        var r = M(Ev("10.0.0.5", 0.9, 0.3));
        r.Host.Should().Be(Ws01);
        r.Method.Should().Be("matched by IP 10.0.0.5");
        M(Ev("10.0.0.7", 0.6, 0.1)).Host.Should().Be(Old);            // host of an earlier ingest (no root)
        M(Ev("10.0.0.5", 0.4, 0.1)).Host.Should().BeNull();           // share too low
        M(Ev("10.0.0.5", 1.0, 0.9)).Host.Should().BeNull();           // not clearly ahead (a two-host conversation)
        M(Ev("10.0.0.5", 1.0, 0.8)).Host.Should().Be(Ws01);           // in every packet, one busy peer
        M(Ev("10.0.0.5", 0.8, 0.7)).Host.Should().BeNull();
        M(Ev("10.9.9.9", 1.0, 0.1)).Host.Should().BeNull();           // nobody's IP
        var twin = new PcapHostCandidate("CLONE", @"X:\src\CLONE\C", new[] { "10.0.0.5" });
        M(Ev("10.0.0.5", 1.0, 0.1), hosts: new[] { Ws01, twin }).Host.Should().BeNull();   // ambiguous, nothing else
    }

    [Fact]
    public void Ambiguous_ip_falls_through_to_names()
    {
        var twin = new PcapHostCandidate("CLONE", @"X:\src\CLONE\C", new[] { "10.0.0.5" });
        var e = Ev("10.0.0.5", 1.0, 0.1);
        e.Names.Add(new PcapNameEvidence("clone", "NBNS registration", "10.0.0.5", true));
        var r = M(e, hosts: new[] { Ws01, twin });
        r.Host.Should().Be(twin);
        r.Method.Should().Be("matched by NBNS registration clone");
    }

    [Fact]
    public void Names_use_owned_evidence_netbios_truncation_and_fqdn()
    {
        var e = new PcapEvidence();
        e.Names.Add(new PcapNameEvidence("WS02", "NBNS query", "10.0.0.9", false));   // a query is not the host's own name
        e.Names.Add(new PcapNameEvidence("FINANCE-LAPTOP-", "NBNS registration", "10.0.0.7", true));
        var r = M(e);
        r.Host.Should().Be(Old);
        r.Method.Should().Contain("FINANCE-LAPTOP-");

        var fq = new PcapEvidence();
        fq.Names.Add(new PcapNameEvidence("ws02.corp.example", "DHCP hostname", null, true));
        M(fq).Host.Should().Be(Ws02);

        var other = new PcapEvidence();
        other.Names.Add(new PcapNameEvidence("WS0", "NBNS registration", null, true));
        M(other).Host.Should().BeNull();

        var alias = new PcapHostCandidate("HOST", @"X:\src\HOST", Array.Empty<string>()) { Names = new[] { "REAL-NAME" } };
        var a = new PcapEvidence();
        a.Ntlm.Add(new PcapNtlmEvidence("REAL-NAME", "CORP", "bob", "10.0.0.50"));
        a.Names.Add(new PcapNameEvidence("REAL-NAME", "NTLM workstation", "10.0.0.50", true));
        M(a, hosts: new[] { Ws01, alias }).Method.Should().Be("matched by NTLM workstation REAL-NAME");

        PcapHostMatcher.SameHostName("ABCDEFGHIJKLMNO", "abcdefghijklmnopq").Should().BeTrue();
        PcapHostMatcher.SameHostName("ABCDEFGHIJKLMN", "abcdefghijklmnopq").Should().BeFalse();
        PcapHostMatcher.SameHostName("", "x").Should().BeFalse();
    }

    [Fact]
    public void Several_named_hosts_fall_through_to_arp_and_dhcp()
    {
        var e = new PcapEvidence();
        e.Names.Add(new PcapNameEvidence("WS01", "NTLM workstation", "10.0.0.5", true));
        e.Names.Add(new PcapNameEvidence("WS02", "NTLM workstation", "10.0.0.6", true));   // e.g. a file server's capture
        M(e).Host.Should().BeNull();
        e.ArpSenderIps.Add("10.0.0.6");
        var r = M(e);
        r.Host.Should().Be(Ws02);
        r.Method.Should().Be("matched by ARP IP 10.0.0.6");

        var d = new PcapEvidence();
        d.DhcpIps.Add("10.0.0.7");
        M(d).Method.Should().Be("matched by DHCP IP 10.0.0.7");
        d.ArpSenderIps.Add("10.0.0.5");
        M(d).Host.Should().BeNull();                                     // two hosts: ambiguous, case-wide
    }

    [Fact]
    public void Synthetic_capture_end_to_end()
    {
        var e = PcapHostEvidence.Read(new MemoryStream(PcapBuild.Pcap(PcapBuild.HostMix())));
        M(e).Should().Be(new PcapHostMatch(Ws01, "matched by IP 10.0.0.5"));
        var noIps = new PcapHostCandidate("WS01", null, Array.Empty<string>());
        M(e, hosts: new[] { noIps, Ws02 }).Method.Should().Be("matched by NTLM workstation WS01");
    }
}
