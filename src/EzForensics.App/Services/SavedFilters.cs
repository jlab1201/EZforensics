using EzForensics.Core.Results;

namespace EzForensics.App.Services;

/// <summary>App-wide saved-filter library (<c>saved-filters.json</c>): loaded on first use, saved after every change.</summary>
public sealed class SavedFilters
{
    public static SavedFilters Instance { get; } = new();

    private readonly object _gate = new();
    private SavedFilterLibrary? _lib;

    /// <summary>Raised (on the calling thread) after the library changed and was written.</summary>
    public event Action? Changed;

    private SavedFilterLibrary Lib
    {
        get
        {
            lock (_gate)
            {
                if (_lib != null) return _lib;
                try { _lib = SavedFilterStore.Load(); }
                catch (Exception ex) { AppLog.Error("Could not load saved filters", ex); _lib = new SavedFilterLibrary(); }
                return _lib;
            }
        }
    }

    /// <summary>Runs <paramref name="read"/> under the lock (do not keep the library past the call).</summary>
    public T Read<T>(Func<SavedFilterLibrary, T> read) { lock (_gate) return read(Lib); }

    /// <summary>Applies <paramref name="change"/> under the lock, writes the file (errors are logged) and raises <see cref="Changed"/>.</summary>
    public T Mutate<T>(Func<SavedFilterLibrary, T> change)
    {
        T result;
        lock (_gate)
        {
            result = change(Lib);
            try { SavedFilterStore.Save(Lib); }
            catch (Exception ex) { AppLog.Error("Could not save saved filters", ex); }
        }
        try { Changed?.Invoke(); } catch (Exception ex) { AppLog.Error("Saved filters change handler failed", ex); }
        return result;
    }

    public void Mutate(Action<SavedFilterLibrary> change) => Mutate<bool>(l => { change(l); return true; });

    public string? CategoryName(string signature) => Read(l => l.Category(signature)?.Name);

    /// <summary>Filters of the category, copied so callers can enumerate outside the lock.</summary>
    public List<SavedFilter> ForSignature(string signature) => Read(l => l.ForSignature(signature));

    public SavedFilter? Find(string signature, string name) => Read(l => l.Find(signature, name));

    public string NextNumberedName(string signature) => Read(l => l.NextNumberedName(signature));

    /// <summary>Every category with its filters (copies), for the manager.</summary>
    public List<(SavedFilterCategory Category, List<SavedFilter> Filters)> Snapshot() =>
        Read(l => l.Categories.Select(c => (c, l.ForSignature(c.Signature))).ToList());
}
