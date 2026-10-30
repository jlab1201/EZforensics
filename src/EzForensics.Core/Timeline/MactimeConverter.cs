using System.Globalization;
using System.Text;

namespace EzForensics.Core.Timeline;

/// <summary>
/// Converts TSK 3.x bodyfiles (<c>MD5|name|inode|mode|uid|gid|size|atime|mtime|ctime|crtime</c>, epoch seconds, possibly fractional; what
/// MFTECmd <c>--body</c> writes) into a mactime-style CSV: one row per distinct non-zero timestamp of a bodyfile line, the flags of the
/// timestamps that share the same instant combined ("macb" positions, '.' when absent: <c>m.cb</c>), sorted ascending by time.
/// Columns: <c>Timestamp</c> (UTC, <see cref="TimelineTimestamp.OutputFormat"/>, what the Analyze grid detects), <c>MACB</c>, <c>Size</c>,
/// <c>Meta</c> (the inode / MFT reference), <c>File Name</c> and <c>Source</c> (<c>FN</c> for a <c>($FILE_NAME)</c> line, else <c>SI</c>).
/// Inputs over <see cref="DefaultChunkRows"/> rows are sorted in chunks written to temp files and merged k-way, so memory stays bounded.
/// </summary>
public static class MactimeConverter
{
    public const string Header = "Timestamp,MACB,Size,Meta,File Name,Source";
    /// <summary>Rows sorted in memory at once; more rows spill to sorted temp chunks that are merged at the end.</summary>
    public const int DefaultChunkRows = 2_000_000;
    private const string FileNameMarker = "($FILE_NAME)";

    /// <summary>One bodyfile line: its name, inode, size and the four timestamps (ticks of UTC, 0 = none). Rows start with the fixed-width timestamp, so ordinal order = time order.</summary>
    internal readonly record struct BodyLine(string Name, string Meta, string Size, long A, long M, long C, long B);

    /// <summary>Converts <paramref name="bodyFiles"/> into <paramref name="csvPath"/>; returns the number of data rows. A partial CSV is deleted on failure or cancel.</summary>
    /// <param name="chunkRows">Rows held in memory before a sorted chunk is spilled to a temp file (small values exercise the merge path).</param>
    public static long Convert(IEnumerable<string> bodyFiles, string csvPath, int chunkRows = DefaultChunkRows, CancellationToken ct = default)
    {
        if (chunkRows < 1) chunkRows = 1;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
        var chunks = new List<string>();
        try
        {
            var buffer = new List<string>();
            foreach (var file in bodyFiles)
            {
                using var reader = new StreamReader(file, Encoding.UTF8, true, 1 << 16);
                string? line;
                int n = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if ((++n & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                    if (!TryParseLine(line, out var b)) continue;
                    AddRows(b, buffer);
                    if (buffer.Count >= chunkRows) chunks.Add(Spill(buffer, csvPath, chunks.Count));
                }
            }
            ct.ThrowIfCancellationRequested();
            long rows = 0;
            var utf8 = new UTF8Encoding(false);
            using (var w = new StreamWriter(csvPath, false, utf8, 1 << 16))
            {
                w.WriteLine(Header);
                if (chunks.Count == 0)
                {
                    buffer.Sort(StringComparer.Ordinal);
                    foreach (var l in buffer) w.WriteLine(l);
                    rows = buffer.Count;
                }
                else
                {
                    if (buffer.Count > 0) chunks.Add(Spill(buffer, csvPath, chunks.Count));
                    rows = Merge(chunks, w, ct);
                }
            }
            return rows;
        }
        catch
        {
            try { File.Delete(csvPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        finally
        {
            foreach (var c in chunks) try { File.Delete(c); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Spill(List<string> buffer, string csvPath, int index)
    {
        buffer.Sort(StringComparer.Ordinal);
        var path = $"{csvPath}.chunk{index}.tmp";
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16))
            foreach (var l in buffer) w.WriteLine(l);
        buffer.Clear();
        return path;
    }

    /// <summary>k-way merge of sorted chunk files (ties keep chunk order, i.e. input order).</summary>
    private static long Merge(IReadOnlyList<string> chunks, StreamWriter w, CancellationToken ct)
    {
        var readers = chunks.Select(c => new StreamReader(c, new UTF8Encoding(false), false, 1 << 16)).ToList();
        try
        {
            var heap = new PriorityQueue<(string Line, int Chunk), (string Line, int Chunk)>(
                Comparer<(string Line, int Chunk)>.Create((x, y) => { var c = string.CompareOrdinal(x.Line, y.Line); return c != 0 ? c : x.Chunk.CompareTo(y.Chunk); }));
            for (int i = 0; i < readers.Count; i++)
                if (readers[i].ReadLine() is { } first) heap.Enqueue((first, i), (first, i));
            long rows = 0;
            while (heap.Count > 0)
            {
                var (line, i) = heap.Dequeue();
                w.WriteLine(line);
                if ((++rows & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                if (readers[i].ReadLine() is { } next) heap.Enqueue((next, i), (next, i));
            }
            return rows;
        }
        finally { foreach (var r in readers) r.Dispose(); }
    }

    /// <summary>Parses one bodyfile line (the name may hold '|': the 9 numeric / id fields are counted from the end). False for blanks, comments and short lines.</summary>
    internal static bool TryParseLine(string line, out BodyLine body)
    {
        body = default;
        if (line.Length == 0 || line[0] == '#') return false;
        var f = line.Split('|');
        if (f.Length < 11) return false;
        int n = f.Length;
        var name = n == 11 ? f[1] : string.Join("|", f, 1, n - 10);
        body = new BodyLine(name, f[n - 9], f[n - 5], Ticks(f[n - 4]), Ticks(f[n - 3]), Ticks(f[n - 2]), Ticks(f[n - 1]));
        return body.A != 0 || body.M != 0 || body.C != 0 || body.B != 0;
    }

    private static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>Epoch seconds (optionally with a fraction) to UTC ticks; 0 for empty, zero, negative, invalid or out-of-range values.</summary>
    internal static long Ticks(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        int dot = s.IndexOf('.');
        var whole = dot < 0 ? s : s[..dot];
        if (!long.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var secs) || secs <= 0) return 0;
        long frac = 0;
        if (dot >= 0 && dot + 1 < s.Length)
        {
            var digits = s.Substring(dot + 1, Math.Min(7, s.Length - dot - 1)).PadRight(7, '0');
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out frac)) frac = 0;
        }
        if (secs > (DateTime.MaxValue.Ticks - UnixEpochTicks) / TimeSpan.TicksPerSecond - 1) return 0;
        return UnixEpochTicks + secs * TimeSpan.TicksPerSecond + frac;
    }

    /// <summary>Adds one CSV row per distinct timestamp of <paramref name="b"/> (flags combined, "macb" order).</summary>
    private static void AddRows(BodyLine b, List<string> rows)
    {
        var times = new (long Ticks, char Flag, int Pos)[]
        {
            (b.M, 'm', 0), (b.A, 'a', 1), (b.C, 'c', 2), (b.B, 'b', 3),
        };
        var source = b.Name.Contains(FileNameMarker, StringComparison.Ordinal) ? "FN" : "SI";
        var name = b.Name.Replace(" " + FileNameMarker, "", StringComparison.Ordinal).Replace(FileNameMarker, "", StringComparison.Ordinal).TrimEnd();
        var tail = string.Join(",", Csv(b.Size), Csv(b.Meta), Csv(name), source);
        foreach (var t in times.Where(t => t.Ticks != 0).Select(t => t.Ticks).Distinct())
        {
            var flags = new char[] { '.', '.', '.', '.' };
            foreach (var x in times)
                if (x.Ticks == t) flags[x.Pos] = x.Flag;
            rows.Add(string.Concat(TimelineTimestamp.Format(new DateTime(t, DateTimeKind.Utc)), ",", new string(flags), ",", tail));
        }
    }

    private static string Csv(string v) =>
        v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";
}
