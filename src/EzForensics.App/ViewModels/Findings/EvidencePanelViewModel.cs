using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Results;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>
/// The evidence panel of the Findings page: the grid of one evidence file (Analyze's <see cref="CsvIndex"/> + <see cref="VirtualRowList"/>,
/// searched with <see cref="FilterEngine"/>), filtered to the rows behind a finding, with Spectre's mark and the examiner's labels in a gutter.
/// Labels are saved through the engine (<see cref="SpectreFindingsClient.FlagEvidenceAsync"/>) and never touch Analyze's own flags.
/// Split: this file (files, grid, navigation), .Flags (labels, notes, new finding).
/// </summary>
public sealed partial class EvidencePanelViewModel : ObservableObject, IDisposable
{
    public const string AuditHint = "Your flags are recorded with your name and the time (UTC) and appear in the report under the finding.";
    public const string UpdateEngineText = "Update the Spectre engine to save flags. The rows are shown read-only.";

    private readonly IEvidencePanelHost _host;
    private CsvIndex? _index;
    private CancellationTokenSource? _openCts, _viewCts;
    private IReadOnlyList<EvidenceTarget> _plan = Array.Empty<EvidenceTarget>();
    private int _openVersion;

    internal EvidencePanelViewModel(IEvidencePanelHost host) { _host = host; }

    [ObservableProperty] private bool _isOpen;
    /// <summary>False for the copy shown in Details → Evidence (it follows the selected finding; there is nothing to close).</summary>
    public bool CanClose { get; init; } = true;
    [ObservableProperty] private string _title = "";
    /// <summary>The finding the panel was opened for; flags on rows no Spectre row names go to it.</summary>
    [ObservableProperty] private string _findingId = "";
    public ObservableCollection<EvidencePanelFile> Files { get; } = new();
    public bool HasSeveralFiles => Files.Count > 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FileLine))] private EvidencePanelFile? _currentFile;

    [ObservableProperty] private IReadOnlyList<string> _headers = Array.Empty<string>();
    [ObservableProperty] private VirtualRowList? _rows;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FileLine))] private long _fileRowCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountLine))] private int _visibleCount;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _showAllRows;
    [ObservableProperty] private string _searchText = "";

    /// <summary>Rows selected in the grid (file row indexes), set by the view.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText))] private IReadOnlyList<long> _selectedRows = Array.Empty<long>();
    /// <summary>The row shown in the detail strip (the grid's current row).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCurrentRow))] private long? _currentRow;
    public ObservableCollection<KeyValuePair<string, string>> DetailCells { get; } = new();
    [ObservableProperty] private string _detailMarkText = "";
    public bool HasCurrentRow => CurrentRow != null;
    public bool HasSelection => SelectedRows.Count > 0;
    public string SelectionText => SelectedRows.Count switch { 0 => "", 1 => $"Row {SelectedRows[0] + 1:N0} selected", var n => $"{n:N0} rows selected" };

    /// <summary>Raised to move the grid to a position of <see cref="Rows"/> (select it and scroll it into view).</summary>
    public event Action<int>? GoToPositionRequested;
    /// <summary>Raised when the columns change (another file); the view rebuilds its grid columns.</summary>
    public event Action? ColumnsChanged;

    /// <summary>"Security.csv · PC1 · 3 Spectre rows of 1,204".</summary>
    public string FileLine
    {
        get
        {
            if (CurrentFile is not { } f) return "";
            var parts = new List<string> { f.Name, f.System ?? "Case-wide" };
            parts.Add(f.IsResolved ? $"{f.SpectreCount:N0} Spectre row{(f.SpectreCount == 1 ? "" : "s")} of {FileRowCount:N0}" : $"{FileRowCount:N0} rows");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>"Showing 3 rows" under the grid.</summary>
    public string CountLine => $"Showing {VisibleCount:N0} row{(VisibleCount == 1 ? "" : "s")}";

    /// <summary>The evidence rows of every file in file order: the rows Previous / Next step through.</summary>
    internal IReadOnlyList<(EvidencePanelFile File, long Row)> EvidenceSequence() =>
        Files.SelectMany(f => EvidenceRowsOf(f).Select(r => (f, r))).ToList();

    /// <summary>Spectre's rows plus the rows the examiner labelled or added, sorted.</summary>
    internal SortedSet<long> EvidenceRowsOf(EvidencePanelFile f)
    {
        var set = new SortedSet<long>(f.SpectreRows);
        set.UnionWith(f.AddedRows);
        var path = Key(f.CasePath, 0).Item1;
        foreach (var key in _labels.Keys.Concat(_notes.Keys))
            if (key.CasePath == path) set.Add(key.Row);
        return set;
    }

    // ------------------------------------------------------------ open / close

    /// <summary>
    /// Opens the panel on the rows behind <paramref name="findings"/> (each finding id with what it names as evidence): plans the files
    /// (<see cref="FindingsEvidence.Plan"/>), loads the examiner's flags, opens the first file and resolves the rows of every file.
    /// False when no evidence file can be opened (the caller falls back).
    /// </summary>
    public async Task<bool> OpenAsync(string title, string primaryFindingId, IReadOnlyList<(string FindingId, EvidenceSource Source)> findings)
    {
        var dir = _host.CaseFolder;
        if (string.IsNullOrEmpty(dir)) return false;
        var plan = await Task.Run(() => FindingsEvidence.Plan(dir, findings.Select(f => f.Source)));
        if (plan.Count == 0) return false;
        var version = ++_openVersion;
        CloseFile();
        _plan = plan;
        Title = title; FindingId = primaryFindingId; Message = ""; SearchText = ""; ShowAllRows = false;
        _labels.Clear(); _notes.Clear();
        Files.Clear();
        var known = findings.SelectMany(f => f.Source.Evidence.Select(e => e.CasePath).Append(f.Source.CasePath)).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct().ToList();
        var files = await Task.Run(() => BuildFiles(dir, plan, findings, known));
        if (version != _openVersion) return true;
        foreach (var f in files) Files.Add(f);
        OnPropertyChanged(nameof(HasSeveralFiles));
        IsOpen = true;
        await LoadFlagsAsync(findings.Select(f => f.FindingId), findings);
        if (version != _openVersion) return true;
        await ShowFileAsync(Files[0], selectFirst: true);
        _ = ResolveOtherFilesAsync(version);
        return true;
    }

    private List<EvidencePanelFile> BuildFiles(string dir, IReadOnlyList<EvidenceTarget> plan, IReadOnlyList<(string FindingId, EvidenceSource Source)> findings, IReadOnlyList<string> known)
    {
        var list = new List<EvidencePanelFile>();
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Resolve(string p) { if (!cache.TryGetValue(p, out var f)) cache[p] = f = FindingsEvidence.ResolveSourceFile(p, dir); return f; }
        foreach (var t in plan)
        {
            var casePath = FindingsEvidence.CasePathOf(dir, t.Csv, known);
            var file = new EvidencePanelFile(t, casePath, _host.SystemOf(casePath));
            foreach (var (id, src) in findings)
                foreach (var e in src.Evidence)
                {
                    if (!string.Equals(Resolve(e.CasePath), t.Csv, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.IsAnalyst) file.AddedRows.Add(e.Row);
                    else { file.SpectreRows.Add(e.Row); file.RowFinding.TryAdd(e.Row, id); }
                }
            file.AddedRows.ExceptWith(file.SpectreRows);
            // explicit rows only (no record ids to look up): the count is known before the file is read
            if (t.RecordIds.Count == 0) { file.SpectreCount = file.SpectreRows.Count; file.IsResolved = true; }
            list.Add(file);
        }
        return list;
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        _openVersion++;
        CloseFile();
        IsNoteOpen = false; IsNewFindingOpen = false;
    }

    private void CloseFile()
    {
        _openCts?.Cancel(); _viewCts?.Cancel();
        var old = _index;
        _index = null;
        Rows = null;
        Headers = Array.Empty<string>();
        ColumnsChanged?.Invoke();
        old?.Dispose();
        CurrentRow = null; SelectedRows = Array.Empty<long>(); DetailCells.Clear(); DetailMarkText = "";
    }

    public void Dispose() => CloseFile();

    [RelayCommand]
    private Task SelectFile(EvidencePanelFile? file) =>
        file == null || ReferenceEquals(file, CurrentFile) ? Task.CompletedTask : ShowFileAsync(file, selectFirst: true);

    /// <summary>Opens <paramref name="file"/> in the grid: index it, resolve its rows, show the evidence view; optionally go to its first evidence row.</summary>
    internal async Task ShowFileAsync(EvidencePanelFile file, bool selectFirst, long? goToRow = null)
    {
        CloseFile();
        var cts = _openCts = new CancellationTokenSource();
        foreach (var f in Files) f.IsCurrent = ReferenceEquals(f, file);
        CurrentFile = file;
        IsLoading = true; Message = "";
        try
        {
            var idx = await CsvIndex.OpenAsync(file.Csv, null, cts.Token);
            if (cts.IsCancellationRequested) { idx.Dispose(); return; }
            await idx.IndexingCompleted.WaitAsync(cts.Token);
            if (!file.IsResolved) await ResolveAsync(file, idx, cts.Token);
            if (cts.IsCancellationRequested) { idx.Dispose(); return; }
            _index = idx;
            FileRowCount = idx.RowCount;
            Headers = idx.Headers;
            Rows = new VirtualRowList(idx) { MarkOf = r => MarkOf(file, r) };
            ColumnsChanged?.Invoke();
            OnPropertyChanged(nameof(FileLine));
            await ApplyViewAsync();
            if (file.SpectreCount == 0 && EvidenceRowsOf(file).Count == 0)
                Message = $"Could not find the rows behind this finding in {file.Name}. Use Show all rows to look through the file.";
            var target = goToRow ?? (selectFirst ? EvidenceRowsOf(file).Cast<long?>().FirstOrDefault() : null);
            if (target is { } row) await GoToRowAsync(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
            Message = $"Could not open {file.Name}: {ex.Message}";
            AppLog.Warn("Evidence panel: " + ex.Message);
        }
        finally { if (ReferenceEquals(cts, _openCts)) IsLoading = false; }
    }

    private static async Task ResolveAsync(EvidencePanelFile file, CsvIndex idx, CancellationToken ct)
    {
        var rows = await Task.Run(() => FindingsEvidence.ResolveRows(idx, file.Target, ct), ct);
        var spectre = new SortedSet<long>(rows);
        spectre.ExceptWith(file.AddedRows);   // rows only the examiner added are not Spectre's
        foreach (var r in file.SpectreRows) if (r < idx.RowCount) spectre.Add(r);
        file.SpectreRows = spectre;
        file.SpectreCount = spectre.Count;
        file.IsResolved = true;
    }

    /// <summary>Counts the rows of the files not opened yet (one at a time, in the background).</summary>
    private async Task ResolveOtherFilesAsync(int version)
    {
        foreach (var f in Files.ToList())
        {
            if (version != _openVersion) return;
            if (f.IsResolved) continue;
            try
            {
                using var idx = await CsvIndex.OpenAsync(f.Csv, null, CancellationToken.None);
                await idx.IndexingCompleted;
                if (version != _openVersion) return;
                await ResolveAsync(f, idx, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
            {
                AppLog.Warn($"Evidence panel: could not count the rows of {f.Name}: {ex.Message}");
            }
        }
        RefreshCounts();
    }

    // ------------------------------------------------------------ view: evidence rows or all, quick search

    partial void OnShowAllRowsChanged(bool value) { if (!_viewSuppressed) _ = ApplyViewAsync(); }
    partial void OnSearchTextChanged(string value) { if (!_viewSuppressed) _ = ApplyViewAsync(); }

    /// <summary>Shows the evidence rows (or every row) of the open file that match the quick search.</summary>
    internal async Task ApplyViewAsync()
    {
        if (_index is not { } idx || Rows is not { } rows || CurrentFile is not { } file) return;
        _viewCts?.Cancel();
        var cts = _viewCts = new CancellationTokenSource();
        var keep = CurrentRow;
        var search = SearchText.Trim();
        try
        {
            if (search.Length == 0 && ShowAllRows) rows.ShowAll(idx.RowCount);
            else if (search.Length == 0) rows.ShowMap(EvidenceRowsOf(file).Where(r => r < idx.RowCount).Select(r => (int)r).ToArray());
            else
            {
                var spec = new FilterSpec { GlobalText = search };
                if (!ShowAllRows) { spec.Flag = FlagFilterMode.Flagged; spec.FlaggedRows = new HashSet<long>(EvidenceRowsOf(file)); }
                var map = await FilterEngine.FilterAsync(idx, spec, null, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(idx, _index)) return;
                rows.ShowMap(map);
            }
            VisibleCount = rows.Count;
            if (Message.StartsWith("The search", StringComparison.Ordinal)) Message = "";
            if (keep is { } k && PositionOf(k) is { } p) GoToPositionRequested?.Invoke(p);
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException ex) { Message = "The search is not valid: " + ex.Message; }
    }

    /// <summary>The position of file row <paramref name="row"/> in the current view; null when it is filtered out.</summary>
    internal int? PositionOf(long row)
    {
        if (Rows is not { } rows) return null;
        if (rows.Map is not { } map) return row < rows.Count ? (int)row : null;
        int i = Array.BinarySearch(map, (int)row);
        return i >= 0 ? i : null;
    }

    /// <summary>Selects <paramref name="row"/> of the open file (clears the search, then shows all rows, when the view hides it).</summary>
    internal async Task GoToRowAsync(long row)
    {
        if (PositionOf(row) == null && SearchText.Length > 0) { _viewSuppressed = true; SearchText = ""; _viewSuppressed = false; await ApplyViewAsync(); }
        if (PositionOf(row) == null && !ShowAllRows) { _viewSuppressed = true; ShowAllRows = true; _viewSuppressed = false; await ApplyViewAsync(); }
        if (PositionOf(row) is not { } p) return;
        CurrentRow = row;
        SelectedRows = new[] { row };
        GoToPositionRequested?.Invoke(p);
    }

    private bool _viewSuppressed;

    partial void OnCurrentRowChanged(long? value) => UpdateDetail();

    /// <summary>The detail strip: the current row's non-empty cells and its marks.</summary>
    private void UpdateDetail()
    {
        DetailCells.Clear();
        DetailMarkText = "";
        if (CurrentRow is not { } row || _index is not { } idx || CurrentFile is not { } file || row >= idx.RowCount) return;
        string[] cells;
        try { cells = idx.ReadRow(row); }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException) { return; }
        for (int i = 0; i < Headers.Count && i < cells.Length; i++)
            if (!string.IsNullOrWhiteSpace(cells[i])) DetailCells.Add(new(Headers[i], cells[i].Replace('\r', ' ').Replace('\n', ' ')));
        var mark = MarkOf(file, row);
        DetailMarkText = $"Row {row + 1:N0}" + (mark is { IsEmpty: false } ? " · " + mark.Text.Replace("\n", " · ") : "");
    }

    // ------------------------------------------------------------ previous / next evidence row (across files)

    [RelayCommand]
    private Task NextRow() => StepAsync(+1);

    [RelayCommand]
    private Task PreviousRow() => StepAsync(-1);

    /// <summary>Moves to the next (or previous) evidence row; past the last row of a file it opens the next file at its first row.</summary>
    internal async Task StepAsync(int dir)
    {
        if (CurrentFile is not { } file) return;
        var seq = EvidenceSequence();
        if (seq.Count == 0) return;
        int at = -1;
        for (int i = 0; i < seq.Count; i++)
            if (ReferenceEquals(seq[i].File, file) && CurrentRow is { } cur && seq[i].Row == cur) { at = i; break; }
        if (at < 0)
        {
            // not on an evidence row: the first one after (or before) the current row in this file, else the file's first
            var cur = CurrentRow ?? -1;
            var fileIdx = Files.IndexOf(file);
            at = dir > 0
                ? seq.FindIndex(x => Files.IndexOf(x.File) > fileIdx || (ReferenceEquals(x.File, file) && x.Row > cur)) - 1
                : seq.FindLastIndex(x => Files.IndexOf(x.File) < fileIdx || (ReferenceEquals(x.File, file) && x.Row < cur)) + 1;
            if (at < -1) at = dir > 0 ? seq.Count - 1 : 0;
        }
        int next = at + dir;
        if (next < 0 || next >= seq.Count) { Message = dir > 0 ? "This is the last evidence row." : "This is the first evidence row."; return; }
        Message = "";
        var (f, row) = seq[next];
        if (!ReferenceEquals(f, CurrentFile)) await ShowFileAsync(f, selectFirst: false, goToRow: row);
        else await GoToRowAsync(row);
    }

    // ------------------------------------------------------------ Open in Analyze (the old behaviour)

    [RelayCommand]
    private void OpenInAnalyze()
    {
        if (_host.CaseFolder is not { } dir || _plan.Count == 0) return;
        var plan = CurrentFile is { } f ? _plan.OrderBy(t => string.Equals(t.Csv, f.Csv, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList() : _plan.ToList();
        Close();
        _host.OpenEvidenceInAnalyze(dir, plan, Title);
    }

    /// <summary>The file a flag's case path names.</summary>
    private bool SameFile(string casePath, EvidencePanelFile f)
    {
        if (string.Equals(Norm(casePath), Norm(f.CasePath), StringComparison.OrdinalIgnoreCase)) return true;
        return _host.CaseFolder is { } dir && string.Equals(FindingsEvidence.ResolveSourceFile(casePath, dir, maxSearch: 0), f.Csv, StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string p) => p.Trim().Replace('\\', '/').TrimStart('/');
}

internal static class ListExtensions
{
    public static int FindIndex<T>(this IReadOnlyList<T> list, Func<T, bool> match)
    {
        for (int i = 0; i < list.Count; i++) if (match(list[i])) return i;
        return -1;
    }

    public static int FindLastIndex<T>(this IReadOnlyList<T> list, Func<T, bool> match)
    {
        for (int i = list.Count - 1; i >= 0; i--) if (match(list[i])) return i;
        return -1;
    }
}
