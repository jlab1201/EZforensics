using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>The timeline graphic and the Key events rail: load, select, step through (J/K), confirm, pin / unpin, Spectre's read.</summary>
public sealed partial class FindingsViewModel
{
    public static IReadOnlyList<TimelineLaneMode> LaneModes { get; } = Enum.GetValues<TimelineLaneMode>();

    [ObservableProperty] private SpectreTimeline _timeline = SpectreTimeline.Empty;
    [ObservableProperty] private bool _isLoadingTimeline;
    [ObservableProperty] private string _timelineError = "";
    [ObservableProperty] private TimelineLaneMode _laneMode = TimelineLaneMode.Tactic;
    /// <summary>The window the timeline shows (two-way with the control: zoom and pan write it back).</summary>
    [ObservableProperty] private TimeRange? _timelineView;
    /// <summary>The selected finding's id, shared by the timeline, the moment cards, the list and the editor.</summary>
    [ObservableProperty] private string? _selectedFindingId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedMomentId))] private MomentCardViewModel? _selectedMoment;

    public ObservableCollection<MomentCardViewModel> Moments { get; } = new();
    public string? SelectedMomentId => SelectedMoment?.Id;
    public bool HasMoments => Moments.Count > 0;
    public bool ShowNoMoments => !IsLoadingTimeline && HasAnalysis && Moments.Count == 0;
    public int FindingCount => _all.Count > 0 ? _all.Count : Timeline.Findings.Count + Timeline.Undated.Count;
    public string UndatedText => Timeline.Undated.Count == 0 ? "" : $"{Timeline.Undated.Count} without a time";

    /// <summary>Raised when E (or a card's Edit) asks for the editor: the view focuses the title box.</summary>
    public event Action? EditRequested;

    private bool _syncingSelection;

    private void ResetMoments()
    {
        Moments.Clear(); SelectedMoment = null; SelectedFindingId = null;
        Timeline = SpectreTimeline.Empty; TimelineView = null; TimelineError = "";
    }

    partial void OnIsLoadingTimelineChanged(bool value) { OnPropertyChanged(nameof(ShowNoMoments)); OnPropertyChanged(nameof(ShowStoryNoDates)); OnPropertyChanged(nameof(ShowRerunCard)); }
    partial void OnTimelineErrorChanged(string value) { OnPropertyChanged(nameof(ShowStoryNoDates)); OnPropertyChanged(nameof(ShowRerunCard)); }
    partial void OnTimelineChanged(SpectreTimeline value) { OnPropertyChanged(nameof(UndatedText)); OnPropertyChanged(nameof(FindingCount)); StatusChanged(); }

    [RelayCommand]
    private Task RefreshTimelineAsync() => GuardAsync(LoadTimelineAsync, "Could not load the timeline");

    /// <summary>Rebuilds the timeline and the moment cards from the case analysis store, keeping the selected moment and the zoom when they still fit.</summary>
    internal async Task LoadTimelineAsync()
    {
        IsLoadingTimeline = true; TimelineError = "";
        try
        {
            await LoadHostInfoAsync();
            if (_store == null && CaseDir is { } dir) OpenStore(dir); else RebuildFromStore();
        }
        finally { IsLoadingTimeline = false; }
    }

    /// <summary>Reads the case's evidence systems (disk work, off the UI thread) so the story can place findings on computers.</summary>
    private async Task LoadHostInfoAsync()
    {
        var dir = CaseDir;
        try { _hostInfo = dir == null ? null : await Task.Run(() => CaseHostInfo.Load(dir)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("Case hosts: " + ex.Message);
            _hostInfo = null;
        }
    }

    /// <summary>The engine has no timeline tool (older Spectre) or it failed: say so plainly; the findings list still works.</summary>
    internal static string TimelineErrorText(Exception ex) => "Could not load the timeline. " + SpectreErrors.Friendly(ex);

    [RelayCommand]
    private void FitTimeline() => TimelineView = FindingsTimelineLayout.FitRange(Timeline);

    // ------------------------------------------------------------ selection (timeline, cards, list stay in step)

    partial void OnSelectedFindingIdChanged(string? value)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            if (value != null && (Selected == null || !Matches(Selected, value)) && FindRow(value) is { } row)
            {
                if (!Rows.Contains(row)) ClearFilters();
                Selected = row;
            }
            // a finding that is a moment's primary finding selects that moment's card too
            var card = value == null ? null : Moments.FirstOrDefault(m => m.PrimaryFindingId != null && IdsMatch(m.PrimaryFindingId, value));
            if (card != null || (SelectedMoment != null && (value == null || !IdsMatch(SelectedMoment.PrimaryFindingId, value)))) SelectedMoment = card;
        }
        finally { _syncingSelection = false; }
        MarkSelectedMoment();
    }

    partial void OnSelectedMomentChanged(MomentCardViewModel? value)
    {
        MarkSelectedMoment();
        if (value == null) return;
        if (Walkthrough.IsOpen) Walkthrough.GoToMoment(value.Id);   // the chat (findings_select_moment) and J/K move the open slides too
        if (!_syncingSelection && value.PrimaryFindingId != null)
        {
            _syncingSelection = true;
            try
            {
                if (FindRow(value.PrimaryFindingId) is { } row) { if (!Rows.Contains(row)) ClearFilters(); Selected = row; }
                SelectedFindingId = Selected?.Id ?? value.PrimaryFindingId;
            }
            finally { _syncingSelection = false; }
        }
        if (!value.ReadRequested) _ = LoadReadAsync(value, refresh: false);
    }

    private void MarkSelectedMoment()
    {
        foreach (var m in Moments) m.IsSelected = ReferenceEquals(m, SelectedMoment);
    }

    private bool IdsMatch(string? a, string? b)
    {
        if (a == null || b == null) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var ra = FindRow(a);
        return ra != null && ReferenceEquals(ra, FindRow(b));
    }

    private static bool Matches(FindingRowViewModel row, string id) =>
        string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(row.Finding.FindingId, id, StringComparison.OrdinalIgnoreCase);

    /// <summary>J: the next key event (the first when none is selected).</summary>
    [RelayCommand]
    public void NextMoment() => StepMoment(1);

    /// <summary>K: the previous key event.</summary>
    [RelayCommand]
    public void PreviousMoment() => StepMoment(-1);

    private void StepMoment(int dir)
    {
        if (Moments.Count == 0) return;
        int i = SelectedMoment == null ? -1 : Moments.IndexOf(SelectedMoment);
        i = i < 0 ? (dir > 0 ? 0 : Moments.Count - 1) : Math.Clamp(i + dir, 0, Moments.Count - 1);
        SelectedMoment = Moments[i];
    }

    /// <summary>Selects a moment by its id or its number (time order, 1 = earliest; for the chat). False when there is no such moment.</summary>
    public bool SelectMoment(string idOrNumber) => OnUi(() => SelectMomentCore(idOrNumber));

    internal bool SelectMomentCore(string idOrNumber)
    {
        var key = idOrNumber.Trim().TrimStart('#');
        var card = Moments.FirstOrDefault(m => string.Equals(m.Id, key, StringComparison.OrdinalIgnoreCase))
                   ?? (int.TryParse(key, out var n) ? Moments.FirstOrDefault(m => m.Number == n) : null);
        if (card == null) return false;
        SelectedMoment = card;
        return true;
    }

    /// <summary>E: the editor for the selected finding.</summary>
    public void RequestEdit()
    {
        if (Selected == null) return;
        DetailListTab = ListTabFindings;
        SelectedDetailTab = TabDetails;
        SelectedTab = FindingsTab.Details;
        EditRequested?.Invoke();
    }

    // ------------------------------------------------------------ card actions

    private FindingRowViewModel? PrimaryRow(MomentCardViewModel? card)
    {
        card ??= SelectedMoment;
        if (card == null) return null;
        if (!ReferenceEquals(SelectedMoment, card)) SelectedMoment = card;
        return card.PrimaryFindingId != null ? FindRow(card.PrimaryFindingId) : null;
    }

    /// <summary>Confirm: approves the moment's primary finding (with a rationale, like Approve).</summary>
    [RelayCommand]
    private async Task ConfirmMomentAsync(MomentCardViewModel? card)
    {
        if (PrimaryRow(card) is not { } row) { ShowMessage("This moment has no finding to approve.", InfoSeverity.Warning); return; }
        if (!ReferenceEquals(Selected, row)) Selected = row;
        var wasPending = card?.NeedsReview ?? false;
        await DecideAsync(SpectreFinding.Approved);
        var shown = card ?? SelectedMoment;
        if (wasPending && shown is { IsReviewed: true }) shown.PlayStamp("Confirmed");
    }

    /// <summary>How long a card shows its stamp before the key events are picked again (0 with animations off; tests set it).</summary>
    internal static Func<int> StampHoldMs { get; set; } = () => EzForensics.App.Controls.Motion.Enabled ? 650 : 0;

    [RelayCommand]
    private void EditMoment(MomentCardViewModel? card)
    {
        if (PrimaryRow(card) is { } row && !ReferenceEquals(Selected, row)) Selected = row;
        RequestEdit();
    }

    /// <summary>Overview card: Show the evidence opens the finding's Evidence tab in Details (one evidence view on the page).</summary>
    [RelayCommand]
    private void ShowMomentInDetails(MomentCardViewModel? card)
    {
        if (card != null && !ReferenceEquals(SelectedMoment, card)) SelectedMoment = card;
        if (ShowKeyEventInDetails(KeyEventOfCard(card ?? SelectedMoment)?.Id)) return;
        if (PrimaryRow(card) is { } row) ShowFindingEvidence(row);
        else ShowMessage("This key event's finding is not in the list.", InfoSeverity.Warning);
    }

    /// <summary>Not significant: the key event leaves the timeline, the cards and the slides (its findings stay in the list; recorded in its history).</summary>
    [RelayCommand]
    private async Task UnpinMomentAsync(MomentCardViewModel? card)
    {
        var shown = card ?? SelectedMoment;
        if (KeyEventOfCard(shown) is not { } k) { ShowMessage("This moment has no key event to change.", InfoSeverity.Warning); return; }
        if (shown != null)
        {
            shown.PlayStamp("Not a key event");
            if (StampHoldMs() is > 0 and var ms) await Task.Delay(ms);   // the card shows its stamp before it leaves the list
        }
        DismissKeyEvent(k.Id);
    }

    /// <summary>Sets the review state of the row's item (Confirm, Reject, Not significant) and logs the rationale when there is one.</summary>
    private void SetReview(FindingRowViewModel row, ReviewState state, string? rationale = null, string? removeFromKeyEvent = null)
    {
        if (row.Item == null) return;
        var id = row.Item.Id;
        Mutate(a =>
        {
            if (a.Find(id) is not { } i) return false;
            AnalysisEdits.SetReview(i, state);
            if (removeFromKeyEvent != null) KeyEventOps.RemoveFinding(a, removeFromKeyEvent, id);
            if (!string.IsNullOrWhiteSpace(rationale)) i.Log("rationale: " + rationale.Trim(), DateTimeOffset.UtcNow);
            return true;
        });
    }

    /// <summary>Asks Spectre about a moment in the chat.</summary>
    [RelayCommand]
    private void AskMoment(MomentCardViewModel? card)
    {
        card ??= SelectedMoment;
        if (card == null) return;
        if (!ReferenceEquals(SelectedMoment, card)) SelectedMoment = card;
        AskSpectreHandler?.Invoke(AskMomentText(card));
    }

    internal static string AskMomentText(MomentCardViewModel card) =>
        $"Look at key event {card.Number} \"{card.Headline}\" ({card.TimeText}; call findings_timeline_view and findings_selected). " +
        "What does the evidence show at this point, how sure is it, and what should I check next?";

    [RelayCommand]
    private Task RefreshReadAsync(MomentCardViewModel? card)
    {
        if (card == null) return Task.CompletedTask;
        if (CaseDir is { } dir) _readConsentAsked.Remove(dir); // Refresh asks again after a "No"
        return LoadReadAsync(card, refresh: true);
    }

    /// <summary>Asks "Send case data?" (title, message); tests replace it.</summary>
    internal Func<string, string, bool> ConfirmCloud { get; set; } = (title, message) => Dialogs.Confirm(title, message, "Yes", "No", defaultNo: true);
    private readonly HashSet<string> _readConsentAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The engine's read goes to the cloud model of Options → AI: like the chat, ask once per case before any case data is sent
    /// (<see cref="Core.Assistant.AssistantConsent"/>). Local, no case or no saved key: nothing to ask.
    /// </summary>
    private bool ReadConsentOk()
    {
        var s = AppServices.Current?.Settings;
        var dir = CaseDir;
        if (s == null || s.AiProvider == "ollama" || string.IsNullOrEmpty(dir)) return true;
        var provider = s.AiProvider;
        if (Core.Assistant.AssistantConsent.Has(dir, provider, s.AiBaseUrl)) return true;
        if (Core.Assistant.AiProviders.NeedsKey(provider) && !new ApiKeyStore().Has(provider)) return true; // no key: the engine has no AI anyway
        if (!_readConsentAsked.Add(dir)) return false;
        var name = Core.Assistant.AiProviders.DisplayName(provider);
        if (!ConfirmCloud("Send case data?", $"Spectre's short read sends case data to {name}. Continue?")) return false;
        try { Core.Assistant.AssistantConsent.Grant(dir, provider, s.AiModel, s.AiBaseUrl); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Could not save the consent for this case: " + ex.Message); return false; }
        return true;
    }

    /// <summary>Spectre's short read of a moment (<c>spectre_moment_explain</c>), loaded the first time its card is opened.</summary>
    internal async Task LoadReadAsync(MomentCardViewModel card, bool refresh)
    {
        if (card.IsReadLoading) return;
        if (!ReadConsentOk())
        {
            card.ReadRequested = true;
            card.ReadOk = false;
            card.ReadText = "Not sent. Choose Local in Options → AI to keep case data on this PC, or press Refresh to be asked again.";
            card.ReadCites = "";
            return;
        }
        card.ReadRequested = true;
        var engineMoment = EngineMomentOf(card);
        if (engineMoment == null || !HasCaseId)
        {
            card.ReadOk = false; card.ReadText = "Spectre has no read for this key event."; card.ReadCites = "";
            return;
        }
        card.IsReadLoading = true;
        try
        {
            var r = await Client.MomentExplainAsync(CaseId!, engineMoment, refresh, CancellationToken.None);
            card.ReadOk = r.Text.Trim().Length > 0;
            card.ReadText = card.ReadOk ? r.Text.Trim() : "Spectre has no read for this moment yet.";
            card.ReadCites = r.Cites.Count > 0 ? "Cites " + string.Join(", ", r.Cites) : "";
        }
        catch (Exception ex) when (ex is SpectreToolException or IOException or TimeoutException)
        {
            card.ReadOk = false;
            card.ReadText = FriendlyReadError(ex.Message);
            card.ReadCites = "";
            AppLog.Warn("spectre_moment_explain: " + ex.Message);
        }
        finally { card.IsReadLoading = false; }
    }

    /// <summary>The read needs the engine's AI model; without one (or on any error) the card says so in plain words.</summary>
    internal static string FriendlyReadError(string message)
    {
        var m = message.ToLowerInvariant();
        if (new[] { "llm", "model", "provider", "ollama", "api key", "api_key", "not configured", "no ai" }.Any(m.Contains))
            return "Spectre's short read needs an AI model for the engine, and none is set up. The reason above is worked out without AI.";
        if (m.Contains("unknown tool") || m.Contains("not found"))
            return "This Spectre version cannot write a read for a moment yet. The reason above is worked out without AI.";
        return "Spectre's read is not available right now. The reason above is worked out without AI.";
    }
}
