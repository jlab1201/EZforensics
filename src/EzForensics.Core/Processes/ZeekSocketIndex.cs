using System.Globalization;
using System.Net;
using EzForensics.Core.Results;

namespace EzForensics.Core.Processes;

/// <summary>
/// A socket of a process with a full 5-tuple, Exact-linked to it: a netscan / netstat row of the PID from memory, or a Sysmon 3 / Security 5156 row linked by PID or GUID.
/// Addresses are normalised (no brackets, no <c>::ffff:</c> prefix, lower case); <see cref="Proto"/> is "tcp" or "udp".
/// </summary>
public sealed record ProcessSocket(string Proto, string LocalIp, int LocalPort, string RemoteIp, int RemotePort, DateTime? Time, string Source)
{
    /// <summary>Direction-free key: protocol plus both endpoints in a fixed order, so an orig / resp pair matches either way round.</summary>
    public string Key => ProcessSockets.TupleKey(Proto, LocalIp, LocalPort, RemoteIp, RemotePort);
}

/// <summary>One Zeek conn.log row.</summary>
public sealed record ZeekConn(DateTime Time, string Uid, string Proto, string OrigIp, int OrigPort, string RespIp, int RespPort, string Service, string Duration,
    string OrigBytes, string RespBytes, string ConnState, string SourceFile);

/// <summary>A dns.log row whose answers hold the connection's responder IP, seen inside the process lifetime and before the connection.</summary>
public sealed record ZeekDnsAnswer(DateTime Time, string Query, string Answers, string SourceFile);

/// <summary>An http.log request of the connection (same uid).</summary>
public sealed record ZeekHttpRequest(string Method, string Host, string Uri, string UserAgent);

/// <summary>
/// A Zeek connection tied to a process through one of its sockets (<see cref="LinkTier.Exact"/>, source note <see cref="ZeekSocketIndex.SourceNote"/>): the conn row has the
/// socket's exact 5-tuple, its time is inside the process lifetime, and no other process owns that 5-tuple at that time. Enriched with dns / http / ssl.
/// </summary>
public sealed record ZeekSocketLink(ProcessSocket Socket, ZeekConn Conn, IReadOnlyList<ZeekDnsAnswer> Dns, IReadOnlyList<ZeekHttpRequest> Http, string SslServerName)
{
    public LinkTier Tier => LinkTier.Exact;
    public string SourceNote => ZeekSocketIndex.SourceNote;
}

/// <summary>Sockets of a process with a complete 5-tuple, from the sources that are Exact-linked to it.</summary>
public static class ProcessSockets
{
    private static readonly string[] ProtoCols = { "proto", "protocol" };
    private static readonly string[] LocalAddrCols = { "localaddr", "localaddress" };
    private static readonly string[] LocalPortCols = { "localport" };
    private static readonly string[] ForeignAddrCols = { "foreignaddr", "foreignaddress", "remoteaddr" };
    private static readonly string[] ForeignPortCols = { "foreignport", "remoteport" };
    private static readonly string[] CreatedCols = { "created", "createtime" };

    /// <summary>
    /// netscan / netstat rows of the PID (memory nodes only; a row with a Created time must fall inside the lifetime) and Sysmon 3 / Security 5156 events Exact-linked to
    /// the node. Listening sockets and rows without a remote endpoint have no 5-tuple and are left out.
    /// </summary>
    public static IReadOnlyList<ProcessSocket> For(ProcessNode node, ProcessFacetIndex? facets, ProcessEventIndex? events, ProcessLifetimes? life = null)
    {
        var list = new List<ProcessSocket>();
        life ??= new ProcessLifetimes(new[] { node });
        if (facets != null && ProcessGraphBuilder.InMemory(node))
        {
            var net = facets.Get(ProcessFacetKind.Network, node.Pid);
            if (net.Status == FacetStatus.Available)
            {
                var reader = MemoryRowReader(net.Headers);
                foreach (var r in net.Rows)
                {
                    var (s, created) = reader(r);
                    if (!life.PidFits(node, created)) continue;
                    if (s != null) list.Add(s);
                }
            }
        }
        if (events != null)
            foreach (var l in events.LinksFor(node, ProcessEventGroup.Network))
            {
                var e = l.Event;
                if (l.Tier != LinkTier.Exact || e.EventId is not (3 or 5156)) continue;
                // Sysmon 3 / 5156: T4 source IP, SourcePort, T2 destination IP, T3 destination port
                if (OfEvent(e) is { } s) list.Add(s);
            }
        return list;
    }

    /// <summary>Reads the socket (null without a full 5-tuple) and the Created time of netscan / netstat rows with these headers.</summary>
    public static Func<string[], (ProcessSocket? Socket, DateTime? Created)> MemoryRowReader(IReadOnlyList<string> headers)
    {
        var norm = headers.Select(ProcessLogText.Norm).ToList();
        int C(string[] a) { foreach (var x in a) { int i = norm.IndexOf(x); if (i >= 0) return i; } return -1; }
        int pr = C(ProtoCols), la = C(LocalAddrCols), lp = C(LocalPortCols), fa = C(ForeignAddrCols), fp = C(ForeignPortCols), cr = C(CreatedCols);
        return r =>
        {
            var created = ParseTime(Cell(r, cr));
            return (Make(Cell(r, pr), Cell(r, la), Cell(r, lp), Cell(r, fa), Cell(r, fp), created, "netscan"), created);
        };
    }

    /// <summary>The socket of a Sysmon 3 / Security 5156 row (source IP and port, destination IP and port); null without a full 5-tuple.</summary>
    public static ProcessSocket? OfEvent(ProcessEventRecord e) => Make(e.Protocol, e.T4, e.SourcePort, e.T2, e.T3, e.Time, e.Source);

    private static string Cell(string[] r, int i) => i >= 0 && i < r.Length ? (r[i] ?? "").Trim() : "";

    private static DateTime? ParseTime(string s) =>
        s.Length > 0 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    /// <summary>A socket when both endpoints are complete (address and a non-zero port); null otherwise.</summary>
    public static ProcessSocket? Make(string proto, string localIp, string localPort, string remoteIp, string remotePort, DateTime? time, string source)
    {
        var p = NormProto(proto);
        var l = NormIp(localIp);
        var r = NormIp(remoteIp);
        if (p.Length == 0 || l.Length == 0 || r.Length == 0) return null;
        if (!int.TryParse(localPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lp) || !int.TryParse(remotePort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rp)) return null;
        if (lp <= 0 || rp <= 0 || IsUnspecified(r)) return null;
        return new ProcessSocket(p, l, lp, r, rp, time, source);
    }

    /// <summary>"TCPv4", "TCP", "6", "tcp" to "tcp"; "UDPv6", "17" to "udp"; anything else empty.</summary>
    public static string NormProto(string? proto)
    {
        var s = (proto ?? "").Trim().ToLowerInvariant();
        if (s.StartsWith("tcp", StringComparison.Ordinal) || s == "6") return "tcp";
        if (s.StartsWith("udp", StringComparison.Ordinal) || s == "17") return "udp";
        return "";
    }

    /// <summary>Canonical address text; empty for "*", "-", "N/A" and anything that is not an IP address.</summary>
    public static string NormIp(string? ip)
    {
        var s = (ip ?? "").Trim().Trim('[', ']');
        int pct = s.IndexOf('%');
        if (pct > 0) s = s[..pct];
        if (!IPAddress.TryParse(s, out var a)) return "";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return a.ToString().ToLowerInvariant();
    }

    private static bool IsUnspecified(string ip) => ip is "0.0.0.0" or "::";

    public static string TupleKey(string proto, string ipA, int portA, string ipB, int portB)
    {
        var a = ipA + "|" + portA.ToString(CultureInfo.InvariantCulture);
        var b = ipB + "|" + portB.ToString(CultureInfo.InvariantCulture);
        return string.CompareOrdinal(a, b) <= 0 ? proto + "|" + a + "|" + b : proto + "|" + b + "|" + a;
    }
}

/// <summary>
/// Zeek conn / dns / http / ssl CSVs of a host (layout v2 <c>Zeek_Conn_&lt;capture&gt;.csv</c> ..., also the case-wide <c>_Case\Network</c> folder; layout v1 <c>conn.csv</c>),
/// joined to processes on facts only: a conn row links to a process when its exact 5-tuple (either direction) equals one of the process's Exact-linked sockets
/// (<see cref="ProcessSockets"/>), its time is inside the process lifetime (<see cref="ProcessLifetimes.PidFits"/>), and no other process owns that 5-tuple at that time.
/// Only key maps are kept in memory; rows are read from disk on lookup. Call <see cref="Attach"/> once the graph and its sockets are known.
/// </summary>
public sealed class ZeekSocketIndex : IDisposable
{
    public const string SourceId = "zeek";
    public const string SourceNote = "Zeek via socket";
    public const int MaxLinks = 5000;
    private const int MaxDns = 5, MaxHttp = 20, ScanChunk = 2000;

    private enum LogKind { Conn, Dns, Http, Ssl }

    private readonly record struct Ref(int File, long Row, long Ticks);

    private sealed class FileSource
    {
        public CsvIndex Index = null!;
        public LogKind Kind;
        public Dictionary<string, int> Cols = new();
        public int Col(string name) => Cols.TryGetValue(name, out var i) ? i : -1;
    }

    private readonly List<FileSource> _files = new();
    private readonly Dictionary<string, List<Ref>> _conn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Ref>> _dnsByIp = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Ref>> _httpByUid = new(StringComparer.Ordinal), _sslByUid = new(StringComparer.Ordinal);
    private readonly List<string> _found = new();
    private readonly List<string> _warnings = new();
    private Dictionary<string, List<(ProcessNode Node, ProcessSocket Socket)>> _owners = new(StringComparer.Ordinal);
    private ProcessLifetimes? _life;
    private Dictionary<ProcessNode, List<string>> _keysByNode = new(ReferenceEqualityComparer.Instance);   // node -> the conn keys it owns (LinksFor must not scan every key per node)

    private ZeekSocketIndex() { }

    public IReadOnlyList<string> Files => _found;
    public IReadOnlyList<string> Warnings => _warnings;
    /// <summary>NotFound when no Zeek conn CSV was found, NoRows when the conn logs are empty, Available otherwise.</summary>
    public EvidenceStatus Status { get; private set; } = EvidenceStatus.NotFound;
    /// <summary>The <c>zeek</c> entry of <c>Resources/processes/sources.json</c> (label, howToGet).</summary>
    public static EvidenceSourceDef? Source => ProcessEvidenceCatalog.Find(SourceId);
    public string HowToGet => Source?.HowToGet ?? "Ingest › Zeek on a pcap";

    private static LogKind? KindOf(string fileName)
    {
        var def = Source;
        if (def != null && !def.Matches(fileName)) return null;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.StartsWith("Zeek_", StringComparison.OrdinalIgnoreCase)) stem = stem[5..];
        int us = stem.IndexOf('_');
        if (us > 0) stem = stem[..us];
        return stem.ToLowerInvariant() switch
        {
            "conn" => LogKind.Conn, "dns" => LogKind.Dns, "http" => LogKind.Http, "ssl" => LogKind.Ssl, _ => null,
        };
    }

    /// <summary>Zeek CSVs of the host folder, its direct sub folders (not <c>Memory</c>) and the case-wide <c>..\_Case\Network</c> folder; directory listings only.</summary>
    public static List<string> DiscoverFiles(string hostFolder)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(hostFolder) || !Directory.Exists(hostFolder)) return found;
        var dirs = new List<string> { hostFolder };
        try { dirs.AddRange(Directory.GetDirectories(hostFolder).Where(d => !string.Equals(Path.GetFileName(d), "Memory", StringComparison.OrdinalIgnoreCase))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var parent = Path.GetDirectoryName(Path.GetFullPath(hostFolder).TrimEnd('\\', '/'));
        if (parent != null && !string.Equals(Path.GetFileName(hostFolder.TrimEnd('\\', '/')), Rita.RitaJobs.CaseWideHost, StringComparison.OrdinalIgnoreCase))
            dirs.Add(Path.Combine(parent, Rita.RitaJobs.CaseWideHost, Zeek.ZeekJobs.Category));
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(d)) continue;
            string[] csvs;
            try { csvs = Directory.GetFiles(d, "*.csv"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(csvs, StringComparer.OrdinalIgnoreCase);
            found.AddRange(csvs.Where(f => KindOf(Path.GetFileName(f)) != null));
        }
        return found;
    }

    public static Task<ZeekSocketIndex> BuildAsync(string hostFolder, CancellationToken ct) => BuildAsync(DiscoverFiles(hostFolder), ct);

    public static Task<ZeekSocketIndex> BuildAsync(IEnumerable<string> files, CancellationToken ct)
    {
        var list = files.ToList();
        return Task.Run(async () =>
        {
            var idx = new ZeekSocketIndex();
            try
            {
                await idx.LoadAsync(list, ct).ConfigureAwait(false);
                return idx;
            }
            catch
            {
                idx.Dispose();
                throw;
            }
        }, ct);
    }

    private async Task LoadAsync(List<string> files, CancellationToken ct)
    {
        long connRows = 0;
        bool sawConn = false;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (KindOf(Path.GetFileName(f)) is not { } kind) continue;
            _found.Add(f);
            if (kind == LogKind.Conn) sawConn = true;
            CsvIndex? index = null;
            try
            {
                index = await CsvIndex.OpenAsync(f, null, ct).ConfigureAwait(false);
                await index.IndexingCompleted.ConfigureAwait(false);
                if (index.RowCount == 0) { index.Dispose(); continue; }
                var src = new FileSource { Index = index, Kind = kind };
                for (int i = 0; i < index.Headers.Count; i++) src.Cols.TryAdd(ProcessLogText.Norm(index.Headers[i]), i);
                int fileNo = _files.Count;
                _files.Add(src);
                if (kind == LogKind.Conn) connRows += index.RowCount;
                long total = index.RowCount;
                for (long start = 0; start < total; start += ScanChunk)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = index.ReadRows(start, ScanChunk);
                    for (int i = 0; i < rows.Length; i++) AddRow(fileNo, start + i, rows[i], src);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Sylvan.Data.Csv.CsvRecordTooLargeException)
            {
                index?.Dispose();
                _warnings.Add($"Could not read {Path.GetFileName(f)}: {ex.Message}");
            }
        }
        Status = !sawConn ? EvidenceStatus.NotFound : connRows == 0 ? EvidenceStatus.NoRows : EvidenceStatus.Available;
    }

    private void AddRow(int fileNo, long rowNo, string[] row, FileSource src)
    {
        string C(string name) => Cell(row, src.Col(name));
        switch (src.Kind)
        {
            case LogKind.Conn:
            {
                var ticks = TicksOf(row, src);
                var proto = ProcessSockets.NormProto(C("proto"));
                var o = ProcessSockets.NormIp(C("idorigh"));
                var r = ProcessSockets.NormIp(C("idresph"));
                if (ticks == 0 || proto.Length == 0 || o.Length == 0 || r.Length == 0) return;
                if (!int.TryParse(C("idorigp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var op) ||
                    !int.TryParse(C("idrespp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rp)) return;
                Put(_conn, ProcessSockets.TupleKey(proto, o, op, r, rp), new Ref(fileNo, rowNo, ticks));
                break;
            }
            case LogKind.Dns:
            {
                var ticks = TicksOf(row, src);
                if (ticks == 0) return;
                HashSet<string>? seen = null;
                foreach (var a in C("answers").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var ip = ProcessSockets.NormIp(a);
                    if (ip.Length > 0 && (seen ??= new HashSet<string>()).Add(ip)) Put(_dnsByIp, ip, new Ref(fileNo, rowNo, ticks));
                }
                break;
            }
            case LogKind.Http:
            case LogKind.Ssl:
            {
                var uid = C("uid");
                if (uid.Length > 0) Put(src.Kind == LogKind.Http ? _httpByUid : _sslByUid, uid, new Ref(fileNo, rowNo, 0));
                break;
            }
        }
    }

    private static void Put(Dictionary<string, List<Ref>> map, string key, Ref r)
    {
        if (!map.TryGetValue(key, out var l)) map[key] = l = new List<Ref>();
        l.Add(r);
    }

    private static string Cell(string[] row, int col) => col >= 0 && col < row.Length ? (row[col] ?? "").Trim() : "";

    /// <summary>The row time: the converter's <c>DateTimeUtc</c> column, else <c>ts</c> (epoch seconds, or ISO text in JSON logs).</summary>
    private static long TicksOf(string[] row, FileSource src)
    {
        var iso = Cell(row, src.Col("datetimeutc"));
        if (iso.Length > 0 && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) return t.Ticks;
        var ts = Cell(row, src.Col("ts"));
        if (ts.Length == 0) return 0;
        var formatted = Zeek.ZeekLogConverter.FormatEpoch(ts);
        if (formatted.Length > 0 && DateTime.TryParse(formatted, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) return t.Ticks;
        return DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t) ? t.Ticks : 0;
    }

    // ------------------------------------------------------------------ join

    /// <summary>
    /// Gives the index every process instance and its sockets (<paramref name="socketsOf"/>, usually <see cref="ProcessSockets.For"/>). A 5-tuple owned by several
    /// instances whose lifetimes cover a connection's time links that connection to none of them.
    /// </summary>
    public void Attach(IEnumerable<ProcessNode> nodes, Func<ProcessNode, IEnumerable<ProcessSocket>> socketsOf, DateTime? evidenceEnd = null)
    {
        var all = nodes.ToList();
        _life = new ProcessLifetimes(all, evidenceEnd);
        var owners = new Dictionary<string, List<(ProcessNode, ProcessSocket)>>(StringComparer.Ordinal);
        var keysByNode = new Dictionary<ProcessNode, List<string>>(ReferenceEqualityComparer.Instance);
        if (_conn.Count == 0) { _owners = owners; _keysByNode = keysByNode; return; }
        foreach (var n in all)
            foreach (var s in socketsOf(n))
            {
                var key = s.Key;
                if (!_conn.ContainsKey(key)) continue;
                if (!owners.TryGetValue(key, out var l)) owners[key] = l = new List<(ProcessNode, ProcessSocket)>();
                if (!l.Any(x => ReferenceEquals(x.Item1, n)))
                {
                    l.Add((n, s));
                    if (!keysByNode.TryGetValue(n, out var ks)) keysByNode[n] = ks = new List<string>();
                    ks.Add(key);
                }
            }
        _owners = owners;
        _keysByNode = keysByNode;
    }

    /// <summary>
    /// <see cref="Attach(IEnumerable{ProcessNode}, Func{ProcessNode, IEnumerable{ProcessSocket}}, DateTime?)"/> with the sockets of <see cref="ProcessSockets.For"/>: netscan /
    /// netstat from <paramref name="facets"/> (null: none) and Sysmon 3 / 5156 from the graph's event index.
    /// </summary>
    public void Attach(ProcessGraph graph, ProcessFacetIndex? facets, DateTime? evidenceEnd = null)
    {
        var life = new ProcessLifetimes(graph.AllNodes, evidenceEnd);
        Attach(graph.AllNodes, n => ProcessSockets.For(n, facets, graph.Evidence.Events, life), evidenceEnd);
    }

    /// <summary>Zeek connections linked to the process through its sockets, oldest first, each with its dns / http / ssl enrichment. Needs <see cref="Attach"/>.</summary>
    public IReadOnlyList<ZeekSocketLink> LinksFor(ProcessNode node)
    {
        var links = new List<ZeekSocketLink>();
        var life = _life;
        if (life == null) return links;
        if (!_keysByNode.TryGetValue(node, out var myKeys)) return links;
        foreach (var key in myKeys)
        {
            var owners = _owners[key];
            int mine = owners.FindIndex(o => ReferenceEquals(o.Node, node));
            if (mine < 0) continue;
            var socket = owners[mine].Socket;
            foreach (var r in _conn[key])
            {
                var t = new DateTime(r.Ticks, DateTimeKind.Utc);
                if (!life.PidFits(node, t)) continue;
                if (owners.Any(o => !ReferenceEquals(o.Node, node) && life.PidFits(o.Node, t))) continue;   // two owners at that time: not linked
                var conn = ReadConn(r);
                if (conn == null) continue;
                links.Add(new ZeekSocketLink(socket, conn, DnsFor(node, conn, life), HttpFor(conn.Uid), SslFor(conn.Uid)));
                if (links.Count >= MaxLinks) break;
            }
        }
        return links.OrderBy(l => l.Conn.Time).ThenBy(l => l.Conn.Uid, StringComparer.Ordinal).ToList();
    }

    private string[]? Read(Ref r)
    {
        try { return _files[r.File].Index.ReadRow(r.Row); }
        catch (ObjectDisposedException) { return null; }
    }

    private ZeekConn? ReadConn(Ref r)
    {
        var row = Read(r);
        if (row == null) return null;
        var f = _files[r.File];
        string C(string n) => Cell(row, f.Col(n));
        int.TryParse(C("idorigp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var op);
        int.TryParse(C("idrespp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rp);
        return new ZeekConn(new DateTime(r.Ticks, DateTimeKind.Utc), C("uid"), C("proto"), C("idorigh"), op, C("idresph"), rp, C("service"), C("duration"),
            C("origbytes"), C("respbytes"), C("connstate"), Path.GetFileName(f.Index.Path));
    }

    /// <summary>dns.log rows answering with the responder IP, inside the process lifetime and not after the connection; the latest <see cref="MaxDns"/>.</summary>
    private IReadOnlyList<ZeekDnsAnswer> DnsFor(ProcessNode node, ZeekConn conn, ProcessLifetimes life)
    {
        var ip = ProcessSockets.NormIp(conn.RespIp);
        if (ip.Length == 0 || !_dnsByIp.TryGetValue(ip, out var refs)) return Array.Empty<ZeekDnsAnswer>();
        var res = new List<ZeekDnsAnswer>();
        foreach (var r in refs.Where(r => r.Ticks <= conn.Time.Ticks).OrderByDescending(r => r.Ticks))
        {
            var t = new DateTime(r.Ticks, DateTimeKind.Utc);
            if (node.CreateTime != null && t < node.CreateTime.Value.AddSeconds(-ProcessLifetimes.SlackSeconds)) break;
            if (!life.PidFits(node, t)) continue;
            var row = Read(r);
            if (row == null) continue;
            var f = _files[r.File];
            res.Add(new ZeekDnsAnswer(t, Cell(row, f.Col("query")), Cell(row, f.Col("answers")), Path.GetFileName(f.Index.Path)));
            if (res.Count >= MaxDns) break;
        }
        return res;
    }

    private IReadOnlyList<ZeekHttpRequest> HttpFor(string uid)
    {
        if (uid.Length == 0 || !_httpByUid.TryGetValue(uid, out var refs)) return Array.Empty<ZeekHttpRequest>();
        var res = new List<ZeekHttpRequest>();
        foreach (var r in refs.Take(MaxHttp))
        {
            var row = Read(r);
            if (row == null) continue;
            var f = _files[r.File];
            res.Add(new ZeekHttpRequest(Cell(row, f.Col("method")), Cell(row, f.Col("host")), Cell(row, f.Col("uri")), Cell(row, f.Col("useragent"))));
        }
        return res;
    }

    private string SslFor(string uid)
    {
        if (uid.Length == 0 || !_sslByUid.TryGetValue(uid, out var refs)) return "";
        foreach (var r in refs)
        {
            var row = Read(r);
            if (row == null) continue;
            var name = Cell(row, _files[r.File].Col("servername"));
            if (name.Length > 0) return name;
        }
        return "";
    }

    public void Dispose()
    {
        foreach (var f in _files) f.Index.Dispose();
        _files.Clear();
        _conn.Clear(); _dnsByIp.Clear(); _httpByUid.Clear(); _sslByUid.Clear();
        _owners = new Dictionary<string, List<(ProcessNode, ProcessSocket)>>(StringComparer.Ordinal);
    }
}
