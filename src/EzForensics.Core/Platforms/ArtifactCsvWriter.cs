using System.Text;
using EzForensics.Core.Execution;

namespace EzForensics.Core.Platforms;

/// <summary>
/// RFC 4180 CSV writer for native parsers: UTF-8 without BOM, CRLF rows, fields quoted when they contain a comma, quote, CR, LF or
/// leading / trailing white space (quotes doubled). Values are written as found (no formula escaping: the CSVs are evidence views).
/// Writes to <c>&lt;name&gt;.tmp</c> and renames on <see cref="Commit"/>; an existing file is never overwritten (the first free
/// <c>Name (n).csv</c> is used). Disposing without Commit removes the temp file.
/// </summary>
public sealed class ArtifactCsvWriter : IDisposable
{
    private readonly StreamWriter _w;
    private readonly string _tmp;
    private readonly int _columns;
    private bool _done;

    public string TargetPath { get; }
    /// <summary>Final path after <see cref="Commit"/> (may differ from <see cref="TargetPath"/> when that name was taken).</summary>
    public string? FinalPath { get; private set; }
    public long Rows { get; private set; }

    public ArtifactCsvWriter(string path, IReadOnlyList<string> headers)
    {
        if (headers.Count == 0) throw new ArgumentException("At least one column is required.", nameof(headers));
        TargetPath = path;
        _columns = headers.Count;
        _tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        _w = new StreamWriter(new FileStream(_tmp, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\r\n" };
        WriteLine(headers);
    }

    public void WriteRow(params string?[] fields)
    {
        if (fields.Length != _columns) throw new ArgumentException($"Expected {_columns} fields, got {fields.Length}.", nameof(fields));
        WriteLine(fields);
        Rows++;
    }

    private void WriteLine(IReadOnlyList<string?> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) _w.Write(',');
            _w.Write(Escape(fields[i]));
        }
        _w.WriteLine();
    }

    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool quote = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]);
        return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Flushes and moves the temp file to the first free name. Returns the final path.</summary>
    public string Commit()
    {
        if (_done) return FinalPath ?? throw new InvalidOperationException("Writer was abandoned.");
        _w.Flush();
        _w.Dispose();
        _done = true;
        var final = OutputNaming.UniquePath(TargetPath);
        File.Move(_tmp, final);
        FinalPath = final;
        return final;
    }

    public void Dispose()
    {
        if (_done) return;
        _done = true;
        _w.Dispose();
        try { File.Delete(_tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
