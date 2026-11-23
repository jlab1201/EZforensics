using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.App.Views;
using EzForensics.Core.Analysis;
using EzForensics.Core.Results;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels;

/// <summary>
/// What the finding editor produces: title, severity (Info..Critical), ATT&amp;CK technique ids, Notes, the finding's rows per CSV and, when the rows
/// had mixed flags, the optional recolour of all of them. Evidence is older free text stored with the notes (<see cref="FindingNotes"/>); the editor
/// no longer shows it but keeps it.
/// </summary>
public sealed record KeyEventDraft(string Title, string Severity, IReadOnlyList<string> Mitre, string Notes = "", string Evidence = "", string? RecolourFlagId = null,
    IReadOnlyList<FindingFileRows>? Rows = null);

/// <summary>The rows (0-based) a finding holds in one CSV (case-relative path); <see cref="RowCount"/> bounds the editor's input when known.</summary>
public sealed record FindingFileRows(string CsvRelPath, IReadOnlyList<long> Rows, long? RowCount = null)
{
    public string FileName => System.IO.Path.GetFileName(CsvRelPath);
}

/// <summary>Flags rows a finding gained in a CSV that is not open (rows already flagged keep their colour).</summary>
internal static class FindingRowFlags
{
    public static void FlagNew(string csvPath, IReadOnlyCollection<long> rows, string flagId)
    {
        if (rows.Count == 0 || !File.Exists(csvPath)) return;
        try
        {
            using var store = RowFlagStore.Open(csvPath);
            var todo = rows.Where(r => !store.IsFlagged(r)).ToList();
            if (todo.Count == 0) return;
            store.SetFlag(todo, flagId);
            store.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error("Could not flag the finding rows of " + csvPath, ex);
        }
    }
}

/// <summary>What the editor needs to offer "Recolour all": the flags to choose from (id, label) and the one it starts on.</summary>
public sealed record FindingFlagChoice(IReadOnlyList<(string Id, string Label)> Flags, string DefaultId);

/// <summary>One line of the Analyze "Key events" side list.</summary>
public sealed partial class FindingListItem : ObservableObject
{
    public FindingListItem(string id, string title, string severity, int rowCount)
    {
        Id = id; _title = title; _severity = severity; _rowCount = rowCount;
    }

    public string Id { get; }
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _severity;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText))] private int _rowCount;
    /// <summary>This key event is the one the grid is filtered to.</summary>
    [ObservableProperty] private bool _isActive;
    public string CountText => RowCount == 1 ? "1 row" : $"{RowCount:N0} rows";
}

/// <summary>
/// Analyze side of key events. Everything is written to the workspace section of the case analysis file only
/// (<see cref="CaseAnalysisStore"/>); the store is re-read before every change so edits made elsewhere are never overwritten.
/// A key event is a bundle of rows from any CSV of the case (stored as case-relative path + row + fingerprint); its rows are always flagged.
/// </summary>
public sealed partial class ResultsViewModel
{
    /// <summary>Key events of the open case (title, severity, row count).</summary>
    public ObservableCollection<FindingListItem> Findings { get; } = new();
    public bool HasFindings => Findings.Count > 0;

    /// <summary>The key event the grid is filtered to (null = none).</summary>
    public string? ActiveFindingId { get; private set; }

    /// <summary>Rows of the active key event in the open file (transient filter, like the evidence rows; never written).</summary>
    private IReadOnlySet<long>? _keyEventRows;

    // gutter marks for the open file: source row -> key event ids, and id -> title
    private Dictionary<long, List<string>> _keyEventRowIds = new();
    private readonly Dictionary<string, string> _keyEventTitles = new();

    // Spectre findings: rows of the open CSV that are evidence for an active Spectre finding, and their finding titles
    private Dictionary<long, List<string>> _spectreRowTitles = new();

    /// <summary>0-based rows of the current CSV that are evidence for any active finding with Origin Spectre.</summary>
    public IReadOnlySet<long> SpectreFlaggedRows { get; private set; } = new HashSet<long>();
    public bool HasSpectreFlagged => SpectreFlaggedRows.Count > 0;
    /// <summary>The "Spectre flagged" quick filter is on.</summary>
    public bool SpectreFilterActive { get; private set; }

    /// <summary>Titles of the Spectre findings holding the row, joined by "; " (empty when none).</summary>
    public string SpectreMarkerTip(long row) =>
        _spectreRowTitles.TryGetValue(row, out var t) ? string.Join("; ", t) : "";

    /// <summary>Shows only the Spectre flagged rows of the open file (in-memory row set, like a finding); again = all rows.</summary>
    public void ToggleSpectreFilter()
    {
        if (SpectreFilterActive) { ClearKeyEventFilter(); return; }
        if (SpectreFlaggedRows.Count == 0) return;
        ClearKeyEventFilter();
        _keyEventRows = SpectreFlaggedRows;
        SpectreFilterActive = true;
        OnPropertyChanged(nameof(SpectreFilterActive));
        NotifyEvidenceRows();
        ApplyNow();
        StatusText = $"Showing the {SpectreFlaggedRows.Count:N0} Spectre flagged rows in {Path.GetFileName(CurrentFile)}.";
    }

    /// <summary>
    /// Asked before rows of a key event are unflagged: the message is "This row is in finding X. Remove it from the finding and unflag it?".
    /// True = Remove and unflag. Tests replace it; the default shows an EzDialog.
    /// </summary>
    public Func<string, bool> UnflagPrompt { get; set; } = message =>
        Dialogs.Confirm("Unflag", message, "Remove and unflag", "Cancel", EzDialogSeverity.Question, defaultNo: true);

    // ------------------------------------------------------------ case root + rows

    /// <summary>
    /// The case root the analysis file belongs to: the open case folder when the CSV is inside it (so bundles span the case's CSVs),
    /// else the CSV's own folder (the store then falls back to the per-user folder when that is not writable).
    /// </summary>
    internal string? AnalysisRoot()
    {
        var csv = CurrentFile;
        if (!string.IsNullOrEmpty(CaseFolder) && (csv == null || IsUnder(csv, CaseFolder))) return CaseFolder;
        return csv == null ? null : Path.GetDirectoryName(csv);
    }

    private string? RelPath(string root, string csv) =>
        IsUnder(csv, root) ? AnalysisRow.Norm(Path.GetRelativePath(root, csv)) : null;

    private List<AnalysisRow>? RowsOf(IEnumerable<long> sources, string root)
    {
        if (CurrentFile == null || RelPath(root, CurrentFile) is not { } rel) return null;
        var fp = _fileFp != null ? AnalysisRow.FingerprintOf(_fileFp) : null;
        return sources.Distinct().OrderBy(r => r).Select(r => new AnalysisRow(rel, r, fp)).ToList();
    }

    private T? WithWorkspace<T>(Func<CaseAnalysis, T> change)
    {
        var root = AnalysisRoot();
        if (root == null) return default;
        try
        {
            var store = CaseAnalysisStore.Open(root);
            if (store.ReadOnlyReason != null) { ShowMessage(store.ReadOnlyReason, InfoSeverity.Warning); return default; }
            var result = change(store.Data);
            store.Save();
            RefreshFindings();
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save key events", ex);
            ShowError("Could not save key events", ex);
            return default;
        }
    }

    // ------------------------------------------------------------ list + gutter

    /// <summary>Reloads the key event list and the gutter marks of the open file from the case analysis file.</summary>
    public void RefreshFindings()
    {
        var root = AnalysisRoot();
        var list = new List<WorkspaceFinding>();
        IReadOnlyList<AnalysisItem> active = Array.Empty<AnalysisItem>();
        if (root != null)
        {
            try
            {
                var data = CaseAnalysisStore.Open(root).Data;
                list = data.Workspace.Findings.ToList();
                active = AnalysisView.ActiveFindings(data);
            }
            catch (Exception ex) { AppLog.Warn("Key events: could not read the case analysis: " + ex.Message); }
        }
        var marks = new Dictionary<long, List<string>>();
        _keyEventTitles.Clear();
        var rel = root != null && CurrentFile != null ? RelPath(root, CurrentFile) : null;
        foreach (var k in list)
        {
            _keyEventTitles[k.Id] = k.Title;
            if (rel == null) continue;
            foreach (var r in k.Rows)
                if (string.Equals(AnalysisRow.Norm(r.CsvRelPath), rel, StringComparison.OrdinalIgnoreCase))
                    (marks.TryGetValue(r.Row, out var ids) ? ids : marks[r.Row] = new()).Add(k.Id);
        }
        var oldMarks = _keyEventRowIds;
        _keyEventRowIds = marks;

        var sp = new Dictionary<long, List<string>>();
        if (rel != null)
            foreach (var it in active.Where(i => i.Origin == ItemOrigin.Spectre))
                foreach (var r in it.SourceRows)
                    if (string.Equals(AnalysisRow.Norm(r.CsvRelPath), rel, StringComparison.OrdinalIgnoreCase))
                    {
                        var t = sp.TryGetValue(r.Row, out var l) ? l : sp[r.Row] = new();
                        if (!t.Contains(it.Title)) t.Add(it.Title);
                    }
        var oldSp = _spectreRowTitles;
        _spectreRowTitles = sp;
        SpectreFlaggedRows = sp.Keys.ToHashSet();
        OnPropertyChanged(nameof(SpectreFlaggedRows));
        OnPropertyChanged(nameof(HasSpectreFlagged));
        if (SpectreFilterActive && sp.Count == 0) ClearKeyEventFilter();

        Findings.Clear();
        foreach (var k in list.OrderBy(k => k.UpdatedAt))
            Findings.Add(new FindingListItem(k.Id, k.Title, k.Severity, k.Rows.Count) { IsActive = k.Id == ActiveFindingId });
        if (ActiveFindingId != null && !_keyEventTitles.ContainsKey(ActiveFindingId)) ClearKeyEventFilter();
        OnPropertyChanged(nameof(HasFindings));
        var changed = oldMarks.Keys.Concat(marks.Keys).Concat(oldSp.Keys).Concat(sp.Keys).Distinct().ToList();
        if (changed.Count > 0) Rows?.NotifyFlagsChanged(changed);
        else Rows?.NotifyFlagsChanged(Array.Empty<long>());
    }

    /// <summary>Titles of the key events holding the row (null when none); for the gutter marker.</summary>
    internal string? KeyEventTextOf(long source) =>
        _keyEventRowIds.TryGetValue(source, out var ids) && ids.Count > 0
            ? string.Join(", ", ids.Select(i => _keyEventTitles.TryGetValue(i, out var t) ? t : i))
            : null;

    // ------------------------------------------------------------ create / add / edit / remove

    /// <summary>The selected rows can be bundled: a CSV is open and its flags are loaded.</summary>
    public bool CanBundleRows => _flags != null && CurrentFile != null && AnalysisRoot() != null;

    /// <summary>Flags the rows that are not flagged yet with "suspicious" (a key event's rows are always flagged).</summary>
    private void FlagForKeyEvent(IReadOnlyList<long> sources)
    {
        if (_flags == null) return;
        var unflagged = sources.Where(r => !_flags.IsFlagged(r)).ToList();
        if (unflagged.Count == 0) return;
        var id = FlagCatalog.IndexOf(S.Settings.Flags, FlagCatalog.Suspicious) >= 0 ? FlagCatalog.Suspicious : EffectiveLastFlagId();
        _flags.SetFlag(unflagged, id);
        AfterFlagEdit(unflagged);
    }

    /// <summary>New key event from the selected rows of the open file (rows from other CSVs are added later with "Add to key event").</summary>
    public FindingListItem? CreateFinding(IReadOnlyCollection<RowItem> rows, KeyEventDraft draft)
    {
        var root = AnalysisRoot();
        if (root == null || _flags == null || rows.Count == 0) return null;
        var src = rows.Select(r => r.Source).Distinct().ToList();
        if (draft.Rows is { Count: > 0 } edited) src = edited.SelectMany(f => f.Rows).Distinct().ToList();   // the editor's row list wins
        if (src.Count == 0) return null;
        var ar = RowsOf(src, root);
        if (ar == null) return null;
        var title = string.IsNullOrWhiteSpace(draft.Title) ? "Finding" : draft.Title.Trim();
        var notes = FindingNotes.Compose(draft.Evidence, draft.Notes);
        var id = WithWorkspace(a => a.AddFinding(title, draft.Severity, draft.Mitre, notes, ar).Id);
        if (id == null) return null;
        FlagForKeyEvent(src);
        if (draft.RecolourFlagId is { } recolour) { _flags.SetFlag(src, recolour); AfterFlagEdit(src); }
        return Findings.FirstOrDefault(k => k.Id == id);
    }

    /// <summary>Adds the selected rows of the open file to an existing key event.</summary>
    public bool AddToFinding(string keyEventId, IReadOnlyCollection<RowItem> rows)
    {
        var root = AnalysisRoot();
        if (root == null || _flags == null || rows.Count == 0) return false;
        var src = rows.Select(r => r.Source).Distinct().ToList();
        var ar = RowsOf(src, root);
        if (ar == null) return false;
        var ok = WithWorkspace(a => a.Workspace.Findings.Any(k => k.Id == keyEventId) && (a.AddRowsToFinding(keyEventId, ar) >= 0));
        if (!ok) return false;
        FlagForKeyEvent(src);
        return true;
    }

    /// <summary>The editor's starting values for an existing key event.</summary>
    public KeyEventDraft? DraftOf(string keyEventId)
    {
        var root = AnalysisRoot();
        if (root == null) return null;
        var k = CaseAnalysisStore.Open(root).Data.Workspace.Findings.FirstOrDefault(e => e.Id == keyEventId);
        if (k == null) return null;
        var (evidence, notes) = FindingNotes.Split(k.Notes);
        return new KeyEventDraft(k.Title, k.Severity, k.Mitre.ToList(), notes, evidence, Rows: FileRowsOf(k, root));
    }

    /// <summary>The finding's rows grouped by CSV (the open file first, with its row count).</summary>
    internal List<FindingFileRows> FileRowsOf(WorkspaceFinding k, string root)
    {
        var open = CurrentFile != null ? RelPath(root, CurrentFile) : null;
        var openKey = open != null ? new RowKey(open, 0).Csv : null;
        return k.Rows.GroupBy(r => r.Key.Csv)
            .Select(g => new FindingFileRows(g.First().CsvRelPath, g.Select(r => r.Row).Distinct().OrderBy(r => r).ToList(),
                openKey == g.Key ? _index?.RowCount : null))
            .OrderByDescending(f => f.RowCount != null).ThenBy(f => f.CsvRelPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The editor's starting rows for a new finding: the selected rows of the open file.</summary>
    public IReadOnlyList<FindingFileRows>? NewFindingRows(IReadOnlyCollection<RowItem> rows)
    {
        var root = AnalysisRoot();
        if (root == null || CurrentFile == null || RelPath(root, CurrentFile) is not { } rel) return null;
        return new[] { new FindingFileRows(rel, rows.Select(r => r.Source).Distinct().OrderBy(r => r).ToList(), _index?.RowCount) };
    }

    /// <summary>The findings (id, title) that hold any of the rows of the open file.</summary>
    public IReadOnlyList<(string Id, string Title)> FindingsOfRows(IEnumerable<long> sources) =>
        sources.SelectMany(s => _keyEventRowIds.TryGetValue(s, out var ids) ? ids : new List<string>()).Distinct()
            .Select(i => (i, _keyEventTitles.TryGetValue(i, out var t) ? t : i)).ToList();

    /// <summary>One evidence line per row (time, host, key columns) to prefill the editor.</summary>
    public string EvidenceTextOf(IReadOnlyCollection<RowItem> rows) =>
        string.Join("\n", rows.OrderBy(r => r.Source).Select(r => FindingNotes.EvidenceLine(Headers, r.Cells)).Where(l => l.Length > 0));

    /// <summary>Set when the rows carry more than one flag colour: the choices for "Recolour all"; null otherwise.</summary>
    public FindingFlagChoice? MixedFlagChoiceOf(IReadOnlyCollection<RowItem> rows)
    {
        if (_flags == null) return null;
        var ids = rows.Select(r => _flags.GetFlagId(r.Source)).Where(i => i != null).Distinct().ToList();
        if (ids.Count < 2) return null;
        return new FindingFlagChoice(FlagOptions.Select(o => (o.Id, o.Label)).ToList(), EffectiveLastFlagId());
    }

    public void UpdateFinding(string keyEventId, KeyEventDraft draft)
    {
        var root = AnalysisRoot();
        var added = new List<(string Rel, List<long> Rows)>();
        WithWorkspace(a =>
        {
            var k = a.Workspace.Findings.FirstOrDefault(e => e.Id == keyEventId);
            if (k == null) return false;
            if (!string.IsNullOrWhiteSpace(draft.Title)) k.Title = draft.Title.Trim();
            k.Severity = draft.Severity; k.Mitre = draft.Mitre.ToList(); k.Notes = FindingNotes.Compose(draft.Evidence, draft.Notes); k.UpdatedAt = DateTimeOffset.UtcNow;
            foreach (var f in draft.Rows ?? Array.Empty<FindingFileRows>())
                added.Add((f.CsvRelPath, a.SetFindingFileRows(keyEventId, f.CsvRelPath, f.Rows)));
            return true;
        });
        if (root != null) FlagAdded(root, added);
    }

    /// <summary>Rows a finding gained in the editor are flagged like "Add to finding" does (the open file through its live flags).</summary>
    private void FlagAdded(string root, IEnumerable<(string Rel, List<long> Rows)> added)
    {
        var open = CurrentFile != null ? RelPath(root, CurrentFile) : null;
        var openKey = open != null ? new RowKey(open, 0).Csv : null;
        var id = FlagCatalog.IndexOf(S.Settings.Flags, FlagCatalog.Suspicious) >= 0 ? FlagCatalog.Suspicious : EffectiveLastFlagId();
        foreach (var (rel, rows) in added)
        {
            if (rows.Count == 0) continue;
            if (openKey == new RowKey(rel, 0).Csv) FlagForKeyEvent(rows);
            else FindingRowFlags.FlagNew(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), rows, id);
        }
    }

    /// <summary>Removes the key event from the workspace. Its rows stay flagged.</summary>
    public void RemoveFinding(string keyEventId)
    {
        if (ActiveFindingId == keyEventId) ClearKeyEventFilter();
        WithWorkspace(a => a.RemoveFinding(keyEventId));
    }

    // ------------------------------------------------------------ unflag guard

    /// <summary>
    /// Call before rows are unflagged. When some are in a key event, asks "This row is in finding X. Remove it from the finding and unflag it?";
    /// on yes the rows leave their key events (an emptied key event is deleted) and true is returned, on no false (nothing changes).
    /// </summary>
    internal bool ConfirmUnflag(IReadOnlyCollection<long> sources)
    {
        if (_flags == null || _keyEventRowIds.Count == 0) return true;
        var hit = sources.Where(r => _keyEventRowIds.ContainsKey(r) && _flags.IsFlagged(r)).Distinct().ToList();
        if (hit.Count == 0) return true;
        var titles = hit.SelectMany(r => _keyEventRowIds[r]).Distinct().Select(i => _keyEventTitles.TryGetValue(i, out var t) ? t : i).ToList();
        var named = titles.Count <= 3 ? string.Join(", ", titles) : string.Join(", ", titles.Take(3)) + "…";
        var msg = hit.Count == 1
            ? $"This row is in finding {named}. Remove it from the finding and unflag it?"
            : $"{hit.Count:N0} of these rows are in findings ({named}). Remove them from the findings and unflag them?";
        if (!UnflagPrompt(msg)) return false;
        var root = AnalysisRoot();
        var rel = root != null && CurrentFile != null ? RelPath(root, CurrentFile) : null;
        if (root == null || rel == null) return true;
        var keys = hit.Select(r => new RowKey(rel, r)).ToList();
        WithWorkspace(a =>
        {
            foreach (var k in a.Workspace.Findings.ToList())
                if (k.Rows.Any(r => keys.Contains(r.Key))) a.RemoveRowsFromFinding(k.Id, keys);
            return true;
        });
        return true;
    }

    // ------------------------------------------------------------ filter the grid to a key event

    private void ClearKeyEventFilter()
    {
        ActiveFindingId = null;
        foreach (var k in Findings) k.IsActive = false;
        if (SpectreFilterActive) { SpectreFilterActive = false; OnPropertyChanged(nameof(SpectreFilterActive)); }
        if (_keyEventRows == null) return;
        _keyEventRows = null;
        NotifyEvidenceRows();
        ApplyNow();
    }

    /// <summary>Show all rows again after a key event filter.</summary>
    public void ShowAllFindingRows() => ClearKeyEventFilter();

    /// <summary>
    /// Click on a key event: filters the grid to its rows with the in-memory row-set filter (same as Open in Analyze). Opens the first CSV
    /// holding rows of it when the open file has none. Clicking the active key event again takes the filter off.
    /// </summary>
    public async Task ToggleFindingFilterAsync(string keyEventId)
    {
        try
        {
            if (ActiveFindingId == keyEventId) { ClearKeyEventFilter(); return; }
            var root = AnalysisRoot();
            if (root == null) return;
            var k = CaseAnalysisStore.Open(root).Data.Workspace.Findings.FirstOrDefault(e => e.Id == keyEventId);
            if (k == null) return;
            var rel = CurrentFile != null ? RelPath(root, CurrentFile) : null;
            bool Here(AnalysisRow r) => rel != null && string.Equals(AnalysisRow.Norm(r.CsvRelPath), rel, StringComparison.OrdinalIgnoreCase);
            if (!k.Rows.Any(Here) && k.Rows.Count > 0)
            {
                var first = AnalysisRow.Norm(k.Rows[0].CsvRelPath);
                var full = Path.GetFullPath(Path.Combine(root, first.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(full)) { ShowMessage($"{first} is not in the case any more.", InfoSeverity.Warning); return; }
                await ShowCsvAsync(root, full);
                rel = first;
                if (!k.Rows.Any(Here)) return;
            }
            var rows = k.Rows.Where(Here).Select(r => r.Row).ToHashSet();
            var others = k.Rows.Count - rows.Count;
            _keyEventRows = rows;
            SpectreFilterActive = false;
            ActiveFindingId = keyEventId;
            foreach (var i in Findings) i.IsActive = i.Id == keyEventId;
            NotifyEvidenceRows();
            ApplyNow();
            StatusText = $"Showing the {rows.Count:N0} rows of key event '{k.Title}' in {Path.GetFileName(CurrentFile)}" + (others > 0 ? $" ({others:N0} more in other files)." : ".");
        }
        catch (Exception ex) { ShowError("Could not show the key event's rows", ex); }
    }
}
