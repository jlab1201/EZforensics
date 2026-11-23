using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>
/// The Findings tabs: Overview (summary and review tiles, severity and tactic tiles and the key event cards), Timeline (the
/// <see cref="Walkthrough"/> slides on a timeline) and Details (the list of findings with the editor beside it).
/// </summary>
/// <summary>The Findings page tabs.</summary>
public enum FindingsTab { Overview, Story, Details }

/// <summary>What the Story tab shows: the timeline with its slides, or the key events as an attack flow graph.</summary>
public enum StoryView { Timeline, AttackFlow }

public sealed partial class FindingsViewModel
{
    public const string RerunTitle = "Re-run Spectre to put this case on a timeline";
    public const string RerunText = "You get dates on the key events and plain names for what was found. Your edits are kept.";
    public const string StaleText = "Analysed with an older Spectre. Re-run to get dates and plain names.";

    /// <summary>The page's tab; Overview is the default. The second tab (named Timeline, value <see cref="FindingsTab.Story"/>) shows the walk-through slides (see <see cref="Walkthrough"/>).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowOverview), nameof(ShowStory), nameof(ShowDetails), nameof(ShowAllFindings), nameof(SelectedTabIndex))]
    private FindingsTab _selectedTab = FindingsTab.Overview;

    /// <summary>Story tab switch: Timeline (default) or Attack flow.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTimelineView), nameof(ShowAttackFlowView), nameof(IsTimelineViewSelected), nameof(IsAttackFlowViewSelected))]
    private StoryView _selectedStoryView = StoryView.Timeline;
    public bool ShowTimelineView => SelectedStoryView == StoryView.Timeline;
    public bool ShowAttackFlowView => SelectedStoryView == StoryView.AttackFlow;
    public bool IsTimelineViewSelected { get => ShowTimelineView; set { if (value) SelectedStoryView = StoryView.Timeline; } }
    public bool IsAttackFlowViewSelected { get => ShowAttackFlowView; set { if (value) SelectedStoryView = StoryView.AttackFlow; } }

    /// <summary>The key events (the page's computer filter applied) as attack flow nodes.</summary>
    public IReadOnlyList<EzForensics.App.Controls.FlowNode> AttackFlowNodes => AllStoryMoments.Where(c => c.Moment.Time != null).Select(c =>
    {
        var m = c.Moment;
        var phase = string.IsNullOrWhiteSpace(m.Phase) ? PlainLanguage.OtherActivity : m.Phase;
        return new EzForensics.App.Controls.FlowNode(m.Id, m.Number, string.IsNullOrWhiteSpace(m.PlainTitle) ? m.Headline : m.PlainTitle, m.Time, phase,
            StoryText.Severity(m, _findingsById), StoryGraphics.Icon(phase, StoryGraphics.MomentTechniques(m, _findingsById)), CardHost(c),
            MomentDetailLines.Build(m, _findingsById, c.Approval));
    }).ToList();

    /// <summary>Cards in the attack flow name their computer when the page shows all computers and there are several.</summary>
    public bool ShowFlowHost => HostFilter == null && _hostTiles.Count > 1;

    /// <summary>Attack flow: click selects the key event.</summary>
    public void SelectFlowNode(string id) => SelectMomentCore(id);

    /// <summary>Attack flow: double click opens the key event's evidence in Details.</summary>
    public void OpenFlowNode(string id) { if (SelectMomentCore(id)) ShowMomentInDetails(SelectedMoment); }

    /// <summary>The Present button works when there are findings or key events to walk through.</summary>
    public bool CanPresent => HasAnalysis || HasMoments;

    /// <summary>Present: opens the walk-through slides at slide 1.</summary>
    [RelayCommand(CanExecute = nameof(CanPresent))]
    private void Present() => Walkthrough.Open();

    private WalkthroughViewModel? _walkthrough;
    public WalkthroughViewModel Walkthrough => _walkthrough ??= new WalkthroughViewModel(this);

    /// <summary>The tab strip's index (Overview 0, Timeline 1, Details 2).</summary>
    public int SelectedTabIndex
    {
        // tab order on the page: Overview, Details, Story
        get => SelectedTab switch { FindingsTab.Details => 1, FindingsTab.Story => 2, _ => 0 };
        set { if (value is >= 0 and <= 2) SelectedTab = value switch { 1 => FindingsTab.Details, 2 => FindingsTab.Story, _ => FindingsTab.Overview }; }
    }
    public bool ShowOverview => SelectedTab == FindingsTab.Overview;
    /// <summary>The Story tab is shown (setting it true switches to it; false leaves it for Overview).</summary>
    public bool ShowStory { get => SelectedTab == FindingsTab.Story; set { if (value) SelectedTab = FindingsTab.Story; else if (ShowStory) SelectedTab = FindingsTab.Overview; } }
    /// <summary>The Details tab is shown (setting it true switches to it; false leaves it for Overview).</summary>
    public bool ShowDetails { get => SelectedTab == FindingsTab.Details; set { if (value) SelectedTab = FindingsTab.Details; else if (ShowDetails) SelectedTab = FindingsTab.Overview; } }
    /// <summary>Older name of <see cref="ShowDetails"/> (the list of every finding lives in Details).</summary>
    public bool ShowAllFindings { get => ShowDetails; set => ShowDetails = value; }

    /// <summary>The key event cards in time order (undated last, after a "No date" divider), for the chosen computer.</summary>
    public IReadOnlyList<MomentCardViewModel> StoryMoments
    {
        get
        {
            var list = Moments.OrderBy(m => m.Number).Where(c => HostFilter == null || Same(CardHost(c), HostFilter)).ToList();
            bool dividerDone = false;
            foreach (var c in list)
            {
                bool show = !dividerDone && c.Moment.Time == null;
                if (show) dividerDone = true;
                c.ShowNoDateDivider = show;
            }
            return list;
        }
    }

    /// <summary>Every key event of the chosen computer in number (time) order (the slides use this).</summary>
    public IReadOnlyList<MomentCardViewModel> AllStoryMoments => Moments.OrderBy(m => m.Number)
        .Where(c => HostFilter == null || Same(CardHost(c), HostFilter)).Where(c => !ConfirmedOnly || c.IsConfirmed).ToList();

    /// <summary>The Story tab's "Confirmed only" switch (both views): only key events whose finding you approved.</summary>
    [ObservableProperty] private bool _confirmedOnly;

    /// <summary>Confirmed only is on and no key event is confirmed yet (the views show a short caption).</summary>
    public bool ShowNoConfirmed => ConfirmedOnly && !AllStoryMoments.Any();

    partial void OnConfirmedOnlyChanged(bool value)
    {
        foreach (var n in new[] { nameof(HostTimeline), nameof(AttackFlowNodes), nameof(ShowNoConfirmed) }) OnPropertyChanged(n);
        _walkthrough?.Refresh();
    }

    // ------------------------------------------------------------ story graphics (stage strip, computers)

    /// <summary>The computer the page is about (the header selector); null is "All computers". It scopes the tiles, the key events, the timeline slides and Details.</summary>
    [ObservableProperty] private string? _hostFilter;

    private IReadOnlyList<StageChipViewModel> _stageStrip = Array.Empty<StageChipViewModel>();
    /// <summary>The stage journey strip: one pictogram per stage with evidence (plus neighbours), in ATT&amp;CK reference order.</summary>
    public IReadOnlyList<StageChipViewModel> StageStrip => _stageStrip;
    public bool ShowStageStrip => HasAnalysis && _stageStrip.Any(c => c.HasEvidence);

    private IReadOnlyList<HostTileViewModel> _hostTiles = Array.Empty<HostTileViewModel>();
    /// <summary>The computers of the case, most severe first.</summary>
    public IReadOnlyList<HostTileViewModel> HostTiles => _hostTiles;
    public bool ShowHostTiles => HasAnalysis && _hostTiles.Count > 0;
    private string _graphicsKey = "";

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);


    private IReadOnlyDictionary<string, SpectreTimelineFinding> _findingsById = new Dictionary<string, SpectreTimelineFinding>();

    /// <summary>The plain stage of a card (normalised to the strip's names).</summary>
    internal static string CardStage(MomentCardViewModel c) => StoryGraphics.StageOf(c.Phase);
    /// <summary>The computer of a card ("Unknown computer" when no finding names one).</summary>
    internal string CardHost(MomentCardViewModel c) => StoryGraphics.MomentHost(c.Moment, _findingsById, _storyHosts);

    /// <summary>What the case knows about its evidence systems (read off the UI thread when the timeline loads); null until then or without a case.</summary>
    private CaseHostInfo? _hostInfo;
    private StoryHosts _storyHosts = StoryHosts.None;

    /// <summary>Where a card stands in the review: its main finding's line of the Review status tile.</summary>
    internal ReviewBucket CardBucket(MomentCardViewModel c)
    {
        if (c.PrimaryFindingId != null && FindRow(c.PrimaryFindingId) is { } row) return BucketOf(row);
        return string.Equals(c.Approval, SpectreFinding.Approved, StringComparison.OrdinalIgnoreCase) ? ReviewBucket.Confirmed
            : string.Equals(c.Approval, SpectreFinding.Rejected, StringComparison.OrdinalIgnoreCase) ? ReviewBucket.Rejected : ReviewBucket.ToReview;
    }

    /// <summary>True when a finding behind the card is at this severity (the card's own severity word counts when it lists none).</summary>
    internal bool CardHasSeverity(MomentCardViewModel c, string severity)
    {
        int rank = SpectreFinding.Rank(severity);
        var ranks = StoryGraphics.MomentSeverityRanks(c.Moment, _findingsById);
        return ranks.Count > 0 ? ranks.Contains(rank) : string.Equals(c.Severity, severity, StringComparison.OrdinalIgnoreCase);
    }

    private void SyncStageStrip()
    {
        foreach (var c in _stageStrip) c.IsActive = false;
    }

    /// <summary>Rebuilds the strip (and the computers map) only when the counts changed, so they light up once, not on every Confirm.</summary>
    private void RefreshGraphics()
    {
        var all = Timeline.Findings.Concat(Timeline.Undated).ToList();
        _findingsById = StoryGraphics.ById(all);
        _storyHosts = _hostInfo != null ? StoryHosts.Resolve(all, _hostInfo) : StoryHosts.None;
        var stages = StoryGraphics.Stages(all, Timeline.Moments);
        var strip = HasAnalysis ? StoryGraphics.Strip(stages) : Array.Empty<StageStripItem>();
        var hosts = HasAnalysis ? StoryGraphics.Hosts(all, Timeline.Moments, _storyHosts) : Array.Empty<HostSummary>();
        var key = string.Join("|", strip.Select(i => i.IsGap ? "~" : $"{i.Stage!.Stage}:{i.Stage.Findings}:{i.Stage.Moments}:{i.Stage.MaxSeverityRank}"))
                  + "#" + string.Join("|", hosts.Select(h => $"{h.Host}:{h.Findings}:{h.Moments}:{string.Join(",", h.BySeverity)}:{h.AliasLine}"));
        if (key == _graphicsKey) return;
        _graphicsKey = key;
        _hostTiles = hosts.Select(h => new HostTileViewModel(h)).ToList();
        _hostTiles = _hostTiles.OrderBy(t => t.Host == StoryGraphics.NoHost).ThenBy(t => t.Host, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var n in new[] { nameof(HostTiles), nameof(ShowHostTiles), nameof(HostChoices), nameof(ShowHostPicker), nameof(ShowHostLabel), nameof(ShowHostBar), nameof(SingleHostName) })
            OnPropertyChanged(n);
        if (HostFilter != null && !_hostTiles.Any(t => Same(t.Host, HostFilter))) HostFilter = null;
        else OnHostFilterChanged(HostFilter);
        _stageStrip = StageChipViewModel.ForStrip(strip);
        OnPropertyChanged(nameof(StageStrip));
        OnPropertyChanged(nameof(ShowStageStrip));
    }

    public SpectreTimelineSummary Summary => Timeline.Summary;

    public string HeadlineText
    {
        get
        {
            if (!HasAnalysis) return "";
            // the engine counts record names as computers; with the case's evidence systems known, count those instead
            if (_storyHosts.UsesCase && _storyHosts.NamedComputerCount != Summary.Hosts)
                return PlainLanguage.SummarySentence(Summary.Moments > 0 ? Summary.Moments : Moments.Count, Summary.FirstSeen, Summary.LastSeen,
                    _storyHosts.NamedComputerCount, Math.Max(Summary.Undated, Timeline.Undated.Count));
            if (Summary.Sentence.Length > 0) return Summary.Sentence;
            return PlainLanguage.SummarySentence(Moments.Count, Summary.FirstSeen, Summary.LastSeen, Summary.Hosts, Math.Max(Summary.Undated, Timeline.Undated.Count));
        }
    }

    public string KeyMomentsTile => Moments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string FirstSignTile => StoryText.Tile(Summary.FirstSeen);
    public string LastSignTile => StoryText.Tile(Summary.LastSeen);
    /// <summary>Findings still waiting for a decision: counted from the loaded list (so a Confirm shows at once), else the engine's count.</summary>
    public int NeedReviewCount => _all.Count > 0 ? PendingApprovals : Summary.NeedReview;

    // ------------------------------------------------------------ overview card (severity ring, review progress)

    /// <summary>Findings by severity rank (Info .. Critical) for the chosen computer: the loaded list (edits count), else the timeline's findings.</summary>
    public IReadOnlyList<int> SeverityCounts
    {
        get
        {
            var counts = new int[SpectreFinding.Severities.Count];
            var severities = _all.Count > 0 ? ScopedRows.Where(r => r.IsActive).Select(r => r.Severity) : ScopedTimelineFindings.Select(f => f.Severity);
            foreach (var s in severities) counts[Math.Max(0, SpectreFinding.Rank(s))]++;
            return counts;
        }
    }
    public int SeverityTotal => SeverityCounts.Sum();
    /// <summary>"1 Critical", "4 High", ... most severe first, only those found (the words beside the ring's colours).</summary>
    public IReadOnlyList<SeverityDot> SeverityLegend =>
        SeverityCounts.Select((n, i) => new SeverityDot(SpectreFinding.Severities[i], n, false)).Where(d => d.Count > 0).Reverse().ToList();
    public string SeverityRingName => SeverityTotal == 0 ? "No findings"
        : $"{StageChipViewModel.Plural(SeverityTotal, "finding")}: " + string.Join(", ", SeverityLegend.Select(d => d.Text));

    /// <summary>Findings with a decision (approved or rejected) out of <see cref="ReviewTotal"/>, for the chosen computer: the review ring.</summary>
    public int ReviewedCount => Math.Max(0, ReviewTotal - ScopedNeedReview);
    public int ReviewTotal => HostFilter == null ? AllFindingsTotal : _all.Count > 0 ? ScopedKeyRows.Count() : ScopedTimelineFindings.Count();
    private int AllFindingsTotal => _all.Count > 0 ? _all.Count(IsActiveKeyRow) : FindingCount;
    private static bool IsActiveKeyRow(FindingRowViewModel r) => r.Item != null && r.IsActive;
    private IEnumerable<FindingRowViewModel> ScopedKeyRows => ScopedRows.Where(IsActiveKeyRow);
    private int ScopedNeedReview => HostFilter == null ? NeedReviewCount
        : _all.Count > 0 ? ScopedKeyRows.Count(r => r.Finding.Approval == null) : ScopedTimelineFindings.Count(f => f.Approval == null);
    public string ReviewText => $"{ReviewedCount} of {ReviewTotal} reviewed";
    /// <summary>Every finding of the case has a decision: the tile says the report is ready.</summary>
    public bool AllReviewed => HasAnalysis && AllFindingsTotal > 0 && NeedReviewCount == 0;
    public const string AllReviewedText = "All reviewed. Ready for the report.";


    private bool HasDatedStory => Timeline.Moments.Any(m => m.Time != null) || Timeline.Findings.Any(f => f.Start != null);
    public bool ShowStoryTimeline => HasAnalysis && HasDatedStory;
    public bool ShowStoryNoDates => HasAnalysis && HasEngineResults && !IsLoadingTimeline && TimelineError.Length == 0 && !HasDatedStory && (Timeline.Findings.Count + Timeline.Undated.Count) > 0;
    /// <summary>The big "Re-run Spectre" card: an old analysis, or one where nothing has a date.</summary>
    public bool ShowRerunCard => ShowStaleBanner || ShowStoryNoDates;
    public bool ShowStaleBanner => HasAnalysis && Timeline.AnalysisStale && !IsAnalysing;
    public string StaleDetail => Timeline.StaleReason;

    /// <summary>The moment list is being rebuilt (the slides wait for the end).</summary>
    internal bool IsRebuildingMoments { get; private set; }

    partial void OnSelectedTabChanged(FindingsTab value)
    {
        if (value == FindingsTab.Story) Walkthrough.Enter();   // the slides live in the Timeline tab
        else _walkthrough?.Leave();
    }

    private HashSet<string> _datedIds = new();

    /// <summary>Tells each row whether the timeline dates it (the Details list labels its time UTC or local clock).</summary>
    private void RefreshDatedRows()
    {
        _datedIds = Timeline.Findings.Where(f => f.Start != null).SelectMany(f => new[] { f.Id, f.FindingId }).Where(i => i.Length > 0).ToHashSet();
        foreach (var r in _all) r.SetDated(_datedIds.Contains(r.Finding.Id) || _datedIds.Contains(r.Finding.FindingId));
    }

    private void StoryChanged()
    {
        if (IsRebuildingMoments) return;
        foreach (var c in Moments)
            c.Approval = c.PrimaryFindingId != null && FindRow(c.PrimaryFindingId) is { } row ? row.Finding.Approval : c.Approval;
        foreach (var n in new[] { nameof(StoryMoments), nameof(Summary), nameof(HeadlineText), nameof(KeyMomentsTile), nameof(FirstSignTile), nameof(LastSignTile),
                                  nameof(NeedReviewCount), nameof(ShowStoryTimeline), nameof(ShowStoryNoDates),
                                  nameof(ShowStaleBanner), nameof(StaleDetail), nameof(ShowRerunCard),
                                  nameof(SeverityCounts), nameof(SeverityTotal), nameof(SeverityLegend), nameof(SeverityRingName), nameof(Radar), nameof(RadarUnmappedText), nameof(HostTimeline), nameof(AttackFlowNodes), nameof(ShowFlowHost), nameof(ShowNoConfirmed),
                                  nameof(ReviewedCount), nameof(ReviewTotal), nameof(ReviewText), nameof(AllReviewed), nameof(ReviewBreakdown), nameof(TechniqueSuggestions) })
            OnPropertyChanged(n);
        RefreshGraphics();
        if (KeyEventsOnly || BucketFilter != null) ApplyFilter(); else RefreshRowMeta();
        OnPropertyChanged(nameof(HeadlineText));
        RefreshDatedRows();
        _walkthrough?.Refresh();
    }

    /// <summary>Wraps a rebuild of <see cref="Moments"/> so the story and the slides update once, at the end.</summary>
    private void RebuildMoments(Action rebuild)
    {
        IsRebuildingMoments = true;
        try { rebuild(); }
        finally { IsRebuildingMoments = false; }
        StoryChanged();
    }

    // ------------------------------------------------------------ card actions

    /// <summary>A click on a story card selects it (the editor stays closed until Edit).</summary>
    [RelayCommand]
    private void SelectCard(MomentCardViewModel? card)
    {
        if (card != null) SelectedMoment = card;
    }

    /// <summary>Show the evidence: opens the rows behind the moment in the evidence panel on this page; without an evidence file, the editor's Evidence tab.</summary>
    [RelayCommand]
    private Task ShowMomentEvidenceAsync(MomentCardViewModel? card) => GuardAsync(async () =>
    {
        if (PrimaryRow(card) is not { } row) { ShowMessage("This moment has no finding with evidence to open.", InfoSeverity.Warning); return; }
        if (!ReferenceEquals(Selected, row)) Selected = row;
        var card2 = card ?? SelectedMoment;
        if (await OpenEvidencePanelAsync(row, card2, card2?.PlainTitle is { Length: > 0 } t ? t : row.Title)) return;
        SelectedTab = FindingsTab.Details;
        SelectedDetailTab = TabEvidence;
    }, "Could not open the evidence");

    /// <summary>Peek: shows (or hides) the first evidence rows inline on the card; read once, off the UI thread.</summary>
    [RelayCommand]
    private async Task TogglePeekAsync(MomentCardViewModel? card)
    {
        if (card == null) return;
        card.IsPeekOpen = !card.IsPeekOpen;
        if (!card.IsPeekOpen || card.PeekLoaded || card.IsPeekLoading) return;
        card.IsPeekLoading = true;
        card.PeekText = "Reading the evidence…";
        try
        {
            var row = card.PrimaryFindingId != null ? FindRow(card.PrimaryFindingId) : null;
            var sources = row != null ? MomentFindings(card, row) : card.Moment.FindingIds
                .Select(id => Timeline.Findings.Concat(Timeline.Undated).FirstOrDefault(f => f.FindingId == id || f.Id == id))
                .Where(f => f != null).Select(f => EvidenceSource.From(f!)).ToList();
            var plan = await PlanEvidenceAsync(sources);
            if (plan.Count == 0) { card.PeekLoaded = true; card.PeekText = "No evidence file to show for this moment."; return; }
            var peek = await Task.Run(() => EvidencePeek.ReadAsync(plan[0]));
            card.SetPeek(peek);
        }
        catch (Exception ex)
        {
            card.PeekText = "Could not read the evidence: " + ErrorText(ex);
        }
        finally { card.IsPeekLoading = false; }
    }

    /// <summary>The findings behind a moment (it may fold several): each one's engine finding, else its timeline row; the primary row when the moment lists none.</summary>
    private IReadOnlyList<EvidenceSource> MomentFindings(MomentCardViewModel? card, FindingRowViewModel primary)
    {
        var list = new List<EvidenceSource>();
        foreach (var id in card?.Moment.FindingIds ?? Array.Empty<string>())
        {
            if (FindRow(id) is { } r) list.Add(EvidenceSource.From(r.Finding));
            else if (Timeline.Findings.Concat(Timeline.Undated).FirstOrDefault(f => f.FindingId == id || f.Id == id) is { } tf) list.Add(EvidenceSource.From(tf));
        }
        if (list.Count == 0) list.Add(EvidenceSource.From(primary.Finding));
        return list;
    }

    /// <summary>How the page opens the records on Analyze; tests replace it (the default is <see cref="AppServices.ShowEvidenceInResults"/>).</summary>
    internal Action<string, IReadOnlyList<EvidenceTarget>, string>? EvidenceOpener { get; set; }

    private void OpenEvidence(string caseDir, IReadOnlyList<EvidenceTarget> plan, string title)
    {
        if (EvidenceOpener != null) EvidenceOpener(caseDir, plan, title);
        else S.ShowEvidenceInResults(caseDir, plan, title);
    }

    /// <summary>The case CSVs and rows behind these findings (see <see cref="FindingsEvidence.Plan"/>); empty when none can be opened.</summary>
    private async Task<IReadOnlyList<EvidenceTarget>> PlanEvidenceAsync(IReadOnlyList<EvidenceSource> sources)
    {
        if (CaseDir is not { } dir) return Array.Empty<EvidenceTarget>();
        return await Task.Run(() => FindingsEvidence.Plan(dir, sources));
    }

    /// <summary>Errors on this page are one plain sentence; the raw text goes to the log only.</summary>
    protected override string ErrorText(Exception ex) => SpectreErrors.Friendly(ex);
}
