using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Analysis;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>What you can do to the items on the page: Add my analysis, the key event actions, Keep anyway / Remove, and the duplicate compare view.</summary>
public sealed partial class FindingsViewModel
{
    // ------------------------------------------------------------ async change (store IO off the UI thread)

    /// <summary>Like <see cref="Mutate"/> but the store work (and CSV reads) run off the UI thread.</summary>
    internal async Task<T?> MutateAsync<T>(Func<CaseAnalysis, T> change)
    {
        if (CaseDir is not { } dir) return default;
        var examiner = Examiner;
        var (store, result) = await Task.Run(() =>
        {
            CurrentIdentity.Provider = () => examiner;
            var st = CaseAnalysisStore.Open(dir);
            if (st.ReadOnlyReason != null) return (st, default(T));
            var r = change(st.Data);
            st.Save();
            return (st, r);
        });
        if (store.ReadOnlyReason != null) { ShowMessage(store.ReadOnlyReason, InfoSeverity.Warning); return default; }
        if (!string.Equals(dir, _openedDir, StringComparison.OrdinalIgnoreCase)) return result;
        _store = store;
        RebuildFromStore();
        return result;
    }

    // ------------------------------------------------------------ Add my analysis

    /// <summary>Brings the analyst's key events and flagged rows into the page (design section 3): one fixed sync, idempotent.</summary>
    [RelayCommand]
    private Task AddMyAnalysisAsync() => GuardAsync(async () =>
    {
        if (CaseDir is not { } dir) return;
        await LoadHostInfoAsync();
        var hostOf = SyncOptionsFor(null).HostForCasePath;
        var res = await MutateAsync(a =>
        {
            using var reader = new CsvAnalysisRowReader(dir);
            var flags = AnalysisSync.ReadSidecars(dir);
            return (Result: AnalysisSync.Sync(a, new SyncOptions { RowReader = reader, HostForCasePath = hostOf }), Flags: flags);
        });
        if (res.Result == null) return;
        _flags = res.Flags;
        var r = res.Result;
        ShowMessage(r.AnyChange
            ? $"Added from your analysis: {r.Created} new, {r.Updated} updated" + (r.RemovedAtSource > 0 ? $", {r.RemovedAtSource} removed in Analyze" : "")
              + (r.Duplicates.Count > 0 ? $". {r.Duplicates.Count} possible duplicate{(r.Duplicates.Count == 1 ? "" : "s")} to check." : ".")
            : "Nothing new in your analysis.", InfoSeverity.Success);
        await RefreshSyncBadgeAsync(dir);
    }, "Could not add your analysis");

    // ------------------------------------------------------------ key events from findings

    /// <summary>The findings selected in the Details list (Extended selection; the view keeps it in step). Falls back to <see cref="Selected"/>.</summary>
    public IList<FindingRowViewModel> SelectedRows { get; } = new List<FindingRowViewModel>();

    private IReadOnlyList<FindingRowViewModel> PickedRows()
    {
        var rows = SelectedRows.Where(r => r.Item != null).ToList();
        if (rows.Count == 0 && Selected is { Item: not null } s) rows.Add(s);
        return rows;
    }

    /// <summary>The view tells the page which rows are selected.</summary>
    public void SetSelectedRows(IEnumerable<FindingRowViewModel> rows)
    {
        SelectedRows.Clear();
        foreach (var r in rows) SelectedRows.Add(r);
        OnPropertyChanged(nameof(CanGroup));
        GroupIntoKeyEventCommand.NotifyCanExecuteChanged();
    }

    public bool CanGroup => SelectedRows.Count(r => r.Item != null) >= 2;

    /// <summary>Add to timeline: a key event with just this finding (one click). A finding already in a key event stays where it is.</summary>
    [RelayCommand]
    private void AddToTimeline(FindingRowViewModel? row)
    {
        row ??= Selected;
        if (row?.Item is not { } item) return;
        var id = item.Id;
        string? made = null;
        Mutate(a =>
        {
            if (a.Find(id) == null) return false;
            if (KeyEventOps.KeyEventOf(a, id) is { } k) { made = k.Id; return false; }
            made = KeyEventOps.AddToTimeline(a, id).Id;
            return true;
        });
        SelectKeyEventRow(made);
    }

    /// <summary>Group into key event: one key event from every selected finding (each leaves the key event it was in).</summary>
    [RelayCommand(CanExecute = nameof(CanGroup))]
    private void GroupIntoKeyEvent()
    {
        var ids = PickedRows().Select(r => r.Item!.Id).Distinct().ToList();
        if (ids.Count == 0) return;
        string? made = null;
        Mutate(a => { made = KeyEventOps.Group(a, ids).Id; return true; });
        SelectKeyEventRow(made);
    }

    /// <summary>"New key event…" in the Add to key event menu: one key event from the selected findings (just the current one when only one is selected).</summary>
    [RelayCommand]
    private void NewKeyEvent()
    {
        if (PickedRows().Count >= 2) GroupIntoKeyEvent(); else AddToTimeline(Selected);
    }

    /// <summary>Add to key event: puts the selected findings into an existing key event.</summary>
    [RelayCommand]
    private void AddToKeyEvent(string? keyEventId)
    {
        if (keyEventId == null) return;
        var ids = PickedRows().Select(r => r.Item!.Id).Distinct().ToList();
        if (ids.Count == 0) return;
        Mutate(a =>
        {
            bool any = false;
            foreach (var id in ids) any |= KeyEventOps.AddFinding(a, keyEventId, id);
            return any;
        });
        SelectKeyEventRow(keyEventId);
    }

    /// <summary>Takes one finding out of a key event; a key event left empty is removed.</summary>
    internal void RemoveFindingFromKeyEvent(string keyEventId, string findingId) =>
        Mutate(a => { if (a.FindKeyEvent(keyEventId) is not { } k || !k.FindingIds.Contains(findingId)) return false; KeyEventOps.RemoveFinding(a, keyEventId, findingId); return true; });

    // ------------------------------------------------------------ removed at source

    [RelayCommand]
    private void KeepAnyway(FindingRowViewModel? row)
    {
        row ??= Selected;
        if (row?.Item == null) return;
        var id = row.Item.Id;
        Mutate(a => { if (a.Find(id) is not { } i) return false; AnalysisEdits.KeepAnyway(i); return true; });
    }

    /// <summary>Overridable by tests: asks whether hidden duplicates and merged items come back when their kept item is removed.</summary>
    public Func<string, string, bool> ConfirmRestore { get; set; } = (title, message) => Dialogs.Confirm(title, message, "Restore them", "Leave them hidden");

    /// <summary>Remove: leaves a tombstone, so syncs and imports never bring the item back.</summary>
    [RelayCommand]
    private void RemoveItem(FindingRowViewModel? row)
    {
        row ??= Selected;
        if (row?.Item == null || Analysis?.Find(row.Item.Id) is not { } current) return;
        var id = row.Item.Id;
        var hidden = AnalysisEdits.HiddenBy(Analysis, current);
        bool restore = hidden.Count == 0 || ConfirmRestore("Remove " + row.Title,
            $"{hidden.Count} duplicate or merged item{(hidden.Count == 1 ? " is" : "s are")} hidden behind this one. Restore {(hidden.Count == 1 ? "it" : "them")}?");
        Mutate(a => { if (a.Find(id) is not { } i) return false; AnalysisEdits.Remove(a, i, restore); return true; });
    }

    /// <summary>Re-raised pair: restores what is hidden behind this item (the other half of "Keep B anyway, or restore A?").</summary>
    [RelayCommand]
    private void RestoreHidden(FindingRowViewModel? row)
    {
        row ??= Selected;
        if (row?.Item == null) return;
        var id = row.Item.Id;
        Mutate(a => a.Find(id) is { } i && AnalysisEdits.RestoreHiddenBy(a, i) > 0);
    }

    // ------------------------------------------------------------ duplicates

    /// <summary>The open duplicate pairs (both items are key events); the cards show a badge and the compare view lists them.</summary>
    public IReadOnlyList<DuplicatePair> DuplicatePairs => _adapted.Pairs;
    public bool HasDuplicatePairs => _adapted.Pairs.Count > 0;
    public string DuplicateBadgeText => _adapted.Pairs.Count == 1 ? "1 possible duplicate" : $"{_adapted.Pairs.Count} possible duplicates";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCompareOpen))] private DuplicateCompareViewModel? _compare;
    public bool IsCompareOpen => Compare != null;

    /// <summary>Opens the side-by-side compare of the pair that includes <paramref name="row"/>, else the first open pair.</summary>
    [RelayCommand]
    private void OpenCompare(FindingRowViewModel? row)
    {
        var pair = row?.Item != null ? _adapted.Pairs.FirstOrDefault(p => p.AId == row.Item.Id || p.BId == row.Item.Id) : _adapted.Pairs.FirstOrDefault();
        pair ??= _adapted.Pairs.FirstOrDefault();
        if (pair == null || Analysis == null) { ShowMessage("There are no duplicates to compare.", InfoSeverity.Info); return; }
        Compare = DuplicateCompareViewModel.For(Analysis, pair, this);
    }

    [RelayCommand] private void CloseCompare() => Compare = null;

    /// <summary>Keep A / Keep B / Merge / Keep both (<see cref="DuplicateDetector.Resolve"/>); nothing is resolved without this click.</summary>
    internal void ResolvePair(DuplicatePair pair, DuplicateResolution op)
    {
        Mutate(a => { DuplicateDetector.Resolve(a, op, pair.AId, pair.BId); return true; });
        Compare = _adapted.Pairs.Count > 0 && Analysis != null ? DuplicateCompareViewModel.For(Analysis, _adapted.Pairs[0], this) : null;
    }

    private void DuplicatesChanged()
    {
        foreach (var n in new[] { nameof(DuplicatePairs), nameof(HasDuplicatePairs), nameof(DuplicateBadgeText) })
            OnPropertyChanged(n);
    }
}

/// <summary>One duplicate pair for the compare view: both items side by side and the four resolutions.</summary>
public sealed class DuplicateCompareViewModel
{
    private readonly FindingsViewModel _page;
    public DuplicatePair Pair { get; }
    public DuplicateSide A { get; }
    public DuplicateSide B { get; }
    public string Reason => Pair.Reason;
    public string MatchText => Pair.Match == DuplicateMatch.SameEvidence ? "Same evidence" : "Possible duplicate";

    private DuplicateCompareViewModel(FindingsViewModel page, DuplicatePair pair, DuplicateSide a, DuplicateSide b) { _page = page; Pair = pair; A = a; B = b; }

    internal static DuplicateCompareViewModel For(CaseAnalysis a, DuplicatePair pair, FindingsViewModel page) =>
        new(page, pair, DuplicateSide.From(a, a.Find(pair.AId)!), DuplicateSide.From(a, a.Find(pair.BId)!));

    public IRelayCommand KeepACommand => new RelayCommand(() => _page.ResolvePair(Pair, DuplicateResolution.KeepA));
    public IRelayCommand KeepBCommand => new RelayCommand(() => _page.ResolvePair(Pair, DuplicateResolution.KeepB));
    public IRelayCommand MergeCommand => new RelayCommand(() => _page.ResolvePair(Pair, DuplicateResolution.Merge));
    public IRelayCommand KeepBothCommand => new RelayCommand(() => _page.ResolvePair(Pair, DuplicateResolution.KeepBoth));
}

/// <summary>One side of the compare view: what the item says and how much evidence it has.</summary>
public sealed record DuplicateSide(string Id, string Title, string Severity, string Origin, string Technique, string Host, string Times, int Rows, string Notes)
{
    internal static DuplicateSide From(CaseAnalysis a, AnalysisItem i) => new(i.Id, i.Title, i.Severity, i.Origin == ItemOrigin.Analyst ? "You" : "Spectre",
        string.Join(", ", i.Mitre), i.Host ?? "",
        i.FirstSeen is { } f ? f.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "No time",
        a.EffectiveRows(i).Count, i.Notes.Length > 0 ? i.Notes : i.Description);
}
