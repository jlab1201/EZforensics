namespace EzForensics.Core.Triage;

/// <summary>
/// The default tick of the Ingest planned jobs (the initial selection after Scan and the "Defaults" button): only the SANS fast-forensics
/// triage set for disk evidence. Every other job (memory, network and RITA jobs, super timeline, SRUM, SQLite hunt, Recycle Bin, Windows
/// Timeline, persistence, imported rules...) is planned but left unticked.
/// </summary>
public static class IngestDefaults
{
    /// <summary>
    /// SANS fast-forensics triage set (the artifact categories of SANS FOR498 / FOR508 triage and the KAPE <c>!SANS_Triage</c> target /
    /// <c>!EZParser</c> module approach): computer details, $MFT + $UsnJrnl:$J (MFTECmd), the filesystem MACB timeline (built from the $MFT), Prefetch (PECmd), Amcache (AmcacheParser),
    /// Shimcache (AppCompatCacheParser), event logs (EvtxECmd, recommended event IDs), registry (RECmd batch), LNK (LECmd), jump lists
    /// (JLECmd), ShellBags (SBECmd) and the process tree data built from the event logs / memory output. Values are planned-job rule ids (<c>Resources/triage/rules.json</c> and <see cref="IngestSteps"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> FastForensicsRuleIds = new[]
    {
        IngestSteps.ComputerDetailsRuleId,
        "mft", IngestSteps.MacbTimelineRuleId, "usnjrnl",
        "prefetch",
        "amcache",
        "appcompat",
        "evtx",
        "registry",
        "lnk",
        "jumplists",
        "shellbags",
        IngestSteps.ProcessTreeRuleId,
    };

    private static readonly HashSet<string> BuiltInSet = new(FastForensicsRuleIds, StringComparer.OrdinalIgnoreCase);
    private static volatile HashSet<string> _fastSet = BuiltInSet;

    /// <summary>Uses the user's built-in "SANS Triage" Ingest selection (Options) as the scan-time default tick; null = the built-in set.</summary>
    public static void UseDefaultSelection(IEnumerable<string>? ruleIds) =>
        _fastSet = ruleIds == null ? BuiltInSet : new HashSet<string>(ruleIds, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="p"/> is ticked by default (see the class summary).</summary>
    public static bool IsDefault(PlannedJob p) =>
        _fastSet.Contains(p.Artifact.RuleId) && (IngestSteps.IsStep(p) || p.SelectedByDefault);

    /// <summary><paramref name="p"/> with <see cref="PlannedJob.SelectedByDefault"/> set to <see cref="IsDefault"/>.</summary>
    public static PlannedJob Apply(PlannedJob p)
    {
        var on = IsDefault(p);
        return on == p.SelectedByDefault ? p : p with { SelectedByDefault = on };
    }
}
