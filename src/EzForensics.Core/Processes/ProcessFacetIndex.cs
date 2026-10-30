using System.Globalization;
using EzForensics.Core.Results;

namespace EzForensics.Core.Processes;

/// <summary>Kinds of per-process detail taken from the Volatility CSVs other than the process lists.</summary>
public enum ProcessFacetKind
{
    CmdLine, Dlls, LdrModules, Network, HandlesFiles, HandlesRegistry, Injected, Services, Sids, Privileges, EnvVars, Sessions,
    /// <summary>cmdscan + consoles rows. Keyed by the conhost PID; consoles rows also by every PID its ConsoleProcessList names (the attached cmd / powershell).</summary>
    ConsoleHistory,
    /// <summary>verinfo: PE version numbers (Major / Minor / Product / Build) of the process image and its DLLs. Version info, not a signature.</summary>
    VersionInfo,
    /// <summary>malware.hollowprocesses notes.</summary>
    Hollowing,
    /// <summary>malware.processghosting: image file object delete-pending / delete-on-close.</summary>
    Ghosting,
    /// <summary>malware.suspicious_threads: threads starting outside a mapped image.</summary>
    SuspiciousThreads,
    /// <summary>malware.pebmasquerade: one row per process, EPROCESS vs PEB names and the two Spoofed flags.</summary>
    PebMasquerade,
    /// <summary>threads: every thread with start address and path.</summary>
    Threads,
    /// <summary>getservicesids (SID, Service; no PID column): the rows of the services svcscan lists for this PID, joined by service name.</summary>
    ServiceSids,
    /// <summary>joblinks: job objects and the processes in them.</summary>
    JobLinks,
    /// <summary>handles of Type Mutant / Process / Thread / Section / Event. Process and Thread rows get an extra <see cref="ProcessFacetIndex.TargetPidColumn"/> column.</summary>
    HandlesMutant, HandlesProcess, HandlesThread, HandlesSection, HandlesEvent,
    // Kinds from here on come from event logs and disk artifacts (see ProcessEvidence), not from Volatility CSVs.
    EvtAll, EvtProcess, EvtNetwork, EvtFiles, EvtRegistry, DiskPrefetch,
}

public enum FacetStatus
{
    /// <summary>The plugin's CSV exists (and, when a PID was asked for, has rows for it).</summary>
    Available,
    /// <summary>No CSV of the plugin was found: it was not run (see <see cref="FacetRows.PluginId"/>).</summary>
    PluginNotRun,
    /// <summary>The plugin ran but has no rows for this process.</summary>
    NoRows,
}

/// <summary>Rows of one facet for one process. <see cref="Rows"/> is capped; <see cref="TotalRows"/> is the real count.</summary>
public sealed class FacetRows
{
    public static FacetRows NotRun(string pluginId) => new() { Status = FacetStatus.PluginNotRun, PluginId = pluginId };

    public FacetStatus Status { get; init; }
    /// <summary>Id in <c>Resources/memory/plugins.json</c> of the plugin that produces this facet (e.g. "handles").</summary>
    public string PluginId { get; init; } = "";
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string[]> Rows { get; init; } = Array.Empty<string[]>();
    public int TotalRows { get; init; }
    public bool Truncated => TotalRows > Rows.Count;
}

/// <summary>
/// Per-process detail (command line, DLLs, network, handles, ...) from a host's Volatility CSVs. Each CSV is opened as a <see cref="CsvIndex"/> (byte offsets only,
/// rows are read on demand) and one pass records the row numbers of every PID, so large files (handles, dlllist) are never held in memory.
/// Build once in the background with <see cref="BuildAsync"/>; the finished index is immutable and safe to query from any thread. Dispose to release the files.
/// </summary>
public sealed class ProcessFacetIndex : IDisposable
{
    public const int MaxRows = 5000;
    private const int ScanChunk = 2000;
    /// <summary>Column appended to <see cref="ProcessFacetKind.HandlesProcess"/> / <see cref="ProcessFacetKind.HandlesThread"/> rows: the PID the handle points to (from the Name column, "notepad.exe Pid 1234" / "Tid 88 Pid 1234"); empty when it names none.</summary>
    public const string TargetPidColumn = "TargetPID";

    private static readonly (ProcessFacetKind Kind, string PluginId, string[] Variants)[] Defs =
    {
        (ProcessFacetKind.CmdLine, "cmdline", new[] { "CmdLine" }),
        (ProcessFacetKind.Dlls, "dlllist", new[] { "DllList" }),
        (ProcessFacetKind.LdrModules, "ldrmodules", new[] { "LdrModules" }),
        (ProcessFacetKind.Network, "netscan", new[] { "NetScan", "NetStat" }),
        (ProcessFacetKind.HandlesFiles, "handles", new[] { "Handles" }),
        (ProcessFacetKind.HandlesRegistry, "handles", new[] { "Handles" }),
        (ProcessFacetKind.Injected, "malfind", new[] { "Malfind" }),
        (ProcessFacetKind.Services, "svcscan", new[] { "SvcScan" }),
        (ProcessFacetKind.Sids, "getsids", new[] { "GetSids" }),
        (ProcessFacetKind.Privileges, "privileges", new[] { "Privileges" }),
        (ProcessFacetKind.EnvVars, "envars", new[] { "EnvVars" }),
        (ProcessFacetKind.Sessions, "sessions", new[] { "Sessions" }),
        (ProcessFacetKind.ConsoleHistory, "cmdscan", new[] { "CmdScan", "Consoles" }),
        (ProcessFacetKind.VersionInfo, "verinfo", new[] { "VerInfo" }),
        (ProcessFacetKind.Hollowing, "hollowprocesses", new[] { "HollowProcesses" }),
        (ProcessFacetKind.Ghosting, "processghosting", new[] { "ProcessGhosting" }),
        (ProcessFacetKind.SuspiciousThreads, "suspicious_threads", new[] { "SuspiciousThreads" }),
        (ProcessFacetKind.PebMasquerade, "pebmasquerade", new[] { "PebMasquerade" }),
        (ProcessFacetKind.Threads, "threads", new[] { "Threads" }),
        (ProcessFacetKind.ServiceSids, "getservicesids", new[] { "GetServiceSids" }),
        (ProcessFacetKind.JobLinks, "joblinks", new[] { "JobLinks" }),
        (ProcessFacetKind.HandlesMutant, "handles", new[] { "Handles" }),
        (ProcessFacetKind.HandlesProcess, "handles", new[] { "Handles" }),
        (ProcessFacetKind.HandlesThread, "handles", new[] { "Handles" }),
        (ProcessFacetKind.HandlesSection, "handles", new[] { "Handles" }),
        (ProcessFacetKind.HandlesEvent, "handles", new[] { "Handles" }),
    };

    /// <summary>handles Type to facet; other types (Token, Desktop, ALPC Port, ...) are not split out.</summary>
    private static readonly Dictionary<string, ProcessFacetKind> HandleKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["File"] = ProcessFacetKind.HandlesFiles, ["Key"] = ProcessFacetKind.HandlesRegistry, ["Mutant"] = ProcessFacetKind.HandlesMutant,
        ["Process"] = ProcessFacetKind.HandlesProcess, ["Thread"] = ProcessFacetKind.HandlesThread, ["Section"] = ProcessFacetKind.HandlesSection,
        ["Event"] = ProcessFacetKind.HandlesEvent,
    };

    // Header aliases; headers are compared lower case with everything but letters and digits removed.
    private static readonly string[] PidCols = { "pid", "processid", "owningpid" };
    private static readonly string[] TypeCols = { "type" };
    private static readonly string[] HandleNameCols = { "name" };
    private static readonly string[] PropertyCols = { "property" };
    private static readonly string[] DataCols = { "data" };
    private static readonly string[] ConsoleInfoCols = { "consoleinfo" };
    private static readonly string[] ServiceCols = { "service" };
    private static readonly string[] ServiceNameCols = { "name", "servicename" };

    // consoles: "_CONSOLE_INFORMATION.ConsoleProcessList.ConsoleProcess_0_ProcessId" carries the PID of a process attached to the console
    private static readonly System.Text.RegularExpressions.Regex AttachedPidProperty =
        new(@"ConsoleProcess_\d+_ProcessId$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // handles Name of a Process / Thread handle: "notepad.exe Pid 1234", "Tid 5678 Pid 1234"
    private static readonly System.Text.RegularExpressions.Regex HandlePidRx =
        new(@"(?:^|\s)Pid\s+(?<p>\d+)\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private sealed class Source
    {
        public CsvIndex Index = null!;
        public string PidColumn = "";
        public Dictionary<int, List<long>> ByPid = new();
        /// <summary>Process / Thread handles: target PID to rows (any holder).</summary>
        public Dictionary<int, List<long>>? ByTarget;
        /// <summary>getservicesids: lower case service name to rows.</summary>
        public Dictionary<string, List<long>>? ByService;
        /// <summary>Column the target PID is parsed from (handles Name), -1 for other facets.</summary>
        public int NameCol = -1;
    }

    private readonly Dictionary<ProcessFacetKind, List<Source>> _sources = new();
    private readonly List<CsvIndex> _indexes = new();
    private readonly List<string> _sourceFiles = new();
    private readonly List<string> _warnings = new();

    private ProcessFacetIndex() { }

    /// <summary>CSV files the facets are read from.</summary>
    public IReadOnlyList<string> SourceFiles => _sourceFiles;
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Plugin id (plugins.json) producing the facet, so the UI can say "windows.handles not run".</summary>
    public static string PluginIdOf(ProcessFacetKind kind) => Defs.FirstOrDefault(d => d.Kind == kind).PluginId ?? "";

    public static Task<ProcessFacetIndex> BuildAsync(string hostFolder, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var idx = new ProcessFacetIndex();
            try
            {
                await idx.LoadAsync(ProcessGraphBuilder.MemoryCsvFiles(hostFolder), ct).ConfigureAwait(false);
                return idx;
            }
            catch
            {
                idx.Dispose();
                throw;
            }
        }, ct);

    private async Task LoadAsync(string[] files, CancellationToken ct)
    {
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(f);
            var kinds = Defs.Where(d => ProcessGraphBuilder.Matches(stem, d.Variants)).Select(d => d.Kind).ToList();
            if (kinds.Count == 0) continue;

            CsvIndex? index = null;
            try
            {
                index = await CsvIndex.OpenAsync(f, null, ct).ConfigureAwait(false);
                await index.IndexingCompleted.ConfigureAwait(false);
                if (kinds.Contains(ProcessFacetKind.ServiceSids))
                {
                    // SID, Service: no PID; joined to a process through the svcscan rows of that PID (see GetServiceSids)
                    LoadByService(index, f, ct);
                    index = null;
                    continue;
                }
                int pidCol = index.Headers.Count == 0 ? -1 : FindCol(index, PidCols);
                if (pidCol < 0)
                {
                    // an empty file still proves the plugin ran; a file without a PID column cannot be split per process
                    if (index.Headers.Count > 0) _warnings.Add($"{Path.GetFileName(f)} has no PID column.");
                    index.Dispose();
                    index = null;
                    foreach (var k in kinds) if (!_sources.ContainsKey(k)) _sources[k] = new List<Source>();
                    _sourceFiles.Add(f);
                    continue;
                }

                var perKind = kinds.ToDictionary(k => k, k => new Source { Index = index, PidColumn = index.Headers[pidCol] });
                bool handles = kinds.Contains(ProcessFacetKind.HandlesFiles);
                int typeCol = handles ? FindCol(index, TypeCols) : -1;
                int nameCol = handles ? FindCol(index, HandleNameCols) : -1;
                if (handles)
                    foreach (var k in new[] { ProcessFacetKind.HandlesProcess, ProcessFacetKind.HandlesThread })
                    {
                        perKind[k].NameCol = nameCol;
                        perKind[k].ByTarget = new Dictionary<int, List<long>>();
                    }
                // consoles rows of one console (conhost PID + ConsoleInfo) also belong to every process its ConsoleProcessList names
                bool consoles = ProcessGraphBuilder.Matches(stem, new[] { "Consoles" });
                int propCol = consoles ? FindCol(index, PropertyCols) : -1, dataCol = consoles ? FindCol(index, DataCols) : -1, infoCol = consoles ? FindCol(index, ConsoleInfoCols) : -1;
                var blocks = consoles ? new Dictionary<(int Pid, string Info), (List<long> Rows, HashSet<int> Attached)>() : null;
                long total = index.RowCount;
                for (long start = 0; start < total; start += ScanChunk)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = index.ReadRows(start, ScanChunk);
                    for (int i = 0; i < rows.Length; i++)
                    {
                        var row = rows[i];
                        if (!int.TryParse(Cell(row, pidCol).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) continue;   // "N/A", empty
                        long rowNo = start + i;
                        if (handles)
                        {
                            if (!HandleKinds.TryGetValue(Cell(row, typeCol).Trim(), out var kind)) continue;
                            var s = perKind[kind];
                            Add(s, pid, rowNo);
                            if (s.ByTarget != null && HandleTargetPid(Cell(row, nameCol)) is { } target) Put(s.ByTarget, target, rowNo);
                        }
                        else if (blocks != null)
                        {
                            var key = (pid, Cell(row, infoCol).Trim());
                            if (!blocks.TryGetValue(key, out var b)) blocks[key] = b = (new List<long>(), new HashSet<int>());
                            b.Rows.Add(rowNo);
                            if (AttachedPidProperty.IsMatch(Cell(row, propCol).Trim())
                                && int.TryParse(Cell(row, dataCol).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attached) && attached != pid)
                                b.Attached.Add(attached);
                        }
                        else
                        {
                            foreach (var s in perKind.Values) Add(s, pid, rowNo);
                        }
                    }
                }
                if (blocks != null)
                    foreach (var ((pid, _), (rowNos, attached)) in blocks)
                        foreach (var s in perKind.Values)
                            foreach (var p in attached.Prepend(pid))
                                foreach (var n in rowNos) Add(s, p, n);
                foreach (var s in perKind.Values)
                    foreach (var l in s.ByPid.Values) l.Sort();

                _indexes.Add(index);
                _sourceFiles.Add(f);
                foreach (var (k, s) in perKind)
                {
                    if (!_sources.TryGetValue(k, out var list)) _sources[k] = list = new List<Source>();
                    list.Add(s);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Sylvan.Data.Csv.CsvRecordTooLargeException)
            {
                index?.Dispose();
                _warnings.Add($"Could not read {Path.GetFileName(f)}: {ex.Message}");
            }
            catch
            {
                index?.Dispose();   // cancelled (or failed) mid-scan: the file is not in _indexes yet, so Dispose() of the index would not release it
                throw;
            }
        }
    }

    private static void Add(Source s, int pid, long row) => Put(s.ByPid, pid, row);

    private static void Put<K>(Dictionary<K, List<long>> map, K key, long row) where K : notnull
    {
        if (!map.TryGetValue(key, out var l)) map[key] = l = new List<long>();
        l.Add(row);
    }

    private void LoadByService(CsvIndex index, string file, CancellationToken ct)
    {
        int col = index.Headers.Count == 0 ? -1 : FindCol(index, ServiceCols);
        var s = new Source { Index = index, ByService = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase) };
        if (col < 0 && index.Headers.Count > 0) _warnings.Add($"{Path.GetFileName(file)} has no Service column.");
        long total = col < 0 ? 0 : index.RowCount;
        for (long start = 0; start < total; start += ScanChunk)
        {
            ct.ThrowIfCancellationRequested();
            var rows = index.ReadRows(start, ScanChunk);
            for (int i = 0; i < rows.Length; i++)
            {
                var name = Cell(rows[i], col).Trim();
                if (name.Length > 0) Put(s.ByService, name, start + i);
            }
        }
        _indexes.Add(index);
        _sourceFiles.Add(file);
        if (!_sources.TryGetValue(ProcessFacetKind.ServiceSids, out var list)) _sources[ProcessFacetKind.ServiceSids] = list = new List<Source>();
        list.Add(s);
    }

    /// <summary>The PID a Process / Thread handle points to, from its Name ("notepad.exe Pid 1234", "Tid 5678 Pid 1234"); null when it names none.</summary>
    public static int? HandleTargetPid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var m = HandlePidRx.Match(name.Trim());
        return m.Success && int.TryParse(m.Groups["p"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static string Cell(string[] row, int col) => col >= 0 && col < row.Length ? row[col] ?? "" : "";

    private static string Norm(string header) =>
        new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int FindCol(CsvIndex index, string[] aliases)
    {
        foreach (var a in aliases)
            for (int i = 0; i < index.Headers.Count; i++)
                if (Norm(index.Headers[i]) == a) return i;
        return -1;
    }

    /// <summary>Available when a CSV of the facet's plugin exists, otherwise <see cref="FacetStatus.PluginNotRun"/>; with a <paramref name="pid"/>, <see cref="FacetStatus.NoRows"/> when it has none for that process.</summary>
    public FacetStatus GetStatus(ProcessFacetKind kind, int? pid = null)
    {
        if (!_sources.TryGetValue(kind, out var list)) return FacetStatus.PluginNotRun;
        if (pid == null) return FacetStatus.Available;
        if (kind == ProcessFacetKind.ServiceSids) return Get(kind, pid.Value).TotalRows > 0 ? FacetStatus.Available : FacetStatus.NoRows;
        return list.Any(s => s.ByPid.ContainsKey(pid.Value)) ? FacetStatus.Available : FacetStatus.NoRows;
    }

    /// <summary>
    /// Rows of the facet for a process (at most <see cref="MaxRows"/>), read from disk now. <see cref="ProcessFacetKind.HandlesProcess"/> / <see cref="ProcessFacetKind.HandlesThread"/>
    /// rows end with a <see cref="TargetPidColumn"/> cell; <see cref="ProcessFacetKind.ServiceSids"/> rows are those of the services svcscan lists for the PID.
    /// </summary>
    public FacetRows Get(ProcessFacetKind kind, int pid)
    {
        var pluginId = PluginIdOf(kind);
        if (!_sources.TryGetValue(kind, out var list)) return FacetRows.NotRun(pluginId);
        if (kind == ProcessFacetKind.ServiceSids) return GetServiceSids(list, pid);
        return Read(kind, list, s => s.ByPid.GetValueOrDefault(pid));
    }

    /// <summary>
    /// Process and Thread handles that other processes hold to <paramref name="pid"/> ("PID X has a handle to this process"), with <see cref="TargetPidColumn"/>.
    /// The process's own handles to itself are left out. Same image only as far as the PID is concerned: the rows come from the memory CSVs of the host.
    /// </summary>
    public FacetRows HandlesTo(int pid)
    {
        var headers = (IReadOnlyList<string>)Array.Empty<string>();
        var rows = new List<string[]>();
        int total = 0;
        bool any = false;
        foreach (var kind in new[] { ProcessFacetKind.HandlesProcess, ProcessFacetKind.HandlesThread })
        {
            if (!_sources.TryGetValue(kind, out var list)) continue;
            any = true;
            var r = Read(kind, list, s => s.ByTarget?.GetValueOrDefault(pid), holderPid => holderPid != pid);
            if (headers.Count == 0 && r.Headers.Count > 0) headers = r.Headers;
            total += r.TotalRows;
            rows.AddRange(r.Rows.Take(MaxRows - rows.Count));
        }
        if (!any) return FacetRows.NotRun("handles");
        return new FacetRows { Status = total == 0 ? FacetStatus.NoRows : FacetStatus.Available, PluginId = "handles", Headers = headers, Rows = rows, TotalRows = total };
    }

    private FacetRows Read(ProcessFacetKind kind, List<Source> list, Func<Source, List<long>?> rowsOf, Func<int, bool>? holderFilter = null)
    {
        var pluginId = PluginIdOf(kind);
        bool target = kind is ProcessFacetKind.HandlesProcess or ProcessFacetKind.HandlesThread;
        IReadOnlyList<string> headers = Array.Empty<string>();
        var rows = new List<string[]>();
        int total = 0;
        foreach (var s in list)
        {
            var nums = rowsOf(s);
            if (nums == null || nums.Count == 0) continue;
            if (headers.Count == 0) headers = target ? s.Index.Headers.Append(TargetPidColumn).ToList() : s.Index.Headers;
            int pidCol = holderFilter == null ? -1 : IndexOf(s.Index.Headers, s.PidColumn);
            foreach (var n in nums)
            {
                if (holderFilter == null && rows.Count >= MaxRows) { total++; continue; }   // counted, not read
                string[] row;
                try { row = s.Index.ReadRow(n); }
                catch (ObjectDisposedException) { return FacetRows.NotRun(pluginId); }
                if (holderFilter != null && int.TryParse(Cell(row, pidCol).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var holder) && !holderFilter(holder)) continue;
                total++;
                if (rows.Count >= MaxRows) continue;
                if (target) row = row.Append(HandleTargetPid(Cell(row, s.NameCol)) is { } t ? t.ToString(CultureInfo.InvariantCulture) : "").ToArray();
                rows.Add(row);
            }
        }
        return new FacetRows
        {
            Status = total == 0 ? FacetStatus.NoRows : FacetStatus.Available,
            PluginId = pluginId, Headers = headers, Rows = rows, TotalRows = total,
        };
    }

    private static int IndexOf(IReadOnlyList<string> headers, string name)
    {
        for (int i = 0; i < headers.Count; i++) if (headers[i] == name) return i;
        return -1;
    }

    /// <summary>
    /// Names of the services svcscan lists for <paramref name="pid"/> (the services hosted in that process, e.g. a svchost.exe), distinct, in row order, at most
    /// <paramref name="cap"/>. Empty when svcscan was not run or has no rows for the PID. Reads the rows from disk.
    /// </summary>
    public IReadOnlyList<string> ServiceNames(int pid, int cap = MaxRows)
    {
        if (Count(ProcessFacetKind.Services, pid) == 0) return Array.Empty<string>();
        var services = Get(ProcessFacetKind.Services, pid);
        int col = -1;
        foreach (var a in ServiceNameCols)
        {
            for (int i = 0; i < services.Headers.Count && col < 0; i++) if (Norm(services.Headers[i]) == a) col = i;
            if (col >= 0) break;
        }
        var names = new List<string>();
        if (col < 0) return names;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in services.Rows)
        {
            var n = Cell(r, col).Trim();
            if (n.Length > 0 && seen.Add(n)) { names.Add(n); if (names.Count >= cap) break; }
        }
        return names;
    }

    /// <summary>PIDs that have svcscan rows (cheap: no rows are read).</summary>
    public IReadOnlyCollection<int> PidsWithServices() =>
        _sources.TryGetValue(ProcessFacetKind.Services, out var list) ? list.SelectMany(s => s.ByPid.Keys).ToHashSet() : (IReadOnlyCollection<int>)Array.Empty<int>();

    /// <summary>getservicesids rows whose Service is the Name of a svcscan row of <paramref name="pid"/> (a fact of the same image: the service runs in this process).</summary>
    private FacetRows GetServiceSids(List<Source> list, int pid)
    {
        var names = new HashSet<string>(ServiceNames(pid), StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return new FacetRows { Status = FacetStatus.NoRows, PluginId = PluginIdOf(ProcessFacetKind.ServiceSids) };
        var merged = new Dictionary<Source, List<long>>();
        foreach (var s in list)
        {
            var l = new List<long>();
            foreach (var n in names) if (s.ByService != null && s.ByService.TryGetValue(n, out var rows)) l.AddRange(rows);
            l.Sort();
            merged[s] = l;
        }
        return Read(ProcessFacetKind.ServiceSids, list, s => merged.GetValueOrDefault(s));
    }

    /// <summary>Rows of the facet for the PID without reading them (the tab counts); <see cref="ProcessFacetKind.ServiceSids"/> is read through <see cref="Get"/>. 0 when not run.</summary>
    public int Count(ProcessFacetKind kind, int pid)
    {
        if (!_sources.TryGetValue(kind, out var list)) return 0;
        if (kind == ProcessFacetKind.ServiceSids) return Get(kind, pid).TotalRows;
        return list.Sum(s => s.ByPid.TryGetValue(pid, out var l) ? l.Count : 0);
    }

    /// <summary>Every CSV of the facet (with rows or not), e.g. the handles CSV for "handles other processes hold to this one".</summary>
    public IReadOnlyList<string> PathsOf(ProcessFacetKind kind) =>
        _sources.TryGetValue(kind, out var list) ? list.Select(s => s.Index.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : Array.Empty<string>();

    /// <summary>CSV files of the facet that have rows for <paramref name="pid"/>, with the header of their PID column (Analyze "Open rows in grid"). None for <see cref="ProcessFacetKind.ServiceSids"/> (no PID column).</summary>
    public IReadOnlyList<(string Path, string PidColumn)> FilesFor(ProcessFacetKind kind, int pid)
    {
        if (!_sources.TryGetValue(kind, out var list)) return Array.Empty<(string, string)>();
        return list.Where(s => s.ByPid.ContainsKey(pid)).Select(s => (s.Index.Path, s.PidColumn)).ToList();
    }

    public void Dispose()
    {
        foreach (var i in _indexes) i.Dispose();
        _indexes.Clear();
        _sources.Clear();
    }
}
