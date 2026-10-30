namespace EzForensics.Core.Results;

/// <summary>Kind of evidence a case folder holds (picks the folder's icon in the Analyze case tree).</summary>
public enum EvidenceKind
{
    Other,
    Timeline,
    EventLogs,
    ProgramExecution,
    FileSystem,
    FileFolderAccess,
    FileDeletion,
    Registry,
    UserActivity,
    Database,
    WebServer,
    Network,
    Persistence,
    Accounts,
    Logins,
    ShellHistory,
    Services,
    ScheduledTasks,
    Packages,
    SystemInfo,
    Memory,
    /// <summary>The built-in process correlator's output (<c>&lt;host&gt;\Processes\Processes.csv</c>, <c>Processes_Activity.csv</c>).</summary>
    Processes,
}

public static class EvidenceKinds
{
    private static readonly Dictionary<string, EvidenceKind> ByCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EventLogs"] = EvidenceKind.EventLogs,
        ["ProgramExecution"] = EvidenceKind.ProgramExecution,
        ["FileSystem"] = EvidenceKind.FileSystem,
        ["FileFolderAccess"] = EvidenceKind.FileFolderAccess,
        ["FileDeletion"] = EvidenceKind.FileDeletion,
        ["Registry"] = EvidenceKind.Registry,
        ["UserActivity"] = EvidenceKind.UserActivity,
        ["Databases"] = EvidenceKind.Database,
        ["SQLDatabases"] = EvidenceKind.Database,
        ["SRUMDatabase"] = EvidenceKind.Database,
        ["SUMDatabase"] = EvidenceKind.Database,
        ["WebServers"] = EvidenceKind.WebServer,
        ["Network"] = EvidenceKind.Network,
        ["Persistence"] = EvidenceKind.Persistence,
        ["Accounts"] = EvidenceKind.Accounts,
        ["Logins"] = EvidenceKind.Logins,
        ["ShellHistory"] = EvidenceKind.ShellHistory,
        ["Services"] = EvidenceKind.Services,
        ["ScheduledTasks"] = EvidenceKind.ScheduledTasks,
        ["Packages"] = EvidenceKind.Packages,
        ["SystemInfo"] = EvidenceKind.SystemInfo,
        ["Memory"] = EvidenceKind.Memory,
        ["Processes"] = EvidenceKind.Processes,
        ["Timeline"] = EvidenceKind.Timeline,
    };

    /// <summary>
    /// Kind of a case tree folder: the <c>Timeline</c> folder, or a category folder (<c>EventLogs</c>, <c>Registry</c>, <c>Network</c>...);
    /// <see cref="EvidenceKind.Other"/> for host, tool and any other folders.
    /// </summary>
    public static EvidenceKind ForFolder(string name)
    {
        if (CaseTreeOrder.IsSuperTimeline(name, true)) return EvidenceKind.Timeline;
        return ByCategory.TryGetValue(name, out var k) ? k : EvidenceKind.Other;
    }
}
