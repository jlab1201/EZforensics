using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Threading;

namespace EzForensics.App.ViewModels.Findings;

public enum SlideKind { Overview, Moment, Summary }

/// <summary>One slide: the overview, one key event, or the closing summary.</summary>
public sealed record WalkthroughSlide(SlideKind Kind, MomentCardViewModel? Card = null);

/// <summary>One severity in the slides' summary line: "2 High".</summary>
public sealed record SeveritySummaryItem(string Severity, int Count, string Text);

/// <summary>A computer name in the slides' summary line; the current slide's computer is marked.</summary>
public sealed record ComputerNameItem(string Name, bool IsCurrent, string Suffix = "");

/// <summary>A progress dot under the slides.</summary>
public sealed partial class SlideDot : ObservableObject
{
    public SlideDot(int index, string label) { Index = index; Label = label; }
    public int Index { get; }
    public string Label { get; }
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// The slides of the Findings page's Story tab: an overview, one slide per key event in time order, then a summary of what is
/// confirmed and what is still open. Confirm, Not important, Show the evidence and Ask Spectre go through the page's own commands, so the
/// cards, the counts and the engine stay in step. Selecting a moment anywhere (chat, J/K, a card) moves the open slides to it.
/// </summary>
public sealed partial class WalkthroughViewModel : ObservableObject
{
    private readonly FindingsViewModel _page;

    public WalkthroughViewModel(FindingsViewModel page) { _page = page; }

    /// <summary>The page, for the overview's headline and tiles.</summary>
    public FindingsViewModel Page => _page;

    public IReadOnlyList<WalkthroughSlide> Slides { get; private set; } = Array.Empty<WalkthroughSlide>();
    public ObservableCollection<SlideDot> Dots { get; } = new();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _index;

    public WalkthroughSlide? Current => Index >= 0 && Index < Slides.Count ? Slides[Index] : null;
    public MomentCardViewModel? Card => Current?.Card;
    public bool IsOverview => Current?.Kind == SlideKind.Overview;
    public bool IsMoment => Current?.Kind == SlideKind.Moment;
    public bool IsSummary => Current?.Kind == SlideKind.Summary;
    /// <summary>The moment the timeline centres on; null on the overview and the summary (the whole timeline).</summary>
    public string? FocusMomentId => Card?.Id;
    public bool CanBack => Index > 0;
    public bool CanNext => Index < Slides.Count - 1;
    public string ProgressText => Slides.Count == 0 ? "" : $"Slide {Index + 1} of {Slides.Count}";

    private IEnumerable<MomentCardViewModel> Cards => Slides.Where(s => s.Card != null).Select(s => s.Card!);
    public int MomentCount => Cards.Count();
    public int ConfirmedCount => Cards.Count(c => c.IsConfirmed);
    public int OpenCount => Cards.Count(c => c.NeedsReview);

    /// <summary>"Key event 2 of 5".</summary>
    public string MomentHeader => Card == null ? "" : $"Key event {Card.Number} of {MomentCount}";

    public string OverviewHint => MomentCount == 0 && _page.ConfirmedOnly ? NoConfirmedText : MomentCount == 0
        ? "Spectre picked no key events for this case. Details lists everything it found."
        : $"Next shows the {(MomentCount == 1 ? "key event" : $"{MomentCount} key events")} one at a time, in the order they were recorded (UTC). The order is not a claim about cause.";

    public const string NoConfirmedText = "No confirmed key events yet.";

    public string ConfirmedText => MomentCount == 0 ? "There are no key events to review."
        : $"{ConfirmedCount} of {MomentCount} key event{(MomentCount == 1 ? "" : "s")} confirmed by you.";
    public string OpenText => OpenCount == 0 ? "Nothing is waiting for your review."
        : $"{OpenCount} still need{(OpenCount == 1 ? "s" : "")} your review.";
    public string UndatedText
    {
        get
        {
            var n = Math.Max(_page.Timeline.Summary.Undated, _page.Timeline.Undated.Count);
            return n == 0 ? "" : $"{n} finding{(n == 1 ? " has" : "s have")} no time, so {(n == 1 ? "it is" : "they are")} not in these slides. They are under Details.";
        }
    }

    // ------------------------------------------------------------ open / move

    /// <summary>Opens the Story tab at the overview (slide 0).</summary>
    public void Open()
    {
        if (_page.SelectedTab != FindingsTab.Story) _page.SelectedTab = FindingsTab.Story;   // the tab change calls Enter()
        else Enter();
        Index = 0;
    }

    /// <summary>The Story tab was selected: builds the slides and shows the one the examiner was on.</summary>
    internal void Enter()
    {
        Rebuild();
        IsOpen = true;
        Changed();
    }

    /// <summary>The Story tab was left: the slides stop (autoplay pauses).</summary>
    internal void Leave() => IsOpen = false;

    [RelayCommand]
    private void Start() => Open();

    /// <summary>Leaves the Story tab for Overview.</summary>
    [RelayCommand]
    public void Close()
    {
        if (_page.SelectedTab == FindingsTab.Story) _page.SelectedTab = FindingsTab.Overview;
        else IsOpen = false;
    }

    [RelayCommand]
    public void Next() { if (CanNext) Index++; }

    [RelayCommand]
    public void Back() { if (CanBack) Index--; }

    [RelayCommand]
    private void GoTo(SlideDot? dot) { if (dot != null) Index = Math.Clamp(dot.Index, 0, Math.Max(0, Slides.Count - 1)); }

    /// <summary>Slides keys (Timeline view only): Left/Right, Page Up/Down, Up/Down, Home/End move; true when the key was used.</summary>
    public bool HandleKey(System.Windows.Input.Key key)
    {
        if (!IsOpen || !_page.ShowTimelineView) return false;
        switch (key)
        {
            case System.Windows.Input.Key.Right or System.Windows.Input.Key.PageDown or System.Windows.Input.Key.Down: PauseAutoplay(); Next(); return true;
            case System.Windows.Input.Key.Left or System.Windows.Input.Key.PageUp or System.Windows.Input.Key.Up: PauseAutoplay(); Back(); return true;
            case System.Windows.Input.Key.Home: PauseAutoplay(); First(); return true;
            case System.Windows.Input.Key.End: PauseAutoplay(); Last(); return true;
            default: return false;
        }
    }

    public void First() => Index = 0;
    public void Last() => Index = Math.Max(0, Slides.Count - 1);

    /// <summary>Moves to a moment's slide (the chat selected it); false when it has no slide.</summary>
    public bool GoToMoment(string momentId)
    {
        for (int i = 0; i < Slides.Count; i++)
            if (Slides[i].Card is { } c && c.Id == momentId)
            {
                if (Index != i) Index = i;
                return true;
            }
        return false;
    }

    partial void OnIndexChanged(int value)
    {
        Changed();
        if (Card is { } card && !ReferenceEquals(_page.SelectedMoment, card)) _page.SelectedMoment = card;
    }

    partial void OnIsOpenChanged(bool value) => Changed();

    /// <summary>The page's moments or their review state changed: rebuild the slides when the moments did, keeping the slide shown.</summary>
    internal void Refresh()
    {
        if (!IsOpen) return;
        Rebuild();
        Changed();
    }

    private void Rebuild()
    {
        var ordered = _page.AllStoryMoments;
        if (Slides.Count == ordered.Count + 2 && Slides.Skip(1).Take(ordered.Count).Select(s => s.Card).SequenceEqual(ordered)) return;
        var keepId = Card?.Id;
        var wasSummary = IsSummary;
        var oldIndex = Index;
        var list = new List<WalkthroughSlide> { new(SlideKind.Overview) };
        list.AddRange(ordered.Select(c => new WalkthroughSlide(SlideKind.Moment, c)));
        list.Add(new WalkthroughSlide(SlideKind.Summary));
        Slides = list;
        Dots.Clear();
        for (int i = 0; i < list.Count; i++)
            Dots.Add(new SlideDot(i, list[i].Kind switch { SlideKind.Overview => "Overview", SlideKind.Summary => "Summary", _ => $"Event {list[i].Card!.Number}" }));
        int next = keepId != null ? list.FindIndex(s => s.Card?.Id == keepId) : -1;
        if (next < 0) next = wasSummary ? list.Count - 1 : Math.Clamp(oldIndex, 0, list.Count - 1);
        if (Index != next) Index = next; else Changed();
    }

    /// <summary>One line for the slide: the moment's reason (Spectre's reading when there is one).</summary>
    public string SlideLine => Card is { } c ? OneLine(c.WhyText) : "";

    private static string OneLine(string s)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        var i = s.IndexOf(". ", StringComparison.Ordinal);
        return i > 0 ? s[..(i + 1)] : s;
    }

    /// <summary>Severity counts as "2 High", most severe first, zero counts left out (the same counts as the hero ring).</summary>
    public IReadOnlyList<SeveritySummaryItem> SeveritySummary =>
        _page.SeverityLegend.Where(d => d.Count > 0).Select(d => new SeveritySummaryItem(d.Severity, d.Count, d.Text)).ToList();

    /// <summary>The computers of the case; on a moment slide the moment's computer is marked current.</summary>
    public IReadOnlyList<ComputerNameItem> Computers
    {
        get
        {
            var host = IsOpen && IsMoment && Card != null ? _page.CardHost(Card) : null;
            var tiles = _page.HostTiles;
            return tiles.Select((t, i) => new ComputerNameItem(t.Host, host != null && string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase), i < tiles.Count - 1 ? ", " : "")).ToList();
        }
    }
    public string ComputerLabel => _page.HostTiles.Count == 1 ? "Host" : "Hosts";
    public bool HasComputers => _page.HostTiles.Count > 0;
    public bool HasSeverityAndComputers => SeveritySummary.Count > 0 && HasComputers;
    public bool HasSummaryLine => Computers.Count > 0;

    // ------------------------------------------------------------ autoplay

    public const int AutoplaySeconds = 6;
    private DispatcherTimer? _autoTimer;

    /// <summary>The slides move on by themselves every few seconds; any click or key pauses it, Space toggles it.</summary>
    [ObservableProperty] private bool _isAutoplay;
    public string AutoplayText => IsAutoplay ? "Pause" : "Autoplay";

    partial void OnIsAutoplayChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoplayText));
        if (value && IsOpen)
        {
            if (!CanNext) Index = 0;
            _autoTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(AutoplaySeconds), DispatcherPriority.Background, (_, _) => AutoStep(), Dispatcher.CurrentDispatcher);
            _autoTimer.Start();
        }
        else _autoTimer?.Stop();
    }

    /// <summary>One autoplay step: the next slide, stopping at the end.</summary>
    internal void AutoStep()
    {
        if (!IsOpen || !CanNext) { IsAutoplay = false; return; }
        Index++;
        if (_autoTimer != null) { _autoTimer.Stop(); _autoTimer.Start(); }
        if (!CanNext) IsAutoplay = false;
    }

    [RelayCommand]
    public void ToggleAutoplay() => IsAutoplay = !IsAutoplay;

    /// <summary>The examiner did something on the slides: autoplay stops.</summary>
    public void PauseAutoplay() { if (IsAutoplay) IsAutoplay = false; }

    private void Changed()
    {
        if (!IsOpen) PauseAutoplay();
        for (int i = 0; i < Dots.Count; i++) Dots[i].IsCurrent = i == Index;
        foreach (var n in new[] { nameof(Slides), nameof(Current), nameof(Card), nameof(IsOverview), nameof(IsMoment), nameof(IsSummary), nameof(FocusMomentId),
                                  nameof(CanBack), nameof(CanNext), nameof(ProgressText), nameof(MomentCount), nameof(ConfirmedCount), nameof(OpenCount),
                                  nameof(MomentHeader), nameof(OverviewHint), nameof(ConfirmedText), nameof(OpenText), nameof(UndatedText),
                                  nameof(SlideLine), nameof(SeveritySummary), nameof(Computers), nameof(ComputerLabel), nameof(HasSummaryLine), nameof(HasComputers), nameof(HasSeverityAndComputers) })
            OnPropertyChanged(n);
    }

    // ------------------------------------------------------------ actions (the page's own paths)

    [RelayCommand]
    private Task ConfirmAsync() => Card is { } c ? _page.ConfirmMomentCommand.ExecuteAsync(c) : Task.CompletedTask;

    [RelayCommand]
    private Task NotImportantAsync() => Card is { } c ? _page.UnpinMomentCommand.ExecuteAsync(c) : Task.CompletedTask;

    [RelayCommand]
    private Task ShowEvidenceAsync() => Card is { } c ? _page.ShowMomentEvidenceCommand.ExecuteAsync(c) : Task.CompletedTask;

    [RelayCommand]
    private void Ask() { if (Card is { } c) _page.AskMomentCommand.Execute(c); }

    /// <summary>Goes back to Overview and opens the report flyout.</summary>
    [RelayCommand]
    private void MakeReport()
    {
        Close();
        _page.IsReportOpen = true;
    }
}
