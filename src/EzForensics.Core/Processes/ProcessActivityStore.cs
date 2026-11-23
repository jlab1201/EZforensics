using System.Globalization;
using System.Text.Json;
using EzForensics.Core.Results;

namespace EzForensics.Core.Processes;

/// <summary>
/// The details card and the tree read from the <see cref="ProcessCorrelator"/>'s files only: <c>Processes.csv</c> (the graph, counts, signature, services and each
/// process's rows in <c>Processes_Sets.csv</c>), <c>Processes_Sets.csv</c> (every row set of the card, with its status / message and its rows' range in
/// <c>Processes_Activity.csv</c>) and <c>Processes_Activity.csv</c> (the rows, with their grid cells). The two large CSVs stay on disk (<see cref="CsvIndex"/> row
/// offsets); a process's sets and rows are read when its card asks for them. Thread safe; dispose to release the files.
/// </summary>
public sealed class ProcessActivityStore : IProcessDetailSource, IDisposable
{
    private const int CachedNodes = 8;

    private sealed record Entry(Dictionary<ProcessTab, int> Counts, string Signature, IReadOnlyList<string> Services, long SetsFirstRow, int SetsRowCount);

    private sealed record SetMeta(ProcessTab Tab, string Id, string Title, EvidenceStatus Status, string Message, string SourceLabel, string Remark, int TotalRows,
        string[] Headers, long ActivityFirstRow, int ActivityRowCount, string Rows);

    private readonly Dictionary<ProcessNode, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, ProcessNode> _byKey = new(StringComparer.Ordinal);
    private readonly CsvIndex _sets;
    private readonly CsvIndex _activity;
    private readonly object _cacheLock = new();
    private readonly LinkedList<(ProcessNode Node, IReadOnlyList<SetMeta> Sets)> _cache = new();
    private bool _disposed;

    private ProcessActivityStore(string hostFolder, ProcessGraph graph, CsvIndex sets, CsvIndex activity)
    {
        HostFolder = hostFolder;
        Graph = graph;
        _sets = sets;
        _activity = activity;
    }

    public string HostFolder { get; }
    /// <summary>The graph of Processes.csv (nodes, links, roots, roles, indicators, source counts and warnings of the manifest); no evidence indexes.</summary>
    public ProcessGraph Graph { get; }

    /// <summary>The store of an up-to-date correlation (<see cref="ProcessCorrelator.IsUpToDate"/>); null when the output is missing, stale or unreadable.</summary>
    public static async Task<ProcessActivityStore?> OpenAsync(string hostFolder, CancellationToken ct = default)
    {
        if (!await Task.Run(() => ProcessCorrelator.IsUpToDate(hostFolder), ct).ConfigureAwait(false)) return null;
        var entries = new List<(ProcessNode, Entry)>();
        ProcessGraph graph;
        try
        {
            graph = await Task.Run(() => ReadGraph(hostFolder, entries), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Sylvan.Data.Csv.CsvRecordTooLargeException or InvalidOperationException or FormatException or JsonException) { return null; }

        CsvIndex? sets = null;
        try
        {
            sets = await CsvIndex.OpenAsync(ProcessCorrelator.SetsPath(hostFolder), null, CancellationToken.None).ConfigureAwait(false);
            var activity = await CsvIndex.OpenAsync(ProcessCorrelator.ActivityPath(hostFolder), null, CancellationToken.None).ConfigureAwait(false);
            var store = new ProcessActivityStore(hostFolder, graph, sets, activity);
            foreach (var (n, e) in entries)
            {
                store._entries[n] = e;
                store._byKey.TryAdd(n.Key, n);
            }
            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sets?.Dispose();
            return null;
        }
    }

    private static ProcessGraph ReadGraph(string hostFolder, List<(ProcessNode, Entry)> entries)
    {
        var g = ProcessCorrelator.ReadNodes(ProcessCorrelator.NodesPath(hostFolder), (n, cell) =>
        {
            if (n.IsGhost) return;
            var counts = Enum.GetValues<ProcessTab>().ToDictionary(t => t, _ => 0);
            foreach (var t in ProcessCorrelator.CountTabs) counts[t] = Int(cell(t + "Count"));
            var services = cell("Services") is { Length: > 0 } s ? JsonSerializer.Deserialize<string[]>(s) ?? Array.Empty<string>() : Array.Empty<string>();
            entries.Add((n, new Entry(counts, cell("Signature"), services, Long(cell("SetsFirstRow")), Int(cell("SetsRowCount")))));
        });
        if (ProcessCorrelator.ReadManifest(hostFolder) is { } m)
        {
            foreach (var (k, v) in m.SourceCounts) if (Enum.TryParse<ProcessSources>(k, out var src)) g.SourceCounts[src] = v;
            g.Warnings.AddRange(m.Warnings);
        }
        g.SourceFiles.Add(ProcessCorrelator.NodesPath(hostFolder));
        return g;
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static long Long(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>The store's entry of <paramref name="node"/>: itself, else the node of the same key (null for ghosts and unknown nodes).</summary>
    private Entry? EntryOf(ref ProcessNode node)
    {
        if (node.IsGhost) return null;
        if (_entries.TryGetValue(node, out var e)) return e;
        if (_byKey.TryGetValue(node.Key, out var same) && _entries.TryGetValue(same, out e)) { node = same; return e; }
        return null;
    }

    // ------------------------------------------------------------------ IProcessDetailSource

    public Dictionary<ProcessTab, int> Counts(ProcessNode node)
    {
        var c = new Dictionary<ProcessTab, int>(TabCounts(node));
        if (EntryOf(ref node) == null) return c;
        c[ProcessTab.Sources] = Sources(node).Rows.Count(r => r.Cells.Length > 1 && r.Cells[1] == ProcessActivity.FoundStatus);
        return c;
    }

    /// <summary>
    /// Linked rows per data tab as the correlator counted them (the Processes.csv count columns, the same rows the card's tabs list): an in-memory lookup that reads
    /// no file, so the tree can badge every node at once. Sources and Overview are 0 (see <see cref="Counts"/> for the card's rail).
    /// </summary>
    public IReadOnlyDictionary<ProcessTab, int> TabCounts(ProcessNode node)
    {
        var e = EntryOf(ref node);
        var c = Enum.GetValues<ProcessTab>().ToDictionary(t => t, _ => 0);
        if (e == null) return c;
        foreach (var (t, v) in e.Counts) c[t] = v;
        c[ProcessTab.Overview] = 0;
        c[ProcessTab.Sources] = 0;
        return c;
    }

    public string? SignatureText(ProcessNode node) => EntryOf(ref node) is { Signature.Length: > 0 } e ? e.Signature : null;

    public IReadOnlyList<string> ServiceNames(ProcessNode node, int cap = 20) =>
        EntryOf(ref node) is { } e ? e.Services.Take(cap).ToList() : Array.Empty<string>();

    public IReadOnlyList<ProcessRowSet> SetsFor(ProcessNode node, ProcessTab tab)
    {
        if (EntryOf(ref node) is not { } e) return Array.Empty<ProcessRowSet>();
        return SetsOf(node, e).Where(s => s.Tab == tab && s.Id != SourcesId).Select(ToSet).ToList();
    }

    public ProcessRowSet Timeline(ProcessNode node) =>
        ProcessActivity.MergeTimeline(ProcessActivity.DataTabs.SelectMany(t => SetsFor(node, t)), ProcessActivity.MaxRows);

    private const string SourcesId = "sources";

    public ProcessRowSet Sources(ProcessNode node)
    {
        var rows = new List<ProcessActivityRow>();
        if (EntryOf(ref node) is { } e && SetsOf(node, e).FirstOrDefault(s => s.Id == SourcesId) is { Rows.Length: > 0 } meta)
            foreach (var r in JsonSerializer.Deserialize<List<string[]>>(meta.Rows) ?? new())
                if (r.Length > 0) rows.Add(new ProcessActivityRow(null, null, r[0], r.Skip(1).ToArray()));
        return new ProcessRowSet
        {
            Id = SourcesId, Title = "Sources", Tab = ProcessTab.Sources, Status = EvidenceStatus.Available, SourceLabel = "what was read for this host",
            Headers = new[] { "Source", "Status", "Rows for this process", "How to get it" }, Rows = rows, TotalRows = rows.Count,
        };
    }

    // ------------------------------------------------------------------ reading

    /// <summary>The node's rows of Processes_Sets.csv (a few dozen), cached for the last few nodes.</summary>
    private IReadOnlyList<SetMeta> SetsOf(ProcessNode node, Entry e)
    {
        lock (_cacheLock)
        {
            for (var it = _cache.First; it != null; it = it.Next)
                if (ReferenceEquals(it.Value.Node, node))
                {
                    _cache.Remove(it);
                    _cache.AddFirst(it);
                    return it.Value.Sets;
                }
        }
        var rows = Read(_sets, e.SetsFirstRow, e.SetsRowCount);
        int C(string col) => _sets.ColumnIndex(col);
        int key = C("ProcessKey"), tab = C("Tab"), id = C("SetId"), title = C("Title"), status = C("Status"), message = C("Message"), label = C("SourceLabel"),
            remark = C("Remark"), total = C("TotalRows"), headers = C("Headers"), first = C("ActivityFirstRow"), count = C("ActivityRowCount"), inline = C("Rows");
        var list = new List<SetMeta>(rows.Length);
        foreach (var r in rows)
        {
            string S(int i) => i >= 0 && i < r.Length ? r[i] : "";
            if (S(key) != node.Key) throw new InvalidDataException("Processes_Sets.csv does not match Processes.csv: run the process correlation again.");
            list.Add(new SetMeta(
                Enum.TryParse<ProcessTab>(S(tab), out var t) ? t : ProcessTab.Overview, S(id), S(title),
                Enum.TryParse<EvidenceStatus>(S(status), out var st) ? st : EvidenceStatus.NotFound, S(message), S(label), S(remark), Int(S(total)),
                S(headers) is { Length: > 0 } h ? JsonSerializer.Deserialize<string[]>(h) ?? Array.Empty<string>() : Array.Empty<string>(),
                Long(S(first)), Int(S(count)), S(inline)));
        }
        lock (_cacheLock)
        {
            _cache.AddFirst((node, list));
            while (_cache.Count > CachedNodes) _cache.RemoveLast();
        }
        return list;
    }

    /// <summary>A set with its rows read from Processes_Activity.csv.</summary>
    private ProcessRowSet ToSet(SetMeta m)
    {
        var rows = new List<ProcessActivityRow>(m.ActivityRowCount);
        if (m.ActivityRowCount > 0)
        {
            int C(string col) => _activity.ColumnIndex(col);
            int time = C("Time"), source = C("Source"), tier = C("Tier"), file = C("SourceFile"), filter = C("SourceRow"), cells = C("Cells");
            foreach (var r in Read(_activity, m.ActivityFirstRow, m.ActivityRowCount))
            {
                string S(int i) => i >= 0 && i < r.Length ? r[i] : "";
                rows.Add(new ProcessActivityRow(
                    ProcessCorrelator.ParseTime(S(time)),
                    Enum.TryParse<LinkTier>(S(tier), out var lt) ? lt : null,
                    S(source),
                    S(cells) is { Length: > 0 } c ? JsonSerializer.Deserialize<string[]>(c) ?? Array.Empty<string>() : Array.Empty<string>(),
                    ProcessCorrelator.OriginOf(HostFolder, S(file), S(filter))));
            }
        }
        return new ProcessRowSet
        {
            Id = m.Id, Title = m.Title, Tab = m.Tab, Status = m.Status, Message = m.Message, SourceLabel = m.SourceLabel, Remark = m.Remark,
            Headers = m.Headers, Rows = rows, TotalRows = m.TotalRows,
        };
    }

    /// <summary>Rows [first, first + count) of a CSV, waiting for its offset index to reach them.</summary>
    private string[][] Read(CsvIndex idx, long first, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessActivityStore));
        if (count <= 0) return Array.Empty<string[]>();
        if (first + count > idx.RowCount) idx.IndexingCompleted.GetAwaiter().GetResult();
        if (first < 0 || first + count > idx.RowCount)
            throw new InvalidDataException(Path.GetFileName(idx.Path) + " is shorter than Processes.csv says: run the process correlation again.");
        return idx.ReadRows(first, count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sets.Dispose();
        _activity.Dispose();
    }
}
