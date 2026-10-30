using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Results;

public enum FilterGroupKind { And, Or }

/// <summary>Row filter by flag state (flags are keyed on the original file row index).</summary>
public enum FlagFilterMode { All, Flagged, Unflagged }

/// <summary>
/// Node of a filter expression tree (Timeline Explorer / DevExpress style):
/// <see cref="FilterGroup"/> (And/Or, optional Not), <see cref="FilterCondition"/> (column, operator, value(s)),
/// <see cref="FilterValueSet"/> (column value in a set, optionally blanks) and <see cref="FilterDateSet"/> (column date in a set of days).
/// All string comparisons are case-insensitive. A node on an unknown column places no constraint (it is ignored).
/// </summary>
public abstract class FilterNode
{
    /// <summary>True when the node places no constraint (an empty group, or a condition without a value).</summary>
    public abstract bool IsEmpty { get; }
    public abstract FilterNode Clone();
    /// <summary>Readable text, e.g. <c>[Source] = 'MFT' And Contains([Message], 'powershell')</c>.</summary>
    public string ToDisplayString() => Render(false);
    public override string ToString() => ToDisplayString();

    /// <summary>True when this node (or a descendant) filters on <paramref name="column"/>.</summary>
    public abstract bool References(string column);

    internal abstract string Render(bool nested);

    /// <summary>Returns a row predicate, or null when the node places no constraint.</summary>
    internal abstract Func<string[], bool>? Compile(Func<string, int> columnIndex);

    internal static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
    /// <summary>Reserved <see cref="FilterCondition.Column"/> meaning "any column" (shown as <c>*</c>).</summary>
    public const string AnyColumn = "*";
    internal static string Col(string c) => c == AnyColumn ? "*" : "[" + c + "]";
    internal static string Cell(string[] row, int col) => (uint)col < (uint)row.Length ? row[col] : "";
}

public sealed class FilterGroup : FilterNode
{
    public FilterGroup() { }
    public FilterGroup(FilterGroupKind kind, IEnumerable<FilterNode> children)
    {
        Kind = kind;
        Children.AddRange(children);
    }

    public FilterGroupKind Kind { get; set; } = FilterGroupKind.And;
    /// <summary>Not (...) around the whole group.</summary>
    public bool Negate { get; set; }
    public List<FilterNode> Children { get; set; } = new();

    public static FilterGroup And(params FilterNode[] children) => new(FilterGroupKind.And, children);
    public static FilterGroup Or(params FilterNode[] children) => new(FilterGroupKind.Or, children);
    public static FilterGroup Not(FilterNode child) => new(FilterGroupKind.And, new[] { child }) { Negate = true };

    public override bool IsEmpty => Children.All(c => c.IsEmpty);

    public override FilterNode Clone() => new FilterGroup(Kind, Children.Select(c => c.Clone())) { Negate = Negate };

    public override bool References(string column) => Children.Any(c => !c.IsEmpty && c.References(column));

    /// <summary>Removes every condition/value set on <paramref name="column"/> (recursively) and prunes groups left empty.</summary>
    public bool RemoveColumn(string column)
    {
        bool changed = false;
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var c = Children[i];
            if (c is FilterGroup g)
            {
                if (g.RemoveColumn(column)) changed = true;
                if (g.Children.Count == 0) { Children.RemoveAt(i); changed = true; }
            }
            else if (c.References(column) || c.IsEmpty)
            {
                Children.RemoveAt(i);
                changed = true;
            }
        }
        return changed;
    }

    internal override string Render(bool nested)
    {
        var parts = Children.Where(c => !c.IsEmpty).Select(c => c.Render(true)).ToList();
        if (parts.Count == 0) return "";
        var body = string.Join(Kind == FilterGroupKind.And ? " And " : " Or ", parts);
        if (Negate) return parts.Count == 1 ? "Not " + body : "Not (" + body + ")";
        return nested && parts.Count > 1 ? "(" + body + ")" : body;
    }

    internal override Func<string[], bool>? Compile(Func<string, int> columnIndex)
    {
        var preds = Children.Where(c => !c.IsEmpty).Select(c => c.Compile(columnIndex)).Where(p => p != null).Cast<Func<string[], bool>>().ToArray();
        if (preds.Length == 0) return null;
        Func<string[], bool> f;
        if (preds.Length == 1) f = preds[0];
        else if (Kind == FilterGroupKind.And)
            f = row => { foreach (var p in preds) if (!p(row)) return false; return true; };
        else
            f = row => { foreach (var p in preds) if (p(row)) return true; return false; };
        if (!Negate) return f;
        var inner = f;
        return row => !inner(row);
    }
}

public sealed class FilterCondition : FilterNode
{
    public FilterCondition() { }
    public FilterCondition(string column, ColumnFilterMode op, string value = "", string? value2 = null)
    {
        Column = column;
        Op = op;
        Value = value ?? "";
        Value2 = value2;
    }

    public string Column { get; set; } = "";
    public ColumnFilterMode Op { get; set; } = ColumnFilterMode.Contains;
    public string Value { get; set; } = "";
    /// <summary>Upper bound for <see cref="ColumnFilterMode.Between"/>.</summary>
    public string? Value2 { get; set; }

    public static bool NeedsValue(ColumnFilterMode op) => op is not (ColumnFilterMode.IsEmpty or ColumnFilterMode.IsNotEmpty);

    public override bool IsEmpty => string.IsNullOrEmpty(Column) ||
        (NeedsValue(Op) && string.IsNullOrEmpty(Value) && (Op != ColumnFilterMode.Between || string.IsNullOrEmpty(Value2)));

    public override FilterNode Clone() => new FilterCondition(Column, Op, Value, Value2);

    public override bool References(string column) => string.Equals(Column, column, StringComparison.OrdinalIgnoreCase);

    internal override string Render(bool nested)
    {
        var c = Col(Column);
        var v = Quote(Value);
        return Op switch
        {
            ColumnFilterMode.Contains => $"Contains({c}, {v})",
            ColumnFilterMode.NotContains => $"Not Contains({c}, {v})",
            ColumnFilterMode.Equals => $"{c} = {v}",
            ColumnFilterMode.NotEquals => $"{c} <> {v}",
            ColumnFilterMode.BeginsWith => $"StartsWith({c}, {v})",
            ColumnFilterMode.EndsWith => $"EndsWith({c}, {v})",
            ColumnFilterMode.Like => $"{c} Like {v}",
            ColumnFilterMode.IsEmpty => $"IsNullOrEmpty({c})",
            ColumnFilterMode.IsNotEmpty => $"Not IsNullOrEmpty({c})",
            ColumnFilterMode.Regex => $"Regex({c}, {v})",
            ColumnFilterMode.Greater => $"{c} > {v}",
            ColumnFilterMode.Less => $"{c} < {v}",
            ColumnFilterMode.Between when string.IsNullOrEmpty(Value2) => $"{c} >= {v}",
            ColumnFilterMode.Between when string.IsNullOrEmpty(Value) => $"{c} <= {Quote(Value2!)}",
            ColumnFilterMode.Between => $"{c} Between({v}, {Quote(Value2!)})",
            _ => $"{c} ? {v}",
        };
    }

    internal override Func<string[], bool>? Compile(Func<string, int> columnIndex)
    {
        if (Column == AnyColumn) return CompileAny();
        int col = columnIndex(Column);
        if (col < 0) return null; // unknown column: ignored
        var test = CellTest(Op);
        return test == null ? null : r => test(Cell(r, col));
    }

    /// <summary>
    /// "Any field": positive operators match when any cell matches; negative operators (Not Contains, &lt;&gt;, IsEmpty) match when no cell
    /// matches the positive counterpart (Contains, =, IsNotEmpty).
    /// </summary>
    private Func<string[], bool>? CompileAny()
    {
        var (positive, negated) = Op switch
        {
            ColumnFilterMode.NotContains => (ColumnFilterMode.Contains, true),
            ColumnFilterMode.NotEquals => (ColumnFilterMode.Equals, true),
            ColumnFilterMode.IsEmpty => (ColumnFilterMode.IsNotEmpty, true),
            _ => (Op, false),
        };
        var test = CellTest(positive);
        if (test == null) return null;
        return r =>
        {
            foreach (var cell in r) if (test(cell)) return !negated;
            return negated;
        };
    }

    /// <summary>The test one cell must pass for <paramref name="op"/> with this condition's operands (compiled once); null when the operator is unknown.</summary>
    private Func<string, bool>? CellTest(ColumnFilterMode op)
    {
        var value = Value;
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        switch (op)
        {
            case ColumnFilterMode.Contains: return c => c.Contains(value, ic);
            case ColumnFilterMode.NotContains: return c => !c.Contains(value, ic);
            case ColumnFilterMode.Equals: return c => string.Equals(c, value, ic);
            case ColumnFilterMode.NotEquals: return c => !string.Equals(c, value, ic);
            case ColumnFilterMode.BeginsWith: return c => c.StartsWith(value, ic);
            case ColumnFilterMode.EndsWith: return c => c.EndsWith(value, ic);
            case ColumnFilterMode.IsEmpty: return c => c.Length == 0;
            case ColumnFilterMode.IsNotEmpty: return c => c.Length != 0;
            case ColumnFilterMode.Regex:
            {
                var rx = new Regex(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
                return c => SafeIsMatch(rx, c);
            }
            case ColumnFilterMode.Like:
            {
                var rx = LikeToRegex(value);
                return c => SafeIsMatch(rx, c);
            }
            case ColumnFilterMode.Greater:
            {
                var cmp = ValueComparer.Create(value);
                return c => cmp.Compare(c) is > 0;
            }
            case ColumnFilterMode.Less:
            {
                var cmp = ValueComparer.Create(value);
                return c => cmp.Compare(c) is < 0;
            }
            case ColumnFilterMode.Between:
            {
                var lo = string.IsNullOrEmpty(value) ? null : ValueComparer.Create(value);
                var hi = string.IsNullOrEmpty(Value2) ? null : ValueComparer.Create(Value2!);
                return cell =>
                {
                    if (lo != null && lo.Compare(cell) is not >= 0) return false;
                    if (hi != null && hi.Compare(cell) is not <= 0) return false;
                    return true;
                };
            }
            default: return null;
        }
    }

    /// <summary>Like pattern: <c>*</c> = any run of characters, <c>?</c> = one character; the whole value must match.</summary>
    public static Regex LikeToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var ch in pattern)
            sb.Append(ch switch { '*' => ".*", '?' => ".", _ => Regex.Escape(ch.ToString()) });
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled, RegexTimeout);
    }

    /// <summary>Per-cell budget of a user-typed regex / Like pattern: a catastrophic pattern must not hang the grid.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A cell whose match times out does not match.</summary>
    private static bool SafeIsMatch(Regex rx, string cell)
    {
        try { return rx.IsMatch(cell); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Compares cells with a fixed operand: numerically when the operand is a number, chronologically when it is a date, else case-insensitive ordinal.</summary>
    private sealed class ValueComparer
    {
        private readonly int _kind; // 0 string, 1 number, 2 date
        private readonly string _s;
        private readonly double _d;
        private readonly DateTime _t;

        private ValueComparer(int kind, string s, double d, DateTime t) { _kind = kind; _s = s; _d = d; _t = t; }

        public static ValueComparer Create(string operand)
        {
            if (CsvValueParser.TryParseNumber(operand, out var d)) return new ValueComparer(1, operand, d, default);
            if (CsvValueParser.TryParseDate(operand, out var t)) return new ValueComparer(2, operand, 0, t);
            return new ValueComparer(0, operand, 0, default);
        }

        /// <summary>Sign of (cell - operand), or null when the cell is empty or not comparable.</summary>
        public int? Compare(string cell)
        {
            if (cell.Length == 0) return null;
            switch (_kind)
            {
                case 1: return CsvValueParser.TryParseNumber(cell, out var d) ? d.CompareTo(_d) : null;
                case 2: return CsvValueParser.TryParseDate(cell, out var t) ? t.CompareTo(_t) : null;
                default: return Math.Sign(string.Compare(cell, _s, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}

/// <summary>Column value is one of <see cref="Values"/> (case-insensitive), or blank when <see cref="IncludeBlanks"/>.</summary>
public sealed class FilterValueSet : FilterNode
{
    private const int DisplayMax = 8;

    public FilterValueSet() { }
    public FilterValueSet(string column, IEnumerable<string> values, bool includeBlanks = false)
    {
        Column = column;
        Values = new HashSet<string>(values.Where(v => v.Length > 0), StringComparer.OrdinalIgnoreCase);
        IncludeBlanks = includeBlanks;
    }

    public string Column { get; set; } = "";
    public HashSet<string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IncludeBlanks { get; set; }

    public override bool IsEmpty => string.IsNullOrEmpty(Column);
    public override FilterNode Clone() => new FilterValueSet(Column, Values, IncludeBlanks);
    public override bool References(string column) => string.Equals(Column, column, StringComparison.OrdinalIgnoreCase);

    internal override string Render(bool nested)
    {
        var parts = new List<string>();
        if (Values.Count > 0)
        {
            var shown = Values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).Take(DisplayMax).Select(Quote);
            var more = Values.Count > DisplayMax ? $", … (+{Values.Count - DisplayMax:N0})" : "";
            parts.Add($"{Col(Column)} In ({string.Join(", ", shown)}{more})");
        }
        if (IncludeBlanks) parts.Add($"IsNullOrEmpty({Col(Column)})");
        if (parts.Count == 0) return $"{Col(Column)} In ()";
        var body = string.Join(" Or ", parts);
        return nested && parts.Count > 1 ? "(" + body + ")" : body;
    }

    internal override Func<string[], bool>? Compile(Func<string, int> columnIndex)
    {
        int col = columnIndex(Column);
        if (col < 0) return null;
        var set = new HashSet<string>(Values, StringComparer.OrdinalIgnoreCase);
        bool blanks = IncludeBlanks;
        return r =>
        {
            var c = Cell(r, col);
            return c.Length == 0 ? blanks : set.Contains(c);
        };
    }
}

/// <summary>Column parses as a timestamp whose (UTC) day is one of <see cref="Days"/>; empty/unparseable cells match when <see cref="IncludeBlanks"/>.</summary>
public sealed class FilterDateSet : FilterNode
{
    public FilterDateSet() { }
    public FilterDateSet(string column, IEnumerable<DateOnly> days, bool includeBlanks = false)
    {
        Column = column;
        Days = new HashSet<DateOnly>(days);
        IncludeBlanks = includeBlanks;
    }

    public string Column { get; set; } = "";
    public HashSet<DateOnly> Days { get; set; } = new();
    public bool IncludeBlanks { get; set; }

    public override bool IsEmpty => string.IsNullOrEmpty(Column);
    public override FilterNode Clone() => new FilterDateSet(Column, Days, IncludeBlanks);
    public override bool References(string column) => string.Equals(Column, column, StringComparison.OrdinalIgnoreCase);

    internal override string Render(bool nested)
    {
        var parts = new List<string>();
        if (Days.Count > 0)
        {
            // collapse consecutive days into ranges
            var sorted = Days.OrderBy(d => d).ToList();
            var ranges = new List<string>();
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1].DayNumber == sorted[j].DayNumber + 1) j++;
                ranges.Add(i == j ? Day(sorted[i]) : $"{Day(sorted[i])}..{Day(sorted[j])}");
                i = j + 1;
            }
            var shown = ranges.Take(6).ToList();
            var more = ranges.Count > 6 ? $", … (+{ranges.Count - 6})" : "";
            parts.Add($"{Col(Column)} InDays({string.Join(", ", shown)}{more})");
        }
        if (IncludeBlanks) parts.Add($"IsNullOrEmpty({Col(Column)})");
        if (parts.Count == 0) return $"{Col(Column)} InDays()";
        var body = string.Join(" Or ", parts);
        return nested && parts.Count > 1 ? "(" + body + ")" : body;
    }

    private static string Day(DateOnly d) => "#" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "#";

    internal override Func<string[], bool>? Compile(Func<string, int> columnIndex)
    {
        int col = columnIndex(Column);
        if (col < 0) return null;
        var days = new HashSet<DateOnly>(Days);
        bool blanks = IncludeBlanks;
        return r =>
        {
            var c = Cell(r, col);
            return c.Length > 0 && CsvValueParser.TryParseDate(c, out var dt) ? days.Contains(DateOnly.FromDateTime(dt)) : blanks;
        };
    }
}
