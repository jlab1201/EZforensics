using System.Diagnostics;
using System.Numerics.Tensors;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Knowledge;

/// <summary>
/// Search over the store: FTS5 bm25 and vector cosine (brute force, in memory), merged by reciprocal rank fusion, then a small boost
/// for notes linked to the best notes. Without an embedder (or with a different model than the stored vectors) only text and graph are used.
/// </summary>
public sealed class HybridRetriever
{
    public const int Candidates = 30;
    public const int RrfK = 60;
    private const double GraphBoost = 0.004;

    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    private readonly KnowledgeStore _store;
    private readonly IEmbedder? _embedder;
    private readonly object _lock = new();

    private Dictionary<long, ChunkRow>? _chunks;
    private Dictionary<long, List<ChunkRow>>? _byNote;
    private Dictionary<long, (string Title, string Path)>? _notes;
    private Dictionary<long, List<(long Other, string Kind)>>? _adj;
    private bool _vectors;

    public HybridRetriever(KnowledgeStore store, IEmbedder? embedder)
    {
        _store = store;
        _embedder = embedder;
    }

    /// <summary>True when vector search is available (embedder present and matching the stored vectors).</summary>
    public bool UsesVectors { get { Load(); return _vectors; } }

    /// <summary>Drops the in-memory copy; the next search reloads it (call after an import or any note status change).</summary>
    public void Invalidate()
    {
        lock (_lock) _chunks = null;
    }

    private void Load()
    {
        lock (_lock)
        {
            if (_chunks != null) return;
            var rows = _store.Chunks(status: "accepted");
            _chunks = rows.ToDictionary(c => c.Id);
            _byNote = rows.GroupBy(c => c.NoteId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ord).ToList());
            _notes = _store.Notes(status: "accepted").ToDictionary(n => n.Id, n => (n.Title, n.Path));
            _adj = new();
            foreach (var (s, d, k) in _store.Links())
            {
                if (!_adj.TryGetValue(s, out var a)) _adj[s] = a = new();
                a.Add((d, k));
                if (!_adj.TryGetValue(d, out var b)) _adj[d] = b = new();
                b.Add((s, k));
            }
            _vectors = _embedder != null && _store.GetMeta("embed_model") == _embedder.ModelId && rows.Any(c => c.Embedding != null);
        }
    }

    /// <summary>FTS5 query from user text: each word quoted, joined with OR. Empty when the text has no words.</summary>
    public static string SanitizeFts(string query) =>
        string.Join(" OR ", Word.Matches(query).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).Select(w => "\"" + w + "\""));

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        { "what", "whats", "the", "and", "for", "are", "how", "does", "did", "can", "you", "with", "that", "this", "from", "about", "explain", "tell", "why", "who", "when", "which" };

    /// <summary>
    /// Relevance flag for auto-attached context: with vectors the chunk must be found by both full-text and vector search;
    /// without, by full-text in the top 3 and matching the query's meaningful words (at least two when it has two or more).
    /// </summary>
    private bool IsStrong(ChunkRow c, string title, string query, Dictionary<long, int> textRank, HashSet<long> vecSet)
    {
        if (!textRank.TryGetValue(c.Id, out var rank)) return false;
        if (_vectors) return vecSet.Contains(c.Id);
        if (rank > 3) return false;
        var words = Word.Matches(query).Select(m => m.Value).Where(w => w.Length >= 3 && !Stop.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (words.Count == 0) return false;
        var hay = title + " " + c.Heading + " " + c.Text;
        var hit = words.Count(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
        return hit >= Math.Min(2, words.Count);
    }

    public KnowledgeResult Search(string query, int k = 6, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        Load();
        lock (_lock)
        {
            var score = new Dictionary<long, double>();
            var textRank = new Dictionary<long, int>();
            var vecSet = new HashSet<long>();
            void Fuse(IEnumerable<long> ranked)
            {
                var rank = 0;
                foreach (var id in ranked) { score[id] = score.GetValueOrDefault(id) + 1.0 / (RrfK + ++rank); }
            }

            var fts = SanitizeFts(query);
            var nFts = 0;
            if (fts.Length > 0)
            {
                var ids = _store.SearchChunks(fts, Candidates).Where(_chunks!.ContainsKey).ToList();
                nFts = ids.Count;
                Fuse(ids);
                for (var i = 0; i < ids.Count; i++) textRank[ids[i]] = i + 1;
                Fuse(_store.SearchTitles(fts, 10).Where(_byNote!.ContainsKey).Select(n => _byNote[n][0].Id));
            }

            var nVec = 0;
            if (_vectors && query.Trim().Length > 0)
            {
                var q = _embedder!.EmbedQuery(query, ct);
                var top = _chunks!.Values.Where(c => c.Embedding != null && c.Embedding.Length == q.Length)
                    .Select(c => (c.Id, S: TensorPrimitives.Dot(q, c.Embedding!)))
                    .OrderByDescending(x => x.S).Take(Candidates).Select(x => x.Id).ToList();
                nVec = top.Count;
                Fuse(top);
                vecSet.UnionWith(top);
            }

            var best = score.OrderByDescending(x => x.Value).Select(x => _chunks![x.Key].NoteId).Distinct().Take(3).ToList();
            var neighbours = new Dictionary<long, int>();
            foreach (var n in best)
                if (_adj!.TryGetValue(n, out var list))
                    foreach (var (o, kind) in list)
                        neighbours[o] = neighbours.GetValueOrDefault(o) + (kind is LinkKinds.Wikilink or LinkKinds.Moc ? 2 : 1);

            foreach (var o in neighbours.Keys)
                if (_byNote!.TryGetValue(o, out var cs))
                {
                    var scored = cs.Where(c => score.ContainsKey(c.Id)).ToList();
                    if (scored.Count == 0) scored.Add(cs[0]);
                    foreach (var c in scored) score[c.Id] = score.GetValueOrDefault(c.Id) + GraphBoost;
                }

            var hits = score.OrderByDescending(x => x.Value).Take(k).Select(x =>
            {
                var c = _chunks![x.Key];
                var (title, path) = _notes![c.NoteId];
                return new KnowledgeHit(c.NoteId, title, path, c.Heading, c.Text, x.Value, IsStrong(c, title, query, textRank, vecSet));
            }).ToList();

            var shown = hits.Select(h => h.NoteId).ToHashSet();
            var related = neighbours.Where(n => !shown.Contains(n.Key) && _notes!.ContainsKey(n.Key))
                .OrderByDescending(n => n.Value).ThenBy(n => _notes![n.Key].Title, StringComparer.OrdinalIgnoreCase)
                .Take(5).Select(n => _notes![n.Key].Title).ToList();

            KnowledgeLog.Info($"Knowledge search: {sw.ElapsedMilliseconds} ms, {hits.Count} hits (text {nFts}, vector {nVec}), query '{(query.Length > 60 ? query[..60] : query)}'");
            return new KnowledgeResult(hits, related);
        }
    }
}
