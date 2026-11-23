using EzForensics.Core.Knowledge;

namespace EzForensics.App.ViewModels.Learn;

/// <summary>The store, importer and intake an operation works with (opened for that operation, disposed after it).</summary>
public sealed record KnowledgeSession(KnowledgeStore Store, KnowledgeImporter Importer, KnowledgeIntake Intake);

/// <summary>
/// Opens the knowledge base for one operation at a time (like the old AI documents page did), so the Learn tabs share one door:
/// every add goes through <see cref="KnowledgeIntake"/>, readers are told to reload (<see cref="KnowledgeService"/>), and
/// <see cref="Changed"/> is raised on the thread that created the workspace (the UI thread).
/// </summary>
public sealed class KnowledgeWorkspace
{
    private readonly Func<IEmbedder?> _embedder;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public KnowledgeWorkspace(string? dbPath = null, Func<IEmbedder?>? embedder = null)
    {
        DbPath = dbPath ?? KnowledgeStore.DefaultPath;
        _embedder = embedder ?? (() => OnnxEmbedder.TryCreate());
    }

    private int _busy;

    /// <summary>True while a Learn tab (Intake, Inbox or Library) runs a job. They share it so only one job runs at a time.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>Raised when <see cref="IsBusy"/> may have changed (so every tab re-checks its commands).</summary>
    public event Action? BusyChanged;

    /// <summary>A tab calls this with true when its job starts and false when it ends.</summary>
    public void SetBusy(bool on)
    {
        if (on) Interlocked.Increment(ref _busy);
        else if (Volatile.Read(ref _busy) > 0) Interlocked.Decrement(ref _busy);
        BusyChanged?.Invoke();
    }

    public string DbPath { get; }

    public bool Exists => File.Exists(DbPath);

    /// <summary>Raised after an operation that changed the knowledge base.</summary>
    public event Action? Changed;

    /// <summary>Runs <paramref name="work"/> on the calling thread (small reads and edits). <paramref name="embed"/> loads the embedding model for the session.</summary>
    public T Run<T>(Func<KnowledgeSession, T> work, bool changes = false, bool embed = false)
    {
        using var store = new KnowledgeStore(DbPath);
        var embedder = embed ? _embedder() : null;
        using var _ = embedder as IDisposable;
        var importer = new KnowledgeImporter(store, embedder);
        var result = work(new KnowledgeSession(store, importer, new KnowledgeIntake(store, importer, embedder)));
        if (changes) NotifyChanged();
        return result;
    }

    /// <summary>Runs <paramref name="work"/> off the calling thread.</summary>
    public Task<T> RunAsync<T>(Func<KnowledgeSession, T> work, bool changes = false, bool embed = false) =>
        Task.Run(() => Run(work, changes, embed));

    /// <summary>Opens a session for an async operation (an add, a re-index) that does its own awaiting.</summary>
    public async Task<T> UseAsync<T>(Func<KnowledgeSession, Task<T>> work, bool changes = true, bool embed = true)
    {
        using var store = new KnowledgeStore(DbPath);
        var embedder = embed ? _embedder() : null;
        using var _ = embedder as IDisposable;
        var importer = new KnowledgeImporter(store, embedder);
        var result = await work(new KnowledgeSession(store, importer, new KnowledgeIntake(store, importer, embedder)));
        if (changes) NotifyChanged();
        return result;
    }

    /// <summary>Tells readers (the assistant, the Brain) and the Learn tabs that something changed.</summary>
    public void NotifyChanged()
    {
        if (DbPath == KnowledgeStore.DefaultPath) KnowledgeService.Default.Invalidate();
        if (_ui != null) _ui.Post(_ => Changed?.Invoke(), null); else Changed?.Invoke();
    }
}
