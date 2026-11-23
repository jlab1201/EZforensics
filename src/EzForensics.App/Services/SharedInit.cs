namespace EzForensics.App.Services;

/// <summary>
/// Runs an async initialisation once and shares it: concurrent callers get the same task. A failed run is forgotten, so the next
/// call starts a clean one (Retry). Used for the WebView2 environment, which the prewarm and the Learn page both ask for.
/// </summary>
public sealed class SharedInit<T>
{
    private readonly Func<Task<T>> _factory;
    private readonly object _gate = new();
    private Task<T>? _task;

    public SharedInit(Func<Task<T>> factory) => _factory = factory;

    public Task<T> GetAsync()
    {
        lock (_gate)
        {
            if (_task != null) return _task;
            var t = Run();
            _task = t;
            return t;
        }
    }

    private async Task<T> Run()
    {
        try { return await _factory().ConfigureAwait(true); }
        catch
        {
            lock (_gate) { _task = null; }   // forget the failure: the next call starts again
            throw;
        }
    }

    /// <summary>Forgets the result so the next call creates it again.</summary>
    public void Reset() { lock (_gate) _task = null; }
}
