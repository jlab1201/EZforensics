using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>A column in the pivot group panel (ordered; each level has its own group sort).</summary>
public sealed partial class PivotGroupChip : ObservableObject
{
    public PivotGroupChip(string column, PivotSort sort)
    {
        Column = column;
        _sort = sort;
    }

    public string Column { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortGlyph), nameof(SortToolTip))]
    private PivotSort _sort;

    public string AutomationId => "Results.Pivot.Chip." + Column;

    /// <summary>▲ ascending / ▼ descending; "#" prefix when the groups are ordered by row count.</summary>
    public string SortGlyph => (Sort.IsCount() ? "#" : "") + (Sort.IsAscending() ? "▲" : "▼");

    public string SortToolTip => "Group order: " + Describe(Sort) + " - click to flip the direction, right-click to sort by value or by count";

    public static PivotSort Next(PivotSort s) => s switch
    {
        PivotSort.ValueAsc => PivotSort.ValueDesc,
        PivotSort.ValueDesc => PivotSort.CountDesc,
        PivotSort.CountDesc => PivotSort.CountAsc,
        _ => PivotSort.ValueAsc,
    };

    private static string Describe(PivotSort s) => s switch
    {
        PivotSort.ValueDesc => "value, descending (Z→A)",
        PivotSort.CountDesc => "count, largest first",
        PivotSort.CountAsc => "count, smallest first",
        _ => "value, ascending (A→Z)",
    };
}

/// <summary>
/// Pivot (Timeline Explorer style grouping): the group panel's columns, grouped mode of the grid (group header lines
/// interleaved with data rows), collapse state and the per-group actions. Grouping runs after filter + sort.
/// </summary>
public sealed partial class ResultsViewModel
{
    private IReadOnlyList<PivotNode>? _pivotRoots;
    private int[]? _pivotMap;
    private int[]? _pivotOrder;
    // filtered + sorted map of the last completed filter run (null = every row in file order) so a chip change only regroups
    private int[]? _pivotBase;
    private bool _pivotBaseValid;
    private int _flattenGen;
    // set when grouping was applied / a level was added / a state without a stored collapse set was restored: the next grouping starts fully collapsed
    private bool _collapseAllPending;
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);

    /// <summary>Group panel columns, outermost level first.</summary>
    public ObservableCollection<PivotGroupChip> GroupColumns { get; } = new();

    public bool IsGrouped => GroupColumns.Count > 0;

    /// <summary>Left padding of data rows in grouped mode (16 px per group level), applied in the flag column.</summary>
    public System.Windows.Thickness PivotRowIndent => new(16 * GroupColumns.Count, 0, 0, 0);

    /// <summary>Raised after the grid lines were rebuilt by a collapse / expand (argument: key of the toggled group, or null).</summary>
    public event Action<string?>? PivotReflattened;

    public bool IsGroupColumn(string column) => GroupColumns.Any(c => string.Equals(c.Column, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a column to the group panel (at <paramref name="index"/>, else last); an already grouped column moves there.</summary>
    public void AddGroupColumn(string column, int index = -1)
    {
        if (_index == null || !Headers.Contains(column)) return;
        var existing = GroupColumns.FirstOrDefault(c => string.Equals(c.Column, column, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (index >= 0) MoveGroupColumn(GroupColumns.IndexOf(existing), index);
            return;
        }
        var chip = new PivotGroupChip(column, PivotSort.ValueAsc);
        if (index < 0 || index > GroupColumns.Count) GroupColumns.Add(chip); else GroupColumns.Insert(index, chip);
        OnGroupingChanged(structural: true);
    }

    public void MoveGroupColumn(int from, int to)
    {
        if ((uint)from >= (uint)GroupColumns.Count) return;
        to = Math.Clamp(to, 0, GroupColumns.Count - 1);
        if (from == to) return;
        GroupColumns.Move(from, to);
        OnGroupingChanged(structural: true);
    }

    [RelayCommand]
    private void RemoveGroupColumn(PivotGroupChip? chip)
    {
        if (chip == null || !GroupColumns.Remove(chip)) return;
        OnGroupingChanged(structural: true);
    }

    /// <summary>Chip click: flips the group order of that level (value A→Z ↔ Z→A, count ↔ count).</summary>
    [RelayCommand]
    private void ToggleGroupSort(PivotGroupChip? chip)
    {
        if (chip == null) return;
        chip.Sort = chip.Sort.Flip();
        OnGroupingChanged(structural: false);
    }

    /// <summary>Chip menu: order the groups of that level by value or by row count (direction kept).</summary>
    public void SetGroupSortKey(PivotGroupChip? chip, bool byCount)
    {
        if (chip == null || chip.Sort.IsCount() == byCount) return;
        chip.Sort = chip.Sort.WithKey(byCount);
        OnGroupingChanged(structural: false);
    }

    [RelayCommand]
    private void ClearGrouping()
    {
        if (GroupColumns.Count == 0) return;
        GroupColumns.Clear();
        OnGroupingChanged(structural: true);
    }

    [RelayCommand]
    private void ExpandAllGroups()
    {
        if (_collapsedGroups.Count == 0) return;
        _collapsedGroups.Clear();
        RememberViewCollapse();
        MarkSessionDirty();
        _ = ReflattenAsync(null);
    }

    [RelayCommand]
    private void CollapseAllGroups()
    {
        if (_pivotRoots == null) return;
        foreach (var n in AllGroups(_pivotRoots)) _collapsedGroups.Add(n.Key);
        RememberViewCollapse();
        MarkSessionDirty();
        _ = ReflattenAsync(null);
    }

    /// <summary>Collapses or expands one group (chevron, double-click, Left / Right); re-flattens without regrouping.</summary>
    public Task ToggleGroupAsync(GroupRowItem? g, bool? collapse = null)
    {
        if (g == null || _pivotRoots == null) return Task.CompletedTask;
        bool now = _collapsedGroups.Contains(g.Node.Key);
        bool want = collapse ?? !now;
        if (want == now) return Task.CompletedTask;
        if (want) _collapsedGroups.Add(g.Node.Key); else _collapsedGroups.Remove(g.Node.Key);
        RememberViewCollapse();
        MarkSessionDirty();
        return ReflattenAsync(g.Node.Key);
    }

    /// <summary>"Filter to this group": an Equals condition per level of the group's path (blank value → Is empty).</summary>
    public void FilterToGroup(PivotNode node)
    {
        var path = _pivotRoots == null ? null : FindPath(_pivotRoots, node);
        if (path == null || path.Count == 0) return;
        EditFilters(() =>
        {
            foreach (var n in path)
            {
                var cell = CellFor(n.Column);
                if (cell == null) continue;
                var op = n.Value.Length == 0 ? ColumnFilterMode.IsEmpty : ColumnFilterMode.Equals;
                var value = n.Value.Length == 0 ? "" : n.Value;
                if (cell.BuildCondition() == null && !cell.HasError) cell.Set(op, value);
                else Extra.Children.Add(new FilterCondition(n.Column, op, value));
            }
        });
    }

    private void OnGroupingChanged(bool structural)
    {
        if (structural) { _collapsedGroups.Clear(); _collapseAllPending = IsGrouped; }
        if (!IsGrouped) { _pivotRoots = null; _pivotMap = null; _pivotOrder = null; }
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(PivotRowIndent));
        MarkSessionDirty();
        _ = ApplyPivotAsync();
    }

    /// <summary>Regroups the last filter result (or re-runs the filter when none is complete yet).</summary>
    private async Task ApplyPivotAsync()
    {
        var idx = _index;
        var rows = Rows;
        if (idx == null || rows == null) return;
        if (!_pivotBaseValid) { await ApplyFilterAsync(); return; }
        if (!IsGrouped)
        {
            _filterCts?.Cancel();
            if (_pivotBase == null) { rows.ShowAll(idx.RowCount); _lastShownCount = idx.RowCount; }
            else rows.ShowMap(_pivotBase);
            UpdateStatus();
            return;
        }
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        try
        {
            IsFiltering = true;
            UpdateStatus();
            await GroupAndShowAsync(idx, rows, _pivotBase ?? AllRows(idx.RowCount), new Progress<double>(p => FilterProgress = p), cts);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Grouping failed", ex); }
        finally
        {
            if (_filterCts == cts) { IsFiltering = false; FilterProgress = 0; }
            UpdateStatus();
        }
    }

    private static int[] AllRows(long count)
    {
        var a = new int[(int)Math.Min(int.MaxValue, count)];
        for (int i = 0; i < a.Length; i++) a[i] = i;
        return a;
    }

    /// <summary>Groups <paramref name="map"/> by the panel columns, flattens with the collapse state and shows it (all off the UI thread).</summary>
    private async Task GroupAndShowAsync(CsvIndex idx, VirtualRowList rows, int[] map, IProgress<double> progress, CancellationTokenSource cts)
    {
        var specs = GroupColumns.Where(c => idx.ColumnIndex(c.Column) >= 0)
            .Select(c => new PivotGroupSpec { Column = c.Column, Sort = c.Sort }).ToList();
        if (specs.Count == 0) { rows.ShowMap(map); return; }
        var roots = await PivotEngine.GroupAsync(idx, map, specs, progress, cts.Token);
        if (_collapseAllPending)
        {
            foreach (var k in PivotEngine.AllKeys(roots)) _collapsedGroups.Add(k);
            _collapseAllPending = false;
        }
        var collapsed = new HashSet<string>(_collapsedGroups, StringComparer.Ordinal);
        var (lines, order) = await Task.Run(() => (PivotEngine.Flatten(roots, collapsed), GroupOrder(roots, map.Length)), cts.Token);
        if (cts.IsCancellationRequested || idx != _index || rows != Rows) return;
        _pivotRoots = roots;
        _pivotMap = map;
        _pivotOrder = order;
        _flattenGen++;
        rows.IsCollapsedKey = k => _collapsedGroups.Contains(k);
        rows.ShowGrouped(map, lines, order);
    }

    private async Task ReflattenAsync(string? key)
    {
        var rows = Rows;
        var roots = _pivotRoots;
        var map = _pivotMap;
        var order = _pivotOrder;
        if (rows == null || roots == null || map == null || order == null) return;
        int gen = ++_flattenGen;
        var collapsed = new HashSet<string>(_collapsedGroups, StringComparer.Ordinal);
        var lines = map.Length > 200_000 ? await Task.Run(() => PivotEngine.Flatten(roots, collapsed)) : PivotEngine.Flatten(roots, collapsed);
        if (gen != _flattenGen || rows != Rows || roots != _pivotRoots) return;
        rows.IsCollapsedKey = k => _collapsedGroups.Contains(k);
        rows.ShowGrouped(map, lines, order);
        UpdateStatus();
        PivotReflattened?.Invoke(key);
    }

    /// <summary>Every data row in group order (leaf groups' rows concatenated).</summary>
    private static int[] GroupOrder(IReadOnlyList<PivotNode> roots, int total)
    {
        var a = new int[total];
        int k = 0;
        void Walk(PivotNode n)
        {
            if (n.Children.Count > 0) { foreach (var c in n.Children) Walk(c); return; }
            Array.Copy(n.Rows, 0, a, k, n.Rows.Length);
            k += n.Rows.Length;
        }
        foreach (var r in roots) Walk(r);
        return k == total ? a : a[..k];
    }

    private static IEnumerable<PivotNode> AllGroups(IReadOnlyList<PivotNode> roots)
    {
        var stack = new Stack<PivotNode>(roots);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.Children) stack.Push(c);
        }
    }

    /// <summary>Group path from the root down to <paramref name="target"/> (null when not in the tree).</summary>
    private static List<PivotNode>? FindPath(IReadOnlyList<PivotNode> nodes, PivotNode target)
    {
        foreach (var n in nodes)
        {
            if (ReferenceEquals(n, target)) return new List<PivotNode> { n };
            if (n.Depth < target.Depth && n.Children.Count > 0 && FindPath(n.Children, target) is { } sub)
            {
                sub.Insert(0, n);
                return sub;
            }
        }
        return null;
    }

    /// <summary>Status text in grouped mode: "N groups · M rows".</summary>
    private string? PivotStatus(long total)
    {
        if (Rows is not { IsGrouped: true } || _pivotRoots == null || _pivotOrder == null) return null;
        int m = _pivotOrder.Length;
        return $"{_pivotRoots.Count:N0} groups  ·  " + (m < total ? $"{m:N0} of {total:N0} rows" : $"{m:N0} rows");
    }

    private void ResetPivotForNewFile()
    {
        GroupColumns.Clear();
        _collapsedGroups.Clear();
        _collapseAllPending = false;
        _pivotRoots = null; _pivotMap = null; _pivotOrder = null;
        _pivotBase = null; _pivotBaseValid = false;
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(PivotRowIndent));
    }

    private void CapturePivot(AnalyzeFileState f)
    {
        f.GroupColumns.AddRange(GroupColumns.Select(c => new PivotGroupSpec { Column = c.Column, Sort = c.Sort }));
        f.CollapsedGroups.AddRange(_collapsedGroups.Order(StringComparer.Ordinal));
    }

    /// <summary>Restores a stored collapse set; without one every group starts collapsed.</summary>
    internal void SeedCollapsed(IEnumerable<string>? saved)
    {
        _collapsedGroups.Clear();
        if (GroupColumns.Count == 0) { _collapseAllPending = false; return; }
        if (saved != null) foreach (var k in saved) _collapsedGroups.Add(k);
        _collapseAllPending = _collapsedGroups.Count == 0;
    }

    private void RestorePivot(AnalyzeFileState st)
    {
        GroupColumns.Clear();
        _collapsedGroups.Clear();
        foreach (var g in st.GroupColumns)
            if (Headers.Contains(g.Column) && !IsGroupColumn(g.Column)) GroupColumns.Add(new PivotGroupChip(g.Column, g.Sort));
        SeedCollapsed(st.CollapsedGroups);
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(PivotRowIndent));
    }
}
