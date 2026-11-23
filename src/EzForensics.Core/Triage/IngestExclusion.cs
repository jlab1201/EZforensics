namespace EzForensics.Core.Triage;

/// <summary>What the Detected artifacts cards leave out: unchecked systems, memory images and packet captures (paths compared ignoring case).</summary>
public sealed record IngestCardSelection(IReadOnlyList<SystemChoice> Systems, IReadOnlyCollection<string> ExcludedMemoryImages, IReadOnlyCollection<string> ExcludedPcaps)
{
    public static readonly IngestCardSelection All = new(Array.Empty<SystemChoice>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Card ticks of the Ingest page never remove planned rows: the plan is built from every detected artifact, and an unchecked card item only
/// marks its rows as excluded (greyed, unticked, disabled, with this reason as tooltip). Memory and network rows follow only their own card;
/// computer details, the disk rows, persistence and a host's super timeline follow their system; computer details and the super timeline of
/// unattributed evidence (<c>_Case</c>) are excluded when no Disk system is checked; Process tree data is never excluded.
/// </summary>
public static class IngestExclusion
{
    public const string DiskReason = "Disk not selected";
    public const string MemoryReason = "Memory not selected";
    public const string NetworkReason = "Network not selected";

    /// <summary>The exclusion reason of every row of <paramref name="plan"/> (null = not excluded), in order.</summary>
    public static IReadOnlyList<string?> Reasons(IReadOnlyList<PlannedJob> plan, IngestCardSelection selection)
    {
        var pcapsOff = new HashSet<string>(selection.ExcludedPcaps, StringComparer.OrdinalIgnoreCase);
        var memoryOff = new HashSet<string>(selection.ExcludedMemoryImages, StringComparer.OrdinalIgnoreCase);
        // Zeek rows still planned in: the RITA rows read their output.
        var zeekOn = plan.Where(p => p.Artifact.RuleId == NetworkTriage.RuleId && !pcapsOff.Contains(p.Artifact.MatchedPath)).ToList();
        bool anySystem = selection.Systems.Count == 0 || selection.Systems.Any(s => s.Included);
        return plan.Select(p => Reason(p, selection, pcapsOff, memoryOff, zeekOn, anySystem)).ToList();
    }

    private static string? Reason(PlannedJob p, IngestCardSelection sel, HashSet<string> pcapsOff, HashSet<string> memoryOff,
        IReadOnlyList<PlannedJob> zeekOn, bool anySystem)
    {
        var rule = p.Artifact.RuleId;
        // Process tree data covers every host of the plan: the run only correlates the hosts of rows still planned in
        if (IngestSteps.IsProcessTree(p)) return null;
        // a host's super timeline and MACB timeline follow its system (below); computer details and the _Case timeline follow every system
        if (IngestSteps.IsStep(p) && !((IngestSteps.IsSupertimeline(p) || IngestSteps.IsMacbTimeline(p)) && p.Artifact.System != null)) return anySystem ? null : DiskReason;
        if (rule == MemoryTriage.RuleId) return memoryOff.Contains(p.Artifact.MatchedPath) ? MemoryReason : null;
        if (rule == NetworkTriage.RuleId) return pcapsOff.Contains(p.Artifact.MatchedPath) ? NetworkReason : null;
        if (rule == Rita.RitaJobs.RuleId)
        {
            // one RITA row per Zeek host folder (the case-wide _Case folder included): excluded when none of its captures is planned in
            var host = p.Spec.Provenance?.Host ?? p.Artifact.MatchedPath;
            return zeekOn.Any(z => string.Equals(NetworkTriage.ZeekHost(z), host, StringComparison.OrdinalIgnoreCase)) ? null : NetworkReason;
        }
        var root = p.Artifact.System?.RootPath ?? p.Artifact.SystemRoot;
        if (string.IsNullOrEmpty(root)) return null;
        var choice = sel.Systems.FirstOrDefault(c => string.Equals(c.System.RootPath, root, StringComparison.OrdinalIgnoreCase));
        if (choice == null || choice.Included) return null;
        return sel.Systems.Count == 1 ? DiskReason : $"{DiskReason}: {choice.Effective.HostName}";
    }
}
