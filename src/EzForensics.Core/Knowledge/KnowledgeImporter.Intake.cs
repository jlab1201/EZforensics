using System.Numerics.Tensors;
using EzForensics.Core.Analysis;

namespace EzForensics.Core.Knowledge;

/// <summary>
/// Turns an import into an intake: new notes are stored as <c>pending</c>, cleaned, given concepts and duplicate flags.
/// The counters tell the caller what happened to each file; <see cref="Pages"/> carries raw PDF pages (by note path) for the cleaner.
/// </summary>
public sealed class IntakeOptions
{
    public string AddedBy { get; init; } = CurrentIdentity.Name;
    public int Added { get; set; }
    public int AlreadyWaiting { get; set; }
    public int RejectedBefore { get; set; }
    public int Unchanged { get; set; }
    /// <summary>Ids of the pending notes this intake created.</summary>
    public List<long> NewIds { get; } = new();
    public Dictionary<string, IReadOnlyList<string>> Pages { get; } = new(StringComparer.Ordinal);
}

public sealed partial class KnowledgeImporter
{
    private const float NearCosine = 0.95f;
    private const double NearJaccard = 0.8;
    private const int LittleTextChars = 200;
    private const double NoiseRatio = 0.5;

    /// <summary>A pending replacement of an accepted note sits at a different path (paths are unique per source); Accept strips this suffix.</summary>
    internal const string PendingMark = "?pending=";

    private sealed record IntakePlan(string InsertPath, long? Replaces, long? DeletePending, double CleanRatio = 1.0);

    /// <summary>Counts a file that is already known by content hash (accepted, waiting or rejected). True when it needs no new row.</summary>
    internal bool SkipKnown(string hash, IntakeOptions o)
    {
        var hits = _store.NotesWithHash(hash);
        if (hits.Count == 0) return false;
        if (hits.Any(h => h.Status == "accepted")) o.Unchanged++;
        else if (hits.Any(h => h.Status == "pending")) o.AlreadyWaiting++;
        else o.RejectedBefore++;
        return true;
    }

    private IntakePlan PlanIntake(string rel, string hash, Dictionary<string, (long Id, string Hash)> existing)
    {
        if (!existing.TryGetValue(rel, out var e)) return new IntakePlan(rel, null, null);
        var mark = rel + PendingMark + hash[..Math.Min(8, hash.Length)];
        return _store.Meta(e.Id)?.Status switch
        {
            // a newer change supersedes an older pending one for the same document
            "accepted" => new IntakePlan(mark, e.Id, _store.PendingReplacing(e.Id).FirstOrDefault()?.Id),
            "pending" => new IntakePlan(rel, null, e.Id),
            _ => new IntakePlan(mark, null, null),
        };
    }

    /// <summary>File name of the stored copy of a note path (the pending mark has a character that file names cannot hold).</summary>
    internal static string CopyName(string notePath) => notePath.Replace('?', '~');

    private void TryDeleteCopy(long sourceId, string notePath)
    {
        try { File.Delete(Path.Combine(FilesDir(sourceId), CopyName(notePath))); } catch (Exception) { /* locked or gone: harmless */ }
    }

    /// <summary>A pending replacement was accepted: its copy takes the place of the old stored copy.</summary>
    internal void SwapPendingCopy(KnowledgeStore.NoteMeta m, string finalPath)
    {
        try
        {
            var from = Path.Combine(FilesDir(m.SourceId), CopyName(m.Path));
            if (File.Exists(from)) File.Move(from, Path.Combine(FilesDir(m.SourceId), CopyName(finalPath)), true);
        }
        catch (Exception ex) { KnowledgeLog.Info("Stored copy could not be replaced: " + ex.Message); }
    }

    /// <summary>A rejected note was deleted for good: its stored copy goes too (vault notes have none).</summary>
    internal void DropCopy(KnowledgeStore.NoteMeta m) => TryDeleteCopy(m.SourceId, m.Path);

    /// <summary>Accepted notes as seen by the duplicate check: mean vectors up front, word shingles on demand.</summary>
    private sealed class AcceptedIndex
    {
        private readonly KnowledgeStore _store;
        private readonly Dictionary<long, HashSet<int>> _shingles = new();
        public Dictionary<long, float[]> Means { get; }
        public List<long> Ids { get; }

        public AcceptedIndex(KnowledgeStore store)
        {
            _store = store;
            Ids = store.Notes(status: "accepted").Select(n => n.Id).ToList();
            Means = MeanVectors(store.Chunks(status: "accepted"));
        }

        public HashSet<int> Shingles(long id) =>
            _shingles.TryGetValue(id, out var s) ? s : _shingles[id] = Shingle(_store.NoteBody(id) ?? "");
    }

    private static HashSet<int> Shingle(string text)
    {
        var words = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<int>();
        if (words.Length == 0) return set;
        const int k = 5;
        if (words.Length < k) { set.Add(string.Join(" ", words).GetHashCode()); return set; }
        for (var i = 0; i + k <= words.Length; i++) set.Add(string.Join(" ", words, i, k).GetHashCode());
        return set;
    }

    private static double Jaccard(HashSet<int> a, HashSet<int> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count < b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    private static float[]? MeanOf(float[][]? vectors)
    {
        if (vectors == null || vectors.Length == 0) return null;
        var dims = vectors[0].Length;
        var sum = new float[dims];
        foreach (var v in vectors.Where(v => v.Length == dims)) TensorPrimitives.Add(sum, v, sum);
        var norm = MathF.Sqrt(TensorPrimitives.Dot(sum, sum));
        if (norm <= 0) return null;
        TensorPrimitives.Divide(sum, norm, sum);
        return sum;
    }

    /// <summary>After a pending note is stored: duplicate and quality flags, intake info and concepts.</summary>
    private void FinishIntake(long id, string body, IReadOnlyList<ChunkPiece> chunks, float[][]? vectors, IntakePlan plan, IntakeOptions o, AcceptedIndex accepted)
    {
        var flags = new List<string>();
        var same = _store.FindAcceptedByBody(body);
        if (same != null && same != plan.Replaces) flags.Add("same:" + same);
        else if (NearestAccepted(body, vectors, plan.Replaces, accepted) is { } near)
            flags.Add($"near:{near.Id}:{(int)Math.Round(near.Sim * 100)}");
        if (body.Trim().Length < LittleTextChars) flags.Add("little-text");
        if (plan.CleanRatio < NoiseRatio) flags.Add("noise");

        _store.SetIntakeInfo(id, DateTime.UtcNow.ToString("o"), o.AddedBy, flags, plan.CleanRatio, plan.Replaces);
        SaveConcepts(id, chunks.Select(c => c.Text));
    }

    private (long Id, double Sim)? NearestAccepted(string body, float[][]? vectors, long? skip, AcceptedIndex accepted)
    {
        var mine = MeanOf(vectors);
        HashSet<int>? myShingles = null;
        (long Id, double Sim)? best = null;
        foreach (var other in accepted.Ids)
        {
            if (other == skip) continue;
            double sim;
            double min;
            if (mine != null && accepted.Means.TryGetValue(other, out var v) && v.Length == mine.Length) { sim = TensorPrimitives.Dot(mine, v); min = NearCosine; }
            else { sim = Jaccard(myShingles ??= Shingle(body), accepted.Shingles(other)); min = NearJaccard; }
            if (sim >= min && (best == null || sim > best.Value.Sim)) best = (other, sim);
        }
        return best;
    }

    /// <summary>Extracts the concepts of the chunk texts and stores them for the note.</summary>
    internal void SaveConcepts(long noteId, IEnumerable<string> chunkTexts)
    {
        var counts = new Dictionary<long, int>();
        foreach (var h in ConceptExtractor.Extract(chunkTexts)) counts[_store.UpsertConcept(h.Kind, h.Key, h.Label)] = h.Count;
        _store.ReplaceNoteConcepts(noteId, counts);
    }

    /// <summary>Rebuilds the document links of a "files" source (so accepted documents link to each other).</summary>
    internal void RefreshDocumentLinks(long sourceId, IReadOnlyCollection<long>? newlyAccepted = null, CancellationToken ct = default)
    {
        var src = _store.Sources().FirstOrDefault(s => s.Id == sourceId);
        if (src is { Kind: FilesKind }) RebuildDocumentLinks(sourceId, ct);
        else if (src is { Kind: "vault" } && newlyAccepted is { Count: > 0 }) AddLinksForAccepted(sourceId, newlyAccepted);
    }
}
