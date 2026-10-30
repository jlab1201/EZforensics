using System.Globalization;
using System.Text;
using EzForensics.Core.Processes;
using EzForensics.Core.Zeek;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>
/// The details card from the correlator's files (<see cref="ProcessActivityStore"/>) equals the live <see cref="ProcessActivity"/>: every tab's sets and rows,
/// the timeline, Sources, counts, signature, services and Overview facts; a stale output is not opened; a process's rows are read without parsing the others.
/// </summary>
public sealed class ProcessActivityStoreTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezforensics_store_" + Guid.NewGuid().ToString("N"));
    private string Host => Path.Combine(_case, "PC1");

    public ProcessActivityStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(Host, "Memory"));
        Directory.CreateDirectory(Path.Combine(Host, "Network"));
        Directory.CreateDirectory(Path.Combine(Host, "EventLogs"));
    }

    public void Dispose() { try { Directory.Delete(_case, true); } catch { } }

    private void Memory(string name, string content) => File.WriteAllText(Path.Combine(Host, "Memory", name), content, new UTF8Encoding(false));

    private const string PsHeader = "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n";
    private static string Ps(int pid, int ppid, string name, string create) => $"0,{pid},{ppid},{name},0xffff{pid:x},5,100,1,False,2024-05-01 {create}.0000000,,Disabled\r\n";
    private const string NetHeader = "TreeDepth,Offset,Proto,LocalAddr,LocalPort,ForeignAddr,ForeignPort,State,PID,Owner,Created\r\n";
    private const string DllHeader = "PID,Process,Base,Size,Name,Path,LoadTime,File output\r\n";
    private static string Dll(int pid, string proc, int i, string time) => $"{pid},{proc},0x{i:x},0x2000,m{i}.dll,C:\\Windows\\m{i}.dll,{(time.Length > 0 ? "2024-05-01 " + time : "")},Disabled\r\n";

    private const string EvtHeader = "TimeCreated,EventId,Channel,Provider,ProcessId,Computer,UserName,ExecutableInfo,PayloadData1,PayloadData2,PayloadData3,PayloadData4,PayloadData5,PayloadData6,Payload\r\n";
    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private const string GA = "{11111111-1111-1111-1111-111111111111}";
    private static string Sys(string time, int eid, string exe, string[] pd, string payload = "")
    {
        var cells = new string[6];
        for (int i = 0; i < 6; i++) cells[i] = i < pd.Length ? pd[i] : "";
        return $"{time},{eid},{Q("Microsoft-Windows-Sysmon/Operational")},{Q("Microsoft-Windows-Sysmon")},4,PC1,{Q(@"PC1\bob")},{Q(exe)},{string.Join(",", cells.Select(Q))},{Q(payload)}\r\n";
    }
    private static string Json(params (string Name, string Value)[] f) =>
        "{\"EventData\":{\"Data\":[" + string.Join(",", f.Select(x => $"{{\"@Name\":\"{x.Name}\",\"#text\":\"{x.Value}\"}}")) + "]}}";

    private static string Epoch(string hhmmss) =>
        ((DateTime.ParseExact("2024-05-01 " + hhmmss, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
          - DateTime.UnixEpoch).TotalSeconds).ToString("0.000000", CultureInfo.InvariantCulture);

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

    private void WriteHost(int extraDlls = 0)
    {
        Memory("Memory_PsList.csv", PsHeader + Ps(600, 4, "services.exe", "09:00:00") + Ps(2000, 600, "svchost.exe", "09:00:05") + Ps(3000, 600, "chrome.exe", "10:00:00"));
        Memory("Memory_NetScan.csv", NetHeader + "0,0xe01,TCPv4,10.0.0.5,50111,142.250.1.1,443,ESTABLISHED,3000,chrome.exe,2024-05-01 10:05:00.0000000\r\n");
        var dlls = new StringBuilder(DllHeader);
        dlls.Append(Dll(3000, "chrome.exe", 1, "10:00:00.2500000")).Append(Dll(3000, "chrome.exe", 2, "10:00:00.7500000")).Append(Dll(3000, "chrome.exe", 3, ""));
        for (int i = 0; i < extraDlls; i++) dlls.Append(Dll(2000, "svchost.exe", 100 + i, "09:00:06.0000000"));
        Memory("Memory_DllList.csv", dlls.ToString());
        Memory("Memory_SvcScan.csv", "TreeDepth,Offset,Order,PID,Start,State,Type,Name,Display,Binary,Binary (Registry),Dll\r\n" +
            "0,0x1,10,2000,SERVICE_AUTO_START,SERVICE_RUNNING,SERVICE_WIN32_SHARE_PROCESS,Dnscache,DNS Client,svchost.exe,,\r\n" +
            "0,0x2,11,2000,SERVICE_AUTO_START,SERVICE_RUNNING,SERVICE_WIN32_SHARE_PROCESS,NlaSvc,NLA,svchost.exe,,\r\n");
        const string a = @"C:\tools\a.exe";
        File.WriteAllText(Path.Combine(Host, "EventLogs", "EventLogs.csv"), EvtHeader +
            Sys("2024-05-01 10:10:00.1234567", 1, a, new[] { $"ProcessID: 100, ProcessGUID: {GA}", "RuleName: -", "SHA1=AA,MD5=BB,SHA256=DDEE", @"ParentProcess: C:\Windows\explorer.exe",
                "ParentProcessID: 4, ParentProcessGUID: {22222222-2222-2222-2222-222222222222}", "ParentCommandLine: explorer.exe" }) +
            Sys("2024-05-01 10:10:00.5000000", 7, a, new[] { $"ProcessID: 100, ProcessGUID: {GA}", "RuleName: -", "SHA1=AA,MD5=BB", "Signed: true", "Image: " + a },
                Json(("Signature", "Contoso Ltd"), ("SignatureStatus", "Valid"))) +
            Sys("2024-05-01 10:11:00.0000000", 3, a, new[] { $"ProcessID: 100, ProcessGUID: {GA}", "RuleName: -", "SourceHostname: PC1", "SourceIp: 10.0.0.5", "DestinationHostname: dns.google", "DestinationIp: 8.8.8.8" },
                Json(("SourcePort", "50300"), ("DestinationPort", "53"), ("Protocol", "udp"), ("Initiated", "true"))), new UTF8Encoding(false));
        ZeekLog("conn", "ts uid id.orig_h id.orig_p id.resp_h id.resp_p proto service duration orig_bytes resp_bytes conn_state local_orig local_resp missed_bytes history orig_pkts orig_ip_bytes resp_pkts resp_ip_bytes tunnel_parents",
            "time string addr port addr port enum string interval count count string bool bool count string count count count count set[string]",
            $"{Epoch("10:05:00")} CmatchA1 10.0.0.5 50111 142.250.1.1 443 tcp ssl 1.250000 1200 88000 SF - - 0 ShADadFf 10 900 12 5000 -",
            $"{Epoch("10:11:00")} Cdns1 10.0.0.5 50300 8.8.8.8 53 udp dns 0.010000 40 80 SF - - 0 Dd 1 68 1 96 -");
    }

    private static string Dump(ProcessRowSet s) =>
        string.Join("\n", new[] { $"{s.Id}|{s.Title}|{s.Tab}|{s.Status}|{s.Message}|{s.SourceLabel}|{s.Remark}|{s.TotalRows}|{string.Join("¦", s.Headers)}" }
            .Concat(s.Rows.Select(r => $"  {r.Time?.Ticks}|{r.Tier}|{r.Source}|{string.Join("¦", r.Cells)}|{r.Origin?.CsvPath}|{(r.Origin == null ? "" : ProcessCorrelator.EncodeFilter(r.Origin.Filter))}")));

    [Fact]
    public async Task Card_data_read_from_the_files_equals_the_live_activity()
    {
        WriteHost();
        using var live = await ProcessCorrelation.BuildAsync(Host, CancellationToken.None);
        var a = live.Activity();
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.Built);
        using var store = await ProcessActivityStore.OpenAsync(Host);
        store.Should().NotBeNull("the output is fresh");

        var liveNodes = live.Graph.AllNodes.OrderBy(n => n.Key, StringComparer.Ordinal).ToList();
        var fileNodes = store!.Graph.AllNodes.OrderBy(n => n.Key, StringComparer.Ordinal).ToList();
        fileNodes.Select(n => n.Key).Should().Equal(liveNodes.Select(n => n.Key));
        liveNodes.Should().HaveCountGreaterThan(3);
        for (int i = 0; i < liveNodes.Count; i++)
        {
            var (l, f) = (liveNodes[i], fileNodes[i]);
            foreach (var tab in ProcessActivity.DataTabs)
                string.Join("\n\n", store.SetsFor(f, tab).Select(Dump)).Should().Be(string.Join("\n\n", a.SetsFor(l, tab).Select(Dump)), $"{l.Name} {tab}");
            Dump(store.Timeline(f)).Should().Be(Dump(a.Timeline(l)), l.Name + " timeline");
            Dump(store.Sources(f)).Should().Be(Dump(a.Sources(l)), l.Name + " sources");
            store.Counts(f).Should().BeEquivalentTo(a.Counts(l), l.Name + " counts");
            store.SignatureText(f).Should().Be(a.SignatureText(l));
            store.ServiceNames(f, ProcessFacetIndex.MaxRows).Should().Equal(a.ServiceNames(l, ProcessFacetIndex.MaxRows));
            ProcessActivity.Seen(store.Timeline(f)).Should().Be(ProcessActivity.Seen(a.Timeline(l)));
            $"{f.Name}|{f.Path}|{f.CmdLine}|{f.User}|{f.Hash}|{f.HashSource}|{f.FileVersion}|{f.Company}|{f.Product}|{f.Guid}|{f.Role}|{f.RoleBasis}|{f.Markers}|{f.Sources}|{f.SessionId}|{f.Wow64}|{f.Parent?.Key}|{string.Join(",", f.Indicators.Select(x => x.Kind + ":" + x.Evidence))}"
                .Should().Be($"{l.Name}|{l.Path}|{l.CmdLine}|{l.User}|{l.Hash}|{l.HashSource}|{l.FileVersion}|{l.Company}|{l.Product}|{l.Guid}|{l.Role}|{l.RoleBasis}|{l.Markers}|{l.Sources}|{l.SessionId}|{l.Wow64}|{l.Parent?.Key}|{string.Join(",", l.Indicators.Select(x => x.Kind + ":" + x.Evidence))}");
        }

        // the fixture covers what the card shows: Zeek inline and via socket, sub-second times, signature, services, rows without a time
        var chrome = fileNodes.Single(n => n.Pid == 3000);
        store.SetsFor(chrome, ProcessTab.Network).Select(s => s.Id).Should().Contain(new[] { "mem.Network", "zeek" });
        store.SetsFor(chrome, ProcessTab.Network).Single(s => s.Id == "zeek").Rows.Should().ContainSingle();
        var dlls = store.SetsFor(chrome, ProcessTab.Modules).Single(s => s.Id == "mem.Dlls");
        dlls.Rows.Should().HaveCount(3);
        dlls.Rows[0].Time!.Value.Ticks.Should().Be(dlls.Rows[1].Time!.Value.Ticks - TimeSpan.FromMilliseconds(500).Ticks, "times keep their fractions");
        store.Timeline(chrome).Remark.Should().Contain("no time");
        store.SetsFor(chrome, ProcessTab.Files).Should().Contain(s => s.Status == EvidenceStatus.NotFound && s.Message.Length > 0, "missing sources keep their how-to-get message");
        store.ServiceNames(fileNodes.Single(n => n.Pid == 2000)).Should().Equal("Dnscache", "NlaSvc");
        var logOnly = fileNodes.Single(n => n.Guid == GA);
        store.SignatureText(logOnly).Should().NotBeNullOrEmpty();
        store.Sources(logOnly).Rows.Should().Contain(r => r.Cells[0] == "Sysmon" && r.Cells[1] == "found");
        store.Sources(logOnly).Rows.Should().Contain(r => r.Cells[1] == "missing" && r.Cells[3].Length > 0);
        var evt = store.SetsFor(logOnly, ProcessTab.Network).Single(s => s.Id == "evt.Network").Rows.Single();
        ProcessRowLocator.GridTarget(new[] { evt })!.CsvPath.Should().Be(Path.Combine(Host, "EventLogs", "EventLogs.csv"), "Open rows in grid works from the files");
    }

    [Fact]
    public async Task Stale_output_is_not_opened_until_the_correlator_runs_again()
    {
        WriteHost();
        (await ProcessActivityStore.OpenAsync(Host)).Should().BeNull("nothing was built yet");
        await ProcessCorrelator.RunAsync(Host);
        using (var s = await ProcessActivityStore.OpenAsync(Host)) s.Should().NotBeNull();

        File.AppendAllText(Path.Combine(Host, "Memory", "Memory_PsList.csv"), Ps(5000, 600, "cmd.exe", "12:00:00"));
        (await ProcessActivityStore.OpenAsync(Host)).Should().BeNull("an input changed");
        (await ProcessCorrelator.RunAsync(Host)).State.Should().Be(ProcessCorrelationState.Built);
        using var fresh = await ProcessActivityStore.OpenAsync(Host);
        fresh!.Graph.AllNodes.Should().Contain(n => n.Pid == 5000);

        var m = ProcessCorrelator.ReadManifest(Host)!;
        m.Version = ProcessCorrelator.FormatVersion - 1;
        File.WriteAllText(ProcessCorrelator.InputsPath(Host), System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        ProcessCorrelator.IsUpToDate(Host).Should().BeFalse("an older format is stale");
    }

    [Fact]
    public async Task A_process_reads_only_its_own_rows_of_a_large_activity_file()
    {
        WriteHost(extraDlls: ProcessFacetIndex.MaxRows + 50);
        await ProcessCorrelator.RunAsync(Host);
        // break the Cells of every svchost row (same length, so the offsets stay): only svchost's card may fail
        var path = ProcessCorrelator.ActivityPath(Host);
        var text = File.ReadAllText(path);
        var broken = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^(2000:[^\r\n]*?)""\[(""[^\r\n]*)", m => m.Groups[1].Value + "\"{" + m.Groups[2].Value);
        broken.Should().NotBe(text);
        File.WriteAllText(path, broken, new UTF8Encoding(false));
        // keep the fingerprint (inputs unchanged): the store opens
        using var store = await ProcessActivityStore.OpenAsync(Host);
        var chrome = store!.Graph.AllNodes.Single(n => n.Pid == 3000);
        store.SetsFor(chrome, ProcessTab.Modules).Single(s => s.Id == "mem.Dlls").Rows.Should().HaveCount(3);
        store.Timeline(chrome).Rows.Should().NotBeEmpty();

        var svchost = store.Graph.AllNodes.Single(n => n.Pid == 2000);
        store.Counts(svchost)[ProcessTab.Modules].Should().Be(ProcessFacetIndex.MaxRows + 50, "counts come from Processes.csv");
        var act = () => store.SetsFor(svchost, ProcessTab.Modules);
        act.Should().Throw<System.Text.Json.JsonException>("its own rows are the ones read");
    }
}
