using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EzForensics.Core.Knowledge.Parsers;

namespace EzForensics.Core.Knowledge;

/// <summary>Result of importing loose files: the counts plus one error text per file that could not be used.</summary>
public sealed record FilesImportResult(ImportResult Result, IReadOnlyList<(string File, string Error)> Errors);

public sealed partial class KnowledgeImporter
{
    public const string FilesKind = "files";
    private const int DocNeighbours = 5;
    private const float DocSemanticMin = 0.75f;

    /// <summary>Folder holding the copies of the files of a source, next to the database.</summary>
    public string FilesDir(long sourceId) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_store.FilePath))!, "files", sourceId.ToString());

    /// <summary>
    /// Adds documents (PDF, Markdown, TXT, HTML) to the source <paramref name="sourceName"/>, keeping earlier files of that source.
    /// Files are copied so Re-index keeps working when the originals move. The same file again is a no-op. Unusable files are
    /// reported in the result (and in the source status), never silently dropped.
    /// </summary>
    public Task<FilesImportResult> ImportFilesAsync(IReadOnlyList<string> files, string sourceName,
        IProgress<(int done, int total, string item)>? progress, CancellationToken ct, IntakeOptions? intake = null)
        => Task.Run(() => ImportFiles(files, sourceName, progress, ct, intake), ct);

    /// <summary>
    /// Adds documents found in a folder to the folder's source: a vault (when the folder has Markdown notes) or a files source.
    /// </summary>
    internal Task<FilesImportResult> ImportFolderFilesAsync(IReadOnlyList<string> files, string sourceName, string kind, string folder,
        IProgress<(int done, int total, string item)>? progress, CancellationToken ct, IntakeOptions intake)
        => Task.Run(() => ImportFiles(files, sourceName, progress, ct, intake, kind, folder), ct);

    /// <summary>
    /// Re-parses the stored copies of the accepted documents of a source (for example after the parsers improved), with the intake
    /// cleanup and concepts. Each document is replaced in place, so it keeps its id (links, layout), status, edited title and tags and
    /// intake info. Nothing is deleted up front: a missing copy or a parse error keeps the old version and is reported, and a cancel or
    /// a failure part-way leaves the documents not yet done as they were. Stored copies that no note owns (left behind by an older
    /// re-index) come back as pending documents, for review.
    /// </summary>
    public Task<FilesImportResult> ReindexFilesAsync(string sourceName, IProgress<(int done, int total, string item)>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var src = _store.SourceByName(sourceName) ?? throw new InvalidOperationException("Unknown source: " + sourceName);
            if (src.Kind != FilesKind) throw new InvalidOperationException($"{sourceName} is not a documents source.");
            var sid = src.Id;
            var dir = FilesDir(sid);
            var errors = new List<(string, string)>();
            const string Kept = "; the document was kept as it was";

            // Only accepted documents are re-parsed (and stay accepted); pending and rejected ones keep their state and are not touched.
            var accepted = _store.NotesByStatus("accepted", sid);
            int updated = 0, done = 0, chunksInBatch = 0;
            var batch = new List<(long Id, string Title, string Hash, string Body, double Ratio, List<ChunkPiece> Chunks)>();
            void Flush()
            {
                if (batch.Count == 0) return;
                var texts = batch.SelectMany(b => b.Chunks.Select(c => c.EmbedText)).ToList();
                var vecs = _embedder != null && texts.Count > 0 ? EmbedAll(texts, ct) : null;
                var at = 0;
                foreach (var b in batch)
                {
                    float[][]? v = null;
                    if (vecs != null) { v = vecs[at..(at + b.Chunks.Count)]; at += b.Chunks.Count; }
                    _store.InTransaction(() =>
                    {
                        _store.ReplaceNoteContent(b.Id, b.Title, b.Hash, b.Body, b.Ratio, b.Chunks, v);
                        SaveConcepts(b.Id, b.Chunks.Select(c => c.Text));
                    });
                    updated++;
                }
                batch.Clear();
                chunksInBatch = 0;
            }
            foreach (var m in accepted)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((++done, accepted.Count, m.Title));
                var copy = Path.Combine(dir, CopyName(m.Path));
                try
                {
                    if (!File.Exists(copy)) { errors.Add((m.Path, "Stored copy is missing" + Kept)); continue; }
                    var parser = DocumentParsers.For(copy);
                    if (parser == null) { errors.Add((m.Path, "Unsupported file type: " + Path.GetExtension(copy) + Kept)); continue; }
                    var parsed = parser.Parse(copy);
                    if (!parsed.Ok) { errors.Add((m.Path, (parsed.Error ?? "No text found") + Kept)); continue; }
                    var title = _store.IsEdited(m.Id) ? m.Title : parsed.Title;
                    var clean = DocumentCleaner.Clean(parsed.ToMarkdown(), parsed.Title, parsed.Pages);
                    var chunks = Chunker.Chunk(title, clean.Text);
                    batch.Add((m.Id, title, HashFile(copy), clean.Text, clean.CleanRatio, chunks));
                    chunksInBatch += chunks.Count;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add((m.Path, ex.Message + Kept)); continue; }
                if (chunksInBatch >= EmbedBatch) Flush();
            }
            Flush();

            // Copies no note owns: back to the Inbox as pending (never straight to accepted).
            var owned = _store.NotesOf(sid).Keys.Select(CopyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orphans = Directory.Exists(dir)
                ? Directory.GetFiles(dir).Where(f => !owned.Contains(Path.GetFileName(f)) && !Path.GetFileName(f).Contains(CopyName(PendingMark), StringComparison.Ordinal)).ToList()
                : new List<string>();
            var recovered = 0;
            if (orphans.Count > 0)
            {
                var o = new IntakeOptions();
                errors.AddRange(ImportFiles(orphans, sourceName, null, ct, o).Errors);
                recovered = o.Added;
            }

            if (_embedder != null && updated > 0)
            {
                _store.SetMeta("embed_model", _embedder.ModelId);
                _store.SetMeta("embed_dims", _embedder.Dims.ToString());
            }
            RebuildDocumentLinks(sid, ct);
            _store.SetMeta("src_status:" + sid, errors.Count == 0 ? "" : string.Join("; ", errors.Select(e => Path.GetFileName(e.Item1) + ": " + e.Item2)));
            BumpRevision();
            return new FilesImportResult(new ImportResult(recovered, updated, 0, 0), errors);
        }, ct);

    private FilesImportResult ImportFiles(IReadOnlyList<string> files, string sourceName, IProgress<(int, int, string)>? progress, CancellationToken ct,
        IntakeOptions? intake = null, string kind = FilesKind, string path = "")
    {
        var sid = SourceFor(sourceName, kind, path);
        var dir = FilesDir(sid);
        Directory.CreateDirectory(dir);
        var existing = _store.NotesOf(sid);
        var errors = new List<(string, string)>();
        var byPath = new Dictionary<string, VaultNote>(StringComparer.Ordinal);
        var fresh = new HashSet<string>(StringComparer.Ordinal); // intake: the files read in this call

        // Earlier documents stay (rebuilt from the store, no re-parse).
        foreach (var (rel, e) in existing)
            if (_store.Note(e.Id) is { } n)
                byPath[rel] = new VaultNote(rel, n.Title, n.Topic, n.Tags.Length == 0 ? Array.Empty<string>() : n.Tags.Split(','), e.Hash, n.Body, Array.Empty<VaultLink>());

        var seen = 0;
        var batchHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((++seen, files.Count, Path.GetFileName(src)));
            try
            {
                var parser = DocumentParsers.For(src);
                if (parser == null) { errors.Add((src, "Unsupported file type: " + Path.GetExtension(src))); continue; }
                if (!File.Exists(src)) { errors.Add((src, "File not found")); continue; }
                var hash = HashFile(src);
                var rel = Path.GetFileName(src);
                if (byPath.TryGetValue(rel, out var prev) && prev.Hash != hash)
                {
                    // Same name, different content: a changed file replaces the old one only when it is the same original path;
                    // otherwise keep both under distinct names.
                    var oldCopy = Path.Combine(dir, rel);
                    if (File.Exists(oldCopy) && HashFile(oldCopy) != hash && !SameOriginal(sid, rel, src)) rel = Path.GetFileNameWithoutExtension(rel) + "-" + hash[..6] + Path.GetExtension(rel);
                }
                if (intake != null)
                {
                    if (SkipKnown(hash, intake)) continue; // counted as waiting / rejected / unchanged
                    if (!batchHashes.Add(hash)) { intake.AlreadyWaiting++; continue; } // same content twice in one call
                }
                else if (byPath.TryGetValue(rel, out var same) && same.Hash == hash) continue; // already imported: no-op

                var parsed = parser.Parse(src);
                if (!parsed.Ok) { errors.Add((src, parsed.Error ?? "No text found")); continue; }
                // A change that is only pending gets its own copy name; the accepted document's copy is swapped when it is accepted.
                var copyName = intake != null ? CopyName(PlanIntake(rel, hash, existing).InsertPath) : rel;
                var target = Path.Combine(dir, copyName);
                if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) // re-index reads the copy itself
                {
                    File.Copy(src, target, true);
                    _store.SetMeta($"orig:{sid}:{rel}", Path.GetFullPath(src));
                }
                byPath[rel] = new VaultNote(rel, parsed.Title, "", Array.Empty<string>(), hash, parsed.ToMarkdown(), Array.Empty<VaultLink>());
                if (intake != null)
                {
                    fresh.Add(rel);
                    if (parsed.Pages != null) intake.Pages[rel] = parsed.Pages;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add((src, ex.Message)); }
        }

        var all = byPath.Values.Where(n => intake == null || fresh.Contains(n.RelPath)).OrderBy(n => n.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
        var result = ImportNotes(sourceName, kind, path, all, null, null, null, ct, removeMissing: false, buildLinks: false, intake: intake);
        if (intake == null) RebuildDocumentLinks(sid, ct); // intake: links are built when notes are accepted
        _store.SetMeta("src_status:" + sid, errors.Count == 0 ? "" : string.Join("; ", errors.Select(e => Path.GetFileName(e.Item1) + ": " + e.Item2)));
        BumpRevision();
        return new FilesImportResult(result, errors);
    }

    private bool SameOriginal(long sid, string rel, string src) =>
        string.Equals(_store.GetMeta($"orig:{sid}:{rel}"), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase);

    internal static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>mention and semantic links from every document of the source to notes anywhere in the store.</summary>
    private void RebuildDocumentLinks(long sid, CancellationToken ct)
    {
        var docs = _store.Notes(sid);
        var all = _store.Notes(status: "accepted"); // only accepted notes can be named or linked to
        var links = new HashSet<(long, long, string)>();

        // mention: the document text names another note/document by title (2+ words, or an acronym of 3+ capitals)
        var targets = all.Where(t => IsMentionable(t.Title)).ToList();
        if (targets.Count <= MaxMentionNotes)
            foreach (var d in docs)
            {
                ct.ThrowIfCancellationRequested();
                var body = _store.Note(d.Id)?.Body ?? "";
                foreach (var t in targets)
                {
                    if (t.Id == d.Id) continue;
                    var acronym = IsAcronym(t.Title);
                    if (Regex.IsMatch(body, @"(?<!\w)" + Regex.Escape(t.Title.Trim()) + @"(?!\w)",
                            acronym ? RegexOptions.CultureInvariant : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        links.Add((d.Id, t.Id, LinkKinds.Mention));
                }
            }

        // semantic: nearest accepted notes by mean chunk vector (only when vectors exist); the documents themselves may still be pending
        var means = MeanVectors(_store.Chunks(status: "accepted"));
        var docMeans = MeanVectors(_store.Chunks(sid));
        foreach (var d in docs)
        {
            if (!docMeans.TryGetValue(d.Id, out var v)) continue;
            var best = means.Where(kv => kv.Key != d.Id && kv.Value.Length == v.Length)
                .Select(kv => (Id: kv.Key, S: TensorPrimitives.Dot(v, kv.Value)))
                .Where(x => x.S >= DocSemanticMin).OrderByDescending(x => x.S).Take(DocNeighbours);
            foreach (var x in best) links.Add((d.Id, x.Id, LinkKinds.Semantic));
        }
        _store.ReplaceLinks(sid, links);
    }

    private static bool IsAcronym(string t) => t.Trim() is { Length: >= 3 } s && s.All(c => char.IsUpper(c) || char.IsDigit(c)) && s.Any(char.IsUpper);

    private static bool IsMentionable(string t)
    {
        t = t.Trim();
        return IsAcronym(t) || t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    private static Dictionary<long, float[]> MeanVectors(IEnumerable<ChunkRow> chunks)
    {
        var means = new Dictionary<long, float[]>();
        foreach (var g in chunks.Where(c => c.Embedding != null).GroupBy(c => c.NoteId))
        {
            var dims = g.First().Embedding!.Length;
            var sum = new float[dims];
            foreach (var c in g.Where(c => c.Embedding!.Length == dims)) TensorPrimitives.Add(sum, c.Embedding!, sum);
            var norm = MathF.Sqrt(TensorPrimitives.Dot(sum, sum));
            if (norm > 0) { TensorPrimitives.Divide(sum, norm, sum); means[g.Key] = sum; }
        }
        return means;
    }
}
