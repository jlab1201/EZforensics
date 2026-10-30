using EzForensics.Core.SystemInfo;

namespace EzForensics.Core.Persistence;

/// <summary>Category names written to the Category column of <c>EzForensics_Persistence.csv</c>.</summary>
public static class PersistenceCategory
{
    public const string RunKeys = "Run keys", Winlogon = "Winlogon", AppInit = "AppInit DLLs", ImageHijacks = "Image hijacks (IFEO)",
        AppCert = "AppCert DLLs", Lsa = "LSA providers", Print = "Print monitors / providers", Services = "Services", Drivers = "Drivers",
        BootExecute = "Boot execute", ActiveSetup = "Active Setup", Explorer = "Explorer add-ons", ComHijack = "COM hijack (user classes)",
        TerminalServer = "Terminal Server", Netsh = "Netsh helpers", TimeProviders = "Time providers", CommandProcessor = "Command Processor AutoRun",
        LogonScripts = "Logon scripts", Screensaver = "Screensaver", Debuggers = "Debuggers (AeDebug)", ScheduledTasks = "Scheduled tasks",
        StartupFolder = "Startup folder", Wmi = "WMI event subscriptions",
        KnownDlls = "KnownDLLs", Winsock = "Winsock providers", NetworkProviders = "Network providers", Codecs = "Codecs",
        OfficeAddins = "Office add-ins", InternetExplorer = "Internet Explorer", GroupPolicyScripts = "Group Policy scripts";
}

/// <summary>One autostart (persistence) location found offline in the evidence: a row of <c>EzForensics_Persistence.csv</c>.</summary>
public sealed class PersistenceEntry
{
    public string Category { get; set; } = "";
    /// <summary>Logical location, e.g. <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run</c> or <c>Startup folder (All Users)</c>.</summary>
    public string Location { get; set; } = "";
    public string EntryName { get; set; } = "";
    /// <summary>Value data / command line / script.</summary>
    public string Command { get; set; } = "";
    /// <summary>The executable or DLL the entry starts, as written (environment variables kept).</summary>
    public string ImagePath { get; set; } = "";
    /// <summary>Profile folder name for per-user locations; empty for machine-wide ones.</summary>
    public string User { get; set; } = "";
    /// <summary>"Yes", "No" or "" (unknown).</summary>
    public string Enabled { get; set; } = "";
    /// <summary>Heuristic flags (see <see cref="PersistenceHeuristics"/>), "; "-separated.</summary>
    public List<string> Flags { get; } = new();
    /// <summary>Where it was read: <c>SOFTWARE\Microsoft\...\Run</c> (hive + key + value) or a file path in the evidence.</summary>
    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>PE version resource of <see cref="ImagePath"/> when the file is in the evidence (Autoruns "Description"/"Company"/"Version"); empty otherwise.</summary>
    public string Description { get; set; } = "";
    public string Company { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Autoruns columns reserved for a later hash + signature check; always empty in this collector.</summary>
    public string Signer { get; set; } = "";
    public string Md5 { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string PeSha1 { get; set; } = "";
    public string PeSha256 { get; set; } = "";
    public string Imp { get; set; } = "";
    public DateTime? KeyLastWriteUtc { get; set; }
    public DateTime? TaskRegisteredUtc { get; set; }
    public DateTime? FileCreatedUtc { get; set; }
    public DateTime? FileModifiedUtc { get; set; }

    /// <summary>The main timestamp: task registration, else key last write, else file modified, else file created.</summary>
    public (DateTime? Utc, string Description) Primary =>
        TaskRegisteredUtc is { } t ? (t, "Task registered")
        : KeyLastWriteUtc is { } k ? (k, "Persistence key last write")
        : FileModifiedUtc is { } m ? (m, "Startup file modified")
        : FileCreatedUtc is { } c ? (c, "Startup file created")
        : (null, "");

    public PersistenceEntry Flag(string flag)
    {
        if (!Flags.Contains(flag, StringComparer.OrdinalIgnoreCase)) Flags.Add(flag);
        return this;
    }
}

/// <summary>A scheduled task (<c>Windows\System32\Tasks\**</c> XML, completed from the SOFTWARE hive's TaskCache).</summary>
public sealed class ScheduledTaskInfo
{
    public string TaskPath { get; set; } = "";
    public string? XmlFile { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    /// <summary>RegistrationInfo/Date as written (local time of the source system, usually without a zone).</summary>
    public string? RegistrationDateRaw { get; set; }
    public DateTime? RegisteredUtc { get; set; }
    /// <summary>How <see cref="RegisteredUtc"/> was obtained.</summary>
    public string? RegisteredSource { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public List<string> Triggers { get; } = new();
    public List<TaskAction> Actions { get; } = new();
    public string? UserId { get; set; }
    public string? LogonType { get; set; }
    public string? RunLevel { get; set; }
    public bool? Enabled { get; set; }
    public bool? Hidden { get; set; }
    public string? TaskCacheId { get; set; }
    public bool InTaskCache { get; set; }
    public bool XmlMissing => XmlFile == null;
    public DateTime? FileModifiedUtc { get; set; }
    public string? Error { get; set; }
}

public sealed record TaskAction(string Kind, string Command, string Arguments, string? WorkingDirectory = null, string? ClassId = null, string? Data = null)
{
    public string CommandLine => Kind == "Exec" ? (Command + (Arguments.Length > 0 ? " " + Arguments : "")).Trim()
        : Kind == "ComHandler" ? $"COM handler {ClassId}" + (string.IsNullOrEmpty(Data) ? "" : " data " + Data)
        : Command;
}

/// <summary>A WMI permanent event subscription found in <c>OBJECTS.DATA</c>.</summary>
public sealed class WmiFinding
{
    public string ConsumerType { get; set; } = "";
    public string ConsumerName { get; set; } = "";
    public string FilterName { get; set; } = "";
    /// <summary>CommandLineTemplate / executable (CommandLineEventConsumer) or the script text (ActiveScriptEventConsumer).</summary>
    public string Command { get; set; } = "";
    public string? ScriptingEngine { get; set; }
    public string FilterQuery { get; set; } = "";
    public string? Namespace { get; set; }
    public bool LikelyLegitimate { get; set; }
    public long ConsumerOffset { get; set; } = -1;
    public long FilterOffset { get; set; } = -1;
}

/// <summary>Everything the built-in collector found for one system root.</summary>
public sealed class PersistenceResult
{
    public string SystemRoot { get; set; } = "";
    public List<PersistenceEntry> Entries { get; } = new();
    public List<ScheduledTaskInfo> Tasks { get; } = new();
    public List<WmiFinding> Wmi { get; } = new();
    public List<HiveInfo> Hives { get; } = new();
    public List<string> Notes { get; } = new();

    public IReadOnlyDictionary<string, int> CountsByCategory() =>
        Entries.GroupBy(e => e.Category).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
}
