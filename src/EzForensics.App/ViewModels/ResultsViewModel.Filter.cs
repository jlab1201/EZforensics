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
    // ------------------------------------------------------------ filters

    partial void OnRowsChanged(VirtualRowList? value) { OnPropertyChanged(nameof(HasFile)); OnPropertyChanged(nameof(GroupBarVisible)); RefreshSavedFilterState(); RefreshSavedViewState(); }
    partial void OnHeadersChanged(IReadOnlyList<string> value) { RefreshSavedFilterState(); RefreshSavedViewState(); }
    partial void OnGlobalTextChanged(string value)
    {
        ValidateSearch(value);
        OnPropertyChanged(nameof(HasAnyFilter));
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(CanSaveFilter));
        Debounce();
    }

    private string _lastValidGlobalText = "";
    /// <summary>Syntax error of the search box text (null when it parses); the grid keeps the last valid result meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchError))]
    private string? _searchError;

    public bool HasSearchError => SearchError != null;

    private void ValidateSearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || SearchQuery.TryParse(text, out _, out var err)) { _lastValidGlobalText = text ?? ""; SearchError = null; }
        else SearchError = err ?? "Invalid search";
    }
    partial void OnFiltersEnabledChanged(bool value) => ApplyNow();
    partial void OnFilterDisplayTextChanged(string value) { OnPropertyChanged(nameof(HasAnyFilter)); OnPropertyChanged(nameof(CanSaveFilter)); }

    private void Debounce()
    {
        if (_suspendApply) return;
        RefreshFilterState();
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Applies at once (operator changes, menu filters, popups).</summary>
    public void ApplyNow()
    {
        if (_suspendApply) return;
        _debounce.Stop();
        RefreshFilterState();
        _ = ApplyFilterAsync();
    }

    /// <summary>Runs <paramref name="edit"/> on the filter state and applies once.</summary>
    public void EditFilters(Action edit)
    {
        _suspendApply = true;
        try { edit(); }
        finally { _suspendApply = false; }
        ApplyNow();
    }

    public ColumnFilterCell? CellFor(string column) =>
        Cells.FirstOrDefault(c => string.Equals(c.Column, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>Context menu "Filter: …": goes into the column's filter-row cell when it is free, else stacks (AND) in <see cref="Extra"/>.</summary>
    public void AddCellFilter(string column, ColumnFilterMode op, string value)
    {
        var cell = CellFor(column);
        if (cell == null) return;
        EditFilters(() =>
        {
            if (cell.BuildCondition() == null && !cell.HasError) cell.Set(op, value);
            else Extra.Children.Add(new FilterCondition(column, op, value));
        });
    }

    public void ClearColumnFilters(string column)
    {
        EditFilters(() =>
        {
            CellFor(column)?.ResetSilently();
            Extra.RemoveColumn(column);
        });
    }

    [RelayCommand]
    private void ClearFilters()
    {
        EditFilters(() =>
        {
            foreach (var c in Cells) c.ResetSilently();
            Extra = new FilterGroup();
            GlobalText = "";
            FlagFilter = null;
            _evidenceRows = null; _keyEventRows = null; ActiveFindingId = null; foreach (var ke in Findings) ke.IsActive = false; NotifyEvidenceRows();
            FiltersEnabled = true;
            ActiveSavedFilter = null;
        });
    }

    /// <summary>Clears only the search box text (other filters stay).</summary>
    [RelayCommand]
    private void ClearSearch() => GlobalText = "";

    [RelayCommand]
    private void CancelFilter() => _filterCts?.Cancel();

    /// <summary>Filter row, funnel filters and <see cref="Extra"/> as one And group.</summary>
    public FilterGroup BuildExpression()
    {
        var root = new FilterGroup();
        foreach (var c in Cells)
        {
            var cond = c.BuildCondition();
            if (cond != null) root.Children.Add(cond);
            if (c.HeaderFilter != null) root.Children.Add(c.HeaderFilter.Clone());
        }
        foreach (var n in Extra.Children) root.Children.Add(n.Clone());
        return root;
    }

    private void LoadExpression(FilterGroup root)
    {
        foreach (var c in Cells) c.ResetSilently();
        Extra = new FilterGroup();
        var items = root is { Kind: FilterGroupKind.And, Negate: false } ? root.Children : new List<FilterNode> { root };
        foreach (var n in items)
        {
            if (n.IsEmpty) continue;
            if (n is FilterCondition fc && fc.Column != FilterNode.AnyColumn && CellFor(fc.Column) is { } cell && cell.BuildCondition() == null)
                cell.Load(fc);
            else if (SetColumn(n) is { } col && CellFor(col) is { HeaderFilter: null } hc)
                hc.LoadHeader(n);
            else Extra.Children.Add(n);
        }
}

    /// <summary>Column of a funnel-style node (value set, date set, or Not(value set)), else null.</summary>
    private static string? SetColumn(FilterNode n) => n switch
    {
        FilterValueSet v => v.Column,
        FilterDateSet d => d.Column,
        FilterGroup { Negate: true, Children: [FilterValueSet v] } => v.Column,
        _ => null,
    };

    private FilterSpec BuildSpec(bool ignoreEnabled = false)
    {
        if (!FiltersEnabled && !ignoreEnabled) return new FilterSpec();
        var spec = new FilterSpec
        {
            GlobalText = string.IsNullOrWhiteSpace(_lastValidGlobalText) ? null : _lastValidGlobalText.Trim(),
            Expression = BuildExpression(),
            Flag = FlagFilter switch { true => FlagFilterMode.Flagged, false => FlagFilterMode.Unflagged, _ => FlagFilterMode.All },
            RowSet = _keyEventRows ?? _evidenceRows,
        };
        if (spec.Flag != FlagFilterMode.All)
        {
            spec.FlagColorRows = _flags?.SnapshotFlags() ?? new Dictionary<long, string>();
            if (spec.Flag == FlagFilterMode.Flagged && SelectedFlagIds is { Count: > 0 } ids) spec.FlagColors = ids;
        }
        return spec;
    }

    /// <summary>Updates the filter panel text and the per-column "filtered" funnels.</summary>
    private void RefreshFilterState()
    {
        var spec = BuildSpec(ignoreEnabled: true);
        MarkSessionDirty();
        FilterDisplayText = spec.ToDisplayString();
        foreach (var c in Cells)
            c.IsFiltered = c.BuildCondition() != null || c.HeaderFilter != null || Extra.References(c.Column);
    }

    private bool IsFilterEmpty() => BuildSpec().IsEmpty;

    private string? _warnedUnknownColumns;

    /// <summary>
    /// Warns (once per distinct set) about typed filter conditions on columns this CSV lacks: such a condition matches nothing. A saved filter has its own
    /// warning (<see cref="WarnMissingColumns"/>).
    /// </summary>
    private void WarnUnknownColumns(FilterSpec spec)
    {
        var missing = ActiveSavedFilter != null || spec.Expression == null ? new List<string>()
            : SavedFilterStore.Columns(spec.Expression).Where(c => !Headers.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var key = string.Join("|", missing);
        if (key == _warnedUnknownColumns) return;
        _warnedUnknownColumns = key;
        if (missing.Count > 0)
            ShowMessage($"Filter on columns not in this CSV matches nothing: {string.Join(", ", missing)}", InfoSeverity.Warning);
    }

    private async Task ApplyFilterAsync()
    {
        var idx = _index;
        var rows = Rows;
        if (idx == null || rows == null) return;
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        _pivotBaseValid = false;
        FilterSpec spec;
        try { spec = BuildSpec(); }
        catch (Exception ex) { ShowError("Invalid filter", ex); return; }
        WarnUnknownColumns(spec);
        try
        {
            bool plain = spec.IsEmpty && SortColumn == null;
            if (plain && !IsGrouped)
            {
                rows.ShowAll(idx.RowCount);
                _lastShownCount = idx.RowCount;
                _pivotBase = null; _pivotBaseValid = true;
                UpdateStatus();
                return;
            }
            IsFiltering = true;
            UpdateStatus();
            var progress = new Progress<double>(p => FilterProgress = p);
            var map = plain ? AllRows(idx.RowCount) : await FilterEngine.FilterAsync(idx, spec, progress, cts.Token);
            if (SortColumn != null && idx.ColumnIndex(SortColumn) >= 0)
                map = await FilterEngine.SortAsync(idx, map, SortColumn, SortDescending, cts.Token, progress);
            if (cts.IsCancellationRequested || idx != _index) return;
            _pivotBase = plain ? null : map;
            _pivotBaseValid = true;
            if (IsGrouped) await GroupAndShowAsync(idx, rows, map, progress, cts);
            else if (plain) rows.ShowAll(idx.RowCount);
            else rows.ShowMap(map);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Filter failed", ex); }
        finally
        {
            if (_filterCts == cts) { IsFiltering = false; FilterProgress = 0; }
            UpdateStatus();
        }
    }

    /// <summary>Called by the view when a column header is clicked.</summary>
    public async Task SortAsync(string column, bool descending)
    {
        SortColumn = column;
        SortDescending = descending;
        MarkSessionDirty();
        await ApplyFilterAsync();
    }
}
