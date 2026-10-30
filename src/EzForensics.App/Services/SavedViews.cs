using EzForensics.Core.Results;

namespace EzForensics.App.Services;

/// <summary>App-wide saved-view library (<c>saved-views.json</c>): loaded on first use, saved after every change.</summary>
public sealed class SavedViews
{
    public static SavedViews Instance { get; } = new();

    private readonly object _gate = new();
    private SavedViewLibrary? _lib;

    /// <summary>Raised (on the calling thread) after the library changed and was written.</summary>
    public event Action? Changed;

    private SavedViewLibrary Lib
    {
        get
        {
            lock (_gate)
            {
                if (_lib != null) return _lib;
                try { _lib = SavedViewStore.Load(); }
                catch (Exception ex) { AppLog.Error("Could not load saved views", ex); _lib = new SavedViewLibrary(); }
                return _lib;
            }
        }
    }

    /// <summary>Runs <paramref name="read"/> under the lock (do not keep the library past the call).</summary>
    public T Read<T>(Func<SavedViewLibrary, T> read) { lock (_gate) return read(Lib); }

    /// <summary>Applies <paramref name="change"/> under the lock, writes the file (errors are logged) and raises <see cref="Changed"/>.</summary>
    public T Mutate<T>(Func<SavedViewLibrary, T> change)
    {
        T result;
        lock (_gate)
        {
            result = change(Lib);
            try { SavedViewStore.Save(Lib); }
            catch (Exception ex) { AppLog.Error("Could not save saved views", ex); }
        }
        try { Changed?.Invoke(); } catch (Exception ex) { AppLog.Error("Saved views change handler failed", ex); }
        return result;
    }

    public void Mutate(Action<SavedViewLibrary> change) => Mutate<bool>(l => { change(l); return true; });

    public string? CategoryName(string signature) => Read(l => l.Category(signature)?.Name);

    /// <summary>Views of the category, copied so callers can enumerate outside the lock.</summary>
    public List<SavedView> ForSignature(string signature) => Read(l => l.ForSignature(signature));

    public SavedView? Find(string signature, string name) => Read(l => l.Find(signature, name));

    public string NextNumberedName(string signature) => Read(l => l.NextNumberedName(signature));

    /// <summary>Every category with its views (copies), for the manager.</summary>
    public List<(SavedFilterCategory Category, List<SavedView> Views)> Snapshot() =>
        Read(l => l.Categories.Select(c => (c, l.ForSignature(c.Signature))).ToList());
}
