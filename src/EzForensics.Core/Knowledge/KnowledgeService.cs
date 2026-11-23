namespace EzForensics.Core.Knowledge;

/// <summary>A note found by title.</summary>
public sealed record KnowledgeNote(string Title, string Path, string Body);

/// <summary>What the assistant needs from the knowledge base (so tests can replace it).</summary>
public interface IKnowledgeBase
{
    bool IsEmpty { get; }
    KnowledgeResult Search(string query, int k);
    KnowledgeNote? GetNote(string title);
    /// <summary>Titles of notes linked to the note (empty when unknown).</summary>
    IReadOnlyList<string> Related(string title);
    /// <summary>Titles that look like <paramref name="title"/> (for a "not found" answer).</summary>
    IReadOnlyList<string> Suggest(string title);
}

/// <summary>
/// App-wide, lazily opened knowledge base. Asking never creates files: a missing or empty database is simply empty.
/// Thread-safe (one lock around the store).
/// </summary>
public sealed class KnowledgeService : IKnowledgeBase, IDisposable
{
    private static readonly Lazy<KnowledgeService> _default = new(() => new KnowledgeService());
    public static KnowledgeService Default => _default.Value;

    private readonly string _path;
    private readonly Func<IEmbedder?> _embedderFactory;
    private readonly object _lock = new();
    private KnowledgeStore? _store;
    private HybridRetriever? _retriever;
    private int _count = -1;
    private string? _rev;

    public KnowledgeService(string? path = null, Func<IEmbedder?>? embedderFactory = null)
    {
        _path = path ?? KnowledgeStore.DefaultPath;
        _embedderFactory = embedderFactory ?? (() => OnnxEmbedder.TryCreate());
    }

    private bool Open()
    {
        if (_store == null)
        {
            if (!File.Exists(_path)) return false;
            try
            {
                _store = new KnowledgeStore(_path);
                _retriever = new HybridRetriever(_store, _embedderFactory());
            }
            catch (Exception ex)
            {
                KnowledgeLog.Info("Knowledge base could not be opened: " + ex.Message);
                _store?.Dispose(); _store = null; _retriever = null;
                return false;
            }
        }
        var n = _store.NoteCount();
        var rev = _store.GetMeta("rev");
        if (n != _count || rev != _rev) { _count = n; _rev = rev; _retriever!.Invalidate(); } // an import or removal happened since the last look
        return n > 0;
    }

    /// <summary>Forget cached vectors so the next question sees content imported or removed meanwhile.</summary>
    public void Invalidate() { lock (_lock) { _count = -1; _rev = null; } }

    public bool IsEmpty { get { lock (_lock) return !Open(); } }

    public KnowledgeResult Search(string query, int k)
    {
        lock (_lock) return Open() ? _retriever!.Search(query, k) : new KnowledgeResult(Array.Empty<KnowledgeHit>(), Array.Empty<string>());
    }

    public KnowledgeNote? GetNote(string title)
    {
        lock (_lock)
        {
            if (!Open() || FindId(title) is not { } id) return null;
            return _store!.Note(id) is { } n ? new KnowledgeNote(n.Title, n.Path, n.Body) : null;
        }
    }

    public IReadOnlyList<string> Related(string title)
    {
        lock (_lock)
        {
            if (!Open() || FindId(title) is not { } id) return Array.Empty<string>();
            var titles = _store!.Notes(status: "accepted").ToDictionary(n => n.Id, n => n.Title);
            return _store.Links().Where(l => l.Src == id || l.Dst == id).Select(l => l.Src == id ? l.Dst : l.Src)
                .Distinct().Where(titles.ContainsKey).Select(o => titles[o]).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public IReadOnlyList<string> Suggest(string title)
    {
        lock (_lock)
        {
            if (!Open()) return Array.Empty<string>();
            var t = (title ?? "").Trim();
            var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();
            return _store!.Notes(status: "accepted").Select(n => n.Title).Distinct()
                .Where(n => n.Contains(t, StringComparison.OrdinalIgnoreCase) || (t.Length > 0 && t.Contains(n, StringComparison.OrdinalIgnoreCase)) || words.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        }
    }

    /// <summary>
    /// A &lt;knowledge&gt; block holding the accepted notes <paramref name="noteIds"/> (in that order, each cut to fit), for a question
    /// the examiner asked about those notes (the Brain's Ask Spectre). Null when none of them is an accepted note.
    /// </summary>
    public string? NotesBlock(IReadOnlyList<long> noteIds, int maxChars)
    {
        lock (_lock)
        {
            if (!Open()) return null;
            var sb = new System.Text.StringBuilder("<knowledge>\n");
            var used = 0;
            foreach (var id in noteIds.Distinct())
            {
                if (_store!.Meta(id) is not { Status: "accepted" } || _store.Note(id) is not { } n) continue;
                var room = maxChars - sb.Length - "</knowledge>\n".Length;
                var head = $"[{n.Title}]\n";
                if (room <= head.Length + 40) break;
                var body = n.Body.Trim();
                if (head.Length + body.Length + 2 > room) body = body[..(room - head.Length - 3)] + "…";
                sb.Append(head).Append(body).Append("\n\n");
                used++;
            }
            if (used == 0) return null;
            return sb.Append("</knowledge>\n").ToString();
        }
    }

    private long? FindId(string title)
    {
        var t = (title ?? "").Trim();
        foreach (var n in _store!.Notes(status: "accepted"))
            if (string.Equals(n.Title, t, StringComparison.OrdinalIgnoreCase)) return n.Id;
        return null;
    }

    public void Dispose()
    {
        lock (_lock) { _store?.Dispose(); _store = null; _retriever = null; }
    }
}
