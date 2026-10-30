using System.Text;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ProcessFacetIndexTests : IDisposable
{
    private readonly string _host = Path.Combine(Path.GetTempPath(), "ezforensics_facet_" + Guid.NewGuid().ToString("N"));
    private string Memory => Path.Combine(_host, "Memory");

    public ProcessFacetIndexTests() => Directory.CreateDirectory(Memory);
    public void Dispose() { try { Directory.Delete(_host, true); } catch { } }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(Memory, name), content, new UTF8Encoding(false));

    private async Task<ProcessFacetIndex> Build() => await ProcessFacetIndex.BuildAsync(_host, CancellationToken.None);

    [Fact]
    public async Task Handles_AreSplitIntoFilesAndRegistry()
    {
        Write("Memory_Handles.csv", "PID,Process,Offset,HandleValue,Type,GrantedAccess,Name\r\n" +
            "100,a.exe,0x1,0x4,File,0x1,\\Device\\One\r\n" +
            "100,a.exe,0x2,0x8,Key,0x1,MACHINE\\SOFTWARE\r\n" +
            "100,a.exe,0x3,0xc,Mutant,0x1,m\r\n" +
            "200,b.exe,0x4,0x4,File,0x1,\\Device\\Two\r\n" +
            "100,a.exe,0x5,0x10,File,0x1,\\Device\\Three\r\n");
        using var idx = await Build();
        var files = idx.Get(ProcessFacetKind.HandlesFiles, 100);
        files.Status.Should().Be(FacetStatus.Available);
        files.Rows.Select(r => r[6]).Should().Equal("\\Device\\One", "\\Device\\Three");
        files.Headers.Should().Contain("HandleValue");
        idx.Get(ProcessFacetKind.HandlesRegistry, 100).Rows.Should().ContainSingle().Which[6].Should().Be("MACHINE\\SOFTWARE");
        idx.GetStatus(ProcessFacetKind.HandlesRegistry, 200).Should().Be(FacetStatus.NoRows);
        idx.Get(ProcessFacetKind.HandlesFiles, 999).Status.Should().Be(FacetStatus.NoRows);
    }

    [Fact]
    public async Task DllList_IsGroupedPerPid()
    {
        Write("Memory_DllList.csv", "PID,Process,Base,Size,Name,Path\r\n" +
            "10,x.exe,0x1,1,ntdll.dll,C:\\ntdll.dll\r\n" +
            "20,y.exe,0x2,1,kernel32.dll,C:\\k32.dll\r\n" +
            "10,x.exe,0x3,1,user32.dll,C:\\u32.dll\r\n");
        using var idx = await Build();
        idx.Get(ProcessFacetKind.Dlls, 10).Rows.Select(r => r[4]).Should().Equal("ntdll.dll", "user32.dll");
        idx.Get(ProcessFacetKind.Dlls, 20).Rows.Should().ContainSingle();
        idx.SourceFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task NetScan_SkipsNotApplicablePid_AndMergesNetStat()
    {
        Write("Memory_NetScan.csv", "Offset,Proto,LocalAddr,LocalPort,ForeignAddr,ForeignPort,State,PID,Owner,Created\r\n" +
            "0x1,TCPv4,1.1.1.1,80,0.0.0.0,0,LISTENING,300,svc.exe,2024\r\n" +
            "0x2,UDPv4,*,53,*,0,,N/A,,\r\n");
        Write("Memory_NetStat.csv", "Offset,Proto,LocalAddr,LocalPort,ForeignAddr,ForeignPort,State,PID,Owner,Created\r\n" +
            "0x3,TCPv4,1.1.1.1,81,0.0.0.0,0,LISTENING,300,svc.exe,2024\r\n");
        using var idx = await Build();
        var net = idx.Get(ProcessFacetKind.Network, 300);
        net.Rows.Should().HaveCount(2);
        net.TotalRows.Should().Be(2);
        idx.Get(ProcessFacetKind.Network, 0).Status.Should().Be(FacetStatus.NoRows);
    }

    [Fact]
    public async Task MissingPlugin_ReportsPluginNotRun_WithPluginId()
    {
        Write("Memory_DllList.csv", "PID,Name\r\n1,a\r\n");
        using var idx = await Build();
        idx.GetStatus(ProcessFacetKind.HandlesFiles).Should().Be(FacetStatus.PluginNotRun);
        var r = idx.Get(ProcessFacetKind.HandlesFiles, 1);
        r.Status.Should().Be(FacetStatus.PluginNotRun);
        r.PluginId.Should().Be("handles");
        ProcessFacetIndex.PluginIdOf(ProcessFacetKind.Network).Should().Be("netscan");
        idx.GetStatus(ProcessFacetKind.Dlls).Should().Be(FacetStatus.Available);
    }

    [Fact]
    public async Task QualifierFiles_AreMerged()
    {
        Write("Memory_CmdLine.csv", "PID,Process,Args\r\n5,a.exe,one\r\n");
        Write("Memory_CmdLine_imageB.csv", "PID,Process,Args\r\n5,a.exe,two\r\n6,b.exe,three\r\n");
        using var idx = await Build();
        idx.SourceFiles.Should().HaveCount(2);
        idx.Get(ProcessFacetKind.CmdLine, 5).Rows.Select(r => r[2]).Should().Equal("one", "two");
        idx.Get(ProcessFacetKind.CmdLine, 6).Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Rows_AreCapped_AndReportedTruncated()
    {
        var sb = new StringBuilder("PID,Name\r\n");
        for (int i = 0; i < ProcessFacetIndex.MaxRows + 25; i++) sb.Append("7,row").Append(i).Append("\r\n");
        Write("Memory_DllList.csv", sb.ToString());
        using var idx = await Build();
        var r = idx.Get(ProcessFacetKind.Dlls, 7);
        r.Rows.Should().HaveCount(ProcessFacetIndex.MaxRows);
        r.TotalRows.Should().Be(ProcessFacetIndex.MaxRows + 25);
        r.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task EmptyHost_HasNoFacets()
    {
        using var idx = await ProcessFacetIndex.BuildAsync(Path.Combine(_host, "missing"), CancellationToken.None);
        idx.SourceFiles.Should().BeEmpty();
        idx.GetStatus(ProcessFacetKind.Sids).Should().Be(FacetStatus.PluginNotRun);
    }
}
