using System.Text;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ProcessGraphBuilderTests : IDisposable
{
    private readonly string _host = Path.Combine(Path.GetTempPath(), "ezforensics_proc_" + Guid.NewGuid().ToString("N"));
    private string Memory => Path.Combine(_host, "Memory");

    public ProcessGraphBuilderTests() => Directory.CreateDirectory(Memory);
    public void Dispose() { try { Directory.Delete(_host, true); } catch { } }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(Memory, name), content, new UTF8Encoding(false));

    private const string ListHeader = "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n";

    private static ProcessNode Find(ProcessGraph g, int pid, int index = 0) => g.AllNodes.Where(n => n.Pid == pid).OrderBy(n => n.CreateTime).ElementAt(index);

    [Fact]
    public void PpidCycles_and_very_deep_chains_neither_loop_nor_overflow()
    {
        var sb = new StringBuilder(ListHeader);
        // 2 <-> 3 and 5 (own parent) are loops; 10..4009 is one 4000-deep chain under pid 4
        sb.Append("0,2,3,a.exe,0x1,1,1,1,False,2024-01-01T00:00:01+00:00,,D\r\n");
        sb.Append("0,3,2,b.exe,0x2,1,1,1,False,2024-01-01T00:00:02+00:00,,D\r\n");
        sb.Append("0,5,5,c.exe,0x3,1,1,1,False,2024-01-01T00:00:03+00:00,,D\r\n");
        var t0 = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 4000; i++)
            sb.Append($"0,{10 + i},{(i == 0 ? 4 : 9 + i)},d.exe,0x{i + 100:x},1,1,1,False,{t0.AddSeconds(i):yyyy-MM-ddTHH:mm:ss}+00:00,,D\r\n");
        Write("Memory_PsList.csv", sb.ToString());
        var g = ProcessGraphBuilder.Build(_host);
        g.AllNodes.Should().HaveCount(4003);
        ProcessCorrelator.Ordered(g).Should().HaveCount(g.AllNodes.Count + g.Ghosts.Count);
        var layout = ProcessTreeLayout.Compute(g.Roots, n => n.Children, 100, 20, 10, 5);
        layout.Positions.Count.Should().BeGreaterThanOrEqualTo(4003);
        FocusProjection.Subtree(g.Roots[0], n => n.Children).Should().NotBeNull();
    }

    [Fact]
    public void BuildsTreeFromPsList_AndSortsChildren()
    {
        Write("Memory_PsList.csv", ListHeader +
            "0,4,0,System,0xa1,100,500,,False,2024-01-01T00:00:00+00:00,,Disabled\r\n" +
            "1,400,4,smss.exe,0xa2,2,30,,False,2024-01-01T00:00:01+00:00,,Disabled\r\n" +
            "1,900,400,late.exe,0xa3,2,30,1,True,2024-01-01T00:05:00+00:00,,Disabled\r\n" +
            "1,800,400,early.exe,0xa4,2,30,1,False,2024-01-01T00:02:00+00:00,,Disabled\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        g.AllNodes.Should().HaveCount(4);
        g.Roots.Should().ContainSingle().Which.Pid.Should().Be(4);
        g.Roots[0].Markers.Should().Be(ProcessMarkers.None);   // System is a natural root
        var smss = Find(g, 400);
        smss.Parent!.Pid.Should().Be(4);
        smss.Children.Select(c => c.Pid).Should().Equal(800, 900);
        Find(g, 900).Wow64.Should().BeTrue();
        Find(g, 900).SessionId.Should().Be(1);
        Find(g, 900).CreateTime!.Value.Kind.Should().Be(DateTimeKind.Utc);
        g.SourceCounts[ProcessSources.PsList].Should().Be(4);
        g.SourceFiles.Should().ContainSingle();
    }

    [Fact]
    public void SamePidWithDifferentCreateTime_IsAnotherProcess_AndParentUsesLatestEarlier()
    {
        Write("Memory_PsList.csv", ListHeader +
            "0,500,4,old.exe,0x1,1,1,1,False,2024-01-01T01:00:00+00:00,2024-01-01T02:00:00+00:00,Disabled\r\n" +
            "0,500,4,new.exe,0x2,1,1,1,False,2024-01-01T05:00:00+00:00,,Disabled\r\n" +
            "0,600,500,child.exe,0x3,1,1,1,False,2024-01-01T06:00:00+00:00,,Disabled\r\n" +
            "0,610,500,early.exe,0x4,1,1,1,False,2024-01-01T01:30:00+00:00,,Disabled\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        g.AllNodes.Count(n => n.Pid == 500).Should().Be(2);
        var oldP = Find(g, 500, 0);
        var newP = Find(g, 500, 1);
        oldP.Key.Should().NotBe(newP.Key);
        Find(g, 600).Parent.Should().BeSameAs(newP);
        Find(g, 610).Parent.Should().BeSameAs(oldP);
        oldP.Markers.Should().HaveFlag(ProcessMarkers.Exited);
        newP.Markers.Should().NotHaveFlag(ProcessMarkers.Exited);
    }

    [Fact]
    public void ParentPidReused_WhenOnlyALaterParentExists()
    {
        Write("Memory_PsList.csv", ListHeader +
            "0,700,4,parent.exe,0x1,1,1,1,False,2024-01-01T09:00:00+00:00,,Disabled\r\n" +
            "0,710,700,child.exe,0x2,1,1,1,False,2024-01-01T03:00:00+00:00,,Disabled\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        var child = Find(g, 710);
        child.Parent!.IsGhost.Should().BeTrue("the real parent was not captured: the reused PID belongs to a later process");
        child.Parent.Pid.Should().Be(700);
        child.Parent.Key.Should().Be("ghost:700").And.NotBe(Find(g, 700).Key);
        child.Markers.Should().HaveFlag(ProcessMarkers.ParentPidReused).And.NotHaveFlag(ProcessMarkers.Orphan);
        g.Roots.Should().Contain(child.Parent).And.NotContain(child);
        g.AllNodes.Should().NotContain(child.Parent);
    }

    [Fact]
    public void Orphan_WhenParentMissing_ButNotSystemOrIdle()
    {
        Write("Memory_PsList.csv", ListHeader +
            "0,0,0,Idle,0x1,1,0,,False,,,Disabled\r\n" +
            "0,4,0,System,0x2,1,0,,False,2024-01-01T00:00:00+00:00,,Disabled\r\n" +
            "0,320,9999,wininit.exe,0x3,1,1,0,False,2024-01-01T00:01:00+00:00,,Disabled\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        Find(g, 320).Markers.Should().HaveFlag(ProcessMarkers.Orphan);
        Find(g, 0).Markers.Should().Be(ProcessMarkers.None);
        Find(g, 4).Markers.Should().Be(ProcessMarkers.None);
        Find(g, 4).Parent.Should().BeSameAs(Find(g, 0));   // System's PPID 0 is Idle
        g.Roots.Should().HaveCount(2);
    }

    [Fact]
    public void PsScanOnly_IsHidden_AndMergesWithPsList()
    {
        Write("Memory_PsList.csv", ListHeader +
            "0,4,0,System,0x1,1,0,,False,2024-01-01T00:00:00+00:00,,Disabled\r\n" +
            "0,800,4,seen.exe,0x2,1,0,1,False,2024-01-01T00:10:00+00:00,,Disabled\r\n");
        Write("Memory_PsScan.csv", "PID,PPID,ImageFileName,Offset(P),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n" +
            "4,0,System,0x10,1,0,,False,2024-01-01T00:00:00+00:00,,Disabled\r\n" +
            "800,4,seen.exe,0x20,1,0,1,False,2024-01-01T00:10:00+00:00,,Disabled\r\n" +
            "850,4,rootkit.exe,0x30,1,0,1,False,2024-01-01T00:20:00+00:00,,Disabled\r\n" +
            "860,4,done.exe,0x40,0,0,1,False,2024-01-01T00:21:00+00:00,2024-01-01T00:22:00+00:00,Disabled\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        g.AllNodes.Should().HaveCount(4);
        Find(g, 800).Sources.Should().Be(ProcessSources.PsList | ProcessSources.PsScan);
        Find(g, 800).Offset.Should().Be("0x2");   // first source keeps its offset
        Find(g, 800).Markers.Should().Be(ProcessMarkers.None);
        Find(g, 850).Markers.Should().Be(ProcessMarkers.Hidden);
        Find(g, 860).Markers.Should().Be(ProcessMarkers.Exited);   // exited and unlinked is expected, not hidden
        g.SourceCounts[ProcessSources.PsScan].Should().Be(4);
        Find(g, 850).Parent!.Pid.Should().Be(4);
    }

    [Fact]
    public void PsScanWithoutPsList_DoesNotFlagHidden()
    {
        Write("Memory_PsScan.csv", "PID,PPID,ImageFileName,Offset(V),CreateTime,ExitTime\r\n4,0,System,0x1,2024-01-01T00:00:00+00:00,\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        Find(g, 4).Markers.Should().Be(ProcessMarkers.None);
    }

    [Fact]
    public void PsTreeFillsPathAndCmd_CmdLineEnriches_MalfindMarksInjected()
    {
        Write("Memory_PsList.csv", ListHeader +
            "0,4,0,System,0x1,1,0,,False,2024-01-01T00:00:00+00:00,,Disabled\r\n" +
            "0,1200,4,notepad.exe,0x2,1,0,1,False,2024-01-01T00:10:00+00:00,,Disabled\r\n" +
            "0,1300,4,calc.exe,0x3,1,0,1,False,2024-01-01T00:11:00+00:00,,Disabled\r\n");
        Write("Memory_PsTree.csv", "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,Audit,Cmd,Path\r\n" +
            "0,1200,4,notepad.exe,0x2,1,0,1,False,2024-01-01T00:10:00+00:00,,\\Device\\x\\notepad.exe,\"notepad.exe \"\"a b.txt\"\"\",C:\\Windows\\notepad.exe\r\n");
        Write("Memory_CmdLine.csv", "PID,Process,Args\r\n1300,calc.exe,calc.exe /x\r\n9999,ghost.exe,ghost\r\n");
        Write("Memory_Malfind.csv", "PID,Process,Start VPN,End VPN,Tag,Protection\r\n1200,notepad.exe,0x1,0x2,VadS,PAGE_EXECUTE_READWRITE\r\n1200,notepad.exe,0x3,0x4,VadS,PAGE_EXECUTE_READWRITE\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        var n = Find(g, 1200);
        n.Path.Should().Be(@"C:\Windows\notepad.exe");
        n.CmdLine.Should().Be("notepad.exe \"a b.txt\"");
        n.Sources.Should().Be(ProcessSources.PsList | ProcessSources.PsTree);
        n.Markers.Should().Be(ProcessMarkers.Injected);
        Find(g, 1300).CmdLine.Should().Be("calc.exe /x");
        Find(g, 1300).Markers.Should().Be(ProcessMarkers.None);
        g.AllNodes.Should().HaveCount(3);   // cmdline rows never add processes
        g.SourceFiles.Should().HaveCount(4);
    }

    [Fact]
    public void SuspiciousParent_RulesFire_CaseInsensitive()
    {
        Write("Memory_PsList.csv", ListHeader +
            "0,4,0,System,0x1,1,0,,False,2024-01-01T00:00:00+00:00,,Disabled\r\n" +
            "0,10,4,explorer.exe,0x2,1,0,1,False,2024-01-01T00:01:00+00:00,,Disabled\r\n" +
            "0,11,10,WINWORD.EXE,0x3,1,0,1,False,2024-01-01T00:02:00+00:00,,Disabled\r\n" +
            "0,12,11,Cmd.exe,0x4,1,0,1,False,2024-01-01T00:03:00+00:00,,Disabled\r\n" +
            "0,20,10,services.exe,0x5,1,0,0,False,2024-01-01T00:04:00+00:00,,Disabled\r\n" +
            "0,21,20,svchost.exe,0x6,1,0,0,False,2024-01-01T00:05:00+00:00,,Disabled\r\n" +
            "0,22,10,svchost.exe,0x7,1,0,0,False,2024-01-01T00:06:00+00:00,,Disabled\r\n" +
            "0,30,4,wininit.exe,0x8,1,0,0,False,2024-01-01T00:07:00+00:00,,Disabled\r\n" +
            "0,31,30,lsass.exe,0x9,1,0,0,False,2024-01-01T00:08:00+00:00,,Disabled\r\n" +
            "0,32,31,whoami.exe,0xa,1,0,0,False,2024-01-01T00:09:00+00:00,,Disabled\r\n" +
            "0,33,30,services.exe,0xb,1,0,0,False,2024-01-01T00:10:00+00:00,,Disabled\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        bool Sus(int pid) => Find(g, pid).Markers.HasFlag(ProcessMarkers.SuspiciousParent);
        Sus(12).Should().BeTrue();    // office -> shell
        Sus(20).Should().BeTrue();    // services.exe under explorer
        Sus(21).Should().BeFalse();   // svchost under services
        Sus(22).Should().BeTrue();    // svchost under explorer
        Sus(31).Should().BeFalse();   // lsass under wininit
        Sus(32).Should().BeTrue();    // child of lsass
        Sus(33).Should().BeFalse();   // services under wininit
        Sus(11).Should().BeFalse();
    }

    [Fact]
    public void ColumnAliases_AndOrder_AreHandled_LinuxVariant()
    {
        Write("Memory_LinuxPsList.csv", "OFFSET (V),PID,TID,PPID,COMM\r\n" +
            "0x1,1,1,0,systemd\r\n0x2,50,50,1,sshd\r\n0x3,50,51,1,sshd\r\n0x4,60,60,50,bash\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        g.AllNodes.Should().HaveCount(3);   // thread row skipped
        Find(g, 60).Parent!.Name.Should().Be("sshd");
        Find(g, 1).Markers.Should().Be(ProcessMarkers.None);   // parent PID 0 is a natural root
        Find(g, 1).Offset.Should().Be("0x1");
        g.Roots.Should().ContainSingle();
    }

    [Fact]
    public void ReorderedLowerCaseHeaders_WorkInExplicitFileOverload()
    {
        var path = Path.Combine(Memory, "custom.csv");
        File.WriteAllText(path, "createtime,comm,ppid,pid\r\n2024-01-01 00:00:00,init,0,1\r\n2024-01-01 00:00:05,sh,1,2\r\n");
        var files = new ProcessGraphFiles();
        files.PsList.Add(path);
        var g = ProcessGraphBuilder.Build(files);
        Find(g, 2).Parent!.Pid.Should().Be(1);
        Find(g, 2).CreateTime.Should().Be(new DateTime(2024, 1, 1, 0, 0, 5, DateTimeKind.Utc));
    }

    [Fact]
    public void EmptyAndMissingFiles_GiveEmptyGraph_WithoutThrowing()
    {
        Write("Memory_PsList.csv", "");
        Write("Memory_PsScan.csv", ListHeader);
        Write("Memory_PsTree.csv", "Foo,Bar\r\n1,2\r\n");
        var g = ProcessGraphBuilder.Build(_host);
        g.IsEmpty.Should().BeTrue();
        g.Roots.Should().BeEmpty();
        g.Warnings.Should().Contain(w => w.Contains("no PID column"));

        ProcessGraphBuilder.Build(Path.Combine(_host, "nope")).IsEmpty.Should().BeTrue();
        var missing = new ProcessGraphFiles();
        missing.PsList.Add(Path.Combine(_host, "missing.csv"));
        var g2 = ProcessGraphBuilder.Build(missing);
        g2.IsEmpty.Should().BeTrue();
        g2.Warnings.Should().ContainSingle(w => w.Contains("Missing"));
    }

    [Fact]
    public void QualifiedFileNames_AreFound_AndSimilarVariantsIgnored()
    {
        Write("Memory_PsList_image2.csv", ListHeader + "0,4,0,System,0x1,1,0,,False,2024-01-01T00:00:00+00:00,,Disabled\r\n");
        Write("Memory_PsListX.csv", ListHeader + "0,5,0,Other,0x1,1,0,,False,2024-01-01T00:00:00+00:00,,Disabled\r\n");
        var files = ProcessGraphBuilder.FindFiles(_host);
        files.PsList.Should().ContainSingle().Which.Should().EndWith("Memory_PsList_image2.csv");
    }
}
