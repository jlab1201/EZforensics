using System.Numerics.Tensors;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Knowledge;

/// <summary>Counts of what an import did.</summary>
public sealed record ImportResult(int Added, int Updated, int Removed, int Unchanged)
{
    public bool NothingChanged => Added == 0 && Updated == 0 && Removed == 0;
}

/// <summary>Chunks (and optionally embeddings) built earlier for a note, e.g. from a pack. Valid only for the note with this hash.</summary>
public sealed record PrebuiltNote(string Hash, IReadOnlyList<ChunkPiece> Chunks, IReadOnlyList<float[]>? Embeddings);

/// <summary>
/// Imports a vault folder (or the notes of a pack) into the store. Incremental by note hash: unchanged notes are skipped, changed ones
/// re-chunked and re-embedded, removed ones deleted. Then links are rebuilt. With no embedder, notes are stored without vectors.
/// </summary>
public sealed partial class KnowledgeImporter
{
    public const int EmbedBatch = 32;
    private const int MaxMentionNotes = 5000;
    private const int MaxSemanticNotes = 3000;

    private readonly KnowledgeStore _store;
    private readonly IEmbedder? _embedder;

    public KnowledgeImporter(KnowledgeStore store, IEmbedder? embedder)
    {
        _store = store;
        _embedder = embedder;
    }

    public Task<ImportResult> ImportFolderAsync(string path, string name, IProgress<(int done, int total, string item)>? progress, CancellationToken ct,
        IntakeOptions? intake = null)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Vault folder not found: " + path);
        return Task.Run(() => ImportNotes(name, "vault", path, MarkdownVaultReader.ReadFolder(path), null, null, progress, ct, intake: intake), ct);
    }

    /// <summary>
    /// Intake of only the given vault notes (a subset of the folder): they come in as pending, nothing of the source is removed, and
    /// no links are built or replaced (ReplaceLinks would wipe the links of notes outside the subset).
    /// </summary>
    internal Task ImportVaultSubsetAsync(string folder, string name, IReadOnlyList<VaultNote> notes,
        IProgress<(int done, int total, string item)>? progress, CancellationToken ct, IntakeOptions intake) => Task.Run(() =>
    {
        ImportNotes(name, "vault", folder, notes, null, null, progress, ct, buildLinks: false, intake: intake);
        BumpRevision();
    }, ct);

    public Task RemoveSourceAsync(string name) => Task.Run(() =>
    {
        if (_store.FindSource(name) is not { } id) return;
        _store.DeleteSource(id);
        _store.SetMeta("src_status:" + id, "");
        var dir = FilesDir(id);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { /* locked file: left behind, harmless */ }
        BumpRevision();
    });

    /// <summary>
    /// The source an import writes to. A source never changes kind (a clash gets a new name, see <see cref="KnowledgeStore.UpsertSource"/>),
    /// and two different folders with the same folder name stay apart (a folder that moved, whose old folder is gone, keeps its source).
    /// Packs match by name, so an updated pack from another place updates its source.
    /// </summary>
    private long SourceFor(string name, string kind, string path) =>
        _store.UpsertSource(name, kind, path, existing => kind != "pack" && path.Length > 0 && existing.Path.Length > 0
            && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing.Path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(existing.Path));

    /// <summary>Creates (or finds) the source row of a folder without importing anything, so it survives a restart before its first import.</summary>
    public long SaveSource(string name, string kind, string path) => SourceFor(name, kind, path);

    /// <summary>Tells readers (KnowledgeService) that content changed even when the note count did not.</summary>
    private void BumpRevision() => _store.SetMeta("rev", Guid.NewGuid().ToString("N"));

    /// <summary>Core import. <paramref name="prebuilt"/> is keyed by note path; <paramref name="prebuiltModel"/> is the model of its embeddings.</summary>
    public ImportResult ImportNotes(string name, string kind, string path, IReadOnlyList<VaultNote> notes,
        IReadOnlyDictionary<string, PrebuiltNote>? prebuilt, string? prebuiltModel,
        IProgress<(int done, int total, string item)>? progress, CancellationToken ct,
        bool removeMissing = true, bool buildLinks = true, IntakeOptions? intake = null)
    {
        if (intake != null) removeMissing = false;
        var sid = SourceFor(name, kind, path);
        var existing = _store.NotesOf(sid);
        var incoming = notes.ToDictionary(n => n.RelPath, StringComparer.Ordinal);

        // Vectors from a pack are usable when there is no embedder here (search then falls back to text) or the model is the same.
        var prebuiltUsable = prebuiltModel != null && (_embedder == null || _embedder.ModelId == prebuiltModel);
        var effectiveModel = _embedder?.ModelId ?? (prebuiltUsable ? prebuiltModel : null);
        var modelChanged = effectiveModel != null && _store.NoteCount() > 0 && _store.GetMeta("embed_model") is { } m && m != effectiveModel;
        var needVectors = _store.NotesWithoutEmbeddings(sid);

        // distilled children (parent_id set) sit at paths the folder never has: a re-import must not remove them
        var removed = removeMissing ? existing.Where(e => !incoming.ContainsKey(e.Key) && _store.Meta(e.Value.Id) is { Status: "accepted", ParentId: null }).ToList() : new List<KeyValuePair<string, (long Id, string Hash)>>();
        foreach (var r in removed) _store.DeleteNote(r.Value.Id);

        var todo = new List<VaultNote>();
        var unchanged = 0;
        var plans = new Dictionary<string, IntakePlan>(StringComparer.Ordinal);
        var batchHashes = new HashSet<string>(StringComparer.Ordinal);
        // Legacy imports only manage accepted notes: pending and rejected ones are never rewritten or removed by them.
        var notAccepted = intake == null ? _store.NotesByStatus("pending", sid).Concat(_store.NotesByStatus("rejected", sid)).Select(m => m.Id).ToHashSet() : new HashSet<long>();
        foreach (var n in notes)
        {
            if (intake == null && existing.TryGetValue(n.RelPath, out var ne) && notAccepted.Contains(ne.Id)) { unchanged++; continue; }
            if (intake != null)
            {
                if (SkipKnown(n.Hash, intake)) continue;
                if (!batchHashes.Add(n.Hash)) { intake.AlreadyWaiting++; continue; } // same content twice in one call
                plans[n.RelPath] = PlanIntake(n.RelPath, n.Hash, existing);
                todo.Add(n);
                continue;
            }
            if (existing.TryGetValue(n.RelPath, out var e) && e.Hash == n.Hash && !modelChanged
                && !(_embedder != null && needVectors.Contains(e.Id))) { unchanged++; continue; }
            todo.Add(n);
        }

        int added = 0, updated = 0, done = 0;
        var group = new List<(VaultNote Note, List<ChunkPiece> Chunks, float[][]? Vectors)>();
        var accepted = new Lazy<AcceptedIndex>(() => new AcceptedIndex(_store));
        void FlushGroup()
        {
            if (group.Count == 0) return;
            var pending = new List<string>();
            foreach (var g in group) if (g.Vectors == null) pending.AddRange(g.Chunks.Select(c => c.EmbedText));
            var vecs = _embedder != null && pending.Count > 0 ? EmbedAll(pending, ct) : Array.Empty<float[]>();
            var at = 0;
            foreach (var g in group)
            {
                var v = g.Vectors;
                if (v == null && _embedder != null)
                {
                    v = new float[g.Chunks.Count][];
                    for (var i = 0; i < v.Length; i++) v[i] = vecs[at++];
                }
                if (intake != null)
                {
                    var plan = plans[g.Note.RelPath];
                    if (plan.DeletePending is { } dp)
                    {
                        var oldPending = _store.Meta(dp);
                        _store.DeleteNote(dp);
                        if (oldPending != null && oldPending.Path != plan.InsertPath) TryDeleteCopy(sid, oldPending.Path);
                    }
                    var nid = _store.InsertNote(sid, g.Note with { RelPath = plan.InsertPath }, g.Chunks, v, "pending");
                    FinishIntake(nid, g.Note.Body, g.Chunks, v, plan, intake, accepted.Value);
                    intake.Added++;
                    intake.NewIds.Add(nid);
                    added++;
                }
                else
                {
                    if (existing.TryGetValue(g.Note.RelPath, out var old)) { _store.DeleteNote(old.Id); updated++; } else added++;
                    _store.InsertNote(sid, g.Note, g.Chunks, v);
                }
                progress?.Report((++done, todo.Count, g.Note.Title));
            }
            group.Clear();
        }

        var chunksInGroup = 0;
        foreach (var n in todo)
        {
            ct.ThrowIfCancellationRequested();
            List<ChunkPiece> chunks;
            float[][]? vectors = null;
            var n2 = n;
            if (prebuilt != null && prebuilt.TryGetValue(n.RelPath, out var pb) && pb.Hash == n.Hash)
            {
                chunks = pb.Chunks.ToList();
                if (prebuiltUsable && pb.Embeddings != null && pb.Embeddings.Count == chunks.Count) vectors = pb.Embeddings.ToArray();
            }
            else if (intake != null)
            {
                var clean = DocumentCleaner.Clean(n.Body, n.Title, intake.Pages.GetValueOrDefault(n.RelPath));
                var plan = plans[n.RelPath];
                plans[n.RelPath] = plan with { CleanRatio = clean.CleanRatio };
                n2 = n with { Body = clean.Text };
                chunks = Chunker.Chunk(n.Title, n2.Body);
            }
            else chunks = Chunker.Chunk(n.Title, n.Body);
            group.Add((n2, chunks, vectors));
            chunksInGroup += chunks.Count;
            if (chunksInGroup >= EmbedBatch) { FlushGroup(); chunksInGroup = 0; }
        }
        FlushGroup();

        if (effectiveModel != null && (_embedder != null || prebuiltUsable))
        {
            _store.SetMeta("embed_model", effectiveModel);
            _store.SetMeta("embed_dims", (_embedder?.Dims ?? _store.Chunks(sid).FirstOrDefault(c => c.Embedding != null)?.Embedding?.Length ?? 0).ToString());
        }
        if (buildLinks) RebuildLinks(sid, notes, ct);
        BumpRevision();
        return new ImportResult(added, updated, removed.Count, unchanged);
    }

    private float[][] EmbedAll(List<string> texts, CancellationToken ct)
    {
        var all = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i += EmbedBatch)
        {
            ct.ThrowIfCancellationRequested();
            all.AddRange(_embedder!.Embed(texts.GetRange(i, Math.Min(EmbedBatch, texts.Count - i)), ct));
        }
        return all.ToArray();
    }

    // ---- links

    /// <summary>The wikilink/moc, tag and folder-note links of the notes (resolved against ids/titles/byFile); the per-note logic of <see cref="RebuildLinks"/>.</summary>
    private static void StructuralLinks(IReadOnlyList<VaultNote> notes, Dictionary<string, (long Id, string Hash)> ids, Dictionary<string, long> titles,
        Dictionary<string, long> byFile, HashSet<(long, long, string)> links, HashSet<(long, long)> linked)
    {
        foreach (var n in notes)
        {
            if (!ids.TryGetValue(n.RelPath, out var src)) continue;
            foreach (var l in n.Links)
                if ((titles.TryGetValue(l.Target, out var dst) || byFile.TryGetValue(l.Target, out dst)) && dst != src.Id)
                {
                    links.Add((src.Id, dst, l.Kind));
                    linked.Add((src.Id, dst));
                }
        }

        // tag: notes sharing a tag (small groups only)
        foreach (var grp in notes.Where(n => ids.ContainsKey(n.RelPath)).SelectMany(n => n.Tags.Select(t => (Tag: t.ToLowerInvariant(), Id: ids[n.RelPath].Id)))
                     .GroupBy(x => x.Tag).Where(g => g.Count() is >= 2 and <= 20))
        {
            var list = grp.Select(x => x.Id).Distinct().OrderBy(x => x).ToList();
            for (var a = 0; a < list.Count; a++)
                for (var b = a + 1; b < list.Count; b++) links.Add((list[a], list[b], LinkKinds.Tag));
        }

        // structure: a note links to the note named like its folder (folder note)
        foreach (var n in notes)
        {
            if (!ids.TryGetValue(n.RelPath, out var src)) continue;
            var dir = Path.GetDirectoryName(n.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(dir)) continue;
            var folder = Path.GetFileName(dir);
            if (folder.Length > 0 && (titles.TryGetValue(folder, out var fn) || byFile.TryGetValue(folder, out fn)) && fn != src.Id)
                links.Add((src.Id, fn, LinkKinds.Structure));
        }

    }

    /// <summary>
    /// Additive links for notes just accepted into a vault source: their wikilink/moc, tag and folder-note links, both ways, against the
    /// accepted notes of the source only. Only links that involve a new note are inserted; nothing is deleted. Mention and semantic
    /// links are skipped (they need the whole source; Re-index or a full add builds them).
    /// </summary>
    internal void AddLinksForAccepted(long sid, IReadOnlyCollection<long> newIds)
    {
        var accepted = _store.NotesByStatus("accepted", sid);
        var ids = new Dictionary<string, (long Id, string Hash)>(StringComparer.Ordinal);
        var titles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<VaultNote>();
        foreach (var m in accepted)
        {
            if (_store.Note(m.Id) is not { } n) continue;
            ids[m.Path] = (m.Id, m.Hash);
            titles.TryAdd(m.Title, m.Id);
            byFile.TryAdd(Path.GetFileNameWithoutExtension(m.Path), m.Id);
            var kind = m.Title.EndsWith("MOC", StringComparison.OrdinalIgnoreCase) ? LinkKinds.Moc : LinkKinds.Wikilink;
            var links = MarkdownVaultReader.Parse(m.Path, n.Body).Links.Select(l => l with { Kind = kind }).ToList();
            var tags = n.Tags.Length == 0 ? Array.Empty<string>() : n.Tags.Split(',');
            notes.Add(new VaultNote(m.Path, m.Title, n.Topic, tags, m.Hash, n.Body, links));
        }
        var found = new HashSet<(long, long, string)>();
        StructuralLinks(notes, ids, titles, byFile, found, new HashSet<(long, long)>());
        _store.AddLinks(found.Where(l => newIds.Contains(l.Item1) || newIds.Contains(l.Item2)));
    }

    private void RebuildLinks(long sid, IReadOnlyList<VaultNote> notes, CancellationToken ct)
    {
        var ids = _store.NotesOf(sid);
        var titles = _store.TitleIndex(sid);
        var byFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in notes) if (ids.TryGetValue(n.RelPath, out var i)) byFile.TryAdd(Path.GetFileNameWithoutExtension(n.RelPath), i.Id);

        var links = new HashSet<(long, long, string)>();
        var linked = new HashSet<(long, long)>();
        StructuralLinks(notes, ids, titles, byFile, links, linked);

        // mention: the body names another note's title
        if (notes.Count <= MaxMentionNotes)
        {
            var named = notes.Where(n => n.Title.Length >= 6 && ids.ContainsKey(n.RelPath)).ToList();
            foreach (var n in notes)
            {
                ct.ThrowIfCancellationRequested();
                if (!ids.TryGetValue(n.RelPath, out var src)) continue;
                foreach (var o in named)
                {
                    var dst = ids[o.RelPath].Id;
                    if (dst == src.Id || linked.Contains((src.Id, dst))) continue;
                    if (Regex.IsMatch(n.Body, @"(?<!\w)" + Regex.Escape(o.Title) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        links.Add((src.Id, dst, LinkKinds.Mention));
                }
            }
        }

        // semantic: nearest notes by mean chunk vector
        if (notes.Count <= MaxSemanticNotes) AddSemantic(sid, links);

        _store.ReplaceLinks(sid, links);
    }

    private void AddSemantic(long sid, HashSet<(long, long, string)> links)
    {
        var means = new Dictionary<long, float[]>();
        foreach (var g in _store.Chunks(sid).Where(c => c.Embedding != null).GroupBy(c => c.NoteId))
        {
            var sum = new float[g.First().Embedding!.Length];
            foreach (var c in g) TensorPrimitives.Add(sum, c.Embedding!, sum);
            var norm = MathF.Sqrt(TensorPrimitives.Dot(sum, sum));
            if (norm > 0) TensorPrimitives.Divide(sum, norm, sum);
            means[g.Key] = sum;
        }
        var keys = means.Keys.ToList();
        foreach (var a in keys)
        {
            var best = keys.Where(b => b != a).Select(b => (b, s: TensorPrimitives.Dot(means[a], means[b])))
                .Where(x => x.s >= 0.8f).OrderByDescending(x => x.s).Take(3);
            foreach (var (b, _) in best) links.Add((a, b, LinkKinds.Semantic));
        }
    }
}
