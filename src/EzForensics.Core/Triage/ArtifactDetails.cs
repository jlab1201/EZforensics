using System.Globalization;
using EzForensics.Core.Memory;
using EzForensics.Core.Network;

namespace EzForensics.Core.Triage;

/// <summary>One bordered box of the Ingest detail window: key/value lines of one system or artifact.</summary>
public sealed record DetailBox(IReadOnlyList<KeyValuePair<string, string>> Rows);

/// <summary>Builds the key/value boxes of the Detected artifacts detail window (only the necessary keys; unknown values are left out).</summary>
public static class ArtifactDetails
{
    private static readonly HashSet<string> NotUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Default", "Default User", "All Users", "desktop.ini", "defaultuser0", "WDAGUtilityAccount",
    };

    /// <summary>"Windows 10 Pro 22H2 (build 19045.3803)" to ("Windows 10 Pro 22H2", "19045.3803"); no build when absent.</summary>
    public static (string Os, string? Build) SplitOsVersion(string text)
    {
        const string marker = " (build ";
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0 || !text.EndsWith(')')) return (text, null);
        return (text[..i], text[(i + marker.Length)..^1]);
    }

    /// <summary>User profile folder names under <c>&lt;root&gt;\Users</c> (system profiles left out), sorted; empty when unreadable.</summary>
    public static IReadOnlyList<string> UsersFound(string root)
    {
        try
        {
            var dir = Path.Combine(root, "Users");
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>()
                .Where(n => !NotUsers.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>Short names of the artifact groups, by planned-job rule id (anything else: the artifact's own label).</summary>
    private static readonly Dictionary<string, string> GroupNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mft"] = "$MFT", ["usnjrnl"] = "$UsnJrnl", ["registry"] = "Registry", ["evtx"] = "Event logs", ["prefetch"] = "Prefetch",
        ["amcache"] = "Amcache", ["appcompat"] = "Shimcache", ["lnk"] = "LNK", ["jumplists"] = "Jump lists", ["shellbags"] = "ShellBags",
        ["recyclebin"] = "Recycle Bin", ["srum"] = "SRUM", ["sum"] = "User Access Logging", ["activitiescache"] = "Windows Timeline",
        ["recentfilecache"] = "RecentFileCache", ["sqlite-hunt"] = "SQLite", [PersistenceTriage.RuleId] = "Persistence",
    };

    /// <summary>Default number of groups on the Disk card line before "+N more".</summary>
    public const int CardGroupLimit = 6;

    /// <summary>The short group name of a planned disk row ("$MFT", "Event logs"...).</summary>
    public static string GroupName(PlannedJob p) =>
        GroupNames.TryGetValue(p.Artifact.RuleId, out var name) ? name : string.IsNullOrWhiteSpace(p.Artifact.Label) ? p.Artifact.RuleId : p.Artifact.Label;

    /// <summary>
    /// The artifact groups found for the system at <paramref name="systemRoot"/>: the disk rows of <paramref name="plan"/> that matched that
    /// root (EZ tool jobs, persistence; not the steps, memory or network rows), deduplicated in plan order.
    /// </summary>
    public static IReadOnlyList<string> ArtifactGroups(IEnumerable<PlannedJob> plan, string systemRoot)
    {
        var root = systemRoot.TrimEnd('\\', '/');
        var groups = new List<string>();
        foreach (var p in plan)
        {
            if (!(ToolAvailability.IsEzRow(p) || p.Artifact.RuleId == PersistenceTriage.RuleId)) continue;
            var rowRoot = (p.Artifact.System?.RootPath ?? p.Artifact.SystemRoot).TrimEnd('\\', '/');
            if (!string.Equals(rowRoot, root, StringComparison.OrdinalIgnoreCase)) continue;
            var name = GroupName(p);
            if (!groups.Contains(name, StringComparer.OrdinalIgnoreCase)) groups.Add(name);
        }
        return groups;
    }

    /// <summary>"$MFT, Registry, Event logs +3 more": at most <paramref name="max"/> groups, then the rest as a count; "none" when empty.</summary>
    public static string ShortList(IReadOnlyList<string> groups, int max = CardGroupLimit)
    {
        if (groups.Count == 0) return "none";
        if (max < 1) max = 1;
        var shown = string.Join(", ", groups.Take(max));
        return groups.Count > max ? $"{shown} +{groups.Count - max} more" : shown;
    }

    /// <summary>The evidence source of a disk system, for the card's "Type:" line: Folder, Mounted drive, Live drive or Archive.</summary>
    public static string EvidenceType(DiscoveredSystem s) => s.Archive != null ? "Archive" : s.AcquisitionKind switch
    {
        AcquisitionKind.ExtractedArchive => "Archive",
        AcquisitionKind.MountedImageOrRawCopy => "Mounted drive",
        AcquisitionKind.LiveVolume => "Live drive",
        _ => "Folder",
    };

    public static DetailBox Disk(string host, string osText, string? osVersion, string root, IReadOnlyList<string> users, long? evidenceSize = null,
        string? evidenceType = null, IReadOnlyList<string>? artifacts = null)
    {
        var rows = new List<KeyValuePair<string, string>> { new("Host", host) };
        if (osVersion != null)
        {
            var (os, build) = SplitOsVersion(osVersion);
            rows.Add(new("OS", os));
            if (build != null) rows.Add(new("Build", build));
        }
        else rows.Add(new("OS", osText));
        rows.Add(new("System root", root));
        if (evidenceType != null) rows.Add(new("Type", evidenceType));
        if (artifacts != null) rows.Add(new("Artifacts", artifacts.Count == 0 ? "none" : string.Join(", ", artifacts)));
        rows.Add(new("Users found", users.Count == 0 ? "none" : string.Join(", ", users)));
        if (evidenceSize is > 0) rows.Add(new("Evidence size", NetworkTriage.FormatSize(evidenceSize.Value)));
        return new DetailBox(rows);
    }

    public static DetailBox Memory(MemoryImage image, DateTime? modifiedUtc)
    {
        var rows = new List<KeyValuePair<string, string>> { new("File", image.Path), new("Size", NetworkTriage.FormatSize(image.Size)) };
        if (modifiedUtc != null) rows.Add(new("Modified", modifiedUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"));
        if (image.Os != MemoryOs.Unknown) rows.Add(new("Detected OS", image.Os == MemoryOs.Mac ? "macOS" : image.Os.ToString()));
        return new DetailBox(rows);
    }

    public static DetailBox Network(PcapFile file) =>
        new(new List<KeyValuePair<string, string>> { new("File", file.Path), new("Size", NetworkTriage.FormatSize(file.Size)), new("Format", file.FormatLabel) });
}
