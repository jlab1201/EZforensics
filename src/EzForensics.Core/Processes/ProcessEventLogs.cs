using System.Globalization;
using System.Text;
using Sylvan.Data.Csv;

namespace EzForensics.Core.Processes;

/// <summary>Group of an event-log row for the process detail facets (one per tab of the process details).</summary>
public enum ProcessEventGroup { Process, Network, Files, Registry, Modules, Threats, Scripts, Identity, Persistence }

/// <summary>Link directions of <see cref="ProcessEventLink.Direction"/>.</summary>
public static class ProcessEvent
{
    /// <summary>Sysmon 8 / 10, the linked process is the source: it accessed another process.</summary>
    public const string Accessed = "accessed";
    /// <summary>Sysmon 8 / 10, the linked process is the target: another process accessed it.</summary>
    public const string AccessedBy = "accessed by";
    /// <summary>Defender: the linked process is the file that was detected, not the process that touched it.</summary>
    public const string FileDetected = "file detected";
}

/// <summary>A full program path an event is linked through when it names no PID, the time rule that link needs (<see cref="LinkTier.PathTime"/>) and its direction.</summary>
public sealed record ProcessLinkPath(string Path, string Direction, TimeFit Fit);

/// <summary>An event row linked to a process: how firmly (<see cref="Tier"/>) and in which direction (<see cref="Direction"/>, "" unless Sysmon 8 / 10 or a Defender file).</summary>
public sealed record ProcessEventLink(ProcessEventRecord Event, LinkTier Tier, string Direction);

/// <summary>Signature of an image seen in Sysmon 7 (image loaded), for the node roll-up.</summary>
public sealed record ProcessSignature(bool? Signed, string Signature, string Status, string Source)
{
    /// <summary>"Signed · Microsoft Windows · Valid (Sysmon 7)"; null without a signature (no other source carries one).</summary>
    public static string? Describe(ProcessSignature? s)
    {
        if (s == null) return null;
        var head = s.Signed switch { true => "Signed", false => "Not signed", _ => "" };
        var parts = new[] { head, s.Signature, s.Status }.Where(p => !string.IsNullOrWhiteSpace(p) && p != "-").Distinct(StringComparer.OrdinalIgnoreCase);
        var text = string.Join(" · ", parts);
        return text.Length == 0 ? null : text + " (" + s.Source + ")";
    }
}

/// <summary>
/// One process-related row of the EvtxECmd CSV (Security 4688 / 4689 / 4624 / 4648 / 465x / 4663 / 5156, Sysmon, PowerShell, Task Scheduler, AppLocker, WMI, Defender, services),
/// reduced to what the process tree shows. The meaning of <see cref="T1"/>-<see cref="T4"/> depends on <see cref="Group"/>.
/// </summary>
public sealed class ProcessEventRecord
{
    public DateTime? Time { get; init; }
    public int EventId { get; init; }
    /// <summary>The acting process (the one the event is about, not the writer of the log row).</summary>
    public int? Pid { get; init; }
    /// <summary>Sysmon ProcessGuid (upper case, braces); empty for Security events.</summary>
    public string Guid { get; init; } = "";
    public string Title { get; init; } = "";
    public string Image { get; init; } = "";
    /// <summary>Process: command line · Network: destination host (DNS: query name) · Files: target file · Registry: key · Modules: module path · Threats: target image / file / threat · Scripts: script or command · Identity: target account · Persistence: name.</summary>
    public string T1 { get; init; } = "";
    /// <summary>Process: parent image · Network: destination IP (DNS: answers) · Registry: value / new name · Files: hash or access · Modules: hash · Threats: access / address · Persistence: path or command.</summary>
    public string T2 { get; init; } = "";
    /// <summary>Network: destination port · Registry: event type · Files: object type · Modules: Signed / Unsigned · Identity: logon id.</summary>
    public string T3 { get; init; } = "";
    /// <summary>Network: source IP · Modules: signature · Threats: call trace · Identity: authentication package.</summary>
    public string T4 { get; init; } = "";
    public string User { get; init; } = "";
    public string Detail { get; init; } = "";
    public string SourceFile { get; init; } = "";
    /// <summary>Log label ("Security", "Sysmon", "PowerShell", "Task Scheduler", "AppLocker", "WMI", "Defender", "System"); empty for rows from older indexes.</summary>
    public string Log { get; init; } = "";

    /// <summary>Network rows (Sysmon 3, Security 5156): transport ("tcp", "udp", lower case) and source port, so the socket's 5-tuple is complete (Zeek join).</summary>
    public string Protocol { get; init; } = "";
    public string SourcePort { get; init; } = "";

    /// <summary>Sysmon 8 / 10 target process.</summary>
    public int? TargetPid { get; init; }
    public string TargetGuid { get; init; } = "";
    public string TargetImage { get; init; } = "";
    /// <summary>Paths this event is linked through (PathTime; only Task Scheduler 200 / 201 and Defender 1116 / 1117 have them).</summary>
    public IReadOnlyList<ProcessLinkPath> Paths { get; init; } = Array.Empty<ProcessLinkPath>();

    /// <summary>Sysmon 6 / 7: <c>Signed</c>, <c>Signature</c> and <c>SignatureStatus</c> of the loaded image.</summary>
    public bool? Signed { get; set; }
    public string Signature { get; set; } = "";
    public string SignatureStatus { get; set; } = "";

    private ProcessEventGroup? _group;
    public ProcessEventGroup Group
    {
        get => _group ?? EventId switch
        {
            3 => ProcessEventGroup.Network,
            11 => ProcessEventGroup.Files,
            13 => ProcessEventGroup.Registry,
            _ => ProcessEventGroup.Process,
        };
        init => _group = value;
    }

    public string Source => (Log.Length > 0 ? Log : EventId is 4688 or 4689 ? "Security" : "Sysmon") + " " + EventId.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A process creation seen in the logs (Security 4688 or Sysmon 1).</summary>
internal sealed class LogProcessRecord
{
    public ProcessSources Source;
    public DateTime? Time;
    public int Pid;
    public int? Ppid;
    public string Guid = "", ParentGuid = "", Image = "", Cmd = "", User = "", ParentImage = "", Hash = "";
}

/// <summary>A process end seen in the logs (Security 4689 or Sysmon 5).</summary>
internal sealed class LogExitRecord
{
    public DateTime? Time;
    public int? Pid;
    public string Guid = "";
}

/// <summary>
/// Process-related event rows of a host, built once. Immutable afterwards. Rows are reachable by PID, Sysmon GUID, normalised program path and exe name;
/// <see cref="LinksFor"/> says how each row is tied to the process (<see cref="LinkTier"/>), on facts only: the same PID inside the process lifetime or the same Sysmon
/// GUID is Exact; the same full path plus a fitting time, with exactly one instance fitting, is PathTime. Nothing is linked by name or by path alone.
/// Call <see cref="Attach"/> with the graph nodes so that PID reuse and path uniqueness are judged against all instances.
/// </summary>
public sealed class ProcessEventIndex
{
    public const int MaxPerPid = 5000;
    private const long MaxTotal = 3_000_000;

    private readonly record struct Entry(ProcessEventRecord Rec, string Dir, string Guid, TimeFit? Fit = null);

    private readonly Dictionary<int, List<Entry>> _byPid = new();
    private readonly Dictionary<string, List<Entry>> _byGuid = new(), _byPath = new();
    private ProcessLifetimes? _life;
    private long _total;

    /// <summary>EvtxECmd CSVs that were read.</summary>
    public List<string> Files { get; } = new();
    public bool HasFiles => Files.Count > 0;
    public int Count => (int)Math.Min(_total, int.MaxValue);
    public bool Truncated { get; private set; }

    private readonly HashSet<string> _logs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Log labels with at least one process event ("Security", "Sysmon", "PowerShell", ...): the Sources tab says "Sysmon not present" when it is missing.</summary>
    public IReadOnlyCollection<string> LogsSeen => _logs;

    /// <summary>Full path of the EvtxECmd CSV an event came from (<see cref="ProcessEventRecord.SourceFile"/> is the file name); empty when unknown.</summary>
    public string PathOf(string sourceFile) =>
        Files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), sourceFile, StringComparison.OrdinalIgnoreCase)) ?? "";

    /// <summary>Label of the log an event row belongs to (older rows without <see cref="ProcessEventRecord.Log"/>: Security for 4688 / 4689, Sysmon otherwise).</summary>
    public static string LogOf(ProcessEventRecord r) => r.Log.Length > 0 ? r.Log : r.EventId is 4688 or 4689 ? "Security" : "Sysmon";

    internal bool Add(ProcessEventRecord r)
    {
        _logs.Add(LogOf(r));
        bool linked = r.Pid != null || r.Guid.Length > 0 || r.TargetPid != null || r.TargetGuid.Length > 0 || r.Paths.Count > 0;
        if (!linked) return true;
        if (_total >= MaxTotal) { Truncated = true; return false; }
        bool access = r.TargetPid != null || r.TargetGuid.Length > 0 || r.TargetImage.Length > 0;
        if (r.Pid != null || r.Guid.Length > 0) AddLink(r, r.Pid, r.Guid, access ? ProcessEvent.Accessed : "");
        if (r.TargetPid != null || r.TargetGuid.Length > 0) AddLink(r, r.TargetPid, r.TargetGuid, ProcessEvent.AccessedBy);
        foreach (var p in r.Paths)
        {
            var key = ProcessLogText.PathKey(p.Path);
            if (key.Length > 0 && r.Time != null) Put(_byPath, key, new Entry(r, p.Direction, "", p.Fit));
        }
        _total++;
        return true;
    }

    private void AddLink(ProcessEventRecord r, int? pid, string guid, string dir)
    {
        var e = new Entry(r, dir, guid);
        if (pid != null) Put(_byPid, pid.Value, e);
        else Put(_byGuid, guid, e);
    }

    private void Put<TKey>(Dictionary<TKey, List<Entry>> map, TKey key, Entry e) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var l)) map[key] = l = new List<Entry>();
        if (l.Count >= MaxPerPid) { Truncated = true; return; }
        l.Add(e);
    }

    internal void Sort()
    {
        foreach (var m in new[] { _byGuid, _byPath })
            foreach (var l in m.Values) l.Sort(ByTime);
        foreach (var l in _byPid.Values) l.Sort(ByTime);
    }

    private static int ByTime(Entry a, Entry b) => Nullable.Compare(a.Rec.Time, b.Rec.Time);

    /// <summary>Gives the index the process instances of the graph, so a reused PID and an ambiguous path are judged against all of them. Without it each node is judged alone.</summary>
    public void Attach(IEnumerable<ProcessNode> nodes, DateTime? evidenceEnd = null) => _life = new ProcessLifetimes(nodes, evidenceEnd);

    private ProcessLifetimes LifeOf(ProcessNode node) => _life ?? new ProcessLifetimes(new[] { node });

    private static bool InLifetime(Entry e, ProcessNode node, ProcessLifetimes life)
    {
        if (e.Guid.Length > 0 && node.Guid.Length > 0) return e.Guid == node.Guid;
        return life.PidFits(node, e.Rec.Time);
    }

    /// <summary>
    /// Every event row tied to the process, oldest first, optionally of one <paramref name="group"/>. The same PID inside the process lifetime or the same GUID gives
    /// <see cref="LinkTier.Exact"/> (hex PIDs of Security 4624 / 4656 / 4657 / 4663 are already decimal here); Task Scheduler 200 / 201 and Defender rows, which name no PID,
    /// give <see cref="LinkTier.PathTime"/> when the full path matches, the row time fits and this is the only such instance. Sysmon 8 / 10 rows appear for the source with direction
    /// <see cref="ProcessEvent.Accessed"/> and for the target with <see cref="ProcessEvent.AccessedBy"/>.
    /// </summary>
    public IReadOnlyList<ProcessEventLink> LinksFor(ProcessNode node, ProcessEventGroup? group = null)
    {
        var best = new Dictionary<(ProcessEventRecord, string), LinkTier>();
        void Put(Entry e, LinkTier t)
        {
            if (group != null && e.Rec.Group != group) return;
            var key = (e.Rec, e.Dir);
            if (!best.TryGetValue(key, out var old) || t < old) best[key] = t;
        }
        var life = LifeOf(node);
        if (_byPid.TryGetValue(node.Pid, out var byPid))
            foreach (var e in byPid) if (InLifetime(e, node, life)) Put(e, LinkTier.Exact);
        if (node.Guid.Length > 0 && _byGuid.TryGetValue(node.Guid, out var byGuid))
            foreach (var e in byGuid) Put(e, LinkTier.Exact);
        var pathKey = ProcessLifetimes.PathKeyOf(node);
        if (pathKey.Length > 0 && _byPath.TryGetValue(pathKey, out var byPath))
            foreach (var e in byPath) if (e.Fit != null && e.Rec.Time is { } t && life.IsUniquePathTime(node, t, e.Fit)) Put(e, LinkTier.PathTime);
        return best.Select(kv => new ProcessEventLink(kv.Key.Item1, kv.Value, kv.Key.Item2))
            .OrderBy(l => l.Event.Time == null).ThenBy(l => l.Event.Time).ThenBy(l => l.Event.EventId).ToList();
    }

    /// <summary>Number of linked rows per group (all tiers), for the tab counts.</summary>
    public Dictionary<ProcessEventGroup, int> GroupCounts(ProcessNode node) =>
        LinksFor(node).GroupBy(l => l.Event.Group).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Events of the process by PID / GUID only (<see cref="LinkTier.Exact"/>): the same Sysmon GUID when both sides have one; otherwise the same PID inside the process lifetime.</summary>
    public List<ProcessEventRecord> For(ProcessNode node) =>
        LinksFor(node).Where(l => l.Tier == LinkTier.Exact).Select(l => l.Event).ToList();

    /// <summary>
    /// Signature of the node's own image as a Sysmon 7 (image loaded) row of this very process saw it (same ProcessGuid, or the same PID inside the lifetime; the loaded
    /// image is the node's full path). Null when there is no such row. The App uses this for the node Signature roll-up.
    /// </summary>
    public ProcessSignature? SignatureFor(ProcessNode node)
    {
        var key = ProcessLifetimes.PathKeyOf(node);
        if (key.Length == 0) return null;
        foreach (var l in LinksFor(node, ProcessEventGroup.Modules))
        {
            var r = l.Event;
            if (l.Tier != LinkTier.Exact || r.EventId != 7 || (r.Signed == null && r.Signature.Length == 0)) continue;
            if (ProcessLogText.PathKey(r.T1) == key) return new ProcessSignature(r.Signed, r.Signature, r.SignatureStatus, "Sysmon 7");
        }
        return null;
    }
}

/// <summary>Reads the EvtxECmd CSVs of a host: only the process events, streamed (the files can be gigabytes).</summary>
internal static class EventLogProcessReader
{
    public sealed class Result
    {
        public List<LogProcessRecord> Creations { get; } = new();
        public List<LogExitRecord> Exits { get; } = new();
        public ProcessEventIndex Events { get; } = new();
    }

    public static Result Read(IEnumerable<string> files, ProcessGraph graph)
    {
        var res = new Result();
        foreach (var f in files)
        {
            try { ReadFile(f, res, graph); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CsvRecordTooLargeException or InvalidOperationException or CsvFormatException)
            {
                graph.Warnings.Add($"Could not read {Path.GetFileName(f)}: {ex.Message}");
            }
        }
        res.Events.Sort();
        return res;
    }

    private static void ReadFile(string path, Result res, ProcessGraph graph)
    {
        if (!File.Exists(path)) { graph.Warnings.Add($"Missing file {Path.GetFileName(path)}."); return; }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
        using var tr = new StreamReader(fs, new UTF8Encoding(false), true, 65536);
        CsvDataReader r;
        try { r = CsvDataReader.Create(tr, new CsvDataReaderOptions { HasHeaders = true, BufferSize = 1 << 20 }); }
        catch (CsvMissingHeadersException) { return; }
        using (r)
        {
            var h = new Dictionary<string, int>();
            for (int i = 0; i < r.FieldCount; i++) h.TryAdd(ProcessLogText.Norm(r.GetName(i)), i);
            int Col(string n) => h.TryGetValue(n, out var i) ? i : -1;
            int cId = Col("eventid");
            if (cId < 0) { graph.Warnings.Add($"{Path.GetFileName(path)} has no EventId column."); return; }
            int cTime = Col("timecreated"), cChan = Col("channel"), cProv = Col("provider"), cUser = Col("username"), cExe = Col("executableinfo");
            int cRowPid = Col("processid"), cPayload = Col("payload");
            var cPay = Enumerable.Range(1, 6).Select(i => Col("payloaddata" + i)).ToArray();
            var file = Path.GetFileName(path);
            bool used = false;
            var cells = new string[6];

            while (r.Read())
            {
                if (!int.TryParse(r.GetFieldSpan(cId).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var eid)) continue;
                var channel = Str(r, cChan);
                var provider = Str(r, cProv);
                if (EvtxProcessEvents.Classify(channel, provider, eid) is not { } log || !EvtxProcessEvents.Wanted(log, eid)) continue;

                var time = ParseTime(Str(r, cTime));
                var user = Str(r, cUser);
                var exe = Str(r, cExe);
                for (int i = 0; i < 6; i++) cells[i] = Str(r, cPay[i]);
                used = true;

                if (log == EvtxLog.Security && eid == 4688)
                {
                    var pid = ProcessLogText.ParsePid(cells[1]);
                    if (pid == null) continue;
                    var u = user.Length > 0 && user != "-" ? user : ProcessLogText.StripLabel(cells[4]);
                    if (u == "-") u = "";
                    var rec = new LogProcessRecord
                    {
                        Source = ProcessSources.Evtx4688, Time = time, Pid = pid.Value, Ppid = ProcessLogText.ParsePid(cells[2]),
                        Image = ProcessLogText.ExePath(exe), Cmd = exe, ParentImage = ProcessLogText.StripLabel(cells[0]), User = u,
                    };
                    res.Creations.Add(rec);
                    AddEvent(res, new ProcessEventRecord
                    {
                        Time = time, EventId = eid, Pid = pid, Title = "Process created", Image = rec.Image, T1 = exe, T2 = rec.ParentImage, User = u,
                        Detail = exe, SourceFile = file,
                    });
                    continue;
                }

                if (!(log == EvtxLog.Security && eid == 4689) && !(log == EvtxLog.Sysmon && eid is 1 or 5))
                {
                    var row = new EvtxRow
                    {
                        Time = time, User = user, Exe = exe, File = file, Cells = (string[])cells.Clone(),
                        RowPid = ProcessLogText.ParsePid(Str(r, cRowPid)),
                        Payload = EvtxProcessEvents.NeedsPayload(log, eid) ? Str(r, cPayload) : "",
                    };
                    if (EvtxProcessEvents.Parse(log, eid, row) is { } ev) AddEvent(res, ev);
                    continue;
                }

                var kv = ProcessLogText.KeyValues(cells);
                var pidKv = ProcessLogText.Get(kv, "processid", "pid", "newprocessid");
                var pidVal = ProcessLogText.ParsePid(pidKv);
                var guid = ProcessLogText.NormGuid(ProcessLogText.Get(kv, "processguid"));
                var image = ProcessLogText.Get(kv, "image", "processname");

                switch (eid)
                {
                    case 4689:
                    case 5:
                    {
                        if (pidVal == null && eid == 4689) pidVal = ProcessLogText.ParsePid(cells[1]);
                        res.Exits.Add(new LogExitRecord { Time = time, Pid = pidVal, Guid = guid });
                        if (image.Length == 0) image = ProcessLogText.ExePath(exe);
                        AddEvent(res, new ProcessEventRecord
                        {
                            Time = time, EventId = eid, Log = eid == 5 ? "Sysmon" : "Security", Pid = pidVal, Guid = guid, Title = eid == 5 ? "Process terminated" : "Process exited", Image = image,
                            User = user, Detail = "Exit", SourceFile = file,
                        });
                        break;
                    }
                    case 1:
                    {
                        if (pidVal == null && guid.Length == 0) break;
                        var cmd = ProcessLogText.Get(kv, "commandline");
                        if (cmd.Length == 0) cmd = exe;
                        if (image.Length == 0) image = ProcessLogText.ExePath(cmd);
                        // EZ map: UserName holds "ParentUser: x" and then the user; PayloadData3 is the unlabelled Hashes, PayloadData4 "ParentProcess: path".
                        var u = user.Length > 0 && !user.StartsWith("ParentUser:", StringComparison.OrdinalIgnoreCase) ? user : ProcessLogText.Get(kv, "user");
                        var parentImage = ProcessLogText.Get(kv, "parentimage", "parentprocess");
                        var hash = ProcessLogText.BestHash(ProcessLogText.Get(kv, "hashes"));
                        if (hash.Length == 0) hash = EvtxProcessEvents.HashOf(new EvtxRow { Cells = (string[])cells.Clone() });
                        var rec = new LogProcessRecord
                        {
                            Source = ProcessSources.Sysmon1, Time = time, Pid = pidVal ?? 0, Ppid = ProcessLogText.ParsePid(ProcessLogText.Get(kv, "parentprocessid")),
                            Guid = guid, ParentGuid = ProcessLogText.NormGuid(ProcessLogText.Get(kv, "parentprocessguid")), Image = image, Cmd = cmd, User = u,
                            ParentImage = parentImage, Hash = hash,
                        };
                        if (pidVal == null) rec.Pid = -1;   // GUID only: matched through the GUID
                        res.Creations.Add(rec);
                        AddEvent(res, new ProcessEventRecord
                        {
                            Time = time, EventId = 1, Log = "Sysmon", Pid = pidVal, Guid = guid, Title = "Process created", Image = image, T1 = cmd, T2 = rec.ParentImage, User = u,
                            Detail = cmd, SourceFile = file,
                        });
                        break;
                    }
                }
            }
            if (used) graph.SourceFiles.Add(path);
            res.Events.Files.Add(path);
        }
    }

    private static void AddEvent(Result res, ProcessEventRecord e) => res.Events.Add(e);

    private static string Str(CsvDataReader r, int col) => col < 0 ? "" : (r.GetString(col) ?? "").Trim();

    private static DateTime? ParseTime(string s) =>
        s.Length > 0 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
