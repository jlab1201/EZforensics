using EzForensics.Core.Network;
using EzForensics.Core.Platforms;

namespace EzForensics.Core.Triage;

/// <summary>One row of the Triage "Detected systems" list: a discovered system, whether it is checked, and its (edited) host label.</summary>
public sealed record SystemChoice(DiscoveredSystem System, bool Included, string HostName)
{
    /// <summary>The system as planned: trimmed label as host name (the discovered name while the label is empty).</summary>
    public DiscoveredSystem Effective => string.IsNullOrWhiteSpace(HostName) || HostName.Trim() == System.HostName ? System : System with { HostName = HostName.Trim() };
}

/// <summary>
/// Host picker logic (testable without the UI): which systems are triaged and under which host folder name. A relabelled system is a
/// <see cref="DiscoveredSystem"/> copy with the new <see cref="DiscoveredSystem.HostName"/>, so planners write to <c>&lt;case&gt;\&lt;label&gt;\...</c>.
/// </summary>
public static class SystemSelection
{
    /// <summary>
    /// Null when <paramref name="label"/> is usable as a host folder: not empty, folder-safe (<see cref="ArtifactOutputLayout.SafeHostFolder"/> leaves it
    /// unchanged), not reserved (Timeline, logs, SystemInfo, artifact categories) and different from every name in <paramref name="others"/> (case-insensitive).
    /// </summary>
    public static string? ValidateHostLabel(string? label, IEnumerable<string?> others)
    {
        var name = (label ?? "").Trim();
        if (name.Length == 0) return "Enter a host name.";
        if (TriagePlanner.HostFolder(name) != name)
            return ArtifactOutputLayout.SafeHostFolder(name) != name
                ? "Not usable as a folder name (no \\ / : * ? \" < > |, no trailing dot or space, max 64 characters)."
                : "This name is reserved (Timeline, logs, SystemInfo or an artifact category).";
        // compare host FOLDERS: another label "Timeline" is written to Host_Timeline, so "Host_Timeline" would share its folder
        if (others.Any(o => string.Equals((o ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrWhiteSpace(o) && string.Equals(TriagePlanner.HostFolder(o.Trim()), name, StringComparison.OrdinalIgnoreCase))))
            return "Another system already uses this name (or the same host folder).";
        return null;
    }

    /// <summary>Validation message per choice (null = fine), in order. Names are compared with all other rows, checked or not.</summary>
    public static IReadOnlyList<string?> Validate(IReadOnlyList<SystemChoice> choices) =>
        choices.Select((c, i) => ValidateHostLabel(c.HostName, choices.Where((_, j) => j != i).Select(o => o.HostName))).ToList();

    /// <summary>Checked systems with their edited labels.</summary>
    public static IReadOnlyList<DiscoveredSystem> Active(IReadOnlyList<SystemChoice> choices) =>
        choices.Where(c => c.Included).Select(c => c.Effective).ToList();

    /// <summary>Every system (checked or not) with its edited label; used to attribute captures that sit under a system root.</summary>
    public static IReadOnlyList<DiscoveredSystem> Labelled(IReadOnlyList<SystemChoice> choices) => choices.Select(c => c.Effective).ToList();

    /// <summary>True when <paramref name="root"/> belongs to a checked system, or to none of the choices.</summary>
    public static bool IsRootIncluded(IReadOnlyList<SystemChoice> choices, string? root)
    {
        if (string.IsNullOrEmpty(root)) return true;
        var c = choices.FirstOrDefault(x => string.Equals(x.System.RootPath, root, StringComparison.OrdinalIgnoreCase));
        return c == null || c.Included;
    }

    /// <summary>Hits of the checked systems, each carrying its system with the edited host label. Hits without a system are kept.</summary>
    public static List<DetectedArtifact> Filter(IReadOnlyList<DetectedArtifact> found, IReadOnlyList<SystemChoice> choices)
    {
        if (choices.Count == 0) return found.ToList();
        var result = new List<DetectedArtifact>();
        foreach (var a in found)
        {
            var c = a.System == null ? null : choices.FirstOrDefault(x => string.Equals(x.System.RootPath, a.System.RootPath, StringComparison.OrdinalIgnoreCase));
            if (c == null) { result.Add(a); continue; }
            if (!c.Included) continue;
            result.Add(c.Effective.HostName == a.System!.HostName ? a : a with { System = c.Effective });
        }
        return result;
    }

    /// <summary>
    /// Every hit (checked system or not), each carrying its system with the edited host label: the Ingest plan is built from all of them and
    /// an unchecked system only excludes its rows (<see cref="IngestExclusion"/>).
    /// </summary>
    public static List<DetectedArtifact> Label(IReadOnlyList<DetectedArtifact> found, IReadOnlyList<SystemChoice> choices) =>
        Filter(found, choices.Select(c => c with { Included = true }).ToList());

    /// <summary>Captures except those under an unchecked system's root.</summary>
    public static List<PcapFile> FilterPcaps(IReadOnlyList<PcapFile> pcaps, IReadOnlyList<SystemChoice> choices)
    {
        var excluded = choices.Where(c => !c.Included).Select(c => c.System.RootPath.TrimEnd('\\', '/') + "\\").ToList();
        return pcaps.Where(p => !excluded.Any(r => p.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Memory images except those under an unchecked system's root.</summary>
    public static List<Memory.MemoryImage> FilterMemoryImages(IReadOnlyList<Memory.MemoryImage> images, IReadOnlyList<SystemChoice> choices)
    {
        var excluded = choices.Where(c => !c.Included).Select(c => c.System.RootPath.TrimEnd('\\', '/') + "\\").ToList();
        return images.Where(m => !excluded.Any(r => m.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
