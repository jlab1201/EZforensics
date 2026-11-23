using System.Globalization;
using System.Text;
using Sylvan.Data.Csv;

namespace EzForensics.Core.Processes;

/// <summary>CSV files (Volatility 3 output) the process graph is built from; each list can hold several files.</summary>
public sealed class ProcessGraphFiles
{
    public List<string> PsList { get; } = new();
    public List<string> PsScan { get; } = new();
    public List<string> PsTree { get; } = new();
    public List<string> CmdLine { get; } = new();
    public List<string> Malfind { get; } = new();
    /// <summary>malware.hollowprocesses / processghosting / suspicious_threads / pebmasquerade CSVs (node markers) and verinfo (node version info).</summary>
    public List<string> Hollow { get; } = new();
    public List<string> Ghosting { get; } = new();
    public List<string> SuspiciousThreads { get; } = new();
    public List<string> PebMasquerade { get; } = new();
    public List<string> VerInfo { get; } = new();
    /// <summary>getsids (token user and integrity label) and sessions (user fallback) CSVs.</summary>
    public List<string> GetSids { get; } = new();
    public List<string> Sessions { get; } = new();
    /// <summary>EvtxECmd CSVs (artifact "eventlog"): Security 4688 and Sysmon 1 create process nodes, other Sysmon events fill the Events facets.</summary>
    public List<string> EventLogs { get; } = new();
    public List<string> Prefetch { get; } = new();
    /// <summary>At least one pslist / psscan / pstree CSV: a process tree can be drawn.</summary>
    public bool HasProcessLists => PsList.Count + PsScan.Count + PsTree.Count > 0;
    /// <summary>A process tree can be drawn: Volatility process lists, or an event log CSV (only its existence is checked, the file is not scanned).</summary>
    public bool CanBuildTree => HasProcessLists || EventLogs.Count > 0;
}

/// <summary>
/// Builds a <see cref="ProcessGraph"/> from the Volatility 3 process CSVs of one host (<c>Memory_PsList</c>, <c>PsScan</c>, <c>PsTree</c>, <c>CmdLine</c>,
/// <c>Malfind</c> and the Linux variants). Rows are merged on (PID, creation time); columns are found by header name, never by position.
/// </summary>
public static class ProcessGraphBuilder
{
    private static readonly string[] PsListVariants = { "PsList", "LinuxPsList" };
    private static readonly string[] PsScanVariants = { "PsScan" };
    private static readonly string[] PsTreeVariants = { "PsTree", "LinuxPsTree" };
    private static readonly string[] CmdLineVariants = { "CmdLine" };
    private static readonly string[] MalfindVariants = { "Malfind" };

    // Header aliases; headers are compared lower case with everything but letters and digits removed ("Offset(V)" = "offsetv").
    private static readonly string[] PidCols = { "pid" };
    private static readonly string[] PpidCols = { "ppid" };
    private static readonly string[] TidCols = { "tid" };
    private static readonly string[] NameCols = { "imagefilename", "comm", "name", "process" };
    private static readonly string[] OffsetCols = { "offsetv", "offsetp", "offset" };
    private static readonly string[] ThreadCols = { "threads", "numthreads" };
    private static readonly string[] HandleCols = { "handles", "numhandles" };
    private static readonly string[] SessionCols = { "sessionid", "session" };
    private static readonly string[] Wow64Cols = { "wow64" };
    private static readonly string[] CreateCols = { "createtime", "creationtime", "starttime" };
    private static readonly string[] ExitCols = { "exittime" };
    private static readonly string[] PathCols = { "path", "imagepath", "fullpath" };
    private static readonly string[] CmdCols = { "cmd", "args", "commandline", "cmdline" };

    /// <summary>Finds the host's Volatility process CSVs (in <c>&lt;hostFolder&gt;\Memory</c>, or the folder itself when it holds them) and builds the graph.</summary>
    public static ProcessGraph Build(string hostFolder) => Build(FindFiles(hostFolder));

    /// <summary>Locates the process CSVs of a host folder.</summary>
    public static ProcessGraphFiles FindFiles(string hostFolder)
    {
        var files = new ProcessGraphFiles();
        foreach (var f in MemoryCsvFiles(hostFolder))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (Matches(stem, PsListVariants)) files.PsList.Add(f);
            else if (Matches(stem, PsScanVariants)) files.PsScan.Add(f);
            else if (Matches(stem, PsTreeVariants)) files.PsTree.Add(f);
            else if (Matches(stem, CmdLineVariants)) files.CmdLine.Add(f);
            else if (Matches(stem, MalfindVariants)) files.Malfind.Add(f);
            else if (Matches(stem, new[] { "HollowProcesses" })) files.Hollow.Add(f);
            else if (Matches(stem, new[] { "ProcessGhosting" })) files.Ghosting.Add(f);
            else if (Matches(stem, new[] { "SuspiciousThreads" })) files.SuspiciousThreads.Add(f);
            else if (Matches(stem, new[] { "PebMasquerade" })) files.PebMasquerade.Add(f);
            else if (Matches(stem, new[] { "VerInfo" })) files.VerInfo.Add(f);
            else if (Matches(stem, new[] { "GetSids" })) files.GetSids.Add(f);
            else if (Matches(stem, new[] { "Sessions" })) files.Sessions.Add(f);
        }
        FindHostCsvs(hostFolder, files);
        return files;
    }

    private static readonly System.Text.RegularExpressions.Regex EventLogName = new(@"^(EventLogs|EvtxECmd_Output)(_[^.]*)?\.csv$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex PrefetchName = new(@"^Prefetch(_[0-9]+)?\.csv$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>EventLogs / Prefetch CSVs in the host folder and its category folders (<c>host\Category\EventLogs.csv</c>); only directory listings, no file is opened.</summary>
    private static void FindHostCsvs(string hostFolder, ProcessGraphFiles files)
    {
        if (string.IsNullOrWhiteSpace(hostFolder) || !Directory.Exists(hostFolder)) return;
        var dirs = new List<string> { hostFolder };
        try { dirs.AddRange(Directory.GetDirectories(hostFolder).Where(d => !string.Equals(System.IO.Path.GetFileName(d), "Memory", StringComparison.OrdinalIgnoreCase))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var d in dirs)
        {
            string[] csvs;
            try { csvs = Directory.GetFiles(d, "*.csv"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(csvs, StringComparer.OrdinalIgnoreCase);
            foreach (var f in csvs)
            {
                var n = System.IO.Path.GetFileName(f);
                if (EventLogName.IsMatch(n)) files.EventLogs.Add(f);
                else if (PrefetchName.IsMatch(n)) files.Prefetch.Add(f);
            }
        }
    }

    /// <summary>A pslist / psscan / pstree CSV (<c>Memory_PsList[_qualifier].csv</c> ...): Analyze offers "View as process tree" for it.</summary>
    public static bool IsProcessListCsv(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(path);
        return Matches(stem, PsListVariants) || Matches(stem, PsScanVariants) || Matches(stem, PsTreeVariants);
    }

    /// <summary>All <c>Memory_*.csv</c> files of a host (in <c>&lt;hostFolder&gt;\Memory</c>, or the folder itself), sorted by name; shared with <see cref="ProcessFacetIndex"/>.</summary>
    internal static string[] MemoryCsvFiles(string hostFolder)
    {
        if (string.IsNullOrWhiteSpace(hostFolder)) return Array.Empty<string>();
        var memory = Path.Combine(hostFolder, "Memory");
        var dir = Directory.Exists(memory) ? memory : hostFolder;
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        string[] all;
        try { all = Directory.GetFiles(dir, "Memory_*.csv"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        Array.Sort(all, StringComparer.OrdinalIgnoreCase);
        return all;
    }

    /// <summary><c>Memory_&lt;Variant&gt;</c> or <c>Memory_&lt;Variant&gt;_&lt;qualifier&gt;</c>.</summary>
    internal static bool Matches(string stem, string[] variants)

    {
        foreach (var v in variants)
        {
            var p = "Memory_" + v;
            if (stem.Equals(p, StringComparison.OrdinalIgnoreCase) || stem.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static ProcessGraph Build(ProcessGraphFiles files)
    {
        var graph = new ProcessGraph();
        var byPid = new Dictionary<int, List<ProcessNode>>();
        bool sawPsList = false;

        foreach (var (list, source) in new[]
        {
            (files.PsList, ProcessSources.PsList), (files.PsScan, ProcessSources.PsScan),
            (files.PsTree, ProcessSources.PsTree), (files.CmdLine, ProcessSources.None),
        })
        {
            if (list.Count > 1) graph.Warnings.Add($"{list.Count} {source} files found (several images?); their rows are merged.");
            foreach (var path in list)
            {
                var rows = ReadRows(path, graph);
                if (rows == null) continue;
                graph.SourceFiles.Add(path);
                if (source == ProcessSources.PsList) sawPsList = true;
                if (source != ProcessSources.None)
                    graph.SourceCounts[source] = graph.SourceCounts.GetValueOrDefault(source) + rows.Count;
                foreach (var row in rows) Merge(graph, byPid, row, source);
            }
        }

        var injected = new HashSet<int>();
        foreach (var path in files.Malfind)
        {
            var pids = ReadPids(path, graph);
            if (pids == null) continue;
            graph.SourceFiles.Add(path);
            injected.UnionWith(pids);
        }

        var events = new ProcessEventIndex();
        if (files.EventLogs.Count > 0)
        {
            var logs = EventLogProcessReader.Read(files.EventLogs, graph);
            events = logs.Events;
            MergeLogs(graph, byPid, logs);
        }
        events.Attach(graph.AllNodes);
        var disk = DiskEvidenceIndex.Read(files, graph);
        graph.Evidence = new ProcessEvidence { Events = events, Disk = disk };
        if (events.Truncated) graph.Warnings.Add("Event log rows were capped (too many process events); the Events facets may be incomplete.");

        foreach (var n in graph.AllNodes)
        {
            if (n.ExitTime != null) n.Markers |= ProcessMarkers.Exited;
            if (sawPsList && n.Sources.HasFlag(ProcessSources.PsScan) && !n.Sources.HasFlag(ProcessSources.PsList) && n.ExitTime == null)
                n.Markers |= ProcessMarkers.Hidden;
            if (injected.Contains(n.Pid)) n.Markers |= ProcessMarkers.Injected;
        }

        // malware.* plugins: a row for the PID in the same memory CSVs marks the memory instance (never a node seen only in the logs)
        var hollow = MarkerPids(files.Hollow, graph);
        var ghosted = MarkerPids(files.Ghosting, graph);
        var threads = MarkerPids(files.SuspiciousThreads, graph);
        var peb = MarkerPids(files.PebMasquerade, graph, Spoofed);   // pebmasquerade prints every process: only a True Spoofed flag counts
        foreach (var n in graph.AllNodes)
        {
            if (!InMemory(n)) continue;
            if (hollow.Contains(n.Pid)) n.Markers |= ProcessMarkers.Hollowed;
            if (ghosted.Contains(n.Pid)) n.Markers |= ProcessMarkers.Ghosted;
            if (threads.Contains(n.Pid)) n.Markers |= ProcessMarkers.SuspiciousThreads;
            if (peb.Contains(n.Pid)) n.Markers |= ProcessMarkers.PebMasquerade;
        }
        ApplyVerInfo(graph, byPid, files.VerInfo);
        ApplyTokens(graph, byPid, files.GetSids, files.Sessions);   // after MergeLogs: a 4688 / Sysmon 1 user wins

        Link(graph, byPid);
        AttachGhosts(graph);
        ProcessIndicators.Annotate(graph);
        return graph;
    }

    /// <summary>The node was seen by a Volatility process list (pslist / psscan / pstree), so memory plugin rows of its PID are about it.</summary>
    internal static bool InMemory(ProcessNode n) => (n.Sources & (ProcessSources.PsList | ProcessSources.PsScan | ProcessSources.PsTree)) != 0;

    // ---- memory plugins: markers and version info -------------------------------------------------------------------------------------------------

    private static readonly string[] ModuleNameCols = { "name", "module", "basedllname" };
    private static readonly string[] VersionPartCols = { "major", "minor", "product", "build" };
    private static readonly string[] CompanyCols = { "companyname", "company" };
    private static readonly string[] ProductNameCols = { "productname" };

    /// <summary>PIDs with at least one row (that <paramref name="keep"/> accepts) in the CSVs.</summary>
    private static HashSet<int> MarkerPids(List<string> paths, ProcessGraph graph, Func<Dictionary<string, int>, CsvDataReader, bool>? keep = null)
    {
        var set = new HashSet<int>();
        foreach (var p in paths)
        {
            int pidCol = -2;
            bool read = ReadTable(p, graph, (h, r) =>
            {
                if (pidCol == -2) pidCol = Col(h, PidCols);
                if (Int(r, pidCol) is { } pid && (keep == null || keep(h, r))) set.Add(pid);
            });
            if (read) graph.SourceFiles.Add(p);
        }
        return set;
    }

    /// <summary>pebmasquerade: a <c>PEB_ImageFilePath_Spoofed</c> or <c>PEB_CommandLine_Spoofed</c> cell is True. (2.28.2 swaps the two values; either one counts, so it does not matter.)</summary>
    private static bool Spoofed(Dictionary<string, int> h, CsvDataReader r)
    {
        foreach (var (name, col) in h)
            if (name.EndsWith("spoofed", StringComparison.Ordinal) && Bool(Str(r, col)) == true) return true;
        return false;
    }

    private sealed record VerRow(string Module, string Version, string Company, string Product);

    /// <summary>
    /// verinfo: for each PID, the row whose module is the node's main image (the file name of its path, else its image name; EPROCESS names are cut at 14
    /// characters) gives Company / Product / FileVersion. Only when exactly one memory instance of the PID has such a row.
    /// </summary>
    private static void ApplyVerInfo(ProcessGraph graph, Dictionary<int, List<ProcessNode>> byPid, List<string> paths)
    {
        var byPidRows = new Dictionary<int, List<VerRow>>();
        foreach (var p in paths)
        {
            int pidCol = -2, modCol = -1, coCol = -1, prCol = -1;
            int[] parts = Array.Empty<int>();
            bool read = ReadTable(p, graph, (h, r) =>
            {
                if (pidCol == -2)
                {
                    pidCol = Col(h, PidCols); modCol = Col(h, ModuleNameCols); coCol = Col(h, CompanyCols); prCol = Col(h, ProductNameCols);
                    parts = VersionPartCols.Select(c => Col(h, new[] { c })).ToArray();
                }
                if (Int(r, pidCol) is not { } pid) return;   // kernel modules: N/A
                var nums = parts.Select(c => Int(r, c)).ToArray();
                var version = nums.Length == 4 && nums.All(x => x != null) ? string.Join(".", nums.Select(x => x!.Value.ToString(CultureInfo.InvariantCulture))) : "";
                var row = new VerRow(Str(r, modCol), version, Str(r, coCol), Str(r, prCol));
                if (row.Module.Length == 0 || (row.Version.Length == 0 && row.Company.Length == 0 && row.Product.Length == 0)) return;
                if (!byPidRows.TryGetValue(pid, out var l)) byPidRows[pid] = l = new List<VerRow>();
                l.Add(row);
            });
            if (read) graph.SourceFiles.Add(p);
        }

        foreach (var (pid, rows) in byPidRows)
        {
            if (!byPid.TryGetValue(pid, out var cands)) continue;
            var hits = cands.Where(InMemory).Select(n => (Node: n, Row: rows.FirstOrDefault(x => IsMainImage(n, x.Module)))).Where(t => t.Row != null).ToList();
            if (hits.Count != 1) continue;   // two instances of the PID with the same image: which one verinfo read is not known
            var (node, v) = (hits[0].Node, hits[0].Row!);
            node.FileVersion = v.Version;
            if (v.Company.Length > 0 || v.Product.Length > 0 || node.VersionInfoSource.Length == 0)
            {
                // verinfo 2.x has no company / product: keep what a Sysmon 1 event gave instead of blanking it
                node.Company = v.Company;
                node.Product = v.Product;
                node.VersionInfoSource = ProcessNode.VersionInfoLabel;
            }
        }
    }

    private static readonly string[] SidCols = { "sid" };
    private static readonly string[] SessionPidCols = { "processid", "pid" };
    private static readonly string[] UserNameCols = { "username" };

    private static readonly Dictionary<string, string> IntegrityLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S-1-16-0"] = "Untrusted", ["S-1-16-4096"] = "Low", ["S-1-16-8192"] = "Medium", ["S-1-16-8448"] = "Medium Plus",
        ["S-1-16-12288"] = "High", ["S-1-16-16384"] = "System", ["S-1-16-20480"] = "Protected Process",
    };

    /// <summary>Label of Company / Product taken from a Sysmon 1 event (the PE version resource Sysmon read from the file).</summary>
    public const string SysmonVersionInfoLabel = "Sysmon 1 (PE version resource, not a signature)";

    private static bool Blank(string s) => s.Length == 0 || s == "-" || s.Equals("N/A", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// getsids: the first row of a PID is the token user (UserAndGroups[0]); an S-1-16-* row is the mandatory integrity label. sessions: user fallback (only when every row
    /// of the PID names the same user). Assigned only to a PID with exactly one memory instance; a User already set by a 4688 / Sysmon 1 event is kept.
    /// </summary>
    private static void ApplyTokens(ProcessGraph graph, Dictionary<int, List<ProcessNode>> byPid, List<string> sidPaths, List<string> sessionPaths)
    {
        var users = new Dictionary<int, string>();
        var integrity = new Dictionary<int, string>();
        var seenPids = new HashSet<int>();
        foreach (var p in sidPaths)
        {
            int pidCol = -2, sidCol = -1, nameCol = -1;
            bool read = ReadTable(p, graph, (h, r) =>
            {
                if (pidCol == -2) { pidCol = Col(h, PidCols); sidCol = Col(h, SidCols); nameCol = Col(h, new[] { "name" }); }
                if (Int(r, pidCol) is not { } pid) return;
                string sid = Str(r, sidCol), name = Str(r, nameCol);
                bool first = seenPids.Add(pid);
                if (sid.StartsWith("S-1-16-", StringComparison.OrdinalIgnoreCase))
                {
                    if (!integrity.ContainsKey(pid)) integrity[pid] = IntegrityLevels.TryGetValue(sid, out var lvl) ? lvl : Blank(name) ? sid : name;
                    return;
                }
                if (first) users[pid] = Blank(name) ? sid : name;   // first row of the PID: the token user
            });
            if (read) graph.SourceFiles.Add(p);
        }

        var sessionUsers = new Dictionary<int, string?>();   // null: the rows of the PID disagree
        foreach (var p in sessionPaths)
        {
            int pidCol = -2, userCol = -1;
            bool read = ReadTable(p, graph, (h, r) =>
            {
                if (pidCol == -2) { pidCol = Col(h, SessionPidCols); userCol = Col(h, UserNameCols); }
                if (Int(r, pidCol) is not { } pid) return;
                var user = Str(r, userCol);
                if (Blank(user)) return;
                if (!sessionUsers.TryGetValue(pid, out var seen)) sessionUsers[pid] = user;
                else if (seen != null && !seen.Equals(user, StringComparison.OrdinalIgnoreCase)) sessionUsers[pid] = null;
            });
            if (read) graph.SourceFiles.Add(p);
        }

        foreach (var pid in users.Keys.Concat(integrity.Keys).Concat(sessionUsers.Keys).Distinct())
        {
            if (!byPid.TryGetValue(pid, out var cands)) continue;
            var mem = cands.Where(InMemory).ToList();
            if (mem.Count != 1) continue;   // two instances of the PID: which one the token belongs to is not known
            var node = mem[0];
            if (node.User.Length == 0)
            {
                if (users.TryGetValue(pid, out var u) && u.Length > 0) { node.User = u; node.UserSource = "getsids"; }
                else if (sessionUsers.TryGetValue(pid, out var su) && su != null) { node.User = su; node.UserSource = "sessions"; }
            }
            if (integrity.TryGetValue(pid, out var il)) node.Integrity = il;
        }
    }

    private static bool IsMainImage(ProcessNode n, string module)
    {
        var path = ProcessLogText.EffectivePath(n);
        if (path.Length > 0) return FileNameOf(path).Equals(module, StringComparison.OrdinalIgnoreCase);
        if (n.Name.Length == 0) return false;
        return n.Name.Equals(module, StringComparison.OrdinalIgnoreCase) || (n.Name.Length >= 14 && module.StartsWith(n.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Streams a CSV row by row (header map: normalised name to column). False when the file is missing, empty or unreadable.</summary>
    private static bool ReadTable(string path, ProcessGraph graph, Action<Dictionary<string, int>, CsvDataReader> onRow)
    {
        if (!File.Exists(path)) { graph.Warnings.Add($"Missing file {System.IO.Path.GetFileName(path)}."); return false; }
        var r = Open(path, graph, out var tr);
        if (r == null) return false;
        using (tr) using (r)
        {
            try
            {
                var h = Headers(r);
                while (r.Read()) onRow(h, r);
                return true;
            }
            catch (Exception ex) when (ex is IOException or CsvRecordTooLargeException or InvalidOperationException)
            {
                graph.Warnings.Add($"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }
    }

    // ---- merge -------------------------------------------------------------------------------------------------------------------------------------

    private static void Merge(ProcessGraph graph, Dictionary<int, List<ProcessNode>> byPid, Row row, ProcessSources source)
    {
        if (!byPid.TryGetValue(row.Pid, out var candidates)) byPid[row.Pid] = candidates = new List<ProcessNode>();
        ProcessNode? node = null;
        foreach (var c in candidates)
        {
            // Same PID + same creation time; a missing time on either side matches only while there is no ambiguity.
            if (c.CreateTime == row.CreateTime || ((c.CreateTime == null || row.CreateTime == null) && candidates.Count == 1)) { node = c; break; }
        }
        if (node == null && source == ProcessSources.None) return;   // cmdline rows only enrich known processes
        if (node == null)
        {
            node = new ProcessNode { Pid = row.Pid };
            candidates.Add(node);
            graph.AllNodes.Add(node);
        }

        node.Sources |= source;
        node.CreateTime ??= row.CreateTime;
        node.ExitTime ??= row.ExitTime;
        if (row.HasPpid && (node.Ppid == 0 || source != ProcessSources.None)) node.Ppid = row.Ppid;
        if (node.Name.Length == 0) node.Name = row.Name;
        if (node.Path.Length == 0) node.Path = row.Path;
        if (node.CmdLine.Length == 0) node.CmdLine = row.Cmd;
        if (node.Offset.Length == 0) node.Offset = row.Offset;
        node.SessionId ??= row.SessionId;
        node.Wow64 ??= row.Wow64;
        node.Threads ??= row.Threads;
        node.Handles ??= row.Handles;
        if (node.Name.Length == 0 && node.Path.Length > 0) node.Name = FileNameOf(node.Path);
    }

    private static string FileNameOf(string path) => ProcessLogText.FileNameOf(path);

    // ---- event logs --------------------------------------------------------------------------------------------------------------------------------

    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(2);

    private static bool SameTime(DateTime? a, DateTime? b) => a != null && b != null && (a.Value - b.Value).Duration() <= MatchWindow;

    private static void MergeLogs(ProcessGraph graph, Dictionary<int, List<ProcessNode>> byPid, EventLogProcessReader.Result logs)
    {
        var byGuid = new Dictionary<string, ProcessNode>();
        foreach (var rec in logs.Creations.OrderBy(r => r.Time))
        {
            graph.SourceCounts[rec.Source] = graph.SourceCounts.GetValueOrDefault(rec.Source) + 1;
            ProcessNode? node = null;
            if (rec.Guid.Length > 0) byGuid.TryGetValue(rec.Guid, out node);
            if (node == null && rec.Pid >= 0 && byPid.TryGetValue(rec.Pid, out var cands))
            {
                // PID + creation time within 2 s; a Sysmon process never merges into a node that already has another GUID
                node = cands.Where(c => SameTime(c.CreateTime, rec.Time) && (rec.Guid.Length == 0 || c.Guid.Length == 0))
                            .OrderBy(c => (c.CreateTime!.Value - rec.Time!.Value).Duration()).FirstOrDefault();
            }
            if (node == null)
            {
                node = new ProcessNode { Pid = Math.Max(rec.Pid, 0), CreateTime = rec.Time, Markers = ProcessMarkers.FromLogsOnly };
                graph.AllNodes.Add(node);
                if (!byPid.TryGetValue(node.Pid, out var l)) byPid[node.Pid] = l = new List<ProcessNode>();
                l.Add(node);
            }
            node.Sources |= rec.Source;
            node.CreateTime ??= rec.Time;
            if (rec.Ppid != null && node.Ppid == 0) node.Ppid = rec.Ppid.Value;
            if (node.Path.Length == 0) node.Path = rec.Image;
            if (node.Name.Length == 0) node.Name = FileNameOf(rec.Image);
            if (node.CmdLine.Length == 0) node.CmdLine = rec.Cmd;
            if (node.User.Length == 0 && rec.User.Length > 0) { node.User = rec.User; node.UserSource = rec.Source == ProcessSources.Evtx4688 ? "Security 4688" : "Sysmon 1"; }
            if (node.ParentImage.Length == 0) node.ParentImage = rec.ParentImage;
            if (rec.Guid.Length > 0) { node.Guid = rec.Guid; byGuid[rec.Guid] = node; }
            if (rec.ParentGuid.Length > 0) node.ParentGuid = rec.ParentGuid;
            if (node.Hash.Length == 0 && rec.Hash.Length > 0) { node.Hash = rec.Hash; node.HashSource = "Sysmon 1"; }
            // memory (getsids, pslist, verinfo) wins; the logs fill what it does not have, for log-only processes above all
            if (node.Integrity.Length == 0 && rec.Integrity.Length > 0) node.Integrity = IntegrityLevels.TryGetValue(rec.Integrity, out var lvl) ? lvl : rec.Integrity;
            node.SessionId ??= rec.SessionId;
            if (node.Company.Length == 0 && node.Product.Length == 0 && (rec.Company.Length > 0 || rec.Product.Length > 0))
            {
                node.Company = rec.Company; node.Product = rec.Product;
                if (node.VersionInfoSource.Length == 0) node.VersionInfoSource = SysmonVersionInfoLabel;
            }
        }

        foreach (var ex in logs.Exits.OrderBy(e => e.Time))
        {
            ProcessNode? node = null;
            if (ex.Guid.Length > 0) byGuid.TryGetValue(ex.Guid, out node);
            if (node == null && ex.Pid is { } pid && byPid.TryGetValue(pid, out var cands))
                node = cands.Where(c => c.CreateTime == null || ex.Time == null || c.CreateTime <= ex.Time)
                            .OrderByDescending(c => c.CreateTime ?? DateTime.MinValue).FirstOrDefault();
            if (node != null && node.ExitTime == null && ex.Time != null && (node.CreateTime == null || ex.Time >= node.CreateTime)) node.ExitTime = ex.Time;
        }
    }

    // ---- linking -----------------------------------------------------------------------------------------------------------------------------------

    private static void Link(ProcessGraph graph, Dictionary<int, List<ProcessNode>> byPid)
    {
        var byGuid = new Dictionary<string, ProcessNode>();
        foreach (var n in graph.AllNodes) if (n.Guid.Length > 0) byGuid[n.Guid] = n;

        foreach (var child in graph.AllNodes)
        {
            ProcessNode? parent = null;
            bool laterOnly = false;
            if (child.ParentGuid.Length > 0 && byGuid.TryGetValue(child.ParentGuid, out var gp) && !ReferenceEquals(gp, child))
            {
                parent = gp;
                if (child.Ppid == 0) child.Ppid = gp.Pid;
            }
            else if (child.Ppid != child.Pid && byPid.TryGetValue(child.Ppid, out var cands))
            {
                foreach (var c in cands)
                {
                    if (ReferenceEquals(c, child)) continue;
                    bool earlier = c.CreateTime == null || child.CreateTime == null || c.CreateTime <= child.CreateTime;
                    if (!earlier) { laterOnly = true; continue; }
                    if (parent == null || (c.CreateTime ?? DateTime.MinValue) >= (parent.CreateTime ?? DateTime.MinValue)) parent = c;
                }
            }

            if (parent != null && !IsAncestor(child, parent))
            {
                child.Parent = parent;
                parent.Children.Add(child);
                if (SuspiciousParentRules.Find(parent.Name, child.Name) != null) child.Markers |= ProcessMarkers.SuspiciousParent;
                continue;
            }

            if (parent == null && laterOnly) child.Markers |= ProcessMarkers.ParentPidReused;
            else if (!IsNaturalRoot(child) && !child.Markers.HasFlag(ProcessMarkers.FromLogsOnly)) child.Markers |= ProcessMarkers.Orphan;   // log-only: the parent may predate the log
            graph.Roots.Add(child);
        }

        graph.Roots.Sort(ByCreate);
        foreach (var n in graph.AllNodes) n.Children.Sort(ByCreate);
    }

    /// <summary>Longest quiet gap between two consecutive children of one ghost: a missing parent silent for longer is not assumed to be the same process.</summary>
    internal static readonly TimeSpan GhostGap = TimeSpan.FromHours(24);

    /// <summary>
    /// Ghost parents for the roots that are orphans or whose parent PID was reused (Ppid &gt; 0). PIDs are reused (other boots, days of 4688 history), so a ghost
    /// groups only children that plausibly share one parent instance. The children of a parent PID, in creation order, are cut at a boot (creation of System or
    /// the master smss.exe), at any known start or exit of another process with that PID (while the parent lives its PID is taken) and at a gap over
    /// <see cref="GhostGap"/>; within a span, children stating different <see cref="ProcessNode.ParentImage"/>s never share a ghost (see <see cref="ByParentImage"/>).
    /// Children without a creation time cannot be placed and share ghosts only among themselves. A ghost is named only after a stated ParentImage; its key is
    /// "ghost:&lt;ppid&gt;", plus <see cref="ProcessNode.GhostSuffix"/> when the PID has several ghosts.
    /// </summary>
    private static void AttachGhosts(ProcessGraph graph)
    {
        var missing = graph.Roots
            .Where(r => r.Ppid > 0 && r.Parent == null && (r.Markers.HasFlag(ProcessMarkers.Orphan) || r.Markers.HasFlag(ProcessMarkers.ParentPidReused)))
            .GroupBy(r => r.Ppid).ToList();
        if (missing.Count == 0) return;
        var boots = graph.AllNodes.Where(IsBootStart).Select(n => n.CreateTime!.Value).Distinct().ToList();
        var byPid = graph.AllNodes.ToLookup(n => n.Pid);
        var moved = new HashSet<ProcessNode>(ReferenceEqualityComparer.Instance);
        foreach (var grp in missing)
        {
            var cuts = boots.Concat(byPid[grp.Key].SelectMany(n => new[] { n.CreateTime, n.ExitTime }).Where(t => t != null).Select(t => t!.Value)).ToList();
            var groups = new List<List<ProcessNode>>();
            var timeless = grp.Where(c => c.CreateTime == null).ToList();
            if (timeless.Count > 0) groups.AddRange(ByParentImage(timeless));
            List<ProcessNode>? span = null;
            DateTime last = default;
            foreach (var c in grp.Where(c => c.CreateTime != null).OrderBy(c => c.CreateTime))
            {
                var t = c.CreateTime!.Value;
                if (span == null || t - last > GhostGap || cuts.Any(x => x > last && x <= t))
                {
                    if (span != null) groups.AddRange(ByParentImage(span));
                    span = new List<ProcessNode>();
                }
                span.Add(c);
                last = t;
            }
            if (span != null) groups.AddRange(ByParentImage(span));

            foreach (var kids in groups)
            {
                kids.Sort(ByCreate);
                var ghost = new ProcessNode { Pid = grp.Key, IsGhost = true, GhostSuffix = groups.Count > 1 ? "/" + kids[0].Key : "" };
                var image = kids.Select(c => c.ParentImage).FirstOrDefault(i => i.Length > 0);
                if (image != null)
                {
                    ghost.Path = image;
                    ghost.Name = FileNameOf(image);
                }
                foreach (var c in kids)
                {
                    c.Parent = ghost;
                    ghost.Children.Add(c);
                    moved.Add(c);
                }
                graph.Ghosts.Add(ghost);
                graph.Roots.Add(ghost);
            }
        }
        graph.Roots.RemoveAll(moved.Contains);
        graph.Roots.Sort(ByCreate);
    }

    /// <summary>
    /// Splits one span of orphans by stated <see cref="ProcessNode.ParentImage"/> (case-insensitive): one group per image. Children stating none join the only
    /// stated image, else (none or several stated) form a group of their own.
    /// </summary>
    private static List<List<ProcessNode>> ByParentImage(List<ProcessNode> span)
    {
        var groups = span.Where(c => c.ParentImage.Length > 0).GroupBy(c => c.ParentImage, StringComparer.OrdinalIgnoreCase).Select(g => g.ToList()).ToList();
        var unstated = span.Where(c => c.ParentImage.Length == 0).ToList();
        if (groups.Count == 1) groups[0].AddRange(unstated);
        else if (unstated.Count > 0) groups.Add(unstated);
        return groups;
    }

    /// <summary>A boot: the System process (PID 4) or the master smss.exe (started by System) with a known creation time.</summary>
    private static bool IsBootStart(ProcessNode n) =>
        n.CreateTime != null && (n.Pid == 4 || (n.Ppid == 4 && n.Name.Equals("smss.exe", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Idle (0) and System (4) have no parent by design, as does anything whose parent PID is 0 (Linux init, kthreadd).</summary>
    private static bool IsNaturalRoot(ProcessNode n) => n.Pid is 0 or 4 || n.Ppid == 0;

    private static bool IsAncestor(ProcessNode node, ProcessNode candidate)
    {
        for (var p = candidate; p != null; p = p.Parent)
            if (ReferenceEquals(p, node)) return true;
        return false;
    }

    private static int ByCreate(ProcessNode a, ProcessNode b)
    {
        int c = Nullable.Compare(a.CreateTime, b.CreateTime);
        return c != 0 ? c : a.Pid.CompareTo(b.Pid);
    }

    // ---- CSV reading -------------------------------------------------------------------------------------------------------------------------------

    private sealed class Row
    {
        public int Pid;
        public int Ppid;
        public bool HasPpid;
        public string Name = "", Path = "", Cmd = "", Offset = "";
        public DateTime? CreateTime, ExitTime;
        public int? SessionId, Threads, Handles;
        public bool? Wow64;
    }

    private static CsvDataReader? Open(string path, ProcessGraph graph, out StreamReader? tr)
    {
        tr = null;
        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            tr = new StreamReader(fs, new UTF8Encoding(false), true, 65536);
            return CsvDataReader.Create(tr, new CsvDataReaderOptions { HasHeaders = true });
        }
        catch (CsvMissingHeadersException)
        {
            // empty file: nothing to read, not an error
            tr?.Dispose();
            tr = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CsvRecordTooLargeException or InvalidOperationException)
        {
            tr?.Dispose();
            tr = null;
            graph.Warnings.Add($"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, int> Headers(CsvDataReader r)
    {
        var map = new Dictionary<string, int>();
        for (int i = 0; i < r.FieldCount; i++) map.TryAdd(Norm(r.GetName(i)), i);
        return map;
    }

    private static string Norm(string header)
    {
        var sb = new StringBuilder(header.Length);
        foreach (var ch in header) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static int Col(Dictionary<string, int> map, string[] aliases)
    {
        foreach (var a in aliases) if (map.TryGetValue(a, out var i)) return i;
        return -1;
    }

    private static string Str(CsvDataReader r, int col) => col < 0 ? "" : (r.GetString(col) ?? "").Trim();

    private static int? Int(CsvDataReader r, int col) =>
        int.TryParse(Str(r, col), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTime? Time(CsvDataReader r, int col)
    {
        var s = Str(r, col);
        if (s.Length == 0) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
    }

    /// <summary>Rows of a process CSV; null when the file is missing or unreadable (a warning is added), empty list when it has no data rows.</summary>
    private static List<Row>? ReadRows(string path, ProcessGraph graph)
    {
        if (!File.Exists(path)) { graph.Warnings.Add($"Missing file {System.IO.Path.GetFileName(path)}."); return null; }
        var r = Open(path, graph, out var tr);
        if (r == null) return null;
        using (tr) using (r)
        {
            try
            {
                var h = Headers(r);
                int pid = Col(h, PidCols), ppid = Col(h, PpidCols), tid = Col(h, TidCols), name = Col(h, NameCols), off = Col(h, OffsetCols);
                int thr = Col(h, ThreadCols), han = Col(h, HandleCols), ses = Col(h, SessionCols), wow = Col(h, Wow64Cols);
                int cre = Col(h, CreateCols), exi = Col(h, ExitCols), pth = Col(h, PathCols), cmd = Col(h, CmdCols);
                var rows = new List<Row>();
                if (pid < 0)
                {
                    if (r.FieldCount > 0) graph.Warnings.Add($"{System.IO.Path.GetFileName(path)} has no PID column.");
                    return rows;
                }
                while (r.Read())
                {
                    var p = Int(r, pid);
                    if (p == null) continue;
                    var t = Int(r, tid);
                    if (t != null && t != p) continue;   // Linux pslist can list threads
                    var pp = Int(r, ppid);
                    rows.Add(new Row
                    {
                        Pid = p.Value, Ppid = pp ?? 0, HasPpid = pp != null,
                        Name = Str(r, name), Path = Str(r, pth), Cmd = Str(r, cmd), Offset = Str(r, off),
                        CreateTime = Time(r, cre), ExitTime = Time(r, exi),
                        SessionId = Int(r, ses), Threads = Int(r, thr), Handles = Int(r, han), Wow64 = Bool(Str(r, wow)),
                    });
                }
                return rows;
            }
            catch (Exception ex) when (ex is IOException or CsvRecordTooLargeException or InvalidOperationException)
            {
                graph.Warnings.Add($"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }
    }

    private static bool? Bool(string s) =>
        s.Length == 0 ? null : s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" ? true : s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0" ? false : null;

    private static HashSet<int>? ReadPids(string path, ProcessGraph graph)
    {
        var rows = ReadRows(path, graph);
        return rows == null ? null : new HashSet<int>(rows.Select(x => x.Pid));
    }
}
