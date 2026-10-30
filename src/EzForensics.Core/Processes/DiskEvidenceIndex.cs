using System.Globalization;
using System.Text;
using Sylvan.Data.Csv;

namespace EzForensics.Core.Processes;

/// <summary>One PECmd row. <see cref="SourceFile"/> is its SourceFilename cell (the .pf file); <see cref="CsvPath"/> the PECmd CSV it was read from.</summary>
public sealed record PrefetchEntry(string Executable, string RunCount, string LastRun, string PreviousRuns, string SourceFile, IReadOnlyList<string> LoadedPathKeys, IReadOnlyList<DateTime> RunTimes, string CsvPath = "");

/// <summary>A Prefetch row linked to a process: the run times that fit the process start.</summary>
public readonly record struct PrefetchMatch(PrefetchEntry Entry, IReadOnlyList<DateTime> Runs);

/// <summary>
/// Prefetch (PECmd) rows of a host. A row belongs to a process only by <see cref="LinkTier.PathTime"/>: its FilesLoaded entry ending with the executable name is the
/// process's full path, one of its run times (LastRun, PreviousRun0-7) fits the process start (tolerance from <c>sources.json</c>), and exactly one instance fits.
/// Amcache and Shimcache are not process evidence (they record no run). Immutable once read.
/// </summary>
public sealed class DiskEvidenceIndex
{
    private readonly List<PrefetchEntry> _prefetch = new();
    private readonly Dictionary<string, List<PrefetchEntry>> _pfByKey = new();
    private ProcessLifetimes _life = new(Array.Empty<ProcessNode>());
    private TimeFit _fit = new(TimeRole.Start, -2, 15);

    public bool HasPrefetch { get; private set; }
    public bool IsEmpty => !HasPrefetch;

    public static DiskEvidenceIndex Read(ProcessGraphFiles files, ProcessGraph graph)
    {
        var idx = new DiskEvidenceIndex { _life = new ProcessLifetimes(graph.AllNodes) };
        if (ProcessEvidenceCatalog.Find("prefetch")?.Fit is { } fit) idx._fit = fit;
        foreach (var f in files.Prefetch) idx.ReadCsv(f, graph, (r, h, _) => idx.OnPrefetch(r, h, f), h => h.ContainsKey("executablename"), () => idx.HasPrefetch = true);
        return idx;
    }

    private void ReadCsv(string path, ProcessGraph graph, Action<CsvDataReader, Dictionary<string, int>, string> onRow,
        Func<Dictionary<string, int>, bool> accepts, Action found)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
            using var tr = new StreamReader(fs, new UTF8Encoding(false), true, 65536);
            CsvDataReader r;
            try { r = CsvDataReader.Create(tr, new CsvDataReaderOptions { HasHeaders = true, BufferSize = 1 << 20 }); }
            catch (CsvMissingHeadersException) { return; }
            using (r)
            {
                var h = new Dictionary<string, int>();
                for (int i = 0; i < r.FieldCount; i++) h.TryAdd(ProcessLogText.Norm(r.GetName(i)), i);
                if (!accepts(h)) return;   // e.g. Amcache program / device tables
                found();
                graph.SourceFiles.Add(path);
                var name = System.IO.Path.GetFileName(path);
                while (r.Read()) onRow(r, h, name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CsvRecordTooLargeException or InvalidOperationException or CsvFormatException)
        {
            graph.Warnings.Add($"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static string Cell(CsvDataReader r, Dictionary<string, int> h, params string[] names)
    {
        foreach (var n in names) if (h.TryGetValue(n, out var i)) return (r.GetString(i) ?? "").Trim();
        return "";
    }

    private static void Add<T>(Dictionary<string, List<T>> d, string key, T v)
    {
        if (key.Length == 0) return;
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<T>();
        l.Add(v);
    }

    private void OnPrefetch(CsvDataReader r, Dictionary<string, int> h, string csvPath)
    {
        var exe = Cell(r, h, "executablename");
        if (exe.Length == 0) return;
        var prev = new List<string>();
        var runs = new List<DateTime>();
        void AddRun(string text)
        {
            if (text.Length > 0 && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) runs.Add(t);
        }
        var last = Cell(r, h, "lastrun");
        AddRun(last);
        for (int i = 0; i < 8; i++) { var p = Cell(r, h, "previousrun" + i); if (p.Length > 0) { prev.Add(p); AddRun(p); } }
        // the program path is the FilesLoaded entry that ends with the executable name; without one the row names no full path and links to nothing
        var loaded = new List<string>();
        var fl = Cell(r, h, "filesloaded");
        if (fl.Length > 0)
        {
            var suffix = "\\" + exe.ToLowerInvariant();
            foreach (var part in fl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var key = ProcessLogText.PathKey(part);
                if (key.EndsWith(suffix, StringComparison.Ordinal)) loaded.Add(key);
            }
        }
        var e = new PrefetchEntry(exe, Cell(r, h, "runcount"), last, string.Join(" | ", prev), Cell(r, h, "sourcefilename"), loaded, runs, csvPath);
        _prefetch.Add(e);
        foreach (var key in loaded.Distinct()) Add(_pfByKey, key, e);
    }

    /// <summary>Prefetch rows of the process by path + run time: the run times that fit the process start, with exactly one instance fitting (see <see cref="ProcessLifetimes.IsUniquePathTime"/>).</summary>
    public List<PrefetchMatch> FindPrefetch(ProcessNode n)
    {
        var key = ProcessLifetimes.PathKeyOf(n);
        var res = new List<PrefetchMatch>();
        if (key.Length == 0 || !_pfByKey.TryGetValue(key, out var list)) return res;
        foreach (var e in list)
        {
            var fits = e.RunTimes.Where(t => _life.IsUniquePathTime(n, t, _fit)).ToList();
            if (fits.Count > 0) res.Add(new PrefetchMatch(e, fits));
        }
        return res;
    }
}

/// <summary>Everything besides memory that describes the processes of a host: event-log rows and disk artifacts. Built once with the graph.</summary>
public sealed class ProcessEvidence
{
    public const int MaxRows = 2000;

    public ProcessEventIndex Events { get; init; } = new();
    public DiskEvidenceIndex Disk { get; init; } = new();

    public static bool IsEvidenceKind(ProcessFacetKind k) => k >= ProcessFacetKind.EvtAll;

    /// <summary>Source label for the note under a facet grid.</summary>
    public static string SourceLabel(ProcessFacetKind k) => k switch
    {
        ProcessFacetKind.EvtAll => "Event logs (Security 4688 / 4689, Sysmon)",
        ProcessFacetKind.EvtProcess => "Event logs (Security 4688 / 4689, Sysmon 1 / 5)",
        ProcessFacetKind.EvtNetwork => "Sysmon 3 events",
        ProcessFacetKind.EvtFiles => "Sysmon 11 events",
        ProcessFacetKind.EvtRegistry => "Sysmon 13 events",
        ProcessFacetKind.DiskPrefetch => "Prefetch (linked by full path + run time)",
        _ => k.ToString(),
    };

    public bool HasSource(ProcessFacetKind k) => k switch
    {
        ProcessFacetKind.DiskPrefetch => Disk.HasPrefetch,
        _ => Events.HasFiles,
    };

    /// <summary>Why a facet has no source at all.</summary>
    public static string NotAvailableMessage(ProcessFacetKind k) => k switch
    {
        ProcessFacetKind.DiskPrefetch => "No Prefetch CSV (PECmd) for this host - run Prefetch from Ingest.",
        _ => "No event log CSV (EvtxECmd) for this host - run Event logs from Ingest.",
    };

    public FacetStatus GetStatus(ProcessFacetKind k, ProcessNode node)
    {
        if (!HasSource(k)) return FacetStatus.PluginNotRun;
        return Get(k, node).TotalRows > 0 ? FacetStatus.Available : FacetStatus.NoRows;
    }

    public FacetRows Get(ProcessFacetKind k, ProcessNode node)
    {
        string[] headers;
        List<string[]> rows;
        static string T(DateTime? t) => t == null ? "" : t.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        switch (k)
        {
            case ProcessFacetKind.DiskPrefetch:
                headers = new[] { "Matched run", "Executable", "Run count", "Last run", "Previous runs", "Prefetch file" };
                rows = Disk.FindPrefetch(node).Select(m => new[] { string.Join(" | ", m.Runs.Select(t => T(t))), m.Entry.Executable, m.Entry.RunCount, m.Entry.LastRun, m.Entry.PreviousRuns, m.Entry.SourceFile }).ToList();
                break;
            default:
            {
                var ev = Events.For(node);
                switch (k)
                {
                    case ProcessFacetKind.EvtNetwork:
                        headers = new[] { "Time", "Event Id", "Image", "Destination host", "Destination IP", "Port", "Source IP" };
                        rows = ev.Where(e => e.Group == ProcessEventGroup.Network).Select(e => new[] { T(e.Time), Id(e), e.Image, e.T1, e.T2, e.T3, e.T4 }).ToList();
                        break;
                    case ProcessFacetKind.EvtFiles:
                        headers = new[] { "Time", "Event Id", "Image", "File" };
                        rows = ev.Where(e => e.Group == ProcessEventGroup.Files).Select(e => new[] { T(e.Time), Id(e), e.Image, e.T1 }).ToList();
                        break;
                    case ProcessFacetKind.EvtRegistry:
                        headers = new[] { "Time", "Event Id", "Image", "Key", "Value", "Type" };
                        rows = ev.Where(e => e.Group == ProcessEventGroup.Registry).Select(e => new[] { T(e.Time), Id(e), e.Image, e.T1, e.T2, e.T3 }).ToList();
                        break;
                    case ProcessFacetKind.EvtProcess:
                        headers = new[] { "Time", "Event Id", "Event", "Image", "Command line", "Parent image", "User" };
                        rows = ev.Where(e => e.Group == ProcessEventGroup.Process).Select(e => new[] { T(e.Time), Id(e), e.Title, e.Image, e.T1, e.T2, e.User }).ToList();
                        break;
                    default:
                        headers = new[] { "Time", "Event Id", "Event", "Image", "Detail", "User", "Source file" };
                        rows = ev.Select(e => new[] { T(e.Time), Id(e), e.Title, e.Image, e.Detail, e.User, e.SourceFile }).ToList();
                        break;
                }
                break;
            }
        }
        int total = rows.Count;
        if (rows.Count > MaxRows) rows = rows.GetRange(0, MaxRows);
        return new FacetRows
        {
            Status = total == 0 ? FacetStatus.NoRows : FacetStatus.Available,
            Headers = headers, Rows = rows, TotalRows = total,
        };
    }

    private static string Id(ProcessEventRecord e) => e.EventId.ToString(CultureInfo.InvariantCulture);
}
