using System.Collections.Concurrent;
using System.Text;
using System.Windows.Threading;
using EzForensics.Core.Execution;

namespace EzForensics.App.Services;

/// <summary>
/// Batches log lines arriving on any thread and hands them to the UI every 100 ms as one chunk.
/// <see cref="Controls.LogConsole"/> renders it with TextBox.AppendText (no per-line UI work).
/// </summary>
public sealed class LogBuffer : IDisposable
{
    private const int MaxChars = 400_000;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _timer;
    private readonly StringBuilder _text = new();
    private IDisposable? _sub;

    public LogBuffer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    /// <summary>Raised on the UI thread with newly appended text.</summary>
    public event Action<string>? Appended;
    /// <summary>Raised on the UI thread when the whole text was replaced.</summary>
    public event Action? Reset;

    public string Text => _text.ToString();

    public void Attach(Job job, JobOutputHub hub)
    {
        SetText("");
        _sub = hub.Subscribe(job, Enqueue, out var snapshot);
        foreach (var l in snapshot) Enqueue(l);
        Flush();
    }

    public void Detach()
    {
        _sub?.Dispose();
        _sub = null;
    }

    public void Enqueue(string line) { if (!JobLogFormat.IsHeaderLine(line)) _pending.Enqueue(line); }

    public void SetText(string text)
    {
        Detach();
        while (_pending.TryDequeue(out _)) { }
        _text.Clear();
        if (text.StartsWith('#') || text.Contains("\n#", StringComparison.Ordinal)) text = JobLogFormat.Split(text).Body;
        _text.Append(text.Length > MaxChars ? text[^MaxChars..] : text);
        Reset?.Invoke();
    }

    public void Clear() => SetText("");

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        var sb = new StringBuilder();
        int n = 0;
        while (n < 20000 && _pending.TryDequeue(out var l)) { sb.Append(l).Append('\n'); n++; }
        var chunk = sb.ToString();
        _text.Append(chunk);
        if (_text.Length > MaxChars)
        {
            _text.Remove(0, _text.Length - MaxChars * 3 / 4);
            Reset?.Invoke();
        }
        else Appended?.Invoke(chunk);
    }

    public void Dispose()
    {
        Detach();
        _timer.Stop();
    }
}
