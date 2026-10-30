using System.Text;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Process tree from non-memory CSVs: EvtxECmd (Security 4688, Sysmon) and Prefetch (linked by full path + run time only).</summary>
public class ProcessEvidenceTests : IDisposable
{
    private readonly string _host = Path.Combine(Path.GetTempPath(), "ezforensics_procev_" + Guid.NewGuid().ToString("N"));

    public ProcessEvidenceTests() => Directory.CreateDirectory(_host);
    public void Dispose() { try { Directory.Delete(_host, true); } catch { } }

    private void Write(string folder, string name, string content)
    {
        var dir = Path.Combine(_host, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), content, new UTF8Encoding(false));
    }

    private const string EvtxHeader = "TimeCreated,EventId,Channel,Provider,Computer,UserName,ExecutableInfo,PayloadData1,PayloadData2,PayloadData3,PayloadData4,PayloadData5,PayloadData6,MapDescription\r\n";
    private const string Sec = "Security,Microsoft-Windows-Security-Auditing,PC1";
    private const string Sys = "Microsoft-Windows-Sysmon/Operational,Microsoft-Windows-Sysmon,PC1";

    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private static string Ev4688(string time, string user, string cmd, string parent, string pid, string ppid) =>
        $"{time},4688,{Sec},{user},{Q(cmd)},{Q(parent)},{Q(pid)},{Q(ppid)},,,,New process\r\n";

    private static string Sysmon1(string time, string user, string cmd, string pid, string guid, string ppid, string pguid, string parentImage, string hashes) =>
        $"{time},1,{Sys},{user},{Q(cmd)},{Q($"ProcessID: {pid}, ProcessGUID: {guid}")},{Q($"ParentProcessID: {ppid}, ParentProcessGUID: {pguid}")},{Q("ParentImage: " + parentImage)},,{Q("Hashes: " + hashes)},,Process Create\r\n";

    private static ProcessNode Find(ProcessGraph g, int pid, int index = 0) => g.AllNodes.Where(n => n.Pid == pid).OrderBy(n => n.CreateTime).ElementAt(index);

    [Fact]
    public void EvtxOnlyHost_BuildsGraph_FromHexPidsWithLabels()
    {
        Write("EventLogs", "EventLogs.csv", EvtxHeader +
            Ev4688("2024-03-01 10:00:00.1234567", "alice", @"C:\Windows\explorer.exe", @"C:\Windows\System32\userinit.exe", "0x400", "0x1f0") +
            Ev4688("2024-03-01 10:05:00.0000000", "alice", @"C:\Windows\System32\cmd.exe /c whoami", @"C:\Windows\explorer.exe", "PID: 0x1a2c", "PID: 0x400") +
            Ev4688("2024-03-01 10:05:01.0000000", "alice", @"""C:\Windows\System32\whoami.exe""", @"C:\Windows\System32\cmd.exe", "6701", "0x1a2c"));

        var files = ProcessGraphBuilder.FindFiles(_host);
        files.HasProcessLists.Should().BeFalse();
        files.CanBuildTree.Should().BeTrue();

        var g = ProcessGraphBuilder.Build(_host);
        g.AllNodes.Should().HaveCount(3);
        g.SourceCounts[ProcessSources.Evtx4688].Should().Be(3);
        var explorer = Find(g, 0x400);
        var cmd = Find(g, 0x1a2c);
        var who = Find(g, 6701);
        cmd.Parent.Should().BeSameAs(explorer);
        who.Parent.Should().BeSameAs(cmd);
        cmd.Name.Should().Be("cmd.exe");
        cmd.Path.Should().Be(@"C:\Windows\System32\cmd.exe");
        cmd.CmdLine.Should().Be(@"C:\Windows\System32\cmd.exe /c whoami");
        who.Path.Should().Be(@"C:\Windows\System32\whoami.exe");
        cmd.User.Should().Be("alice");
        cmd.Markers.Should().HaveFlag(ProcessMarkers.FromLogsOnly);
        explorer.Markers.Should().NotHaveFlag(ProcessMarkers.Orphan);   // its parent (userinit) predates the log
        g.Roots.Should().ContainSingle().Which.Should().BeSameAs(explorer);
    }

    [Fact]
    public void FindFiles_EventLogsOnlyNeedTheFileToExist()
    {
        ProcessGraphBuilder.FindFiles(_host).CanBuildTree.Should().BeFalse();
        Write("EventLogs", "EventLogs.csv", EvtxHeader);   // header only: not scanned, no rows needed
        ProcessGraphBuilder.FindFiles(_host).CanBuildTree.Should().BeTrue();
        ProcessGraphBuilder.Build(_host).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Sysmon1_LinksByParentGuid_EvenWhenThePidWasReused()
    {
        const string gA = "{11111111-1111-1111-1111-111111111111}", gB = "{22222222-2222-2222-2222-222222222222}", gC = "{33333333-3333-3333-3333-333333333333}";
        Write("EventLogs", "EventLogs.csv", EvtxHeader +
            Sysmon1("2024-03-01 09:00:00", @"PC1\bob", @"C:\tools\a.exe", "100", gA, "4", "{00000000-0000-0000-0000-000000000000}", @"C:\Windows\System32\smss.exe", "SHA1=AABBCC,MD5=DD,SHA256=0123456789ABCDEF,IMPHASH=00") +
            Sysmon1("2024-03-01 09:30:00", @"PC1\bob", @"C:\tools\b.exe", "100", gB, "4", "{00000000-0000-0000-0000-000000000000}", @"C:\Windows\System32\smss.exe", "MD5=DD") +
            Sysmon1("2024-03-01 09:40:00", @"PC1\bob", @"""C:\tools\child.exe"" -x", "200", gC, "100", gA, @"C:\tools\a.exe", "SHA1=EEFF"));

        var g = ProcessGraphBuilder.Build(_host);
        g.SourceCounts[ProcessSources.Sysmon1].Should().Be(3);
        var a = g.AllNodes.Single(n => n.Guid == gA);
        var b = g.AllNodes.Single(n => n.Guid == gB);
        var child = Find(g, 200);
        a.Pid.Should().Be(100);
        b.Pid.Should().Be(100);
        child.Parent.Should().BeSameAs(a);   // the PID alone would have picked B (latest earlier process with PID 100)
        a.Children.Should().Contain(child);
        child.Hash.Should().Be("SHA1 eeff");
        child.HashSource.Should().Be("Sysmon 1");
        a.Hash.Should().Be("SHA256 0123456789abcdef");   // SHA256 preferred
        b.Hash.Should().Be("MD5 dd");
        child.User.Should().Be(@"PC1\bob");
        child.Path.Should().Be(@"C:\tools\child.exe");
        child.Sources.Should().HaveFlag(ProcessSources.Sysmon1);
    }

    [Fact]
    public void MemoryAndEvtx_MergeByPidAndCreateTime_AndUnmatchedBecomeLogOnlyNodes()
    {
        Write("Memory", "Memory_PsList.csv",
            "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n" +
            "0,4,0,System,0x1,1,1,,False,2024-03-01T08:00:00+00:00,,Disabled\r\n" +
            "0,6700,4,cmd.exe,0x2,1,1,1,False,2024-03-01T10:05:01+00:00,,Disabled\r\n");
        Write("EventLogs", "EventLogs.csv", EvtxHeader +
            Ev4688("2024-03-01 10:05:02.5000000", "alice", @"C:\Windows\System32\cmd.exe /c dir", @"C:\Windows\explorer.exe", "0x1a2c", "0x4") +     // 1.5 s: same process
            Ev4688("2024-03-01 11:00:00.0000000", "alice", @"C:\Windows\System32\cmd.exe /c later", @"C:\Windows\explorer.exe", "0x1a2c", "0x4"));    // PID reused by another process

        var g = ProcessGraphBuilder.Build(_host);
        g.AllNodes.Where(n => n.Pid == 0x1a2c).Should().HaveCount(2);
        var mem = Find(g, 0x1a2c, 0);
        mem.Sources.Should().HaveFlag(ProcessSources.PsList).And.HaveFlag(ProcessSources.Evtx4688);
        mem.Markers.Should().NotHaveFlag(ProcessMarkers.FromLogsOnly);
        mem.CmdLine.Should().Be(@"C:\Windows\System32\cmd.exe /c dir");
        mem.User.Should().Be("alice");
        mem.Path.Should().Be(@"C:\Windows\System32\cmd.exe");
        mem.Parent!.Pid.Should().Be(4);
        var later = Find(g, 0x1a2c, 1);
        later.Markers.Should().HaveFlag(ProcessMarkers.FromLogsOnly);
        later.CmdLine.Should().EndWith("later");
    }

    [Fact]
    public void EventsFacets_FollowTheProcessLifetimeAndGuid()
    {
        const string gA = "{11111111-1111-1111-1111-111111111111}", gB = "{22222222-2222-2222-2222-222222222222}";
        string Net(string time, string guid, string pid, string ip, string port) =>
            $"{time},3,{Sys},PC1\\bob,{Q(@"C:\tools\a.exe")},{Q($"ProcessGuid: {guid}, ProcessId: {pid}")},,,,{Q("DestinationHostname: evil.example")},{Q("DestinationIp: " + ip)},Network connection\r\n";
        string Reg(string time, string guid, string pid) =>
            $"{time},13,{Sys},PC1\\bob,,{Q($"ProcessGuid: {guid}, ProcessId: {pid}")},,{Q(@"Image: C:\tools\a.exe")},{Q("EventType: SetValue")},{Q(@"HKU\x\Software\Microsoft\Windows\CurrentVersion\Run\evil")},{Q(@"Details: C:\tools\a.exe")},Registry value set\r\n";
        Write("EventLogs", "EventLogs.csv", EvtxHeader +
            Sysmon1("2024-03-01 09:00:00", @"PC1\bob", @"C:\tools\a.exe", "100", gA, "4", gB, @"C:\x\p.exe", "MD5=DD") +
            Net("2024-03-01 09:01:00", gA, "100", "203.0.113.5", "443") +
            Net("2024-03-01 09:02:00", gB, "100", "198.51.100.9", "80") +   // another process with the same PID
            Reg("2024-03-01 09:03:00", gA, "100") +
            "2024-03-01 09:10:00,4689,Security,Microsoft-Windows-Security-Auditing,PC1,bob,,\"Pid: 0x64\",,,,,,Process exit\r\n");

        var g = ProcessGraphBuilder.Build(_host);
        var a = g.AllNodes.Single(n => n.Guid == gA);
        a.ExitTime.Should().Be(new DateTime(2024, 3, 1, 9, 10, 0, DateTimeKind.Utc));
        var ev = g.Evidence;
        ev.GetStatus(ProcessFacetKind.EvtNetwork, a).Should().Be(FacetStatus.Available);
        var net = ev.Get(ProcessFacetKind.EvtNetwork, a);
        net.TotalRows.Should().Be(1);
        net.Rows[0].Should().Contain("203.0.113.5").And.Contain("evil.example");
        var reg = ev.Get(ProcessFacetKind.EvtRegistry, a);
        reg.TotalRows.Should().Be(1);
        reg.Rows[0].Should().Contain(@"HKU\x\Software\Microsoft\Windows\CurrentVersion\Run\evil");
        ev.Get(ProcessFacetKind.EvtFiles, a).TotalRows.Should().Be(0);
        ev.GetStatus(ProcessFacetKind.EvtFiles, a).Should().Be(FacetStatus.NoRows);
        ev.Get(ProcessFacetKind.EvtAll, a).TotalRows.Should().BeGreaterThanOrEqualTo(4);   // create, network, registry, exit
        ev.Get(ProcessFacetKind.EvtProcess, a).Rows.Should().Contain(r => r[2] == "Process exited");
    }

    [Fact]
    public void NoEventLog_FacetsReportMissingSource()
    {
        Write("Memory", "Memory_PsList.csv",
            "PID,PPID,ImageFileName,CreateTime\r\n4,0,System,2024-03-01T08:00:00+00:00\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        g.Evidence.GetStatus(ProcessFacetKind.EvtAll, g.AllNodes[0]).Should().Be(FacetStatus.PluginNotRun);
        g.Evidence.GetStatus(ProcessFacetKind.DiskPrefetch, g.AllNodes[0]).Should().Be(FacetStatus.PluginNotRun);
    }

    [Fact]
    public void Prefetch_LinksByFullPathAndRunTime_AndAmcacheShimcacheGiveNoHashOrFacet()
    {
        Write("Memory", "Memory_PsList.csv",
            "PID,PPID,ImageFileName,CreateTime\r\n" +
            "4,0,System,2024-03-01T08:00:00+00:00\r\n" +
            "500,4,cmd.exe,2024-03-01T08:01:00+00:00\r\n" +
            "600,4,tool.exe,2024-03-01T08:02:00+00:00\r\n");
        Write("Memory", "Memory_CmdLine.csv",
            "PID,Process,Args\r\n" +
            "500,cmd.exe,\"C:\\Windows\\System32\\cmd.exe /c x\"\r\n" +
            "600,tool.exe,\"tool.exe\"\r\n");
        Write("ProgramExecution", "Amcache_UnassociatedFileEntries.csv",
            "ApplicationName,ProgramId,FileKeyLastWriteTimestamp,SHA1,IsOsComponent,FullPath,Name,FileExtension,ProductName,Size,Version\r\n" +
            "x,p,2023-01-01 00:00:00,DA39A3EE5E6B4B0D3255BFEF95601890AFD80709,False,c:\\windows\\system32\\cmd.exe,cmd.exe,.exe,Windows,100,10.0\r\n");
        Write("ProgramExecution", "ShimCache.csv",
            "ControlSet,CacheEntryPosition,Path,LastModifiedTimeUTC,Executed\r\n" +
            "1,0,C:\\Windows\\System32\\cmd.exe,2022-05-01 01:02:03,Yes\r\n");
        Write("ProgramExecution", "Prefetch.csv",
            "SourceFilename,ExecutableName,RunCount,LastRun,PreviousRun0,FilesLoaded\r\n" +
            "CMD.EXE-AAAA.pf,CMD.EXE,12,2024-03-01 08:01:10,2024-02-28 07:00:00,\"\\VOLUME{abc}\\WINDOWS\\SYSTEM32\\CMD.EXE, \\VOLUME{abc}\\WINDOWS\\SYSTEM32\\NTDLL.DLL\"\r\n" +
            "TOOL.EXE-BBBB.pf,TOOL.EXE,1,2024-03-01 08:02:05,,\"\\VOLUME{abc}\\WINDOWS\\SYSTEM32\\NTDLL.DLL\"\r\n");   // no FilesLoaded entry for the program: no path

        var g = ProcessGraphBuilder.Build(_host);
        var cmd = Find(g, 500);
        var tool = Find(g, 600);
        cmd.Hash.Should().BeEmpty();        // Amcache is no process evidence
        cmd.HashSource.Should().BeEmpty();
        g.SourceFiles.Should().NotContain(f => f.Contains("Amcache") || f.Contains("ShimCache"));

        var ev = g.Evidence;
        ev.Disk.HasPrefetch.Should().BeTrue();
        var pf = ev.Get(ProcessFacetKind.DiskPrefetch, cmd);
        pf.TotalRows.Should().Be(1);   // LastRun 8:01:10 is 10 s after the start; PreviousRun0 is a day before
        pf.Rows[0][0].Should().Be("2024-03-01 08:01:10");
        pf.Rows[0].Should().Contain("12");
        ev.GetStatus(ProcessFacetKind.DiskPrefetch, tool).Should().Be(FacetStatus.NoRows);   // a name match is not a link
    }

    [Fact]
    public void Prefetch_RunInsideAndOutsideTheStartTolerance_AndAmbiguousInstances()
    {
        Write("Memory", "Memory_PsList.csv",
            "PID,PPID,ImageFileName,CreateTime\r\n" +
            "4,0,System,2024-03-01T08:00:00+00:00\r\n" +
            "500,4,a.exe,2024-03-01T08:01:00+00:00\r\n" +
            "501,4,b.exe,2024-03-01T08:02:00+00:00\r\n" +
            "502,4,b.exe,2024-03-01T08:02:05+00:00\r\n" +
            "503,4,c.exe,2024-03-01T08:03:00+00:00\r\n");
        Write("Memory", "Memory_CmdLine.csv",
            "PID,Process,Args\r\n500,a.exe,\"C:\\x\\a.exe\"\r\n501,b.exe,\"C:\\x\\b.exe\"\r\n502,b.exe,\"C:\\x\\b.exe\"\r\n503,c.exe,\"C:\\x\\c.exe\"\r\n");
        Write("ProgramExecution", "Prefetch.csv",
            "SourceFilename,ExecutableName,RunCount,LastRun,PreviousRun0,FilesLoaded\r\n" +
            "A.EXE-1.pf,A.EXE,2,2024-03-01 08:01:16,,\"\\VOLUME{v}\\X\\A.EXE\"\r\n" +        // 16 s after the start: outside [-2, 15]
            "B.EXE-1.pf,B.EXE,1,2024-03-01 08:02:07,,\"\\VOLUME{v}\\X\\B.EXE\"\r\n" +        // fits both b.exe instances: ambiguous
            "C.EXE-1.pf,C.EXE,1,2024-03-01 08:03:15,,\"\\VOLUME{v}\\X\\C.EXE\"\r\n");        // 15 s: on the edge, inside

        var g = ProcessGraphBuilder.Build(_host);
        g.Evidence.Get(ProcessFacetKind.DiskPrefetch, Find(g, 500)).TotalRows.Should().Be(0);
        g.Evidence.Get(ProcessFacetKind.DiskPrefetch, Find(g, 501)).TotalRows.Should().Be(0);
        g.Evidence.Get(ProcessFacetKind.DiskPrefetch, Find(g, 502)).TotalRows.Should().Be(0);
        g.Evidence.Get(ProcessFacetKind.DiskPrefetch, Find(g, 503)).TotalRows.Should().Be(1);
    }

    [Fact]
    public void LogText_ParsesPidsLabelsAndPaths()
    {
        ProcessLogText.ParsePid("0x1a2c").Should().Be(6700);
        ProcessLogText.ParsePid("PID: 0x1a2c").Should().Be(6700);
        ProcessLogText.ParsePid("Process Id: 6700").Should().Be(6700);
        ProcessLogText.ParsePid("n/a").Should().BeNull();
        ProcessLogText.StripLabel(@"C:\Windows\x.exe").Should().Be(@"C:\Windows\x.exe");
        ProcessLogText.StripLabel(@"Parent Process: C:\Windows\x.exe").Should().Be(@"C:\Windows\x.exe");
        ProcessLogText.PathKey(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe").Should().Be(ProcessLogText.PathKey(@"C:\WINDOWS\system32\CMD.EXE"));
        ProcessLogText.PathKey("cmd.exe").Should().BeEmpty();
        var kv = ProcessLogText.KeyValues(@"ProcessGuid: {ABC}, ProcessId: 12, Image: C:\a b\c.exe");
        kv["processid"].Should().Be("12");
        kv["image"].Should().Be(@"C:\a b\c.exe");
        ProcessLogText.ExePath(@"""C:\Program Files\x\y.exe"" /a").Should().Be(@"C:\Program Files\x\y.exe");
        ProcessLogText.ExePath(@"C:\Program Files\x\y.exe /a").Should().Be(@"C:\Program Files\x\y.exe");
    }
}
