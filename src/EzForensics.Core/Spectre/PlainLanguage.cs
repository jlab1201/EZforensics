using System.Globalization;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Spectre;

/// <summary>
/// Wording for the Findings story view (ATT&amp;CK tactic names, standard forensic artifact names). A Core copy of the engine's glossary, used when an older engine does not send
/// its own <c>plain_title</c>, <c>phase</c> or <c>summary</c>. Wording describes what is in the data; it never claims a cause.
/// </summary>
public static class PlainLanguage
{
    public const string OtherActivity = "Unmapped";

    /// <summary>MITRE ATT&amp;CK tactic names, keyed by the name with only its letters, lower case.</summary>
    private static readonly Dictionary<string, string> Tactics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["initialaccess"] = "Initial Access",
        ["execution"] = "Execution",
        ["persistence"] = "Persistence",
        ["privilegeescalation"] = "Privilege Escalation",
        ["defenseevasion"] = "Defense Evasion",
        ["credentialaccess"] = "Credential Access",
        ["discovery"] = "Discovery",
        ["lateralmovement"] = "Lateral Movement",
        ["collection"] = "Collection",
        ["commandandcontrol"] = "Command and Control",
        ["exfiltration"] = "Exfiltration",
        ["impact"] = "Impact",
    };

    /// <summary>Stage names stored by older analyses, as the ATT&amp;CK tactic names that replaced them.</summary>
    private static readonly Dictionary<string, string> LegacyPhases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Getting in"] = "Initial Access",
        ["Running programs"] = "Execution",
        ["Staying in"] = "Persistence",
        ["Gaining control"] = "Privilege Escalation",
        ["Covering tracks"] = "Defense Evasion",
        ["Stealing passwords"] = "Credential Access",
        ["Looking around"] = "Discovery",
        ["Spreading to other computers"] = "Lateral Movement",
        ["Gathering data"] = "Collection",
        ["Bringing in tools"] = "Command and Control",
        ["Remote control"] = "Command and Control",
        ["Taking data out"] = "Exfiltration",
        ["Damage"] = "Impact",
        ["Other activity"] = OtherActivity,
    };

    /// <summary>ATT&amp;CK tactic ("Initial Access", "initial-access") as its standard name; empty or unknown gives "Unmapped".</summary>
    public static string Tactic(string? tactic)
    {
        if (string.IsNullOrWhiteSpace(tactic)) return OtherActivity;
        var key = new string(tactic.Where(char.IsLetter).ToArray());
        return Tactics.TryGetValue(key, out var name) ? name : OtherActivity;
    }

    /// <summary>A stage name read from an older analysis ("Staying in") as the tactic name ("Persistence"); other names are returned as they are.</summary>
    public static string? MigratePhase(string? phase)
        => phase != null && LegacyPhases.TryGetValue(phase.Trim(), out var name) ? name : phase;

    /// <summary>Stage name: the ATT&amp;CK tactic name (the technique does not change it).</summary>
    public static string Phase(string? tactic, string? technique) => Tactic(tactic);

    /// <summary>Artifact type ("evtx", "mft", "evtx security") as its standard forensic name. Unknown types become Title Case.</summary>
    public static string Artifact(string? artifactType)
    {
        if (string.IsNullOrWhiteSpace(artifactType)) return "Other source";
        var t = artifactType.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        var raw = artifactType.Trim().ToLowerInvariant();
        if (raw == "logfile_record" || t == "logfile record") return "Log entry";
        if (t == "mft") return "MFT ($MFT)";
        if (t is "usn" or "usnjrnl" or "usn journal") return "USN Journal";
        if (t.StartsWith("evtx", StringComparison.Ordinal) && t.Contains("security", StringComparison.Ordinal)) return "Windows Security Event Log";
        if (t.StartsWith("evtx", StringComparison.Ordinal)) return "Windows Event Log";
        if (t == "prefetch") return "Prefetch";
        if (t == "amcache") return "Amcache";
        if (t == "shimcache") return "Shimcache (AppCompatCache)";
        if (t == "registry") return "Registry";
        if (t == "lnk") return "LNK files";
        if (t == "jumplist") return "Jump Lists";
        if (t == "browser") return "Browser history";
        if (t == "srum") return "SRUM";
        if (t is "scheduled task" or "scheduledtask") return "Scheduled Tasks";
        if (t == "service") return "Services";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t);
    }

    private static readonly Regex WinPath = new(@"(?:[A-Za-z]:|\\\\[^\\\s""']+)\\[^\s""'<>|*?]*", RegexOptions.Compiled);
    private static readonly Regex UnixPath = new(@"(?<![\w.])/(?:[\w.\-~@+%=,]+/)*[\w.\-~@+%=,]+", RegexOptions.Compiled);

    /// <summary>The headline with every file path cut down to its last part ("C:\Users\a\evil.exe ran" gives "evil.exe ran").</summary>
    public static string TitleWithoutPaths(string? headline)
    {
        if (string.IsNullOrWhiteSpace(headline)) return "";
        var s = WinPath.Replace(headline, m => Basename(m.Value));
        s = UnixPath.Replace(s, m => Basename(m.Value));
        return s.Trim();
    }

    private static string Basename(string path)
    {
        var trimmed = path.TrimEnd('\\', '/', '.', ',', ';', ':', ')');
        var i = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        var name = i >= 0 ? trimmed[(i + 1)..] : trimmed;
        return name.Length > 0 ? name : path;
    }

    /// <summary>Opening sentence for the story. Neutral: counts and dates only.</summary>
    public static string SummarySentence(int moments, DateTimeOffset? first, DateTimeOffset? last, int hosts, int undated)
    {
        string day(DateTimeOffset d) => d.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        string tail = undated > 0 ? $" {undated} finding{(undated == 1 ? " has" : "s have")} no date." : "";
        if (moments <= 0)
            return (undated > 0 ? "No key events with a date were found." : "No key events were found.") + tail;
        var noun = moments == 1 ? "1 key event" : $"{moments} key events";
        if (first == null || last == null)
            return $"Spectre found {noun}, but none has a date, so {(moments == 1 ? "it" : "they")} cannot be placed in time.{tail}";
        var host = hosts > 1 ? $" across {hosts} hosts" : "";
        if (moments == 1 || day(first.Value) == day(last.Value))
            return $"Spectre found {noun} on {day(first.Value)} (UTC){host}.{tail}";
        return $"Spectre found {noun} between {day(first.Value)} and {day(last.Value)} (UTC){host}.{tail}";
    }
}
