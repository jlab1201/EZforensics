using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

public sealed partial class FindingsViewModel
{
    public const int TabDetails = 0, TabEvidence = 1, TabHistory = 2;

    public static IReadOnlyList<string> SeverityFilters { get; } = new[] { FindingsFilter.Any }.Concat(SpectreFinding.Severities.Reverse()).ToList();
    public static IReadOnlyList<string> ApprovalFilters => FindingsFilter.ApprovalChoices;
    public static IReadOnlyList<string> Severities => SpectreFinding.Severities;

    private readonly List<FindingRowViewModel> _all = new();
    private bool _loadingEditor;
    private int _unregistered;

    /// <summary>The findings the filters let through (most severe first).</summary>
    public ObservableCollection<FindingRowViewModel> Rows { get; } = new();

    [ObservableProperty] private bool _isLoadingFindings;
    [ObservableProperty] private string _findingsError = "";
    [ObservableProperty] private string _listSummary = "";

    // ---- filters
    [ObservableProperty] private string _severityFilter = FindingsFilter.Any;
    [ObservableProperty] private string _approvalFilter = FindingsFilter.Any;

    // Details filters: origin, review, duplicate, removed
    public const string AnyOrigin = "Any origin", OriginYou = "You", OriginSpectre = "Spectre", OriginBoth = "Both";
    public const string AnyReview = "Any review";
    public const string AnyDuplicate = "Any duplicate", DuplicatesOnly = "Duplicates only", NoDuplicates = "No duplicates";
    public const string AnySource = "Any source", RemovedOnly = "Removed at source", CurrentOnly = "Source current";
    public static IReadOnlyList<string> OriginFilters { get; } = new[] { AnyOrigin, OriginYou, OriginSpectre, OriginBoth };
    public static IReadOnlyList<string> ReviewFilters { get; } = new[] { AnyReview, "To review", "Confirmed", "Rejected" };
    public static IReadOnlyList<string> DuplicateFilters { get; } = new[] { AnyDuplicate, DuplicatesOnly, NoDuplicates };
    public static IReadOnlyList<string> SourceFilters { get; } = new[] { AnySource, RemovedOnly, CurrentOnly };
    [ObservableProperty] private string _originFilter = AnyOrigin;
    [ObservableProperty] private string _duplicateFilter = AnyDuplicate;
    [ObservableProperty] private string _sourceFilter = AnySource;
    partial void OnOriginFilterChanged(string value) => ApplyFilter();
    partial void OnDuplicateFilterChanged(string value) => ApplyFilter();
    partial void OnSourceFilterChanged(string value) => ApplyFilter();

    /// <summary>The review filter as the combo shows it; maps onto <see cref="BucketFilter"/>.</summary>
    public string ReviewFilter
    {
        get => BucketFilter == null ? AnyReview : BucketFilterText;
        set => BucketFilter = value switch { "To review" => ReviewBucket.ToReview, "Confirmed" => ReviewBucket.Confirmed, "Rejected" => ReviewBucket.Rejected, _ => null };
    }

    private bool PassesDetailFilters(FindingRowViewModel r) =>
        (OriginFilter == AnyOrigin || r.OriginText == OriginFilter)
        && (DuplicateFilter == AnyDuplicate || r.IsHiddenDuplicate == (DuplicateFilter == DuplicatesOnly))
        && (SourceFilter == AnySource || (r.Item?.SourceStatus == SourceStatus.RemovedAtSource) == (SourceFilter == RemovedOnly));
    [ObservableProperty] private string _searchText = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowFindingEditor), nameof(ShowNoFindingSelected))] private FindingRowViewModel? _selected;
    public bool HasSelection => Selected != null;

    // ---- editor (the examiner's values; the engine's stay read-only next to them)
    [ObservableProperty] private string _draftTitle = "";
    [ObservableProperty] private string _draftDescription = "";
    [ObservableProperty] private string _draftSeverity = "Info";
    [ObservableProperty] private string _draftMitre = "";
    [ObservableProperty] private string _draftNotes = "";
    [ObservableProperty] private string _reasonText = "";
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _editMessage = "";

    [ObservableProperty] private int _selectedDetailTab;
    [ObservableProperty] private bool _isLoadingEvidence;
    [ObservableProperty] private string _evidenceMessage = "";
    [ObservableProperty] private bool _isLoadingHistory;
    [ObservableProperty] private string _historyMessage = "";
    public ObservableCollection<string> HistoryLines { get; } = new();

    /// <summary>Overridable by tests: asks the examiner for a short text (rationale of an approval). Null = cancelled.</summary>
    public Func<string, string, string?> Ask { get; set; } = (title, message) => Dialogs.Prompt(title, message);

    // engine values (read-only column)
    public string EngineTitle => Selected?.Finding.Title ?? "";
    public string EngineDescription => Selected?.Finding.Description ?? "";
    public string EngineSeverity => Selected?.Finding.Severity ?? "";
    public string EngineMitre => FindingDraft.MitreText(Selected?.Finding.MitreAttack ?? Array.Empty<string>());
    /// <summary>The engine's mapping spelled out ("T1218 · System Binary Proxy Execution — Defense Evasion").</summary>
    public string EngineMitreDisplay => Controls.TechniquePicker.DisplayOf(EngineMitre);

    public bool HasAnyFindings => _all.Count > 0;
    public bool ShowEmptyList => !IsLoadingFindings && FindingsError.Length == 0 && _all.Count == 0;
    public bool ShowNoMatch => !IsLoadingFindings && _all.Count > 0 && Rows.Count == 0;

    private void ListStateChanged() { OnPropertyChanged(nameof(ShowEmptyList)); OnPropertyChanged(nameof(ShowNoMatch)); }
    partial void OnIsLoadingFindingsChanged(bool value) => ListStateChanged();
    partial void OnFindingsErrorChanged(string value) => ListStateChanged();
    public int PendingApprovals => _all.Count(r => r.Item != null && r.IsActive && r.Finding.Approval == null);

    private bool Differs(string draft, string engine) => !string.Equals(draft.Trim(), engine.Trim(), StringComparison.Ordinal);
    public bool TitleDiffers => Selected != null && Differs(DraftTitle, EngineTitle);
    public bool DescriptionDiffers => Selected != null && Differs(DraftDescription, EngineDescription);
    public bool SeverityDiffers => Selected != null && !string.Equals(DraftSeverity, EngineSeverity, StringComparison.OrdinalIgnoreCase);
    public bool MitreDiffers => Selected != null && !FindingDraft.ParseMitre(DraftMitre).SequenceEqual(Selected.Finding.MitreAttack.Select(s => s.ToUpperInvariant()));
    public bool NotesDiffers => Selected != null && DraftNotes.Trim().Length > 0;

    /// <summary>What Save would send; empty when nothing changed.</summary>
    public SpectreFindingChanges CurrentChanges()
    {
        if (Selected == null) return new SpectreFindingChanges();
        var f = Selected.Finding;
        var c = FindingDraft.BuildChanges(f, DraftTitle, DraftDescription, DraftSeverity, DraftMitre, DraftNotes, f.IsIncluded);
        return c;
    }

    public bool IsDirty => !CurrentChanges().IsEmpty || RowsDirty;
    public bool CanSave => IsDirty && !IsSaving && !MitreInvalid && !RowsInvalid;
    public bool NeedsReason => false;

    private void ResetReview()
    {
        _all.Clear(); Rows.Clear(); Selected = null; _unregistered = 0;
        FindingsError = ""; ListSummary = ""; HistoryLines.Clear(); _detailEvidence?.Close();
        OnPropertyChanged(nameof(HasAnyFindings)); OnPropertyChanged(nameof(PendingApprovals)); ListStateChanged();
    }

    // ------------------------------------------------------------ list

    [RelayCommand]
    private Task RefreshFindingsAsync() => GuardAsync(LoadFindingsAsync, "Could not load the findings");

    /// <summary>Reads the engine's newest results into the store (when it has any), then shows the store.</summary>
    internal async Task LoadFindingsAsync()
    {
        IsLoadingFindings = true; FindingsError = "";
        try
        {
            if (CaseDir is { } cd)
            {
                // what Analyze changed on disk shows up on Refresh: re-read the store, then pull the Analyze findings into their items
                await ReloadStoreAsync(cd);
                if (_store != null) Mutate(a => AnalysisSync.RefreshLinked(a, SyncOptionsFor(null)) > 0);
            }
            if (CaseDir is { } dir && HasEngineResults && EngineAvailable && await ImportFromEngineAsync(dir, force: true)) return;
            if (_store == null && CaseDir is { } d) OpenStore(d); else RebuildFromStore();
        }
        finally { IsLoadingFindings = false; }
    }

    private void ApplyFilter(string? keepId = null)
    {
        keepId ??= Selected?.Id;
        RefreshRowMeta();
        var f = new FindingsFilter(SeverityFilter, ApprovalFilter, false, SearchText);
        var shown = f.Apply(_all.Select(r => r.Finding)).Select(x => x.Id).ToList();
        var byId = _all.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var next = shown.Where(byId.ContainsKey).Select(id => byId[id])
            .Where(r => (!KeyEventsOnly || r.IsKeyEvent) && (BucketFilter == null || BucketOf(r) == BucketFilter) && PassesDetailFilters(r) && RowInHost(r)).ToList();
        if (!next.SequenceEqual(Rows))   // rebuilding drops the list selection, so only when the rows really changed
        {
            Rows.Clear();
            foreach (var r in next) Rows.Add(r);
        }
        ListSummary = $"{Rows.Count} of {(HostFilter == null ? _all.Count : ScopedRows.Count())} findings" + (_unregistered > 0 ? $" · {_unregistered} could not be registered by Spectre" : "");
        Selected = keepId != null ? Rows.FirstOrDefault(r => r.Id == keepId) : null;
        ListStateChanged();
    }

    partial void OnSeverityFilterChanged(string value) => ApplyFilter();
    partial void OnApprovalFilterChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearFilters()
    {
        SeverityFilter = FindingsFilter.Any; ApprovalFilter = FindingsFilter.Any; SearchText = ""; KeyEventsOnly = false; BucketFilter = null;
        OriginFilter = AnyOrigin; DuplicateFilter = AnyDuplicate; SourceFilter = AnySource;
    }

    /// <summary>Selects a finding by its id (for the chat: "go to F-003"). False when it is not in the loaded list.</summary>
    public bool SelectFinding(string id) => OnUi(() => SelectFindingCore(id));

    internal bool SelectFindingCore(string id)
    {
        var row = FindRow(id);
        if (row == null) return false;
        if (!Rows.Contains(row)) ClearFilters();
        Selected = row;
        return true;
    }

    private FindingRowViewModel? FindRow(string id) =>
        _all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Finding.FindingId, id, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------ editor

    partial void OnSelectedChanged(FindingRowViewModel? value)
    {
        if (!_syncingSelection) SelectedFindingId = value?.Id;
        OnPropertyChanged(nameof(IsSelectedKeyEvent)); OnPropertyChanged(nameof(KeyEventButtonText)); OnPropertyChanged(nameof(KeyEventGlyph));
        LoadEditor(value);
        ApproveCommand.NotifyCanExecuteChanged(); RejectCommand.NotifyCanExecuteChanged();   // the editor is always on screen: the buttons follow the selection
        HistoryLines.Clear(); EvidenceMessage = ""; HistoryMessage = ""; _detailEvidence?.Close();
        // stay on the tab the examiner is reading: Evidence and History follow the new selection
        if (value == null) SelectedDetailTab = TabDetails;
        else OnSelectedDetailTabChanged(SelectedDetailTab);
    }

    private void LoadEditor(FindingRowViewModel? row)
    {
        _loadingEditor = true;
        try
        {
            var f = row?.Finding;
            DraftTitle = f?.EffectiveTitle ?? ""; DraftDescription = f?.EffectiveDescription ?? "";
            DraftSeverity = f != null && SpectreFinding.Rank(f.EffectiveSeverity) >= 0 ? SpectreFinding.Severities[SpectreFinding.Rank(f.EffectiveSeverity)] : "Info";
            DraftMitre = FindingDraft.MitreText(f?.EffectiveMitreAttack ?? Array.Empty<string>());
            DraftNotes = f?.Notes ?? "";
            ReasonText = ""; EditMessage = "";
        }
        finally { _loadingEditor = false; }
        LoadRowLines(row);
        EditorChanged();
        foreach (var n in new[] { nameof(EngineTitle), nameof(EngineDescription), nameof(EngineSeverity), nameof(EngineMitre), nameof(EngineMitreDisplay) }) OnPropertyChanged(n);
        OnPropertyChanged(nameof(IsSelectedKeyEvent)); OnPropertyChanged(nameof(KeyEventButtonText)); OnPropertyChanged(nameof(KeyEventGlyph));
    }

    private void EditorChanged()
    {
        if (_loadingEditor) return;
        foreach (var n in new[] { nameof(IsDirty), nameof(CanSave), nameof(NeedsReason), nameof(TitleDiffers), nameof(DescriptionDiffers), nameof(SeverityDiffers),
                                  nameof(MitreDiffers), nameof(NotesDiffers), nameof(MitreInvalid), nameof(SelectedTactic) }) OnPropertyChanged(n);
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnDraftTitleChanged(string value) => EditorChanged();
    partial void OnDraftDescriptionChanged(string value) => EditorChanged();
    partial void OnDraftSeverityChanged(string value) => EditorChanged();
    partial void OnDraftMitreChanged(string value) => EditorChanged();
    partial void OnDraftNotesChanged(string value) => EditorChanged();
    partial void OnReasonTextChanged(string value) => EditorChanged();
    partial void OnIsSavingChanged(bool value) { EditorChanged(); ApproveCommand.NotifyCanExecuteChanged(); RejectCommand.NotifyCanExecuteChanged(); }

    /// <summary>Puts one field back to the engine's value in the editor (Save then reverts the override).</summary>
    [RelayCommand]
    private void RevertField(string field)
    {
        if (Selected is not { } row) return;
        var f = row.Finding;
        switch (field)
        {
            case FindingDraft.FieldTitle: DraftTitle = f.Title; break;
            case FindingDraft.FieldDescription: DraftDescription = f.Description; break;
            case FindingDraft.FieldSeverity: DraftSeverity = SpectreFinding.Rank(f.Severity) >= 0 ? SpectreFinding.Severities[SpectreFinding.Rank(f.Severity)] : "Info"; break;
            case FindingDraft.FieldMitre: DraftMitre = FindingDraft.MitreText(f.MitreAttack); break;
            case FindingDraft.FieldNotes: DraftNotes = ""; break;
        }
    }

    [RelayCommand]
    private void DiscardEdits() => LoadEditor(Selected);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        var rowsSaved = RowsDirty && SaveRowEdits(row);   // the Analyze rows first; the field changes follow
        var changes = CurrentChanges();
        if (changes.IsEmpty)
        {
            if (RowsDirty) { if (rowsSaved && ReferenceEquals(Selected, row)) LoadEditor(row); EditMessage = rowsSaved ? "Saved." : "Could not save."; }
            return Task.CompletedTask;
        }
        return SaveChangesAsync(row, changes, ReasonText.Trim());
    }

    /// <summary>
    /// Applies <paramref name="changes"/> to the row's item in the store: each field goes through <see cref="AnalysisEdits"/> (history, edited fields), so
    /// later syncs and imports keep it. Used by Save and by accepting a suggestion. Nothing is sent to the engine.
    /// </summary>
    private Task SaveChangesAsync(FindingRowViewModel row, SpectreFindingChanges changes, string reason)
    {
        if (changes.IsEmpty || row.Item == null) return Task.CompletedTask;
        IsSaving = true; EditMessage = "Saving…";
        try
        {
            var id = row.Item.Id;
            var j = changes.ToJson();
            var saved = Mutate(a =>
            {
                if (a.Find(id) is not { } i) return false;
                foreach (var (field, v) in j)
                {
                    if (v == null) continue;   // a revert has no source value to go back to
                    switch (field)
                    {
                        case FindingDraft.FieldTitle: AnalysisEdits.SetTitle(i, v.ToString()); break;
                        case FindingDraft.FieldDescription: AnalysisEdits.SetDescription(i, v.ToString()); break;
                        case FindingDraft.FieldSeverity: AnalysisEdits.SetSeverity(i, v.ToString()); break;
                        case FindingDraft.FieldNotes: AnalysisEdits.SetNotes(i, v.ToString()); break;
                        case FindingDraft.FieldMitre:
                            AnalysisEdits.SetMitre(i, v is JsonArray arr ? arr.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0) : FindingDraft.ParseMitre(v.ToString())); break;
                    }
                }
                if (!string.IsNullOrWhiteSpace(reason)) i.Log("reason: " + reason.Trim(), DateTimeOffset.UtcNow);
                return true;
            });
            if (saved && ReferenceEquals(Selected, row)) LoadEditor(row);
            EditMessage = saved ? "Saved." : "Could not save.";
        }
        finally { IsSaving = false; }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ approve / reject

    private bool CanDecide() => Selected != null && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private Task ApproveAsync() => DecideAsync(SpectreFinding.Approved);

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private Task RejectAsync() => DecideAsync(SpectreFinding.Rejected);

    /// <summary>Overridable by tests: "Remove it from key event {title} too?" with Remove / Cancel. True = Remove.</summary>
    public Func<string, string, bool> ConfirmRemoveFromKeyEvent { get; set; } = (title, message) => Dialogs.Confirm(title, message, "Remove", "Cancel");

    private Task DecideAsync(string decision)
    {
        if (Selected is not { } row || row.Item == null) return Task.CompletedTask;
        bool reject = decision != SpectreFinding.Approved;
        // rejecting a finding that sits in a key event takes it out of that key event too; Cancel changes nothing
        var inKey = reject && Analysis is { } an ? KeyEventOps.KeyEventOf(an, row.Item.Id) : null;
        if (inKey != null && !ConfirmRemoveFromKeyEvent("Reject " + row.Title, $"Remove it from key event {KeyEventOps.Effective(Analysis!, inKey).Title} too?"))
            return Task.CompletedTask;
        var verb = reject ? "Reject" : "Approve";
        var rationale = Ask($"{verb} {row.Id}", $"Why are you {(reject ? "rejecting" : "approving")} this finding? The rationale is recorded with your name ({Examiner}).");
        if (string.IsNullOrWhiteSpace(rationale)) return Task.CompletedTask;
        SetReview(row, reject ? ReviewState.Rejected : ReviewState.Confirmed, rationale, inKey?.Id);
        EditMessage = decision == SpectreFinding.Approved ? "Approved." : "Rejected.";
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ suggestions from the chat

    /// <summary>
    /// Adds a change Spectre proposes for a finding as a yellow bar on it; nothing reaches the engine until the examiner accepts.
    /// Safe to call from any thread. False when the finding is not in the loaded list. A newer suggestion replaces an older one.
    /// </summary>
    public bool AddSuggestion(string findingId, SpectreFindingChanges changes, string why)
    {
        if (changes.IsEmpty) return false;
        return OnUi(() => AddSuggestionCore(findingId, changes, why));
    }

    internal bool AddSuggestionCore(string findingId, SpectreFindingChanges changes, string why)
    {
        var row = FindRow(findingId);
        if (row == null) return false;
        row.PendingSuggestion = new FindingSuggestion(row.Finding.FindingId, changes, why ?? "");
        return true;
    }

    [RelayCommand]
    private async Task AcceptSuggestionAsync(FindingRowViewModel? row)
    {
        row ??= Selected;
        if (row?.PendingSuggestion is not { } s) return;
        await GuardAsync(async () =>
        {
            await SaveChangesAsync(row, s.Changes, $"Suggested by Spectre, accepted by {Examiner}");
            if (EditMessage == "Saved.") row.PendingSuggestion = null;
        }, "Could not apply the suggestion");
    }

    /// <summary>Puts the suggestion into the editor so the examiner can adjust it and save with their own reason.</summary>
    [RelayCommand]
    private void EditSuggestion(FindingRowViewModel? row)
    {
        row ??= Selected;
        if (row?.PendingSuggestion is not { } s) return;
        if (!ReferenceEquals(Selected, row)) Selected = row;
        var j = s.Changes.ToJson();
        _loadingEditor = true;
        try
        {
            foreach (var (field, v) in j)
            {
                if (v == null) { RevertField(field); continue; }
                switch (field)
                {
                    case FindingDraft.FieldTitle: DraftTitle = v.ToString(); break;
                    case FindingDraft.FieldDescription: DraftDescription = v.ToString(); break;
                    case FindingDraft.FieldSeverity: DraftSeverity = v.ToString(); break;
                    case FindingDraft.FieldMitre: DraftMitre = v is JsonArray a ? string.Join(", ", a.Select(x => x?.ToString())) : v.ToString(); break;
                    case FindingDraft.FieldNotes: DraftNotes = v.ToString(); break;
                }
            }
        }
        finally { _loadingEditor = false; }
        EditorChanged();
        row.PendingSuggestion = null;
        SelectedDetailTab = TabDetails;
        EditMessage = "Suggestion loaded. Adjust it, give a reason and save.";
    }

    [RelayCommand]
    private void DismissSuggestion(FindingRowViewModel? row)
    {
        row ??= Selected;
        if (row != null) row.PendingSuggestion = null;
    }

    // ------------------------------------------------------------ evidence + history tabs

    partial void OnSelectedDetailTabChanged(int value)
    {
        if (Selected == null) return;
        if (value == TabEvidence && !DetailEvidence.IsOpen && !IsLoadingEvidence) _ = GuardAsync(LoadEvidenceAsync, "Could not load the evidence");
        else if (value == TabHistory && HistoryLines.Count == 0 && !IsLoadingHistory) _ = GuardAsync(LoadHistoryAsync, "Could not load the history");
    }

    /// <summary>Opens the selected finding's rows in the tab's evidence view; a message when the finding names no evidence file.</summary>
    private async Task LoadEvidenceAsync()
    {
        if (Selected is not { } row) return;
        IsLoadingEvidence = true; EvidenceMessage = "";
        try
        {
            var opened = await DetailEvidence.OpenAsync(row.Title, row.Finding.FindingId, PanelFindings(new[] { row.Finding.FindingId }, row));
            if (!ReferenceEquals(Selected, row)) return;
            if (!opened) EvidenceMessage = "This finding names no evidence file that can be opened.";
        }
        finally { IsLoadingEvidence = false; }
    }

    /// <summary>The item's own history (who, when, what): edits, review decisions, promotions, imports.</summary>
    private Task LoadHistoryAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        HistoryLines.Clear();
        var history = row.Item?.History ?? new List<HistoryEntry>();
        foreach (var h in history.OrderBy(h => h.When)) HistoryLines.Add($"{h.When.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC · {h.Who} · {h.What}");
        HistoryMessage = history.Count == 0 ? "No edits or decisions yet." : "";
        return Task.CompletedTask;
    }

    internal static string HistoryText(SpectreHistoryRow h)
    {
        var what = h.Params is JsonObject o
            ? string.Join(", ", new[] { "reason", "rationale", "decision" }.Select(k => o[k] is JsonValue v ? $"{k}: {v}" : null).Where(x => x != null))
            : "";
        return $"{h.Ts.Replace('T', ' ').TrimEnd('Z')} · {h.Action}" + (h.Examiner != null ? $" · {h.Examiner}" : "") + (what.Length > 0 ? " · " + what : "");
    }
}
