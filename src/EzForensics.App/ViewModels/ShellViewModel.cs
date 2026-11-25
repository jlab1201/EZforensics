using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels.Explore;
using EzForensics.App.ViewModels.Findings;
using EzForensics.App.ViewModels.Learn;
using EzForensics.Core.Execution;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    public NavItem(string title, string glyph, PageViewModel page, string? description = null) { Title = title; Glyph = glyph; Page = page; Description = description; }
    /// <summary>Tooltip text when the title alone is not enough (null = the title).</summary>
    public string? Description { get; }
    public string Title { get; }
    public string Glyph { get; }
    public PageViewModel Page { get; }
    /// <summary>Stable page key (saved as <c>AppSettings.LastPage</c>).</summary>
    public string Key => Page.GetType().Name;
    /// <summary>Red dot on the icon (e.g. new results are ready on Analyze).</summary>
    [ObservableProperty] private bool _hasAttention;
    /// <summary>Spinning arc on the icon while jobs run (Ingest).</summary>
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private string? _badgeTip;
    /// <summary>Tooltip: the title plus the badge's meaning when one is shown.</summary>
    public string Tip => string.IsNullOrEmpty(BadgeTip) ? Description ?? Title : $"{Title}: {BadgeTip}";
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppServices _s;

    [ObservableProperty] private NavItem? _selected;
    /// <summary>Selection of the main list only (null while a bottom-zone page is current).</summary>
    [ObservableProperty] private NavItem? _selectedMain;
    [ObservableProperty] private bool _isFooterPageActive;
    [ObservableProperty] private bool _isAiActive;
    [ObservableProperty] private bool _isToolsActive;
    [ObservableProperty] private bool _isOptionsActive;

    [ObservableProperty] private string _themeGlyph = "";
    [ObservableProperty] private string _themeTip = "";
    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private int _runningJobs;
    [ObservableProperty] private int _queuedJobs;
    [ObservableProperty] private string _toolsRoot = "";
    [ObservableProperty] private string _net9Text = ".NET 9 and 10: checking…";
    [ObservableProperty] private string _toolCountText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNavCompact), nameof(NavWidth), nameof(NavColumnWidth), nameof(IsNavOverlay))]
    private bool _navPinned;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNavCompact), nameof(NavWidth), nameof(IsNavOverlay))]
    private bool _navExpanded;

    public const double NavFullWidth = 200;
    public const double NavRailWidth = 48;

    /// <summary>Icon-only rail (unpinned and not hovered/opened).</summary>
    public bool IsNavCompact => !NavPinned && !NavExpanded;
    /// <summary>Unpinned panel currently expanded over the page.</summary>
    public bool IsNavOverlay => !NavPinned && NavExpanded;
    public double NavWidth => IsNavCompact ? NavRailWidth : NavFullWidth;
    /// <summary>Layout space reserved for the nav; the overlay never reflows the page.</summary>
    public System.Windows.GridLength NavColumnWidth => new(IsFocusMode ? 0 : NavPinned ? NavFullWidth : NavRailWidth);

    /// <summary>Analyze focus mode: the navigation is hidden (its pinned/expanded state is kept and comes back on exit).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NavColumnWidth))]
    private bool _isFocusMode;

    /// <summary>Current UI zoom for the status bar ("110%").</summary>
    [ObservableProperty] private string _zoomText = "100%";

    /// <summary>Opens Ingest (the status bar's running / queued counts, Spectre's "jobs" page).</summary>
    [RelayCommand]
    private void ShowJobs() => Selected = _jobsItem;

    [RelayCommand] private void ZoomIn() => Appearance.ZoomIn();
    [RelayCommand] private void ZoomOut() => Appearance.ZoomOut();
    [RelayCommand] private void ZoomReset() => Appearance.ZoomReset();

    [RelayCommand]
    private void ToggleNavExpanded()
    {
        if (!NavPinned) NavExpanded = !NavExpanded;
    }

    /// <summary>The edge handle: pin the drawer open (from the rail or the hover overlay) or collapse it back to the rail.</summary>
    [RelayCommand]
    private void ToggleNavPinned() => NavPinned = !NavPinned;

    // navigation state from before focus mode, restored on exit (null = not in focus mode)
    private bool? _navExpandedBeforeFocus;
    private bool _navPinnedBeforeFocus;

    private void OnFocusModeChanged(bool on)
    {
        if (on)
        {
            _navExpandedBeforeFocus = NavExpanded;
            _navPinnedBeforeFocus = NavPinned;
            NavExpanded = false;
            IsFocusMode = true;
            return;
        }
        IsFocusMode = false;
        if (_navExpandedBeforeFocus is { } expanded)
        {
            if (NavPinned != _navPinnedBeforeFocus) NavPinned = _navPinnedBeforeFocus;
            NavExpanded = !NavPinned && expanded;   // OnNavPinnedChanged resets it, so set it last
        }
        _navExpandedBeforeFocus = null;
    }

    partial void OnCurrentPageChanged(PageViewModel? value) => Assistant?.OnPageChanged();

    partial void OnNavPinnedChanged(bool value)
    {
        NavExpanded = false;
        if (_s.Settings.NavPinned == value) return;
        _s.Settings.NavPinned = value;
        try { _s.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
    }

    public ShellViewModel(AppServices s)
    {
        _s = s;
        _navPinned = s.Settings.NavPinned;
        Triage = new TriageViewModel();
        Tools = new ToolsViewModel();
        Results = new ResultsViewModel();
        Explore = new ExploreViewModel();
        Findings = new FindingsViewModel(() => CaseDir);
        Learn = new LearnViewModel();
        Settings = new SettingsViewModel();
        Assistant = new AssistantChatViewModel(s.Settings, new ApiKeyStore(), s.CreateHttpClient, () => CaseDir, () => CurrentPage,
            allPages: () => new PageViewModel[] { Triage, Results, Explore, Findings, Learn, Tools, Settings }, localAi: s.LocalAi);
        Findings.AskSpectreHandler = Assistant.Ask;

        // Top: Ingest, Analyze, Explore, Review. Bottom zone: AI (the Learn page), Tools, Options.
        Items = new ObservableCollection<NavItem>
        {
            new(PageNames.Ingest, "", Triage),
            new("Analyze", "", Results),
            new(PageNames.Explore, "", Explore),
            new(PageNames.Findings, "", Findings),
            new(PageNames.Learn, "", Learn),
            new("Tools", "", Tools),
            new("Options", "", Settings),
        };
        MainItems = new ObservableCollection<NavItem>(Items.Take(4));
        FooterItems = new ObservableCollection<NavItem>(Items.Skip(4));
        _jobsItem = Items.First(i => i.Page == Triage);
        AiItem = Items.First(i => i.Page == Learn);
        ToolsItem = Items.First(i => i.Page == Tools);
        OptionsItem = Items.First(i => i.Page == Settings);
        _analyzeItem = Items.First(i => i.Page == Results);

        s.ResultsReady += () => MarkResultsReady();
        s.FocusModeChanged += OnFocusModeChanged;
        s.Settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.Theme)) UpdateThemeGlyph(); };
        UpdateThemeGlyph();
        Triage.ManageSelectionsRequested += () =>
        {
            Selected = Items.FirstOrDefault(i => i.Page == Settings) ?? Selected;
            Settings.FocusIngestSelections();
        };
        s.OptionsAiRequested += () => { Selected = OptionsItem; Settings.FocusAi(); };
        s.NavigationRequested += t => Selected = Items.FirstOrDefault(i => i.Page.GetType() == t) ?? Selected;
        s.JobRequested += j =>
        {
            Selected = _jobsItem;
            Triage.ShowJob(j);
        };
        s.JobsRequested += ShowJobs;
        s.LearnInboxRequested += () => Learn.SelectedTab = LearnTab.Inbox;
        s.CaseOpenRequested += dir => _ = Results.OpenCaseFolderAsync(dir);
        s.OpenCsvRequested += (caseDir, csv) =>
        {
            var open = Results.ShowCsvAsync(caseDir, csv);
            Selected = _analyzeItem;
            _ = open;
        };
        s.OpenRecordRequested += (caseDir, csv, record) =>
        {
            var open = Results.ShowRecordAsync(caseDir, csv, record);
            Selected = _analyzeItem;
            _ = open;
        };
        s.ShowEvidenceRequested += (caseDir, targets, title) =>
        {
            var open = Results.ShowEvidenceAsync(caseDir, targets, title);
            Selected = _analyzeItem;
            _ = open;
        };
        s.ToolsRescanned += UpdateToolsInfo;
        s.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ToolsRoot)) ToolsRoot = s.Settings.ToolsRoot;
            else if (e.PropertyName == nameof(AppSettings.UiZoom)) ZoomText = UiZoom.Format(s.Settings.UiZoom);
        };
        ZoomText = UiZoom.Format(s.Settings.UiZoom);
        s.Queue.Jobs.CollectionChanged += OnJobsChanged;
        ToolsRoot = s.Settings.ToolsRoot;
        s.Settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.HotKeys)) NotifyBackChanged(); };
        // open on Analyze when an earlier ingestion exists, otherwise on Ingest; the setup wizard is decided elsewhere
        Selected = HasPreviousIngestion(s.Settings.CaseRoot) ? _analyzeItem : Items.FirstOrDefault(i => i.Page == Triage) ?? Items[0];
        _restored = true;
    }

    /// <summary>True when the case folder already holds a case (any sub-folder not starting with an underscore).</summary>
    private static bool HasPreviousIngestion(string? caseRoot)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(caseRoot) && Directory.Exists(caseRoot)
                && new DirectoryInfo(caseRoot).EnumerateDirectories().Any(d => !d.Name.StartsWith("_"));
        }
        catch { return false; }
    }

    /// <summary>Forgets the Back history (the first page of a session has nothing to go back to).</summary>
    public void ClearBackHistory()
    {
        _back.Clear();
        NotifyBackChanged();
    }

    // ---- navigation history (Back)
    private const int BackLimit = 30;
    private readonly List<NavItem> _back = new();
    private NavItem? _current;
    private bool _goingBack;
    private bool _restored;

    public bool CanGoBack => _back.Count > 0;

    /// <summary>"Back to Ingest (Alt+Left)", or just "Back" when there is no history.</summary>
    public string BackTip
    {
        get
        {
            var g = HotKeys.Resolve(_s.Settings.HotKeys).GetValueOrDefault(HotKeys.Back, "");
            if (_back.Count == 0) return "Back";
            return g.Length > 0 ? $"Back to {_back[^1].Title} ({g})" : $"Back to {_back[^1].Title}";
        }
    }

    private void NotifyBackChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(BackTip));
        GoBackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_back.Count == 0) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _goingBack = true;
        try { Selected = target; }
        finally { _goingBack = false; NotifyBackChanged(); }
    }

    private void RecordNavigation(NavItem? previous, NavItem next)
    {
        if (_goingBack || previous == null || previous == next) return;
        if (_back.Count == 0 || _back[^1] != previous) _back.Add(previous);
        if (_back.Count > BackLimit) _back.RemoveAt(0);
        NotifyBackChanged();
    }

    public TriageViewModel Triage { get; }
    public ToolsViewModel Tools { get; }
    public ResultsViewModel Results { get; }
    public ExploreViewModel Explore { get; }
    public FindingsViewModel Findings { get; }
    public LearnViewModel Learn { get; }
    /// <summary>Spectre, the app-wide assistant: one conversation that follows the page on screen.</summary>
    public AssistantChatViewModel Assistant { get; }

    /// <summary>The case Spectre works on: the one Explore shows while on Explore, otherwise the one open in Analyze, then the last ingested.</summary>
    private string? CaseDir => (CurrentPage == Explore ? Explore.CaseDir : null) ?? Results.CaseFolder ?? Explore.CaseDir ?? _s.LastCaseDir;
    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> Items { get; }
    public ObservableCollection<NavItem> MainItems { get; }
    public ObservableCollection<NavItem> FooterItems { get; }
    private readonly NavItem _jobsItem;
    public NavItem AiItem { get; }
    public NavItem ToolsItem { get; }
    public NavItem OptionsItem { get; }
    private readonly NavItem _analyzeItem;

    partial void OnSelectedMainChanged(NavItem? value)
    {
        if (value != null && value != Selected) Selected = value;
    }

    private void MarkResultsReady()
    {
        if (Selected == _analyzeItem) return;
        _analyzeItem.HasAttention = true;
        _analyzeItem.BadgeTip = "New results are ready";
    }

    private void UpdateThemeGlyph()
    {
        bool dark = _s.Settings.Theme == AppTheme.Dark;
        ThemeGlyph = dark ? "\uE706" : "\uE708"; // sun while dark (switch to light), moon while light
        ThemeTip = "Switch theme";
    }

    /// <summary>Flips light/dark, applies it, keeps the Options page in sync and saves.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        var next = _s.Settings.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _s.Settings.Theme = next;
        ThemeManager.Apply(next);
        if (Settings.Edit != null && Settings.Edit.Theme != next) Settings.Edit.Theme = next;
        try { _s.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
        UpdateThemeGlyph();
    }

    [RelayCommand] private void OpenFooterPage(NavItem? item) { if (item != null) Selected = item; }

    public bool IsAdmin => Elevation.IsAdministrator;
    public string WindowTitle => IsAdmin ? "EZ Forensics (Administrator)" : "EZ Forensics";

    public Task InitializeAsync() => _init ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            await _s.RescanAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Initial rescan failed", ex);
        }
        try
        {
            // Status bar only (no banner): the setup installs a private .NET 9 by default, and the net4 builds work without it.
            // A tool that exists only as a modern (.NET 10) build is simply not installed without .NET 9 (its job fails with "Tool not installed").
            var has = await _s.ProbeNet9Async();
            Net9Text = has ? ".NET 9 and 10: installed" : ".NET 9 and 10: not installed (using compatible builds)";
            Net9Tip = has ? "The .NET 10 builds of the EZ tools can run."
                : "Without .NET 9 and 10 the EZ tools run as their net4 builds, which need only the .NET Framework that ships with Windows. " +
                  "A few tools that exist only as .NET 10 builds need .NET 9 and 10: Run setup installs a private copy (no admin rights).";
        }
        catch (Exception ex)
        {
            Net9Text = ".NET 9 and 10: unknown";
            AppLog.Error("Runtime probe failed", ex);
        }
    }

    private Task? _init;

    /// <summary>Tooltip of the status bar's .NET 9 text.</summary>
    [ObservableProperty] private string _net9Tip = "";

    /// <summary>Routes an unhandled exception to the current page's banner.</summary>
    public void ReportUnhandled(Exception ex)
    {
        if (CurrentPage != null) CurrentPage.ShowError("Unexpected error", ex);
    }

    partial void OnSelectedChanged(NavItem? value)
    {
        if (value == null) return;
        RecordNavigation(_current, value);
        _current = value;
        if (_restored && _s.Settings.LastPage != value.Key)
        {
            _s.Settings.LastPage = value.Key;
            try { _s.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
        }
        if (value != _analyzeItem) _s.SetFocusMode(false);
        NavExpanded = false;
        SelectedMain = MainItems.Contains(value) ? value : null;
        IsFooterPageActive = FooterItems.Contains(value);
        IsAiActive = value == AiItem;
        IsToolsActive = value == ToolsItem;
        IsOptionsActive = value == OptionsItem;
        if (value == _analyzeItem) { value.HasAttention = false; value.BadgeTip = null; }
        CurrentPage = value.Page;
        try { value.Page.OnNavigatedTo(); }
        catch (Exception ex) { value.Page.ShowError("Could not open page", ex); }
    }

    private void UpdateToolsInfo()
    {
        int n = _s.Catalog.Count(c => _s.IsInstalled(c.Exe));
        ToolCountText = $"{n}/{_s.Catalog.Count} tools installed";
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Job j in e.NewItems) j.PropertyChanged += OnJobPropertyChanged;
        RecountJobs();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Job.State)) return;
        RecountJobs();
        if (sender is Job { State: JobState.Succeeded, Hidden: false }) MarkResultsReady();
    }

    private void RecountJobs()
    {
        RunningJobs = _s.Queue.Jobs.Count(j => !j.Hidden && j.State == JobState.Running);
        QueuedJobs = _s.Queue.Jobs.Count(j => !j.Hidden && j.State == JobState.Queued);
        _jobsItem.IsBusy = RunningJobs > 0;
        _jobsItem.BadgeTip = RunningJobs > 0 ? $"{RunningJobs} running, {QueuedJobs} queued" : null;
    }
}
