using System.Globalization;

namespace EzForensics.Core.Results;

/// <summary>Column operators (case-insensitive). Greater/Less/Between compare numerically or chronologically when the operand parses as a number or date.</summary>
public enum ColumnFilterMode
{
    Contains, Equals, Regex, NotContains,
    NotEquals, BeginsWith, EndsWith, Like, IsEmpty, IsNotEmpty, Greater, Less, Between,
}

public sealed record ColumnFilter(string Column, ColumnFilterMode Mode, string Value)
{
    /// <summary>Upper bound for <see cref="ColumnFilterMode.Between"/>.</summary>
    public string? Value2 { get; init; }
}

public sealed class FilterSpec
{
    /// <summary>Search-box text across all columns (see <see cref="SearchQuery"/>; one term = case-insensitive contains). Invalid text makes the engine throw <see cref="ArgumentException"/>.</summary>
    public string? GlobalText { get; set; }
    /// <summary>Simple per-column filters; ANDed with <see cref="Expression"/>.</summary>
    public List<ColumnFilter> ColumnFilters { get; set; } = new();
    /// <summary>Filter expression tree (filter row, header filters, filter builder); ANDed with everything else.</summary>
    public FilterNode? Expression { get; set; }
    public string? DateColumn { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public FlagFilterMode Flag { get; set; }
    /// <summary>Flagged original row indices (a snapshot; read from worker threads).</summary>
    public IReadOnlySet<long>? FlaggedRows { get; set; }
    /// <summary>Flag id of each flagged row (a snapshot). When set it is used instead of <see cref="FlaggedRows"/>.</summary>
    public IReadOnlyDictionary<long, string>? FlagColorRows { get; set; }
    /// <summary>With <see cref="Flag"/> = Flagged: only rows flagged with one of these flag ids (null or empty = any colour).</summary>
    public IReadOnlySet<string>? FlagColors { get; set; }

    /// <summary>True when the row passes the flag filter (<see cref="Flag"/>, <see cref="FlagColors"/>).</summary>
    public bool FlagMatches(long row)
    {
        if (Flag == FlagFilterMode.All) return true;
        bool flagged;
        string? color = null;
        if (FlagColorRows != null) flagged = FlagColorRows.TryGetValue(row, out color);
        else flagged = FlaggedRows != null && FlaggedRows.Contains(row);
        if (Flag == FlagFilterMode.Unflagged) return !flagged;
        if (!flagged) return false;
        if (FlagColors is { Count: > 0 }) return FlagColorRows != null && color != null && FlagColors.Contains(color);
        return true;
    }

    /// <summary>Rows that can pass the Flagged filter (ascending, below <paramref name="rowCount"/>).</summary>
    public int[] FlaggedCandidates(long rowCount)
    {
        IEnumerable<long> rows = FlagColorRows != null
            ? FlagColorRows.Where(kv => FlagColors is not { Count: > 0 } || FlagColors.Contains(kv.Value)).Select(kv => kv.Key)
            : FlagColors is { Count: > 0 } ? Enumerable.Empty<long>() : (IEnumerable<long>?)FlaggedRows ?? Array.Empty<long>();
        return rows.Where(r => r >= 0 && r < rowCount).Select(r => (int)r).OrderBy(r => r).ToArray();
    }

    private bool HasDate => DateColumn != null && (DateFrom != null || DateTo != null);

    public bool IsEmpty =>
        string.IsNullOrEmpty(GlobalText) && ColumnFilters.Count == 0 && !HasDate &&
        (Expression == null || Expression.IsEmpty) && Flag == FlagFilterMode.All;

    /// <summary>The column filters and <see cref="Expression"/> as one And group.</summary>
    public FilterGroup BuildExpression()
    {
        var root = new FilterGroup();
        foreach (var f in ColumnFilters) root.Children.Add(new FilterCondition(f.Column, f.Mode, f.Value, f.Value2));
        if (Expression is FilterGroup { Kind: FilterGroupKind.And, Negate: false } g) root.Children.AddRange(g.Children);
        else if (Expression != null) root.Children.Add(Expression);
        return root;
    }

    /// <summary>The whole filter as readable text, e.g. <c>[Source] = 'MFT' And Contains([Message], 'powershell') And Not [Flag]</c>.</summary>
    public string ToDisplayString()
    {
        var parts = new List<string>();
        var e = BuildExpression().ToDisplayString();
        if (e.Length > 0) parts.Add(e);
        if (!string.IsNullOrEmpty(GlobalText))
        {
            if (!SearchQuery.TryParse(GlobalText, out var q, out _)) parts.Add($"Contains(*, {FilterNode.Quote(GlobalText)})");
            else if (q != null) parts.Add(q.IsSingleTerm ? $"Contains(*, {FilterNode.Quote(q.SingleTerm!)})" : $"Search(*, {FilterNode.Quote(q.ToDisplayString())})");
        }
        if (HasDate)
        {
            var c = FilterNode.Col(DateColumn!);
            string D(DateTime d) => "#" + d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "#";
            parts.Add(DateFrom != null && DateTo != null ? $"{c} Between({D(DateFrom.Value)}, {D(DateTo.Value)})"
                : DateFrom != null ? $"{c} >= {D(DateFrom.Value)}" : $"{c} <= {D(DateTo!.Value)}");
        }
        if (Flag == FlagFilterMode.Flagged)
            parts.Add(FlagColors is { Count: > 0 }
                ? "[Flag] In (" + string.Join(", ", FlagColors.OrderBy(c => c, StringComparer.Ordinal).Select(c => FilterNode.Quote(c))) + ")"
                : "[Flag]");
        else if (Flag == FlagFilterMode.Unflagged) parts.Add("Not [Flag]");
        return string.Join(" And ", parts);
    }
}

public static class CsvValueParser
{
    /// <summary>The timestamp formats Analyze parses exactly (anything else falls back to a general parse).</summary>
    public static IReadOnlyList<string> AnalyzeDateFormats => DateFormats;

    /// <summary>True when <paramref name="format"/> (a --dt value) is one of <see cref="AnalyzeDateFormats"/>.</summary>
    public static bool IsAnalyzeDateFormat(string format) => DateFormats.Contains(format, StringComparer.Ordinal);

    /// <summary>Warning shown for a --dt format Analyze does not parse.</summary>
    public static string DateFormatWarning =>
        "Analyze parses dates as " + string.Join(", ", DateFormats) + "; another --dt format can break date filters and sorting.";

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd HH:mm:ss.fffffff", "yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fffffff", "yyyy-MM-ddTHH:mm:ss.fffffffK",
        "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-dd",
    };

    public static bool TryParseDate(string s, out DateTime dt)
    {
        dt = default;
        if (s.Length < 8) return false;
        const DateTimeStyles st = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, st, out dt)
            || DateTime.TryParse(s, CultureInfo.InvariantCulture, st, out dt);
    }

    public static bool TryParseNumber(string s, out double d)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    internal static DateTime ToUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Local => d.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        _ => d,
    };
}

public static class FilterEngine
{
    private const int ChunkRows = 5000;

    public static Task<int[]> FilterAsync(CsvIndex idx, FilterSpec spec, IProgress<double>? progress, CancellationToken ct)
        => Task.Run(() => Filter(idx, spec, progress, ct), ct);

    private static int[] Filter(CsvIndex idx, FilterSpec spec, IProgress<double>? progress, CancellationToken ct)
    {
        long total = idx.RowCount;
        if (total > int.MaxValue) total = int.MaxValue;
        int n = (int)total;

        if (spec.IsEmpty)
        {
            progress?.Report(1);
            var all = new int[n];
            for (int i = 0; i < n; i++) all[i] = i;
            return all;
        }

        var matcher = new RowMatcher(idx, spec);

        // "Show only flagged": evaluate the flagged rows only (no full scan).
        if (spec.Flag == FlagFilterMode.Flagged)
        {
            var cand = spec.FlaggedCandidates(n);
            if (!matcher.HasRowConstraints) { progress?.Report(1); return cand; }
            var hits = new List<int>(cand.Length);
            for (int i = 0; i < cand.Length; i++)
            {
                if ((i & 255) == 0) { ct.ThrowIfCancellationRequested(); progress?.Report((double)i / Math.Max(1, cand.Length)); }
                if (matcher.Matches(idx.ReadRow(cand[i]), cand[i])) hits.Add(cand[i]);
            }
            progress?.Report(1);
            return hits.ToArray();
        }

        int chunks = (n + ChunkRows - 1) / ChunkRows;
        var parts = new int[chunks][];
        long done = 0;

        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, c =>
        {
            int start = c * ChunkRows;
            int cnt = Math.Min(ChunkRows, n - start);
            var rows = idx.ReadRows(start, cnt);
            var hits = new List<int>();
            for (int r = 0; r < rows.Length; r++)
                if (matcher.Matches(rows[r], start + r)) hits.Add(start + r);
            parts[c] = hits.ToArray();
            var d = Interlocked.Add(ref done, cnt);
            progress?.Report(n == 0 ? 1 : (double)d / n);
        });

        ct.ThrowIfCancellationRequested();
        int sum = 0;
        foreach (var p in parts) sum += p.Length;
        var result = new int[sum];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(result, o); o += p.Length; }
        return result;
    }

    private sealed class RowMatcher
    {
        private readonly string? _global;
        private readonly Func<string[], bool>? _globalQuery;
        private readonly Func<string[], bool>? _expr;
        private readonly int _dateCol = -1;
        private readonly DateTime? _from, _to;
        private readonly bool _impossible;
        private readonly FilterSpec _spec;

        public RowMatcher(CsvIndex idx, FilterSpec spec)
        {
            if (!SearchQuery.TryParse(spec.GlobalText, out var q, out var err)) throw new ArgumentException(err, nameof(spec));
            if (q != null) { if (q.IsSingleTerm) _global = q.SingleTerm; else _globalQuery = q.Compile(); }
            _expr = spec.BuildExpression().Compile(idx.ColumnIndex);
            if (spec.DateColumn != null && (spec.DateFrom != null || spec.DateTo != null))
            {
                _dateCol = idx.ColumnIndex(spec.DateColumn);
                if (_dateCol < 0) _impossible = true;
                _from = spec.DateFrom is { } f ? CsvValueParser.ToUtc(f) : null;
                _to = spec.DateTo is { } t ? CsvValueParser.ToUtc(t) : null;
            }
            _spec = spec;
        }

        /// <summary>True when something other than the flag filter constrains rows.</summary>
        public bool HasRowConstraints => _impossible || _global != null || _globalQuery != null || _expr != null || _dateCol >= 0;

        public bool Matches(string[] row, long index)
        {
            if (_impossible) return false;
            if (!_spec.FlagMatches(index)) return false;
            if (_global != null)
            {
                bool any = false;
                foreach (var cell in row)
                    if (cell.Contains(_global, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
                if (!any) return false;
            }
            else if (_globalQuery != null && !_globalQuery(row)) return false;
            if (_expr != null && !_expr(row)) return false;
            if (_dateCol >= 0)
            {
                var cell = _dateCol < row.Length ? row[_dateCol] : "";
                if (!CsvValueParser.TryParseDate(cell, out var dt)) return false;
                if (_from != null && dt < _from) return false;
                if (_to != null && dt > _to) return false;
            }
            return true;
        }
    }

    // ------------------------------------------------------------------ sort

    /// <summary>
    /// Returns rowIndices reordered by the column. Ordinal string order, unless every non-empty value in
    /// the column parses as a number (numeric order) or as a date (chronological). Stable; empty values first.
    /// </summary>
    public static Task<int[]> SortAsync(CsvIndex idx, int[] rowIndices, string column, bool descending,
        CancellationToken ct = default, IProgress<double>? progress = null)
        => Task.Run(() => Sort(idx, rowIndices, column, descending, ct, progress), ct);

    private static int[] Sort(CsvIndex idx, int[] rows, string column, bool desc, CancellationToken ct, IProgress<double>? progress)
    {
        int col = idx.ColumnIndex(column);
        if (col < 0) throw new ArgumentException("Unknown column: " + column, nameof(column));
        int n = rows.Length;
        var keys = new string[n];

        int chunks = (n + ChunkRows - 1) / ChunkRows;
        long done = 0;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, c =>
        {
            int s = c * ChunkRows, e = Math.Min(n, s + ChunkRows);
            int min = int.MaxValue, max = int.MinValue;
            for (int i = s; i < e; i++) { if (rows[i] < min) min = rows[i]; if (rows[i] > max) max = rows[i]; }
            long span = (long)max - min + 1;
            if (e > s && span <= (long)(e - s) * 4 && span <= int.MaxValue)
            {
                var block = idx.ReadRows(min, (int)span);
                for (int i = s; i < e; i++) keys[i] = Cell(block[rows[i] - min], col);
            }
            else
            {
                for (int i = s; i < e; i++) keys[i] = Cell(idx.ReadRow(rows[i]), col);
            }
            progress?.Report(0.5 * Interlocked.Add(ref done, e - s) / Math.Max(1, n));
        });

        // choose comparison mode
        bool allNum = true, allDate = true;
        foreach (var k in keys)
        {
            if (k.Length == 0) continue;
            if (allNum && !CsvValueParser.TryParseNumber(k, out _)) allNum = false;
            if (allDate && !CsvValueParser.TryParseDate(k, out _)) allDate = false;
            if (!allNum && !allDate) break;
        }

        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        Comparison<int> cmp;
        if (allNum)
        {
            var d = new double[n];
            for (int i = 0; i < n; i++) d[i] = keys[i].Length == 0 ? double.NegativeInfinity : (CsvValueParser.TryParseNumber(keys[i], out var v) ? v : double.NegativeInfinity);
            cmp = (a, b) => d[a].CompareTo(d[b]);
        }
        else if (allDate)
        {
            var d = new long[n];
            for (int i = 0; i < n; i++) d[i] = keys[i].Length == 0 ? long.MinValue : (CsvValueParser.TryParseDate(keys[i], out var v) ? v.Ticks : long.MinValue);
            cmp = (a, b) => d[a].CompareTo(d[b]);
        }
        else
        {
            cmp = (a, b) => string.CompareOrdinal(keys[a], keys[b]);
        }

        Comparison<int> full = desc
            ? (a, b) => { int r = cmp(b, a); return r != 0 ? r : a.CompareTo(b); }
            : (a, b) => { int r = cmp(a, b); return r != 0 ? r : a.CompareTo(b); };
        ct.ThrowIfCancellationRequested();
        Array.Sort(perm, full);

        var result = new int[n];
        for (int i = 0; i < n; i++) result[i] = rows[perm[i]];
        progress?.Report(1);
        return result;
    }

    private static string Cell(string[] row, int col) => col < row.Length ? row[col] : "";
}
