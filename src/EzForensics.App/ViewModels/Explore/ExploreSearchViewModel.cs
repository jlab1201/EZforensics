using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Explore.Search;

namespace EzForensics.App.ViewModels.Explore;

/// <summary>One matched file or folder in the results grid.</summary>
public sealed class SearchRow
{
    public SearchRow(SearchResult r) { Result = r; }

    public SearchResult Result { get; }
    public string Name => Result.Name;
    public string Folder => Result.Folder;
    public string FullPath => Result.FullPath;
    public bool IsDirectory => Result.IsDirectory;
    public long SizeBytes => Result.IsDirectory ? -1 : Result.Size;
    public string SizeText => Result.IsDirectory ? "" : Fmt.Size(Result.Size);
    public DateTime ModifiedUtc => Result.ModifiedUtc;
    public string ModifiedText => Fmt.Utc(Result.ModifiedUtc);
    public int HitCount => Result.HitCount;
    public string HitsText => ExploreSearchLogic.HitsText(Result);
    public string Extension => Result.Extension;
    public string Glyph => Result.IsDirectory ? "" : "";
}

/// <summary>One content hit: the matching line (split for highlighting) with its context lines.</summary>
public sealed record HitItem(int LineNumber, string Before, string After, IReadOnlyList<TextSegment> Segments)
{
    public bool HasBefore => Before.Length > 0;
    public bool HasAfter => After.Length > 0;
}

public sealed record SkippedRow(string Path, string Reason, string Message);

/// <summary>
/// Agent Ransack style search over files and folders, shown in Explore instead of the folder list. The engine is
/// <see cref="FileSearcher"/> (Core); this class owns the criteria, streams results into the grid in batches, and keeps the hits pane.
/// Nothing found is ever opened or run: results are only revealed in Explore.
/// </summary>
public sealed partial class ExploreSearchViewModel : ObservableObject
{
    private const int FlushMs = 150;
    private const int FlushBatch = 500;

    private readonly ExploreViewModel _owner;
    private readonly SavedSearches _saved;
    private readonly ConcurrentQueue<SearchResult> _incoming = new();
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _cts;
    private FileSearcher? _searcher;
    private SearchProgress? _lastProgress;
    private bool _lookInFollowsBrowse = true;
    private bool _settingLookIn;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private const int MaxRecent = 10;

    public ExploreSearchViewModel(ExploreViewModel owner, SavedSearches? saved = null)
    {
        _owner = owner;
        _saved = saved ?? new SavedSearches();
        // Size is typed in KB on the Main tab (saved searches are converted to KB when loaded).
        Criteria.MinSizeUnit = SizeUnit.KB;
        Criteria.MaxSizeUnit = SizeUnit.KB;
    }

    public SearchCriteria Criteria { get; } = new();
    public ObservableCollection<SearchRow> Results { get; } = new();
    public ObservableCollection<HitItem> Hits { get; } = new();
    public ObservableCollection<SkippedRow> Skipped { get; } = new();
    public ObservableCollection<string> SavedNames { get; } = new();
    /// <summary>The last folders searched, newest first (this session).</summary>
    public ObservableCollection<string> RecentFolders { get; } = new();

    public IReadOnlyList<NameMatchMode> NameModes { get; } = Enum.GetValues<NameMatchMode>();
    public IReadOnlyList<SearchTarget> Targets { get; } = Enum.GetValues<SearchTarget>();
    public IReadOnlyList<ContentMatchMode> ContentModes { get; } = Enum.GetValues<ContentMatchMode>();
    public IReadOnlyList<ContentEncoding> Encodings { get; } = Enum.GetValues<ContentEncoding>();
    public IReadOnlyList<SizeUnit> SizeUnits { get; } = Enum.GetValues<SizeUnit>();
    public IReadOnlyList<AttrFilter> AttrFilters { get; } = Enum.GetValues<AttrFilter>();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanExport))] [NotifyPropertyChangedFor(nameof(CanClear))] private bool _isRunning;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ErrorsOneLine))] private string? _errors;
    [ObservableProperty] private SearchRow? _selectedResult;
    [ObservableProperty] private string? _hitsNote;
    [ObservableProperty] private string? _hitsHint = "Select a result to see its matching lines.";
    [ObservableProperty] private bool _hasSkipped;
    [ObservableProperty] private bool _showSkipped;
    [ObservableProperty] private string _skippedHeader = "";
    [ObservableProperty] private string? _selectedSaved;
    [ObservableProperty] private string _lookIn = "";
    [ObservableProperty] private string _statusLeft = "Ready.";
    [ObservableProperty] private string _statusRight = "";
    [ObservableProperty] private string _elapsedText = "0:00";
    [ObservableProperty] private long _foundCount;
    [ObservableProperty] private long _checkedCount;
    [ObservableProperty] private long _skippedCount;
    public string? ErrorsOneLine => Errors?.ReplaceLineEndings("  ");
    public bool CanExport => !IsRunning && Results.Count > 0;
    public bool CanClear => !IsRunning && (Results.Count > 0 || !string.IsNullOrEmpty(Status));

    // ------------------------------------------------------------------------------ entering / look in

    /// <summary>Called when the window is opened: unless the user typed or picked folders, "Look in" follows the folder open in Explore.</summary>
    public void OnEnter(string? currentPath)
    {
        ReloadSaved();
        if (!_lookInFollowsBrowse || string.IsNullOrWhiteSpace(currentPath) || IsRunning) return;
        SetLookIn(currentPath);   // a single-file source is searched as it is; for a folder the search looks inside
    }

    private void SetLookIn(string value)
    {
        _settingLookIn = true;
        try { LookIn = value; }
        finally { _settingLookIn = false; }
    }

    partial void OnLookInChanged(string value)
    {
        if (!_settingLookIn) _lookInFollowsBrowse = false;
    }

    /// <summary>Copies the "Look in" text (folders separated by ;) into the criteria's folder list.</summary>
    private void SyncRoots()
    {
        Criteria.Roots.Clear();
        foreach (var r in ExploreSearchLogic.ParseLookIn(LookIn)) Criteria.Roots.Add(r);
    }

    private void AddRecent(IEnumerable<string> folders)
    {
        foreach (var f in folders.Reverse())
        {
            var existing = RecentFolders.FirstOrDefault(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
            if (existing != null) RecentFolders.Remove(existing);
            RecentFolders.Insert(0, f);
        }
        while (RecentFolders.Count > MaxRecent) RecentFolders.RemoveAt(RecentFolders.Count - 1);
    }

    [RelayCommand]
    private void BrowseLookIn()
    {
        var first = ExploreSearchLogic.ParseLookIn(LookIn).FirstOrDefault();
        var dir = Dialogs.PickFolder("Folder to search", first ?? _owner.CurrentPath);
        if (string.IsNullOrEmpty(dir)) return;
        LookIn = dir;
    }

    // ------------------------------------------------------------------------------ running

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        await RunSearchAsync();
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void ToggleSkipped() => ShowSkipped = !ShowSkipped;

    /// <summary>Stops a running search without waiting (leaving the page, starting another search).</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>Runs one search to the end. Results stream into <see cref="Results"/> in batches; returns when finished or cancelled.</summary>
    public async Task RunSearchAsync()
    {
        Cancel();
        SyncRoots();
        var (options, errors) = ExploreSearchLogic.Build(Criteria);
        if (options == null)
        {
            Errors = string.Join(Environment.NewLine, errors);
            return;
        }
        Errors = null;
        ClearResults();
        AddRecent(Criteria.Roots);
        ElapsedText = "0:00";
        _clock.Restart();

        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        var searcher = _searcher = new FileSearcher();
        var progress = new LatestProgress(p => _lastProgress = p);
        IsRunning = true;
        Status = "Searching...";
        StatusLeft = "Searching " + LookIn.Trim() + "\u2026";
        StatusRight = "";
        StartTimer();
        try
        {
            await Task.Run(async () =>
            {
                await foreach (var r in searcher.SearchAsync(options, progress, ct).ConfigureAwait(false)) _incoming.Enqueue(r);
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Errors = ex.Message; }
        finally
        {
            StopTimer();
            Flush(all: true);
            if (ReferenceEquals(_cts, cts))
            {
                var last = _lastProgress;
                if (last != null)
                {
                    if (ct.IsCancellationRequested && !last.Cancelled) last = last with { Cancelled = true };
                    Status = ExploreSearchLogic.StatusText(last);
                    ShowCounts(last, Results.Count);
                    ElapsedText = ExploreSearchLogic.FormatElapsed(last.Elapsed);
                }
                else Status = ct.IsCancellationRequested ? "Cancelled" : "";
                StatusLeft = ct.IsCancellationRequested ? "Search stopped." : Errors != null ? "Search failed." : "Search finished.";
                StatusRight = $"Found {FoundCount:N0} / checked {CheckedCount:N0}  \u00b7  {ElapsedText}";
                RefreshSkipped(searcher);
                IsRunning = false;
                _cts = null;
            }
            cts.Dispose();
        }
    }

    private sealed class LatestProgress : IProgress<SearchProgress>
    {
        private readonly Action<SearchProgress> _set;
        public LatestProgress(Action<SearchProgress> set) { _set = set; }
        public void Report(SearchProgress value) => _set(value);
    }

    private void StartTimer()
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(FlushMs) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    private void OnTick(object? s, EventArgs e)
    {
        Flush(all: false);
        if (_lastProgress is { } p && IsRunning)
        {
            Status = ExploreSearchLogic.StatusText(p with { Matches = Math.Max(p.Matches, Results.Count) });
            ShowCounts(p, Results.Count);
        }
        if (IsRunning)
        {
            ElapsedText = ExploreSearchLogic.FormatElapsed(_clock.Elapsed);
            StatusRight = $"Found {FoundCount:N0} / checked {CheckedCount:N0}  \u00b7  {ElapsedText}";
        }
    }

    private void ShowCounts(SearchProgress p, int shown)
    {
        FoundCount = Math.Max(p.Matches, shown);
        CheckedCount = p.FilesScanned + p.FoldersScanned;
        SkippedCount = p.Skipped;
    }

    /// <summary>Moves queued results into the grid: at most one batch per tick (so the UI stays responsive), or everything at the end.</summary>
    internal void Flush(bool all)
    {
        int n = 0;
        while ((all || n < FlushBatch) && _incoming.TryDequeue(out var r)) { Results.Add(new SearchRow(r)); n++; }
        if (n > 0) { OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(CanClear)); }
    }

    private void RefreshSkipped(FileSearcher searcher)
    {
        Skipped.Clear();
        foreach (var s in searcher.Skipped) Skipped.Add(new SkippedRow(s.Path, ExploreSearchLogic.ReasonText(s.Reason), s.Message ?? ""));
        HasSkipped = Skipped.Count > 0;
        long total = _lastProgress?.Skipped ?? Skipped.Count;
        SkippedHeader = total > Skipped.Count ? $"Skipped items (first {Skipped.Count:N0} of {total:N0})" : $"Skipped items ({Skipped.Count:N0})";
        if (!HasSkipped) ShowSkipped = false;
    }

    private void ClearResults()
    {
        while (_incoming.TryDequeue(out _)) { }
        SelectedResult = null;
        Results.Clear();
        Hits.Clear();
        HitsNote = null;
        Skipped.Clear();
        HasSkipped = false;
        ShowSkipped = false;
        _lastProgress = null;
        FoundCount = CheckedCount = SkippedCount = 0;
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanClear));
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsRunning) return;
        ClearResults();
        Status = "";
        Errors = null;
        StatusLeft = "Ready.";
        StatusRight = "";
        ElapsedText = "0:00";
        OnPropertyChanged(nameof(CanClear));
    }

    /// <summary>The case changed or the page was left for good: stop and drop everything.</summary>
    public void Reset()
    {
        Cancel();
        ClearResults();
        Status = "";
        Errors = null;
        StatusLeft = "Ready.";
        StatusRight = "";
        ElapsedText = "0:00";
        _lookInFollowsBrowse = true;
    }

    // ------------------------------------------------------------------------------ selection / hits

    partial void OnSelectedResultChanged(SearchRow? value)
    {
        Hits.Clear();
        HitsNote = null;
        HitsHint = "Select a result to see its matching lines.";
        if (value == null) return;
        var all = value.Result.Hits;
        HitsHint = all.Count == 0 ? (value.IsDirectory ? "Folder match." : "No content hits for this result.") : null;
        foreach (var h in all.Take(ExploreSearchLogic.MaxHitsShown))
            Hits.Add(new HitItem(h.LineNumber, string.Join(Environment.NewLine, h.Before), string.Join(Environment.NewLine, h.After),
                ExploreSearchLogic.SplitSegments(h.LineText, h.Spans)));
        if (all.Count > ExploreSearchLogic.MaxHitsShown) HitsNote = $"Showing the first {ExploreSearchLogic.MaxHitsShown:N0} of {all.Count:N0} hits.";
        else if (value.Result.HitsTruncated) HitsNote = $"Showing {all.Count:N0} hits; the file has more (stopped at the per-file limit).";
    }

    // ------------------------------------------------------------------------------ actions

    [RelayCommand]
    private Task OpenInExploreAsync(SearchRow? row) => (row ?? SelectedResult) is { } r ? RevealAndActivateAsync(r.FullPath, r.IsDirectory) : Task.CompletedTask;

    [RelayCommand]
    private Task ShowFolderInExploreAsync(SearchRow? row) => (row ?? SelectedResult) is { } r ? RevealAndActivateAsync(r.FullPath, false) : Task.CompletedTask;

    private async Task RevealAndActivateAsync(string path, bool intoFolder)
    {
        await _owner.RevealAsync(path, intoFolder);
        try { Application.Current?.MainWindow?.Activate(); } catch (InvalidOperationException) { }
    }

    [RelayCommand]
    private void CopyPath(SearchRow? row) { if ((row ?? SelectedResult) is { } r) Copy(r.FullPath); }

    [RelayCommand]
    private void CopyName(SearchRow? row) { if ((row ?? SelectedResult) is { } r) Copy(r.Name); }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException) { }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Results.Count == 0) return;
        var path = Dialogs.SaveFile("Export search results", "CSV files|*.csv", "search-results.csv", _owner.CaseDir);
        if (path == null) return;
        var rows = Results.Select(r => r.Result).ToList();
        try
        {
            // Names and paths come from evidence: formulas are neutralised so Excel never runs them.
            int n = await Task.Run(() => SearchCsvExport.Write(path, rows, escapeFormulas: true));
            _owner.ShowMessage($"Exported {n:N0} result{(n == 1 ? "" : "s")} to {path}", InfoSeverity.Success);
        }
        catch (Exception ex) { _owner.ShowError("Could not export the results", ex); }
    }

    // ------------------------------------------------------------------------------ saved searches

    private void ReloadSaved()
    {
        try
        {
            var names = _saved.Load().Select(s => s.Name).ToList();
            SavedNames.Clear();
            foreach (var n in names) SavedNames.Add(n);
        }
        catch (Exception) { /* a locked or unreadable file just leaves the list empty */ }
    }

    partial void OnSelectedSavedChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try
        {
            if (_saved.Get(value) is not { } s) return;
            ExploreSearchLogic.Apply(Criteria, s.Options);
            ToKilobytes(Criteria);
            SetLookIn(string.Join("; ", Criteria.Roots));
            _lookInFollowsBrowse = false;
            Errors = null;
        }
        catch (Exception ex) { _owner.ShowError("Could not load the saved search", ex); }
    }

    /// <summary>The Main tab shows sizes in KB: converts what a saved search loaded (any unit) to KB.</summary>
    private static void ToKilobytes(SearchCriteria c)
    {
        (c.MinSize, c.MinSizeUnit) = InKb(c.MinSize, c.MinSizeUnit);
        (c.MaxSize, c.MaxSizeUnit) = InKb(c.MaxSize, c.MaxSizeUnit);
    }

    private static (string, SizeUnit) InKb(string text, SizeUnit unit)
    {
        if (!ExploreSearchLogic.TryParseSize(text, unit, out var bytes) || bytes is not { } b) return (text, unit);
        return ((b / 1024d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), SizeUnit.KB);
    }

    /// <summary>The name "Save as..." offers first.</summary>
    public string SuggestedSaveName => SelectedSaved ?? "";

    /// <summary>Validates the criteria and stores them under <paramref name="name"/> (the view asks for the name). False when nothing was saved.</summary>
    public bool SaveCurrentAs(string name)
    {
        SyncRoots();
        var (options, errors) = ExploreSearchLogic.Build(Criteria, checkRootsExist: false);
        if (options == null) { Errors = string.Join(Environment.NewLine, errors); return false; }
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            _saved.Save(name, options);
            ReloadSaved();
            SelectedSaved = SavedNames.FirstOrDefault(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
            return true;
        }
        catch (Exception ex) { _owner.ShowError("Could not save the search", ex); return false; }
    }

    [RelayCommand]
    private void DeleteSaved()
    {
        if (string.IsNullOrEmpty(SelectedSaved)) return;
        if (!Dialogs.Confirm("Delete saved search", $"Delete \"{SelectedSaved}\"?", "Delete")) return;
        try
        {
            _saved.Delete(SelectedSaved);
            SelectedSaved = null;
            ReloadSaved();
        }
        catch (Exception ex) { _owner.ShowError("Could not delete the saved search", ex); }
    }
}
