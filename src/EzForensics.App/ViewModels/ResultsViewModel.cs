using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

public sealed partial class ResultsViewModel : PageViewModel
{
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _indexTimer;
    private CancellationTokenSource? _openCts;
    private CancellationTokenSource? _filterCts;
    private CsvIndex? _index;
    private RowFlagStore? _flags;
    private long _lastShownCount = -1;
    private DateTime _lastGrow = DateTime.MinValue;
    private bool _treeLoadedOnce;
    private string? _treeFolder;
    private bool _suspendApply;
    // tree stats columns
    private readonly FileStatsCache _statsCache = new();
    private readonly ConcurrentQueue<(int Gen, FileNode Node, FileStats? Stats)> _statsQueue = new();
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _caseJobTimer;
    private CancellationTokenSource? _statsCts;
    private int _statsGen;
    private int _statsDoneGen = -1;

    [ObservableProperty] private string? _caseFolder;
    [ObservableProperty] private FileNode? _selectedNode;
    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private VirtualRowList? _rows;
    [ObservableProperty] private IReadOnlyList<string> _headers = Array.Empty<string>();
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private double _indexProgress;
    [ObservableProperty] private bool _isFiltering;
    [ObservableProperty] private double _filterProgress;
    [ObservableProperty] private string _statusText = "Open a CSV file to view it.";
    [ObservableProperty] private string _globalText = "";
    [ObservableProperty] private bool _isLoadingTree;
    [ObservableProperty] private string? _sortColumn;
    [ObservableProperty] private bool _sortDescending;
    /// <summary>Flag column filter: null = all rows, true = flagged only, false = unflagged only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnlyFlagged))]
    private bool? _flagFilter;
    /// <summary>Filter panel checkbox: off keeps every filter but shows all rows.</summary>
    [ObservableProperty] private bool _filtersEnabled = true;
    [ObservableProperty] private string _filterDisplayText = "";
    [ObservableProperty] private int _flaggedCount;
    /// <summary>Flag filter dropdown button text ("All rows", "All flagged", "Red, Blue", "Unflagged").</summary>
    [ObservableProperty] private string _flagFilterText = "All rows";
    /// <summary>Tree stats (Earliest / Latest / Events) are being computed in the background.</summary>
    [ObservableProperty] private bool _isComputingStats;

    public ResultsViewModel()
    {
        SavedFilters.Instance.Changed += RefreshSavedFilterState;
        SavedViews.Instance.Changed += RefreshSavedViewState;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = ApplyFilterAsync(); };
        _indexTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _indexTimer.Tick += (_, _) => OnIndexTick();
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statsTimer.Tick += (_, _) => DrainStats();
        _caseJobTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _caseJobTimer.Tick += (_, _) => { _caseJobTimer.Stop(); OnCaseJobsSettled(); };
        Columns = new TreeColumnsViewModel(S.Settings.ResultsTreeColumns);
        RebuildFlagOptions();
        HotKeyMap = EzForensics.Core.Settings.HotKeys.Resolve(S.Settings.HotKeys);
        Details = new ComputerDetailsViewModel(ShowError, ShowMessage);
        Details.OpenCsvRequested += path =>
        {
            var open = OpenCsvAsync(path);
            if (!RevealFile(path)) _ = RefreshTreeAsync();
            _ = open;
        };
        S.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.DateFormat)) AppServices.OnUi(AggregateTree);
            else if (e.PropertyName == nameof(AppSettings.Flags)) AppServices.OnUi(RebuildFlagOptions);
            else if (e.PropertyName == nameof(AppSettings.FlagRowTint)) AppServices.OnUi(() => OnPropertyChanged(nameof(FlagRowTint)));
            else if (e.PropertyName == nameof(AppSettings.ShowAnalyzeHints)) AppServices.OnUi(NotifyHintsChanged);
            else if (e.PropertyName == nameof(AppSettings.HotKeys)) AppServices.OnUi(RefreshHotKeys);
        };
        HookSession();
        S.Queue.Jobs.CollectionChanged += OnQueueJobsChanged;
        foreach (var j in S.Queue.Jobs) j.PropertyChanged += OnQueueJobChanged;
    }

    public override string Title => "Analyze";
    public ObservableCollection<FileNode> Tree { get; } = new();
    /// <summary>Tree columns: visibility, widths, header sort (persisted as <see cref="AppSettings.ResultsTreeColumns"/>).</summary>
    public TreeColumnsViewModel Columns { get; }
    /// <summary>One filter cell per CSV column (header content: name, funnel, filter row).</summary>
    public ObservableCollection<ColumnFilterCell> Cells { get; } = new();
    /// <summary>Conditions that do not fit the filter row / funnel (stacked context-menu filters, filter builder groups). ANDed.</summary>
    public FilterGroup Extra { get; private set; } = new();
    public CsvIndex? Index => _index;
    public bool HasFile => Rows != null;
    /// <summary>Any filter is active: search text, column filters, filter builder or flag filter.</summary>
    public bool HasSearchText => !string.IsNullOrEmpty(GlobalText);
    public bool HasAnyFilter => FilterDisplayText.Length > 0 || !string.IsNullOrEmpty(GlobalText) || FlagFilter != null || _evidenceRows != null || _keyEventRows != null;
    public string FlaggedText => $"{FlaggedCount:N0} flagged";
    /// <summary>Flags (Settings order) with their counts in the open file (flag filter dropdown, Flag menu).</summary>
    public ObservableCollection<FlagOption> FlagOptions { get; } = new();
    /// <summary>Tint flagged rows lightly in their flag colour (Settings).</summary>
    public bool FlagRowTint => S.Settings.FlagRowTint;

    // Analyze hints (reliability banner + column markers): hidden by the permanent setting (Options > Display) or the session switch
    // (Actions > Hide hints, kept on AppServices, never saved). The advisory data is always computed; only visibility changes.
    public bool HintsVisible => EzForensics.Core.Advisories.AnalyzeHints.IsVisible(S.Settings.ShowAnalyzeHints, S.HideHintsThisSession);
    public bool HideHintsChecked => EzForensics.Core.Advisories.AnalyzeHints.MenuChecked(S.Settings.ShowAnalyzeHints, S.HideHintsThisSession);
    public bool HideHintsEnabled => EzForensics.Core.Advisories.AnalyzeHints.MenuEnabled(S.Settings.ShowAnalyzeHints);
    /// <summary>Actions > Hide hints (this session) toggled.</summary>
    public void SetHideHintsThisSession(bool hide) { S.HideHintsThisSession = hide; NotifyHintsChanged(); }
    private void NotifyHintsChanged()
    {
        OnPropertyChanged(nameof(HintsVisible)); OnPropertyChanged(nameof(HideHintsChecked)); OnPropertyChanged(nameof(HideHintsEnabled));
    }
    /// <summary>Flag of a click on the flag cell, Space and "Flag selected" (the last one picked): its label.</summary>
    public string LastFlagLabel => FlagLabel(EffectiveLastFlagId());
    /// <summary>Computer details panel (top of the left pane).</summary>
    public ComputerDetailsViewModel Details { get; }

    /// <summary>Slot shown directly above the events grid (EventsPane.AdvisoryBannerHost). Null = nothing; later features set it.</summary>
    [ObservableProperty] private object? _advisoryBanner;

    public bool ShowOnlyFlagged
    {
        get => FlagFilter == true;
        set => FlagFilter = value ? true : null;
    }

    /// <summary>Raised when the header set changes so the view regenerates columns.</summary>
    public event Action? ColumnsChanged;
    /// <summary>Raised after flags change so the view refreshes realised rows.</summary>
    public event Action? FlagsChanged;
    /// <summary>Effective shortcut (canonical text, "" = unassigned) per <see cref="EzForensics.Core.Settings.HotKeys"/> action id; follows Options live.</summary>
    public IReadOnlyDictionary<string, string> HotKeyMap { get; private set; } = EzForensics.Core.Settings.HotKeys.Resolve(null);
    public event Action? HotKeysChanged;
    private void RefreshHotKeys()
    {
        HotKeyMap = EzForensics.Core.Settings.HotKeys.Resolve(S.Settings.HotKeys);
        HotKeysChanged?.Invoke();
    }

    public override void OnNavigatedTo()
    {
        var last = S.LastCaseDir;
        if (!_sessionRestoreStarted)
        {
            _sessionRestoreStarted = true;
            if (string.IsNullOrEmpty(last) && !_treeLoadedOnce)
            {
                _treeLoadedOnce = true;
                _ = RestoreLastSessionAsync(); // last case + file + focus mode; falls back to the newest case
                return;
            }
            _ = EnsureSessionAsync().ContinueWith(t => { if (t.IsCompletedSuccessfully) AppServices.OnUi(() => _session ??= t.Result); });
        }
        if (!string.IsNullOrEmpty(last) && !string.Equals(last, CaseFolder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(last))
        {
            LeaveFileOutside(last); // e.g. a new Ingest run: the old case's file and filters must not stay on screen
            CaseFolder = last;
            _ = RefreshTreeAsync();
        }
        else if (!_treeLoadedOnce)
        {
            _treeLoadedOnce = true;
            _ = InitDefaultCaseAsync();
        }
        else _ = RevalidateStatsAsync(); // same case: recompute only the files that changed meanwhile
    }

    private async Task InitDefaultCaseAsync()
    {
        await GuardAsync(async () =>
        {
            var root = S.Settings.CaseRoot;
            var latest = await Task.Run(() =>
            {
                if (!Directory.Exists(root)) return null;
                return new DirectoryInfo(root).EnumerateDirectories()
                    .Where(d => !d.Name.StartsWith("_"))
                    .OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault()?.FullName;
            });
            if (latest != null)
            {
                CaseFolder = latest;
                await RefreshTreeAsync();
            }
        }, "Could not find the latest case");
    }

    private void OnQueueJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Job j in e.NewItems) j.PropertyChanged += OnQueueJobChanged;
    }

    /// <summary>A job of the open case finished: refresh the stats (and, once the case's jobs are all done, the tree) shortly after.</summary>
    private void OnQueueJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Job.State) || sender is not Job { IsFinished: true } job) return;
        AppServices.OnUi(() =>
        {
            Details.OnJobFinished(job);
            if (CaseFolder == null || !SamePath(job.CaseDir, CaseFolder)) return;
            _caseJobTimer.Stop();
            _caseJobTimer.Start();
        });
    }

    private void OnCaseJobsSettled()
    {
        S.RaiseResultsReady();
        var folder = CaseFolder;
        if (folder == null || Tree.Count == 0) return;
        bool running = S.Queue.Jobs.Any(j => !j.IsFinished && SamePath(j.CaseDir, folder));
        if (running) _ = RevalidateStatsAsync(); // triage still running: keep the tree as it is
        else _ = RefreshTreeAsync();              // run finished: pick up new files too (stats of unchanged files carry over)
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Opens <paramref name="dir"/> as the case without a file (Load demo case); the tree reloads even while another page is on screen.</summary>
    public async Task OpenCaseFolderAsync(string dir)
    {
        _treeLoadedOnce = true;
        LeaveFileOutside(dir);
        CaseFolder = dir;
        await RefreshTreeAsync();
    }

    [RelayCommand]
    private async Task BrowseCaseAsync()
    {
        var d = Dialogs.PickFolder("Select a case folder", CaseFolder ?? S.Settings.CaseRoot);
        if (d == null) return;
        LeaveFileOutside(d);
        CaseFolder = d;
        await RefreshTreeAsync();
    }

    /// <summary>
    /// Switching to another case: when the open file is not in <paramref name="newCase"/>, its view is saved (restored if it is opened again),
    /// then the file is closed and the search, filters, flag filter, sort and pivot are cleared, so nothing carries over to the new case.
    /// </summary>
    internal void LeaveFileOutside(string newCase)
    {
        var file = CurrentFile;
        if (file == null || IsUnder(file, newCase)) return;
        StashCurrentFile();
        _openCts?.Cancel();
        _suspendApply = true;
        try
        {
            GlobalText = "";
            FiltersEnabled = true;
            _syncingFlagOptions = true;
            try
            {
                foreach (var o in FlagOptions) o.IsSelected = false;
                FlagFilter = null;
            }
            finally { _syncingFlagOptions = false; }
            UpdateFlagFilterText();
            Extra = new FilterGroup();
            ActiveSavedFilter = null;
            ActiveSavedView = null;
            SortColumn = null;
            SortDescending = false;
            ResetPivotForNewFile();
        }
        finally { _suspendApply = false; }
        _evidenceRows = null; NotifyEvidenceRows();
        _pendingView = null;
        _viewOrder = Array.Empty<int>(); _viewSelPos = -1; _viewSelSource = -1; _viewScroll = 0;
        _fileFp = null;
        ClearAdvisories();
        Banner = null;
        CloseCurrentFile();
        RefreshFilterState();
        MarkSessionDirty();
    }

    private static bool IsUnder(string path, string folder)
    {
        try
        {
            var f = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(f, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    partial void OnCaseFolderChanged(string? value) { _ = Details.LoadAsync(value); MarkSessionDirty(); RefreshFindings(); }

    partial void OnSelectedNodeChanged(FileNode? value)
    {
        if (value is { IsFile: true } && !string.Equals(value.FullPath, CurrentFile, StringComparison.OrdinalIgnoreCase))
        {
            _ = OpenCsvAsync(value.FullPath);
            // a file found by the tree's content search: pre-fill the grid's global search with the keyword
            if (TreeSearchKeywordFor(value) is { } keyword) { GlobalText = keyword; _autoFilledGlobalText = keyword; }
        }
    }
}
