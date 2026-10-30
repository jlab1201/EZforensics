using System.Text;
using Microsoft.Win32.SafeHandles;
using Sylvan.Data.Csv;

namespace EzForensics.Core.Results;

/// <summary>
/// Random-access view over a (possibly huge) CSV file. A single background pass records the byte
/// offset of every record; rows become readable while indexing is still running.
/// Thread safe: reads use positional I/O and a locked LRU page cache.
/// </summary>
public sealed class CsvIndex : IDisposable
{
    public const int PageSize = 500;
    private const int MaxCachedPages = 128;
    private const int IndexBufferSize = 4 * 1024 * 1024;

    private readonly FileStream _readStream;
    private readonly SafeFileHandle _handle;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, int> _colIndex;

    private long[] _offsets = new long[1024];
    private long _count;
    private long _bytesIndexed;
    private bool _disposed;

    // LRU page cache
    private readonly object _cacheLock = new();
    private readonly Dictionary<long, LinkedListNode<KeyValuePair<long, string[][]>>> _cache = new();
    private readonly LinkedList<KeyValuePair<long, string[][]>> _lru = new();

    public string Path { get; }
    public IReadOnlyList<string> Headers { get; }
    public long FileLength { get; }
    /// <summary>Number of data rows (excluding the header) indexed so far; grows until <see cref="IndexingCompleted"/> finishes.</summary>
    public long RowCount => Volatile.Read(ref _count);
    public long BytesIndexed => Volatile.Read(ref _bytesIndexed);
    public bool IsIndexingComplete => _done.Task.IsCompletedSuccessfully;
    /// <summary>Completes when every row has been indexed (faults/cancels if indexing fails/cancels).</summary>
    public Task IndexingCompleted => _done.Task;

    private CsvIndex(string path, IReadOnlyList<string> headers, FileStream readStream, long length, CancellationToken ct)
    {
        Path = path;
        Headers = headers;
        _readStream = readStream;
        _handle = readStream.SafeFileHandle;
        FileLength = length;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++) _colIndex.TryAdd(headers[i], i);
    }

    public static async Task<CsvIndex> OpenAsync(string path, IProgress<long>? rowsIndexed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var share = FileShare.ReadWrite | FileShare.Delete;
        var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, share, 4096, FileOptions.RandomAccess);
        CsvIndex? idx = null;
        try
        {
            var headers = await Task.Run(() => ReadHeaders(path, readStream), ct).ConfigureAwait(false);
            idx = new CsvIndex(path, headers, readStream, readStream.Length, ct);
            idx.StartIndexing(rowsIndexed);
            return idx;
        }
        catch
        {
            if (idx == null) readStream.Dispose(); else idx.Dispose();
            throw;
        }
    }

    /// <summary>Header names exactly as <see cref="Headers"/> would report them (shared with <see cref="FileStatsCalculator"/>).</summary>
    public static string[] ReadHeaderNames(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        return ReadHeaders(path, fs);
    }

    private static string[] ReadHeaders(string path, FileStream readStream)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var tr = new StreamReader(fs, new UTF8Encoding(false), true, 65536);
            using var r = CsvDataReader.Create(tr, new CsvDataReaderOptions { HasHeaders = true });
            var names = new string[r.FieldCount];
            for (int i = 0; i < names.Length; i++) names[i] = r.GetName(i);
            if (names.Length > 0) return names;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // fall through to the built-in parser
        }
        var rr = new CsvRecordReader(readStream.SafeFileHandle, HasBom(readStream.SafeFileHandle) ? 3 : 0);
        return rr.Next() ?? Array.Empty<string>();
    }

    private static bool HasBom(SafeFileHandle h)
    {
        Span<byte> b = stackalloc byte[3];
        return RandomAccess.Read(h, b, 0) == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
    }

    public int ColumnIndex(string name) => _colIndex.TryGetValue(name, out var i) ? i : -1;

    // ---------------------------------------------------------------- indexing

    private void StartIndexing(IProgress<long>? progress)
    {
        var ct = _cts.Token;
        _ = Task.Run(() =>
        {
            try
            {
                IndexLoop(progress, ct);
                _done.TrySetResult();
            }
            catch (OperationCanceledException) { _done.TrySetCanceled(ct); }
            catch (Exception ex) { _done.TrySetException(ex); }
        });
    }

    private void IndexLoop(IProgress<long>? progress, CancellationToken ct)
    {
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.SequentialScan);
        var buf = new byte[IndexBufferSize];
        long pos = 0;
        bool headerSeen = false;
        bool atRecordStart = true, atFieldStart = true, inQuotes = false, quoteSeen = false;
        bool first = true;
        long lastReported = 0;

        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int i = 0;
            if (first)
            {
                first = false;
                if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            }
            for (; i < n; i++)
            {
                byte b = buf[i];
                if (inQuotes)
                {
                    if (quoteSeen)
                    {
                        quoteSeen = false;
                        if (b == (byte)'"') continue;   // escaped quote
                        inQuotes = false;               // closing quote; handle b below
                    }
                    else
                    {
                        if (b == (byte)'"') quoteSeen = true;
                        continue;
                    }
                }

                if (atRecordStart)
                {
                    if (b == (byte)'\n' || b == (byte)'\r') continue;
                    if (!headerSeen) headerSeen = true;
                    else AddOffset(pos + i);
                    atRecordStart = false;
                    atFieldStart = true;
                }

                if (b == (byte)'\n') { atRecordStart = true; atFieldStart = true; }
                else if (b == (byte)',') atFieldStart = true;
                else if (b == (byte)'"' && atFieldStart) { inQuotes = true; atFieldStart = false; }
                else if (b != (byte)'\r') atFieldStart = false;
            }
            pos += n;
            Volatile.Write(ref _bytesIndexed, pos);
            var c = RowCount;
            if (progress != null && c - lastReported >= 50_000) { progress.Report(c); lastReported = c; }
        }
        progress?.Report(RowCount);
    }

    private void AddOffset(long off)
    {
        long c = _count;
        var arr = _offsets;
        if (c == arr.Length)
        {
            var bigger = new long[arr.Length * 2];
            Array.Copy(arr, bigger, c);
            Volatile.Write(ref _offsets, bigger);
            arr = bigger;
        }
        arr[c] = off;
        Volatile.Write(ref _count, c + 1);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Reads one row (cached in 500-row pages).</summary>
    public string[] ReadRow(long index)
    {
        if (index < 0 || index >= RowCount) throw new ArgumentOutOfRangeException(nameof(index));
        long page = index / PageSize;
        var rows = GetPage(page);
        return rows[(int)(index - page * PageSize)];
    }

    private string[][] GetPage(long page)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(page, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Value;
            }
        }

        long start = page * PageSize;
        long total = RowCount;
        int want = (int)Math.Min(PageSize, total - start);
        var rows = ReadRows(start, want);
        if (rows.Length == PageSize || IsIndexingComplete)
        {
            lock (_cacheLock)
            {
                if (!_cache.ContainsKey(page))
                {
                    var node = _lru.AddFirst(new KeyValuePair<long, string[][]>(page, rows));
                    _cache[page] = node;
                    while (_cache.Count > MaxCachedPages)
                    {
                        var last = _lru.Last!;
                        _cache.Remove(last.Value.Key);
                        _lru.RemoveLast();
                    }
                }
            }
        }
        return rows;
    }

    /// <summary>Sequentially parses <paramref name="count"/> rows starting at <paramref name="start"/>, bypassing the cache.</summary>
    public string[][] ReadRows(long start, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CsvIndex));
        long total = RowCount;
        if (start < 0 || start > total) throw new ArgumentOutOfRangeException(nameof(start));
        count = (int)Math.Min(count, total - start);
        if (count <= 0) return Array.Empty<string[]>();

        long off = Volatile.Read(ref _offsets)[start];
        var rr = new CsvRecordReader(_handle, off, count > 64 ? 256 * 1024 : 64 * 1024);
        var result = new string[count][];
        int got = 0;
        int cols = Headers.Count;
        while (got < count)
        {
            var rec = rr.Next();
            if (rec == null) break;
            if (rec.Length < cols) { var p = new string[cols]; Array.Copy(rec, p, rec.Length); for (int k = rec.Length; k < cols; k++) p[k] = ""; rec = p; }
            result[got++] = rec;
        }
        if (got < count) Array.Resize(ref result, got);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _readStream.Dispose();
        lock (_cacheLock) { _cache.Clear(); _lru.Clear(); }
    }
}

/// <summary>Minimal RFC-4180 record reader over positional file reads (UTF-8).</summary>
internal sealed class CsvRecordReader
{
    private readonly SafeFileHandle _h;
    private long _filePos;
    private readonly byte[] _buf;
    private int _pos, _len;
    private bool _eof;
    private byte[] _fb = new byte[512];
    private int _fl;

    public CsvRecordReader(SafeFileHandle h, long start, int bufSize = 64 * 1024)
    {
        _h = h; _filePos = start; _buf = new byte[bufSize];
    }

    private int Read()
    {
        if (_pos >= _len)
        {
            if (_eof) return -1;
            _len = RandomAccess.Read(_h, _buf, _filePos);
            _pos = 0;
            if (_len <= 0) { _eof = true; _len = 0; return -1; }
            _filePos += _len;
        }
        return _buf[_pos++];
    }

    private void Append(int b)
    {
        if (_fl == _fb.Length) Array.Resize(ref _fb, _fb.Length * 2);
        _fb[_fl++] = (byte)b;
    }

    private string Take()
    {
        if (_fl == 0) return "";
        var s = Encoding.UTF8.GetString(_fb, 0, _fl);
        _fl = 0;
        return s;
    }

    /// <summary>Next record, or null at end of file. Blank lines are skipped.</summary>
    public string[]? Next()
    {
        int c;
        while (true)
        {
            c = Read();
            if (c < 0) return null;
            if (c != '\n' && c != '\r') break;
        }
        _pos--;
        var fields = new List<string>(16);
        _fl = 0;
        bool atFieldStart = true, inQ = false;
        while (true)
        {
            c = Read();
            if (c < 0) { fields.Add(Take()); return fields.ToArray(); }
            if (inQ)
            {
                if (c == '"')
                {
                    int n = Read();
                    if (n == '"') Append('"');
                    else { inQ = false; if (n >= 0) _pos--; }
                }
                else Append(c);
                continue;
            }
            if (c == '\n') { fields.Add(Take()); return fields.ToArray(); }
            if (c == ',') { fields.Add(Take()); atFieldStart = true; continue; }
            if (c == '\r') continue;
            if (c == '"' && atFieldStart) { inQ = true; atFieldStart = false; continue; }
            atFieldStart = false;
            Append(c);
        }
    }
}
