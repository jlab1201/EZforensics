using EzForensics.Core.Network;
using EzForensics.Core.Rita;
using EzForensics.Core.SystemInfo;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class NetworkTriageHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_nethost_" + Guid.NewGuid().ToString("N"));
    public NetworkTriageHostTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DiscoveredSystem Sys(string root, string name, params string[] ips) =>
        new() { RootPath = root, HostName = name, HostNameSource = HostDiscovery.FromSystemHive, IpAddresses = ips };

    private static PcapFile Pcap(string path) => new(path, 2048, PcapFormat.Pcap);

    [Fact]
    public void Captures_go_to_their_host_by_location_or_evidence_else_to_the_case_folder()
    {
        var caseDir = @"C:\case";
        var ws01 = Sys(@"X:\src\WS01\C", "WS01", "10.0.0.5");
        var under = Pcap(@"X:\src\WS01\C\Users\a\local.pcap");
        var loose = Pcap(@"X:\src\captures\ws01.pcap");
        var router = Pcap(@"X:\src\captures\router.pcap");
        var evidence = PcapHostEvidence.Read(new MemoryStream(PcapBuild.Pcap(PcapBuild.HostMix())));
        var asked = new List<string>();
        var plan = NetworkTriage.Plan(new[] { under, loose, router }, caseDir, null, @"X:\src", new[] { ws01 },
            p => { asked.Add(p); return p == loose.Path ? evidence : new PcapEvidence(); });

        asked.Should().Equal(loose.Path, router.Path);                       // no evidence read for a capture under a system root
        plan[0].Spec.OutputDir.Should().Be(@"C:\case\WS01\Network");
        plan[0].Spec.Label.Should().EndWith("· host WS01 (matched by capture location)");
        plan[0].Artifact.System.Should().Be(ws01);
        plan[1].Spec.OutputDir.Should().Be(@"C:\case\WS01\Network");
        plan[1].Spec.Label.Should().EndWith("· host WS01 (matched by IP 10.0.0.5)");
        plan[2].Spec.OutputDir.Should().Be(@"C:\case\_Case\Network");        // no "only system" shortcut
        plan[2].Spec.Provenance!.Host.Should().Be(RitaJobs.CaseWideHost);
        plan[2].Spec.Label.Should().EndWith("· case-wide: source host not identified");
        plan[2].Artifact.System.Should().BeNull();
    }

    [Fact]
    public void Hosts_of_an_earlier_ingest_match_by_their_computer_details_ips()
    {
        var known = new[] { new KnownHost("FILESRV", new[] { "FILESRV" }, new[] { "10.0.0.5" }), new KnownHost("_Case", Array.Empty<string>(), new[] { "10.0.0.9" }) };
        var evidence = PcapHostEvidence.Read(new MemoryStream(PcapBuild.Pcap(PcapBuild.HostMix())));
        var plan = NetworkTriage.Plan(new[] { Pcap(@"X:\new\c.pcap") }, @"C:\case", null, null, null, _ => evidence, known);
        plan[0].Spec.OutputDir.Should().Be(@"C:\case\FILESRV\Network");
        plan[0].Spec.Label.Should().Contain("matched by IP 10.0.0.5");

        var merged = NetworkTriage.Candidates(new[] { Sys(@"X:\a", "WS01") }, new[] { new KnownHost("WS01", new[] { "ws01" }, new[] { "10.1.1.1" }) });
        merged.Should().ContainSingle().Which.IpAddresses.Should().Equal("10.1.1.1");
        NetworkTriage.Candidates(null, known).Select(c => c.HostName).Should().Equal("FILESRV");   // "_Case" is never a host
    }

    [Fact]
    public void A_host_named_Case_cannot_collide_with_the_case_wide_folder()
    {
        var odd = Sys(@"X:\src\_Case\C", HostDiscovery.Sanitize("_Case"));
        odd.HostName.Should().NotBe(RitaJobs.CaseWideHost);
        TriagePlanner.HostFolder("_Case").Should().NotBe(RitaJobs.CaseWideHost);
        var plan = NetworkTriage.Plan(new[] { Pcap(@"X:\src\_Case\C\x.pcap") }, @"C:\case", null, null, new[] { odd with { HostName = "_Case" } });
        plan[0].Spec.OutputDir.Should().NotBe(@"C:\case\_Case\Network");
    }

    [Fact]
    public void Read_evidence_skips_captures_under_a_system_root()
    {
        var root = Path.Combine(_dir, "WS01");
        Directory.CreateDirectory(root);
        var inside = Path.Combine(root, "in.pcap");
        var outside = Path.Combine(_dir, "out.pcap");
        File.WriteAllBytes(inside, PcapBuild.Pcap(PcapBuild.HostMix()));
        File.WriteAllBytes(outside, PcapBuild.Pcap(PcapBuild.HostMix()));
        var ev = NetworkTriage.ReadEvidence(new[] { Pcap(inside), Pcap(outside) }, new[] { Sys(root, "WS01") });
        ev.Keys.Should().Equal(outside);
        ev[outside].DominantIp.Should().Be("10.0.0.5");
    }
}
