using System.Text;

namespace EzForensics.Core.Results;

/// <summary>An extra first column written by <see cref="CsvExporter"/> (e.g. the flag colour), computed from the original row index.</summary>
public sealed record ExportExtraColumn(string Header, Func<int, string> Value)
{
    /// <summary>"Flag" column with each row's flag label (empty when not flagged; <paramref name="labelOf"/> maps a flag id to its label). Named "EzForensics_Flag" when the CSV already has a "Flag" column.</summary>
    public static ExportExtraColumn FlagColumn(IReadOnlyDictionary<long, string> flags, IReadOnlyList<string> headers, Func<string, string> labelOf)
    {
        var name = headers.Any(h => string.Equals(h, "Flag", StringComparison.OrdinalIgnoreCase)) ? "EzForensics_Flag" : "Flag";
        return new ExportExtraColumn(name, r => flags.TryGetValue(r, out var id) ? labelOf(id) : "");
    }
}

public static class CsvExporter
{
    /// <summary>Writes the header plus the given rows (in the given order) as UTF-8 CSV, optionally with an extra first column.</summary>
    public static Task ExportAsync(CsvIndex idx, IReadOnlyList<int> rowIndices, string outPath,
        IProgress<double>? progress = null, CancellationToken ct = default, ExportExtraColumn? extra = null)
        => Task.Run(() =>
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var w = new StreamWriter(outPath, false, new UTF8Encoding(false), 1 << 16) { NewLine = "\r\n" };
            WriteLine(w, extra == null ? idx.Headers : new[] { extra.Header }.Concat(idx.Headers).ToList());
            int n = rowIndices.Count;
            const int batch = 2000;
            for (int s = 0; s < n; s += batch)
            {
                ct.ThrowIfCancellationRequested();
                int e = Math.Min(n, s + batch);
                int min = int.MaxValue, max = int.MinValue;
                for (int i = s; i < e; i++) { var r = rowIndices[i]; if (r < min) min = r; if (r > max) max = r; }
                long span = (long)max - min + 1;
                string[][]? block = span <= (long)(e - s) * 4 ? idx.ReadRows(min, (int)span) : null;
                for (int i = s; i < e; i++)
                {
                    var row = block != null ? block[rowIndices[i] - min] : idx.ReadRow(rowIndices[i]);
                    if (extra != null) { w.Write(Escape(extra.Value(rowIndices[i]))); w.Write(','); }
                    WriteLine(w, row);
                }
                progress?.Report((double)e / n);
            }
            progress?.Report(1);
        }, ct);

    private static string Escape(string c) =>
        c.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c;

    private static void WriteLine(StreamWriter w, IReadOnlyList<string> cells)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0) w.Write(',');
            var c = cells[i];
            if (c.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            {
                w.Write('"'); w.Write(c.Replace("\"", "\"\"")); w.Write('"');
            }
            else w.Write(c);
        }
        w.WriteLine();
    }
}

public static class TimestampColumnGuesser
{
    // Source* columns are the file-system times of the parsed input file: on copied collections they are the copy
    // time, so they come after every embedded timestamp (see Core/Advisories rules.json AffectedColumns).
    private static readonly string[] Preferred =
    {
        "TimeCreated", "LastRun", "Created0x10", "Timestamp", "TimeStamp", "DateTime",
        "KeyLastWriteTimestamp", "LastWriteTimestamp", "LastWriteTime", "LastModified", "Modified0x10",
        "CreationTime", "Created", "CreatedOn", "LastAccessed", "FileKeyLastWriteTimestamp",
        "ExecutionTime", "ModifiedTime", "StartTime", "EndTime", "DeletedOn", "Time", "Date",
        "SourceCreated", "SourceModified",
    };

    /// <summary>Per advisory-artifact id: embedded / reliable timestamp columns, best first (tried before <see cref="Preferred"/>).</summary>
    private static readonly Dictionary<string, string[]> ByArtifact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prefetch"] = new[] { "LastRun" },
        ["prefetch-timeline"] = new[] { "RunTime" },
        ["lnk"] = new[] { "TargetModified", "TargetCreated", "TargetAccessed", "TrackerCreatedOn" },
        ["jumplist"] = new[] { "LastModified", "CreationTime", "TargetModified", "TargetCreated", "TargetAccessed", "TrackerCreatedOn" },
    };

    private static readonly Lazy<EzForensics.Core.Advisories.ReliabilityAdvisor> Advisor = new(() => new EzForensics.Core.Advisories.ReliabilityAdvisor());

    /// <summary>Returns the most likely timestamp column name, or null (generic preference, no artifact knowledge).</summary>
    public static string? Guess(IReadOnlyList<string> headers) => Guess(headers, null);

    /// <summary>
    /// Like <see cref="Guess(IReadOnlyList{string})"/> but identifies the artifact (headers, then <paramref name="csvFileName"/>)
    /// and prefers its embedded / reliable timestamp (Prefetch: LastRun; LNK: TargetModified ...) over the Source* file-system
    /// times that are the copy time on copied collections.
    /// </summary>
    public static string? Guess(IReadOnlyList<string> headers, string? csvFileName)
    {
        string? artifact = null;
        try { artifact = Advisor.Value.Identify(headers, csvFileName)?.Id; } catch (Exception) { }
        if (artifact != null && ByArtifact.TryGetValue(artifact, out var list))
            foreach (var p in list)
                foreach (var h in headers)
                    if (string.Equals(h, p, StringComparison.OrdinalIgnoreCase)) return h;
        foreach (var p in Preferred)
            foreach (var h in headers)
                if (string.Equals(h, p, StringComparison.OrdinalIgnoreCase)) return h;
        foreach (var h in headers)
            if (h.Contains("time", StringComparison.OrdinalIgnoreCase) || h.Contains("date", StringComparison.OrdinalIgnoreCase)) return h;
        return null;
    }
}
