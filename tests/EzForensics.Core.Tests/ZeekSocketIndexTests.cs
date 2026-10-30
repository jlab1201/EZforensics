using System.Globalization;
using System.Text;
using EzForensics.Core.Processes;
using EzForensics.Core.Zeek;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Zeek conn / dns / http / ssl joined to processes only through an Exact-linked socket. The Zeek fixtures are ASCII logs in Zeek's own field layout, converted with
/// <see cref="ZeekLogConverter"/> exactly as the Zeek job does (<c>Zeek_Conn_cap.csv</c> ...).
/// </summary>
public sealed class ZeekSocketIndexTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezforensics_zeekjoin_" + Guid.NewGuid().ToString("N"));
    private string Host => Path.Combine(_case, "PC1");

    public ZeekSocketIndexTests()
    {
        Directory.CreateDirectory(Path.Combine(Host, "Memory"));
        Directory.CreateDirectory(Path.Combine(Host, "Network"));
    }

    public void Dispose() { try { Directory.Delete(_case, true); } catch { } }

    private static string Epoch(string hhmmss) =>
        ((DateTime.ParseExact("2024-05-01 " + hhmmss, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
          - DateTime.UnixEpoch).TotalSeconds).ToString("0.000000", CultureInfo.InvariantCulture);

    private void Memory(string name, string content) => File.WriteAllText(Path.Combine(Host, "Memory", name), content, new UTF8Encoding(false));

    /// <summary>Writes a Zeek ASCII log into Network and converts it the way the Zeek job does.</summary>
    private void ZeekLog(string log, string fields, string types, params string[] records)
    {
        var sb = new StringBuilder();
        sb.Append("#separator \\x09\n#set_separator\t,\n#empty_field\t(empty)\n#unset_field\t-\n#path\t").Append(log).Append('\n');
        sb.Append("#fields\t").Append(fields.Replace(' ', '\t')).Append('\n');
        sb.Append("#types\t").Append(types.Replace(' ', '\t')).Append('\n');
        foreach (var r in records) sb.Append(r.Replace(' ', '\t')).Append('\n');
        sb.Append("#close\t2024-05-01-12-00-00\n");
        var name = "Zeek_" + char.ToUpperInvariant(log[0]) + log[1..] + "_cap";
        var logPath = Path.Combine(Host, "Network", name + ".log");
        File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(false));
        ZeekLogConverter.Convert(logPath).Succeeded.Should().BeTrue();
    }

    private const string ConnFields = "ts uid id.orig_h id.orig_p id.resp_h id.resp_p proto service duration orig_bytes resp_bytes conn_state local_orig local_resp missed_bytes history orig_pkts orig_ip_bytes resp_pkts resp_ip_bytes tunnel_parents";
    private const string ConnTypes = "time string addr port addr port enum string interval count count string bool bool count string count count count count set[string]";

    private string Conn(string time, string uid, string oh, int op, string rh, int rp, string proto, string service, string origBytes = "517", string respBytes = "4096") =>
        $"{Epoch(time)} {uid} {oh} {op} {rh} {rp} {proto} {service} 1.250000 {origBytes} {respBytes} SF - - 0 ShADadFf 10 900 12 5000 -";

    private void WriteDns(params string[] records) => ZeekLog("dns",
        "ts uid id.orig_h id.orig_p id.resp_h id.resp_p proto trans_id rtt query qclass qclass_name qtype qtype_name rcode rcode_name AA TC RD RA Z answers TTLs rejected",
        "time string addr port addr port enum count interval string count string count string count string bool bool bool bool count vector[string] vector[interval] bool",
        records);

    private string Dns(string time, string query, string answers) =>
        $"{Epoch(time)} Cdns{query.Length} 10.0.0.5 51515 10.0.0.1 53 udp 4242 0.010000 {query} 1 C_INTERNET 1 A 0 NOERROR F F T T 0 {answers} 300.000000 F";

    private const string PsHeader = "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output\r\n";
    private static string Ps(int pid, string name, string create) => $"0,{pid},4,{name},0xffff{pid:x},5,100,1,False,2024-05-01 {create}.0000000,,Disabled\r\n";

    private const string NetHeader = "TreeDepth,Offset,Proto,LocalAddr,LocalPort,ForeignAddr,ForeignPort,State,PID,Owner,Created\r\n";
    private static string Net(string proto, string la, int lp, string fa, int fp, string state, int pid, string owner, string created) =>
        $"0,0xe0{pid:x}{lp},{proto},{la},{lp},{fa},{fp},{state},{pid},{owner},{(created.Length > 0 ? "2024-05-01 " + created + ".0000000" : "")}\r\n";

    private async Task<(ProcessGraph Graph, ZeekSocketIndex Zeek, ProcessFacetIndex Facets)> Build()
    {
        var g = ProcessGraphBuilder.Build(Host);
        var facets = await ProcessFacetIndex.BuildAsync(Host, CancellationToken.None);
        var zeek = await ZeekSocketIndex.BuildAsync(Host, CancellationToken.None);
        zeek.Attach(g, facets);
        return (g, zeek, facets);
    }

    private void WriteStandardCase()
    {
        Memory("Memory_PsList.csv", PsHeader + Ps(3000, "chrome.exe", "10:00:00") + Ps(3100, "svchost.exe", "08:00:00") + Ps(4000, "a.exe", "10:00:00") + Ps(4100, "b.exe", "10:00:00"));
        Memory("Memory_NetScan.csv", NetHeader +
            Net("TCPv4", "10.0.0.5", 50111, "142.250.1.1", 443, "ESTABLISHED", 3000, "chrome.exe", "10:05:00") +
            Net("TCPv4", "10.0.0.5", 3389, "10.0.0.9", 51000, "ESTABLISHED", 3100, "svchost.exe", "") +
            Net("TCPv4", "0.0.0.0", 135, "0.0.0.0", 0, "LISTENING", 3100, "svchost.exe", "08:00:01") +
            // one socket owned by two processes (a duplicated handle): Zeek cannot tell which one sent the bytes
            Net("TCPv4", "10.0.0.5", 50200, "1.2.3.4", 80, "ESTABLISHED", 4000, "a.exe", "") +
            Net("TCPv4", "10.0.0.5", 50200, "1.2.3.4", 80, "ESTABLISHED", 4100, "b.exe", ""));
        ZeekLog("conn", ConnFields, ConnTypes,
            Conn("10:05:00", "CmatchA1", "10.0.0.5", 50111, "142.250.1.1", 443, "tcp", "ssl", "1200", "88000"),
            Conn("09:00:00", "CbeforeStart", "10.0.0.5", 50111, "142.250.1.1", 443, "tcp", "ssl"),    // before chrome started: not its connection
            Conn("10:06:00", "CinboundRdp", "10.0.0.9", 51000, "10.0.0.5", 3389, "tcp", "-"),          // the other direction: orig is the remote side
            Conn("10:07:00", "CotherPort", "10.0.0.5", 50112, "142.250.1.1", 443, "tcp", "ssl"),       // no process has this socket
            Conn("10:08:00", "CtwoOwners", "10.0.0.5", 50200, "1.2.3.4", 80, "tcp", "http"),
            Conn("10:09:00", "CudpSame", "10.0.0.5", 50111, "142.250.1.1", 443, "udp", "-"));          // same addresses and ports, other protocol
        WriteDns(
            Dns("09:30:00", "early.example", "142.250.1.1"),      // before chrome started
            Dns("10:04:59", "www.google.com", "www.google.com,142.250.1.1"),
            Dns("10:05:30", "late.example", "142.250.1.1"),       // after the connection
            Dns("10:04:00", "other.example", "9.9.9.9"));
        ZeekLog("ssl", "ts uid id.orig_h id.orig_p id.resp_h id.resp_p version cipher curve server_name resumed last_alert next_protocol established ssl_history cert_chain_fps client_cert_chain_fps sni_matches_cert",
            "time string addr port addr port string string string string bool string string bool string vector[string] vector[string] bool",
            $"{Epoch("10:05:00")} CmatchA1 10.0.0.5 50111 142.250.1.1 443 TLSv13 TLS_AES_128_GCM_SHA256 x25519 www.google.com F - h2 T CsiI (empty) (empty) T");
        ZeekLog("http", "ts uid id.orig_h id.orig_p id.resp_h id.resp_p trans_depth method host uri referrer version user_agent origin request_body_len response_body_len status_code status_msg",
            "time string addr port addr port count string string string string string string string count count count string",
            $"{Epoch("10:08:00")} CtwoOwners 10.0.0.5 50200 1.2.3.4 80 1 GET 1.2.3.4 /payload.bin - 1.1 curl/8.0 - 0 1024 200 OK");
    }

    [Fact]
    public async Task Conn_row_links_through_the_exact_socket_inside_the_lifetime_and_is_enriched()
    {
        WriteStandardCase();
        var (g, zeek, facets) = await Build();
        using var _f = facets;
        using var _z = zeek;
        zeek.Status.Should().Be(EzForensics.Core.Processes.EvidenceStatus.Available);
        zeek.Files.Should().HaveCount(4);

        var chrome = g.AllNodes.Single(n => n.Pid == 3000);
        var links = zeek.LinksFor(chrome);
        var link = links.Should().ContainSingle("the 09:00 row predates the process, the other port and the udp row are no socket of it").Subject;
        link.Conn.Uid.Should().Be("CmatchA1");
        link.Conn.Service.Should().Be("ssl");
        link.Conn.OrigBytes.Should().Be("1200");
        link.Conn.RespBytes.Should().Be("88000");
        link.Conn.Duration.Should().Be("1.250000");
        link.Tier.Should().Be(LinkTier.Exact);
        link.SourceNote.Should().Be("Zeek via socket");
        link.Socket.Source.Should().Be("netscan");
        link.SslServerName.Should().Be("www.google.com");
        link.Dns.Select(d => d.Query).Should().Equal(new[] { "www.google.com" }, "inside the lifetime and before the connection only");

        var rdp = zeek.LinksFor(g.AllNodes.Single(n => n.Pid == 3100));
        rdp.Should().ContainSingle().Which.Conn.Uid.Should().Be("CinboundRdp", "either direction of the 5-tuple matches");
    }

    [Fact]
    public async Task A_5_tuple_owned_by_two_processes_at_that_time_links_to_neither()
    {
        WriteStandardCase();
        var (g, zeek, facets) = await Build();
        using var _f = facets;
        using var _z = zeek;
        zeek.LinksFor(g.AllNodes.Single(n => n.Pid == 4000)).Should().BeEmpty();
        zeek.LinksFor(g.AllNodes.Single(n => n.Pid == 4100)).Should().BeEmpty();
    }

    [Fact]
    public async Task No_shared_socket_no_link_and_no_zeek_is_reported_not_found()
    {
        Memory("Memory_PsList.csv", PsHeader + Ps(3000, "chrome.exe", "10:00:00"));
        Memory("Memory_NetScan.csv", NetHeader + Net("TCPv4", "10.0.0.5", 50111, "142.250.1.1", 443, "ESTABLISHED", 3000, "chrome.exe", ""));
        var (g, zeek, facets) = await Build();
        using (facets) using (zeek)
        {
            zeek.Status.Should().Be(EzForensics.Core.Processes.EvidenceStatus.NotFound);
            zeek.HowToGet.Should().Be("Ingest › Zeek on a pcap");
            zeek.LinksFor(g.AllNodes[0]).Should().BeEmpty();
        }

        ZeekLog("conn", ConnFields, ConnTypes, Conn("10:05:00", "Cx", "10.0.0.5", 50999, "142.250.1.1", 443, "tcp", "ssl"));
        var (g2, zeek2, facets2) = await Build();
        using (facets2) using (zeek2)
        {
            zeek2.Status.Should().Be(EzForensics.Core.Processes.EvidenceStatus.Available);
            zeek2.LinksFor(g2.AllNodes[0]).Should().BeEmpty("the conn row's source port is no socket of the process");
        }
    }

    [Fact]
    public async Task Sysmon_3_socket_of_a_log_only_process_links_its_zeek_connection()
    {
        const string header = "TimeCreated,EventId,Channel,Provider,ProcessId,Computer,UserName,ExecutableInfo,PayloadData1,PayloadData2,PayloadData3,PayloadData4,PayloadData5,PayloadData6,Payload\r\n";
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        const string guid = "{11111111-1111-1111-1111-111111111111}";
        var rows =
            $"2024-05-01 10:00:00,1,{Q("Microsoft-Windows-Sysmon/Operational")},{Q("Microsoft-Windows-Sysmon")},4,PC1,{Q(@"PC1\bob")},{Q(@"C:\tools\a.exe")}," +
            $"{Q($"ProcessID: 5000, ProcessGUID: {guid}")},{Q("RuleName: -")},{Q("SHA1=AA")},{Q(@"ParentProcess: C:\Windows\explorer.exe")},{Q("ParentProcessID: 4, ParentProcessGUID: {22222222-2222-2222-2222-222222222222}")},{Q("ParentCommandLine: explorer.exe")},{Q("")}\r\n" +
            $"2024-05-01 10:01:00,3,{Q("Microsoft-Windows-Sysmon/Operational")},{Q("Microsoft-Windows-Sysmon")},4,PC1,{Q(@"PC1\bob")},{Q(@"C:\tools\a.exe")}," +
            $"{Q($"ProcessID: 5000, ProcessGUID: {guid}")},{Q("RuleName: -")},{Q("SourceHostname: PC1")},{Q("SourceIp: 10.0.0.5")},{Q("DestinationHostname: dns.google")},{Q("DestinationIp: 8.8.8.8")}," +
            Q("{\"EventData\":{\"Data\":[{\"@Name\":\"SourcePort\",\"#text\":\"50300\"},{\"@Name\":\"DestinationPort\",\"#text\":\"53\"},{\"@Name\":\"Protocol\",\"#text\":\"udp\"},{\"@Name\":\"Initiated\",\"#text\":\"true\"}]}}") + "\r\n";
        Directory.CreateDirectory(Path.Combine(Host, "EventLogs"));
        File.WriteAllText(Path.Combine(Host, "EventLogs", "EventLogs.csv"), header + rows, new UTF8Encoding(false));
        ZeekLog("conn", ConnFields, ConnTypes, Conn("10:01:00", "Cdns1", "10.0.0.5", 50300, "8.8.8.8", 53, "udp", "dns"));

        var (g, zeek, facets) = await Build();
        using var _f = facets;
        using var _z = zeek;
        var node = g.AllNodes.Single(n => n.Pid == 5000);
        node.Markers.Should().HaveFlag(ProcessMarkers.FromLogsOnly);
        var link = zeek.LinksFor(node).Should().ContainSingle().Subject;
        link.Socket.Source.Should().Be("Sysmon 3");
        link.Socket.Proto.Should().Be("udp");
        link.Conn.Service.Should().Be("dns");
    }

    [Fact]
    public void Socket_normalisation_and_direction_free_key()
    {
        ProcessSockets.NormIp("[::ffff:10.0.0.5]").Should().Be("10.0.0.5");
        ProcessSockets.NormIp("fe80::1%12").Should().Be("fe80::1");
        ProcessSockets.NormIp("*").Should().BeEmpty();
        ProcessSockets.NormProto("TCPv6").Should().Be("tcp");
        ProcessSockets.NormProto("17").Should().Be("udp");
        ProcessSockets.Make("TCPv4", "0.0.0.0", "135", "0.0.0.0", "0", null, "netscan").Should().BeNull("a listening socket has no 5-tuple");
        ProcessSockets.TupleKey("tcp", "10.0.0.5", 1, "1.1.1.1", 2).Should().Be(ProcessSockets.TupleKey("tcp", "1.1.1.1", 2, "10.0.0.5", 1));
    }
}
