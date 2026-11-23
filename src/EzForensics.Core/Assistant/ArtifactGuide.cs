namespace EzForensics.Core.Assistant;

/// <summary>One short, factual "what does this artifact answer" hint for the case inventory.</summary>
/// <param name="Hint">One line; says what the artifact shows and, where it matters, what it does not prove.</param>
/// <param name="Rank">Question group, lowest first: 0 execution, 1 file / user activity, 2 events, 3 persistence and network, 9 other.</param>
public sealed record ArtifactHint(string Hint, int Rank);

/// <summary>
/// Static map from the ids <see cref="Case.CaseIndex"/> produces (tool id, artifact id) and, as a fallback, file names to a one-line hint.
/// Lets a small model see which case file answers "what ran?" without guessing from file names.
/// </summary>
public static class ArtifactGuide
{
    public const int Execution = 0;

    private static readonly ArtifactHint Prefetch = new("program execution: exe name, run count, last 8 run times, files loaded", Execution);
    private static readonly ArtifactHint Amcache = new("program presence and first-seen evidence with SHA1 (not proof it ran)", Execution);
    private static readonly ArtifactHint Shimcache = new("file existence on disk (execution not proven on Windows 10+)", Execution);
    private static readonly ArtifactHint Srum = new("per-app CPU / network / bytes usage over time (apps that ran)", Execution);
    private static readonly ArtifactHint Bam = new("BAM/UserAssist/RunMRU registry keys: last execution per user", Execution);
    private static readonly ArtifactHint Eventlog = new("Windows events: 4688 process creation (if auditing on), Sysmon 1, 4624 logons, 7045 services", 2);

    /// <summary>Hint for a file, from its ids first and its file name second; null when nothing is known.</summary>
    public static ArtifactHint? For(string? toolId, string? artifactId, string? fileName)
    {
        var a = (artifactId ?? "").ToLowerInvariant();
        var t = (toolId ?? "").ToLowerInvariant();
        var n = (fileName ?? "").ToLowerInvariant();
        bool Has(string s) => a.Contains(s) || n.Contains(s);

        if (a.StartsWith("prefetch") || t == "pecmd" || n.Contains("prefetch") || n.StartsWith("pecmd")) return Prefetch;
        if (a.StartsWith("amcache") || t == "amcacheparser" || n.Contains("amcache")) return Amcache;
        if (a == "shimcache" || t == "appcompatcacheparser" || n.Contains("shimcache") || n.Contains("appcompatcache")) return Shimcache;
        if (a.StartsWith("srum") || t == "srumecmd" || n.Contains("srum")) return Srum;
        if (Has("userassist") || Has("bam") && (t == "recmd" || n.Contains("recmd") || a.StartsWith("registry"))) return Bam;
        if (a.StartsWith("recentfilecache") || t == "recentfilecacheparser" || n.Contains("recentfilecache"))
            return new("legacy list of executed programs (Windows 7/8)", Execution);
        if (a.StartsWith("eventlog") || t == "evtxecmd" || n.Contains("evtx") || n.StartsWith("eventlogs")) return Eventlog;
        if (a.StartsWith("jumplist") || t == "jlecmd" || n.Contains("jumplist"))
            return new("files and apps opened by user, with app id and times", 1);
        if (a == "lnk" || t == "lecmd" || n.StartsWith("lnk")) return new("shortcut targets: files opened by the user, with target times", 1);
        if (a == "mft" || a.StartsWith("mft-") || t == "mftecmd" && !a.StartsWith("usn") || n.Contains("mft"))
            return new("every file on the volume: names, paths, created/modified times (creation, not execution)", 1);
        if (a.StartsWith("usn") || n.Contains("usn")) return new("file create / rename / delete journal with timestamps", 1);
        if (a.StartsWith("shellbags") || t == "sbecmd") return new("folders the user browsed, incl. removable and network", 1);
        if (a.StartsWith("registry") || t == "recmd") return new("registry keys and values (autoruns, user activity, config)", 1);
        if (a.StartsWith("recyclebin") || t == "rbcmd") return new("deleted files: original path and delete time", 1);
        if (a.StartsWith("activitiescache") || t == "wxtcmd") return new("Windows Timeline: apps and documents used by the user", 1);
        if (a == "ual" || t == "sumecmd") return new("server client access log: who connected, from where", 2);
        if (a.StartsWith("persistence") || t.Contains("persist")) return new("autoruns, services, scheduled tasks, WMI: what starts by itself", 3);
        if (a == "processes" || t.Contains("process")) return new("process list with parent/child links", Execution);
        if (a.StartsWith("zeek") || a.StartsWith("rita") || t is "zeek" or "rita") return new("network connections and beaconing analysis", 3);
        if (a == "sqlite" || t == "sqlecmd") return new("browser history, downloads and other SQLite app data", 1);
        if (t == "volatility3" || t.StartsWith("vol") || a.StartsWith("memory")) return new("memory image: processes, network, injected code", Execution);
        return null;
    }
}
