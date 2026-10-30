using System.Text;
using EzForensics.Core.Memory;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Volatility 3 (2.28.2) plugins added for the process tree: handles types, cmdscan / consoles, verinfo, malware.* markers, getservicesids. Column layouts are the plugins' TreeGrid columns.</summary>
public class ProcessMemoryPluginTests : IDisposable
{
    private readonly string _host = Path.Combine(Path.GetTempPath(), "ezforensics_memplug_" + Guid.NewGuid().ToString("N"));
    private string Memory => Path.Combine(_host, "Memory");

    public ProcessMemoryPluginTests() => Directory.CreateDirectory(Memory);
    public void Dispose() { try { Directory.Delete(_host, true); } catch { } }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(Memory, name), content, new UTF8Encoding(false));

    private const string PsListHeader = "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n";

    private void WritePsList(params string[] rows) => Write("Memory_PsList.csv", PsListHeader + string.Concat(rows.Select(r => r + "\r\n")));

    private static string Ps(int pid, int ppid, string name, string create = "2024-05-01 10:00:00.0000000") =>
        $"0,{pid},{ppid},{name},0xffff{pid:x},5,100,1,False,{create},,Disabled";

    private async Task<ProcessFacetIndex> Facets() => await ProcessFacetIndex.BuildAsync(_host, CancellationToken.None);

    // ------------------------------------------------------------------ handles

    [Fact]
    public async Task Handles_split_into_mutant_process_thread_section_event_with_target_pid()
    {
        Write("Memory_Handles.csv", "TreeDepth,PID,Process,Offset,HandleValue,Type,GrantedAccess,Name\r\n" +
            "0,100,evil.exe,0xa1,0x4,Mutant,0x1f0001,Global\\EvilMutex\r\n" +
            "0,100,evil.exe,0xa2,0x8,Process,0x1fffff,lsass.exe Pid 640\r\n" +
            "0,100,evil.exe,0xa3,0xc,Thread,0x1fffff,Tid 7720 Pid 640\r\n" +
            "0,100,evil.exe,0xa4,0x10,Section,0xf001f,\r\n" +
            "0,100,evil.exe,0xa5,0x14,Event,0x1f0003,\\KernelObjects\\LowMemoryCondition\r\n" +
            "0,100,evil.exe,0xa6,0x18,Process,0x1000,evil.exe Pid 100\r\n" +
            "0,100,evil.exe,0xa7,0x1c,Token,0x8,\r\n" +
            "0,640,lsass.exe,0xb1,0x4,File,0x1,\\Device\\HarddiskVolume3\\Windows\\System32\r\n");
        using var idx = await Facets();

        idx.Get(ProcessFacetKind.HandlesMutant, 100).Rows.Should().ContainSingle().Which[7].Should().Be("Global\\EvilMutex");
        idx.Get(ProcessFacetKind.HandlesSection, 100).Rows.Should().ContainSingle();
        idx.Get(ProcessFacetKind.HandlesEvent, 100).Rows.Should().ContainSingle();
        idx.Get(ProcessFacetKind.HandlesFiles, 640).Rows.Should().ContainSingle();
        idx.GetStatus(ProcessFacetKind.HandlesMutant, 640).Should().Be(FacetStatus.NoRows);
        ProcessFacetIndex.PluginIdOf(ProcessFacetKind.HandlesProcess).Should().Be("handles");

        var proc = idx.Get(ProcessFacetKind.HandlesProcess, 100);
        proc.Headers[^1].Should().Be(ProcessFacetIndex.TargetPidColumn);
        proc.Rows.Select(r => r[^1]).Should().Equal("640", "100");
        idx.Get(ProcessFacetKind.HandlesThread, 100).Rows.Should().ContainSingle().Which[^1].Should().Be("640");

        // the other direction: who holds a handle to lsass; evil.exe's handle to itself is not listed for evil.exe
        var toLsass = idx.HandlesTo(640);
        toLsass.TotalRows.Should().Be(2);
        toLsass.Rows.Should().OnlyContain(r => r[1] == "100");
        idx.HandlesTo(100).Status.Should().Be(FacetStatus.NoRows);

        ProcessFacetIndex.HandleTargetPid("svchost.exe Pid 1234").Should().Be(1234);
        ProcessFacetIndex.HandleTargetPid("Tid 5 Pid 12").Should().Be(12);
        ProcessFacetIndex.HandleTargetPid("\\BaseNamedObjects\\Pid 12x").Should().BeNull();
        ProcessFacetIndex.HandleTargetPid("").Should().BeNull();
    }

    // ------------------------------------------------------------------ cmdscan / consoles

    [Fact]
    public async Task Consoles_rows_belong_to_conhost_and_to_every_attached_pid_cmdscan_to_conhost_only()
    {
        Write("Memory_Consoles.csv", "TreeDepth,PID,Process,ConsoleInfo,Property,Address,Data\r\n" +
            "0,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION,0x1d0f2e0,\r\n" +
            "1,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION.ProcessCount,0x1d0f2f8,2\r\n" +
            "1,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION.ConsoleProcessList,0x1d0f300,\r\n" +
            "2,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION.ConsoleProcessList.ConsoleProcess_0,0x1d11a40,\r\n" +
            "3,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION.ConsoleProcessList.ConsoleProcess_0_ProcessId,0x1d11a58,4400\r\n" +
            "3,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION.ConsoleProcessList.ConsoleProcess_1_ProcessId,0x1d11b58,5100\r\n" +
            "2,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION.HistoryList.CommandHistory_0_Command_0,0x1d2c000,whoami /all\r\n" +
            "0,6000,conhost.exe,0x2e00000,_CONSOLE_INFORMATION,0x2e00000,\r\n" +
            "3,6000,conhost.exe,0x2e00000,_CONSOLE_INFORMATION.ConsoleProcessList.ConsoleProcess_0_ProcessId,0x2e00058,7000\r\n");
        Write("Memory_CmdScan.csv", "TreeDepth,PID,Process,ConsoleInfo,Property,Address,Data\r\n" +
            "0,4420,conhost.exe,0x1d2b000,_COMMAND_HISTORY,0x1d2b000,\r\n" +
            "1,4420,conhost.exe,0x1d2b000,_COMMAND_HISTORY.Application,0x1d2b040,cmd.exe\r\n" +
            "1,4420,conhost.exe,0x1d2b000,_COMMAND_HISTORY.Command_0,0x1d2c000,whoami /all\r\n");
        using var idx = await Facets();

        idx.Get(ProcessFacetKind.ConsoleHistory, 4420).TotalRows.Should().Be(7 + 3, "all consoles rows of its console plus its cmdscan rows");
        var cmd = idx.Get(ProcessFacetKind.ConsoleHistory, 4400);
        cmd.TotalRows.Should().Be(7, "cmd.exe 4400 is attached to conhost 4420; cmdscan names no attached PID");
        cmd.Rows.Should().OnlyContain(r => r[1] == "4420");
        cmd.Rows.Should().Contain(r => r[6] == "whoami /all");
        idx.Get(ProcessFacetKind.ConsoleHistory, 5100).TotalRows.Should().Be(7);
        idx.Get(ProcessFacetKind.ConsoleHistory, 7000).TotalRows.Should().Be(2, "only the rows of its own console (conhost 6000)");
        idx.GetStatus(ProcessFacetKind.ConsoleHistory, 1).Should().Be(FacetStatus.NoRows);
        ProcessFacetIndex.PluginIdOf(ProcessFacetKind.ConsoleHistory).Should().Be("cmdscan");
    }

    [Fact]
    public void Normalize_keeps_multi_line_console_dumps_as_one_row()
    {
        var raw = "Volatility 3 Framework 2.28.2\n" +
                  "TreeDepth,PID,Process,ConsoleInfo,Property,Address,Data\n" +
                  "0,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION,0x1d0f2e0,\n" +
                  "2,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION.ScreenBuffer_0.Dump,0x1d30000,\"C:\\\\Users\\\\bob>whoami\n" +
                  "desktop\\\\bob\n\nC:\\\\Users\\\\bob>\"\n" +
                  "3,4420,conhost.exe,0x1d0f2e0,_CONSOLE_INFORMATION.ConsoleProcessList.ConsoleProcess_0_ProcessId,0x1d11a58,4400\n\n";
        var sw = new StringWriter();
        var res = VolatilityCsv.Normalize(new StringReader(raw), sw, MemoryPlugins.Find("consoles"));
        res.Rows.Should().Be(3);
        res.Headers.Should().Equal("TreeDepth", "PID", "Process", "ConsoleInfo", "Property", "Address", "Data");
        sw.ToString().Should().Contain("\"C:\\Users\\bob>whoami\ndesktop\\bob\n\nC:\\Users\\bob>\"");
    }

    // ------------------------------------------------------------------ verinfo, markers

    [Fact]
    public void VerInfo_fills_the_node_from_its_main_image_row_only()
    {
        WritePsList(Ps(4, 0, "System"), Ps(1200, 4, "notepad.exe"), Ps(1300, 4, "MicrosoftEdgeU"));   // EPROCESS ImageFileName is cut at 14 characters
        Write("Memory_VerInfo.csv", "TreeDepth,PID,Process,Base,Name,Major,Minor,Product,Build\r\n" +
            "0,N/A,N/A,0xfffff80000000000,ntoskrnl.exe,10,0,19041,4355\r\n" +
            "0,1200,notepad.exe,0x7ff6a0000000,notepad.exe,10,0,19041,3996\r\n" +
            "0,1200,notepad.exe,0x7ffd10000000,ntdll.dll,10,0,19041,4355\r\n" +
            "0,1300,MicrosoftEdgeU,0x400000,MicrosoftEdgeUpdate.exe,1,3,175,29\r\n" +
            "0,1300,MicrosoftEdgeU,0x7ffd10000000,ntdll.dll,10,0,19041,4355\r\n" +
            "0,4,System,0x7ffd10000000,ntdll.dll,10,0,19041,4355\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        var notepad = g.AllNodes.Single(n => n.Pid == 1200);
        notepad.FileVersion.Should().Be("10.0.19041.3996", "the notepad.exe row, not ntdll.dll");
        notepad.VersionInfoSource.Should().Be(ProcessNode.VersionInfoLabel).And.Contain("not a signature");
        notepad.Company.Should().BeEmpty("verinfo 2.x prints no company string");
        g.AllNodes.Single(n => n.Pid == 1300).FileVersion.Should().Be("1.3.175.29");
        g.AllNodes.Single(n => n.Pid == 4).FileVersion.Should().BeEmpty("System has no row for its own image");
    }

    [Fact]
    public void VerInfo_with_two_instances_of_the_pid_and_the_same_image_is_not_assigned()
    {
        Write("Memory_PsScan.csv", "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n" +
            "0,1200,4,notepad.exe,0x1,1,1,1,False,2024-05-01 09:00:00.0000000,2024-05-01 09:30:00.0000000,Disabled\r\n" +
            "0,1200,4,notepad.exe,0x2,1,1,1,False,2024-05-01 10:00:00.0000000,,Disabled\r\n");
        Write("Memory_VerInfo.csv", "TreeDepth,PID,Process,Base,Name,Major,Minor,Product,Build\r\n" +
            "0,1200,notepad.exe,0x7ff6a0000000,notepad.exe,10,0,19041,3996\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        g.AllNodes.Where(n => n.Pid == 1200).Should().HaveCount(2).And.OnlyContain(n => n.FileVersion == "");
    }

    [Fact]
    public void Malware_plugins_set_hollowed_ghosted_threads_and_peb_markers_on_memory_nodes()
    {
        WritePsList(Ps(4, 0, "System"), Ps(2000, 4, "svchost.exe"), Ps(2100, 4, "explorer.exe"), Ps(2200, 4, "lsass.exe"), Ps(2300, 4, "calc.exe"));
        Write("Memory_HollowProcesses.csv", "TreeDepth,PID,Process,Notes\r\n" +
            "0,2000,svchost.exe,Invalid VAD protection for the process executable: PAGE_EXECUTE_READWRITE\r\n" +
            "0,2000,svchost.exe,Process image is not mapped from a file\r\n");
        Write("Memory_ProcessGhosting.csv", "TreeDepth,PID,Process,Base,FILE_OBJECT,DeletePending,DeleteOnClose,Path\r\n" +
            "0,2100,explorer.exe,0x7ff600000000,0xc0012345,1,N/A,N/A\r\n");
        Write("Memory_SuspiciousThreads.csv", "TreeDepth,Process,PID,TID,Context,Address,VAD Path,Note\r\n" +
            "0,lsass.exe,2200,6012,Win32StartAddress,0x1f0000,N/A,This thread started execution in the VAD at the start address\r\n");
        Write("Memory_PebMasquerade.csv", "TreeDepth,PID,EPROCESS_ImageFileName,EPROCESS_SeAudit_ImageFileName,PEB_ImageFilePath,PEB_ImageFilePath_Spoofed,PEB_CommandLine_Spoofed\r\n" +
            "0,2200,lsass.exe,\\Device\\HarddiskVolume3\\Windows\\System32\\lsass.exe,C:\\Windows\\system32\\lsass.exe,False,False\r\n" +
            "0,2300,calc.exe,\\Device\\HarddiskVolume3\\Users\\bob\\x.exe,C:\\Windows\\System32\\calc.exe,False,True\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        ProcessMarkers M(int pid) => g.AllNodes.Single(n => n.Pid == pid).Markers;
        M(2000).Should().HaveFlag(ProcessMarkers.Hollowed).And.NotHaveFlag(ProcessMarkers.Ghosted);
        M(2100).Should().HaveFlag(ProcessMarkers.Ghosted).And.NotHaveFlag(ProcessMarkers.Hollowed);
        M(2200).Should().HaveFlag(ProcessMarkers.SuspiciousThreads).And.NotHaveFlag(ProcessMarkers.PebMasquerade, "both Spoofed flags are False");
        M(2300).Should().HaveFlag(ProcessMarkers.PebMasquerade);
        M(4).Should().Be(ProcessMarkers.None);
        g.SourceFiles.Should().Contain(f => f.EndsWith("Memory_HollowProcesses.csv"));
    }

    [Fact]
    public async Task Malware_and_other_new_plugins_are_facets_by_pid()
    {
        Write("Memory_HollowProcesses.csv", "TreeDepth,PID,Process,Notes\r\n0,2000,svchost.exe,Process image is not mapped from a file\r\n");
        Write("Memory_Threads.csv", "TreeDepth,Offset,PID,TID,StartAddress,StartPath,Win32StartAddress,Win32StartPath,CreateTime,ExitTime\r\n" +
            "0,0xa,2000,10,0x7ffd1,C:\\Windows\\System32\\ntdll.dll,0x7ff61,C:\\Windows\\System32\\svchost.exe,2024-05-01 10:00:01.0000000,\r\n");
        Write("Memory_JobLinks.csv", "TreeDepth,Offset(V),Name,PID,PPID,Sess,JobSess,Wow64,Total,Active,Term,JobLink,Process\r\n" +
            "0,0xb,svchost.exe,2000,4,0,0,False,1,1,0,-,svchost.exe\r\n");
        using var idx = await Facets();
        idx.Get(ProcessFacetKind.Hollowing, 2000).Rows.Should().ContainSingle();
        idx.Get(ProcessFacetKind.Threads, 2000).Rows.Should().ContainSingle();
        idx.Get(ProcessFacetKind.JobLinks, 2000).Rows.Should().ContainSingle();
        idx.GetStatus(ProcessFacetKind.Ghosting).Should().Be(FacetStatus.PluginNotRun);
        idx.Get(ProcessFacetKind.Ghosting, 2000).PluginId.Should().Be("processghosting");
    }

    [Fact]
    public async Task ServiceSids_join_getservicesids_to_the_services_svcscan_lists_for_the_pid()
    {
        Write("Memory_SvcScan.csv", "TreeDepth,Offset,Order,PID,Start,State,Type,Name,Display,Binary,Binary (Registry),Dll\r\n" +
            "0,0x1,10,2000,SERVICE_AUTO_START,SERVICE_RUNNING,SERVICE_WIN32_SHARE_PROCESS,Dnscache,DNS Client,C:\\Windows\\system32\\svchost.exe -k NetworkService,,\r\n" +
            "0,0x2,11,2000,SERVICE_AUTO_START,SERVICE_RUNNING,SERVICE_WIN32_SHARE_PROCESS,NlaSvc,Network Location Awareness,C:\\Windows\\system32\\svchost.exe -k NetworkService,,\r\n" +
            "0,0x3,12,3000,SERVICE_DEMAND_START,SERVICE_RUNNING,SERVICE_WIN32_OWN_PROCESS,Spooler,Print Spooler,C:\\Windows\\System32\\spoolsv.exe,,\r\n");
        Write("Memory_GetServiceSids.csv", "TreeDepth,SID,Service\r\n" +
            "0,S-1-5-80-859482183-879914841-863379149-1145462774-2388618682,Dnscache\r\n" +
            "0,S-1-5-80-3141615172-2057878085-1754447212-2405740020-3916490453,NlaSvc\r\n" +
            "0,S-1-5-80-1234,Spooler\r\n" +
            "0,S-1-5-80-9999,Unrelated\r\n");
        using var idx = await Facets();
        var sids = idx.Get(ProcessFacetKind.ServiceSids, 2000);
        sids.Rows.Select(r => r[2]).Should().BeEquivalentTo("Dnscache", "NlaSvc");
        idx.Get(ProcessFacetKind.ServiceSids, 3000).Rows.Should().ContainSingle().Which[2].Should().Be("Spooler");
        idx.GetStatus(ProcessFacetKind.ServiceSids, 4000).Should().Be(FacetStatus.NoRows);
        idx.GetStatus(ProcessFacetKind.ServiceSids).Should().Be(FacetStatus.Available);
        idx.FilesFor(ProcessFacetKind.ServiceSids, 2000).Should().BeEmpty("the CSV has no PID column to filter on");
    }

    [Fact]
    public async Task ServiceNames_lists_the_svcscan_services_of_a_pid_capped()
    {
        Write("Memory_SvcScan.csv", "TreeDepth,Offset,Order,PID,Start,State,Type,Name,Display,Binary,Binary (Registry),Dll\r\n" +
            "0,0x1,10,2000,SERVICE_AUTO_START,SERVICE_RUNNING,SERVICE_WIN32_SHARE_PROCESS,Dnscache,DNS Client,svchost.exe,,\r\n" +
            "0,0x2,11,2000,SERVICE_AUTO_START,SERVICE_RUNNING,SERVICE_WIN32_SHARE_PROCESS,NlaSvc,NLA,svchost.exe,,\r\n" +
            "0,0x4,13,2000,SERVICE_AUTO_START,SERVICE_RUNNING,SERVICE_WIN32_SHARE_PROCESS,nlasvc,NLA,svchost.exe,,\r\n" +
            "0,0x3,12,3000,SERVICE_DEMAND_START,SERVICE_RUNNING,SERVICE_WIN32_OWN_PROCESS,Spooler,Print Spooler,spoolsv.exe,,\r\n");
        using var idx = await Facets();
        idx.ServiceNames(2000).Should().Equal("Dnscache", "NlaSvc");
        idx.ServiceNames(2000, cap: 1).Should().Equal("Dnscache");
        idx.ServiceNames(4000).Should().BeEmpty();
        idx.PidsWithServices().Should().BeEquivalentTo(new[] { 2000, 3000 });
    }

    // ------------------------------------------------------------------ getsids / sessions

    private const string SidHeader = "TreeDepth,PID,Process,SID,Name\r\n";

    [Fact]
    public void GetSids_first_row_is_the_user_and_the_integrity_label_is_mapped()
    {
        WritePsList(Ps(4, 0, "System"), Ps(1200, 4, "notepad.exe"), Ps(1300, 4, "calc.exe"));
        Write("Memory_GetSids.csv", SidHeader +
            "0,1200,notepad.exe,S-1-5-21-1-2-3-1001,bob\r\n" +
            "0,1200,notepad.exe,S-1-5-32-544,Administrators\r\n" +
            "0,1200,notepad.exe,S-1-16-12288,High Mandatory Level\r\n" +
            "0,1300,calc.exe,S-1-5-21-1-2-3-1002,-\r\n" +
            "0,1300,calc.exe,S-1-16-8192,Medium Mandatory Level\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        var n = g.AllNodes.Single(x => x.Pid == 1200);
        n.User.Should().Be("bob");
        n.UserSource.Should().Be("getsids");
        n.Integrity.Should().Be("High");
        var c = g.AllNodes.Single(x => x.Pid == 1300);
        c.User.Should().Be("S-1-5-21-1-2-3-1002", "an empty name falls back to the SID");
        c.Integrity.Should().Be("Medium");
        g.AllNodes.Single(x => x.Pid == 4).User.Should().BeEmpty();
        g.SourceFiles.Should().Contain(p => p.EndsWith("Memory_GetSids.csv"));
    }

    [Fact]
    public void GetSids_unknown_label_uses_the_name_column()
    {
        WritePsList(Ps(1200, 4, "notepad.exe"));
        Write("Memory_GetSids.csv", SidHeader + "0,1200,notepad.exe,S-1-5-18,SYSTEM\r\n0,1200,notepad.exe,S-1-16-99999,Odd Level\r\n");
        ProcessGraphBuilder.Build(_host).AllNodes.Single().Integrity.Should().Be("Odd Level");
    }

    [Fact]
    public void GetSids_with_two_instances_of_the_pid_leaves_user_and_integrity_empty()
    {
        Write("Memory_PsScan.csv", "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n" +
            "0,1200,4,notepad.exe,0x1,1,1,1,False,2024-05-01 09:00:00.0000000,2024-05-01 09:30:00.0000000,Disabled\r\n" +
            "0,1200,4,notepad.exe,0x2,1,1,1,False,2024-05-01 10:00:00.0000000,,Disabled\r\n");
        Write("Memory_GetSids.csv", SidHeader + "0,1200,notepad.exe,S-1-5-21-1-2-3-1001,bob\r\n0,1200,notepad.exe,S-1-16-12288,High Mandatory Level\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        g.AllNodes.Where(n => n.Pid == 1200).Should().HaveCount(2).And.OnlyContain(n => n.User == "" && n.UserSource == "" && n.Integrity == "");
    }

    [Fact]
    public void Sessions_is_the_user_fallback_and_getsids_wins()
    {
        WritePsList(Ps(1200, 4, "notepad.exe"), Ps(1300, 4, "calc.exe"), Ps(1400, 4, "mixed.exe"));
        Write("Memory_Sessions.csv", "TreeDepth,Session ID,Session Type,Process ID,Process,User Name,Create Time\r\n" +
            "0,1,Console,1200,notepad.exe,PC1\\bob,2024-05-01 10:00:00.0000000\r\n" +
            "0,1,Console,1300,calc.exe,PC1\\carol,2024-05-01 10:00:00.0000000\r\n" +
            "0,1,Console,1400,mixed.exe,PC1\\a,2024-05-01 10:00:00.0000000\r\n" +
            "0,2,Console,1400,mixed.exe,PC1\\b,2024-05-01 10:00:00.0000000\r\n");
        Write("Memory_GetSids.csv", SidHeader + "0,1300,calc.exe,S-1-5-21-1-2-3-1002,carol-sid\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        var n = g.AllNodes.Single(x => x.Pid == 1200);
        (n.User, n.UserSource).Should().Be((@"PC1\bob", "sessions"));
        var c = g.AllNodes.Single(x => x.Pid == 1300);
        (c.User, c.UserSource).Should().Be(("carol-sid", "getsids"));
        g.AllNodes.Single(x => x.Pid == 1400).User.Should().BeEmpty("the sessions rows of the PID disagree");
    }

    [Fact]
    public void GetSids_is_selected_by_default()
    {
        MemoryPlugins.ForOs(MemoryOs.Windows).Single(p => p.Id == "getsids").DefaultSelected.Should().BeTrue();
    }
}
