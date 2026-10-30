using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Processes;

/// <summary>What an indicator says. Every one is derived from stated fields of the node (or its markers); none is a verdict.</summary>
public enum IndicatorKind
{
    SuspiciousParent, Injected, Hollowed, Ghosted, PebMasquerade, SuspiciousThreads, Hidden, ParentPidReused,
    UnexpectedLocation, DuplicateSingleton, UnexpectedSession, EncodedCommand,
}

/// <summary>One indicator of one process, with the facts that triggered it quoted in <see cref="Evidence"/>.</summary>
public sealed record ProcessIndicator(ProcessNode Node, IndicatorKind Kind, string Evidence);

/// <summary>Gaps in the data (not indicators): the process may be perfectly normal, the sources just do not show its parent.</summary>
public enum DataGapKind { Orphan, FromLogsOnly }

public sealed record ProcessDataGap(ProcessNode Node, DataGapKind Kind, string Text);

/// <summary>Indicators of a process graph, from <c>roles.json</c>, the node fields and the markers set while the graph was built. Facts only.</summary>
public static class ProcessIndicators
{
    /// <summary>Joins the kinds of a node's indicators with ';' (the Indicators column of Processes.csv).</summary>
    public static string KindsText(ProcessNode n) => string.Join(";", n.Indicators.Select(i => i.Kind.ToString()).Distinct());

    /// <summary>Sets <see cref="ProcessNode.Role"/>, <see cref="ProcessNode.RoleBasis"/> and <see cref="ProcessNode.Indicators"/> on every node (ghosts get none).</summary>
    public static void Annotate(ProcessGraph graph)
    {
        foreach (var n in graph.AllNodes)
        {
            var (role, basis) = ProcessRoles.Classify(n);
            n.Role = role;
            n.RoleBasis = basis;
            n.Indicators.Clear();
        }
        foreach (var i in Evaluate(graph)) i.Node.Indicators.Add(i);
    }

    public static List<ProcessIndicator> Evaluate(ProcessGraph graph)
    {
        var list = new List<ProcessIndicator>();
        foreach (var n in graph.AllNodes)
        {
            if (n.IsGhost) continue;
            Markers(n, graph, list);
            Role(n, list);
            Encoded(n, list);
        }
        Duplicates(graph, list);
        return list;
    }

    /// <summary>Orphans and nodes seen only in event logs.</summary>
    public static List<ProcessDataGap> DataGaps(ProcessGraph graph)
    {
        var list = new List<ProcessDataGap>();
        foreach (var n in graph.AllNodes)
        {
            if (n.Markers.HasFlag(ProcessMarkers.Orphan))
                list.Add(new ProcessDataGap(n, DataGapKind.Orphan, $"Parent PID {n.Ppid} is not in any source" + (n.ParentImage.Length > 0 ? $" (event log names its parent image: {n.ParentImage})" : "") + "."));
            if (n.Markers.HasFlag(ProcessMarkers.FromLogsOnly))
                list.Add(new ProcessDataGap(n, DataGapKind.FromLogsOnly, "Only seen in event logs, not in a memory image."));
        }
        return list;
    }

    private static string Who(ProcessNode n) => $"{(n.Name.Length > 0 ? n.Name : "(no name)")} (PID {n.Pid})";

    private static string T(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "unknown";

    private static void Markers(ProcessNode n, ProcessGraph graph, List<ProcessIndicator> list)
    {
        void Add(IndicatorKind k, string why) => list.Add(new ProcessIndicator(n, k, why));
        var m = n.Markers;
        if (m.HasFlag(ProcessMarkers.SuspiciousParent) && n.Parent != null && SuspiciousParentRules.Find(n.Parent.Name, n.Name) is { } rule)
            Add(IndicatorKind.SuspiciousParent, $"{rule.Why}: {Who(n.Parent)} started {Who(n)}.");
        else if (m.HasFlag(ProcessMarkers.SuspiciousParent))
            Add(IndicatorKind.SuspiciousParent, "Parent/child combination matches a suspicious parent rule.");
        if (m.HasFlag(ProcessMarkers.Injected)) Add(IndicatorKind.Injected, $"windows.malfind has a hit for PID {n.Pid}.");
        if (m.HasFlag(ProcessMarkers.Hollowed)) Add(IndicatorKind.Hollowed, $"malware.hollowprocesses has a row for PID {n.Pid} (the image in memory does not match the mapped file).");
        if (m.HasFlag(ProcessMarkers.Ghosted)) Add(IndicatorKind.Ghosted, $"malware.processghosting has a row for PID {n.Pid} (image file object is delete-pending or delete-on-close).");
        if (m.HasFlag(ProcessMarkers.PebMasquerade)) Add(IndicatorKind.PebMasquerade, $"malware.pebmasquerade reports a spoofed PEB image path or command line for PID {n.Pid}.");
        if (m.HasFlag(ProcessMarkers.SuspiciousThreads)) Add(IndicatorKind.SuspiciousThreads, $"malware.suspicious_threads has a row for PID {n.Pid} (a thread starts outside any mapped image).");
        if (m.HasFlag(ProcessMarkers.Hidden)) Add(IndicatorKind.Hidden, $"PID {n.Pid} is in the psscan pool scan but not in the pslist active list, and has no exit time.");
        if (m.HasFlag(ProcessMarkers.ParentPidReused))
        {
            var later = graph.AllNodes.Where(p => p.Pid == n.Ppid && p.CreateTime != null).Select(p => p.CreateTime).OrderBy(t => t).FirstOrDefault();
            Add(IndicatorKind.ParentPidReused, $"Parent PID {n.Ppid} exists only with a creation time ({T(later)}) later than this process's ({T(n.CreateTime)}): the PID was reused.");
        }
    }

    private static void Role(ProcessNode n, List<ProcessIndicator> list)
    {
        var role = ProcessRoles.Find(n);
        if (role == null) return;
        if (ProcessRoles.IsUnexpectedLocation(n, role, out var actual))
            list.Add(new ProcessIndicator(n, IndicatorKind.UnexpectedLocation,
                $"{role.Name} runs from \"{actual}\"; expected {string.Join(" or ", role.Dirs.Select(d => "\"" + d + "\""))}."));
        if (role.Session0 && n.SessionId is int s && s != 0)
            list.Add(new ProcessIndicator(n, IndicatorKind.UnexpectedSession, $"{role.Name} normally runs in session 0; this one has SessionId {s}."));
    }

    private static void Duplicates(ProcessGraph graph, List<ProcessIndicator> list)
    {
        foreach (var g in graph.AllNodes.Where(n => ProcessRoles.Find(n) is { Singleton: true }).GroupBy(ProcessRoles.ImageName))
        {
            var nodes = g.ToList();
            if (nodes.Count < 2) continue;
            foreach (var a in nodes)
            {
                var others = nodes.Where(b => !ReferenceEquals(a, b) && CoExist(a, b)).ToList();
                if (others.Count == 0) continue;
                list.Add(new ProcessIndicator(a, IndicatorKind.DuplicateSingleton,
                    $"{g.Key} normally has one instance, but {string.Join(", ", others.Select(b => $"PID {b.Pid} (created {T(b.CreateTime)})"))} also {(others.Count == 1 ? "runs" : "run")} at the same time as PID {a.Pid}."));
            }
        }
    }

    /// <summary>Both instances are in the memory active list, or both have a known start and a known end (exit time, or active in pslist) and the lifetimes overlap.</summary>
    private static bool CoExist(ProcessNode a, ProcessNode b)
    {
        if (Active(a) && Active(b)) return true;
        if (a.CreateTime is not { } sa || b.CreateTime is not { } sb) return false;
        var ea = End(a);
        var eb = End(b);
        if (ea == null || eb == null) return false;
        return sa < eb.Value && sb < ea.Value;
    }

    private static bool Active(ProcessNode n) => n.Sources.HasFlag(ProcessSources.PsList) && n.ExitTime == null && !n.Markers.HasFlag(ProcessMarkers.Exited);

    private static DateTime? End(ProcessNode n) => n.ExitTime ?? (Active(n) ? DateTime.MaxValue : null);

    // ---- encoded PowerShell ------------------------------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> EncodedSwitches = new(StringComparer.OrdinalIgnoreCase)
        { "e", "ec", "en", "enc", "enco", "encod", "encode", "encoded", "encodedc", "encodedco", "encodedcom", "encodedcomm", "encodedcomma", "encodedcomman", "encodedcommand" };

    private static void Encoded(ProcessNode n, List<ProcessIndicator> list)
    {
        if (n.CmdLine.Length == 0) return;
        var image = ProcessRoles.ImageName(n);
        if (image.Length == 0) image = ProcessLogText.FileNameOf(ProcessLogText.ExePath(n.CmdLine)).ToLowerInvariant();
        if (image is not ("powershell.exe" or "pwsh.exe" or "powershell" or "pwsh")) return;
        var tokens = Regex.Matches(n.CmdLine, "\"[^\"]*\"|\\S+").Select(m => m.Value.Trim('"')).ToList();
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            var t = tokens[i];
            if (t.Length < 2 || (t[0] != '-' && t[0] != '/')) continue;
            if (!EncodedSwitches.Contains(t[1..])) continue;
            var arg = tokens[i + 1];
            var decoded = Decode(arg);
            list.Add(new ProcessIndicator(n, IndicatorKind.EncodedCommand,
                $"Command line has {t} {Shorten(arg, 60)}; " + (decoded != null ? "decoded: " + Shorten(decoded, 500) : "the argument is not valid Base64 UTF-16 text.")));
            return;
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    /// <summary>The UTF-16LE text of a Base64 argument, or null when it is not valid Base64 / not readable text.</summary>
    public static string? Decode(string arg)
    {
        try
        {
            var bytes = Convert.FromBase64String(arg.Trim());
            if (bytes.Length == 0 || bytes.Length % 2 != 0) return null;
            var text = Encoding.Unicode.GetString(bytes);
            foreach (var c in text)
                if (char.IsControl(c) && c is not ('\r' or '\n' or '\t')) return null;
                else if (c == '�') return null;
            return text;
        }
        catch (FormatException) { return null; }
    }
}
