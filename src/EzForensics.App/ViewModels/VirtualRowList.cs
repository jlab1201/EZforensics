using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>A lightweight row handed to the DataGrid; cells are read lazily through the CsvIndex page cache.</summary>
public sealed class RowItem : INotifyPropertyChanged
{
    private readonly VirtualRowList _owner;
    private string[]? _cells;

    internal RowItem(VirtualRowList owner, int position, long source, int version)
    {
        _owner = owner;
        Position = position;
        Source = source;
        Version = version;
    }

    public int Position { get; }
    /// <summary>Always false (group header lines are <see cref="GroupRowItem"/>); lets the row style tell them apart.</summary>
    public bool IsGroupHeader => false;
    /// <summary>Row index in the CSV file (0 = first data row).</summary>
    public long Source { get; }
    internal int Version { get; }

    public string[] Cells => _cells ??= _owner.ReadSource(Source);

    /// <summary>1-based file row number (for display).</summary>
    public long RowNumber => Source + 1;

    /// <summary>Flag state (looked up in the owner's flag set; keyed on <see cref="Source"/>).</summary>
    public bool IsFlagged => _owner.IsFlagged?.Invoke(Source) == true;

    /// <summary>Stable id of the row's flag (null when not flagged).</summary>
    public string? FlagId => _owner.FlagIdOf?.Invoke(Source);

    /// <summary>Colour of the row's flag (null when not flagged).</summary>
    public System.Windows.Media.Brush? FlagBrush => FlagId is { } id ? FlagBrushes.For(ResultsViewModel.FlagColorHex(id)) : null;

    /// <summary>Light tint of the row's flag colour (null when not flagged).</summary>
    public System.Windows.Media.Brush? FlagTintBrush => FlagId is { } id ? FlagBrushes.TintFor(ResultsViewModel.FlagColorHex(id)) : null;

    /// <summary>Label of the row's flag, for the flag cell tooltip and UIA ("" when not flagged).</summary>
    public string FlagText => FlagId is { } id ? ResultsViewModel.FlagLabel(id) : "";

    /// <summary>Tooltip of the flag cell: the flag label (or how to flag).</summary>
    public string FlagToolTip => FlagId is { } id
        ? $"{ResultsViewModel.FlagLabel(id)}\nClick to cycle flags. Right-click: choose a flag. Ctrl+1..9 flag the selected rows, Ctrl+0 clears, Space toggles."
        : "Click to flag. Right-click: choose a flag. Ctrl+1..9 flag the selected rows, Ctrl+0 clears, Space toggles.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaiseFlagChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFlagged)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlagId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlagBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlagTintBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlagText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlagToolTip)));
    }

    public string this[int column]
    {
        get
        {
            var c = Cells;
            return (uint)column < (uint)c.Length ? c[column] : "";
        }
    }
}

/// <summary>A pivot group header line in grouped mode ("EventId: 4624 (1,532)"); not a CSV row.</summary>
public sealed class GroupRowItem
{
    internal GroupRowItem(PivotNode node, int position, int depth, int version)
    {
        Node = node;
        Position = position;
        Depth = depth;
        Version = version;
    }

    public PivotNode Node { get; }
    public int Position { get; }
    public int Depth { get; }
    internal int Version { get; }
    public bool IsGroupHeader => true;
    public bool IsFlagged => false;
    public string Column => Node.Column;
    /// <summary>Group value for display ("(blank)" for empty cells).</summary>
    public string ValueText => Node.Value.Length == 0 ? "(blank)" : Node.Value.Replace('\r', ' ').Replace('\n', ' ');
    public string CountText => $"(Count = {Node.Count:N0})";
    public string Text => $"{Column}: {ValueText} {CountText}";
    /// <summary>Left indentation (16 px per level).</summary>
    public System.Windows.Thickness Indent => new(4 + 16 * Depth, 0, 0, 0);
    /// <summary>Set by the owner when the group is collapsed.</summary>
    public bool IsCollapsed { get; internal set; }
    /// <summary>Chevron: right when collapsed, down when expanded (Segoe Fluent Icons).</summary>
    public string Chevron => IsCollapsed ? "" : "";
    /// <summary>Cell bindings never fail on header lines (they have no cells, but copy may still ask).</summary>
    public string this[int column] => "";
}

/// <summary>
/// IList + INotifyCollectionChanged view over a CsvIndex (optionally through a filtered/sorted index map).
/// WPF's ListCollectionView uses the indexer and Count only, so the DataGrid stays fully virtualized.
/// </summary>
public sealed class VirtualRowList : IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private const int MaxCachedItems = 4000;
    private readonly CsvIndex _idx;
    private readonly Dictionary<int, object> _items = new();
    private List<PivotLine>? _lines;
    private int[]? _groupOrder;
    private int[]? _map;
    private int _count;
    private int _version;

    public VirtualRowList(CsvIndex idx)
    {
        _idx = idx;
        _count = (int)Math.Min(int.MaxValue, idx.RowCount);
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public CsvIndex Index => _idx;
    /// <summary>Flag lookup by file row index (a HashSet lookup; no re-indexing).</summary>
    public Func<long, bool>? IsFlagged { get; set; }
    /// <summary>Collapsed-group lookup by <see cref="PivotNode.Key"/> (grouped mode).</summary>
    public Func<string, bool>? IsCollapsedKey { get; set; }
    /// <summary>Flag id lookup by file row index.</summary>
    public Func<long, string?>? FlagIdOf { get; set; }

    /// <summary>Refreshes the flag glyph of realised rows (all of them when <paramref name="sources"/> is null).</summary>
    public void NotifyFlagsChanged(IReadOnlyCollection<long>? sources = null)
    {
        HashSet<long>? set = sources == null ? null : new HashSet<long>(sources);
        foreach (var it in _items.Values.OfType<RowItem>().ToList())
            if (set == null || set.Contains(it.Source)) it.RaiseFlagChanged();
    }

    /// <summary>Grouped (pivot) mode: the view shows group header lines and data rows.</summary>
    public bool IsGrouped => _lines != null;
    /// <summary>Flattened pivot lines in grouped mode (null otherwise).</summary>
    public IReadOnlyList<PivotLine>? Lines => _lines;

    /// <summary>Line of the group header with <paramref name="key"/> (-1 when not shown).</summary>
    public int PositionOfGroup(string key)
    {
        var lines = _lines;
        if (lines == null) return -1;
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Node is { } n && n.Key == key) return i;
        return -1;
    }
    /// <summary>Current filtered/sorted map, or null when showing every row in file order.</summary>
    public int[]? Map => _map;
    public int Count => _count;

    /// <summary>Show all rows in file order (count grows while indexing).</summary>
    public void ShowAll(long rowCount)
    {
        _map = null;
        _lines = null;
        _groupOrder = null;
        _count = (int)Math.Min(int.MaxValue, rowCount);
        RaiseReset();
    }

    public void ShowMap(int[] map)
    {
        _map = map;
        _lines = null;
        _groupOrder = null;
        _count = map.Length;
        RaiseReset();
    }

    /// <summary>
    /// Grouped mode: <paramref name="map"/> is the filtered/sorted row map, <paramref name="lines"/> the flattened pivot (headers + visible rows)
    /// and <paramref name="groupOrder"/> every data row in group order (collapsed groups included; used by export and copy).
    /// </summary>
    public void ShowGrouped(int[] map, List<PivotLine> lines, int[] groupOrder)
    {
        _map = map;
        _lines = lines;
        _groupOrder = groupOrder;
        _count = lines.Count;
        RaiseReset();
    }

    /// <summary>Indices of all data rows in display order (group order when grouped; materialises 0..n-1 when unfiltered).</summary>
    public int[] CurrentIndices()
    {
        if (_groupOrder != null) return _groupOrder;
        if (_map != null) return _map;
        var a = new int[_count];
        for (int i = 0; i < a.Length; i++) a[i] = i;
        return a;
    }

    internal string[] ReadSource(long source)
    {
        try { return source < _idx.RowCount ? _idx.ReadRow(source) : Array.Empty<string>(); }
        catch (ObjectDisposedException) { return Array.Empty<string>(); }
    }

    private void RaiseReset()
    {
        _version++;
        _items.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public object? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            if (_items.TryGetValue(index, out var it)) return it;
            if (_items.Count > MaxCachedItems) _items.Clear();
            if (_lines != null && _lines[index] is { Node: { } node } line)
                it = new GroupRowItem(node, index, line.Depth, _version) { IsCollapsed = IsCollapsedKey?.Invoke(node.Key) == true };
            else
            {
                long src = _lines != null ? _lines[index].Row : _map != null ? _map[index] : index;
                it = new RowItem(this, index, src, _version);
            }
            _items[index] = it;
            return it;
        }
        set => throw new NotSupportedException();
    }

    public int IndexOf(object? value) => value switch
    {
        RowItem r when r.Version == _version && r.Position < _count => r.Position,
        GroupRowItem g when g.Version == _version && g.Position < _count => g.Position,
        _ => -1,
    };

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }

    public bool IsReadOnly => true;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < _count; i++) array.SetValue(this[i], index + i);
    }
}
