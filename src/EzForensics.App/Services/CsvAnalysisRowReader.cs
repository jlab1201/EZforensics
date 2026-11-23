using EzForensics.Core.Analysis;
using EzForensics.Core.Results;
using EzForensics.Core.Timeline;

namespace EzForensics.App.Services;

/// <summary>
/// Backs "Add my analysis" with the case CSVs: the time of a row (its timestamp column) gives an analyst item its first and last seen.
/// Opens each CSV once per use; blocks, so call it off the UI thread. Anything unreadable gives "no facts" and never throws.
/// </summary>
internal sealed class CsvAnalysisRowReader : IAnalysisRowReader, IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, (CsvIndex? Index, int Column)> _open = new(StringComparer.OrdinalIgnoreCase);

    public CsvAnalysisRowReader(string caseRoot) { _root = caseRoot; }

    public AnalysisRowInfo? Read(AnalysisRow row)
    {
        try
        {
            if (!_open.TryGetValue(row.CsvRelPath, out var e)) _open[row.CsvRelPath] = e = Open(row.CsvRelPath);
            if (e.Index == null || e.Column < 0 || row.Row < 0 || row.Row >= e.Index.RowCount) return null;
            var cells = e.Index.ReadRow(row.Row);
            if (e.Column >= cells.Length) return null;
            if (TimelineTimestamp.TryParse(cells[e.Column], out var utc, monthFirstSlash: e.Index.MonthFirstSlashDates) != TimestampParseResult.Ok) return null;
            return new AnalysisRowInfo(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private (CsvIndex?, int) Open(string rel)
    {
        try
        {
            var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return (null, -1);
            var idx = CsvIndex.OpenAsync(path, null, CancellationToken.None).GetAwaiter().GetResult();
            idx.IndexingCompleted.GetAwaiter().GetResult();
            var col = TimestampColumnGuesser.Guess(idx.Headers, path);
            return (idx, col == null ? -1 : idx.ColumnIndex(col));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return (null, -1);
        }
    }

    public void Dispose()
    {
        foreach (var (idx, _) in _open.Values) idx?.Dispose();
        _open.Clear();
    }
}
