using System.Globalization;
using System.Text;
using EzForensics.Core.Platforms;
using EzForensics.Core.Results;

namespace EzForensics.Core.Explore.Search;

/// <summary>Search results as CSV: UTF-8 with BOM (Excel), CRLF, RFC 4180 quoting; times UTC <c>yyyy-MM-dd HH:mm:ss</c>.</summary>
public static class SearchCsvExport
{
    public static readonly IReadOnlyList<string> Headers = new[]
    {
        "Path", "Name", "Folder", "Type", "Size", "Modified (UTC)", "Created (UTC)", "Accessed (UTC)", "Attributes", "Hits", "First hit line",
    };

    /// <summary>Writes <paramref name="results"/> to <paramref name="path"/> (replaced atomically); returns the rows written.</summary>
    /// <param name="escapeFormulas">Prefix cells Excel would run as formulas (<c>= + - @</c>) with <c>'</c>.</param>
    public static int Write(string path, IEnumerable<SearchResult> results, bool escapeFormulas = false, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        int rows;
        try
        {
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(true), 1 << 16) { NewLine = "\r\n" })
                rows = Write(w, results, escapeFormulas, ct);
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return rows;
    }

    /// <summary>Writes header and rows to <paramref name="w"/> (the caller owns the encoding).</summary>
    public static int Write(TextWriter w, IEnumerable<SearchResult> results, bool escapeFormulas = false, CancellationToken ct = default)
    {
        WriteRow(w, Headers, false);
        int n = 0;
        var cells = new string[Headers.Count];
        foreach (var r in results)
        {
            if ((n & 255) == 0) ct.ThrowIfCancellationRequested();
            var first = r.Hits.Count > 0 ? r.Hits[0] : null;
            cells[0] = r.FullPath;
            cells[1] = r.Name;
            cells[2] = r.Folder;
            cells[3] = r.IsDirectory ? "Folder" : "File";
            cells[4] = r.IsDirectory ? "" : r.Size.ToString(CultureInfo.InvariantCulture);
            cells[5] = Time(r.ModifiedUtc);
            cells[6] = Time(r.CreatedUtc);
            cells[7] = Time(r.AccessedUtc);
            cells[8] = r.Attributes == 0 ? "" : r.Attributes.ToString();
            cells[9] = r.HitCount.ToString(CultureInfo.InvariantCulture);
            cells[10] = first == null ? "" : $"{first.LineNumber}: {first.LineText}";
            WriteRow(w, cells, escapeFormulas);
            n++;
        }
        w.Flush();
        return n;
    }

    private static string Time(DateTime t) => t == default ? "" : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static void WriteRow(TextWriter w, IReadOnlyList<string> cells, bool escapeFormulas)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0) w.Write(',');
            var c = cells[i];
            if (escapeFormulas && TsvFormatter.IsFormulaLike(c)) c = "'" + c;
            w.Write(ArtifactCsvWriter.Escape(c));
        }
        w.WriteLine();
    }
}
