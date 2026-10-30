using System.Text;

namespace EzForensics.Core.Setup;

/// <summary>
/// A UTF-8 text log written one line at a time and flushed after every line, shared for reading (the elevated helper's
/// <c>--log</c> file, tailed live by <see cref="LogFileTailer"/>). Thread-safe; write errors are swallowed (the log is best effort).
/// </summary>
public sealed class LineLogFile : IDisposable
{
    private readonly StreamWriter _w;
    private readonly object _gate = new();
    private bool _disposed;

    private LineLogFile(StreamWriter w) => _w = w;

    /// <summary>
    /// Creates the file; null when it cannot be opened or already exists. Never opens an existing file: the elevated helper writes it at
    /// a path in a user-writable folder, and truncating whatever a planted link there points to would let a normal process damage any
    /// file as Administrator (the wizard deletes an earlier log before it starts the helper).
    /// </summary>
    public static LineLogFile? TryOpen(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new LineLogFile(new StreamWriter(fs, new UTF8Encoding(false)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _w.Write(line.Replace("\r", "").Replace("\n", " "));
                _w.Write('\n');
                _w.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _w.Dispose(); } catch (IOException) { }
        }
    }
}

/// <summary>
/// Reads the complete lines appended to a log file since the last read (shared read, so the writer keeps writing). A line is
/// returned only once its '\n' is there; a missing file reads as nothing yet.
/// </summary>
public sealed class LogFileTailer
{
    /// <summary>Poll interval of <see cref="TailAsync"/> (settable for tests).</summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    private readonly string _path;
    private long _position;
    private readonly List<byte> _partial = new();

    public LogFileTailer(string path) => _path = path;

    /// <summary>The new complete lines (may be empty).</summary>
    public IReadOnlyList<string> ReadNew()
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(_path)) return lines;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _position) { _position = 0; _partial.Clear(); } // truncated: start again
            fs.Seek(_position, SeekOrigin.Begin);
            var buf = new byte[8192];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                _position += n;
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] != (byte)'\n') { _partial.Add(buf[i]); continue; }
                    var line = Encoding.UTF8.GetString(_partial.ToArray()).TrimEnd('\r');
                    _partial.Clear();
                    lines.Add(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return lines;
    }

    /// <summary>
    /// Polls <paramref name="path"/> every <see cref="PollInterval"/> and forwards each new line to <paramref name="onLine"/> until
    /// <paramref name="ct"/> is cancelled, then reads what is left once more. Returns the number of lines forwarded.
    /// </summary>
    public static async Task<int> TailAsync(string path, Action<string> onLine, CancellationToken ct)
    {
        var t = new LogFileTailer(path);
        int count = 0;
        void Drain() { foreach (var l in t.ReadNew()) { count++; onLine(l); } }
        while (!ct.IsCancellationRequested)
        {
            Drain();
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        Drain();
        return count;
    }
}
