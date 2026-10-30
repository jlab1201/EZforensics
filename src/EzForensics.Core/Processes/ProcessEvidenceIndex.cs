using System.Globalization;
using EzForensics.Core.Results;

namespace EzForensics.Core.Processes;

/// <summary>
/// How a source row is tied to a process. Only facts link: there is no name-only, path-only or time-window link, and a link that would leave a further question
/// (two instances fit) is not made.
/// </summary>
public enum LinkTier
{
    /// <summary>Same Sysmon GUID, or same PID with the row time inside the process lifetime (or without a time to compare).</summary>
    Exact,
    /// <summary>Same full normalised path, a row time that fits the process under the source's <see cref="TimeFit"/>, and exactly one instance fits.</summary>
    PathTime,
}

public enum EvidenceStatus
{
    Available,
    /// <summary>No CSV of the source was found: it was not collected (see <see cref="EvidenceSourceStatus.HowToGet"/>).</summary>
    NotFound,
    /// <summary>The source exists but has no rows (for a process lookup: none linked to that process).</summary>
    NoRows,
}

/// <summary>One CSV row linked to a process, with the tier it was linked by.</summary>
public sealed record LinkedRow(string[] Cells, LinkTier Tier, string SourceFile);

/// <summary>Global state of a catalog source in a host: which files were found and how many rows they hold.</summary>
public sealed record EvidenceSourceStatus(EvidenceSourceDef Source, EvidenceStatus Status, IReadOnlyList<string> Files, long TotalRows)
{
    public string HowToGet => Source.HowToGet;
}

/// <summary>Rows of one source linked to one process. <see cref="Rows"/> is capped at <see cref="ProcessFacetIndex.MaxRows"/>; <see cref="TotalRows"/> is the real count.</summary>
public sealed class EvidenceResult
{
    public EvidenceSourceDef Source { get; init; } = null!;
    public EvidenceStatus Status { get; init; }
    public string HowToGet => Source.HowToGet;
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<LinkedRow> Rows { get; init; } = Array.Empty<LinkedRow>();
    public int TotalRows { get; init; }
    public bool Truncated => TotalRows > Rows.Count;
}

/// <summary>
/// Disk and log evidence of a host linked to processes. Every CSV matching a catalog source (<see cref="ProcessEvidenceCatalog"/>) is opened as a
/// <see cref="CsvIndex"/> and scanned once in the background; only three key maps are kept in memory (PID, GUID, normalised full path with the row times), never the
/// rows. <see cref="Lookup"/> reads the linked rows from disk. To add a source: add an entry to <c>Resources/processes/sources.json</c> (path sources need a
/// <c>timeRole</c>). Sources with <c>reader: evtx</c> are not indexed here (see <see cref="ProcessEventIndex"/>). Call <see cref="Attach"/> with the graph nodes so that
/// PID reuse and path uniqueness are judged against all instances.
/// </summary>
public sealed class ProcessEvidenceIndex : IDisposable
{
    private const int ScanChunk = 2000;

    private readonly record struct RowRef(int File, long Row, long Ticks);
    /// <summary>A row reachable by path: every time the row carries (e.g. Prefetch LastRun and PreviousRun0-7).</summary>
    private readonly record struct PathRef(int File, long Row, long[] Times);

    private sealed class FileSource
    {
        public CsvIndex Index = null!;
        public EvidenceSourceDef Def = null!;
    }

    private readonly List<FileSource> _files = new();
    private readonly List<EvidenceSourceStatus> _statuses = new();
    private readonly Dictionary<int, List<RowRef>> _byPid = new();
    private readonly Dictionary<string, List<RowRef>> _byGuid = new();
    private readonly Dictionary<string, List<PathRef>> _byPath = new();
    private readonly List<string> _warnings = new();
    private ProcessLifetimes? _life;

    private ProcessEvidenceIndex() { }

    public IReadOnlyList<EvidenceSourceStatus> Statuses => _statuses;
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Gives the index the process instances of the graph (see <see cref="ProcessLifetimes"/>). Without it each node is judged alone.</summary>
    public void Attach(IEnumerable<ProcessNode> nodes, DateTime? evidenceEnd = null) => _life = new ProcessLifetimes(nodes, evidenceEnd);

    public EvidenceSourceStatus? StatusOf(string sourceId) => _statuses.FirstOrDefault(s => s.Source.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the catalog CSVs in <paramref name="hostFolder"/> and its category folders (not <c>Memory</c>) and indexes them in the background.</summary>
    public static Task<ProcessEvidenceIndex> BuildAsync(string hostFolder, CancellationToken ct, IReadOnlyList<EvidenceSourceDef>? catalog = null) =>
        BuildAsync(DiscoverFiles(hostFolder, catalog ?? ProcessEvidenceCatalog.Sources), ct, catalog);

    public static Task<ProcessEvidenceIndex> BuildAsync(IEnumerable<string> files, CancellationToken ct, IReadOnlyList<EvidenceSourceDef>? catalog = null)
    {
        var list = files.ToList();
        return Task.Run(async () =>
        {
            var idx = new ProcessEvidenceIndex();
            try
            {
                await idx.LoadAsync(list, catalog ?? ProcessEvidenceCatalog.Sources, ct).ConfigureAwait(false);
                return idx;
            }
            catch
            {
                idx.Dispose();
                throw;
            }
        }, ct);
    }

    /// <summary>Files of the host folder and its direct sub folders (except <c>Memory</c>) whose name belongs to a catalog source; only directory listings.</summary>
    public static List<string> DiscoverFiles(string hostFolder, IReadOnlyList<EvidenceSourceDef> catalog)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(hostFolder) || !Directory.Exists(hostFolder)) return found;
        var dirs = new List<string> { hostFolder };
        try { dirs.AddRange(Directory.GetDirectories(hostFolder).Where(d => !string.Equals(Path.GetFileName(d), "Memory", StringComparison.OrdinalIgnoreCase))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var d in dirs)
        {
            string[] csvs;
            try { csvs = Directory.GetFiles(d, "*.csv"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(csvs, StringComparer.OrdinalIgnoreCase);
            found.AddRange(csvs.Where(f => catalog.Any(s => s.Matches(Path.GetFileName(f)))));
        }
        return found;
    }

    // ------------------------------------------------------------------ build

    private sealed class ColumnMap
    {
        public int[] Pid = Array.Empty<int>(), Guid = Array.Empty<int>(), Path = Array.Empty<int>(), PathJoin = Array.Empty<int>(), Name = Array.Empty<int>(), Time = Array.Empty<int>();
    }

    private async Task LoadAsync(List<string> files, IReadOnlyList<EvidenceSourceDef> catalog, CancellationToken ct)
    {
        var perSource = catalog.ToDictionary(s => s.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var rowCounts = catalog.ToDictionary(s => s.Id, _ => 0L, StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var def = ProcessEvidenceCatalog.ForFile(Path.GetFileName(f), catalog);
            if (def == null) continue;
            perSource[def.Id].Add(f);

            CsvIndex? index = null;
            try
            {
                index = await CsvIndex.OpenAsync(f, null, ct).ConfigureAwait(false);
                await index.IndexingCompleted.ConfigureAwait(false);
                var cols = Resolve(index, def);
                long total = index.RowCount;
                rowCounts[def.Id] += total;
                if (total == 0) { index.Dispose(); continue; }
                if (cols.Pid.Length + cols.Guid.Length + cols.Path.Length + cols.PathJoin.Length == 0)
                {
                    _warnings.Add($"{Path.GetFileName(f)} has none of the columns {def.Label} is linked by.");
                    index.Dispose();
                    continue;
                }

                int fileNo = _files.Count;
                _files.Add(new FileSource { Index = index, Def = def });
                for (long start = 0; start < total; start += ScanChunk)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = index.ReadRows(start, ScanChunk);
                    for (int i = 0; i < rows.Length; i++) AddRow(fileNo, start + i, rows[i], def, cols);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Sylvan.Data.Csv.CsvRecordTooLargeException)
            {
                // a half scanned file keeps its map entries; reading them later reports the disposed index as not found
                index?.Dispose();
                _warnings.Add($"Could not read {Path.GetFileName(f)}: {ex.Message}");
            }
        }

        foreach (var def in catalog)
        {
            var found = perSource[def.Id];
            var status = def.IsCsv
                ? found.Count == 0 ? EvidenceStatus.NotFound : rowCounts[def.Id] == 0 ? EvidenceStatus.NoRows : EvidenceStatus.Available
                : EvidenceStatus.NotFound;   // evtx sources are read elsewhere; nothing was looked for here
            _statuses.Add(new EvidenceSourceStatus(def, status, found, rowCounts[def.Id]));
        }
    }

    private static ColumnMap Resolve(CsvIndex index, EvidenceSourceDef def)
    {
        var norm = index.Headers.Select(ProcessLogText.Norm).ToList();
        int[] Find(IReadOnlyList<string> aliases) =>
            aliases.Select(a => norm.IndexOf(a)).Where(i => i >= 0).ToArray();
        var pj = def.Columns.PathJoin.Select(a => norm.IndexOf(a)).ToArray();
        return new ColumnMap
        {
            Pid = Find(def.Columns.Pid), Guid = Find(def.Columns.Guid), Path = Find(def.Columns.Path), Name = Find(def.Columns.Name), Time = Find(def.Columns.Time),
            PathJoin = pj.Length > 0 && pj.All(i => i >= 0) ? pj : Array.Empty<int>(),
        };
    }

    private void AddRow(int fileNo, long rowNo, string[] row, EvidenceSourceDef def, ColumnMap c)
    {
        var times = c.Time.Select(i => ParseTicks(Cell(row, i))).Where(t => t != 0).ToArray();
        long ticks = times.Length > 0 ? times[0] : 0;

        // paths: one cell, a joined pair of cells, or a list in one cell
        var paths = new List<string>();
        if (c.PathJoin.Length > 0) paths.Add(JoinPath(Cell(row, c.PathJoin[0]), Cell(row, c.PathJoin[^1])));
        else
        {
            var cell = First(row, c.Path);
            if (cell.Length > 0)
            {
                if (def.Columns.PathList) paths.AddRange(cell.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                else paths.Add(def.Columns.ExeFromCommand ? ProcessLogText.ExePath(cell) : cell);
            }
        }

        var name = First(row, c.Name);
        if (name.Length == 0 && paths.Count > 0) name = ProcessLogText.FileNameOf(paths[0]);
        name = NameKey(name);
        if (def.RowFilter != null && !def.RowFilter.IsMatch(name)) return;

        var r = new RowRef(fileNo, rowNo, ticks);
        var pr = new PathRef(fileNo, rowNo, times);
        var pid = c.Pid.Length > 0 ? ProcessLogText.ParsePid(First(row, c.Pid)) : null;
        if (pid is > 0) Add(_byPid, pid.Value, r);
        var guid = c.Guid.Length > 0 ? ProcessLogText.NormGuid(First(row, c.Guid)) : "";
        if (guid.Length > 0) Add(_byGuid, guid, r);

        HashSet<string>? seen = null;
        foreach (var p in paths)
        {
            // a path list holds the other files of the program too: only the program itself counts
            if (def.Columns.PathList && name.Length > 0 && !NameKey(ProcessLogText.FileNameOf(p)).Equals(name, StringComparison.Ordinal)) continue;
            var key = ProcessLogText.PathKey(p);
            if (key.Length == 0 || !(seen ??= new HashSet<string>()).Add(key)) continue;
            Add(_byPath, key, pr);
        }
    }

    private static void Add<K, V>(Dictionary<K, List<V>> d, K key, V r) where K : notnull
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<V>();
        l.Add(r);
    }

    private static string Cell(string[] row, int col) => col >= 0 && col < row.Length ? (row[col] ?? "").Trim() : "";

    private static string First(string[] row, int[] cols)
    {
        foreach (var c in cols) { var v = Cell(row, c); if (v.Length > 0) return v; }
        return "";
    }

    /// <summary>MFTECmd style: <c>.\Windows\System32</c> + <c>cmd.exe</c> to <c>\Windows\System32\cmd.exe</c>.</summary>
    public static string JoinPath(string parent, string name)
    {
        if (name.Length == 0) return "";
        if (parent.Length == 0) return name;
        if (parent.StartsWith(".\\", StringComparison.Ordinal) || parent == ".") parent = parent[1..];
        return parent.TrimEnd('\\') + "\\" + name;
    }

    private static string NameKey(string name) => name.Trim().Trim('"').TrimEnd('*').ToLowerInvariant();

    private static long ParseTicks(string s) =>
        s.Length > 0 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t.Ticks : 0;

    // ------------------------------------------------------------------ lookup

    /// <summary>Rows of every catalog source linked to the process (one result per source, in catalog order).</summary>
    public IReadOnlyList<EvidenceResult> LookupAll(ProcessNode node)
    {
        var tiers = Match(node);
        var results = new List<EvidenceResult>();
        foreach (var st in _statuses) results.Add(Build(st, tiers));
        return results;
    }

    public EvidenceResult Lookup(ProcessNode node, string sourceId)
    {
        var st = StatusOf(sourceId) ?? throw new ArgumentException("Unknown evidence source " + sourceId, nameof(sourceId));
        return Build(st, Match(node));
    }

    private EvidenceResult Build(EvidenceSourceStatus st, Dictionary<(int File, long Row), LinkTier> tiers)
    {
        if (st.Status == EvidenceStatus.NotFound) return new EvidenceResult { Source = st.Source, Status = EvidenceStatus.NotFound };
        var mine = tiers.Where(kv => ReferenceEquals(_files[kv.Key.File].Def, st.Source)).OrderBy(kv => kv.Value).ThenBy(kv => kv.Key.File).ThenBy(kv => kv.Key.Row).ToList();
        if (mine.Count == 0) return new EvidenceResult { Source = st.Source, Status = EvidenceStatus.NoRows };

        IReadOnlyList<string> headers = Array.Empty<string>();
        var rows = new List<LinkedRow>();
        foreach (var (key, tier) in mine)
        {
            if (rows.Count >= ProcessFacetIndex.MaxRows) break;
            var f = _files[key.File];
            if (headers.Count == 0) headers = f.Index.Headers;
            try { rows.Add(new LinkedRow(f.Index.ReadRow(key.Row), tier, Path.GetFileName(f.Index.Path))); }
            catch (ObjectDisposedException) { return new EvidenceResult { Source = st.Source, Status = EvidenceStatus.NotFound }; }
        }
        return new EvidenceResult { Source = st.Source, Status = EvidenceStatus.Available, Headers = headers, Rows = rows, TotalRows = mine.Count };
    }

    /// <summary>Row to its strongest tier. Each map adds its rows, a row seen by several keeps the lowest (strongest) tier.</summary>
    private Dictionary<(int File, long Row), LinkTier> Match(ProcessNode node)
    {
        var res = new Dictionary<(int, long), LinkTier>();
        void Put(RowRef r, LinkTier t)
        {
            var k = (r.File, r.Row);
            if (!res.TryGetValue(k, out var old) || t < old) res[k] = t;
        }

        var life = _life ?? new ProcessLifetimes(new[] { node });

        if (node.Guid.Length > 0 && _byGuid.TryGetValue(node.Guid, out var g))
            foreach (var r in g) Put(r, LinkTier.Exact);

        // a PID is reused: with a row time and a process lifetime the time must fall inside it
        if (node.Pid > 0 && _byPid.TryGetValue(node.Pid, out var p))
            foreach (var r in p) if (life.PidFits(node, r.Ticks == 0 ? null : new DateTime(r.Ticks, DateTimeKind.Utc))) Put(r, LinkTier.Exact);

        // full path + a time that fits the source's rule + exactly one instance fitting; never by name, path alone or a time window
        var key = ProcessLifetimes.PathKeyOf(node);
        if (key.Length > 0 && _byPath.TryGetValue(key, out var byPath))
            foreach (var r in byPath)
            {
                var fit = _files[r.File].Def.Fit;
                if (fit == null) continue;
                foreach (var t in r.Times)
                    if (life.IsUniquePathTime(node, new DateTime(t, DateTimeKind.Utc), fit)) { Put(new RowRef(r.File, r.Row, t), LinkTier.PathTime); break; }
            }
        return res;
    }

    public void Dispose()
    {
        foreach (var f in _files) f.Index.Dispose();
        _files.Clear();
        _byPid.Clear(); _byGuid.Clear(); _byPath.Clear();
    }
}
