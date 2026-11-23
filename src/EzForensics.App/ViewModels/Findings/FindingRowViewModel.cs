using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>One row of the Review list. Wraps the engine's finding (replaced after every save) and holds a pending chat suggestion, if any.</summary>
public sealed partial class FindingRowViewModel : ObservableObject
{
    [ObservableProperty] private SpectreFinding _finding;
    [ObservableProperty] private FindingSuggestion? _pendingSuggestion;

    public FindingRowViewModel(SpectreFinding finding, EzForensics.Core.Analysis.AnalysisItem? item = null, IReadOnlyList<ItemChip>? chips = null, bool active = true)
    {
        _finding = finding; Item = item; Chips = chips ?? Array.Empty<ItemChip>(); IsActive = active;
    }

    /// <summary>The store item behind the row (null only for rows made without a store, in tests).</summary>
    public EzForensics.Core.Analysis.AnalysisItem? Item { get; private set; }
    public IReadOnlyList<ItemChip> Chips { get; private set; }
    /// <summary>Counts in the tiles, timeline and report (see AnalysisView.IsActive).</summary>
    public bool IsActive { get; private set; }

    /// <summary>Takes the fresh state of the same item (after an edit or a sync), so the row keeps its identity and the selection stays.</summary>
    public void Update(SpectreFinding finding, EzForensics.Core.Analysis.AnalysisItem? item, IReadOnlyList<ItemChip>? chips, bool active)
    {
        Item = item; Chips = chips ?? Array.Empty<ItemChip>(); IsActive = active;
        Finding = finding;
        foreach (var n in new[] { nameof(Chips), nameof(InfoChips), nameof(HasInfoChips), nameof(ChipText), nameof(OriginText), nameof(IsActive), nameof(IsRemovedAtSource), nameof(IsHiddenDuplicate), nameof(HasDuplicatePair), nameof(Item), nameof(IsKeyEvent), nameof(StarGlyph) })
            OnPropertyChanged(n);
    }
    /// <summary>"You", "Spectre" or "Both".</summary>
    public string OriginText => Chips.FirstOrDefault(c => c.Kind == ItemChip.Origin)?.Text ?? "";
    public IReadOnlyList<ItemChip> InfoChips => Chips.Where(c => c.Kind != ItemChip.Origin).ToList();
    public bool HasInfoChips => InfoChips.Count > 0;
    public string ChipText => string.Join(" · ", InfoChips.Select(c => c.Text));
    public bool IsRemovedAtSource => Item is { SourceStatus: EzForensics.Core.Analysis.SourceStatus.RemovedAtSource, KeptByUser: false };
    public bool IsHiddenDuplicate => Item?.IsHidden == true;
    public bool HasDuplicatePair => Chips.Any(c => c.Kind == ItemChip.Duplicate);

    public string Id => Finding.Id;
    public string Title => Finding.EffectiveTitle;
    public string Severity => Finding.EffectiveSeverity;
    public string Status => Finding.Status ?? "";
    public string Approval => Finding.ApprovalText;
    public string Mitre => FindingDraft.MitreText(Finding.EffectiveMitreAttack);
    /// <summary>The mapping as Analyze shows it: "T1218 · System Binary Proxy Execution — Defense Evasion", comma separated; empty when unmapped.</summary>
    public string Mapping => Controls.TechniquePicker.DisplayOf(Mitre);
    public bool HasMapping => Mapping.Length > 0;
    /// <summary>
    /// The finding's time with its basis: "UTC" when the timeline dates it (or the engine's time ends in Z), "(local clock)" for an undated log entry
    /// (the clock of the machine that wrote it, no zone known), "(zone not stated)" otherwise.
    /// </summary>
    public string Time
    {
        get
        {
            var raw = Finding.EventTime ?? Finding.FirstSeen ?? "";
            var t = raw.Replace('T', ' ').TrimEnd('Z');
            if (t.Length == 0) return "";
            if (IsDated || raw.EndsWith('Z')) return t + " UTC";
            return t + (string.Equals(Finding.ArtifactType, "logfile_record", StringComparison.OrdinalIgnoreCase) ? " (local clock)" : " (zone not stated)");
        }
    }

    /// <summary>True when the timeline puts this finding at a time (set by the page).</summary>
    public bool IsDated { get; private set; }
    public void SetDated(bool dated)
    {
        if (IsDated == dated) return;
        IsDated = dated;
        OnPropertyChanged(nameof(Time)); OnPropertyChanged(nameof(MetaLine));
    }
    public bool IsEdited => Item != null ? Item.EditedFields.Count > 0 : Finding.IsEdited;
    public bool IsIncluded => Finding.IsIncluded;

    /// <summary>The finding sits in a key event (the star; set by the page).</summary>
    public bool IsKeyEvent { get; private set; }
    /// <summary>The plain tactic worked out from the technique, else from the timeline; empty when unknown (set by the page).</summary>
    public string Tactic { get; private set; } = "";
    public void SetMeta(bool keyEvent, string tactic)
    {
        if (IsKeyEvent != keyEvent) { IsKeyEvent = keyEvent; OnPropertyChanged(nameof(IsKeyEvent)); OnPropertyChanged(nameof(StarGlyph)); }
        if (Tactic != tactic) { Tactic = tactic; OnPropertyChanged(nameof(Tactic)); OnPropertyChanged(nameof(MetaLine)); }
    }
    /// <summary>"Pending · 2026-01-02 08:16:00 UTC": the second line of a list row (empty parts left out).</summary>
    public string MetaLine => string.Join(" · ", new[] { Approval, Time }.Where(x => x.Length > 0));
    /// <summary>Segoe MDL2 filled star for a key event, nothing otherwise (the list's first column).</summary>
    public string StarGlyph => IsKeyEvent ? "\uE735" : "";

    public bool HasSuggestion => PendingSuggestion != null;
    public string SuggestionWhy => PendingSuggestion?.Why ?? "";
    /// <summary>Field-by-field "current → proposed" lines of the pending suggestion.</summary>
    public IReadOnlyList<string> SuggestionLines => PendingSuggestion?.DiffLines(Finding) ?? Array.Empty<string>();

    partial void OnFindingChanged(SpectreFinding value)
    {
        foreach (var n in new[] { nameof(Id), nameof(Title), nameof(Severity), nameof(Status), nameof(Approval), nameof(Mitre), nameof(Mapping), nameof(HasMapping), nameof(Time), nameof(MetaLine), nameof(IsEdited), nameof(IsIncluded), nameof(SuggestionLines) })
            OnPropertyChanged(n);
    }

    partial void OnPendingSuggestionChanged(FindingSuggestion? value)
    {
        OnPropertyChanged(nameof(HasSuggestion)); OnPropertyChanged(nameof(SuggestionWhy)); OnPropertyChanged(nameof(SuggestionLines));
    }
}
