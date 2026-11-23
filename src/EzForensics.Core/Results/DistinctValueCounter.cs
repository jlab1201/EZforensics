namespace EzForensics.Core.Results;

public sealed record DistinctValue(string Value, long Count);

public sealed class DistinctValueResult
{
    /// <summary>Non-blank values, most frequent first (ties by value), at most the requested maximum.</summary>
    public IReadOnlyList<DistinctValue> Values { get; init; } = Array.Empty<DistinctValue>();
    public long BlankCount { get; init; }
    public long NonBlankCount { get; init; }
    public long RowsScanned { get; init; }
    /// <summary>Distinct non-blank values seen (a lower bound when <see cref="CountsApproximate"/>).</summary>
    public int DistinctCount { get; init; }
    /// <summary>More distinct values exist than <see cref="Values"/> holds.</summary>
    public bool Truncated { get; init; }
    /// <summary>The tracking cap was hit: values first seen after it are not counted.</summary>
    public bool CountsApproximate { get; init; }
}

public sealed record DayCount(DateOnly Day, long Count);

public sealed class DateBucketResult
{
    /// <summary>Days (UTC) in ascending order.</summary>
    public IReadOnlyList<DayCount> Days { get; init; } = Array.Empty<DayCount>();
    /// <summary>Empty or unparseable cells.</summary>
    public long BlankCount { get; init; }
    public long RowsScanned { get; init; }
}

/// <summary>Background, cancellable distinct-value and per-day counts for a column (header filter popups).</summary>
public static class DistinctValueCounter
{
    public const int DefaultMaxValues = 10_000;
    public const int DefaultMaxTracked = 250_000;
    private const int ChunkRows = 5000;

    public static Task<DistinctValueResult> CountAsync(CsvIndex idx, string column, int maxValues = DefaultMaxValues,
        string? search = null, IProgress<double>? progress = null, CancellationToken ct = default, int maxTracked = DefaultMaxTracked)
        => Task.Run(() => Count(idx, column, maxValues, search, progress, ct, maxTracked), ct);

    private static DistinctValueResult Count(CsvIndex idx, string column, int maxValues, string? search,
        IProgress<double>? progress, CancellationToken ct, int maxTracked)
    {
        int col = idx.ColumnIndex(column);
        if (col < 0) throw new ArgumentException("Unknown column: " + column, nameof(column));
        int n = (int)Math.Min(int.MaxValue, idx.RowCount);
        var global = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        long blanks = 0, nonBlank = 0, done = 0;
        bool capped = false;
        if (string.IsNullOrEmpty(search)) search = null;
        int chunks = (n + ChunkRows - 1) / ChunkRows;

        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, c =>
        {
            int start = c * ChunkRows;
            var rows = idx.ReadRows(start, Math.Min(ChunkRows, n - start));
            var local = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long b = 0, nb = 0;
            foreach (var row in rows)
            {
                var v = col < row.Length ? row[col] : "";
                if (v.Length == 0) { b++; continue; }
                nb++;
                if (search != null && !v.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                local[v] = local.TryGetValue(v, out var k) ? k + 1 : 1;
            }
            lock (gate)
            {
                blanks += b;
                nonBlank += nb;
                foreach (var (v, k) in local)
                {
                    if (global.TryGetValue(v, out var g)) global[v] = g + k;
                    else if (global.Count < maxTracked) global[v] = k;
                    else capped = true;
                }
            }
            progress?.Report((double)Interlocked.Add(ref done, rows.Length) / Math.Max(1, n));
        });
        ct.ThrowIfCancellationRequested();

        var top = global.Select(kv => new DistinctValue(kv.Key, kv.Value))
            .OrderByDescending(d => d.Count).ThenBy(d => d.Value, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maxValues)).ToList();
        return new DistinctValueResult
        {
            Values = top,
            BlankCount = blanks,
            NonBlankCount = nonBlank,
            RowsScanned = n,
            DistinctCount = global.Count,
            Truncated = capped || global.Count > top.Count,
            CountsApproximate = capped,
        };
    }

    public static Task<DateBucketResult> CountDaysAsync(CsvIndex idx, string column, IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            int col = idx.ColumnIndex(column);
            if (col < 0) throw new ArgumentException("Unknown column: " + column, nameof(column));
            bool monthFirst = idx.MonthFirstSlashDates;
            int n = (int)Math.Min(int.MaxValue, idx.RowCount);
            var global = new Dictionary<int, long>();
            var gate = new object();
            long blanks = 0, done = 0;
            int chunks = (n + ChunkRows - 1) / ChunkRows;
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, c =>
            {
                int start = c * ChunkRows;
                var rows = idx.ReadRows(start, Math.Min(ChunkRows, n - start));
                var local = new Dictionary<int, long>();
                long b = 0;
                foreach (var row in rows)
                {
                    var v = col < row.Length ? row[col] : "";
                    if (v.Length == 0 || !CsvValueParser.TryParseDate(v, out var dt, monthFirst)) { b++; continue; }
                    int day = DateOnly.FromDateTime(dt).DayNumber;
                    local[day] = local.TryGetValue(day, out var k) ? k + 1 : 1;
                }
                lock (gate)
                {
                    blanks += b;
                    foreach (var (d, k) in local) global[d] = global.TryGetValue(d, out var g) ? g + k : k;
                }
                progress?.Report((double)Interlocked.Add(ref done, rows.Length) / Math.Max(1, n));
            });
            ct.ThrowIfCancellationRequested();
            return new DateBucketResult
            {
                Days = global.OrderBy(kv => kv.Key).Select(kv => new DayCount(DateOnly.FromDayNumber(kv.Key), kv.Value)).ToList(),
                BlankCount = blanks,
                RowsScanned = n,
            };
        }, ct);

    /// <summary>True when most of the first non-empty values (up to <paramref name="sample"/>) parse as timestamps.</summary>
    public static bool LooksLikeDateColumn(CsvIndex idx, string column, int sample = 200)
    {
        int col = idx.ColumnIndex(column);
        if (col < 0) return false;
        int seen = 0, dates = 0;
        long n = Math.Min(idx.RowCount, 20_000);
        for (long start = 0; start < n && seen < sample; start += CsvIndex.PageSize)
        {
            var rows = idx.ReadRows(start, (int)Math.Min(CsvIndex.PageSize, n - start));
            foreach (var r in rows)
            {
                var v = col < r.Length ? r[col] : "";
                if (v.Length == 0) continue;
                seen++;
                if (v.Length >= 10 && !CsvValueParser.TryParseNumber(v, out _) && CsvValueParser.TryParseDate(v, out _, idx.MonthFirstSlashDates)) dates++;
                if (seen >= sample) break;
            }
        }
        return seen > 0 && dates >= seen * 0.9;
    }
}
