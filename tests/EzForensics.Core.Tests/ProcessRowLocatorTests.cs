using System.Text;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ProcessRowLocatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_rowloc_" + Guid.NewGuid().ToString("N"));

    public ProcessRowLocatorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task FindsRowByPid_NameAndCreateTime()
    {
        var path = Path.Combine(_dir, "Memory_PsList.csv");
        File.WriteAllText(path, "PID,PPID,ImageFileName,CreateTime\r\n" +
            "4,0,System,2024-01-01T00:00:00+00:00\r\n" +
            "900,4,old.exe,2024-01-01T00:01:00+00:00\r\n" +
            "900,4,new.exe,2024-01-01T00:09:00+00:00\r\n", new UTF8Encoding(false));
        (await ProcessRowLocator.FindRowsAsync(path, 4, "System", null, default)).Should().Equal(0L);
        (await ProcessRowLocator.FindRowsAsync(path, 900, "new.exe", null, default)).Should().Equal(2L);
        var t = new DateTime(2024, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        (await ProcessRowLocator.FindRowsAsync(path, 900, null, t, default)).Should().Equal(1L);
        (await ProcessRowLocator.FindRowsAsync(path, 77, null, null, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task MatchRows_uses_keyed_and_plain_filters_in_one_pass()
    {
        var path = Path.Combine(_dir, "Security.csv");
        File.WriteAllText(path, "EventId,TimeCreated,uid,Message\r\n" +
            "4688,2024-01-01 00:00:01.123,a,x\r\n" +
            "4688,2024-01-01 00:00:02.000,b,y\r\n" +
            "4624,2024-01-01 00:00:01.500,c,powershell\r\n" +
            "4688,2024-01-01 00:00:01.900,d,z\r\n", new UTF8Encoding(false));
        var filters = new List<EzForensics.Core.Results.FilterNode>
        {
            EzForensics.Core.Results.FilterGroup.And(
                new EzForensics.Core.Results.FilterCondition("EventId", EzForensics.Core.Results.ColumnFilterMode.Equals, "4688"),
                new EzForensics.Core.Results.FilterCondition("TimeCreated", EzForensics.Core.Results.ColumnFilterMode.BeginsWith, "2024-01-01 00:00:01")),
            new EzForensics.Core.Results.FilterCondition("UID", EzForensics.Core.Results.ColumnFilterMode.Equals, "B"),
            new EzForensics.Core.Results.FilterCondition("Message", EzForensics.Core.Results.ColumnFilterMode.Contains, "shell"),
        };
        (await ProcessRowLocator.MatchRowsAsync(path, filters, default)).Should().Equal(0L, 1L, 2L, 3L);
        (await ProcessRowLocator.MatchRowsAsync(path, filters.Take(1).ToList(), default)).Should().Equal(0L, 3L);
    }

    private sealed class FakeSource : IProcessDetailSource
    {
        public Dictionary<(ProcessNode, ProcessTab), List<ProcessRowSet>> Sets { get; } = new();
        public IReadOnlyList<ProcessRowSet> SetsFor(ProcessNode node, ProcessTab tab) => Sets.TryGetValue((node, tab), out var s) ? s : new List<ProcessRowSet>();
        public ProcessRowSet Timeline(ProcessNode node) => new();
        public ProcessRowSet Sources(ProcessNode node) => new();
        public Dictionary<ProcessTab, int> Counts(ProcessNode node) => new();
        public string? SignatureText(ProcessNode node) => null;
        public IReadOnlyList<string> ServiceNames(ProcessNode node, int cap = 20) => Array.Empty<string>();
    }

    [Fact]
    public async Task CollectRows_reads_card_origins_and_process_lists_per_csv()
    {
        var pslist = Path.Combine(_dir, "Memory_PsList.csv");
        File.WriteAllText(pslist, "PID,PPID,ImageFileName,CreateTime\r\n4,0,System,\r\n900,4,evil.exe,\r\n", new UTF8Encoding(false));
        var netscan = Path.Combine(_dir, "Memory_NetScan.csv");
        File.WriteAllText(netscan, "PID,Owner,ForeignAddr\r\n900,evil.exe,1.2.3.4\r\n4,System,0.0.0.0\r\n900,evil.exe,5.6.7.8\r\n", new UTF8Encoding(false));
        var node = new ProcessNode { Pid = 900, Name = "evil.exe" };
        var src = new FakeSource();
        var origin = new ProcessRowOrigin(netscan, new EzForensics.Core.Results.FilterCondition("PID", EzForensics.Core.Results.ColumnFilterMode.Equals, "900"));
        src.Sets[(node, ProcessTab.Network)] = new List<ProcessRowSet>
        {
            new() { Id = "net", Tab = ProcessTab.Network, Rows = new[] { new ProcessActivityRow(null, null, "netscan", Array.Empty<string>(), origin), new ProcessActivityRow(null, null, "netscan", Array.Empty<string>(), origin) } },
        };
        var result = await ProcessRowLocator.CollectRowsAsync(src, new[] { node }, new[] { pslist }, default);
        result.Select(r => (Path.GetFileName(r.CsvPath), string.Join(",", r.Rows))).Should().BeEquivalentTo(new[] { ("Memory_NetScan.csv", "0,2"), ("Memory_PsList.csv", "1") });
    }
}
