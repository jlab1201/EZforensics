using EzForensics.Core.Platforms;
using EzForensics.Core.SystemInfo;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class TriageHostDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_hosts_" + Guid.NewGuid().ToString("N"));

    public TriageHostDiscoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Empty Windows system root (no hives).</summary>
    private string MakeRoot(params string[] parts)
    {
        var root = Path.Combine(new[] { _dir }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.Combine(root, "Windows", "System32", "config"));
        Directory.CreateDirectory(Path.Combine(root, "Users", "alice"));
        return root;
    }

    [Fact]
    public void Os_version_line_from_the_software_hive()
    {
        var soft = new InMemoryHive("SOFTWARE");
        soft.Key(@"Microsoft\Windows NT\CurrentVersion").With(RegValue.Sz("ProductName", "Windows 10 Pro"), RegValue.Sz("DisplayVersion", "22H2"),
            RegValue.Sz("CurrentBuild", "19045"), RegValue.Dword("UBR", 3803));
        HostDiscovery.OsVersionText(soft).Should().Be("Windows 10 Pro 22H2 (build 19045.3803)");

        var w11 = new InMemoryHive("SOFTWARE");
        w11.Key(@"Microsoft\Windows NT\CurrentVersion").With(RegValue.Sz("ProductName", "Windows 10 Enterprise"), RegValue.Sz("CurrentBuild", "22631"));
        HostDiscovery.OsVersionText(w11).Should().Be("Windows 11 Enterprise (build 22631)");

        HostDiscovery.OsVersionText(new InMemoryHive("SOFTWARE")).Should().BeNull();
        HostDiscovery.ReadOsVersion(MakeRoot("NOHIVE")).Should().BeNull();
    }

    [Fact]
    public void Single_host_source_is_the_root()
    {
        var root = MakeRoot("WKS-01");
        var found = HostDiscovery.Discover(root);
        found.Should().ContainSingle();
        found[0].RootPath.Should().Be(root);
        found[0].HostName.Should().Be("WKS-01");
        found[0].HostNameSource.Should().Be(HostDiscovery.FromFolderName);
        found[0].OsFamily.Should().Be(OsFamily.Windows);
        found[0].AcquisitionKind.Should().Be(AcquisitionKind.CopiedCollection);
    }

    [Fact]
    public void Two_hosts_with_C_folders()
    {
        var a = MakeRoot("HOST1", "C");
        var b = MakeRoot("HOST2", "C");
        var found = HostDiscovery.Discover(_dir);
        found.Select(f => f.RootPath).Should().Equal(a, b);
        found.Select(f => f.HostName).Should().Equal("HOST1", "HOST2");
    }

    [Fact]
    public void Kape_tout_layout_and_does_not_descend_into_found_roots()
    {
        var root = MakeRoot("SRV-DC01", "tout", "C");
        MakeRoot("SRV-DC01", "tout", "C", "Users", "alice", "nested");   // would be a root if we descended into Users
        File.WriteAllBytes(Path.Combine(root, "$MFT"), new byte[16]);    // KAPE copies $MFT, but no OS binaries
        var found = HostDiscovery.Discover(_dir);
        found.Should().ContainSingle();
        found[0].RootPath.Should().Be(root);
        found[0].HostName.Should().Be("SRV-DC01");
        found[0].AcquisitionKind.Should().Be(AcquisitionKind.CopiedCollection);
    }

    [Fact]
    public void Full_volume_with_MFT_is_mounted_image()
    {
        var root = MakeRoot("IMG", "[root]");
        File.WriteAllBytes(Path.Combine(root, "$MFT"), new byte[16]);
        File.WriteAllBytes(Path.Combine(root, "Windows", "System32", "ntoskrnl.exe"), new byte[16]);
        var found = HostDiscovery.Discover(_dir);
        found.Should().ContainSingle().Which.AcquisitionKind.Should().Be(AcquisitionKind.MountedImageOrRawCopy);
        found[0].HostName.Should().Be("IMG");
    }

    [Fact]
    public void Depth_limit_is_respected()
    {
        MakeRoot("a", "b", "c", "HOST", "C");   // root at depth 5
        HostDiscovery.Discover(_dir).Should().BeEmpty();
        HostDiscovery.Discover(_dir, maxDepth: 5).Should().ContainSingle().Which.HostName.Should().Be("HOST");
    }

    [Fact]
    public void Duplicate_names_are_made_unique()
    {
        MakeRoot("case1", "PC", "C");
        MakeRoot("case2", "PC", "C");
        HostDiscovery.Discover(_dir).Select(f => f.HostName).Should().Equal("PC", "PC (2)");
    }

    [Fact]
    public void Host_name_comes_from_SYSTEM_hive_when_readable()
    {
        var root = MakeRoot("box", "C");
        File.WriteAllBytes(Path.Combine(root, "Windows", "System32", "config", "SYSTEM"), new byte[16]);
        var found = HostDiscovery.Discover(_dir, opener: new FakeOpener("WS:42*"));
        found.Should().ContainSingle();
        found[0].HostName.Should().Be("WS_42_");
        found[0].HostNameSource.Should().Be(HostDiscovery.FromSystemHive);
    }

    [Fact]
    public void Unreadable_hive_falls_back_to_folder()
    {
        var root = MakeRoot("box", "C");
        File.WriteAllBytes(Path.Combine(root, "Windows", "System32", "config", "SYSTEM"), new byte[16]);   // not a hive
        HostDiscovery.Discover(_dir).Should().ContainSingle().Which.HostNameSource.Should().Be(HostDiscovery.FromFolderName);
    }

    [Fact]
    public void Sanitize_handles_reserved_and_empty_names()
    {
        HostDiscovery.Sanitize("CON").Should().Be("_CON");
        HostDiscovery.Sanitize("  ...  ").Should().Be(HostDiscovery.DefaultHostName);
        HostDiscovery.Sanitize("a/b\\c").Should().Be("a_b_c");
    }

    [Fact]
    public void Plain_folder_yields_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "docs", "Users"));   // "Users" under a non-volume folder is not a root
        HostDiscovery.Discover(_dir).Should().BeEmpty();
    }

    [Fact]
    public void Only_the_running_os_root_is_live_and_not_walked()
    {
        var a = MakeRoot("HOST1", "C");
        MakeRoot("HOST2", "C");
        var live = HostDiscovery.Discover(_dir, isRunningOsRoot: p => string.Equals(Path.GetFullPath(p).TrimEnd('\\'), _dir, StringComparison.OrdinalIgnoreCase));
        live.Should().ContainSingle();
        live[0].RootPath.Should().Be(_dir);
        live[0].AcquisitionKind.Should().Be(AcquisitionKind.LiveVolume);
        // the same tree when it is NOT the running OS volume (USB disk / mounted image reporting "Fixed"): walked, hosts found
        var evidence = HostDiscovery.Discover(_dir, isRunningOsRoot: _ => false);
        evidence.Select(s => s.HostName).Should().Equal("HOST1", "HOST2");
        evidence.Should().OnlyContain(s => s.AcquisitionKind == AcquisitionKind.CopiedCollection);
        evidence[0].RootPath.Should().Be(a);
    }

    [Fact]
    public void Non_os_volume_with_full_os_is_mounted_image_not_live()
    {
        var root = MakeRoot("E01");
        File.WriteAllBytes(Path.Combine(root, "$MFT"), new byte[16]);
        File.WriteAllBytes(Path.Combine(root, "Windows", "System32", "ntoskrnl.exe"), new byte[16]);
        HostDiscovery.Kind(root, _ => false).Should().Be(AcquisitionKind.MountedImageOrRawCopy);
        HostDiscovery.Kind(root, _ => true).Should().Be(AcquisitionKind.LiveVolume);
        var scan = ArtifactScanner.DiscoverSystems(root, null, 3, out var notes, isRunningOsRoot: _ => false);
        scan.Should().ContainSingle().Which.AcquisitionKind.Should().Be(AcquisitionKind.MountedImageOrRawCopy);
        notes.Should().NotContain(n => n.Contains("Live NTFS"));
    }

    [Fact]
    public void Running_os_root_detection()
    {
        SourceLayoutDetector.IsRunningOsRoot(_dir).Should().BeFalse();
        SourceLayoutDetector.IsLiveVolumeRoot(_dir).Should().BeFalse();
        SourceLayoutDetector.IsVolumeRoot(_dir).Should().BeFalse();
        var sys = Path.GetPathRoot(Environment.SystemDirectory)!;
        SourceLayoutDetector.IsVolumeRoot(sys).Should().BeTrue();
        if (OperatingSystem.IsWindows() && new DriveInfo(sys).DriveFormat == "NTFS")
            SourceLayoutDetector.IsRunningOsRoot(sys).Should().BeTrue();
    }

    [Fact]
    public void Discovered_system_carries_the_interface_ips_of_its_system_hive()
    {
        var root = MakeRoot("EV", "C");
        File.WriteAllBytes(Path.Combine(root, "Windows", "System32", "config", "SYSTEM"), new byte[] { 1 });
        var found = HostDiscovery.Discover(Path.Combine(_dir, "EV"), opener: new FakeOpener("WS01", withIps: true));
        found.Should().ContainSingle();
        found[0].HostName.Should().Be("WS01");
        found[0].IpAddresses.Should().Equal("192.168.1.20", "192.168.1.21", "10.0.0.5");   // static first, 0.0.0.0 dropped, distinct

        HostDiscovery.Discover(MakeRoot("NOHIVE2"))[0].IpAddresses.Should().BeEmpty();
    }

    [Fact]
    public void Interface_addresses_skip_unusable_and_count_skipped()
    {
        var h = new FakeOpener("X", withIps: true).Open("", "SYSTEM");
        var list = InterfaceAddresses.Read(h.GetKey(@"ControlSet002\" + InterfaceAddresses.InterfacesKey), out int skipped);
        list.Should().HaveCount(2);
        skipped.Should().Be(1);
        InterfaceAddresses.AllIps(new InMemoryHive("SYSTEM")).Should().BeEmpty();
    }

    [Fact]
    public void Known_hosts_read_names_and_ips_from_the_case_computer_details()
    {
        var caseDir = Path.Combine(_dir, "case");
        var report = new ComputerDetailsReport();
        var sd = new SystemDetails { Title = "WS01" };
        sd.Fields.Add(new DetailField(ComputerDetailsCollector.CatSystem, "Computer name", "WS01", "s"));
        sd.Fields.Add(new DetailField(ComputerDetailsCollector.CatNetwork + ": Intel", "IP address (static)", "10.0.0.5, 10.0.0.6", "s"));
        sd.Fields.Add(new DetailField(ComputerDetailsCollector.CatNetwork + ": Wifi", "IP address (DHCP)", "192.168.0.9", "s"));
        sd.Fields.Add(new DetailField(ComputerDetailsCollector.CatNetwork + ": Wifi", "DHCP server", "192.168.0.1", "s"));
        report.Systems.Add(sd);
        ComputerDetailsStore.Write(report, ComputerDetailsStore.HostFolderFor(caseDir, "WS01"));

        var hosts = ComputerDetailsStore.KnownHosts(caseDir);
        hosts.Should().ContainSingle();
        hosts[0].Host.Should().Be("WS01");
        hosts[0].Names.Should().Equal("WS01");
        hosts[0].IpAddresses.Should().Equal("10.0.0.5", "10.0.0.6", "192.168.0.9");
        ComputerDetailsStore.KnownHosts(Path.Combine(_dir, "nocase")).Should().BeEmpty();
    }

    private sealed class FakeOpener : IHiveOpener
    {
        private readonly string _name;
        private readonly bool _withIps;
        public FakeOpener(string name, bool withIps = false) { _name = name; _withIps = withIps; }
        public IRegistryHive Open(string hivePath, string name)
        {
            var h = new InMemoryHive(name);
            h.Key("Select").With(RegValue.Dword("Current", 2));
            h.Key(@"ControlSet002\Control\ComputerName\ComputerName").With(RegValue.Sz("ComputerName", _name));
            if (_withIps)
            {
                h.Key(@"ControlSet002\Services\Tcpip\Parameters\Interfaces\{A}").With(RegValue.Multi("IPAddress", "192.168.1.20", "192.168.1.21"),
                    RegValue.Sz("DhcpIPAddress", "10.0.0.5"));
                h.Key(@"ControlSet002\Services\Tcpip\Parameters\Interfaces\{B}").With(RegValue.Sz("DhcpIPAddress", "10.0.0.5"));
                h.Key(@"ControlSet002\Services\Tcpip\Parameters\Interfaces\{C}").With(RegValue.Sz("DhcpIPAddress", "0.0.0.0"));
            }
            return h;
        }
    }
}
