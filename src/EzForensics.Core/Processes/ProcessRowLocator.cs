using System.Globalization;
using EzForensics.Core.Results;

namespace EzForensics.Core.Processes;

/// <summary>A CSV to open in the Analyze grid with the filter that shows the chosen rows (<see cref="ProcessRowLocator.GridTarget"/>).</summary>
public sealed record ProcessGridTarget(string CsvPath, FilterNode Filter, int Rows, bool Truncated);

/// <summary>Data rows (0-based, as <see cref="RowFlagStore"/> uses them) of one CSV found for some processes (<see cref="ProcessRowLocator.CollectRowsAsync"/>).</summary>
public sealed record ProcessCsvRows(string CsvPath, IReadOnlyList<long> Rows);

/// <summary>
/// Finds the data rows of one process in a pslist / psscan / pstree CSV (Analyze process tree "Add to Evidence" flags them), and turns detail-card rows of any source
/// (memory, event logs, Prefetch, Zeek) into one CSV plus a grid filter ("Open rows in grid").
/// </summary>
public static class ProcessRowLocator
{
    private const int Chunk = 2000;
    /// <summary>At most this many distinct row filters are ORed together; more rows open the first ones (<see cref="ProcessGridTarget.Truncated"/>).</summary>
    public const int MaxFilterTerms = 200;

    /// <summary>
    /// The CSV and filter for <paramref name="rows"/>: rows without an origin are skipped; the CSV is <paramref name="preferredPath"/> when a row has it, else the one most
    /// rows come from (the first on a tie). Identical filters are merged (all rows of a memory facet share "PID = n"); distinct ones are ORed. Null when no row has an origin.
    /// </summary>
    public static ProcessGridTarget? GridTarget(IEnumerable<ProcessActivityRow> rows, string? preferredPath = null)
    {
        var withOrigin = rows.Where(r => r.Origin is { CsvPath.Length: > 0 }).Select(r => r.Origin!).ToList();
        if (withOrigin.Count == 0) return null;
        string path = preferredPath != null && withOrigin.Any(o => string.Equals(o.CsvPath, preferredPath, StringComparison.OrdinalIgnoreCase))
            ? withOrigin.First(o => string.Equals(o.CsvPath, preferredPath, StringComparison.OrdinalIgnoreCase)).CsvPath
            : withOrigin.GroupBy(o => o.CsvPath, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key;
        var mine = withOrigin.Where(o => string.Equals(o.CsvPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filters = new List<FilterNode>();
        foreach (var o in mine) if (seen.Add(o.Filter.ToDisplayString())) filters.Add(o.Filter);
        bool truncated = filters.Count > MaxFilterTerms;
        if (truncated) filters = filters.GetRange(0, MaxFilterTerms);
        var filter = filters.Count == 1 ? filters[0].Clone() : FilterGroup.Or(filters.Select(f => f.Clone()).ToArray());
        return new ProcessGridTarget(path, filter, mine.Count, truncated);
    }

    /// <summary>
    /// Row numbers (0-based data rows, as <see cref="RowFlagStore"/> uses them) whose PID is <paramref name="pid"/>; when the CSV has the columns,
    /// the image name and creation time (±1 s) must match too, so a reused PID only finds its own row.
    /// </summary>
    public static async Task<IReadOnlyList<long>> FindRowsAsync(string csvPath, int pid, string? name, DateTime? createTime, CancellationToken ct)
    {
        using var idx = await CsvIndex.OpenAsync(csvPath, null, ct).ConfigureAwait(false);
        await idx.IndexingCompleted.ConfigureAwait(false);
        int pidCol = Col(idx, "pid"), nameCol = Col(idx, "imagefilename", "name", "comm", "process"), timeCol = Col(idx, "createtime", "creationtime", "starttime");
        var found = new List<long>();
        if (pidCol < 0) return found;
        for (long start = 0; start < idx.RowCount; start += Chunk)
        {
            ct.ThrowIfCancellationRequested();
            var rows = idx.ReadRows(start, Chunk);
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                if (!int.TryParse(Cell(r, pidCol).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) || p != pid) continue;
                if (!string.IsNullOrEmpty(name) && nameCol >= 0)
                {
                    var n = Cell(r, nameCol).Trim().TrimStart('*', ' ');
                    if (n.Length > 0 && !n.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (createTime != null && timeCol >= 0
                    && DateTime.TryParse(Cell(r, timeCol), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                    && Math.Abs((t - createTime.Value.ToUniversalTime()).TotalSeconds) > 1) continue;
                found.Add(start + i);
            }
        }
        return found;
    }

    /// <summary>
    /// Every source row of <paramref name="nodes"/>, per CSV (Process tree "Flag rows" / "Flag all"): the origins of the rows of every data tab (the same rows and
    /// filters as the details card and "Open rows in grid"), and the processes' own rows in the pslist / psscan / pstree CSVs (<see cref="FindRowsAsync"/>).
    /// Each CSV is read once. A tab capped at <see cref="ProcessActivity.MaxRows"/> rows contributes only the rows the card lists (memory facets filter by PID, so they
    /// still find every row). CSVs are in first-seen order; rows ascending and distinct.
    /// </summary>
    public static async Task<IReadOnlyList<ProcessCsvRows>> CollectRowsAsync(IProcessDetailSource source, IReadOnlyCollection<ProcessNode> nodes,
        IEnumerable<string> processListCsvs, CancellationToken ct)
    {
        var filters = new Dictionary<string, (List<FilterNode> List, HashSet<string> Seen)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var found = new Dictionary<string, SortedSet<long>>(StringComparer.OrdinalIgnoreCase);
        SortedSet<long> Rows(string csv)
        {
            if (!found.TryGetValue(csv, out var s)) { found[csv] = s = new SortedSet<long>(); if (!filters.ContainsKey(csv)) order.Add(csv); }
            return s;
        }
        foreach (var n in nodes)
        {
            if (n.IsGhost) continue;
            foreach (var tab in ProcessActivity.DataTabs)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var set in source.SetsFor(n, tab))
                foreach (var r in set.Rows)
                {
                    if (r.Origin is not { CsvPath.Length: > 0 } o) continue;
                    if (!filters.TryGetValue(o.CsvPath, out var f))
                    {
                        filters[o.CsvPath] = f = (new List<FilterNode>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        if (!found.ContainsKey(o.CsvPath)) order.Add(o.CsvPath);
                    }
                    if (f.Seen.Add(o.Filter.ToDisplayString())) f.List.Add(o.Filter);
                }
            }
        }
        foreach (var csv in processListCsvs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(csv)) continue;
            var rows = Rows(csv);
            foreach (var n in nodes)
                if (!n.IsGhost) rows.UnionWith(await FindRowsAsync(csv, n.Pid, n.Name, n.CreateTime, ct).ConfigureAwait(false));
        }
        foreach (var (csv, f) in filters)
        {
            if (!File.Exists(csv)) continue;
            Rows(csv).UnionWith(await MatchRowsAsync(csv, f.List, ct).ConfigureAwait(false));
        }
        return order.Where(c => found.TryGetValue(c, out var s) && s.Count > 0).Select(c => new ProcessCsvRows(c, found[c].ToList())).ToList();
    }

    /// <summary>
    /// Data rows of <paramref name="csvPath"/> that match any of <paramref name="filters"/> (ORed), in one pass. Filters that are a condition or an AND of
    /// conditions with <c>=</c> / <c>BeginsWith</c> (the card's origins: PID, uid, EventId + second) are looked up by key, so thousands of them stay one hash
    /// lookup per row; any other filter is evaluated as the grid would.
    /// </summary>
    public static async Task<IReadOnlyList<long>> MatchRowsAsync(string csvPath, IReadOnlyList<FilterNode> filters, CancellationToken ct)
    {
        var found = new List<long>();
        if (filters.Count == 0) return found;
        using var idx = await CsvIndex.OpenAsync(csvPath, null, ct).ConfigureAwait(false);
        await idx.IndexingCompleted.ConfigureAwait(false);
        var keyed = new Dictionary<string, (int[] Cols, int[] Prefix, HashSet<string> Keys)>(StringComparer.Ordinal);
        var other = new List<Func<string[], bool>>();
        foreach (var f in filters)
        {
            if (KeyShape(f, idx) is { } k)
            {
                var shape = string.Join("|", k.Cols.Zip(k.Prefix, (c, p) => c + ":" + p));
                if (!keyed.TryGetValue(shape, out var g)) keyed[shape] = g = (k.Cols, k.Prefix, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                g.Keys.Add(k.Key);
            }
            else if (f.Compile(idx.ColumnIndex, idx.MonthFirstSlashDates) is { } p) other.Add(p);
        }
        var shapes = keyed.Values.ToList();
        for (long start = 0; start < idx.RowCount; start += Chunk)
        {
            ct.ThrowIfCancellationRequested();
            var rows = idx.ReadRows(start, Chunk);
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                bool hit = false;
                foreach (var s in shapes)
                    if (RowKey(r, s.Cols, s.Prefix) is { } key && s.Keys.Contains(key)) { hit = true; break; }
                if (!hit) foreach (var p in other) if (p(r)) { hit = true; break; }
                if (hit) found.Add(start + i);
            }
        }
        return found;
    }

    private const char KeySep = '\u001f';

    /// <summary>Columns, prefix lengths (-1 = whole cell, =) and the key of a filter made of = / BeginsWith conditions on known columns; null otherwise.</summary>
    private static (int[] Cols, int[] Prefix, string Key)? KeyShape(FilterNode f, CsvIndex idx)
    {
        var conds = new List<FilterCondition>();
        if (f is FilterCondition c) conds.Add(c);
        else if (f is FilterGroup { Kind: FilterGroupKind.And, Negate: false } g && g.Children.All(x => x is FilterCondition)) conds.AddRange(g.Children.Cast<FilterCondition>());
        else return null;
        if (conds.Count == 0) return null;
        var parts = conds.Select(x => (Col: x.Column == FilterCondition.AnyColumn ? -1 : idx.ColumnIndex(x.Column), x.Op, x.Value, x.Value2))
            .OrderBy(x => x.Col).ThenBy(x => x.Op).ToList();
        if (parts.Any(x => x.Col < 0 || x.Value2 != null || x.Op is not (ColumnFilterMode.Equals or ColumnFilterMode.BeginsWith))) return null;
        return (parts.Select(x => x.Col).ToArray(), parts.Select(x => x.Op == ColumnFilterMode.Equals ? -1 : x.Value.Length).ToArray(),
            string.Join(KeySep, parts.Select(x => x.Value)));
    }

    private static string? RowKey(string[] r, int[] cols, int[] prefix)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < cols.Length; i++)
        {
            var cell = Cell(r, cols[i]);
            if (prefix[i] >= 0)
            {
                if (cell.Length < prefix[i]) return null;
                cell = cell[..prefix[i]];
            }
            if (i > 0) sb.Append(KeySep);
            sb.Append(cell);
        }
        return sb.ToString();
    }

    private static string Cell(string[] r, int c) => c >= 0 && c < r.Length ? r[c] ?? "" : "";

    private static int Col(CsvIndex idx, params string[] aliases)
    {
        foreach (var a in aliases)
            for (int i = 0; i < idx.Headers.Count; i++)
                if (new string(idx.Headers[i].Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()) == a) return i;
        return -1;
    }
}
