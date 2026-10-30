using System.Collections.Concurrent;
using EzForensics.Core.Execution;

namespace EzForensics.App.Services;

/// <summary>
/// Keeps a bounded in-memory tail of every job's output so any page can show a live log, even one
/// opened after the job started. Subscribe returns the snapshot and registers the listener atomically.
/// </summary>
public sealed class JobOutputHub
{
    private const int MaxLines = 5000;

    private sealed class Store
    {
        public readonly object Lock = new();
        public readonly Queue<string> Lines = new();
        public Action<string>? Listeners;
    }

    private readonly ConcurrentDictionary<Guid, Store> _stores = new();

    public void Attach(Job job)
    {
        var s = _stores.GetOrAdd(job.Id, _ => new Store());
        job.Output += (_, line) =>
        {
            lock (s.Lock)
            {
                s.Lines.Enqueue(line);
                if (s.Lines.Count > MaxLines) s.Lines.Dequeue();
                s.Listeners?.Invoke(line);
            }
        };
    }

    public IDisposable Subscribe(Job job, Action<string> onLine, out string[] snapshot)
    {
        var s = _stores.GetOrAdd(job.Id, _ => new Store());
        lock (s.Lock)
        {
            snapshot = s.Lines.ToArray();
            s.Listeners += onLine;
        }
        return new Unsub(() => { lock (s.Lock) s.Listeners -= onLine; });
    }

    private sealed class Unsub : IDisposable
    {
        private Action? _a;
        public Unsub(Action a) => _a = a;
        public void Dispose() => Interlocked.Exchange(ref _a, null)?.Invoke();
    }
}
