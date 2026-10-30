using System.Windows.Threading;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;
using EzForensics.App.Services;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Analyze session persistence: the open case and file, focus mode and, per file, the filters, search, flag filter, sort,
/// column order, selected row and scroll position (<see cref="AnalyzeSessionStore"/>, <c>analyze-session.json</c>).
/// Column widths and hidden columns are kept in the settings by the view (ColumnLayouts); flags in their own sidecar (RowFlagStore).
/// Restoring only sets the view state; it never queues jobs or re-runs tools.
/// </summary>
public sealed partial class ResultsViewModel
{
    private Task<AnalyzeSessionState>? _sessionLoad;
    private AnalyzeSessionState? _session;
    private DispatcherTimer? _sessionTimer;
    private CsvFingerprint? _fileFp;
    private bool _sessionRestoreStarted;
    // view state reported by EventsPane (column order, selected row, scroll); ignored while a saved view is still to be applied
    private int[] _viewOrder = Array.Empty<int>();
    private int _viewSelPos = -1;
    private long _viewSelSource = -1;
    private int _viewScroll;
    private AnalyzeFileState? _pendingView;

    /// <summary>Raised once the rows of a reopened file are shown, so the view applies column order, selection and scroll position.</summary>
    public event Action<AnalyzeFileState>? SessionViewRestoreRequested;

    private Task<AnalyzeSessionState> EnsureSessionAsync() => _sessionLoad ??= Task.Run(() =>
    {
        var s = AnalyzeSessionStore.Load();
        try
        {
            foreach (var gone in s.PruneMissing()) AppLog.Info("Analyze session: dropped the saved view of a missing file: " + gone);
        }
        catch (Exception ex) { AppLog.Warn("Analyze session: could not check the saved files: " + ex.Message); }
        return s;
    });

    private void HookSession()
    {
        S.FocusModeChanged += _ => MarkSessionDirty();
    }

    /// <summary>First visit of the page: loads the saved session (off the UI thread) and reopens the last case and file.</summary>
    private async Task RestoreLastSessionAsync()
    {
        try
        {
            var st = await EnsureSessionAsync();
            _session = st;
            var folder = st.CaseFolder;
            if (CaseFolder != null || string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                if (CaseFolder == null) await InitDefaultCaseAsync();
                return;
            }
            if (st.FocusMode) S.SetFocusMode(true);
            CaseFolder = folder;
            await RefreshTreeAsync();
            var file = st.SelectedFile;
            if (CurrentFile == null && !string.IsNullOrEmpty(file) && File.Exists(file))
            {
                var open = OpenCsvAsync(file);
                RevealFile(file);
                await open;
            }
            AppLog.Info($"Analyze session restored: case {folder}" + (CurrentFile != null ? ", file " + Path.GetFileName(CurrentFile) : ""));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not restore the Analyze session", ex);
            if (CaseFolder == null) await InitDefaultCaseAsync();
        }
    }

    /// <summary>Called by the view when column order, selection or scroll position change.</summary>
    public void NoteViewState(int[] columnOrder, int selectedPosition, long selectedSource, int scrollOffset)
    {
        if (_index == null || Rows == null || _pendingView != null) return;
        _viewOrder = columnOrder;
        _viewSelPos = selectedPosition;
        _viewSelSource = selectedSource;
        _viewScroll = scrollOffset;
        MarkSessionDirty();
    }

    /// <summary>The view applied the saved column order / selection / scroll position.</summary>
    public void ViewRestoreDone() => _pendingView = null;

    /// <summary>Saves in about 2 s unless something changes again (filters, sort, view, selected file, focus mode).</summary>
    public void MarkSessionDirty()
    {
        if (_session == null) return;
        if (_sessionTimer == null)
        {
            _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _sessionTimer.Tick += (_, _) => { _sessionTimer.Stop(); SaveSessionNow(); };
        }
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private AnalyzeFileState? CaptureFile()
    {
        var path = CurrentFile;
        var fp = _fileFp;
        if (path == null || fp == null || _index == null || Rows == null) return null;
        var f = new AnalyzeFileState
        {
            Path = path, Size = fp.Size, LastWriteUtc = fp.LastWriteUtc, HeadSha256 = fp.HeadSha256, SavedUtc = DateTime.UtcNow,
            GlobalText = GlobalText ?? "", SavedFilterName = ActiveSavedFilter, SavedViewName = ActiveSavedView, FiltersEnabled = FiltersEnabled, FlagFilter = FlagFilter,
            SortColumn = SortColumn, SortDescending = SortDescending,
        };
        f.FlagIds.AddRange(SelectedFlagIds);
        foreach (var c in Cells)
        {
            bool rowActive = c.Value.Length > 0 || c.Op is ColumnFilterMode.IsEmpty or ColumnFilterMode.IsNotEmpty;
            if (!rowActive && c.HeaderFilter == null) continue;
            f.Cells.Add(new AnalyzeCellFilter
            {
                Column = c.Column, Op = c.Op, Value = rowActive ? c.Value : "",
                Header = c.HeaderFilter != null ? AnalyzeFilterNode.From(c.HeaderFilter) : null,
            });
        }
        if (Extra.Children.Count > 0) f.Extra = AnalyzeFilterNode.From(Extra);
        var src = _pendingView; // a saved view not yet applied wins over the (default) live one
        f.ColumnOrder.AddRange(src?.ColumnOrder ?? _viewOrder.ToList());
        f.SelectedPosition = src?.SelectedPosition ?? _viewSelPos;
        f.SelectedSource = src?.SelectedSource ?? _viewSelSource;
        f.ScrollOffset = src?.ScrollOffset ?? _viewScroll;
        CapturePivot(f);
        return f;
    }

    private void StashCurrentFile()
    {
        if (_session == null) return;
        try { if (CaptureFile() is { } f) _session.Put(f); }
        catch (Exception ex) { AppLog.Warn("Analyze session: could not capture the view: " + ex.Message); }
    }

    /// <summary>Writes the session now (file switch, app exit, Windows logoff / shutdown, crash). No-op until the Analyze page was opened once.</summary>
    public void SaveSessionNow()
    {
        var s = _session;
        if (s == null) return;
        _sessionTimer?.Stop();
        try
        {
            StashCurrentFile();
            if (!string.IsNullOrEmpty(CaseFolder)) s.CaseFolder = CaseFolder;
            if (CurrentFile != null) s.SelectedFile = CurrentFile;
            s.FocusMode = S.IsFocusMode;
            AnalyzeSessionStore.Save(s);
        }
        catch (Exception ex) { AppLog.Error("Could not save the Analyze session", ex); }
    }

    /// <summary>Sets the filters, search, flag filter and sort of a reopened file (no filtering is started here).</summary>
    private void RestoreFileState(AnalyzeFileState st)
    {
        _suspendApply = true;
        try
        {
            if (_autoFilledGlobalText == null || GlobalText != _autoFilledGlobalText) GlobalText = st.GlobalText ?? "";
            FiltersEnabled = st.FiltersEnabled;
            ActiveSavedFilter = st.SavedFilterName;
            ActiveSavedView = st.SavedViewName;
            foreach (var cs in st.Cells)
            {
                var cell = CellFor(cs.Column);
                if (cell == null) continue;
                if (cs.Value.Length > 0 || cs.Op is ColumnFilterMode.IsEmpty or ColumnFilterMode.IsNotEmpty) cell.LoadRaw(cs.Op, cs.Value);
                if (cs.Header?.ToNode() is { } hn) cell.LoadHeader(hn);
            }
            if (st.Extra?.ToNode() is FilterGroup g) Extra = g;
            else if (st.Extra?.ToNode() is { } single) Extra = new FilterGroup(FilterGroupKind.And, new[] { single });
            var ids = st.FlagIds.ToHashSet();
            _syncingFlagOptions = true;
            try
            {
                foreach (var o in FlagOptions) o.IsSelected = ids.Contains(o.Id);
                FlagFilter = st.FlagFilter;
            }
            finally { _syncingFlagOptions = false; }
            UpdateFlagFilterText();
            if (st.SortColumn != null && Headers.Contains(st.SortColumn))
            {
                SortColumn = st.SortColumn;
                SortDescending = st.SortDescending;
            }
            RestorePivot(st);
        }
        finally { _suspendApply = false; }
    }
}
