using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>Display metadata for a filter operator.</summary>
public sealed record FilterOpInfo(ColumnFilterMode Op, string Label, string Symbol, bool NeedsValue, bool IsComparison = false)
{
    public override string ToString() => Label;
}

public static class FilterOps
{
    public static IReadOnlyList<FilterOpInfo> All { get; } = new[]
    {
        new FilterOpInfo(ColumnFilterMode.Contains, "Contains", "∋", true),
        new FilterOpInfo(ColumnFilterMode.NotContains, "Does not contain", "∌", true),
        new FilterOpInfo(ColumnFilterMode.Equals, "Equals", "=", true),
        new FilterOpInfo(ColumnFilterMode.NotEquals, "Does not equal", "≠", true),
        new FilterOpInfo(ColumnFilterMode.BeginsWith, "Begins with", "a…", true),
        new FilterOpInfo(ColumnFilterMode.EndsWith, "Ends with", "…a", true),
        new FilterOpInfo(ColumnFilterMode.Like, "Like (* and ?)", "*?", true),
        new FilterOpInfo(ColumnFilterMode.IsEmpty, "Is empty", "∅", false),
        new FilterOpInfo(ColumnFilterMode.IsNotEmpty, "Is not empty", "≠∅", false),
        new FilterOpInfo(ColumnFilterMode.Regex, "Regex", ".*", true),
        new FilterOpInfo(ColumnFilterMode.Greater, "Greater than", ">", true, true),
        new FilterOpInfo(ColumnFilterMode.Less, "Less than", "<", true, true),
        new FilterOpInfo(ColumnFilterMode.Between, "Between (a..b)", "↔", true, true),
    };

    /// <summary>Operators of the "Flag" field: Is (a chosen flag), Is flagged (any flag), Is not flagged.</summary>
    public static IReadOnlyList<FilterOpInfo> Flag { get; } = new[]
    {
        new FilterOpInfo(ColumnFilterMode.Equals, "Is", "=", true),
        new FilterOpInfo(ColumnFilterMode.IsNotEmpty, "Is flagged", "⚑", false),
        new FilterOpInfo(ColumnFilterMode.IsEmpty, "Is not flagged", "⚐", false),
    };

    public static FilterOpInfo Get(ColumnFilterMode op) => All.FirstOrDefault(o => o.Op == op) ?? All[0];

    /// <summary>Splits a filter-row "a..b" value into Between bounds.</summary>
    public static (string lo, string? hi) SplitBetween(string text)
    {
        int i = text.IndexOf("..", StringComparison.Ordinal);
        return i < 0 ? (text.Trim(), null) : (text[..i].Trim(), text[(i + 2)..].Trim());
    }
}

/// <summary>
/// Per-column filter state: the auto-filter-row cell (operator + text) and the header (funnel) filter.
/// Bound as the column header content, so the header shows the name, the funnel and the filter row.
/// </summary>
public sealed partial class ColumnFilterCell : ObservableObject
{
    private readonly Action _changed;
    private readonly Action _applyNow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpSymbol), nameof(OpLabel), nameof(OpNeedsValue))]
    private ColumnFilterMode _op = ColumnFilterMode.Contains;
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private bool _hasError;
    /// <summary>Filter chosen in the funnel popup (value set, date set or custom condition group).</summary>
    [ObservableProperty] private FilterNode? _headerFilter;
    /// <summary>True when any filter (row cell, funnel, filter builder) references this column.</summary>
    [ObservableProperty] private bool _isFiltered;

    public ColumnFilterCell(string column, int index, Action changed, Action applyNow)
    {
        Column = column;
        Index = index;
        _changed = changed;
        _applyNow = applyNow;
    }

    /// <summary>Reliability marker in the header: "warn", "crit", "info", "ok" or "" (set by ResultsViewModel.Advisories).</summary>
    [ObservableProperty] private string _advisoryKind = "";
    [ObservableProperty] private string? _advisoryTip;

    public string Column { get; }
    /// <summary>CSV column index.</summary>
    public int Index { get; }
    public string OpSymbol => FilterOps.Get(Op).Symbol;
    public string OpLabel => "Filter row operator: " + FilterOps.Get(Op).Label + (Op == ColumnFilterMode.Between ? " (type low..high)" : "");
    public bool OpNeedsValue => FilterOps.Get(Op).NeedsValue;
    public bool IsDateColumn { get; set; }


    private bool _quiet;

    partial void OnValueChanged(string value) { if (!_quiet) _changed(); }
    partial void OnOpChanged(ColumnFilterMode value) { if (!_quiet) _applyNow(); }
    partial void OnHeaderFilterChanged(FilterNode? value) { if (!_quiet) _applyNow(); }

    private void Quietly(Action a)
    {
        _quiet = true;
        try { a(); }
        finally { _quiet = false; }
    }

    /// <summary>Sets operator and value together and applies at once (context menu).</summary>
    public void Set(ColumnFilterMode op, string value)
    {
        Quietly(() => { Op = op; Value = value; });
        _applyNow();
    }

    /// <summary>Clears the row cell and the funnel filter without notifying the owner.</summary>
    public void ResetSilently() => Quietly(() =>
    {
        Op = ColumnFilterMode.Contains;
        Value = "";
        HeaderFilter = null;
    });

    /// <summary>The filter-row condition, or null when the cell is empty or invalid (invalid regex sets <see cref="HasError"/>).</summary>
    public FilterCondition? BuildCondition()
    {
        HasError = false;
        var info = FilterOps.Get(Op);
        if (!info.NeedsValue) return new FilterCondition(Column, Op);
        if (string.IsNullOrEmpty(Value)) return null;
        if (Op == ColumnFilterMode.Between)
        {
            var (lo, hi) = FilterOps.SplitBetween(Value);
            var c = new FilterCondition(Column, Op, lo, hi);
            return c.IsEmpty ? null : c;
        }
        if (Op == ColumnFilterMode.Regex)
        {
            try { _ = new System.Text.RegularExpressions.Regex(Value); }
            catch (ArgumentException) { HasError = true; return null; }
        }
        return new FilterCondition(Column, Op, Value);
    }

    /// <summary>Loads a condition into the row cell without notifying the owner (filter builder round trip).</summary>
    public void Load(FilterCondition c) => Quietly(() =>
    {
        Op = c.Op;
        Value = c.Op == ColumnFilterMode.Between ? $"{c.Value}..{c.Value2}" : c.Value;
    });

    /// <summary>Sets the row cell exactly as typed (operator + text) without notifying the owner (session restore).</summary>
    public void LoadRaw(ColumnFilterMode op, string value) => Quietly(() => { Op = op; Value = value; });

    public void LoadHeader(FilterNode? n) => Quietly(() => HeaderFilter = n);
}