using System.Globalization;
using EzForensics.Core.Results;

namespace EzForensics.Core.Processes;

/// <summary>Tabs of the process details card, in rail order.</summary>
public enum ProcessTab { Overview, Timeline, Network, Files, Registry, Modules, Threats, Scripts, Identity, Persistence, Execution, Sources }

/// <summary>Where a row lives: its CSV and an Analyze grid filter that finds it there ("Open rows in grid").</summary>
public sealed record ProcessRowOrigin(string CsvPath, FilterNode Filter);

/// <summary>
/// One row of the details card. <see cref="Tier"/> is how the row is tied to the process (null for rows that are no link, e.g. the Sources tab); <see cref="Source"/> is the
/// source chip ("windows.handles", "Sysmon 3", "Zeek via socket", "Prefetch"). <see cref="Cells"/> line up with the headers of its <see cref="ProcessRowSet"/>.
/// </summary>
public sealed record ProcessActivityRow(DateTime? Time, LinkTier? Tier, string Source, string[] Cells, ProcessRowOrigin? Origin = null)
{
    public string TierText => TierLabel(Tier);
    public bool HasTier => Tier != null;

    public static string TierLabel(LinkTier? t) => t switch
    {
        LinkTier.Exact => "Exact",
        LinkTier.PathTime => "Path + time",
        _ => "",
    };
}

/// <summary>Rows of one source for one process in one tab (a sub-tab of the card). <see cref="Rows"/> is capped; <see cref="TotalRows"/> is the real count.</summary>
public sealed class ProcessRowSet
{
    /// <summary>Stable key ("mem.Dlls", "evt.Network", "zeek", "prefetch", "timeline", "sources").</summary>
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public ProcessTab Tab { get; init; }
    /// <summary>NotFound: the source was not collected (or the process is not in it); NoRows: it was, without rows for this process.</summary>
    public EvidenceStatus Status { get; init; }
    /// <summary>Why there are no rows, with how to get the source; empty when there are rows.</summary>
    public string Message { get; init; } = "";
    /// <summary>"memory (windows.handles)", "Event logs", ... for the note under the grid.</summary>
    public string SourceLabel { get; init; } = "";
    /// <summary>Extra note (e.g. how many rows had no time and stay out of the timeline).</summary>
    public string Remark { get; init; } = "";
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProcessActivityRow> Rows { get; init; } = Array.Empty<ProcessActivityRow>();
    public int TotalRows { get; init; }
    public bool Truncated => TotalRows > Rows.Count;
    public bool HasRows => Rows.Count > 0;
}

/// <summary>
/// What the process details card and the tree's service chips read for one process. The App reads only <see cref="ProcessActivityStore"/> (the correlator's files);
/// <see cref="ProcessActivity"/> is the live engine the <see cref="ProcessCorrelator"/> writes those files from. Implementations read from disk: call off the UI thread.
/// </summary>
public interface IProcessDetailSource
{
    /// <summary>Row sets of one data tab (<see cref="ProcessActivity.DataTabs"/>), in display order.</summary>
    IReadOnlyList<ProcessRowSet> SetsFor(ProcessNode node, ProcessTab tab);
    /// <summary>Every dated row of the data tabs, merged and sorted by time (<see cref="ProcessActivity.MergeTimeline"/>).</summary>
    ProcessRowSet Timeline(ProcessNode node);
    /// <summary>The Sources tab: one row per source (found / missing / no rows, rows for the process, how to get it).</summary>
    ProcessRowSet Sources(ProcessNode node);
    /// <summary>Linked rows per tab (every <see cref="ProcessTab"/> present; Overview 0).</summary>
    Dictionary<ProcessTab, int> Counts(ProcessNode node);
    /// <summary>The Overview Signature text (Sysmon 7 of the process's own image), null without one.</summary>
    string? SignatureText(ProcessNode node);
    /// <summary>Services svcscan lists for the process (the "3 services" chip), at most <paramref name="cap"/> names.</summary>
    IReadOnlyList<string> ServiceNames(ProcessNode node, int cap = 20);
}

/// <summary>
/// Everything linked to one process, per tab of the details card: memory facets (same PID of the same image, memory nodes only), event-log links
/// (<see cref="ProcessEventIndex.LinksFor"/>), Zeek through the process's sockets, Prefetch by path + run time, and the generic catalog sources. Reads from disk:
/// call off the UI thread. The indexes may be null while they are still being built; the affected sets then say so.
/// </summary>
public sealed class ProcessActivity : IProcessDetailSource
{
    public const int MaxRows = ProcessFacetIndex.MaxRows;
    /// <summary>Status cell of a Sources row whose source has rows for the process.</summary>
    public const string FoundStatus = "found";
    private const int SummaryMax = 300;

    public ProcessActivity(ProcessGraph graph, ProcessFacetIndex? facets = null, ZeekSocketIndex? zeek = null, ProcessEvidenceIndex? evidence = null)
    {
        Graph = graph;
        Facets = facets;
        Zeek = zeek;
        EvidenceIndex = evidence;
        _lookup = new Lazy<(HashSet<ProcessNode>, Dictionary<string, ProcessNode>)>(() =>
        {
            var members = new HashSet<ProcessNode>(graph.AllNodes, ReferenceEqualityComparer.Instance);
            var byKey = new Dictionary<string, ProcessNode>(StringComparer.Ordinal);
            foreach (var n in graph.AllNodes) byKey.TryAdd(n.Key, n);
            return (members, byKey);
        });
    }

    public ProcessGraph Graph { get; }

    /// <summary>
    /// The node of <see cref="Graph"/> for <paramref name="node"/>: itself when it belongs to the graph, else the graph's node with the same <see cref="ProcessNode.Key"/> (the
    /// Analyze tree may draw nodes loaded from <c>Processes.csv</c> while the details read the live graph); <paramref name="node"/> when the graph has no such key.
    /// </summary>
    public ProcessNode Resolve(ProcessNode node)
    {
        var (members, byKey) = _lookup.Value;
        return members.Contains(node) ? node : byKey.TryGetValue(node.Key, out var live) ? live : node;
    }

    private readonly Lazy<(HashSet<ProcessNode> Members, Dictionary<string, ProcessNode> ByKey)> _lookup;

    /// <summary>The Sysmon 7 signature of the process's own image (see <see cref="ProcessEventIndex.SignatureFor"/>).</summary>
    public ProcessSignature? SignatureFor(ProcessNode node) => Events.SignatureFor(Resolve(node));

    public string? SignatureText(ProcessNode node) => node.IsGhost ? null : ProcessSignature.Describe(SignatureFor(node));

    /// <summary>Services svcscan lists for the process (memory nodes only; the node chip "3 services"), at most <paramref name="cap"/> names.</summary>
    public IReadOnlyList<string> ServiceNames(ProcessNode node, int cap = 20)
    {
        if (Facets == null || node.IsGhost) return Array.Empty<string>();
        var live = Resolve(node);
        if (!ProcessGraphBuilder.InMemory(live)) return Array.Empty<string>();
        try { return Facets.ServiceNames(live.Pid, cap); }
        catch (ObjectDisposedException) { return Array.Empty<string>(); }
    }

    /// <summary>Number of services svcscan lists for the process (no names read); 0 for non-memory nodes.</summary>
    public int ServiceCount(ProcessNode node)
    {
        if (Facets == null || node.IsGhost) return 0;
        var live = Resolve(node);
        return ProcessGraphBuilder.InMemory(live) ? Facets.Count(ProcessFacetKind.Services, live.Pid) : 0;
    }

    public ProcessFacetIndex? Facets { get; }
    public ZeekSocketIndex? Zeek { get; }
    public ProcessEvidenceIndex? EvidenceIndex { get; }
    private ProcessEventIndex Events => Graph.Evidence.Events;

    /// <summary>Tabs whose rows come from sources (not Overview / Timeline / Sources).</summary>
    public static readonly IReadOnlyList<ProcessTab> DataTabs = new[]
    {
        ProcessTab.Network, ProcessTab.Files, ProcessTab.Registry, ProcessTab.Modules, ProcessTab.Threats, ProcessTab.Scripts, ProcessTab.Identity,
        ProcessTab.Persistence, ProcessTab.Execution,
    };

    public static string TabTitle(ProcessTab t) => t switch
    {
        ProcessTab.Scripts => "Scripts & console",
        _ => t.ToString(),
    };

    /// <summary>Memory facets per tab, in display order (Network sockets and the handles held to this process are built separately).</summary>
    private static readonly (ProcessTab Tab, ProcessFacetKind Kind, string Title)[] MemorySets =
    {
        (ProcessTab.Files, ProcessFacetKind.HandlesFiles, "File handles"),
        (ProcessTab.Registry, ProcessFacetKind.HandlesRegistry, "Registry handles"),
        (ProcessTab.Modules, ProcessFacetKind.Dlls, "DLLs (dlllist)"),
        (ProcessTab.Modules, ProcessFacetKind.LdrModules, "Ldr modules"),
        (ProcessTab.Modules, ProcessFacetKind.VersionInfo, "Version info (verinfo)"),
        (ProcessTab.Threats, ProcessFacetKind.Injected, "Injected code (malfind)"),
        (ProcessTab.Threats, ProcessFacetKind.Hollowing, "Hollowing"),
        (ProcessTab.Threats, ProcessFacetKind.Ghosting, "Ghosting"),
        (ProcessTab.Threats, ProcessFacetKind.SuspiciousThreads, "Suspicious threads"),
        (ProcessTab.Threats, ProcessFacetKind.PebMasquerade, "PEB masquerade"),
        (ProcessTab.Threats, ProcessFacetKind.HandlesProcess, "Process handles held"),
        (ProcessTab.Threats, ProcessFacetKind.HandlesThread, "Thread handles held"),
        (ProcessTab.Threats, ProcessFacetKind.HandlesMutant, "Mutants"),
        (ProcessTab.Threats, ProcessFacetKind.Threads, "Threads"),
        (ProcessTab.Scripts, ProcessFacetKind.ConsoleHistory, "Console history"),
        (ProcessTab.Scripts, ProcessFacetKind.CmdLine, "Command line"),
        (ProcessTab.Identity, ProcessFacetKind.Sids, "SIDs"),
        (ProcessTab.Identity, ProcessFacetKind.Privileges, "Privileges"),
        (ProcessTab.Identity, ProcessFacetKind.Sessions, "Sessions"),
        (ProcessTab.Identity, ProcessFacetKind.EnvVars, "Environment"),
        (ProcessTab.Identity, ProcessFacetKind.ServiceSids, "Service SIDs"),
        (ProcessTab.Persistence, ProcessFacetKind.Services, "Services (svcscan)"),
        (ProcessTab.Persistence, ProcessFacetKind.JobLinks, "Job links"),
    };

    /// <summary>Event group shown in each tab.</summary>
    private static readonly Dictionary<ProcessTab, ProcessEventGroup> EventGroups = new()
    {
        [ProcessTab.Network] = ProcessEventGroup.Network, [ProcessTab.Files] = ProcessEventGroup.Files, [ProcessTab.Registry] = ProcessEventGroup.Registry,
        [ProcessTab.Modules] = ProcessEventGroup.Modules, [ProcessTab.Threats] = ProcessEventGroup.Threats, [ProcessTab.Scripts] = ProcessEventGroup.Scripts,
        [ProcessTab.Identity] = ProcessEventGroup.Identity, [ProcessTab.Persistence] = ProcessEventGroup.Persistence, [ProcessTab.Execution] = ProcessEventGroup.Process,
    };

    /// <summary>Memory facets whose rows carry a time (for the Timeline count).</summary>
    private static readonly ProcessFacetKind[] TimedKinds = { ProcessFacetKind.Network, ProcessFacetKind.Dlls, ProcessFacetKind.Threads, ProcessFacetKind.Sessions };
    private static readonly string[] TimeCols = { "created", "createtime", "creationtime", "loadtime", "starttime", "timestamp" };
    /// <summary>Columns left out of a row summary (they repeat the process itself).</summary>
    private static readonly HashSet<string> SummarySkip = new(StringComparer.Ordinal)
    {
        "pid", "ppid", "processid", "owningpid", "process", "imagefilename", "owner", "offset", "offsetv", "offsetp", "treedepth", "fileoutput", "image", "time", "event",
    };

    public static string EventTabTitle(ProcessEventGroup g) => g switch
    {
        ProcessEventGroup.Network => "Events (Sysmon 3 / 22, 5156)",
        ProcessEventGroup.Files => "Events (Sysmon 11 / 23 / 26 / 2 / 15, 4663)",
        ProcessEventGroup.Registry => "Events (Sysmon 12 / 13 / 14, 4657)",
        ProcessEventGroup.Modules => "Events (Sysmon 7)",
        ProcessEventGroup.Threats => "Events (Sysmon 8 / 10, Defender)",
        ProcessEventGroup.Scripts => "Events (PowerShell)",
        ProcessEventGroup.Identity => "Events (4624 / 4648)",
        ProcessEventGroup.Persistence => "Events (Task Scheduler, WMI)",
        _ => "Process events (4688 / 4689, Sysmon 1 / 5)",
    };

    // ------------------------------------------------------------------ tabs

    /// <summary>Row sets of one data tab (see <see cref="DataTabs"/>); Timeline and Sources have their own methods, Overview has none.</summary>
    public IReadOnlyList<ProcessRowSet> SetsFor(ProcessNode node, ProcessTab tab)
    {
        if (node.IsGhost) return Array.Empty<ProcessRowSet>();   // a ghost parent has no evidence of its own (its PID may belong to a later, unrelated process)
        node = Resolve(node);
        var sets = new List<ProcessRowSet>();
        IReadOnlyList<ZeekSocketLink>? zeekLinks = null;
        IReadOnlyList<ZeekSocketLink> ZeekLinks() => zeekLinks ??= Zeek?.LinksFor(node) ?? Array.Empty<ZeekSocketLink>();

        if (tab == ProcessTab.Network) sets.Add(MemorySockets(node, ZeekLinks()));
        if (tab == ProcessTab.Execution) sets.Add(Prefetch(node));
        foreach (var (t, kind, title) in MemorySets)
        {
            if (t != tab) continue;
            sets.Add(Memory(node, kind, title));
            if (kind == ProcessFacetKind.HandlesThread) sets.Add(HandlesTo(node));
        }
        if (EventGroups.TryGetValue(tab, out var g)) sets.Add(EventSet(node, tab, g, tab == ProcessTab.Network ? ZeekLinks() : null));
        if (tab == ProcessTab.Network) sets.Add(ZeekSet(node, ZeekLinks()));
        sets.AddRange(CatalogSets(node, tab));
        return sets;
    }

    /// <summary>Every dated row of the data tabs, merged and sorted by time.</summary>
    public ProcessRowSet Timeline(ProcessNode node) => MergeTimeline(DataTabs.SelectMany(t => SetsFor(node, t)), MaxRows);

    /// <summary>
    /// The timeline of <paramref name="sets"/>: each row with a time becomes one row (Time, Tab, What, Detail) keeping its tier, source chip and origin, oldest first (rows
    /// of the same time keep the set order). At most <paramref name="cap"/> rows are kept; <see cref="ProcessRowSet.TotalRows"/> counts them all.
    /// </summary>
    public static ProcessRowSet MergeTimeline(IEnumerable<ProcessRowSet> sets, int cap = MaxRows)
    {
        var dated = new List<(DateTime Time, int Order, ProcessActivityRow Row)>();
        int order = 0, undated = 0;
        foreach (var s in sets)
            foreach (var r in s.Rows)
            {
                if (r.Time is not { } t) { undated++; continue; }
                dated.Add((t, order++, new ProcessActivityRow(t, r.Tier, r.Source,
                    new[] { FormatTime(t), TabTitle(s.Tab), s.Title, Summarize(s.Headers, r.Cells) }, r.Origin)));
            }
        dated.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Order.CompareTo(b.Order));
        return new ProcessRowSet
        {
            Id = "timeline", Title = "Timeline", Tab = ProcessTab.Timeline, SourceLabel = "every linked source",
            Status = dated.Count == 0 ? EvidenceStatus.NoRows : EvidenceStatus.Available,
            Message = dated.Count == 0 ? "No linked row with a time for this process." : "",
            Remark = undated > 0 ? undated.ToString("N0", CultureInfo.InvariantCulture) + " linked rows have no time and are only in their tabs." : "",
            Headers = new[] { "Time", "Tab", "What", "Detail" },
            Rows = dated.Take(cap).Select(d => d.Row).ToList(), TotalRows = dated.Count,
        };
    }

    /// <summary>First and last time of the timeline rows (null when none).</summary>
    public static (DateTime? First, DateTime? Last) Seen(ProcessRowSet timeline)
    {
        DateTime? first = null, last = null;
        foreach (var r in timeline.Rows)
        {
            if (r.Time is not { } t) continue;
            if (first == null || t < first) first = t;
            if (last == null || t > last) last = t;
        }
        return (first, last);
    }

    /// <summary>
    /// Linked rows per tab without reading them where the index can count (memory facets, event groups); Timeline is the sum of the dated sources (memory rows of
    /// timed plugins are counted even when a row lacks its time), Sources the number of sources with rows for the process, Overview 0.
    /// </summary>
    public Dictionary<ProcessTab, int> Counts(ProcessNode node)
    {
        if (node.IsGhost) return Enum.GetValues<ProcessTab>().ToDictionary(t => t, _ => 0);
        node = Resolve(node);
        var c = Enum.GetValues<ProcessTab>().ToDictionary(t => t, _ => 0);
        bool mem = Facets != null && ProcessGraphBuilder.InMemory(node);
        int Mem(ProcessFacetKind k) => mem ? Facets!.Count(k, node.Pid) : 0;

        c[ProcessTab.Network] += Mem(ProcessFacetKind.Network);
        foreach (var (t, kind, _) in MemorySets) c[t] += Mem(kind);
        if (mem) c[ProcessTab.Threats] += Facets!.HandlesTo(node.Pid).TotalRows;

        var groups = Events.GroupCounts(node);
        foreach (var (t, g) in EventGroups) c[t] += groups.GetValueOrDefault(g);
        int zeek = Zeek?.LinksFor(node).Count ?? 0;
        c[ProcessTab.Network] += zeek;
        int prefetch = Graph.Evidence.Disk.FindPrefetch(node).Count;
        c[ProcessTab.Execution] += prefetch;
        if (EvidenceIndex != null)
            foreach (var st in EvidenceIndex.Statuses.Where(s => s.Source.IsCsv && !IsPrefetch(s.Source) && s.Status == EvidenceStatus.Available))
                c[TabOf(st.Source.Tab)] += EvidenceIndex.Lookup(node, st.Source.Id).TotalRows;

        c[ProcessTab.Timeline] = groups.Values.Sum() + zeek + prefetch + TimedKinds.Sum(Mem);
        c[ProcessTab.Sources] = Sources(node).Rows.Count(r => r.Cells[1] == FoundStatus);
        return c;
    }

    // ------------------------------------------------------------------ memory

    private static string PluginName(string pluginId) => EzForensics.Core.Memory.MemoryPlugins.Find(pluginId)?.Plugin ?? "windows." + pluginId;

    private ProcessRowSet? MemoryUnavailable(ProcessNode node, string id, ProcessTab tab, string title, string pluginId)
    {
        var plugin = PluginName(pluginId);
        if (!ProcessGraphBuilder.InMemory(node))
            return new ProcessRowSet { Id = id, Tab = tab, Title = title, Status = EvidenceStatus.NotFound, SourceLabel = "memory (" + plugin + ")",
                Message = "Not in the memory image (this process is seen in the event logs only)." };
        if (Facets == null)
            return new ProcessRowSet { Id = id, Tab = tab, Title = title, Status = EvidenceStatus.NotFound, SourceLabel = "memory (" + plugin + ")",
                Message = "Indexing the Volatility CSVs…" };
        return null;
    }

    private static string NotRunMessage(string plugin) => $"{plugin} not run for this image — run it from Tools › Volatility.";

    private ProcessRowSet Memory(ProcessNode node, ProcessFacetKind kind, string title)
    {
        var pluginId = ProcessFacetIndex.PluginIdOf(kind);
        var id = "mem." + kind;
        var tab = MemorySets.First(m => m.Kind == kind).Tab;
        if (MemoryUnavailable(node, id, tab, title, pluginId) is { } none) return none;
        var r = SafeGet(() => Facets!.Get(kind, node.Pid), pluginId);
        return FromFacet(id, tab, title, r, PluginName(pluginId), MemoryOrigin(kind, node.Pid, r.Headers));
    }

    private ProcessRowSet HandlesTo(ProcessNode node)
    {
        const string id = "mem.HandlesTo", title = "Handles to this process";
        if (MemoryUnavailable(node, id, ProcessTab.Threats, title, "handles") is { } none) return none;
        var r = SafeGet(() => Facets!.HandlesTo(node.Pid), "handles");
        var path = Facets!.PathsOf(ProcessFacetKind.HandlesProcess).FirstOrDefault() ?? "";
        int nameCol = IndexOf(r.Headers, "name");
        var nameHeader = nameCol >= 0 ? r.Headers[nameCol] : "Name";
        var pid = node.Pid.ToString(CultureInfo.InvariantCulture);
        Func<string[], ProcessRowOrigin?> origin = _ => path.Length == 0 ? null
            : new ProcessRowOrigin(path, new FilterCondition(nameHeader, ColumnFilterMode.EndsWith, " Pid " + pid));
        return FromFacet(id, ProcessTab.Threats, title, r, "windows.handles", origin, "No other process holds a handle to this one.");
    }

    private static FacetRows SafeGet(Func<FacetRows> get, string pluginId)
    {
        try { return get(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return FacetRows.NotRun(pluginId); }
    }

    /// <summary>Origin of a memory row: the facet's CSV filtered on the row's own PID cell (consoles rows of an attached process carry the conhost PID), getservicesids by Service.</summary>
    private Func<string[], ProcessRowOrigin?> MemoryOrigin(ProcessFacetKind kind, int pid, IReadOnlyList<string> headers)
    {
        if (Facets == null) return _ => null;
        if (kind == ProcessFacetKind.ServiceSids)
        {
            var p = Facets.PathsOf(kind).FirstOrDefault() ?? "";
            int sc = IndexOf(headers, "service");
            if (p.Length == 0 || sc < 0) return _ => null;
            return row => Cell(row, sc) is { Length: > 0 } svc ? new ProcessRowOrigin(p, new FilterCondition(headers[sc], ColumnFilterMode.Equals, svc)) : null;
        }
        IReadOnlyList<(string Path, string PidColumn)> files;
        try { files = Facets.FilesFor(kind, pid); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return _ => null; }
        if (files.Count == 0) return _ => null;
        var (path, pidColumn) = files[0];
        int pc = headers.ToList().IndexOf(pidColumn);
        var fallback = pid.ToString(CultureInfo.InvariantCulture);
        return row => new ProcessRowOrigin(path, new FilterCondition(pidColumn, ColumnFilterMode.Equals, pc >= 0 && Cell(row, pc).Length > 0 ? Cell(row, pc) : fallback));
    }

    private static ProcessRowSet FromFacet(string id, ProcessTab tab, string title, FacetRows r, string plugin, Func<string[], ProcessRowOrigin?> origin, string? noRows = null)
    {
        var label = "memory (" + plugin + ")";
        if (r.Status == FacetStatus.PluginNotRun)
            return new ProcessRowSet { Id = id, Tab = tab, Title = title, Status = EvidenceStatus.NotFound, SourceLabel = label, Message = NotRunMessage(plugin) };
        if (r.Status == FacetStatus.NoRows || r.Rows.Count == 0)
            return new ProcessRowSet { Id = id, Tab = tab, Title = title, Status = EvidenceStatus.NoRows, SourceLabel = label, Message = noRows ?? $"No rows for this process in {plugin}." };
        int tc = TimeCol(r.Headers);
        var rows = r.Rows.Select(c => new ProcessActivityRow(tc >= 0 ? ParseTime(Cell(c, tc)) : null, LinkTier.Exact, plugin, c, origin(c))).ToList();
        return new ProcessRowSet { Id = id, Tab = tab, Title = title, Status = EvidenceStatus.Available, SourceLabel = label, Headers = r.Headers, Rows = rows, TotalRows = r.TotalRows };
    }

    /// <summary>netscan / netstat rows of the PID with the Zeek connections of each socket inline.</summary>
    private ProcessRowSet MemorySockets(ProcessNode node, IReadOnlyList<ZeekSocketLink> zeek)
    {
        const string id = "mem.Network", title = "Sockets (netscan / netstat)";
        if (MemoryUnavailable(node, id, ProcessTab.Network, title, "netscan") is { } none) return none;
        var r = SafeGet(() => Facets!.Get(ProcessFacetKind.Network, node.Pid), "netscan");
        var set = FromFacet(id, ProcessTab.Network, title, r, PluginName("netscan"), MemoryOrigin(ProcessFacetKind.Network, node.Pid, r.Headers));
        if (!set.HasRows) return set;
        var bySocket = ZeekBySocket(zeek);
        var read = ProcessSockets.MemoryRowReader(r.Headers);
        var rows = set.Rows.Select(x => x with { Cells = x.Cells.Concat(ZeekCells(read(x.Cells).Socket, bySocket)).ToArray() }).ToList();
        return new ProcessRowSet
        {
            Id = id, Tab = set.Tab, Title = title, Status = set.Status, SourceLabel = set.SourceLabel, Headers = set.Headers.Concat(ZeekHeaders).ToList(), Rows = rows,
            TotalRows = set.TotalRows, Remark = Zeek == null ? "" : "Zeek columns: connections with this socket's exact 5-tuple inside the process lifetime.",
        };
    }

    // ------------------------------------------------------------------ events

    private static readonly string[] ZeekHeaders = { "Zeek", "DNS (Zeek)", "TLS / HTTP host" };

    private static Dictionary<string, List<ZeekSocketLink>> ZeekBySocket(IReadOnlyList<ZeekSocketLink> zeek)
    {
        var d = new Dictionary<string, List<ZeekSocketLink>>(StringComparer.Ordinal);
        foreach (var l in zeek)
        {
            if (!d.TryGetValue(l.Socket.Key, out var list)) d[l.Socket.Key] = list = new List<ZeekSocketLink>();
            list.Add(l);
        }
        return d;
    }

    private static string[] ZeekCells(ProcessSocket? s, Dictionary<string, List<ZeekSocketLink>> bySocket)
    {
        if (s == null || !bySocket.TryGetValue(s.Key, out var links)) return new[] { "", "", "" };
        long orig = links.Sum(l => Num(l.Conn.OrigBytes)), resp = links.Sum(l => Num(l.Conn.RespBytes));
        var service = string.Join(", ", links.Select(l => l.Conn.Service).Where(v => v.Length > 0 && v != "-").Distinct(StringComparer.OrdinalIgnoreCase));
        var inv = CultureInfo.InvariantCulture;
        var conn = string.Format(inv, "{0:N0} conn{1} · {2:N0} / {3:N0} bytes", links.Count, links.Count == 1 ? "" : "s", orig, resp) + (service.Length > 0 ? " · " + service : "");
        var dns = string.Join(", ", links.SelectMany(l => l.Dns).Select(d => d.Query).Where(q => q.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        var host = string.Join(", ", links.Select(l => l.SslServerName).Concat(links.SelectMany(l => l.Http).Select(h => h.Host))
            .Where(h => h.Length > 0 && h != "-").Distinct(StringComparer.OrdinalIgnoreCase));
        return new[] { conn, dns, host };
    }

    private static long Num(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private ProcessRowSet EventSet(ProcessNode node, ProcessTab tab, ProcessEventGroup group, IReadOnlyList<ZeekSocketLink>? zeek)
    {
        var id = "evt." + group;
        var title = EventTabTitle(group);
        const string label = "Event logs (EvtxECmd)";
        if (!Events.HasFiles)
            return new ProcessRowSet { Id = id, Tab = tab, Title = title, Status = EvidenceStatus.NotFound, SourceLabel = label,
                Message = "No event log CSV (EvtxECmd) for this host — run Event logs from Ingest." };
        var links = Events.LinksFor(node, group);
        if (links.Count == 0)
            return new ProcessRowSet { Id = id, Tab = tab, Title = title, Status = EvidenceStatus.NoRows, SourceLabel = label,
                Message = "No " + group.ToString().ToLowerInvariant() + " events linked to this process." };
        var (headers, project) = EventProjection(group);
        var bySocket = zeek == null ? null : ZeekBySocket(zeek);
        if (bySocket != null) headers = headers.Concat(ZeekHeaders).ToArray();
        var rows = links.Take(MaxRows).Select(l =>
        {
            var cells = project(l);
            if (bySocket != null) cells = cells.Concat(ZeekCells(ProcessSockets.OfEvent(l.Event), bySocket)).ToArray();
            return new ProcessActivityRow(l.Event.Time, l.Tier, l.Event.Source, cells, EventOrigin(l.Event));
        }).ToList();
        return new ProcessRowSet { Id = id, Tab = tab, Title = title, Status = EvidenceStatus.Available, SourceLabel = label, Headers = headers, Rows = rows, TotalRows = links.Count };
    }

    /// <summary>Headers and cells of an event row per group (the meaning of T1-T4 is set by <see cref="ProcessEventRecord"/>).</summary>
    public static (string[] Headers, Func<ProcessEventLink, string[]> Project) EventProjection(ProcessEventGroup g)
    {
        static string T(ProcessEventRecord e) => FormatTime(e.Time);
        return g switch
        {
            ProcessEventGroup.Network => (new[] { "Time", "Event", "Image", "Destination host", "Destination IP", "Port", "Source IP", "Source port", "Protocol" },
                l => new[] { T(l.Event), l.Event.Title, l.Event.Image, l.Event.T1, l.Event.T2, l.Event.T3, l.Event.T4, l.Event.SourcePort, l.Event.Protocol }),
            ProcessEventGroup.Files => (new[] { "Time", "Event", "Image", "File", "Hash / access", "Object type" },
                l => new[] { T(l.Event), l.Event.Title, l.Event.Image, l.Event.T1, l.Event.T2, l.Event.T3 }),
            ProcessEventGroup.Registry => (new[] { "Time", "Event", "Image", "Key", "Value", "Type" },
                l => new[] { T(l.Event), l.Event.Title, l.Event.Image, l.Event.T1, l.Event.T2, l.Event.T3 }),
            ProcessEventGroup.Modules => (new[] { "Time", "Event", "Image", "Module", "Hash", "Signed", "Signature" },
                l => new[] { T(l.Event), l.Event.Title, l.Event.Image, l.Event.T1, l.Event.T2, l.Event.T3, l.Event.T4 }),
            ProcessEventGroup.Threats => (new[] { "Time", "Event", "Direction", "Image", "Target", "Access / address", "Call trace", "Detail" },
                l => new[] { T(l.Event), l.Event.Title, l.Direction, l.Event.Image, l.Event.TargetImage.Length > 0 ? l.Event.TargetImage : l.Event.T1, l.Event.T2, l.Event.T4, l.Event.Detail }),
            ProcessEventGroup.Scripts => (new[] { "Time", "Event", "Image", "Script / command", "Path", "User" },
                l => new[] { T(l.Event), l.Event.Title, l.Event.Image, l.Event.T1, l.Event.T2, l.Event.User }),
            ProcessEventGroup.Identity => (new[] { "Time", "Event", "Image", "Target account", "Logon id", "Authentication", "User" },
                l => new[] { T(l.Event), l.Event.Title, l.Event.Image, l.Event.T1, l.Event.T3, l.Event.T4, l.Event.User }),
            ProcessEventGroup.Persistence => (new[] { "Time", "Event", "Image", "Name", "Path / command", "User" },
                l => new[] { T(l.Event), l.Event.Title, l.Event.Image, l.Event.T1, l.Event.T2, l.Event.User }),
            _ => (new[] { "Time", "Event", "Image", "Command line", "Parent image", "User" },
                l => new[] { T(l.Event), l.Event.Title, l.Event.Image, l.Event.T1, l.Event.T2, l.Event.User }),
        };
    }

    /// <summary>The EvtxECmd CSV filtered to the event: same EventId and TimeCreated in the same second.</summary>
    private ProcessRowOrigin? EventOrigin(ProcessEventRecord e)
    {
        var path = Events.PathOf(e.SourceFile);
        if (path.Length == 0 || e.Time == null) return null;
        return new ProcessRowOrigin(path, FilterGroup.And(
            new FilterCondition("EventId", ColumnFilterMode.Equals, e.EventId.ToString(CultureInfo.InvariantCulture)),
            new FilterCondition("TimeCreated", ColumnFilterMode.BeginsWith, FormatTime(e.Time))));
    }

    // ------------------------------------------------------------------ Zeek / Prefetch / catalog

    private ProcessRowSet ZeekSet(ProcessNode node, IReadOnlyList<ZeekSocketLink> links)
    {
        const string id = "zeek", title = "Zeek connections (via socket)";
        var label = ZeekSocketIndex.SourceNote;
        if (Zeek == null)
            return new ProcessRowSet { Id = id, Tab = ProcessTab.Network, Title = title, Status = EvidenceStatus.NotFound, SourceLabel = label, Message = "Reading the Zeek logs…" };
        if (Zeek.Status == EvidenceStatus.NotFound)
            return new ProcessRowSet { Id = id, Tab = ProcessTab.Network, Title = title, Status = EvidenceStatus.NotFound, SourceLabel = label,
                Message = "No Zeek conn log for this host — " + Zeek.HowToGet + "." };
        if (links.Count == 0)
            return new ProcessRowSet { Id = id, Tab = ProcessTab.Network, Title = title, Status = EvidenceStatus.NoRows, SourceLabel = label,
                Message = "No Zeek connection has the exact 5-tuple of a socket of this process inside its lifetime." };
        var headers = new[] { "Time", "Proto", "Originator", "Responder", "Service", "Duration", "Orig bytes", "Resp bytes", "State", "DNS", "HTTP", "TLS server name", "Socket from" };
        var rows = links.Take(MaxRows).Select(l =>
        {
            var c = l.Conn;
            var path = Zeek.Files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), c.SourceFile, StringComparison.OrdinalIgnoreCase)) ?? "";
            var cells = new[]
            {
                FormatTime(c.Time), c.Proto, c.OrigIp + ":" + c.OrigPort.ToString(CultureInfo.InvariantCulture), c.RespIp + ":" + c.RespPort.ToString(CultureInfo.InvariantCulture),
                c.Service, c.Duration, c.OrigBytes, c.RespBytes, c.ConnState, string.Join(", ", l.Dns.Select(d => d.Query)),
                string.Join(", ", l.Http.Select(h => (h.Method + " " + h.Host + h.Uri).Trim())), l.SslServerName, l.Socket.Source,
            };
            return new ProcessActivityRow(c.Time, l.Tier, label, cells,
                path.Length > 0 && c.Uid.Length > 0 ? new ProcessRowOrigin(path, new FilterCondition("uid", ColumnFilterMode.Equals, c.Uid)) : null);
        }).ToList();
        return new ProcessRowSet { Id = id, Tab = ProcessTab.Network, Title = title, Status = EvidenceStatus.Available, SourceLabel = label, Headers = headers, Rows = rows, TotalRows = links.Count };
    }

    private ProcessRowSet Prefetch(ProcessNode node)
    {
        const string id = "prefetch", title = "Prefetch (path + run time)", label = "Prefetch (PECmd)";
        var disk = Graph.Evidence.Disk;
        if (!disk.HasPrefetch)
            return new ProcessRowSet { Id = id, Tab = ProcessTab.Execution, Title = title, Status = EvidenceStatus.NotFound, SourceLabel = label,
                Message = "No Prefetch CSV (PECmd) for this host — run Prefetch from Ingest." };
        var matches = disk.FindPrefetch(node);
        if (matches.Count == 0)
            return new ProcessRowSet { Id = id, Tab = ProcessTab.Execution, Title = title, Status = EvidenceStatus.NoRows, SourceLabel = label,
                Message = "No Prefetch run of this program's full path fits the process start." };
        var headers = new[] { "Matched run", "Executable", "Run count", "Last run", "Previous runs", "Prefetch file" };
        var rows = matches.Select(m =>
        {
            var e = m.Entry;
            ProcessRowOrigin? origin = e.CsvPath.Length == 0 ? null
                : new ProcessRowOrigin(e.CsvPath, e.SourceFile.Length > 0
                    ? new FilterCondition("SourceFilename", ColumnFilterMode.Equals, e.SourceFile)
                    : new FilterCondition("ExecutableName", ColumnFilterMode.Equals, e.Executable));
            return new ProcessActivityRow(m.Runs.Count > 0 ? m.Runs.Min() : null, LinkTier.PathTime, "Prefetch",
                new[] { string.Join(" | ", m.Runs.Select(t => FormatTime(t))), e.Executable, e.RunCount, e.LastRun, e.PreviousRuns, e.SourceFile }, origin);
        }).ToList();
        return new ProcessRowSet { Id = id, Tab = ProcessTab.Execution, Title = title, Status = EvidenceStatus.Available, SourceLabel = label, Headers = headers, Rows = rows, TotalRows = rows.Count };
    }

    private static bool IsPrefetch(EvidenceSourceDef d) => d.Id.Equals("prefetch", StringComparison.OrdinalIgnoreCase);

    private static ProcessTab TabOf(EvidenceTab t) => t switch
    {
        EvidenceTab.Network => ProcessTab.Network, EvidenceTab.Files => ProcessTab.Files, EvidenceTab.Registry => ProcessTab.Registry,
        EvidenceTab.Modules => ProcessTab.Modules, EvidenceTab.Threats => ProcessTab.Threats, EvidenceTab.Scripts => ProcessTab.Scripts,
        EvidenceTab.Identity => ProcessTab.Identity, EvidenceTab.Persistence => ProcessTab.Persistence, _ => ProcessTab.Execution,
    };

    /// <summary>Catalog CSV sources other than Prefetch (which has its own set with the matched run times) that were found for the host.</summary>
    private IEnumerable<ProcessRowSet> CatalogSets(ProcessNode node, ProcessTab tab)
    {
        if (EvidenceIndex == null) yield break;
        foreach (var st in EvidenceIndex.Statuses)
        {
            if (!st.Source.IsCsv || IsPrefetch(st.Source) || TabOf(st.Source.Tab) != tab || st.Status == EvidenceStatus.NotFound) continue;
            var r = EvidenceIndex.Lookup(node, st.Source.Id);
            var id = "src." + st.Source.Id;
            if (r.Status != EvidenceStatus.Available)
            {
                yield return new ProcessRowSet { Id = id, Tab = tab, Title = st.Source.Label, Status = r.Status, SourceLabel = st.Source.Label,
                    Message = r.Status == EvidenceStatus.NotFound ? "Not collected — " + st.HowToGet + "." : "No rows linked to this process." };
                continue;
            }
            int tc = TimeCol(r.Headers);
            var rows = r.Rows.Select(x => new ProcessActivityRow(tc >= 0 ? ParseTime(Cell(x.Cells, tc)) : null, x.Tier, st.Source.Label, x.Cells)).ToList();
            yield return new ProcessRowSet { Id = id, Tab = tab, Title = st.Source.Label, Status = EvidenceStatus.Available, SourceLabel = st.Source.Label,
                Headers = r.Headers, Rows = rows, TotalRows = r.TotalRows };
        }
    }

    // ------------------------------------------------------------------ sources

    private static readonly (string Log, string HowToGet)[] Logs =
    {
        ("Security", "Ingest › Event logs (process creation needs the 'Audit Process Creation' policy)"),
        ("Sysmon", "Sysmon not present: install Sysmon on the host, then Ingest › Event logs"),
        ("PowerShell", "Ingest › Event logs (script block logging must be enabled)"),
        ("Windows PowerShell", "Ingest › Event logs"),
        ("Task Scheduler", "Ingest › Event logs"),
        ("WMI", "Ingest › Event logs"),
        ("Defender", "Ingest › Event logs"),
        ("System", "Ingest › Event logs"),
    };

    /// <summary>
    /// Every source the card reads, one row each: Source, Status (found / missing / no rows), rows for this process, how to get it. The source chip is the category
    /// (Memory, Event log, Disk, Network, Catalog).
    /// </summary>
    public ProcessRowSet Sources(ProcessNode node)
    {
        node = Resolve(node);
        var rows = new List<ProcessActivityRow>();
        void Add(string category, string source, string status, int forProcess, string howToGet) =>
            rows.Add(new ProcessActivityRow(null, null, category, new[] { source, status, forProcess > 0 ? forProcess.ToString("N0", CultureInfo.InvariantCulture) : status == "missing" ? "" : "0", status == "found" && forProcess > 0 ? "" : howToGet }));

        bool inMemory = ProcessGraphBuilder.InMemory(node);
        foreach (var (src, plugin) in new[] { (ProcessSources.PsList, "windows.pslist"), (ProcessSources.PsScan, "windows.psscan"), (ProcessSources.PsTree, "windows.pstree") })
            Add("Memory", plugin, Graph.SourceCounts.ContainsKey(src) ? "found" : "missing", node.Sources.HasFlag(src) ? 1 : 0, "Tools › Volatility › " + plugin);

        var kinds = new[] { ProcessFacetKind.Network }.Concat(MemorySets.Select(m => m.Kind)).ToList();
        foreach (var g in kinds.GroupBy(ProcessFacetIndex.PluginIdOf))
        {
            var plugin = PluginName(g.Key);
            var how = "Tools › Volatility › " + plugin;
            if (Facets == null) { Add("Memory", plugin, "indexing…", 0, how); continue; }
            var st = Facets.GetStatus(g.First());
            if (st == FacetStatus.PluginNotRun) { Add("Memory", plugin, "missing", 0, how); continue; }
            int n = !inMemory ? 0 : g.Key == "handles" ? g.Sum(k => Facets.Count(k, node.Pid)) : Facets.Count(g.First(), node.Pid);
            Add("Memory", plugin, n > 0 ? "found" : "no rows", n, inMemory ? how : "This process is not in the memory image.");
        }

        if (!Events.HasFiles) Add("Event log", "Event logs (EvtxECmd)", "missing", 0, "Ingest › Event logs");
        else
        {
            var perLog = Events.LinksFor(node).GroupBy(l => ProcessEventIndex.LogOf(l.Event), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var (log, how) in Logs)
            {
                bool seen = Events.LogsSeen.Contains(log);
                if (!seen && log is "Windows PowerShell" or "System") continue;   // rarely relevant: listed only when present
                int n = perLog.GetValueOrDefault(log);
                Add("Event log", log, seen ? n > 0 ? "found" : "no rows" : "missing", n, how);
            }
        }

        var disk = Graph.Evidence.Disk;
        var prefetchHow = ProcessEvidenceCatalog.Find("prefetch")?.HowToGet ?? "Ingest › Prefetch";
        int pf = disk.HasPrefetch ? disk.FindPrefetch(node).Count : 0;
        Add("Disk", "Prefetch (path + run time)", disk.HasPrefetch ? pf > 0 ? "found" : "no rows" : "missing", pf, prefetchHow);

        if (Zeek == null) Add("Network", "Zeek (via the process's sockets)", "indexing…", 0, ZeekSocketIndex.Source?.HowToGet ?? "Ingest › Zeek on a pcap");
        else
        {
            int z = Zeek.LinksFor(node).Count;
            Add("Network", "Zeek (via the process's sockets)", Zeek.Status == EvidenceStatus.NotFound ? "missing" : z > 0 ? "found" : "no rows", z, Zeek.HowToGet);
        }

        if (EvidenceIndex != null)
            foreach (var st in EvidenceIndex.Statuses.Where(s => s.Source.IsCsv && !IsPrefetch(s.Source)))
            {
                int n = st.Status == EvidenceStatus.Available ? EvidenceIndex.Lookup(node, st.Source.Id).TotalRows : 0;
                Add("Catalog", st.Source.Label, st.Status == EvidenceStatus.NotFound ? "missing" : n > 0 ? "found" : "no rows", n, st.HowToGet);
            }

        return new ProcessRowSet
        {
            Id = "sources", Title = "Sources", Tab = ProcessTab.Sources, Status = EvidenceStatus.Available, SourceLabel = "what was read for this host",
            Headers = new[] { "Source", "Status", "Rows for this process", "How to get it" }, Rows = rows, TotalRows = rows.Count,
        };
    }

    // ------------------------------------------------------------------ helpers

    public static string FormatTime(DateTime? t) => t == null ? "" : t.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime? ParseTime(string s) => EzForensics.Core.Time.TimeParse.Lenient(s);

    private static string Cell(string[] r, int i) => i >= 0 && i < r.Length ? (r[i] ?? "").Trim() : "";

    private static int IndexOf(IReadOnlyList<string> headers, string norm)
    {
        for (int i = 0; i < headers.Count; i++) if (ProcessLogText.Norm(headers[i]) == norm) return i;
        return -1;
    }

    private static int TimeCol(IReadOnlyList<string> headers)
    {
        foreach (var a in TimeCols) { int i = IndexOf(headers, a); if (i >= 0) return i; }
        return -1;
    }

    /// <summary>The non-empty cells of a row as "Header: value · ..." without the columns that repeat the process (PID, name, offset, time).</summary>
    public static string Summarize(IReadOnlyList<string> headers, string[] cells)
    {
        var parts = new List<string>();
        for (int i = 0; i < cells.Length; i++)
        {
            var v = (cells[i] ?? "").Trim();
            if (v.Length == 0 || v == "-" || v == "N/A") continue;
            var h = i < headers.Count ? headers[i] : "";
            var n = ProcessLogText.Norm(h);
            if (SummarySkip.Contains(n) || TimeCols.Contains(n)) continue;
            parts.Add(h.Length > 0 ? h + ": " + v : v);
        }
        var s = string.Join(" · ", parts);
        return s.Length > SummaryMax ? s[..SummaryMax] + "…" : s;
    }
}
