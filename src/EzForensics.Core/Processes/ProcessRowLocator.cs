using System.Globalization;
using EzForensics.Core.Results;

namespace EzForensics.Core.Processes;

/// <summary>A CSV to open in the Analyze grid with the filter that shows the chosen rows (<see cref="ProcessRowLocator.GridTarget"/>).</summary>
public sealed record ProcessGridTarget(string CsvPath, FilterNode Filter, int Rows, bool Truncated);

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

    private static string Cell(string[] r, int c) => c >= 0 && c < r.Length ? r[c] ?? "" : "";

    private static int Col(CsvIndex idx, params string[] aliases)
    {
        foreach (var a in aliases)
            for (int i = 0; i < idx.Headers.Count; i++)
                if (new string(idx.Headers[i].Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()) == a) return i;
        return -1;
    }
}
