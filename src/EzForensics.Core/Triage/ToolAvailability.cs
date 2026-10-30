using EzForensics.Core.Execution;
using EzForensics.Core.Tools;

namespace EzForensics.Core.Triage;

/// <summary>Which external tools are known to be missing right now (false while a tool is still being probed: nothing is greyed then).</summary>
public sealed record ToolStatus(bool ZeekMissing = false, bool RitaMissing = false, bool VolatilityMissing = false, bool PlasoMissing = false);

/// <summary>The Detected artifacts cards of the Ingest page.</summary>
public enum DetectedCardKind { Disk, Memory, Network }

/// <summary>Where a card's "Set up" link goes.</summary>
public enum SetUpTarget { None, Wizard, Zeek, Rita, Volatility }

/// <summary>A card's availability: greyed out (checkboxes disabled), a note (RITA only), and the "Set up" link.</summary>
public sealed record CardState(bool Disabled, string? Reason, string? Note, string? SetUpText, SetUpTarget Target)
{
    public static readonly CardState Ready = new(false, null, null, null, SetUpTarget.None);
    public bool HasLink => Target != SetUpTarget.None;
}

/// <summary>
/// Pure availability rules of the Ingest page: why a planned row (or a Detected artifacts card) cannot run because its tool is not installed
/// or has an issue. Such rows and cards are shown greyed out, unticked and with the reason as tooltip.
/// </summary>
public static class ToolAvailability
{
    /// <summary>True for a disk-artifact row run by an EZ tool (not a step, Zeek, RITA, memory, persistence row).</summary>
    public static bool IsEzRow(PlannedJob p)
    {
        if (IngestSteps.IsStep(p)) return false;
        var rule = p.Artifact.RuleId;
        return rule != NetworkTriage.RuleId && rule != MemoryTriage.RuleId && rule != Rita.RitaJobs.RuleId
            && rule != PersistenceTriage.RuleId;
    }

    /// <summary>Why <paramref name="p"/> cannot run (null = available).</summary>
    public static string? RowReason(PlannedJob p, ToolStatus s, IToolLocator? locator, BuildFlavor? preferred = null)
    {
        var rule = p.Artifact.RuleId;
        // built-in steps: no external tool to find
        if (IngestSteps.IsComputerDetails(p)) return null;
        if (IngestSteps.IsSupertimeline(p)) return s.PlasoMissing ? "plaso is not set up (Tools > Timeline)." : null;
        if (rule == NetworkTriage.RuleId) return s.ZeekMissing ? "Zeek is not set up (Tools > Network)." : null;
        if (rule == Rita.RitaJobs.RuleId)
            return s.ZeekMissing ? "Zeek is not set up (Tools > Network): RITA reads its logs."
                 : s.RitaMissing ? "RITA is not set up (Tools > Network)." : null;
        if (rule == MemoryTriage.RuleId) return s.VolatilityMissing ? "Volatility 3 is not set up (Tools > Memory)." : null;
        if (rule == PersistenceTriage.RuleId || locator == null) return null;
        var missing = TriageRunCheck.ToolMissing(p.Spec, locator, preferred);
        return missing == null ? null : $"{Path.GetFileName(missing)} is not installed (Open setup, or Tools).";
    }

    /// <summary>
    /// A card's state. <paramref name="hasItems"/>: the scan found something for the card. <paramref name="ezAllMissing"/>: the card's EZ rows
    /// exist and none of their tools is installed. Memory needs Volatility, Network needs Zeek (RITA alone missing is only noted), Disk the EZ tools.
    /// </summary>
    public static CardState Card(DetectedCardKind kind, ToolStatus s, bool hasItems, bool ezAllMissing)
    {
        if (!hasItems) return CardState.Ready;
        switch (kind)
        {
            case DetectedCardKind.Memory when s.VolatilityMissing:
                return new CardState(true, "Volatility 3 is not set up: the memory jobs cannot run.", null, "Set up Volatility", SetUpTarget.Volatility);
            case DetectedCardKind.Network when s.ZeekMissing:
                return new CardState(true, "Zeek is not set up: the packet capture jobs cannot run.", null, "Set up Zeek", SetUpTarget.Zeek);
            case DetectedCardKind.Network when s.RitaMissing:
                return new CardState(false, null, "RITA is not set up: the RITA jobs cannot run.", "Set up RITA", SetUpTarget.Rita);
            case DetectedCardKind.Disk when ezAllMissing:
                return new CardState(true, "The EZ tools are not installed: the disk jobs cannot run.", null, "Set up", SetUpTarget.Wizard);
            default:
                return CardState.Ready;
        }
    }
}
