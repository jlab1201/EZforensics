using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>One round pictogram of the stage journey strip (or a "…" gap for stages with nothing found).</summary>
public sealed partial class StageChipViewModel : ObservableObject
{
    public StageChipViewModel(StageStripItem item, int delayMs)
    {
        Item = item;
        DelayMs = delayMs;
    }

    public StageStripItem Item { get; }
    public bool IsGap => Item.IsGap;
    public string Stage => Item.Stage?.Stage ?? "";
    public string Glyph => Item.Stage?.Glyph ?? "";
    public bool HasEvidence => Item.Stage?.HasEvidence == true;
    /// <summary>"High", ... ; "" when nothing was found (the ring stays neutral).</summary>
    public string Severity => Item.Stage?.Severity ?? "";
    public int Count => Item.Stage is { } s ? Math.Max(s.Findings, s.Moments) : 0;
    public string CountText => Count > 99 ? "99+" : Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>When it lights up on load (lit stages one after another).</summary>
    public int DelayMs { get; }
    public string ToolTip => IsGap
        ? "Nothing found at: " + string.Join(", ", Item.Hidden)
        : HasEvidence
            ? $"{Stage}: {Plural(Item.Stage!.Findings, "finding")}, {Plural(Item.Stage.Moments, "key event")}. Highest: {Severity}. Click to show only these."
            : $"{Stage}: nothing found";
    public string AutomationName => IsGap ? "Stages with nothing found" : HasEvidence ? $"{Stage}, {Count}, {Severity}" : $"{Stage}, nothing found";

    // the connecting line (the journey path): each chip draws the half to its left and the half to its right
    /// <summary>Time per chip of the line drawing itself left to right (ms); a lit chip pops in as the line reaches its centre.</summary>
    public const int LineStepMs = 130;
    public bool HasLeftLine { get; init; }
    public bool HasRightLine { get; init; }
    /// <summary>Dashed unless both ends of the half have evidence.</summary>
    public bool LeftDashed { get; init; }
    public bool RightDashed { get; init; }
    public int LeftLineDelayMs { get; init; }
    public int RightLineDelayMs => LeftLineDelayMs + LineStepMs / 2;
    public int LineHalfMs => LineStepMs / 2;
    /// <summary>The most severe stage (most findings on a tie): it pulses softly once lit.</summary>
    public bool IsPeak { get; init; }
    public int PulseDelayMs => DelayMs + 300;

    /// <summary>
    /// Chips for a strip with the journey line worked out: solid between neighbours with evidence, dashed through the rest, timed so
    /// the line draws left to right and each lit stage pops in as it arrives.
    /// </summary>
    public static IReadOnlyList<StageChipViewModel> ForStrip(IReadOnlyList<StageStripItem> strip)
    {
        bool Lit(int i) => i >= 0 && i < strip.Count && strip[i].Stage?.HasEvidence == true;
        var peak = strip.Select((it, i) => (it, i)).Where(x => Lit(x.i))
            .OrderByDescending(x => x.it.Stage!.MaxSeverityRank).ThenByDescending(x => Math.Max(x.it.Stage!.Findings, x.it.Stage.Moments)).ThenBy(x => x.i)
            .Select(x => x.i).DefaultIfEmpty(-1).First();
        return strip.Select((it, i) => new StageChipViewModel(it, i * LineStepMs + LineStepMs / 2)
        {
            HasLeftLine = i > 0,
            HasRightLine = i < strip.Count - 1,
            LeftDashed = !(Lit(i - 1) && Lit(i)),
            RightDashed = !(Lit(i) && Lit(i + 1)),
            LeftLineDelayMs = i * LineStepMs,
            IsPeak = i == peak,
        }).ToList();
    }

    /// <summary>The cards are filtered to this stage.</summary>
    [ObservableProperty] private bool _isActive;
    /// <summary>The slide being shown is at this stage (others are faint on the slide).</summary>
    [ObservableProperty] private bool _isCurrent;

    internal static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>One computer tile of the computers map.</summary>
public sealed partial class HostTileViewModel : ObservableObject
{
    public HostTileViewModel(HostSummary host)
    {
        Summary = host;
        Dots = SpectreFinding.Severities.Select((s, i) => (s, n: host.BySeverity[i])).Where(x => x.n > 0).Reverse()
            .Select(x => new SeverityDot(x.s, x.n)).ToList();
    }

    public HostSummary Summary { get; }
    public string Host => Summary.Host;
    public bool HasEvidence => Summary.HasEvidence;
    public string Severity => Summary.Severity;
    /// <summary>Count per severity, most severe first (colour and word together).</summary>
    public IReadOnlyList<SeverityDot> Dots { get; }
    public string CountText => !HasEvidence ? "Nothing found"
        : StageChipViewModel.Plural(Summary.Findings, "finding") + (Summary.Moments > 0 ? " · " + StageChipViewModel.Plural(Summary.Moments, "key event") : "");
    /// <summary>"Names seen in the records: A, B" when the records name this computer differently; "" otherwise.</summary>
    public string AliasLine => Summary.AliasLine;
    public bool HasAliases => AliasLine.Length > 0;
    public string ToolTip => (HasEvidence ? $"{Host}: {CountText}. Click to show only this host." : $"{Host}: nothing found") + (HasAliases ? " " + AliasLine + "." : "");

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>"3 High": a count with its severity word (the colour comes from the word).</summary>
public sealed record SeverityDot(string Severity, int Count, bool IsActive = false)
{
    public string Text => $"{Count} {Severity}";
    public string ToolTip => $"Show only {Severity} key events";
    public string AutomationName => $"{Severity}, {Count}";
}
