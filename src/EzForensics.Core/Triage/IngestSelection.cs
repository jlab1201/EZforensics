namespace EzForensics.Core.Triage;

/// <summary>A named set of Ingest planned-job rule ids ("Custom selection"): picking it ticks exactly those rows.</summary>
public sealed class IngestSelection
{
    public string Name { get; set; } = "";
    public List<string> RuleIds { get; set; } = new();

    public IngestSelection() { }
    public IngestSelection(string name, IEnumerable<string> ruleIds) { Name = name; RuleIds = ruleIds.ToList(); }

    public IngestSelection Clone() => new(Name, RuleIds);
}

/// <summary>One ingest rule as listed in the selection editor.</summary>
public sealed record IngestRuleInfo(string Id, string Label, string Group);

/// <summary>Rules, validation and normalisation of the saved Ingest selections.</summary>
public static class IngestSelections
{
    /// <summary>Name of the built-in selection: always present, cannot be renamed or deleted.</summary>
    public const string DefaultName = "SANS Triage";

    /// <summary>Name the built-in selection had up to 1.5.11; a stored selection with this name is the built-in one and is renamed on load.</summary>
    public const string LegacyDefaultName = "Default";

    public const string DiskGroup = "Disk", MemoryGroup = "Memory", NetworkGroup = "Network", TimelineGroup = "Timeline";
    public static readonly string[] Groups = { DiskGroup, MemoryGroup, NetworkGroup, TimelineGroup };

    public static bool IsDefault(string? name) =>
        string.Equals(name?.Trim(), DefaultName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), LegacyDefaultName, StringComparison.OrdinalIgnoreCase);

    public static IngestSelection BuiltInDefault() => new(DefaultName, IngestDefaults.FastForensicsRuleIds);

    /// <summary>
    /// Every rule id an Ingest plan can contain, with a readable label and its group: computer details and the embedded disk rules (plus
    /// persistence), memory images, packet captures (Zeek) and RITA, the timelines and Process tree data.
    /// </summary>
    public static IReadOnlyList<IngestRuleInfo> AllRules(IEnumerable<TriageRule>? rules = null)
    {
        var list = new List<IngestRuleInfo> { new(IngestSteps.ComputerDetailsRuleId, "Computer details (offline registry)", DiskGroup) };
        foreach (var r in rules ?? TriageRules.LoadEmbedded())
            if (!list.Any(x => string.Equals(x.Id, r.Id, StringComparison.OrdinalIgnoreCase)))
                list.Add(new(r.Id, string.IsNullOrWhiteSpace(r.Label) ? r.Id : r.Label, DiskGroup));
        list.Add(new(PersistenceTriage.RuleId, "Persistence (registry, tasks, services)", DiskGroup));
        list.Add(new(MemoryTriage.RuleId, "Memory images (Volatility)", MemoryGroup));
        list.Add(new(NetworkTriage.RuleId, "Packet captures (Zeek)", NetworkGroup));
        list.Add(new(Rita.RitaJobs.RuleId, "RITA (beaconing analysis)", NetworkGroup));
        list.Add(new(IngestSteps.MacbTimelineRuleId, "Filesystem timeline (MACB)", TimelineGroup));
        list.Add(new(IngestSteps.SupertimelineRuleId, "Supertimeline (plaso)", TimelineGroup));
        list.Add(new(IngestSteps.ProcessTreeRuleId, "Process tree data (event logs, memory)", TimelineGroup));
        return list;
    }

    /// <summary>
    /// The list as it is stored: names trimmed, blank and duplicate names (case-insensitive) dropped, rule ids trimmed and de-duplicated,
    /// and the built-in SANS Triage selection always first (seeded when missing; a stored one keeps its edited rule ids but its name is fixed, and a legacy "Default" is renamed).
    /// </summary>
    public static List<IngestSelection> Normalize(IEnumerable<IngestSelection>? stored)
    {
        var result = new List<IngestSelection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // a legacy "Default" wins over a custom selection the user happened to name "SANS Triage" before the rename
        var def = stored?.FirstOrDefault(s => s != null && string.Equals(s.Name?.Trim(), LegacyDefaultName, StringComparison.OrdinalIgnoreCase))
                  ?? stored?.FirstOrDefault(s => s != null && IsDefault(s.Name));
        result.Add(def == null ? BuiltInDefault() : new IngestSelection(DefaultName, CleanIds(def.RuleIds)));
        seen.Add(DefaultName);
        seen.Add(LegacyDefaultName);
        foreach (var s in stored ?? Enumerable.Empty<IngestSelection>())
        {
            var name = s?.Name?.Trim();
            if (s == null || string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            result.Add(new IngestSelection(name, CleanIds(s.RuleIds)));
        }
        return result;
    }

    private static List<string> CleanIds(IEnumerable<string>? ids) =>
        (ids ?? Enumerable.Empty<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Why <paramref name="name"/> cannot be used for a selection (null = fine). <paramref name="existing"/> = the other selections' names.</summary>
    public static string? ValidateName(string? name, IEnumerable<string> existing)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return "Enter a name for the selection.";
        if (existing.Any(e => string.Equals(e?.Trim(), n, StringComparison.OrdinalIgnoreCase))) return $"A selection named \"{n}\" already exists.";
        return null;
    }

    /// <summary>The selection called <paramref name="name"/> (case-insensitive), else the Default one.</summary>
    public static IngestSelection Find(IEnumerable<IngestSelection>? stored, string? name)
    {
        var all = Normalize(stored);
        return all.FirstOrDefault(s => string.Equals(s.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? all[0];
    }

    /// <summary>True when a planned row with this rule id is ticked by <paramref name="selection"/>.</summary>
    public static bool Includes(IngestSelection selection, string ruleId) =>
        selection.RuleIds.Any(i => string.Equals(i, ruleId, StringComparison.OrdinalIgnoreCase));
}
