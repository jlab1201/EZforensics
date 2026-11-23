using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>The three lines of the Review status tile; every finding is in exactly one.</summary>
public enum ReviewBucket { Confirmed, Rejected, ToReview }

/// <summary>One line of the Review status tile: a coloured dot, the name and the count. A click shows those findings in Details.</summary>
public sealed record ReviewLine(ReviewBucket Bucket, string Label, int Count)
{
    public string Text => $"{Count} {Label}";
}

public sealed partial class FindingsViewModel
{
    // ------------------------------------------------------------ Overview layout

    /// <summary>The Overview is wide enough for two columns (the tiles side by side, key event cards in two columns); the view sets it from its width.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OverviewColumns))] private bool _isWideLayout = true;
    public int OverviewColumns => IsWideLayout ? 2 : 1;

    // ------------------------------------------------------------ Overview: review status

    /// <summary>Where a finding stands: confirmed, rejected, or still to review.</summary>
    internal static ReviewBucket BucketOf(FindingRowViewModel row)
    {
        var f = row.Finding;
        if (string.Equals(f.Approval, SpectreFinding.Approved, StringComparison.OrdinalIgnoreCase)) return ReviewBucket.Confirmed;
        if (string.Equals(f.Approval, SpectreFinding.Rejected, StringComparison.OrdinalIgnoreCase)) return ReviewBucket.Rejected;
        return ReviewBucket.ToReview;
    }

    /// <summary>Confirmed, Rejected and To review, with counts that add up to the number of active findings.</summary>
    public IReadOnlyList<ReviewLine> ReviewBreakdown
    {
        get
        {
            // key events by review (the tiles and the ring): duplicates, merged and removed-at-source items are left out
            var counts = new int[3];
            foreach (var r in ScopedRows.Where(r => r.Item != null && r.IsActive)) counts[(int)BucketOf(r)]++;
            return new[]
            {
                new ReviewLine(ReviewBucket.Confirmed, "Confirmed", counts[0]),
                new ReviewLine(ReviewBucket.Rejected, "Rejected", counts[1]),
                new ReviewLine(ReviewBucket.ToReview, "To review", counts[2]),
            };
        }
    }

    /// <summary>The review line shown as a filter on the Details list; null shows every finding.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBucketFilter), nameof(BucketFilterText))] private ReviewBucket? _bucketFilter;
    public bool HasBucketFilter => BucketFilter != null;
    public string BucketFilterText => BucketFilter switch
    {
        ReviewBucket.Confirmed => "Confirmed", ReviewBucket.Rejected => "Rejected", ReviewBucket.ToReview => "To review", _ => "",
    };
    partial void OnBucketFilterChanged(ReviewBucket? value) { OnPropertyChanged(nameof(ReviewFilter)); ApplyFilter(); }

    /// <summary>A click on a review line: the Details tab, with only those findings listed.</summary>
    [RelayCommand]
    private void FilterByReview(ReviewLine? line)
    {
        if (line == null) return;
        BucketFilter = line.Bucket;
        SelectedTab = FindingsTab.Details;
    }

    [RelayCommand]
    private void ClearBucketFilter() => BucketFilter = null;

    // ------------------------------------------------------------ key events (the list's star, the editor's toggle)

    /// <summary>Only the findings that sit in a key event are listed in Details.</summary>
    [ObservableProperty] private bool _keyEventsOnly;
    partial void OnKeyEventsOnlyChanged(bool value) => ApplyFilter();

    /// <summary>Per row: the star (the finding sits in a key event) and the tactic.</summary>
    private void RefreshRowMeta()
    {
        var a = Analysis;
        foreach (var r in _all) r.SetMeta(a != null && r.Item != null && KeyEventOps.KeyEventOf(a, r.Item.Id) != null, RowTactic(r));
        OnPropertyChanged(nameof(IsSelectedKeyEvent)); OnPropertyChanged(nameof(KeyEventButtonText)); OnPropertyChanged(nameof(KeyEventGlyph));
        OnPropertyChanged(nameof(SelectedTactic));
    }

    /// <summary>The finding's tactic: from its technique when the list knows it, else the tactic on the timeline (plain name; empty when neither says).</summary>
    private string RowTactic(FindingRowViewModel r)
    {
        if (r.Item != null && AnalysisAdapter.TacticOf(r.Item) is { Length: > 0 } own) return PlainLanguage.Tactic(own);
        foreach (var id in r.Finding.EffectiveMitreAttack)
            if (TechniqueCatalog.TacticOf(id) is { } t) return t;
        if (_findingsById.TryGetValue(r.Id, out var tf) || _findingsById.TryGetValue(r.Finding.FindingId, out tf))
            return string.IsNullOrWhiteSpace(tf.Tactic) ? "" : PlainLanguage.Tactic(tf.Tactic);
        return "";
    }

    public bool IsSelectedKeyEvent => Selected?.IsKeyEvent == true;
    public string KeyEventButtonText => IsSelectedKeyEvent ? "In key event" : "Add to timeline";
    /// <summary>Segoe MDL2: filled star when on, outline when off.</summary>
    public string KeyEventGlyph => IsSelectedKeyEvent ? "" : "";

    /// <summary>On: Add to timeline (a one-finding key event). Off: takes the finding out of its key event.</summary>
    [RelayCommand]
    private void ToggleSelectedKeyEvent()
    {
        if (Selected is not { Item: { } item } row || Analysis is not { } a) return;
        if (KeyEventOps.KeyEventOf(a, item.Id) is { } k) RemoveFindingFromKeyEvent(k.Id, item.Id);
        else AddToTimeline(row);
    }

    /// <summary>The Overview card's "Remove from key events" button: the key event is removed (its findings stay).</summary>
    [RelayCommand]
    private void RemoveKeyEvent(MomentCardViewModel? card) => DismissKeyEvent(KeyEventOfCard(card ?? SelectedMoment)?.Id);

    /// <summary>Removes a key event (a tombstone: imports never bring it back); its findings stay in the list.</summary>
    internal void DismissKeyEvent(string? keyEventId)
    {
        if (keyEventId == null) return;
        Mutate(a => { if (a.FindKeyEvent(keyEventId) is not { Dismissed: false } k) return false; KeyEventEdits.Remove(k); return true; });
    }

    // ------------------------------------------------------------ mapping (technique in, tactic out)

    /// <summary>Techniques to pick from: those in this case first, then the common ones.</summary>
    public IReadOnlyList<string> TechniqueSuggestions
    {
        get
        {
            var inCase = _all.SelectMany(r => r.Finding.EffectiveMitreAttack)
                .Concat(Timeline.Findings.Concat(Timeline.Undated).SelectMany(f => StoryGraphics.SplitTechniques(new[] { f.Technique })))
                .Select(i => i.ToUpperInvariant()).Where(TechniqueCatalog.IsValid).Distinct().OrderBy(i => i, StringComparer.Ordinal);
            return inCase.Concat(TechniqueCatalog.KnownIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>The tactic for the technique in the editor (read only): the first technique the list knows, else the finding's tactic on the timeline.</summary>
    public string SelectedTactic
    {
        get
        {
            if (Selected == null) return "";
            foreach (var id in FindingDraft.ParseMitre(DraftMitre))
                if (TechniqueCatalog.TacticOf(id) is { } t) return t;
            return Selected.Tactic;
        }
    }

    /// <summary>The technique box holds something that is not an ATT&amp;CK id (T1234 or T1234.567); Save waits.</summary>
    public bool MitreInvalid => MitreDiffers && TechniqueCatalog.Invalid(FindingDraft.ParseMitre(DraftMitre)).Count > 0;
    public const string MitreInvalidTip = "Use ATT&CK ids like T1059 or T1059.001, separated by commas.";
}
