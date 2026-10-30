using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.SystemInfo;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SystemInfoComputerDetailsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_sysinfo_" + Guid.NewGuid().ToString("N"));

    public SystemInfoComputerDetailsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DetailField F(SystemDetails d, string field, string? category = null) =>
        d.Fields.Single(f => f.Field == field && (category == null || f.Category == category));

    private static (InMemoryHive System, InMemoryHive Software, InMemoryHive Sam) BuildHives()
    {
        var sys = new InMemoryHive("SYSTEM");
        sys.Key("Select").With(RegValue.Dword("Current", 2));
        sys.Key(@"ControlSet001\Control\ComputerName\ComputerName").With(RegValue.Sz("ComputerName", "WRONG-SET"));
        sys.Key(@"ControlSet002\Control\ComputerName\ComputerName").With(RegValue.Sz("ComputerName", "WKS-042"));
        sys.Key(@"ControlSet002\Services\Tcpip\Parameters").With(RegValue.Sz("Hostname", "wks-042"), RegValue.Sz("Domain", "corp.example"));
        var if1 = sys.Key(@"ControlSet002\Services\Tcpip\Parameters\Interfaces\{AAAA0000-0000-0000-0000-000000000001}");
        if1.With(RegValue.Dword("EnableDHCP", 1), RegValue.Sz("DhcpIPAddress", "10.0.0.5"), RegValue.Sz("DhcpSubnetMask", "255.255.255.0"),
            RegValue.Multi("DhcpDefaultGateway", "10.0.0.1"), RegValue.Sz("DhcpServer", "10.0.0.2"), RegValue.Sz("DhcpNameServer", "10.0.0.2 8.8.8.8"),
            RegValue.Dword("LeaseObtainedTime", 1700000000), RegValue.Dword("LeaseTerminatesTime", 1700086400));
        sys.Key(@"ControlSet002\Services\Tcpip\Parameters\Interfaces\{BBBB0000-0000-0000-0000-000000000002}")
            .With(RegValue.Dword("EnableDHCP", 0), RegValue.Multi("IPAddress", "192.168.1.20", "192.168.1.21"), RegValue.Multi("SubnetMask", "255.255.255.0", "255.255.255.0"),
                RegValue.Multi("DefaultGateway", "192.168.1.1"));
        sys.Key(@"ControlSet002\Services\Tcpip\Parameters\Interfaces\{CCCC0000-0000-0000-0000-000000000003}")
            .With(RegValue.Dword("EnableDHCP", 1), RegValue.Sz("DhcpIPAddress", "0.0.0.0"));
        sys.Key(@"ControlSet002\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\{BBBB0000-0000-0000-0000-000000000002}\Connection")
            .With(RegValue.Sz("Name", "Ethernet 2"));
        sys.Key(@"ControlSet002\Control\TimeZoneInformation").With(RegValue.Sz("TimeZoneKeyName", "W. Europe Standard Time"),
            RegValue.Dword("Bias", unchecked((uint)-60)), RegValue.Dword("ActiveTimeBias", unchecked((uint)-120)), RegValue.Dword("DaylightBias", unchecked((uint)-60)),
            RegValue.Dword("DynamicDaylightTimeDisabled", 0));
        var shutdown = new DateTime(2024, 3, 1, 12, 30, 0, DateTimeKind.Utc).ToFileTimeUtc();
        sys.Key(@"ControlSet002\Control\Windows").With(RegValue.Bin("ShutdownTime", BitConverter.GetBytes(shutdown)));
        sys.Key(@"ControlSet002\Control\Session Manager\Environment").With(RegValue.Sz("PROCESSOR_ARCHITECTURE", "AMD64"),
            RegValue.Sz("PROCESSOR_IDENTIFIER", "Intel64 Family 6 Model 85"), RegValue.Sz("NUMBER_OF_PROCESSORS", "16"));
        sys.Key(@"ControlSet002\Control\Session Manager\Memory Management").With(RegValue.Multi("PagingFiles", @"?:\pagefile.sys"),
            RegValue.Multi("ExistingPageFiles", @"\??\C:\pagefile.sys"));
        var mbr = new byte[12];
        BitConverter.GetBytes(0x1234ABCDu).CopyTo(mbr, 0);
        BitConverter.GetBytes(1048576UL).CopyTo(mbr, 4);
        sys.Key("MountedDevices").With(new RegValue(@"\DosDevices\C:", RegType.Binary, mbr),
            new RegValue(@"\DosDevices\E:", RegType.Binary, Encoding.Unicode.GetBytes(@"\??\USBSTOR#Disk&Ven_SanDisk#123")),
            new RegValue(@"\??\Volume{11111111-2222-3333-4444-555555555555}", RegType.Binary, mbr));
        sys.Key(@"ControlSet002\Enum\USBSTOR\Disk&Ven_SanDisk&Prod_Cruzer&Rev_1.00\4C530001").With(RegValue.Sz("FriendlyName", "SanDisk Cruzer USB Device"));
        sys.Key(@"ControlSet002\Enum\SCSI\Disk&Ven_NVMe&Prod_Samsung_SSD_970\5&1a2b&0&000000").With(RegValue.Sz("FriendlyName", "Samsung SSD 970 EVO"));
        sys.Key(@"ControlSet002\Enum\SCSI\CdRom&Ven_X\1").With(RegValue.Sz("FriendlyName", "not a disk"));

        var soft = new InMemoryHive("SOFTWARE");
        soft.Key(@"Microsoft\Windows NT\CurrentVersion").With(RegValue.Sz("ProductName", "Windows 10 Pro"), RegValue.Sz("EditionID", "Professional"),
            RegValue.Sz("DisplayVersion", "23H2"), RegValue.Sz("CurrentBuild", "22631"), RegValue.Dword("UBR", 3007), RegValue.Dword("InstallDate", 1690000000),
            RegValue.Qword("InstallTime", (ulong)new DateTime(2023, 7, 22, 4, 26, 40, DateTimeKind.Utc).ToFileTimeUtc()),
            RegValue.Sz("RegisteredOwner", "Analyst"), RegValue.Sz("SystemRoot", @"C:\WINDOWS"));
        soft.Key(@"Microsoft\Windows NT\CurrentVersion\NetworkCards\7").With(RegValue.Sz("ServiceName", "{AAAA0000-0000-0000-0000-000000000001}"),
            RegValue.Sz("Description", "Intel(R) Ethernet Connection I219-LM"));
        soft.Key(@"Microsoft\Windows NT\CurrentVersion\ProfileList\S-1-5-21-1-2-3-1001").With(RegValue.Sz("ProfileImagePath", @"C:\Users\alice", RegType.ExpandSz));
        var st = new byte[16];
        foreach (var (i, v) in new[] { (0, 2024), (2, 2), (6, 14), (8, 9), (10, 5), (12, 7) }) BitConverter.GetBytes((ushort)v).CopyTo(st, i);
        soft.Key(@"Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\{P1}").With(RegValue.Sz("ProfileName", "CoffeeShop"), RegValue.Bin("DateLastConnected", st));

        var sam = new InMemoryHive("SAM");
        var names = sam.Key(@"SAM\Domains\Account\Users\Names");
        var alice = new MemKey("alice", new DateTime(2023, 7, 22, 5, 0, 0, DateTimeKind.Utc));
        alice.Vals.Add(new RegValue("", 0x3E9, Array.Empty<byte>()));
        names.Children.Add(alice);
        var fval = new byte[0x50];
        BitConverter.GetBytes(new DateTime(2024, 2, 29, 8, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).CopyTo(fval, 8);
        BitConverter.GetBytes((ushort)0x0010).CopyTo(fval, 56);
        BitConverter.GetBytes((ushort)42).CopyTo(fval, 66);
        sam.Key(@"SAM\Domains\Account\Users\000003E9").With(RegValue.Bin("F", fval));
        return (sys, soft, sam);
    }

    [Fact]
    public void Extracts_identity_os_network_and_sources_from_hives()
    {
        var (sys, soft, sam) = BuildHives();
        var d = ComputerDetailsCollector.Extract(@"X:\evidence\C", sys, soft, sam, new FileFacts { HiberfilSize = 6L << 30 });

        F(d, "Computer name").Value.Should().Be("WKS-042");   // from the current control set (Select\Current = 2)
        F(d, "Computer name").Source.Should().Be(@"SYSTEM\ControlSet002\Control\ComputerName\ComputerName\ComputerName");
        F(d, "Current control set").Value.Should().Be("ControlSet002");
        F(d, "Host name (TCP/IP)").Value.Should().Be("wks-042");
        F(d, "Domain (TCP/IP)").Value.Should().Be("corp.example");
        F(d, "Product name").Value.Should().Be("Windows 10 Pro");
        F(d, "Operating system (derived)").Value.Should().Be("Windows 11 Pro");
        F(d, "Build").Value.Should().Be("22631.3007");
        F(d, "Version").Value.Should().Be("23H2");
        F(d, "Install date (UTC)").Value.Should().Be("2023-07-22 04:26:40");
        F(d, "Install time (UTC)").Value.Should().Be("2023-07-22 04:26:40");
        F(d, "Architecture").Value.Should().Be("x64 (AMD64)");
        F(d, "Logical processors").Value.Should().Be("16");
        F(d, "Time zone").Value.Should().Be("W. Europe Standard Time");
        F(d, "UTC offset (standard)").Value.Should().StartWith("UTC+01:00");
        F(d, "UTC offset (active at last update)").Value.Should().StartWith("UTC+02:00");
        F(d, "Daylight saving").Value.Should().Contain("-60 min").And.Contain("automatic DST adjustment on");
        F(d, "Last shutdown (UTC)").Value.Should().Be("2024-03-01 12:30:00");
        F(d, "Installed memory").Value.Should().Be(ComputerDetailsCollector.MemoryUnknown);
        F(d, "hiberfil.sys size").Value.Should().StartWith("6 GB");
        F(d, "Paging files (existing at last boot)").Value.Should().Be(@"\??\C:\pagefile.sys");

        var dhcpCat = "Network: Intel(R) Ethernet Connection I219-LM";
        F(d, "IP address (DHCP)", dhcpCat).Value.Should().Be("10.0.0.5");
        F(d, "IP address (DHCP)", dhcpCat).Source.Should().Be(@"SYSTEM\ControlSet002\Services\Tcpip\Parameters\Interfaces\{AAAA0000-0000-0000-0000-000000000001}\DhcpIPAddress");
        F(d, "DNS servers", dhcpCat).Value.Should().Be("10.0.0.2, 8.8.8.8");
        F(d, "Default gateway (DHCP)", dhcpCat).Value.Should().Be("10.0.0.1");
        F(d, "Lease obtained (UTC)", dhcpCat).Value.Should().Be("2023-11-14 22:13:20");
        F(d, "IP address (static)", "Network: Ethernet 2").Value.Should().Be("192.168.1.20, 192.168.1.21");
        d.Fields.Should().NotContain(f => f.Category.Contains("CCCC"));   // 0.0.0.0 only: not listed
        d.Notes.Should().Contain(n => n.Contains("1 network interface(s) without an IPv4 address"));

        F(d, "CoffeeShop").Value.Should().Be("last connected 2024-02-14 09:05:07 (local time)");
        F(d, "C:").Value.Should().Be("MBR disk signature 1234ABCD, partition offset 1,048,576 bytes");
        F(d, "E:").Value.Should().Be(@"\??\USBSTOR#Disk&Ven_SanDisk#123");
        F(d, "Volume GUIDs seen").Value.Should().Be("1");
        F(d, "USB: SanDisk Cruzer USB Device").Value.Should().Contain("4C530001");
        F(d, "SCSI/SATA/NVMe: Samsung SSD 970 EVO").Should().NotBeNull();
        d.Fields.Should().NotContain(f => f.Field.Contains("not a disk"));
        F(d, "S-1-5-21-1-2-3-1001").Value.Should().Be(@"C:\Users\alice");
        F(d, "alice").Value.Should().Be("RID 1001 · created 2023-07-22 05:00:00 · last login 2024-02-29 08:00:00 · logins 42");

        d.Title.Should().Be("WKS-042");
        d.Summary.Should().Be("WKS-042 · Windows 11 Pro 23H2 (22631.3007) · 10.0.0.5, 192.168.1.20 +1");
        d.Fields.Should().OnlyContain(f => f.Value.Length > 0 && f.Source.Length > 0);
    }

    [Fact]
    public void Missing_hives_and_values_give_dashes()
    {
        var d = ComputerDetailsCollector.Extract(@"X:\C", null, null, null);
        F(d, "Computer name").Value.Should().Be("-");
        F(d, "Product name").Value.Should().Be("-");
        d.Title.Should().Be("C");
        d.Summary.Should().Be("- · - · -");

        var sys = new InMemoryHive("SYSTEM");   // no Select key: falls back to ControlSet001
        sys.Key(@"ControlSet001\Control\ComputerName\ComputerName").With(RegValue.Sz("ComputerName", "HOST1"));
        var d2 = ComputerDetailsCollector.Extract(@"X:\C", sys, null, null);
        F(d2, "Computer name").Value.Should().Be("HOST1");
        F(d2, "Current control set").Source.Should().Contain("Select\\Current missing");
    }

    [Fact]
    public void Select_Current_naming_an_absent_control_set_falls_back_to_ControlSet001()
    {
        var sys = new InMemoryHive("SYSTEM");
        sys.Key("Select").With(RegValue.Dword("Current", 3));   // ControlSet003 is not in the hive
        sys.Key(@"ControlSet001\Control\ComputerName\ComputerName").With(RegValue.Sz("ComputerName", "HOST1"));
        F(ComputerDetailsCollector.Extract(@"X:\C", sys, null, null), "Computer name").Value.Should().Be("HOST1");
        ComputerDetailsCollector.ResolveControlSet(sys).Should().Be("ControlSet001");
    }

    [Theory]
    [InlineData(420L, "UTC-07:00")]
    [InlineData(0L, "UTC+00:00")]
    [InlineData(4294967036L, "UTC+04:20")]   // -260 as an unsigned DWORD
    public void Bias_is_formatted_as_utc_offset(long bias, string expected)
        => ComputerDetailsCollector.Bias(bias).Should().StartWith(expected);

    [Fact]
    public void Value_decoding_and_helpers()
    {
        RegValue.Multi("x", "a", "b").AsMulti().Should().Equal("a", "b");
        RegValue.Multi("x", "a", "b").AsString().Should().Be("a, b");
        RegValue.Dword("x", 7).AsNumber().Should().Be(7);
        RegValue.Qword("x", 1UL << 40).AsNumber().Should().Be(1L << 40);
        RegValue.Sz("x", "hi").AsString().Should().Be("hi");
        ComputerDetailsCollector.DeriveOsName("Windows 10 Enterprise", "19045").Should().Be("Windows 10 Enterprise");
        ComputerDetailsCollector.DeriveOsName("Windows 10 Enterprise", "22000").Should().Be("Windows 11 Enterprise");
        var gpt = Encoding.ASCII.GetBytes("DMIO:ID:").Concat(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef").ToByteArray()).ToArray();
        ComputerDetailsCollector.DecodeMountedDevice(gpt).Should().Be("GPT partition GUID {01234567-89ab-cdef-0123-456789abcdef}");
        var boot = new byte[512];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(boot, 3);
        BitConverter.GetBytes(0x0011223344556677UL).CopyTo(boot, 0x48);
        FileFacts.NtfsSerial(boot).Should().Be("4455-6677 (full 0011223344556677)");
        FileFacts.NtfsSerial(new byte[512]).Should().BeNull();
    }

    [Fact]
    public void Store_writes_json_and_csv_atomically_and_reads_back()
    {
        var (sys, soft, sam) = BuildHives();
        var report = new ComputerDetailsReport { Source = @"X:\evidence" };
        report.Systems.Add(ComputerDetailsCollector.Extract(@"X:\evidence\C", sys, soft, sam));
        var caseDir = Path.Combine(_dir, "case");
        var (json, csv) = ComputerDetailsStore.Write(report, ComputerDetailsStore.FolderFor(caseDir));
        json.Should().Be(ComputerDetailsStore.JsonPathFor(caseDir));
        csv.Should().Be(ComputerDetailsStore.CsvPathFor(caseDir));
        Directory.GetFiles(ComputerDetailsStore.FolderFor(caseDir), "*.tmp").Should().BeEmpty();
        var lines = File.ReadAllLines(csv);
        lines[0].Should().Be("Category,Field,Value,Source");
        lines.Should().Contain(@"System,Computer name,WKS-042,SYSTEM\ControlSet002\Control\ComputerName\ComputerName\ComputerName");
        lines.Should().Contain(l => l.StartsWith("Network: Ethernet 2,IP address (static),\"192.168.1.20, 192.168.1.21\","));
        var back = ComputerDetailsStore.TryLoad(caseDir)!;
        back.Systems.Single().Summary.Should().Be(report.Systems[0].Summary);
        back.Systems[0].Fields.Count.Should().Be(report.Systems[0].Fields.Count);
        File.ReadAllText(json).Should().Contain("\"field\"").And.Contain("\"summary\"").And.Contain("\"schemaVersion\"");
    }

    [Fact]
    public void Manifest_source_path_is_found()
    {
        var caseDir = Path.Combine(_dir, "case2");
        Directory.CreateDirectory(caseDir);
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(caseDir, "EzHub_manifest_20240101T000000Z.json"), "{\"sourcePath\": \"" + src.Replace("\\", "\\\\") + "\"}");
        File.WriteAllText(Path.Combine(caseDir, "EzHub_manifest_20250101T000000Z.json"), "{\"sourcePath\": \"Z:\\\\gone\"}");
        ComputerDetailsStore.FindSourceFromManifests(caseDir).Should().Be(src);
        ComputerDetailsStore.FindSourceFromManifests(Path.Combine(_dir, "nope")).Should().BeNull();
    }

    [Fact]
    public void Manifest_source_roots_and_systems_are_the_exact_extracted_roots_not_their_parent()
    {
        var caseDir = Path.Combine(_dir, "case3");
        var extracted = Path.Combine(caseDir, ".ezstaging", "extracted");
        var rootA = Path.Combine(extracted, "aaaa1111", "one");
        var rootB = Path.Combine(extracted, "bbbb2222", "two");
        var unrelated = Path.Combine(extracted, "cccc3333", "earlier-ingest");
        foreach (var d in new[] { rootA, rootB, unrelated }) Directory.CreateDirectory(Path.Combine(d, "C"));
        var archive = Path.Combine(_dir, "two.zip");
        File.WriteAllText(archive, "PK");

        // Older run (another archive), then this ingest: sourcePath = the archive the user picked, sourceRoots = this ingest's roots.
        WriteManifest(caseDir, "20240101T000000Z", new RunManifest
        {
            SourcePath = unrelated,
            Sources = new() { new ManifestSource { RootPath = Path.Combine(unrelated, "C"), Host = "OLD" } },
        });
        WriteManifest(caseDir, "20250101T000000Z", new RunManifest
        {
            SourcePath = archive,
            SourceRoots = new() { rootA, rootB, Path.Combine(extracted, "gone") },
            Sources = new()
            {
                new ManifestSource { RootPath = Path.Combine(rootA, "C"), Host = "HOST1", ExtractedTo = rootA },
                new ManifestSource { RootPath = Path.Combine(rootB, "C"), Host = "HOST2", ExtractedTo = rootB },
                new ManifestSource { RootPath = Path.Combine(rootB, "missing"), Host = "HOST3" },
            },
        });

        ComputerDetailsStore.FindSourcesFromManifests(caseDir).Should().Equal(rootA, rootB);
        ComputerDetailsStore.FindSourceFromManifests(caseDir).Should().Be(rootA);
        ComputerDetailsStore.FindSystemsFromManifests(caseDir).Should().Equal(
            new ComputerDetailsStore.ManifestSystem(Path.Combine(rootA, "C"), "HOST1"),
            new ComputerDetailsStore.ManifestSystem(Path.Combine(rootB, "C"), "HOST2"),
            new ComputerDetailsStore.ManifestSystem(Path.Combine(unrelated, "C"), "OLD"));
        ComputerDetailsStore.FindSourcesFromManifests(caseDir).Should().NotContain(extracted);
    }

    [Fact]
    public void Manifest_with_an_archive_source_path_and_no_roots_falls_back_to_an_older_folder_source()
    {
        var caseDir = Path.Combine(_dir, "case4");
        var folder = Path.Combine(_dir, "src4");
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(_dir, "a4.zip");
        File.WriteAllText(archive, "PK");
        WriteManifest(caseDir, "20240101T000000Z", new RunManifest { SourcePath = folder });
        WriteManifest(caseDir, "20250101T000000Z", new RunManifest { SourcePath = archive });
        ComputerDetailsStore.FindSourcesFromManifests(caseDir).Should().Equal(folder);
        ComputerDetailsStore.FindSystemsFromManifests(caseDir).Should().BeEmpty();
    }

    private static void WriteManifest(string caseDir, string stamp, RunManifest m)
    {
        Directory.CreateDirectory(caseDir);
        File.WriteAllText(Path.Combine(caseDir, $"EzHub_manifest_{stamp}.json"), System.Text.Json.JsonSerializer.Serialize(m, ManifestWriter.JsonOptions));
    }

    // ------------------------------------------------------------------ real hives (samples\tout from EricZimmerman/Registry test data)

    private static string? SamplesConfig()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "samples", "tout", "C", "Windows", "System32", "config");
            if (Directory.Exists(p)) return p;
            if (File.Exists(Path.Combine(d.FullName, "EzForensics.sln"))) break;
        }
        return null;
    }

    [Fact]
    public void Real_sample_hives_are_parsed_without_modifying_them()
    {
        var config = SamplesConfig();
        if (config == null) return; // samples\ is git-ignored; nothing to check on a clean clone
        var root = Path.GetFullPath(Path.Combine(config, "..", "..", ".."));
        var before = Directory.GetFiles(config).ToDictionary(f => f, ZimmermanHiveOpener.Sha256);

        var report = new ComputerDetailsCollector().Collect(Path.GetDirectoryName(root)!);
        var s = report.Systems.Should().ContainSingle().Subject;
        F(s, "Computer name").Value.Should().Be("HAXOR4");
        F(s, "Host name (TCP/IP)").Value.Should().Be("haxor4");
        F(s, "Domain (TCP/IP)").Value.Should().Be("SU.local");
        F(s, "Product name").Value.Should().Be("Windows 7 Professional");
        F(s, "Build").Value.Should().Be("7601");
        F(s, "Time zone").Value.Should().Be("Mountain Standard Time");
        F(s, "UTC offset (standard)").Value.Should().StartWith("UTC-07:00");
        s.Fields.Where(f => f.Field.StartsWith("IP address")).Select(f => f.Value).Should().Contain(new[] { "192.168.195.1", "192.168.5.1", "192.168.134.1" });
        // NetworkCards in this SOFTWARE (another machine) names no interface of this SYSTEM: titled by the connection name
        F(s, "IP address (static)", "Network: VMware Network Adapter VMnet1").Value.Should().Be("192.168.195.1");
        s.Fields.Should().Contain(f => f.Category == ComputerDetailsCollector.CatUsers && f.Field == "Administrator" && f.Value.StartsWith("RID 500"));
        s.Hives.Single(h => h.Name == "SYSTEM").Dirty.Should().BeTrue();
        s.Hives.Single(h => h.Name == "SYSTEM").Note.Should().Contain("no .LOG1/.LOG2");
        s.Hives.Single(h => h.Name == "SOFTWARE").Dirty.Should().BeFalse();

        Directory.GetFiles(config).ToDictionary(f => f, ZimmermanHiveOpener.Sha256).Should().Equal(before);
    }

    [Fact]
    public void Dirty_hive_with_unusable_logs_is_read_and_the_evidence_is_untouched()
    {
        var config = SamplesConfig();
        if (config == null) return;
        var cfg = Path.Combine(_dir, "ev", "C", "Windows", "System32", "config");
        Directory.CreateDirectory(cfg);
        var sam = Path.Combine(cfg, "SAM");
        File.Copy(Path.Combine(config, "SAM"), sam);
        // make it dirty: secondary sequence number (offset 8) != primary (offset 4)
        var bytes = File.ReadAllBytes(sam);
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, 4) - 1).CopyTo(bytes, 8);
        File.WriteAllBytes(sam, bytes);
        File.WriteAllBytes(sam + ".LOG1", Encoding.ASCII.GetBytes("not a transaction log").Concat(new byte[4096]).ToArray());
        var hashes = new[] { sam, sam + ".LOG1" }.ToDictionary(f => f, ZimmermanHiveOpener.Sha256);

        using (var h = new ZimmermanHiveOpener().Open(sam, "SAM"))
        {
            h.Info.Dirty.Should().BeTrue();
            h.Info.TransactionLogs.Should().Equal("SAM.LOG1");
            h.Info.LogsReplayed.Should().BeFalse();
            h.Info.Note.Should().Contain("replaying the transaction logs failed");
            h.GetKey(@"SAM\Domains\Account\Users\Names")!.SubKeys.Select(k => k.Name).Should().Contain("Administrator");
        }
        new[] { sam, sam + ".LOG1" }.ToDictionary(f => f, ZimmermanHiveOpener.Sha256).Should().Equal(hashes);
        Directory.GetFiles(cfg).Should().HaveCount(2);
    }

    [Fact]
    public void Command_line_writes_outputs_and_reports_missing_hives()
    {
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        var outDir = Path.Combine(_dir, "out");
        var w = new StringWriter();
        ComputerDetailsStore.RunCommandLine(new[] { "--source", empty, "--out", outDir }, w).Should().Be(2);
        File.Exists(Path.Combine(outDir, ComputerDetailsStore.JsonName)).Should().BeTrue();
        w.ToString().Should().Contain("No Windows system root");
        ComputerDetailsStore.RunCommandLine(new[] { "--source" }, new StringWriter()).Should().Be(1);
    }
}
