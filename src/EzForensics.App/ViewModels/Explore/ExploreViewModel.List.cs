using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Explore;
using EzForensics.Core.Explore.Search;
using NameFilterMatcher = EzForensics.Core.Explore.NameFilter;

namespace EzForensics.App.ViewModels.Explore;

public enum FactsState { None, Loading, Done, Skipped, Failed }

/// <summary>One row of the folder listing. Name, size and times come from the directory listing; the fact columns fill lazily.</summary>
public sealed partial class FileRow : ObservableObject
{
    public FileRow(FileEntry entry) { Entry = entry; }

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public bool IsDirectory => Entry.IsDirectory;
    public long SizeBytes => Entry.IsDirectory ? -1 : Entry.Length;
    public string SizeText => Entry.IsDirectory ? "" : Fmt.Size(Entry.Length);
    public DateTime ModifiedUtc => Entry.ModifiedUtc;
    public string ModifiedText => Fmt.Utc(Entry.ModifiedUtc);
    /// <summary>The containing folder (the Path column while the name filter is crawling subfolders).</summary>
    public string ParentPath => Path.GetDirectoryName(Entry.FullPath) ?? "";
    public string Glyph => Entry.IsDirectory ? "" : "";

    [ObservableProperty] private FactsState _state;
    [ObservableProperty] private FileFacts? _facts;
    [ObservableProperty] private string _typeText = "";
    [ObservableProperty] private string _extFlag = "";
    [ObservableProperty] private double _entropy = -1;
    [ObservableProperty] private string _entropyText = "";
    [ObservableProperty] private string _signedText = "";
    [ObservableProperty] private string _sha256 = "";

    public void Apply(FileFacts f)
    {
        Facts = f;
        TypeText = f.Type.Description;
        ExtFlag = f.ExtensionMismatch ? "≠" : "";
        Entropy = f.Entropy;
        EntropyText = f.Entropy.ToString("0.00");
        SignedText = !f.IsPe ? "" : f.Signature is { Valid: true } s ? (string.IsNullOrEmpty(s.Signer) ? "Signed" : s.Signer!) : "Unsigned";
        Sha256 = f.Hashes.Sha256;
        State = FactsState.Done;
    }
}

public sealed partial class ExploreViewModel
{
    public const int PageSize = 500;

    public ObservableCollection<FileRow> Rows { get; } = new();
    public ListCollectionView RowsView { get; private set; } = null!;
    public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = new();

    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _nameFilter = "";
    [ObservableProperty] private bool _isLoadingFolder;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _listStatus = "";
    [ObservableProperty] private string? _listError;
    [ObservableProperty] private bool _canBack;
    [ObservableProperty] private bool _canForward;
    [ObservableProperty] private bool _canUp;
    /// <summary>True while the list shows the name filter's matches from the current folder and its subfolders (not the plain listing).</summary>
    [ObservableProperty] private bool _isFiltering;

    private readonly NavHistory _history = new();
    private CancellationTokenSource _folderCts = new();
    private int _nextSkip;
    private bool _loadingMore;
    private bool _singleFile;
    private string _rootPath = "";
    private string _rootLabel = "";

    private readonly ConcurrentDictionary<string, FileFacts> _factsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<FileFacts>> _factsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _factsGate = new(2, 2);

    private void InitList()
    {
        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.SortDescriptions.Add(new SortDescription(nameof(FileRow.IsDirectory), ListSortDirection.Descending));
        RowsView.SortDescriptions.Add(new SortDescription(nameof(FileRow.Name), ListSortDirection.Ascending));
    }

    private void ResetList()
    {
        _folderCts.Cancel();
        _folderCts = new CancellationTokenSource();
        Rows.Clear();
        Breadcrumb.Clear();
        _history.Clear();
        CurrentPath = "";
        ListStatus = "";
        ListError = null;
        HasMore = false;
        UpdateNavState();
    }

    private CancellationTokenSource? _filterDebounce;
    private CancellationTokenSource? _crawlCts;
    private Task _crawlTask = Task.CompletedTask;
    private bool _crawlRunning;
    private bool _crawlCapped;

    /// <summary>Most matches the filter crawl lists.</summary>
    public const int CrawlMaxResults = 10_000;
    private const int CrawlFlushMs = 150;
    private const int CrawlFlushBatch = 1000;

    // Typing is debounced (~200 ms); then the filter crawls the current folder and its subfolders (see NameFilter for the syntax).
    partial void OnNameFilterChanged(string value)
    {
        _filterDebounce?.Cancel();
        var cts = _filterDebounce = new CancellationTokenSource();
        var ctx = SynchronizationContext.Current;
        _ = Task.Delay(200, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (ctx != null) ctx.Post(_ => { if (!cts.IsCancellationRequested) ApplyNameFilter(); }, null);
            else ApplyNameFilter();
        }, TaskScheduler.Default);
    }

    /// <summary>Esc in the filter box: clears the filter and shows the normal listing right away.</summary>
    public void ClearNameFilter()
    {
        _filterDebounce?.Cancel();
        NameFilter = "";
        _filterDebounce?.Cancel();
        ApplyNameFilter();
    }

    /// <summary>Applies the current <see cref="NameFilter"/> now (the debounce calls this): empty restores the folder listing, else the crawl restarts.</summary>
    internal void ApplyNameFilter()
    {
        if (string.IsNullOrEmpty(CurrentPath) || _singleFile) return;
        if (NameFilterMatcher.IsEmpty(NameFilter))
        {
            CancelCrawl();
            if (IsFiltering) _ = LoadFolderAsync(CurrentPath);
            return;
        }
        _ = StartCrawlAsync(CurrentPath, NameFilter.Trim());
    }

    private void CancelCrawl()
    {
        _crawlCts?.Cancel();
        _crawlCts = null;
        _crawlRunning = false;
    }

    /// <summary>Resolves when the running (or last) crawl has finished; for tests.</summary>
    internal Task WaitForCrawlAsync() => _crawlTask;

    private Task StartCrawlAsync(string folder, string filter) => _crawlTask = CrawlAsync(folder, filter);

    private async Task CrawlAsync(string folder, string filter)
    {
        CancelCrawl();
        var cts = _crawlCts = new CancellationTokenSource();
        var ct = cts.Token;
        // The plain listing stops (paging, row facts); the rows are replaced by the matches as they stream in.
        _folderCts.Cancel();
        _folderCts = new CancellationTokenSource();
        Rows.Clear();
        ListError = null;
        HasMore = false;
        _loadingMore = false;
        IsFiltering = true;
        IsLoadingFolder = true;
        _crawlRunning = true;
        _crawlCapped = false;
        UpdateStatus();

        var incoming = new ConcurrentQueue<SearchResult>();
        var options = new SearchOptions
        {
            Roots = new[] { folder },
            IncludeSubfolders = true,
            NameText = filter,
            NameAsFilter = true,
            Target = SearchTarget.Both,
            MaxResults = CrawlMaxResults + 1, // one extra result proves the limit was really exceeded
        };
        var producer = Task.Run(async () =>
        {
            await foreach (var r in new FileSearcher().SearchAsync(options, null, ct).ConfigureAwait(false)) incoming.Enqueue(r);
        }, CancellationToken.None);

        int total = 0;
        void Flush(bool all)
        {
            int n = 0;
            while ((all || n < CrawlFlushBatch) && !ct.IsCancellationRequested && incoming.TryDequeue(out var r))
            {
                if (total >= CrawlMaxResults) { _crawlCapped = true; continue; } // the extra match: drop it, but the list really was cut
                Rows.Add(new FileRow(new FileEntry(r.FullPath, r.Name, r.IsDirectory, r.Size, r.CreatedUtc, r.ModifiedUtc, r.AccessedUtc, r.Attributes)));
                n++; total++;
            }
            if (n > 0) UpdateStatus();
        }

        try
        {
            while (!producer.IsCompleted)
            {
                await Task.WhenAny(producer, Task.Delay(CrawlFlushMs, CancellationToken.None));
                if (ct.IsCancellationRequested) return;
                Flush(all: false);
            }
            await producer;
            if (ct.IsCancellationRequested) return;
            Flush(all: true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { if (!ct.IsCancellationRequested) ListError = ex.Message; }
        finally
        {
            if (ReferenceEquals(_crawlCts, cts))
            {
                _crawlRunning = false;
                IsLoadingFolder = false;
                _crawlCts = null;
                UpdateStatus();
            }
            else _ = producer.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            cts.Dispose();
        }
    }

    // ------------------------------------------------------------------------------ navigation

    public void NavigateTo(string path) => NavigateCore(path, push: true);

    private void NavigateCore(string path, bool push)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (push) _history.Push(path);
        UpdateNavState();
        _ = LoadFolderAsync(path);
    }

    private void UpdateNavState()
    {
        CanBack = _history.CanBack;
        CanForward = _history.CanForward;
        CanUp = !string.IsNullOrEmpty(CurrentPath) && !_singleFile && Breadcrumbs.Parent(CurrentPath, _rootPath) != null;
    }

    [RelayCommand]
    private void Back() { if (_history.Back() is { } p) NavigateCore(p, push: false); }

    [RelayCommand]
    private void Forward() { if (_history.Forward() is { } p) NavigateCore(p, push: false); }

    [RelayCommand]
    private void Up()
    {
        if (!string.IsNullOrEmpty(CurrentPath) && Breadcrumbs.Parent(CurrentPath, _rootPath) is { } p) NavigateTo(p);
    }

    [RelayCommand]
    private void Refresh() { if (!string.IsNullOrEmpty(CurrentPath)) _ = LoadFolderAsync(CurrentPath); }

    [RelayCommand]
    private void GoToSegment(BreadcrumbSegment? seg) { if (seg != null) NavigateTo(seg.Path); }

    /// <summary>Double-click / Enter on a row: a folder is entered; a file is only selected (never opened).</summary>
    public void ActivateRow(FileRow row)
    {
        // A filter result can sit in a subfolder: go back to Browse at it (folders are entered, files selected).
        if (IsFiltering) { _ = RevealAsync(row.FullPath, row.IsDirectory); return; }
        if (row.IsDirectory) NavigateTo(row.FullPath);
    }

    private async Task LoadFolderAsync(string path)
    {
        CancelCrawl();
        _folderCts.Cancel();
        var cts = _folderCts = new CancellationTokenSource();
        var ct = cts.Token;
        Rows.Clear();
        ListError = null;
        HasMore = false;
        _nextSkip = 0;
        _loadingMore = false;
        IsLoadingFolder = true;
        _singleFile = false;
        CurrentPath = path;
        _ = SyncTreeAsync(path);

        var owner = _sources?.Owner(path) ?? _sources?.CaseSource;
        _rootPath = owner?.Path ?? path;
        _rootLabel = owner?.Label ?? path;
        Breadcrumb.Clear();
        foreach (var seg in Breadcrumbs.Build(path, _rootPath, _rootLabel)) Breadcrumb.Add(seg);

        if (!NameFilterMatcher.IsEmpty(NameFilter) && !File.Exists(path))
        {
            // A filter is active: list its matches from this folder down instead of the plain listing.
            UpdateNavState();
            _ = StartCrawlAsync(path, NameFilter.Trim());
            return;
        }
        IsFiltering = false;

        try
        {
            if (File.Exists(path))
            {
                _singleFile = true;
                var e = await Task.Run(() => FileEntry.FromPath(path), ct);
                if (ct.IsCancellationRequested) return;
                if (e == null) ListError = "The file is not available any more.";
                else
                {
                    var row = new FileRow(e);
                    Rows.Add(row);
                    SelectedRow = row;
                }
            }
            else
            {
                await AppendPageAsync(path, ct);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { ListError = ex.Message; }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingFolder = false; UpdateNavState(); UpdateStatus();
            }
        }
    }

    private async Task AppendPageAsync(string path, CancellationToken ct)
    {
        var opts = OptionsFor(path);
        int skip = _nextSkip;
        var page = await Task.Run(() => FolderEnumerator.Page(path, skip, PageSize, opts), ct);
        if (ct.IsCancellationRequested) return;
        if (page.Error != null) { ListError = page.Error; return; }
        foreach (var e in page.Entries) Rows.Add(new FileRow(e));
        _nextSkip += page.Entries.Count;
        HasMore = page.HasMore;
    }

    /// <summary>Loads the next page (scrolled near the end).</summary>
    public async Task LoadMoreAsync()
    {
        if (_loadingMore || !HasMore || IsFiltering || string.IsNullOrEmpty(CurrentPath)) return;
        _loadingMore = true;
        var ct = _folderCts.Token;
        var path = CurrentPath;
        try
        {
            await AppendPageAsync(path, ct);
            UpdateStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ListError = ex.Message; }
        finally { _loadingMore = false; }
    }

    private void UpdateStatus()
    {
        if (IsFiltering)
        {
            int n = Rows.Count;
            string where = Breadcrumb.LastOrDefault()?.Label is { Length: > 0 } l ? l : CurrentPath;
            ListStatus = $"{n:N0} match{(n == 1 ? "" : "es")} in {where}"
                + (_crawlRunning ? " (searching…)" : _crawlCapped ? $" (stopped at the {CrawlMaxResults:N0} match limit; narrow the filter)" : "");
            return;
        }
        ListStatus = $"{Rows.Count:N0} item{(Rows.Count == 1 ? "" : "s")}{(HasMore ? "+ (scroll for more)" : "")}";
    }

    // ------------------------------------------------------------------------------ lazy facts

    private static string KeyOf(FileEntry e) => $"{e.FullPath}|{e.Length}|{e.ModifiedUtc.Ticks}";

    /// <summary>Visible rows ask for their facts here (hashes, entropy, type, signer): at most 2 at a time, files up to 64 MB. Bigger files wait for selection.</summary>
    public void RequestFacts(FileRow row)
    {
        if (row.IsDirectory || row.State != FactsState.None) return;
        if (_factsCache.TryGetValue(KeyOf(row.Entry), out var hit)) { row.Apply(hit); return; }
        if (row.Entry.Length > RowFactsMaxBytes) { row.State = FactsState.Skipped; row.TypeText = "(select to analyze)"; return; }
        row.State = FactsState.Loading;
        _ = ApplyRowFactsAsync(row);
    }

    private async Task ApplyRowFactsAsync(FileRow row)
    {
        try { row.Apply(await GetFactsAsync(row.Entry, _folderCts.Token, null, background: true)); }
        catch (OperationCanceledException) { if (row.State == FactsState.Loading) row.State = FactsState.None; }
        catch (Exception) { row.State = FactsState.Failed; row.TypeText = "(unreadable)"; }
    }

    private async Task<FileFacts> GetFactsAsync(FileEntry entry, CancellationToken ct, IProgress<double>? progress, bool background)
    {
        var key = KeyOf(entry);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_factsCache.TryGetValue(key, out var hit)) return hit;
            if (_factsInFlight.TryGetValue(key, out var running))
            {
                try { return await running.WaitAsync(ct); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // the other requester gave up: drop its entry and start our own
                    _factsInFlight.TryRemove(new KeyValuePair<string, Task<FileFacts>>(key, running));
                    continue;
                }
            }
            // Background rows live as long as the folder view, the viewer's request as long as the selection.
            var token = ct;
            var task = Task.Run(async () =>
            {
                if (background) await _factsGate.WaitAsync(token);
                try
                {
                    var f = FileFactsReader.Compute(entry.FullPath, null, progress, token);
                    _factsCache[key] = f;
                    return f;
                }
                finally { if (background) _factsGate.Release(); _factsInFlight.TryRemove(key, out _); }
            }, token);
            if (!_factsInFlight.TryAdd(key, task)) continue;
            try { return await task; }
            finally { _factsInFlight.TryRemove(new KeyValuePair<string, Task<FileFacts>>(key, task)); }
        }
    }
}
