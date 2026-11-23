namespace EzForensics.Core.Spectre;

/// <summary>The first few evidence rows of a finding, cut down for a quick look on a story card: a few key columns, short cells.</summary>
public sealed record EvidencePeek(string FileName, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, int TotalRows)
{
    public static EvidencePeek Empty { get; } = new("", Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), 0);
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Columns worth showing first, when the file has them (time, what, where, who).</summary>
    public static readonly IReadOnlyList<string> KeyColumns = new[]
    {
        "TimeCreated", "Timestamp", "datetime", "SourceCreated", "LastRun", "Created0x10", "EventId", "Computer", "UserName", "MapDescription",
        "ExecutableName", "ExecutableInfo", "Path", "FileName", "Name", "message", "Payload", "PayloadData1",
    };

    /// <summary>
    /// Reads the rows behind <paramref name="target"/> (as <see cref="FindingsEvidence.ResolveRows"/> finds them) and keeps the first
    /// <paramref name="maxRows"/>, up to <paramref name="maxColumns"/> columns that have values (key columns first), cells cut to
    /// <paramref name="maxCell"/> characters. Empty when no row is found.
    /// </summary>
    public static async Task<EvidencePeek> ReadAsync(EvidenceTarget target, int maxRows = 3, int maxColumns = 5, int maxCell = 40, CancellationToken ct = default)
    {
        using var index = await Results.CsvIndex.OpenAsync(target.Csv, null, ct).ConfigureAwait(false);
        await index.IndexingCompleted.ConfigureAwait(false);
        var rows = FindingsEvidence.ResolveRows(index, target, ct);
        if (rows.Count == 0) return Empty with { FileName = System.IO.Path.GetFileName(target.Csv) };
        var picked = rows.Take(maxRows).Select(r => index.ReadRow(r)).ToList();
        return Build(System.IO.Path.GetFileName(target.Csv), index.Headers, picked, rows.Count, maxColumns, maxCell);
    }

    /// <summary>Chooses the columns and cuts the cells (pure; see <see cref="ReadAsync"/>).</summary>
    public static EvidencePeek Build(string fileName, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, int total, int maxColumns = 5, int maxCell = 40)
    {
        bool HasValue(int c) => rows.Any(r => c < r.Length && !string.IsNullOrWhiteSpace(r[c]));
        var cols = new List<int>();
        foreach (var key in KeyColumns)
        {
            if (cols.Count >= maxColumns) break;
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i].Trim(), key, StringComparison.OrdinalIgnoreCase) && !cols.Contains(i) && HasValue(i)) { cols.Add(i); break; }
        }
        for (int i = 0; i < headers.Count && cols.Count < maxColumns; i++)
            if (!cols.Contains(i) && HasValue(i)) cols.Add(i);
        cols.Sort();
        string Cut(string s)
        {
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length <= maxCell ? s : s[..(maxCell - 1)] + "…";
        }
        return new EvidencePeek(fileName, cols.Select(c => headers[c]).ToList(),
            rows.Select(r => (IReadOnlyList<string>)cols.Select(c => c < r.Length ? Cut(r[c]) : "").ToList()).ToList(), total);
    }
}
