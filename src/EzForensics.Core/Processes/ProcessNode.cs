using System.Globalization;

namespace EzForensics.Core.Processes;

/// <summary>Where a process row came from (flags: one process can be seen by several sources).</summary>
[Flags]
public enum ProcessSources
{
    None = 0,
    PsList = 1,
    PsScan = 2,
    PsTree = 4,
    /// <summary>Security 4688 process creation event (merged in later).</summary>
    Evtx4688 = 8,
    /// <summary>Sysmon event 1 process creation (merged in later).</summary>
    Sysmon1 = 16,
}

/// <summary>Analyst-facing flags set while the graph is built.</summary>
[Flags]
public enum ProcessMarkers
{
    None = 0,
    /// <summary>Found by the pool scan (psscan) but not in the active list (pslist): unlinked or exited.</summary>
    Hidden = 1,
    Exited = 2,
    /// <summary>The parent PID is not in any source.</summary>
    Orphan = 4,
    /// <summary>The parent PID exists only with a creation time later than the child's: the PID was reused.</summary>
    ParentPidReused = 8,
    /// <summary>The process has a malfind hit.</summary>
    Injected = 16,
    /// <summary>Parent/child combination matches a <see cref="SuspiciousParentRules"/> rule.</summary>
    SuspiciousParent = 32,
    /// <summary>Only seen in event logs, not in memory.</summary>
    FromLogsOnly = 64,
    /// <summary>malware.hollowprocesses has a row for this PID (the image in memory does not match the mapped file).</summary>
    Hollowed = 128,
    /// <summary>malware.processghosting has a row for this PID (the image file object is delete-pending / delete-on-close).</summary>
    Ghosted = 256,
    /// <summary>malware.pebmasquerade reports a spoofed PEB image path or command line for this PID.</summary>
    PebMasquerade = 512,
    /// <summary>malware.suspicious_threads has a row for this PID (a thread starts outside any mapped image).</summary>
    SuspiciousThreads = 1024,
}

/// <summary>One process instance: a PID with one creation time. Same PID with another creation time is another node.</summary>
public sealed class ProcessNode
{
    public int Pid { get; set; }
    public int Ppid { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string CmdLine { get; set; } = "";
    /// <summary>Account the process ran as; empty until a source provides it.</summary>
    public string User { get; set; } = "";
    /// <summary>Where <see cref="User"/> came from: "Security 4688" / "Sysmon 1" (event logs), "getsids" / "sessions" (memory); empty when unknown.</summary>
    public string UserSource { get; set; } = "";
    /// <summary>Integrity level of the token as getsids printed it (S-1-16-* label: "High", "Medium"...); empty when unknown.</summary>
    public string Integrity { get; set; } = "";
    public DateTime? CreateTime { get; set; }
    public DateTime? ExitTime { get; set; }
    /// <summary>Kernel structure offset as printed by the plugin (virtual or physical).</summary>
    public string Offset { get; set; } = "";
    public int? SessionId { get; set; }
    public bool? Wow64 { get; set; }
    public int? Threads { get; set; }
    public int? Handles { get; set; }

    /// <summary>Sysmon ProcessGuid (upper case, with braces) when a Sysmon 1 event was merged in; empty otherwise.</summary>
    public string Guid { get; set; } = "";
    public string ParentGuid { get; set; } = "";
    /// <summary>Parent image path as the event log printed it (4688 / Sysmon 1), for nodes whose parent is not in the graph.</summary>
    public string ParentImage { get; set; } = "";
    /// <summary>File hash with its algorithm ("SHA1 3a1f..."), from the Sysmon 1 event of this very process (same ProcessGuid, or the same PID inside its lifetime); empty otherwise.</summary>
    public string Hash { get; set; } = "";
    /// <summary>Where <see cref="Hash"/> came from ("Sysmon 1").</summary>
    public string HashSource { get; set; } = "";

    /// <summary>
    /// PE version resource of the node's own image as Volatility verinfo read it from memory (the row of this PID whose module is the main image). Version info, NOT a
    /// signature: anyone can write any company name into a PE. verinfo 2.x prints only the version numbers, so <see cref="Company"/> / <see cref="Product"/> stay empty
    /// unless the CSV has CompanyName / ProductName columns.
    /// </summary>
    public string Company { get; set; } = "";
    public string Product { get; set; } = "";
    /// <summary>"10.0.19041.1" (Major.Minor.Product.Build of verinfo).</summary>
    public string FileVersion { get; set; } = "";
    /// <summary>Label for <see cref="Company"/> / <see cref="Product"/> / <see cref="FileVersion"/>: <see cref="VersionInfoLabel"/> when set, empty otherwise.</summary>
    public string VersionInfoSource { get; set; } = "";
    public const string VersionInfoLabel = "verinfo (PE version resource, not a signature)";

    public ProcessSources Sources { get; set; }
    public ProcessMarkers Markers { get; set; }

    /// <summary>
    /// Synthetic parent of orphans: the stated parent PID of one or more nodes whose parent was not captured by any source. It has no times, no sources and no evidence; its
    /// <see cref="Name"/> / <see cref="Path"/> come only from a stated <see cref="ParentImage"/> of its children. Not part of <see cref="ProcessGraph.AllNodes"/>.
    /// </summary>
    public bool IsGhost { get; set; }

    /// <summary>Role from roles.json (see <see cref="ProcessRoles.Classify"/>); empty until <see cref="ProcessIndicators.Annotate"/> ran.</summary>
    public string Role { get; set; } = "";
    public RoleBasis RoleBasis { get; set; }
    /// <summary>Indicators found by <see cref="ProcessIndicators.Annotate"/> (kind + the facts that triggered it).</summary>
    public List<ProcessIndicator> Indicators { get; } = new();

    public ProcessNode? Parent { get; set; }
    public List<ProcessNode> Children { get; } = new();

    /// <summary>Stable identity within a host: PID plus creation time ticks (0 when unknown).</summary>
    public string Key => IsGhost ? GhostKey(Pid) : Pid.ToString(CultureInfo.InvariantCulture) + ":" + (CreateTime?.Ticks ?? 0).ToString(CultureInfo.InvariantCulture);

    public static string GhostKey(int pid) => "ghost:" + pid.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => $"{Pid} {Name}";
}
