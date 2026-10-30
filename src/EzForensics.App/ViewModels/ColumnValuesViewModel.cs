using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

public sealed partial class ValueItem : ObservableObject
{
    private readonly ColumnValuesViewModel _owner;

    public ValueItem(ColumnValuesViewModel owner, string value, long count)
    {
        _owner = owner;
        Value = value;
        Count = count;
    }

    public string Value { get; }
    public long Count { get; }
    public string Display => Value.Length > 200 ? Value[..200] + "…" : Value.Replace("\r", " ").Replace("\n", " ");
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);

    public bool IsChecked
    {
        get => _owner.IsValueChecked(Value);
        set { _owner.SetValueChecked(Value, value); OnPropertyChanged(); }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>Year / month / day node of the date tree (tri-state).</summary>
public sealed partial class DateNode : ObservableObject
{
    private bool? _isChecked = true;

    public DateNode(string label, DateNode? parent, DateOnly? day = null, long count = 0)
    {
        Label = label;
        Parent = parent;
        Day = day;
        Count = count;
    }

    public string Label { get; }
    public DateNode? Parent { get; }
    public DateOnly? Day { get; }
    public long Count { get; set; }
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
    public ObservableCollection<DateNode> Children { get; } = new();
    public bool IsExpanded { get; set; }

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            // clicking an indeterminate box checks it
            var v = value ?? true;
            SetDown(v);
            Parent?.Recompute();
        }
    }

    private void SetDown(bool v)
    {
        _isChecked = v;
        OnPropertyChanged(nameof(IsChecked));
        foreach (var c in Children) c.SetDown(v);
    }

    internal void Recompute()
    {
        bool all = Children.All(c => c._isChecked == true), none = Children.All(c => c._isChecked == false);
        _isChecked = all ? true : none ? false : null;
        OnPropertyChanged(nameof(IsChecked));
        Parent?.Recompute();
    }

    internal void SetLeaf(bool v) { _isChecked = v; OnPropertyChanged(nameof(IsChecked)); }

    public IEnumerable<DateNode> Leaves() => Children.Count == 0 ? new[] { this } : Children.SelectMany(c => c.Leaves());
}

public sealed partial class CustomConditionViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue), nameof(IsBetween))]
    private FilterOpInfo _op = FilterOps.All[0];
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _value2 = "";

    public bool NeedsValue => Op.NeedsValue;
    public bool IsBetween => Op.Op == ColumnFilterMode.Between;

    public FilterCondition? Build(string column)
    {
        var c = new FilterCondition(column, Op.Op, Value, IsBetween ? Value2 : null);
        return c.IsEmpty ? null : c;
    }

    public void Load(FilterCondition c)
    {
        Op = FilterOps.Get(c.Op);
        Value = c.Value;
        Value2 = c.Value2 ?? "";
    }
}

/// <summary>
/// Header funnel popup: checklist of distinct values with counts ((Select All), (Blanks), (Non blanks), search),
/// a year &gt; month &gt; day tree for timestamp columns, and a two-condition custom filter. Counting runs in the background and is cancelled on close.
/// Checklist state is "base + exceptions", so unchecking values in a truncated (top 10k) list excludes exactly those values.
/// </summary>
public sealed partial class ColumnValuesViewModel : ObservableObject, IDisposable
{
    private readonly ResultsViewModel _owner;
    private readonly CsvIndex _idx;
    private readonly ColumnFilterCell _cell;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly HashSet<string> _exceptions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _searchCts;
    private bool _baseChecked = true;
    private bool _blanksChecked = true;
    private bool _truncated;
    private DistinctValueResult? _full;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string? _note;
    [ObservableProperty] private long _blankCount;
    [ObservableProperty] private long _nonBlankCount;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _datesLoading;
    [ObservableProperty] private bool _dateBlanksChecked = true;
    [ObservableProperty] private long _dateBlankCount;
    [ObservableProperty] private bool _customIsOr;

    public ColumnValuesViewModel(ResultsViewModel owner, CsvIndex idx, ColumnFilterCell cell)
    {
        _owner = owner;
        _idx = idx;
        _cell = cell;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is ValueItem v && (SearchText.Length == 0 || v.Value.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = OnSearchAsync(); };
        IsDateColumn = cell.IsDateColumn;
        LoadState(cell.HeaderFilter);
    }

    public string Column => _cell.Column;
    public bool IsDateColumn { get; }
    public ObservableCollection<ValueItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<DateNode> Years { get; } = new();
    public CustomConditionViewModel Custom1 { get; } = new();
    public CustomConditionViewModel Custom2 { get; } = new();
    public IReadOnlyList<FilterOpInfo> Ops => FilterOps.All;

    public int CustomJoinIndex
    {
        get => CustomIsOr ? 1 : 0;
        set => CustomIsOr = value == 1;
    }

    partial void OnCustomIsOrChanged(bool value) => OnPropertyChanged(nameof(CustomJoinIndex));

    /// <summary>Raised when the popup should close.</summary>
    public event Action? CloseRequested;

    private void LoadState(FilterNode? f)
    {
        switch (f)
        {
            case FilterValueSet vs:
                _baseChecked = false;
                foreach (var v in vs.Values) _exceptions.Add(v);
                _blanksChecked = vs.IncludeBlanks;
                break;
            case FilterGroup { Negate: true, Children: [FilterValueSet nv] }:
                _baseChecked = true;
                foreach (var v in nv.Values) _exceptions.Add(v);
                _blanksChecked = !nv.IncludeBlanks;
                break;
            case FilterCondition { Op: ColumnFilterMode.IsNotEmpty }:
                _blanksChecked = false;
                break;
            case FilterDateSet:
                SelectedTab = 1;
                break;
            case FilterCondition c:
                Custom1.Load(c);
                SelectedTab = 2;
                break;
            case FilterGroup { Negate: false } g when g.Children.Count is 1 or 2 && g.Children.All(x => x is FilterCondition):
                Custom1.Load((FilterCondition)g.Children[0]);
                if (g.Children.Count == 2) Custom2.Load((FilterCondition)g.Children[1]);
                CustomIsOr = g.Kind == FilterGroupKind.Or;
                SelectedTab = 2;
                break;
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            var r = await DistinctValueCounter.CountAsync(_idx, Column, DistinctValueCounter.DefaultMaxValues, null,
                new Progress<double>(p => Progress = p), _cts.Token);
            _full = r;
            _truncated = r.Truncated;
            BlankCount = r.BlankCount;
            NonBlankCount = r.NonBlankCount;
            ShowValues(r);
            Note = r.Truncated
                ? $"Showing the {r.Values.Count:N0} most frequent of {(r.CountsApproximate ? "more than " : "")}{r.DistinctCount:N0} values. Search to find others."
                : $"{r.DistinctCount:N0} distinct values";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note = "Could not count values: " + ex.Message; }
        finally { IsLoading = false; }

        if (IsDateColumn) await LoadDatesAsync();
    }

    private void ShowValues(DistinctValueResult r)
    {
        Items.Clear();
        foreach (var v in r.Values.OrderBy(v => v.Value, StringComparer.OrdinalIgnoreCase)) Items.Add(new ValueItem(this, v.Value, v.Count));
        RaiseAggregate();
    }

    private async Task LoadDatesAsync()
    {
        DatesLoading = true;
        try
        {
            var r = await DistinctValueCounter.CountDaysAsync(_idx, Column, null, _cts.Token);
            DateBlankCount = r.BlankCount;
            var existing = _cell.HeaderFilter as FilterDateSet;
            if (existing != null) DateBlanksChecked = existing.IncludeBlanks;
            Years.Clear();
            foreach (var y in r.Days.GroupBy(d => d.Day.Year))
            {
                var yn = new DateNode(y.Key.ToString(CultureInfo.InvariantCulture), null) { Count = y.Sum(d => d.Count) };
                foreach (var m in y.GroupBy(d => d.Day.Month))
                {
                    var mn = new DateNode(CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(m.Key), yn) { Count = m.Sum(d => d.Count) };
                    foreach (var d in m)
                    {
                        var dn = new DateNode(d.Day.ToString("yyyy-MM-dd ddd", CultureInfo.CurrentCulture), mn, d.Day, d.Count);
                        if (existing != null) dn.SetLeaf(existing.Days.Contains(d.Day));
                        mn.Children.Add(dn);
                    }
                    if (existing != null) mn.Recompute();
                    yn.Children.Add(mn);
                }
                Years.Add(yn);
            }
            if (Years.Count == 1) Years[0].IsExpanded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note = "Could not count days: " + ex.Message; }
        finally { DatesLoading = false; }
    }

    // ---------------------------------------------------------------- checklist state

    internal bool IsValueChecked(string v) => _baseChecked ^ _exceptions.Contains(v);

    internal void SetValueChecked(string v, bool on)
    {
        if (on == _baseChecked) _exceptions.Remove(v); else _exceptions.Add(v);
        RaiseAggregate();
    }

    /// <summary>(Select All): blanks and every value.</summary>
    public bool? SelectAll
    {
        get => BlanksChecked && NonBlanksChecked == true ? true : !BlanksChecked && NonBlanksChecked == false ? false : null;
        set
        {
            var on = value ?? true;
            _blanksChecked = on;
            SetAllValues(on);
        }
    }

    public bool BlanksChecked
    {
        get => _blanksChecked;
        set { _blanksChecked = value; RaiseAggregate(); }
    }

    /// <summary>(Non blanks): every non-blank value.</summary>
    public bool? NonBlanksChecked
    {
        get => _exceptions.Count == 0 ? _baseChecked : null;
        set => SetAllValues(value ?? true);
    }

    private void SetAllValues(bool on)
    {
        _baseChecked = on;
        _exceptions.Clear();
        foreach (var i in Items) i.Refresh();
        RaiseAggregate();
    }

    private void RaiseAggregate()
    {
        OnPropertyChanged(nameof(SelectAll));
        OnPropertyChanged(nameof(BlanksChecked));
        OnPropertyChanged(nameof(NonBlanksChecked));
    }

    partial void OnSearchTextChanged(string value)
    {
        ItemsView.Refresh();
        if (_truncated) { _searchTimer.Stop(); _searchTimer.Start(); }
    }

    private async Task OnSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var text = SearchText;
        if (text.Length == 0) { if (_full != null) ShowValues(_full); return; }
        IsLoading = true;
        try
        {
            var r = await DistinctValueCounter.CountAsync(_idx, Column, DistinctValueCounter.DefaultMaxValues, text,
                new Progress<double>(p => Progress = p), cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowValues(r);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note = "Search failed: " + ex.Message; }
        finally { if (_searchCts == cts) IsLoading = false; }
    }

    // ---------------------------------------------------------------- result

    /// <summary>The filter for the tab in use, or null for "no filter on this column".</summary>
    public FilterNode? BuildResult()
    {
        switch (SelectedTab)
        {
            case 1 when IsDateColumn && Years.Count > 0:
            {
                var leaves = Years.SelectMany(y => y.Leaves()).ToList();
                if (leaves.All(l => l.IsChecked == true) && DateBlanksChecked) return null;
                return new FilterDateSet(Column, leaves.Where(l => l.IsChecked == true && l.Day != null).Select(l => l.Day!.Value), DateBlanksChecked);
            }
            case 2:
            {
                var a = Custom1.Build(Column);
                var b = Custom2.Build(Column);
                if (a == null && b == null) return null;
                if (a == null || b == null) return a ?? b;
                return new FilterGroup(CustomIsOr ? FilterGroupKind.Or : FilterGroupKind.And, new FilterNode[] { a, b });
            }
            default:
            {
                if (_baseChecked && _exceptions.Count == 0)
                    return _blanksChecked ? null : new FilterCondition(Column, ColumnFilterMode.IsNotEmpty);
                if (_baseChecked) return FilterGroup.Not(new FilterValueSet(Column, _exceptions, !_blanksChecked));
                return new FilterValueSet(Column, _exceptions, _blanksChecked);
            }
        }
    }

    [RelayCommand]
    private void Ok()
    {
        try
        {
            var r = BuildResult();
            _cell.HeaderFilter = r;
        }
        catch (Exception ex) { _owner.ShowError("Invalid filter", ex); }
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void ClearColumn()
    {
        _cell.HeaderFilter = null;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    public void Dispose()
    {
        _searchTimer.Stop();
        _cts.Cancel();
        _searchCts?.Cancel();
    }
}
