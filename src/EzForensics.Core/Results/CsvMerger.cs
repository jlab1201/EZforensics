using System.Security.Cryptography;
using System.Text;
using Sylvan.Data.Csv;

namespace EzForensics.Core.Results;

public sealed record CsvMergeInput(string Path, string? Host = null);

public sealed record CsvMergeOptions
{
    public bool AddSourceFileColumn { get; init; } = true;
    public bool AddHostColumn { get; init; } = true;
}

/// <param name="InputSha256">Lower-case hex SHA-256 of each input file's raw bytes, keyed by input path.</param>
public sealed record CsvMergeResult(long RowsWritten, IReadOnlyList<string> Headers,
    IReadOnlyDictionary<string, string> InputSha256, string OutputSha256);

public static class CsvMerger
{
    public const string SourceFileColumn = "SourceFile";
    public const string HostColumn = "Host";

    /// <summary>True when every file has the identical header sequence (case-insensitive). False for an empty list or an unreadable file.</summary>
    public static bool HaveSameSchema(IEnumerable<string> paths)
    {
        string[]? first = null;
        int n = 0;
        foreach (var p in paths)
        {
            n++;
            string[] h;
            try { h = ReadHeaders(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CsvRecordTooLargeException) { return false; }
            if (first == null) { first = h; continue; }
            if (h.Length != first.Length) return false;
            for (int i = 0; i < h.Length; i++)
                if (!string.Equals(h[i], first[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return n > 0;
    }

    /// <summary>
    /// Streams the inputs into a new CSV: optional SourceFile / Host columns first, then the union of headers in first-seen order
    /// (case-insensitive match). Refuses to overwrite an existing file or to write into one of the inputs. A partial output is deleted on failure/cancel.
    /// </summary>
    public static async Task<CsvMergeResult> MergeAsync(IReadOnlyList<CsvMergeInput> inputs, string outputPath,
        CsvMergeOptions? options = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        options ??= new CsvMergeOptions();
        if (inputs.Count == 0) throw new ArgumentException("No input files.", nameof(inputs));
        var outFull = System.IO.Path.GetFullPath(outputPath);
        foreach (var i in inputs)
            if (string.Equals(System.IO.Path.GetFullPath(i.Path), outFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output file cannot be one of the input files.");
        if (File.Exists(outFull)) throw new IOException($"Output file already exists: {outFull}");

        // Pass 1: union of headers.
        var added = new List<string>();
        if (options.AddSourceFileColumn) added.Add(SourceFileColumn);
        if (options.AddHostColumn) added.Add(HostColumn);
        var headers = new List<string>(added);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < added.Count; i++) index[added[i]] = i;
        var maps = new int[inputs.Count][];
        for (int f = 0; f < inputs.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            var h = ReadHeaders(inputs[f].Path);
            var map = new int[h.Length];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < h.Length; c++)
            {
                var name = h[c];
                // Distinct name within this file, and never collide with the synthetic columns.
                var candidate = name;
                int k = 2;
                while (seen.Contains(candidate) || (c >= 0 && added.Contains(candidate, StringComparer.OrdinalIgnoreCase)))
                    candidate = $"{name} ({k++})";
                seen.Add(candidate);
                if (!index.TryGetValue(candidate, out var pos))
                {
                    pos = headers.Count;
                    headers.Add(candidate);
                    index[candidate] = pos;
                }
                map[c] = pos;
            }
            maps[f] = map;
        }

        long totalBytes = inputs.Sum(i => new FileInfo(i.Path).Length);
        long readBytes = 0;
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long rows = 0;
        bool completed = false;
        try
        {
            await using (var outFs = new FileStream(outFull, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var w = new StreamWriter(outFs, new UTF8Encoding(false), 65536))
            {
                w.NewLine = "\r\n";
                await w.WriteLineAsync(string.Join(',', headers.Select(Quote))).ConfigureAwait(false);
                var cells = new string[headers.Count];
                var sb = new StringBuilder();
                for (int f = 0; f < inputs.Count; f++)
                {
                    var input = inputs[f];
                    var map = maps[f];
                    using var hs = new HashingReadStream(new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan),
                        n => { readBytes += n; if (totalBytes > 0) progress?.Report(Math.Min(1.0, (double)readBytes / totalBytes)); });
                    using (var tr = new StreamReader(hs, new UTF8Encoding(false), true, 65536))
                    using (var r = await CsvDataReader.CreateAsync(tr, new CsvDataReaderOptions { HasHeaders = true }).ConfigureAwait(false))
                    {
                        int n = Math.Min(map.Length, r.FieldCount);
                        long sinceCheck = 0;
                        while (await r.ReadAsync(ct).ConfigureAwait(false))
                        {
                            Array.Clear(cells);
                            int p = 0;
                            if (options.AddSourceFileColumn) cells[p++] = System.IO.Path.GetFileName(input.Path);
                            if (options.AddHostColumn) cells[p++] = input.Host ?? "";
                            for (int c = 0; c < n; c++) cells[map[c]] = r.GetString(c);
                            sb.Clear();
                            for (int c = 0; c < cells.Length; c++)
                            {
                                if (c > 0) sb.Append(',');
                                AppendQuoted(sb, cells[c]);
                            }
                            await w.WriteLineAsync(sb).ConfigureAwait(false);
                            rows++;
                            if (++sinceCheck % 4096 == 0) ct.ThrowIfCancellationRequested();
                        }
                        // drain any unread trailing bytes so the hash covers the whole file
                        hs.DrainTo(Stream.Null);
                        hashes[input.Path] = hs.HashHex();
                    }
                }
                await w.FlushAsync(ct).ConfigureAwait(false);
            }
            string outHash;
            await using (var rs = new FileStream(outFull, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                outHash = Convert.ToHexString(await SHA256.HashDataAsync(rs, ct).ConfigureAwait(false)).ToLowerInvariant();
            completed = true;
            progress?.Report(1.0);
            return new CsvMergeResult(rows, headers, hashes, outHash);
        }
        finally
        {
            if (!completed) { try { File.Delete(outFull); } catch { } }
        }
    }

    private static string[] ReadHeaders(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        using var tr = new StreamReader(fs, new UTF8Encoding(false), true, 65536);
        using var r = CsvDataReader.Create(tr, new CsvDataReaderOptions { HasHeaders = true });
        var h = new string[r.FieldCount];
        for (int i = 0; i < h.Length; i++) h[i] = r.GetName(i);
        return h;
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        AppendQuoted(sb, s);
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string? s)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (s.AsSpan().IndexOfAny(",\"\r\n") < 0) { sb.Append(s); return; }
        sb.Append('"');
        foreach (var ch in s)
        {
            if (ch == '"') sb.Append('"');
            sb.Append(ch);
        }
        sb.Append('"');
    }

    /// <summary>Read-through stream that hashes everything read and reports byte counts.</summary>
    private sealed class HashingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly Action<int> _onRead;
        private string? _hex;

        public HashingReadStream(Stream inner, Action<int> onRead) { _inner = inner; _onRead = onRead; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            if (n > 0) { _hash.AppendData(buffer, offset, n); _onRead(n); }
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = _inner.Read(buffer);
            if (n > 0) { _hash.AppendData(buffer[..n]); _onRead(n); }
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0) { _hash.AppendData(buffer.Span[..n]); _onRead(n); }
            return n;
        }

        public void DrainTo(Stream _)
        {
            var buf = new byte[8192];
            while (Read(buf, 0, buf.Length) > 0) { }
        }

        public string HashHex() => _hex ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); _hash.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
