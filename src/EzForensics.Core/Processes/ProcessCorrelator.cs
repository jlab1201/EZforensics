using System.Globalization;
using System.Text;
using System.Text.Json;
using EzForensics.Core.Case;
using EzForensics.Core.Execution;
using EzForensics.Core.Persistence;
using EzForensics.Core.Results;
using Sylvan.Data.Csv;

namespace EzForensics.Core.Processes;

/// <summary>One input CSV of a correlation: path relative to the host folder (absolute when outside it), size and last write time.</summary>
public sealed record ProcessCorrelationInput(string Path, long Size, long LastWriteUtcTicks);

/// <summary><c>&lt;host&gt;\.ezprocesses\.inputs.json</c>: what the correlation was built from, and the graph facts Processes.csv has no column for.</summary>
public sealed class ProcessCorrelationManifest
{
    public int Version { get; set; }
    public DateTime BuiltUtc { get; set; }
    public List<ProcessCorrelationInput> Inputs { get; set; } = new();
    /// <summary>Rows read per process source (<see cref="ProcessGraph.SourceCounts"/>), by <see cref="ProcessSources"/> name.</summary>
    public Dictionary<string, int> SourceCounts { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public int Nodes { get; set; }
    public long ActivityRows { get; set; }
}

/// <summary>How one correlation run ended.</summary>
public enum ProcessCorrelationState { Built, UpToDate, NoSource, Failed, Cancelled }

public sealed record ProcessCorrelationOutcome(string Host, ProcessCorrelationState State, int Nodes = 0, long ActivityRows = 0, string? Error = null);

/// <summary>
/// Built-in EZ Forensics "Process correlator" (hidden backend of the process graph): builds the host's <see cref="ProcessCorrelation"/> and writes
/// <c>&lt;host&gt;\.ezprocesses\Processes.csv</c> (one row per process instance, its parent key, roll-up and counts per details tab) and
/// <c>Processes_Activity.csv</c> (one row per linked evidence row, Exact or PathTime only, with the CSV and the grid filter that reopen it), plus
/// <c>.inputs.json</c> (paths, sizes and times of every input CSV): the step and the Analyze tree use it to know whether the files are up to date.
/// Files are written to a temporary name and moved into place; the fingerprint is written last.
/// </summary>
public static class ProcessCorrelator
{
    public const string ToolId = "EzForensics.Processes";
    /// <summary>Hidden backend folder of a host (<c>&lt;host&gt;\.ezprocesses</c>): dot-folders are never listed in the case tree.</summary>
    public const string FolderName = ".ezprocesses";
    /// <summary>The visible folder <c>&lt;host&gt;\Processes</c>: it holds the one user-facing merged process list <see cref="ListCsv"/>; the older correlator files in it are removed after a build.</summary>
    public const string LegacyFolderName = "Processes";
    public const string ListFolderName = "Processes";
    /// <summary>The merged, readable process list (one row per process instance, all sources combined).</summary>
    public const string ListCsv = "Processes.csv";
    /// <summary>Category of the correlator job (Jobs page); not a case-tree category.</summary>
    public const string JobCategory = "Process tree data";
    public const string Switch = "--process-correlator";
    public const string NodesCsv = "Processes.csv";
    public const string ActivityCsv = "Processes_Activity.csv";
    public const string SetsCsv = "Processes_Sets.csv";
    public const string InputsFile = ".inputs.json";
    /// <summary>Bumped when a column or the meaning of a file changes: older output then counts as stale.</summary>
    public const int FormatVersion = 7;
    internal const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff";
    /// <summary>A JSON cell (Cells, Headers, Rows) is kept under this length so <see cref="PersistenceCollector.Esc"/> never cuts it.</summary>
    private const int MaxJsonCell = 30000;

    public static string FolderFor(string hostFolder) => Path.Combine(hostFolder, FolderName);
    public static string ListPath(string hostFolder) => Path.Combine(hostFolder, ListFolderName, ListCsv);
    public static string NodesPath(string hostFolder) => Path.Combine(FolderFor(hostFolder), NodesCsv);
    public static string ActivityPath(string hostFolder) => Path.Combine(FolderFor(hostFolder), ActivityCsv);
    public static string SetsPath(string hostFolder) => Path.Combine(FolderFor(hostFolder), SetsCsv);
    public static string InputsPath(string hostFolder) => Path.Combine(FolderFor(hostFolder), InputsFile);

    /// <summary>Count columns of Processes.csv: the data tabs plus Timeline.</summary>
    public static readonly IReadOnlyList<ProcessTab> CountTabs = ProcessActivity.DataTabs.Append(ProcessTab.Timeline).ToList();

    public static readonly IReadOnlyList<string> NodeHeaders = new[]
    {
        "Key", "PID", "PPID", "ParentKey", "Name", "Path", "CommandLine", "User", "CreateTime", "ExitTime", "Sources", "Markers",
        "Hash", "HashSource", "Signature", "FileVersion", "FirstSeen", "LastSeen",
    }.Concat(CountTabs.Select(t => t + "Count"))
     .Concat(new[] { "Guid", "ParentGuid", "ParentImage", "Offset", "SessionId", "Wow64", "Threads", "Handles", "Company", "Product", "VersionInfoSource", "UserSource", "Integrity", "Role", "RoleBasis", "Indicators", "IsGhost",
         "Services", "SetsFirstRow", "SetsRowCount" })
     .ToList();

    /// <summary>One row per linked evidence row (Exact / PathTime). <c>Cells</c> (JSON array) lines up with the <c>Headers</c> of its set in Processes_Sets.csv.</summary>
    public static readonly IReadOnlyList<string> ActivityHeaders = new[]
    {
        "ProcessKey", "PID", "ProcessName", "Time", "Tab", "What", "Source", "Tier", "Summary", "SourceFile", "SourceRow", "SetId", "Cells",
    };

    /// <summary>
    /// One row per (process, row set of the details card), in card order, including the sets without rows (their status and how-to-get message) and one
    /// <c>sources</c> set per process (the Sources tab; its rows are inline in <c>Rows</c> as JSON <c>[[category, source, status, rows, howToGet], ...]</c>).
    /// <c>ActivityFirstRow</c> / <c>ActivityRowCount</c>: the set's data rows in Processes_Activity.csv (0-based data row numbers).
    /// </summary>
    public static readonly IReadOnlyList<string> SetHeaders = new[]
    {
        "ProcessKey", "Tab", "SetId", "Title", "Status", "Message", "SourceLabel", "Remark", "TotalRows", "Headers", "ActivityFirstRow", "ActivityRowCount", "Rows",
    };

    // ------------------------------------------------------------------ inputs / freshness

    /// <summary>Every CSV the correlation reads for the host (process lists, memory plugins, event logs, Prefetch, Zeek, catalog sources); none under <c>.ezprocesses</c>.</summary>
    public static List<string> InputFiles(string hostFolder)
    {
        var all = new List<string>();
        if (string.IsNullOrWhiteSpace(hostFolder) || !Directory.Exists(hostFolder)) return all;
        var f = ProcessGraphBuilder.FindFiles(hostFolder);
        all.AddRange(ProcessGraphBuilder.MemoryCsvFiles(hostFolder));
        all.AddRange(f.EventLogs);
        all.AddRange(f.Prefetch);
        all.AddRange(ZeekSocketIndex.DiscoverFiles(hostFolder));
        all.AddRange(ProcessEvidenceIndex.DiscoverFiles(hostFolder, ProcessEvidenceCatalog.Sources));
        var own = FolderFor(hostFolder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return all.Where(p => !p.StartsWith(own, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Path, size and last write time of every input (see <see cref="InputFiles"/>), sorted by path.</summary>
    public static List<ProcessCorrelationInput> Fingerprint(string hostFolder)
    {
        var list = new List<ProcessCorrelationInput>();
        foreach (var p in InputFiles(hostFolder))
        {
            try
            {
                var fi = new FileInfo(p);
                if (fi.Exists) list.Add(new ProcessCorrelationInput(Relative(hostFolder, p), fi.Length, fi.LastWriteTimeUtc.Ticks));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    public static ProcessCorrelationManifest? ReadManifest(string hostFolder)
    {
        try
        {
            var path = InputsPath(hostFolder);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ProcessCorrelationManifest>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Processes.csv and Processes_Activity.csv exist and were built, in the current format, from exactly the host's current inputs.</summary>
    public static bool IsUpToDate(string hostFolder)
    {
        if (!File.Exists(NodesPath(hostFolder)) || !File.Exists(ActivityPath(hostFolder)) || !File.Exists(SetsPath(hostFolder))) return false;
        var m = ReadManifest(hostFolder);
        if (m == null || m.Version != FormatVersion || m.Inputs.Count == 0) return false;
        var now = Fingerprint(hostFolder);
        if (now.Count != m.Inputs.Count) return false;
        for (int i = 0; i < now.Count; i++)
        {
            var (a, b) = (now[i], m.Inputs[i]);
            if (!string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) || a.Size != b.Size || a.LastWriteUtcTicks != b.LastWriteUtcTicks) return false;
        }
        return true;
    }

    /// <summary>The host has a source a process tree is built from (Volatility process lists or an event log CSV; file names only).</summary>
    public static bool HasProcessSource(string? hostFolder)
    {
        if (string.IsNullOrEmpty(hostFolder)) return false;
        try { return ProcessGraphBuilder.FindFiles(hostFolder).CanBuildTree; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Relative(string hostFolder, string path)
    {
        var root = Path.GetFullPath(hostFolder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : full;
    }

    private static string Absolute(string hostFolder, string path) =>
        path.Length == 0 || Path.IsPathRooted(path) ? path : Path.Combine(hostFolder, path);

    // ------------------------------------------------------------------ run

    /// <summary>
    /// Correlates the host: skipped (<see cref="ProcessCorrelationState.UpToDate"/>) when the output is fresh and <paramref name="force"/> is off;
    /// <see cref="ProcessCorrelationState.NoSource"/> without a process source. <paramref name="log"/> gets progress lines.
    /// </summary>
    public static async Task<ProcessCorrelationOutcome> RunAsync(string hostFolder, bool force = false, Action<string>? log = null, CancellationToken ct = default)
    {
        var host = Path.GetFileName(hostFolder.TrimEnd('\\', '/'));
        if (!HasProcessSource(hostFolder))
            return new ProcessCorrelationOutcome(host, ProcessCorrelationState.NoSource, Error: "No process source: run windows.pslist / psscan / pstree (Tools › Volatility) or ingest the event logs first.");
        if (!force && IsUpToDate(hostFolder))
        {
            var m = ReadManifest(hostFolder)!;
            return new ProcessCorrelationOutcome(host, ProcessCorrelationState.UpToDate, m.Nodes, m.ActivityRows);
        }
        var inputs = Fingerprint(hostFolder);   // before reading: an input that changes meanwhile leaves the output stale
        try
        {
            log?.Invoke("Building the process graph and indexes of " + hostFolder);
            using var c = await Task.Run(() => ProcessCorrelation.BuildAsync(hostFolder, ct), ct).ConfigureAwait(false);
            foreach (var (what, ex) in c.Errors) log?.Invoke($"Warning: could not index {what}: {ex.Message}");
            log?.Invoke($"{c.Graph.AllNodes.Count:N0} processes; writing {FolderFor(hostFolder)}");
            var (nodes, rows) = await Task.Run(() => Write(hostFolder, c, inputs, ct), ct).ConfigureAwait(false);
            var warnings = new List<string>();
            RemoveLegacyFolder(hostFolder, warnings.Add);
            foreach (var w in warnings) log?.Invoke("Warning: " + w);
            return new ProcessCorrelationOutcome(host, ProcessCorrelationState.Built, nodes, rows);
        }
        catch (OperationCanceledException) { return new ProcessCorrelationOutcome(host, ProcessCorrelationState.Cancelled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CsvRecordTooLargeException
                                       or FormatException or ArgumentException or JsonException or InvalidDataException or NullReferenceException or IndexOutOfRangeException
                                       or KeyNotFoundException or InvalidCastException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {   // malformed evidence must fail this one correlation, not crash the headless job
            return new ProcessCorrelationOutcome(host, ProcessCorrelationState.Failed, Error: ex.Message);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex LegacyFileName = new(
        @"^(\.inputs\.json(\.tmp)?|Processes(_Activity|_Sets)?(_[0-9]+)?\.csv(\..+)?|Processes(_Activity|_Sets)?(_[0-9]+)?\.ezflags\.json|Processes(_Activity|_Sets)?(_[0-9]+)?\.csv\.tmp)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The pre-round-4 visible output folder <c>&lt;host&gt;\Processes</c>.</summary>
    public static string LegacyFolderFor(string hostFolder) => Path.Combine(hostFolder, LegacyFolderName);

    /// <summary>True when the file name is one the correlator wrote into the legacy folder (the three CSVs with collision numbers, the fingerprint, their sidecars).</summary>
    public static bool IsLegacyCorrelatorFile(string fileName) => LegacyFileName.IsMatch(fileName);

    /// <summary>The new list stays in <c>&lt;host&gt;\Processes</c> when the old correlator files are removed; sidecars of the old-format file (stats, flags by row number) are stale and go.</summary>
    private static bool IsKeptListFile(string fileName) => fileName.Equals(ListCsv, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes <c>&lt;host&gt;\Processes</c> when every entry in it is a correlator file (see <see cref="IsLegacyCorrelatorFile"/>), through
    /// <see cref="CaseFileOperations"/> so a DELETE line per file is logged in <c>logs/file_operations.log</c>. A folder holding anything else is kept and
    /// <paramref name="warn"/> gets a message. <paramref name="recycle"/> replaces the default hard delete (the files are regenerable output). Returns true when the folder was removed.
    /// </summary>
    public static bool RemoveLegacyFolder(string hostFolder, Action<string>? warn = null, Action<string, bool>? recycle = null)
    {
        var legacy = LegacyFolderFor(hostFolder);
        if (!Directory.Exists(legacy)) return false;
        try
        {
            var foreign = Directory.EnumerateFileSystemEntries(legacy, "*", SearchOption.TopDirectoryOnly)
                .Where(e => Directory.Exists(e) || !IsLegacyCorrelatorFile(Path.GetFileName(e))).Select(Path.GetFileName).ToList();
            if (foreign.Count > 0)
            {
                warn?.Invoke($"The old folder {legacy} also holds files that are not process correlator output ({string.Join(", ", foreign.Take(5))}{(foreign.Count > 5 ? ", ..." : "")}); it was left in place.");
                return false;
            }
            var caseDir = Path.GetDirectoryName(Path.GetFullPath(hostFolder).TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(caseDir)) return false;
            var ops = new CaseFileOperations(caseDir, recycle ?? ((p, isDir) => { if (isDir) Directory.Delete(p, true); else File.Delete(p); }), "EzForensics");
            var old = Directory.EnumerateFileSystemEntries(legacy, "*", SearchOption.TopDirectoryOnly).Where(e => !IsKeptListFile(Path.GetFileName(e))).ToList();
            // sidecars (stats, flags) follow their CSV in CaseFileOperations and cannot be deleted alone: removed here, they only describe the old-format file
            foreach (var sc in old.Where(e => !Directory.Exists(e) && OutputNaming.IsEzForensicsSidecar(e)).ToList())
            {
                (recycle ?? ((x, _) => File.Delete(x)))(sc, false);
                old.Remove(sc);
            }
            if (old.Count > 0) ops.Delete(old);
            return old.Count > 0 && !Directory.EnumerateFileSystemEntries(legacy).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CaseFileOperationException)
        {
            warn?.Invoke($"The old folder {legacy} could not be removed: {ex.Message}");
            return false;
        }
    }

    public static readonly IReadOnlyList<string> ListHeaders = new[]
    {
        "PID", "PPID", "Name", "Path", "CommandLine", "User", "Integrity", "SessionId", "Wow64", "CreateTime", "ExitTime", "ParentName", "Role", "Indicators",
        "Sources", "Hash", "Signature", "Company", "Threads", "Handles", "Offset",
    };

    /// <summary>Readable names of the process sources of a node ("pslist, psscan, Security 4688").</summary>
    public static string SourcesText(ProcessSources s)
    {
        var l = new List<string>();
        if ((s & ProcessSources.PsList) != 0) l.Add("pslist");
        if ((s & ProcessSources.PsScan) != 0) l.Add("psscan");
        if ((s & ProcessSources.PsTree) != 0) l.Add("pstree");
        if ((s & ProcessSources.Evtx4688) != 0) l.Add("Security 4688");
        if ((s & ProcessSources.Sysmon1) != 0) l.Add("Sysmon 1");
        return string.Join(", ", l);
    }

    private static string SpacedKind(string kind) => System.Text.RegularExpressions.Regex.Replace(kind, "(?<=[a-z0-9])(?=[A-Z])", " ");

    /// <summary>Indicators as text: "Kind: evidence" per indicator, joined with "; ".</summary>
    public static string IndicatorsText(ProcessNode n) =>
        string.Join("; ", n.Indicators.Select(i => string.IsNullOrWhiteSpace(i.Evidence) ? SpacedKind(i.Kind.ToString()) : SpacedKind(i.Kind.ToString()) + ": " + i.Evidence.Trim()).Distinct());

    /// <summary>
    /// Writes <c>&lt;host&gt;\Processes\Processes.csv</c>: the merged process list, one row per real process instance (no ghost rows) ordered by CreateTime
    /// (no CreateTime last, then PID), temp file + move. Every row is one instance, so a PID link resolves it by PID + CreateTime.
    /// </summary>
    internal static void WriteList(string hostFolder, ProcessGraph g, ProcessActivity activity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ListPath(hostFolder))!);
        static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
        var rows = g.AllNodes.Where(n => !n.IsGhost)
            .OrderBy(n => n.CreateTime == null).ThenBy(n => n.CreateTime).ThenBy(n => n.Pid).ToList();
        using var o = new CsvOut(ListPath(hostFolder), ListHeaders);
        foreach (var n in rows)
        {
            string sig;
            try { sig = ProcessSignature.Describe(activity.SignatureFor(n)) ?? ""; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { sig = ""; }
            o.Write(new[]
            {
                I(n.Pid), I(n.Ppid), n.Name, n.Path, n.CmdLine, n.User, n.Integrity, I(n.SessionId), n.Wow64 is { } w ? (w ? "True" : "False") : "", T(n.CreateTime), T(n.ExitTime),
                n.Parent is { IsGhost: false } p ? p.Name : n.ParentImage, n.Role, IndicatorsText(n), SourcesText(n.Sources), n.Hash, sig, n.Company, I(n.Threads), I(n.Handles), n.Offset,
            });
        }
        o.Commit();
    }

    /// <summary>Per-node facts gathered while the activity / sets files are written (first / last linked time, the node's rows in Processes_Sets.csv).</summary>
    private readonly record struct NodeWritten(DateTime? First, DateTime? Last, long SetsFirstRow, long SetsRowCount);

    /// <summary>Writes the activity and sets CSVs (streamed, one pass over the card data of every process), the nodes CSV, then the fingerprint; returns (nodes, activity rows).</summary>
    public static (int Nodes, long Rows) Write(string hostFolder, ProcessCorrelation c, IReadOnlyList<ProcessCorrelationInput> inputs, CancellationToken ct = default)
    {
        Directory.CreateDirectory(FolderFor(hostFolder));
        var activity = c.Activity();
        var ordered = Ordered(c.Graph);
        int realNodes = ordered.Count(n => !n.IsGhost);
        var written = new Dictionary<ProcessNode, NodeWritten>(ReferenceEqualityComparer.Instance);
        long rows;
        using (var aw = new CsvOut(ActivityPath(hostFolder), ActivityHeaders))
        using (var sw = new CsvOut(SetsPath(hostFolder), SetHeaders))
        {
            foreach (var n in ordered.Where(x => !x.IsGhost))
            {
                ct.ThrowIfCancellationRequested();
                written[n] = WriteNode(hostFolder, activity, n, aw, sw);
            }
            aw.Commit();
            sw.Commit();
            rows = aw.Rows;
        }
        ct.ThrowIfCancellationRequested();
        PersistenceCollector.WriteCsv(NodesPath(hostFolder), NodeHeaders, ordered.Select(n => NodeRow(n, activity, written.GetValueOrDefault(n))));
        WriteList(hostFolder, c.Graph, activity);
        var manifest = new ProcessCorrelationManifest
        {
            Version = FormatVersion, BuiltUtc = DateTime.UtcNow, Inputs = inputs.ToList(), Nodes = realNodes, ActivityRows = rows,
            SourceCounts = c.Graph.SourceCounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            Warnings = c.Graph.Warnings.Concat(c.Warnings).ToList(),
        };
        var json = InputsPath(hostFolder);
        File.WriteAllText(json + ".tmp", JsonSerializer.Serialize(manifest, JsonOpts), new UTF8Encoding(false));
        File.Move(json + ".tmp", json, overwrite: true);
        return (realNodes, rows);
    }

    /// <summary>Nodes in tree order (roots, each followed by its subtree, children in order), then any node not reached from a root.</summary>
    public static List<ProcessNode> Ordered(ProcessGraph g)
    {
        var list = new List<ProcessNode>(g.AllNodes.Count);
        var done = new HashSet<ProcessNode>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<ProcessNode>();
        foreach (var root in g.Roots.Concat(g.AllNodes))
        {
            if (done.Contains(root)) continue;
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!done.Add(n)) continue;
                list.Add(n);
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }
        return list;
    }

    /// <summary>Every row set of the node's data tabs (activity rows: linked rows only, Exact / PathTime) and its Sources set.</summary>
    private static NodeWritten WriteNode(string hostFolder, ProcessActivity activity, ProcessNode n, CsvOut aw, CsvOut sw)
    {
        DateTime? first = null, last = null;
        long setsFirst = sw.Rows;
        var pid = n.Pid.ToString(CultureInfo.InvariantCulture);
        foreach (var tab in ProcessActivity.DataTabs)
        {
            IReadOnlyList<ProcessRowSet> sets;
            try { sets = activity.SetsFor(n, tab); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { continue; }
            foreach (var s in sets)
            {
                long actFirst = aw.Rows;
                foreach (var r in s.Rows)
                {
                    if (r.Tier is not { } tier) continue;   // facts only: Exact / PathTime
                    if (r.Time is { } t)
                    {
                        if (first == null || t < first) first = t;
                        if (last == null || t > last) last = t;
                    }
                    aw.Write(new[]
                    {
                        n.Key, pid, n.Name, T(r.Time), ProcessActivity.TabTitle(tab), s.Title,
                        r.Source, tier.ToString(), ProcessActivity.Summarize(s.Headers, r.Cells),
                        r.Origin is { } o ? Relative(hostFolder, o.CsvPath) : "", r.Origin is { } o2 ? EncodeFilter(o2.Filter) : "",
                        s.Id, JsonCells(r.Cells),
                    });
                }
                sw.Write(SetRow(n, s, actFirst, aw.Rows - actFirst, ""));
            }
        }
        ProcessRowSet? sources = null;
        try { sources = activity.Sources(n); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        if (sources != null)
            sw.Write(SetRow(n, sources, aw.Rows, 0, JsonList(sources.Rows.Select(r => new[] { r.Source }.Concat(r.Cells).ToArray()).ToList())));
        return new NodeWritten(first, last, setsFirst, sw.Rows - setsFirst);
    }

    private static string[] SetRow(ProcessNode n, ProcessRowSet s, long actFirst, long actCount, string rows) => new[]
    {
        n.Key, s.Tab.ToString(), s.Id, s.Title, s.Status.ToString(), s.Message, s.SourceLabel, s.Remark, s.TotalRows.ToString(CultureInfo.InvariantCulture),
        JsonCells(s.Headers.ToArray()), actFirst.ToString(CultureInfo.InvariantCulture), actCount.ToString(CultureInfo.InvariantCulture), rows,
    };

    /// <summary>The cells as a JSON array of strings, each cell shortened (with "…") until the JSON fits in one CSV cell.</summary>
    internal static string JsonCells(string[] cells)
    {
        var json = JsonSerializer.Serialize(cells.Select(x => x ?? "").ToArray());
        for (int cap = 8000; json.Length > MaxJsonCell && cap >= 16; cap /= 2)
        {
            int k = cap;
            json = JsonSerializer.Serialize(cells.Select(x => (x ?? "").Length > k ? x![..k] + "…" : x ?? "").ToArray());
        }
        return json;
    }

    /// <summary>A list of rows as JSON (<c>[[...], ...]</c>), its rows' cells shortened until it fits in one CSV cell.</summary>
    private static string JsonList(List<string[]> rows)
    {
        var json = JsonSerializer.Serialize(rows);
        for (int cap = 2000; json.Length > MaxJsonCell && cap >= 16; cap /= 2)
        {
            int k = cap;
            json = JsonSerializer.Serialize(rows.Select(r => r.Select(x => (x ?? "").Length > k ? x![..k] + "…" : x ?? "").ToArray()).ToList());
        }
        return json;
    }

    /// <summary>A CSV written to a temporary name and moved into place on <see cref="Commit"/> (left out and deleted when disposed without it).</summary>
    private sealed class CsvOut : IDisposable
    {
        private readonly string _path, _tmp;
        private StreamWriter? _w;

        public CsvOut(string path, IReadOnlyList<string> headers)
        {
            _path = path;
            _tmp = path + ".tmp";
            _w = new StreamWriter(_tmp, false, new UTF8Encoding(false)) { NewLine = "\r\n" };
            _w.WriteLine(string.Join(",", headers.Select(PersistenceCollector.Esc)));
        }

        /// <summary>Data rows written so far (the next row's 0-based number).</summary>
        public long Rows { get; private set; }

        public void Write(string[] row)
        {
            _w!.WriteLine(string.Join(",", row.Select(PersistenceCollector.Esc)));
            Rows++;
        }

        public void Commit()
        {
            _w!.Dispose();
            _w = null;
            File.Move(_tmp, _path, overwrite: true);
        }

        public void Dispose()
        {
            if (_w == null) return;
            _w.Dispose();
            _w = null;
            try { File.Delete(_tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string[] NodeRow(ProcessNode n, ProcessActivity activity, NodeWritten seen)
    {
        Dictionary<ProcessTab, int> counts;
        if (n.IsGhost) counts = new();
        else try { counts = activity.Counts(n); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { counts = new(); }
        string? sig;
        if (n.IsGhost) sig = null;
        else try { sig = ProcessSignature.Describe(activity.SignatureFor(n)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { sig = null; }
        IReadOnlyList<string> services;
        if (n.IsGhost) services = Array.Empty<string>();
        else try { services = activity.ServiceCount(n) > 0 ? activity.ServiceNames(n, ProcessFacetIndex.MaxRows) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { services = Array.Empty<string>(); }
        static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
        static string L(long v) => v.ToString(CultureInfo.InvariantCulture);
        return new[]
        {
            n.Key, I(n.Pid), I(n.Ppid), n.Parent?.Key ?? "", n.Name, n.Path, n.CmdLine, n.User, T(n.CreateTime), T(n.ExitTime), n.Sources.ToString(), n.Markers.ToString(),
            n.Hash, n.HashSource, sig ?? "", n.FileVersion, T(seen.First), T(seen.Last),
        }.Concat(CountTabs.Select(t => I(counts.GetValueOrDefault(t))))
         .Concat(new[] { n.Guid, n.ParentGuid, n.ParentImage, n.Offset, I(n.SessionId), n.Wow64 is { } w ? (w ? "True" : "False") : "", I(n.Threads), I(n.Handles),
             n.Company, n.Product, n.VersionInfoSource, n.UserSource, n.Integrity, n.Role, n.RoleBasis.ToString(), ProcessIndicators.KindsText(n), n.IsGhost ? "True" : "False",
             services.Count == 0 ? "" : JsonCells(services.ToArray()), n.IsGhost ? "" : L(seen.SetsFirstRow), n.IsGhost ? "" : L(seen.SetsRowCount) })
         .ToArray();
    }

    private static string T(DateTime? t) => t?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? "";

    internal static DateTime? ParseTime(string s) =>
        DateTime.TryParseExact(s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    // ------------------------------------------------------------------ load

    /// <summary>
    /// The graph of an up-to-date Processes.csv (see <see cref="IsUpToDate"/>): nodes, parent / child links, roots, source counts and warnings; no event or disk
    /// evidence (the details read the live indexes). Null when the output is missing, stale or unreadable.
    /// </summary>
    public static ProcessGraph? LoadGraph(string hostFolder)
    {
        if (!IsUpToDate(hostFolder)) return null;
        var m = ReadManifest(hostFolder);
        try
        {
            var g = ReadNodes(NodesPath(hostFolder));
            if (m != null)
            {
                foreach (var (k, v) in m.SourceCounts) if (Enum.TryParse<ProcessSources>(k, out var s)) g.SourceCounts[s] = v;
                g.Warnings.AddRange(m.Warnings);
            }
            g.SourceFiles.Add(NodesPath(hostFolder));
            return g;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CsvRecordTooLargeException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Reads a Processes.csv into a graph (no freshness check).</summary>
    public static ProcessGraph ReadNodes(string path) => ReadNodes(path, null);

    /// <summary>Reads a Processes.csv; <paramref name="extra"/> gets each node with a reader of its row's cells by column name.</summary>
    internal static ProcessGraph ReadNodes(string path, Action<ProcessNode, Func<string, string>>? extra)
    {
        var g = new ProcessGraph();
        var byKey = new Dictionary<string, ProcessNode>(StringComparer.Ordinal);
        var parents = new List<(ProcessNode Node, string ParentKey)>();
        using var tr = new StreamReader(path, Encoding.UTF8, true);
        using var r = CsvDataReader.Create(tr, new CsvDataReaderOptions { HasHeaders = true, BufferSize = 1 << 20 });
        var h = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < r.FieldCount; i++) h[r.GetName(i)] = i;
        string S(string col) => h.TryGetValue(col, out var i) && i < r.FieldCount ? r.GetString(i) : "";
        int? N(string col) => int.TryParse(S(col), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        while (r.Read())
        {
            var n = new ProcessNode
            {
                Pid = N("PID") ?? 0, Ppid = N("PPID") ?? 0, Name = S("Name"), Path = S("Path"), CmdLine = S("CommandLine"), User = S("User"),
                CreateTime = ParseTime(S("CreateTime")), ExitTime = ParseTime(S("ExitTime")),
                Sources = Enum.TryParse<ProcessSources>(S("Sources"), out var src) ? src : ProcessSources.None,
                Markers = Enum.TryParse<ProcessMarkers>(S("Markers"), out var mk) ? mk : ProcessMarkers.None,
                Hash = S("Hash"), HashSource = S("HashSource"), FileVersion = S("FileVersion"),
                Guid = S("Guid"), ParentGuid = S("ParentGuid"), ParentImage = S("ParentImage"), Offset = S("Offset"),
                SessionId = N("SessionId"), Wow64 = S("Wow64") switch { "True" => true, "False" => false, _ => null }, Threads = N("Threads"), Handles = N("Handles"),
                Company = S("Company"), Product = S("Product"), VersionInfoSource = S("VersionInfoSource"), UserSource = S("UserSource"), Integrity = S("Integrity"),
                IsGhost = S("IsGhost") == "True",
            };
            (n.IsGhost ? g.Ghosts : g.AllNodes).Add(n);
            extra?.Invoke(n, S);
            byKey.TryAdd(n.Key, n);
            if (S("ParentKey") is { Length: > 0 } pk) parents.Add((n, pk));
        }
        foreach (var (n, pk) in parents)
            if (byKey.TryGetValue(pk, out var p) && !ReferenceEquals(p, n) && !IsAncestorOrSelf(n, p))
            {
                n.Parent = p;
                p.Children.Add(n);
            }
        g.Roots.AddRange(g.AllNodes.Concat(g.Ghosts).Where(n => n.Parent == null));
        ProcessIndicators.Annotate(g);
        return g;
    }

    /// <summary>True when <paramref name="node"/> is <paramref name="from"/> or one of its ancestors (a link node to from would close a cycle in a damaged CSV).</summary>
    private static bool IsAncestorOrSelf(ProcessNode node, ProcessNode from)
    {
        int guard = 0;
        for (var c = from; c != null && guard < 100_000; c = c.Parent, guard++)
            if (ReferenceEquals(c, node)) return true;
        return false;
    }

    /// <summary>The CSV and grid filter of a Processes_Activity.csv row (its SourceFile and SourceRow cells); null when the row has none.</summary>
    public static ProcessRowOrigin? OriginOf(string hostFolder, string sourceFile, string sourceRow) =>
        sourceFile.Length > 0 && DecodeFilter(sourceRow) is { } f ? new ProcessRowOrigin(Absolute(hostFolder, sourceFile), f) : null;

    // ------------------------------------------------------------------ SourceRow: the grid filter of an origin

    /// <summary>
    /// The filter of a row origin as JSON <c>[["Column","Op","Value"],...]</c> (conditions ANDed): what <see cref="ProcessRowLocator.GridTarget"/> needs to reopen the
    /// row. Empty for a filter that is not a condition or an AND of conditions.
    /// </summary>
    public static string EncodeFilter(FilterNode filter)
    {
        var conds = new List<FilterCondition>();
        if (!Flatten(filter, conds) || conds.Count == 0) return "";
        return JsonSerializer.Serialize(conds.Select(c => new[] { c.Column, c.Op.ToString(), c.Value }).ToList());
    }

    private static bool Flatten(FilterNode f, List<FilterCondition> into)
    {
        switch (f)
        {
            case FilterCondition c when c.Value2 == null: into.Add(c); return true;
            case FilterGroup { Kind: FilterGroupKind.And, Negate: false } g: return g.Children.All(ch => Flatten(ch, into));
            default: return false;
        }
    }

    /// <summary>The filter of <see cref="EncodeFilter"/> (a single condition, or an AND group); null when empty or not readable.</summary>
    public static FilterNode? DecodeFilter(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            var items = JsonSerializer.Deserialize<List<string[]>>(encoded);
            if (items == null || items.Count == 0) return null;
            var conds = new List<FilterNode>();
            foreach (var x in items)
            {
                if (x == null || x.Length < 3 || x[0] == null || x[2] == null || !Enum.TryParse<ColumnFilterMode>(x[1], out var op)) return null;
                conds.Add(new FilterCondition(x[0], op, x[2]));
            }
            return conds.Count == 1 ? conds[0] : FilterGroup.And(conds.ToArray());
        }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------ headless job: EzForensics.exe --process-correlator --host <folder> [--force]

    /// <summary>The Ingest / Analyze job of one host: the EZ Forensics executable runs <see cref="RunCommandLine"/>.</summary>
    public static JobSpec Spec(string exe, string hostFolder, bool force = false, string? label = null) =>
        new(ToolId, Path.GetFileName(exe), force ? new[] { Switch, "--host", hostFolder, "--force" } : new[] { Switch, "--host", hostFolder },
            FolderFor(hostFolder), label ?? "Process tree data: " + Path.GetFileName(hostFolder.TrimEnd('\\', '/')), JobCategory,
            ExePath: exe, Timeout: TimeSpan.FromMinutes(60), ExeVersion: typeof(ProcessCorrelator).Assembly.GetName().Version?.ToString(),
            ManifestDetails: new Dictionary<string, string>
            {
                ["engine"] = "EZ Forensics built-in process correlator (links: Exact and PathTime only)",
                ["evidenceAccess"] = "read-only; reads the host's tool output CSVs in the case folder",
                ["host"] = hostFolder,
            });

    /// <summary>Entry point of the built-in job. Exit code 0 = written or up to date, 1 = bad arguments / no process source, 2 = failed or cancelled.</summary>
    public static int RunCommandLine(IReadOnlyList<string> args, TextWriter stdout, CancellationToken ct = default)
    {
        string? host = null;
        bool force = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--host" when i + 1 < args.Count: host = args[++i]; break;
                case "--force": force = true; break;
            }
        }
        if (host == null || !Directory.Exists(host))
        {
            stdout.WriteLine(host == null ? "Usage: --process-correlator --host <case host folder> [--force]" : "Error: the host folder does not exist: " + host);
            return 1;
        }
        var o = RunAsync(host, force, stdout.WriteLine, ct).GetAwaiter().GetResult();
        stdout.WriteLine(Describe(o));
        return o.State switch
        {
            ProcessCorrelationState.Built or ProcessCorrelationState.UpToDate => 0,
            ProcessCorrelationState.NoSource => 1,
            _ => 2,
        };
    }

    /// <summary>One line for the job output and the Ingest row.</summary>
    public static string Describe(ProcessCorrelationOutcome o) => o.State switch
    {
        ProcessCorrelationState.Built => $"Process correlation of {o.Host}: {o.Nodes:N0} processes, {o.ActivityRows:N0} linked rows.",
        ProcessCorrelationState.UpToDate => $"Process correlation of {o.Host} is up to date ({o.Nodes:N0} processes, {o.ActivityRows:N0} linked rows).",
        ProcessCorrelationState.NoSource => $"Process correlation of {o.Host} was not built. {o.Error}".TrimEnd(),
        ProcessCorrelationState.Cancelled => $"The process correlation of {o.Host} was cancelled.",
        _ => $"The process correlation of {o.Host} failed: {o.Error}".TrimEnd(),
    };
}
