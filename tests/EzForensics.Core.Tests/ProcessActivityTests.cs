using System.Globalization;
using System.Text;
using EzForensics.Core.Processes;
using EzForensics.Core.Results;
using EzForensics.Core.Zeek;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Details card data (<see cref="ProcessActivity"/>): per-tab row sets with tier / source chips, the merged timeline, tab counts, sources and grid targets.</summary>
public sealed class ProcessActivityTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezforensics_activity_" + Guid.NewGuid().ToString("N"));
    private string Host => Path.Combine(_case, "PC1");

    public ProcessActivityTests()
    {
        Directory.CreateDirectory(Path.Combine(Host, "Memory"));
        Directory.CreateDirectory(Path.Combine(Host, "Network"));
    }

    public void Dispose() { try { Directory.Delete(_case, true); } catch { } }

    // ------------------------------------------------------------------ pure merge / locator

    private static ProcessActivityRow Row(string time, string source, LinkTier? tier = LinkTier.Exact, string path = "", string filterValue = "") =>
        new(time.Length == 0 ? null : DateTime.Parse("2024-05-01 " + time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            tier, source, new[] { time, source },
            path.Length == 0 ? null : new ProcessRowOrigin(path, new FilterCondition("PID", ColumnFilterMode.Equals, filterValue)));

    private static ProcessRowSet Set(ProcessTab tab, string title, params ProcessActivityRow[] rows) =>
        new() { Id = title, Title = title, Tab = tab, Headers = new[] { "Time", "What" }, Rows = rows, TotalRows = rows.Length, Status = EvidenceStatus.Available };

    [Fact]
    public void Timeline_merges_every_dated_row_by_time_and_keeps_chips_and_origins()
    {
        var net = Set(ProcessTab.Network, "Sockets", Row("10:05:00", "windows.netscan", LinkTier.Exact, @"C:\m\Memory_NetScan.csv", "3000"), Row("", "windows.netscan"));
        var exec = Set(ProcessTab.Execution, "Prefetch", Row("10:00:05", "Prefetch", LinkTier.PathTime));
        var evt = Set(ProcessTab.Network, "Events", Row("10:05:00", "Sysmon 3"), Row("09:59:59", "Sysmon 1"));

        var t = ProcessActivity.MergeTimeline(new[] { net, exec, evt });

        t.Rows.Select(r => r.Source).Should().Equal("Sysmon 1", "Prefetch", "windows.netscan", "Sysmon 3");
        t.TotalRows.Should().Be(4);
        t.Remark.Should().Contain("1 linked rows have no time");
        t.Headers.Should().Equal("Time", "Tab", "What", "Detail");
        var prefetch = t.Rows[1];
        prefetch.TierText.Should().Be("Path + time");
        prefetch.Cells[1].Should().Be("Execution");
        prefetch.Cells[2].Should().Be("Prefetch");
        t.Rows[2].Origin!.CsvPath.Should().EndWith("Memory_NetScan.csv");
        ProcessActivity.Seen(t).Should().Be((t.Rows[0].Time, t.Rows[3].Time));
    }

    [Fact]
    public void Timeline_cap_keeps_the_oldest_rows_and_the_real_total()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Row($"10:{59 - i:00}:00", "Sysmon 3")).ToArray();
        var t = ProcessActivity.MergeTimeline(new[] { Set(ProcessTab.Network, "Events", rows) }, cap: 10);
        t.Rows.Should().HaveCount(10);
        t.TotalRows.Should().Be(30);
        t.Truncated.Should().BeTrue();
        t.Rows[0].Cells[0].Should().Be("2024-05-01 10:30:00");
        t.Rows[0].Time.Should().BeBefore(t.Rows[9].Time!.Value);
    }

    [Fact]
    public void Grid_target_merges_identical_filters_and_picks_the_main_csv()
    {
        var a = @"C:\h\Memory\Memory_Handles.csv";
        var b = @"C:\h\EventLogs\EventLogs.csv";
        var rows = new[] { Row("10:00:00", "x", path: a, filterValue: "12"), Row("10:00:01", "x", path: a, filterValue: "12"), Row("10:00:02", "y", path: b, filterValue: "1"), Row("", "z") };

        var t = ProcessRowLocator.GridTarget(rows)!;
        t.CsvPath.Should().Be(a);
        t.Rows.Should().Be(2);
        t.Filter.Should().BeOfType<FilterCondition>().Which.Value.Should().Be("12", "the same PID filter is not repeated");

        var pref = ProcessRowLocator.GridTarget(rows, b)!;
        pref.CsvPath.Should().Be(b);

        ProcessRowLocator.GridTarget(new[] { Row("", "z") }).Should().BeNull("no row has an origin");

        var many = Enumerable.Range(0, ProcessRowLocator.MaxFilterTerms + 5).Select(i => Row("10:00:00", "x", path: b, filterValue: i.ToString(CultureInfo.InvariantCulture))).ToList();
        var big = ProcessRowLocator.GridTarget(many)!;
        big.Truncated.Should().BeTrue();
        big.Filter.Should().BeOfType<FilterGroup>().Which.Children.Should().HaveCount(ProcessRowLocator.MaxFilterTerms);
        ((FilterGroup)big.Filter).Kind.Should().Be(FilterGroupKind.Or);
    }

    // ------------------------------------------------------------------ host fixtures

    private static string Epoch(string hhmmss) =>
        ((DateTime.ParseExact("2024-05-01 " + hhmmss, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
          - DateTime.UnixEpoch).TotalSeconds).ToString("0.000000", CultureInfo.InvariantCulture);

    private void Memory(string name, string content) => File.WriteAllText(Path.Combine(Host, "Memory", name), content, new UTF8Encoding(false));

    private void ZeekLog(string log, string fields, string types, params string[] records)
    {
        var sb = new StringBuilder();
        sb.Append("#separator \\x09\n#set_separator\t,\n#empty_field\t(empty)\n#unset_field\t-\n#path\t").Append(log).Append('\n');
        sb.Append("#fields\t").Append(fields.Replace(' ', '\t')).Append('\n');
        sb.Append("#types\t").Append(types.Replace(' ', '\t')).Append('\n');
        foreach (var r in records) sb.Append(r.Replace(' ', '\t')).Append('\n');
        sb.Append("#close\t2024-05-01-12-00-00\n");
        var logPath = Path.Combine(Host, "Network", "Zeek_" + char.ToUpperInvariant(log[0]) + log[1..] + "_cap.log");
        File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(false));
        ZeekLogConverter.Convert(logPath).Succeeded.Should().BeTrue();
    }

    private const string ConnFields = "ts uid id.orig_h id.orig_p id.resp_h id.resp_p proto service duration orig_bytes resp_bytes conn_state local_orig local_resp missed_bytes history orig_pkts orig_ip_bytes resp_pkts resp_ip_bytes tunnel_parents";
    private const string ConnTypes = "time string addr port addr port enum string interval count count string bool bool count string count count count count set[string]";

    private string Conn(string time, string uid, string oh, int op, string rh, int rp, string proto, string service, string origBytes = "517", string respBytes = "4096") =>
        $"{Epoch(time)} {uid} {oh} {op} {rh} {rp} {proto} {service} 1.250000 {origBytes} {respBytes} SF - - 0 ShADadFf 10 900 12 5000 -";

    private const string PsHeader = "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n";
    private static string Ps(int pid, string name, string create) => $"0,{pid},4,{name},0xffff{pid:x},5,100,1,False,2024-05-01 {create}.0000000,,Disabled\r\n";
    private const string NetHeader = "TreeDepth,Offset,Proto,LocalAddr,LocalPort,ForeignAddr,ForeignPort,State,PID,Owner,Created\r\n";
    private static string Net(string proto, string la, int lp, string fa, int fp, string state, int pid, string owner, string created) =>
        $"0,0xe0{pid:x}{lp},{proto},{la},{lp},{fa},{fp},{state},{pid},{owner},{(created.Length > 0 ? "2024-05-01 " + created + ".0000000" : "")}\r\n";

    private const string EvtHeader = "TimeCreated,EventId,Channel,Provider,ProcessId,Computer,UserName,ExecutableInfo,PayloadData1,PayloadData2,PayloadData3,PayloadData4,PayloadData5,PayloadData6,Payload\r\n";
    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private async Task<ProcessActivity> Build()
    {
        var g = ProcessGraphBuilder.Build(Host);
        var facets = await ProcessFacetIndex.BuildAsync(Host, CancellationToken.None);
        var zeek = await ZeekSocketIndex.BuildAsync(Host, CancellationToken.None);
        var evidence = await ProcessEvidenceIndex.BuildAsync(Host, CancellationToken.None);
        evidence.Attach(g.AllNodes);
        zeek.Attach(g, facets);
        _disposables.AddRange(new IDisposable[] { facets, zeek, evidence });
        return new ProcessActivity(g, facets, zeek, evidence);
    }

    private readonly List<IDisposable> _disposables = new();

    private void WriteMemoryAndZeek()
    {
        Memory("Memory_PsList.csv", PsHeader + Ps(3000, "chrome.exe", "10:00:00"));
        Memory("Memory_NetScan.csv", NetHeader + Net("TCPv4", "10.0.0.5", 50111, "142.250.1.1", 443, "ESTABLISHED", 3000, "chrome.exe", "10:05:00"));
        ZeekLog("conn", ConnFields, ConnTypes, Conn("10:05:00", "CmatchA1", "10.0.0.5", 50111, "142.250.1.1", 443, "tcp", "ssl", "1200", "88000"));
        ZeekLog("dns",
            "ts uid id.orig_h id.orig_p id.resp_h id.resp_p proto trans_id rtt query qclass qclass_name qtype qtype_name rcode rcode_name AA TC RD RA Z answers TTLs rejected",
            "time string addr port addr port enum count interval string count string count string count string bool bool bool bool count vector[string] vector[interval] bool",
            $"{Epoch("10:04:59")} Cdns1 10.0.0.5 51515 10.0.0.1 53 udp 4242 0.010000 www.google.com 1 C_INTERNET 1 A 0 NOERROR F F T T 0 142.250.1.1 300.000000 F");
    }

    [Fact]
    public async Task Network_tab_shows_memory_sockets_with_zeek_inline_and_the_zeek_connection_with_chips()
    {
        WriteMemoryAndZeek();
        try
        {
            var a = await Build();
            var chrome = a.Graph.AllNodes.Single(n => n.Pid == 3000);
            var sets = a.SetsFor(chrome, ProcessTab.Network);
            sets.Select(s => s.Id).Should().Equal("mem.Network", "evt.Network", "zeek");

            var mem = sets[0];
            mem.Status.Should().Be(EvidenceStatus.Available);
            var sock = mem.Rows.Should().ContainSingle().Subject;
            sock.TierText.Should().Be("Exact");
            sock.Source.Should().Be("windows.netscan");
            sock.Time.Should().NotBeNull("netscan Created is the row time");
            int zc = mem.Headers.ToList().IndexOf("Zeek");
            sock.Cells[zc].Should().Be("1 conn · 1,200 / 88,000 bytes · ssl");
            sock.Cells[zc + 1].Should().Be("www.google.com");
            sock.Origin!.CsvPath.Should().EndWith("Memory_NetScan.csv");
            sock.Origin.Filter.ToDisplayString().Should().Be("[PID] = '3000'");

            sets[1].Status.Should().Be(EvidenceStatus.NotFound, "no event log CSV");
            sets[1].Message.Should().Contain("Ingest");

            var z = sets[2].Rows.Should().ContainSingle().Subject;
            z.Source.Should().Be("Zeek via socket");
            z.Tier.Should().Be(LinkTier.Exact);
            z.Origin!.Filter.ToDisplayString().Should().Be("[uid] = 'CmatchA1'");

            var counts = a.Counts(chrome);
            counts[ProcessTab.Network].Should().Be(2);
            counts[ProcessTab.Files].Should().Be(0);
            counts[ProcessTab.Timeline].Should().Be(2);

            var timeline = a.Timeline(chrome);
            timeline.Rows.Select(r => r.Source).Should().BeEquivalentTo(new[] { "windows.netscan", "Zeek via socket" });

            var files = a.SetsFor(chrome, ProcessTab.Files);
            files[0].Id.Should().Be("mem.HandlesFiles");
            files[0].Status.Should().Be(EvidenceStatus.NotFound);
            files[0].Message.Should().Be("windows.handles not run for this image — run it from Tools › Volatility.");

            var sources = a.Sources(chrome);
            Status(sources, "windows.netscan").Should().Be("found");
            Status(sources, "windows.handles").Should().Be("missing");
            Status(sources, "Zeek (via the process's sockets)").Should().Be("found");
            Status(sources, "Prefetch (path + run time)").Should().Be("missing");
            Status(sources, "Event logs (EvtxECmd)").Should().Be("missing");
            sources.Rows.Single(r => r.Cells[0] == "windows.handles").Cells[3].Should().Be("Tools › Volatility › windows.handles");
            counts[ProcessTab.Sources].Should().Be(sources.Rows.Count(r => r.Cells[1] == ProcessActivity.FoundStatus));
        }
        finally { foreach (var d in _disposables) d.Dispose(); }
    }

    private static string Status(ProcessRowSet sources, string name) => sources.Rows.Single(r => r.Cells[0] == name).Cells[1];

    [Fact]
    public async Task Log_only_process_gets_event_rows_with_grid_origins_and_no_memory_rows()
    {
        const string guid = "{11111111-1111-1111-1111-111111111111}";
        var rows =
            $"2024-05-01 10:00:00.1234567,1,{Q("Microsoft-Windows-Sysmon/Operational")},{Q("Microsoft-Windows-Sysmon")},4,PC1,{Q(@"PC1\bob")},{Q(@"C:\tools\a.exe")}," +
            $"{Q($"ProcessID: 5000, ProcessGUID: {guid}")},{Q("RuleName: -")},{Q("SHA1=AA")},{Q(@"ParentProcess: C:\Windows\explorer.exe")},{Q("ParentProcessID: 4, ParentProcessGUID: {22222222-2222-2222-2222-222222222222}")},{Q("ParentCommandLine: explorer.exe")},{Q("")}\r\n" +
            $"2024-05-01 10:01:00.5000000,3,{Q("Microsoft-Windows-Sysmon/Operational")},{Q("Microsoft-Windows-Sysmon")},4,PC1,{Q(@"PC1\bob")},{Q(@"C:\tools\a.exe")}," +
            $"{Q($"ProcessID: 5000, ProcessGUID: {guid}")},{Q("RuleName: -")},{Q("SourceHostname: PC1")},{Q("SourceIp: 10.0.0.5")},{Q("DestinationHostname: dns.google")},{Q("DestinationIp: 8.8.8.8")}," +
            Q("{\"EventData\":{\"Data\":[{\"@Name\":\"SourcePort\",\"#text\":\"50300\"},{\"@Name\":\"DestinationPort\",\"#text\":\"53\"},{\"@Name\":\"Protocol\",\"#text\":\"udp\"},{\"@Name\":\"Initiated\",\"#text\":\"true\"}]}}") + "\r\n";
        Directory.CreateDirectory(Path.Combine(Host, "EventLogs"));
        var evtx = Path.Combine(Host, "EventLogs", "EventLogs.csv");
        File.WriteAllText(evtx, EvtHeader + rows, new UTF8Encoding(false));
        Memory("Memory_PsList.csv", PsHeader + Ps(5000, "other.exe", "08:00:00"));   // the same PID in memory is another process (started earlier, different image)
        Memory("Memory_DllList.csv", "PID,Process,Base,Size,Name,Path,LoadTime,File output\r\n5000,other.exe,0x1,0x2,x.dll,C:\\x.dll,2024-05-01 08:00:01.0000000,Disabled\r\n");
        ZeekLog("conn", ConnFields, ConnTypes, Conn("10:01:00", "Cdns1", "10.0.0.5", 50300, "8.8.8.8", 53, "udp", "dns"));
        try
        {
            var a = await Build();
            var node = a.Graph.AllNodes.Single(n => n.Guid == guid);
            node.Markers.Should().HaveFlag(ProcessMarkers.FromLogsOnly);

            var modules = a.SetsFor(node, ProcessTab.Modules);
            modules[0].Id.Should().Be("mem.Dlls");
            modules[0].Status.Should().Be(EvidenceStatus.NotFound);
            modules[0].Message.Should().StartWith("Not in the memory image", "a PID match alone is no link for a log-only process");

            var net = a.SetsFor(node, ProcessTab.Network).Single(s => s.Id == "evt.Network");
            var row = net.Rows.Should().ContainSingle().Subject;
            row.Source.Should().Be("Sysmon 3");
            row.TierText.Should().Be("Exact");
            row.Cells[net.Headers.ToList().IndexOf("Destination IP")].Should().Be("8.8.8.8");
            row.Cells[net.Headers.ToList().IndexOf("Zeek")].Should().StartWith("1 conn");
            row.Origin!.CsvPath.Should().Be(evtx);
            row.Origin.Filter.ToDisplayString().Should().Be("[EventId] = '3' And StartsWith([TimeCreated], '2024-05-01 10:01:00')");

            var exec = a.SetsFor(node, ProcessTab.Execution);
            exec.Select(s => s.Id).Should().Equal("prefetch", "evt.Process");
            exec[1].Rows.Should().ContainSingle().Which.Source.Should().Be("Sysmon 1");

            var target = ProcessRowLocator.GridTarget(net.Rows)!;
            target.CsvPath.Should().Be(evtx);

            var sources = a.Sources(node);
            Status(sources, "Sysmon").Should().Be("found");
            Status(sources, "Security").Should().Be("missing");
            Status(sources, "windows.dlllist").Should().Be("no rows");
            a.Counts(node)[ProcessTab.Modules].Should().Be(0, "the memory DLL rows of PID 5000 belong to the other process");
        }
        finally { foreach (var d in _disposables) d.Dispose(); }
    }

    [Fact]
    public void Summary_skips_the_columns_that_repeat_the_process()
    {
        ProcessActivity.Summarize(new[] { "PID", "Process", "Name", "LoadTime", "Path" }, new[] { "12", "a.exe", "x.dll", "2024", @"C:\x.dll" })
            .Should().Be(@"Name: x.dll · Path: C:\x.dll");
    }
}
