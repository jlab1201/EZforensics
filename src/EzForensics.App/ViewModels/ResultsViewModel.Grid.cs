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

public sealed partial class ResultsViewModel
{
    // ------------------------------------------------------------ open / index

    /// <summary>Shows <paramref name="caseDir"/> in the tree and opens <paramref name="csvPath"/> (used by other pages).</summary>
    /// <summary>
    /// "Open in Analyze" from a Spectre finding: opens the file, waits for it to be indexed, and selects the row whose record-id column
    /// (EventRecordId, RecordNumber, EntryNumber...) equals <paramref name="recordId"/>. The status line says when the record was not found.
    /// </summary>
    public async Task ShowRecordAsync(string caseDir, string csvPath, string recordId)
    {
        try
        {
            await ShowCsvAsync(caseDir, csvPath);
            var idx = _index;
            if (idx == null || !string.Equals(CurrentFile, csvPath, StringComparison.OrdinalIgnoreCase)) return;
            try { await idx.IndexingCompleted.WaitAsync(TimeSpan.FromMinutes(2)); }
            catch (TimeoutException) { StatusText = $"The file is still being indexed; record {recordId} was not looked up."; return; }
            catch (OperationCanceledException) { return; }
            if (!ReferenceEquals(idx, _index)) return;
            long? row;
            try { row = await Task.Run(() => Core.Spectre.FindingsEvidence.FindRecordRow(idx, recordId)); }
            catch (ObjectDisposedException) { return; }   // another file was opened meanwhile
            if (!ReferenceEquals(idx, _index)) return;
            var name = Path.GetFileName(csvPath);
            if (row == null) { StatusText = $"Record {recordId} was not found in {name} (no matching record-id column value); the file is open."; return; }
            var err = ((IAssistantGridTarget)this).GoToRow(row.Value);
            StatusText = err == null ? $"Record {recordId} of {name} (row {row.Value}), from a Spectre finding." : $"Record {recordId} is row {row.Value} of {name}: {err}";
        }
        catch (Exception ex) { ShowError("Could not open the finding's record", ex); }
    }

    public async Task ShowCsvAsync(string caseDir, string csvPath)
    {
        _treeLoadedOnce = true;
        var open = OpenCsvAsync(csvPath); // sets CurrentFile before its first await
        if (!string.Equals(caseDir, CaseFolder, StringComparison.OrdinalIgnoreCase) || Tree.Count == 0)
        {
            CaseFolder = caseDir;
            await RefreshTreeAsync();
        }
        else if (!RevealFile(csvPath))
            await RefreshTreeAsync(); // a new file (e.g. a fresh super timeline) is not in the tree yet
        RevealFile(csvPath);
        _ = RevalidateStatsAsync(); // e.g. a super timeline rebuilt under the same name
        await open;
    }

    public async Task OpenCsvAsync(string path)
    {
        PidLinkHost = null;
        _openCts?.Cancel();
        _filterCts?.Cancel();
        StashCurrentFile(); // the view of the file being left (filters, sort, selection, scroll) is kept for when it is opened again
        // the next file starts with an empty search / flag filter (its own saved view, if any, is restored once it is open)
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
        }
        finally { _suspendApply = false; }
        _evidenceRows = null; _keyEventRows = null; ActiveFindingId = null; NotifyEvidenceRows();
        _pendingView = null;
        _viewOrder = Array.Empty<int>(); _viewSelPos = -1; _viewSelSource = -1; _viewScroll = 0;
        _fileFp = null;
        var cts = _openCts = new CancellationTokenSource();
        _indexTimer.Stop();
        var old = _index;
        _index = null;
        Rows = null;
        Headers = Array.Empty<string>();
        Cells.Clear();
        ColumnsChanged?.Invoke();
        old?.Dispose();
        CloseFlags();
        ClearAdvisories();

        CurrentFile = path;
        StatusText = "Opening…";
        Banner = null;
        try
        {
            var idx = await CsvIndex.OpenAsync(path, null, cts.Token);
            if (cts.IsCancellationRequested) { idx.Dispose(); return; }
            // saved view of this file (loaded off the UI thread; dropped when the file changed since)
            AnalyzeFileState? saved = null;
            string? dropped = null;
            try
            {
                var session = await EnsureSessionAsync();
                _session ??= session;
                var fp = await Task.Run(() => CsvFingerprint.Compute(path));
                if (cts.IsCancellationRequested) { idx.Dispose(); return; }
                _fileFp = fp;
                saved = session.TryGet(path, fp, out dropped);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Warn("Analyze session: could not look up the saved view: " + ex.Message); }
            if (dropped != null) AppLog.Info("Analyze session: dropped the saved view, " + dropped);
            RowFlagStore? flags = null;
            try { flags = await Task.Run(() => RowFlagStore.Open(path)); }
            catch (Exception ex) { AppLog.Error("Could not load row flags", ex); }
            if (cts.IsCancellationRequested) { idx.Dispose(); flags?.Dispose(); return; }
            _index = idx;
            AttachFlags(flags);
            RefreshFindings();
            SortColumn = null;
            SortDescending = false;
            ActiveSavedFilter = null;
            ActiveSavedView = null;
            ResetPivotForNewFile();
            Headers = idx.Headers;
            Extra = new FilterGroup();
            for (int i = 0; i < idx.Headers.Count; i++)
                Cells.Add(new ColumnFilterCell(idx.Headers[i], i, Debounce, ApplyNow));
            if (saved != null)
            {
                try { RestoreFileState(saved); _pendingView = saved; }
                catch (Exception ex) { AppLog.Warn("Analyze session: could not restore the view of " + Path.GetFileName(path) + ": " + ex.Message); }
            }
            _lastShownCount = idx.RowCount;
            Rows = new VirtualRowList(idx) { IsFlagged = r => _flags?.IsFlagged(r) == true, FlagIdOf = r => _flags?.GetFlagId(r), KeyEventOf = KeyEventTextOf, SpectreOf = r => _spectreRowTitles.ContainsKey(r) ? SpectreMarkerTip(r) : null };
            StartAdvisories(path, idx.Headers);
            UpdatePidLinks(path, idx.Headers);
            ColumnsChanged?.Invoke();
            IsIndexing = !idx.IndexingCompleted.IsCompleted;
            UpdateStatus();
            RefreshFilterState();
            _indexTimer.Start();
            _ = WatchIndexingAsync(idx);
            if (!IsFilterEmpty() || SortColumn != null || IsGrouped) _ = ApplyFilterAsync();
            if (flags?.StaleWarning is { } w) ShowMessage(w, InfoSeverity.Warning);
            else if (dropped != null) ShowMessage("The saved view of this file (filters, sort, position) was dropped because the file changed.", InfoSeverity.Info);
            MarkSessionDirty();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Could not open the file.";
            ShowError($"Could not open {Path.GetFileName(path)}", ex);
        }
    }

    private async Task WatchIndexingAsync(CsvIndex idx)
    {
        try { await idx.IndexingCompleted; }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (idx == _index) ShowError("Indexing failed (rows indexed so far are still viewable)", ex);
        }
        if (idx != _index) return;
        IsIndexing = false;
        OnIndexTick(force: true);
        _indexTimer.Stop();
        if (!IsFilterEmpty() || SortColumn != null || IsGrouped) await ApplyFilterAsync(); // cover rows indexed after the last filter
        if (idx == _index && _pendingView is { } pv) SessionViewRestoreRequested?.Invoke(pv);
    }

    private void OnIndexTick(bool force = false)
    {
        var idx = _index;
        if (idx == null || Rows == null) return;
        IndexProgress = idx.FileLength == 0 ? 1 : (double)idx.BytesIndexed / idx.FileLength;
        long count = idx.RowCount;
        // Grow the unfiltered view at most once a second (each grow is a Reset for the grid).
        if (Rows.Map == null && count != _lastShownCount && (force || DateTime.UtcNow - _lastGrow > TimeSpan.FromSeconds(1)))
        {
            _lastShownCount = count;
            _lastGrow = DateTime.UtcNow;
            Rows.ShowAll(count);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var idx = _index;
        if (idx == null || Rows == null) return;
        var total = idx.RowCount;
        var s = PivotStatus(total) ?? (Rows.Map == null ? $"{Rows.Count:N0} rows" : $"{Rows.Count:N0} of {total:N0} rows");
        if (IsIndexing) s += $"  ·  indexing {IndexProgress:P0}";
        if (IsFiltering) s += "  ·  filtering…";
        if (!FiltersEnabled && HasAnyFilter) s += "  ·  filters disabled";
        StatusText = s;
    }

    // ------------------------------------------------------------ flags

    private void AttachFlags(RowFlagStore? flags)
    {
        _flags = flags;
        if (flags != null) { flags.DefaultFlagId = EffectiveLastFlagId(); flags.Changed += OnFlagStoreChanged; }
        FlaggedCount = flags?.Count ?? 0;
        RefreshFlagCounts();
    }

    private void CloseFlags()
    {
        var f = _flags;
        _flags = null;
        FlaggedCount = 0;
        RefreshFlagCounts();
        if (f == null) return;
        f.Changed -= OnFlagStoreChanged;
        try { f.Dispose(); } catch (Exception ex) { AppLog.Error("Could not save row flags", ex); }
    }

    /// <summary>Writes pending flag changes (app exit). False when the save failed (already logged).</summary>
    public bool FlushFlags()
    {
        try { _flags?.Flush(); return true; }
        catch (Exception ex) { AppLog.Error("Could not save row flags", ex); return false; }
    }

    private void OnFlagStoreChanged() => AppServices.OnUi(() =>
    {
        FlaggedCount = _flags?.Count ?? 0;
        RefreshFlagCounts();
        if (_flags?.LastSaveError is { } err) ShowError("Could not save flags", err);
    });

    partial void OnFlaggedCountChanged(int value) => OnPropertyChanged(nameof(FlaggedText));

    /// <summary>Flag sidecar path in use (for the tooltip).</summary>
    public string? FlagsPath => _flags?.SidecarPath;

    public bool IsFlagged(long sourceRow) => _flags?.IsFlagged(sourceRow) == true;

    public string? FlagIdOf(long sourceRow) => _flags?.GetFlagId(sourceRow);

    /// <summary>
    /// Cycles the clicked row's colour (<see cref="FlagCatalog.Next"/>). When the row is part of a multi-row <paramref name="selection"/>,
    /// the clicked row's next colour is applied to every selected row. A colour (not a clear) becomes the new default colour.
    /// </summary>
    public void CycleFlag(RowItem? clicked, IReadOnlyCollection<RowItem>? selection)
    {
        if (clicked == null || _flags == null) return;
        var next = FlagCatalog.Next(S.Settings.Flags, _flags.GetFlagId(clicked.Source));
        IReadOnlyCollection<RowItem> targets = selection is { Count: > 1 } && selection.Any(r => r.Source == clicked.Source)
            ? selection
            : new[] { clicked };
        SetFlagId(targets, next);
    }

    /// <summary>Gives the rows a flag (null = clear the flag). A flag becomes the new default for clicks and Space.</summary>
    public void SetFlagId(IReadOnlyCollection<RowItem> rows, string? flagId)
    {
        if (_flags == null || rows.Count == 0) return;
        if (flagId == null && !ConfirmUnflag(rows.Select(r => r.Source).ToList())) return;
        if (flagId is { } c && S.Settings.FlagLastId != c)
        {
            S.Settings.FlagLastId = c;
            _flags.DefaultFlagId = c;
            OnPropertyChanged(nameof(LastFlagLabel));
            try { S.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
        }
        var src = rows.Select(r => r.Source).Distinct().ToList();
        _flags.SetFlag(src, flagId);
        AfterFlagEdit(src);
    }

    /// <summary>Label of a flag id from Settings (the id itself when the flag was removed).</summary>
    public static string FlagLabel(string id) => FlagCatalog.LabelOf(S.Settings.Flags, id);

    /// <summary>Colour ("#RRGGBB") of a flag id from Settings (grey when the flag was removed).</summary>
    public static string FlagColorHex(string id) => FlagCatalog.Resolve(S.Settings.Flags, id).Color;

    /// <summary>The last picked flag id, or the default / first flag when it was removed in Settings.</summary>
    public static string EffectiveLastFlagId()
    {
        var list = S.Settings.Flags;
        var last = S.Settings.FlagLastId;
        if (FlagCatalog.IndexOf(list, last) >= 0) return last;
        return FlagCatalog.IndexOf(list, FlagCatalog.DefaultId) >= 0 ? FlagCatalog.DefaultId : list[0].Id;
    }

    /// <summary>Rebuilds the flag list (menus, filter dropdown) from Settings; keeps the ticked filters that still exist.</summary>
    private void RebuildFlagOptions()
    {
        var selected = FlagOptions.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet();
        _syncingFlagOptions = true;
        try
        {
            foreach (var o in FlagOptions) o.PropertyChanged -= OnFlagOptionChanged;
            FlagOptions.Clear();
            int n = 0;
            foreach (var d in S.Settings.Flags)
            {
                var o = new FlagOption(d, ++n) { IsSelected = selected.Contains(d.Id) };
                o.PropertyChanged += OnFlagOptionChanged;
                FlagOptions.Add(o);
            }
        }
        finally { _syncingFlagOptions = false; }
        if (_flags != null) _flags.DefaultFlagId = EffectiveLastFlagId();
        OnPropertyChanged(nameof(LastFlagLabel));
        RefreshFlagCounts();
        Rows?.NotifyFlagsChanged();
    }

    private void RefreshFlagCounts()
    {
        var counts = _flags?.Counts();
        foreach (var o in FlagOptions) o.Count = counts != null && counts.TryGetValue(o.Id, out var n) ? n : 0;
        UpdateFlagFilterText();
    }

    private bool _syncingFlagOptions;

    private void OnFlagOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FlagOption.IsSelected) || _syncingFlagOptions) return;
        // ticking colours = "flagged with one of these colours"; unticking the last one = all rows
        bool any = FlagOptions.Any(o => o.IsSelected);
        if (any && FlagFilter != true) { _syncingFlagOptions = true; try { FlagFilter = true; } finally { _syncingFlagOptions = false; } }
        else if (!any && FlagFilter == true) { _syncingFlagOptions = true; try { FlagFilter = null; } finally { _syncingFlagOptions = false; } }
        UpdateFlagFilterText();
        ApplyNow();
    }

    /// <summary>Selected flags (ids) of the flag filter (empty = any flag).</summary>
    public IReadOnlySet<string> SelectedFlagIds => FlagOptions.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet();

    /// <summary>Flag filter dropdown: "All rows" (null), "All flagged" (true), "Unflagged" (false); clears the colour choice.</summary>
    public void SetFlagMode(bool? mode)
    {
        _syncingFlagOptions = true;
        try { foreach (var o in FlagOptions) o.IsSelected = false; }
        finally { _syncingFlagOptions = false; }
        if (FlagFilter == mode) { UpdateFlagFilterText(); ApplyNow(); }
        else FlagFilter = mode;
    }

    /// <summary>Flag filter dropdown: only rows with exactly this flag.</summary>
    public void SetFlagFilter(string flagId)
    {
        _syncingFlagOptions = true;
        try { foreach (var o in FlagOptions) o.IsSelected = o.Id == flagId; FlagFilter = true; }
        finally { _syncingFlagOptions = false; }
        UpdateFlagFilterText();
        ApplyNow();
    }

    private void UpdateFlagFilterText()
    {
        var sel = FlagOptions.Where(o => o.IsSelected).ToList();
        FlagFilterText = FlagFilter switch
        {
            true when sel.Count > 0 => string.Join(", ", sel.Select(o => o.Label)) + $"  ({sel.Sum(o => o.Count):N0})",
            true => $"All flagged  ({FlaggedCount:N0})",
            false => "Not flagged",
            _ => "All rows",
        };
    }

    /// <summary>Flags (or unflags) the given rows. With <paramref name="flag"/> null: flag all unless all are flagged.</summary>
    public void SetFlags(IReadOnlyCollection<RowItem> rows, bool? flag)
    {
        if (_flags == null || rows.Count == 0) return;
        var src = rows.Select(r => r.Source).Distinct().ToList();
        bool value = flag ?? !src.All(_flags.IsFlagged);
        if (!value && !ConfirmUnflag(src)) return;
        _flags.DefaultFlagId = EffectiveLastFlagId();
        _flags.SetMany(src, value);
        AfterFlagEdit(src);
    }

    /// <summary>
    /// Space / Ctrl+F2: rotates the rows through every flag (<see cref="FlagCatalog.RotateNext"/>), starting from the last used flag, then clears.
    /// The <paramref name="anchor"/> row (the current row, else the first selected one) decides the next flag for all rows. The rotation
    /// does not change the last used flag, so every new row starts from the same colour.
    /// </summary>
    public void RotateFlags(IReadOnlyCollection<RowItem> rows, RowItem? anchor)
    {
        if (_flags == null || rows.Count == 0) return;
        var a = anchor != null && rows.Any(r => r.Source == anchor.Source) ? anchor : rows.First();
        var next = FlagCatalog.RotateNext(S.Settings.Flags, _flags.GetFlagId(a.Source), EffectiveLastFlagId());
        var src = rows.Select(r => r.Source).Distinct().ToList();
        if (next == null && !ConfirmUnflag(src)) return;
        _flags.SetFlag(src, next);
        AfterFlagEdit(src);
    }

    private void AfterFlagEdit(IReadOnlyCollection<long> sources)
    {
        FlaggedCount = _flags?.Count ?? 0;
        RefreshFlagCounts();
        Rows?.NotifyFlagsChanged(sources);
        FlagsChanged?.Invoke();
        OnPropertyChanged(nameof(FlagsPath));
        if (FlagFilter != null && FiltersEnabled) Debounce();
    }

    partial void OnFlagFilterChanged(bool? value)
    {
        OnPropertyChanged(nameof(HasAnyFilter));
        if (!_syncingFlagOptions && value != true)
        {
            _syncingFlagOptions = true;
            try { foreach (var o in FlagOptions) o.IsSelected = false; }
            finally { _syncingFlagOptions = false; }
        }
        UpdateFlagFilterText();
        if (!_syncingFlagOptions) ApplyNow();
    }

    // ------------------------------------------------------------ actions

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!await WaitForIndexingBeforeExportAsync()) return;
        var rows = Rows;
        if (rows == null) return;
        await ExportRowsAsync(rows.CurrentIndices(), "_filtered", "Export filtered rows");
    }

    [RelayCommand]
    private async Task ExportFlaggedAsync()
    {
        if (!await WaitForIndexingBeforeExportAsync()) return;
        var idx = _index;
        if (idx == null || _flags == null) return;
        var indices = _flags.ToSortedArray().Where(r => r < idx.RowCount).Select(r => (int)r).ToArray();
        if (indices.Length == 0)
        {
            ShowMessage("No rows are flagged.", InfoSeverity.Warning);
            return;
        }
        await ExportRowsAsync(indices, "_flagged", "Export flagged rows");
    }

    /// <summary>
    /// While the file is still being indexed the grid only covers the rows read so far, so an export would silently be partial.
    /// Waits for indexing to finish, then re-runs the filter/sort over the complete index. False = do not export.
    /// </summary>
    private async Task<bool> WaitForIndexingBeforeExportAsync()
    {
        var idx = _index;
        if (idx == null) return false;
        if (!IsIndexing && idx.IndexingCompleted.IsCompleted) return true;
        StatusText = "Waiting for indexing to finish…";
        try { await idx.IndexingCompleted; }
        catch (Exception)
        {
            if (idx == _index) ShowMessage("Indexing did not finish, so the export was cancelled (it would have been missing rows).", InfoSeverity.Warning);
            return false;
        }
        if (idx != _index) return false;
        await ApplyFilterAsync(); // filter/sort/group results cover only the rows indexed so far
        return idx == _index;
    }

    private async Task ExportRowsAsync(int[] indices, string suffix, string title)
    {
        var idx = _index;
        if (idx == null) return;
        var name = Path.GetFileNameWithoutExtension(idx.Path) + suffix + ".csv";
        var target = Dialogs.SaveFile(title, "CSV files|*.csv", name, Path.GetDirectoryName(idx.Path));
        if (target == null) return;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(idx.Path), StringComparison.OrdinalIgnoreCase))
        {
            ShowMessage("Choose a different file name; the source CSV cannot be overwritten while it is open.", InfoSeverity.Warning);
            return;
        }
        await GuardAsync(async () =>
        {
            IsFiltering = true;
            StatusText = "Exporting…";
            // the flag label goes into an extra first column whenever the file has flags
            var flagIds = _flags?.SnapshotFlags();
            var extra = flagIds is { Count: > 0 } ? ExportExtraColumn.FlagColumn(flagIds, idx.Headers, FlagLabel) : null;
            await CsvExporter.ExportAsync(idx, indices, target, new Progress<double>(p => FilterProgress = p), default, extra);
            ShowMessage($"Exported {indices.Length:N0} rows to {target}" + (extra != null ? $" (with a {extra.Header} label column)" : ""), InfoSeverity.Success);
        }, "Export failed");
        IsFiltering = false;
        FilterProgress = 0;
        UpdateStatus();
    }

    [RelayCommand]
    private void OpenInDefaultApp()
    {
        Guard(() => ShellOps.OpenFile(CurrentFile ?? throw new InvalidOperationException("Open a CSV first.")), "Could not open the file");
    }

    [RelayCommand]
    private void OpenFolder() => Guard(() => ShellOps.OpenFolder(CurrentFile ?? CaseFolder), "Could not open the folder");

    public void ReportCopyError(Exception ex) => ShowError("Could not copy", ex);

    // ------------------------------------------------------------ copy rows (tab-separated with a header row)

    /// <summary>Above this many rows a copy asks first.</summary>
    public const int CopyWarnRows = 100_000;
    /// <summary>Clipboard text cap (characters); a larger copy stops at the last row that fits.</summary>
    public const int CopyMaxChars = 64_000_000;

    /// <summary>Grid Ctrl+C: the selected cells as tab-separated rows (no header), cleaned like the row copy.</summary>
    public void CopySelectedCells(IReadOnlyList<RowItem> rows, IReadOnlyList<int> columns)
    {
        int n = TsvFormatter.BuildCells(rows.Select(r => (Func<int, string?>)(i => r[i])), columns, CopyWarnRows, out var text, S.Settings.EscapeFormulasOnCopy);
        System.Windows.Clipboard.SetDataObject(text, true);
        if (n < rows.Count) ShowMessage($"Copied the first {n:N0} of {rows.Count:N0} selected rows (limit). Use Export for the rest.", InfoSeverity.Warning);
    }

    public Task CopySelectedRowsAsync(IReadOnlyCollection<RowItem> rows, IReadOnlyList<int> columns) =>
        CopyRowsAsync(rows.OrderBy(r => r.Position).Select(r => r.Source).Distinct().ToArray(), columns, "selected");

    public Task CopyAllFilteredRowsAsync(IReadOnlyList<int> columns)
    {
        var rows = Rows;
        if (rows == null) return Task.CompletedTask;
        return CopyRowsAsync(rows.CurrentIndices().Select(i => (long)i).ToArray(), columns, "filtered");
    }

    public Task CopyFlaggedRowsAsync(IReadOnlyList<int> columns)
    {
        var idx = _index;
        if (idx == null || _flags == null) return Task.CompletedTask;
        var src = _flags.ToSortedArray().Where(r => r < idx.RowCount).ToArray();
        if (src.Length == 0) { ShowMessage("No rows are flagged.", InfoSeverity.Warning); return Task.CompletedTask; }
        return CopyRowsAsync(src, columns, "flagged");
    }

    private async Task CopyRowsAsync(long[] sources, IReadOnlyList<int> columns, string what)
    {
        var idx = _index;
        if (idx == null || sources.Length == 0 || columns.Count == 0) return;
        if (sources.Length > CopyWarnRows)
        {
            if (!EzForensics.App.Services.Dialogs.Confirm($"Copy {sources.Length:N0} rows?", "This can be slow and use a lot of memory. Export is better for this many.", "Copy", "Cancel", EzForensics.App.Views.EzDialogSeverity.Warning)) return;
        }
        string text = "";
        int written = 0;
        bool complete = true;
        await GuardAsync(async () =>
        {
            StatusText = "Copying…";
            var headers = idx.Headers;
            bool escape = S.Settings.EscapeFormulasOnCopy;
            bool fitted = true;
            (text, written, fitted) = await Task.Run(() =>
            {
                bool ok = TsvFormatter.Build(headers, columns, ReadRows(idx, sources), CopyMaxChars, out var t, out var n, escapeFormulas: escape);
                return (t, n, ok);
            });
            // ReadRows stops early when the file is closed (index disposed) during the copy: then fewer rows than requested were written.
            complete = fitted && written == sources.Length;
            System.Windows.Clipboard.SetDataObject(text, true);
            if (complete) ShowMessage($"Copied {written:N0} {what} rows with a header row.", InfoSeverity.Success);
            else if (!fitted) ShowMessage($"Copied the first {written:N0} of {sources.Length:N0} {what} rows (clipboard size limit). Use Export for the rest.", InfoSeverity.Warning);
            else ShowMessage($"Copy incomplete: only {written:N0} of {sources.Length:N0} {what} rows were copied because the file was closed during the copy. Open it again and repeat the copy.", InfoSeverity.Warning);
        }, "Could not copy");
        UpdateStatus();
    }

    private static IEnumerable<string[]> ReadRows(CsvIndex idx, long[] sources)
    {
        foreach (var s in sources)
        {
            string[] row;
            try { row = s < idx.RowCount ? idx.ReadRow(s) : Array.Empty<string>(); }
            catch (ObjectDisposedException) { yield break; }
            yield return row;
        }
    }
}
