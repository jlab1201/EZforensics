using EzForensics.Core.Analysis;
using EzForensics.Core.Knowledge.Parsers;

namespace EzForensics.Core.Knowledge;

/// <summary>What happened to the files handed to <see cref="KnowledgeIntake.AddAsync"/>.</summary>
public sealed record IntakeResult(int Added, int AlreadyWaiting, int RejectedBefore, int Unchanged, IReadOnlyList<(string File, string Error)> Errors)
{
    /// <summary>Ids of the pending notes this add created (the app labels them).</summary>
    public IReadOnlyList<long> NewIds { get; init; } = Array.Empty<long>();
}

public enum IntakeFileState { New, Changed, AlreadyIn, RejectedBefore, Unsupported }

/// <summary>One file seen by Check for new, with how it compares to the store. <see cref="ExistingNoteId"/> is the note at that path (Changed) or the one with the same content.</summary>
public sealed record IntakeFile(string FullPath, string RelPath, string Title, long Size, IntakeFileState State, long? ExistingNoteId);

/// <summary>
/// The review queue of the knowledge base. New documents come in as <c>pending</c> (not searchable), the examiner accepts or rejects them.
/// Every status change records who and when and bumps the store revision, so readers (<see cref="KnowledgeService"/>) reload.
/// Not thread safe: use it on the thread that owns the store, like the store itself.
/// </summary>
public sealed class KnowledgeIntake
{
    public const string Pending = "pending", Accepted = "accepted", Rejected = "rejected";
    private const string BackfillKey = "concepts_backfilled";

    private readonly KnowledgeStore _store;
    private readonly KnowledgeImporter _importer;

    /// <summary>Raised after any status change or import (so the app can refresh its lists).</summary>
    public event Action? Changed;

    public KnowledgeIntake(KnowledgeStore store, KnowledgeImporter importer, IEmbedder? embedder)
    {
        _store = store;
        _importer = importer;
        _ = embedder; // the importer embeds; kept so callers pass the one they use for search
    }

    // ---- adding

    /// <summary>Source of loose files added with no source name.</summary>
    public const string DocumentsSource = "Documents";

    /// <summary>
    /// Adds files (PDF, Markdown, TXT, HTML), folders and .ezkb packs to the pending queue. With a blank <paramref name="sourceName"/>
    /// each folder gets its own source named after the folder, a pack its manifest name, and loose files go to "Documents".
    /// A folder with Markdown notes is a vault; its other documents (or all of them, when it has no Markdown) are added as files to the
    /// same source. Unsupported files are reported in the errors.
    /// </summary>
    public Task<IntakeResult> AddAsync(IReadOnlyList<string> paths, string sourceName,
        IProgress<(int done, int total, string item)>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        var o = new IntakeOptions();
        var errors = new List<(string, string)>();
        var loose = new List<string>();
        var named = !string.IsNullOrWhiteSpace(sourceName);
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(p)) errors.AddRange(await AddFolderAsync(p, named ? sourceName : Path.GetFileName(Path.TrimEndingDirectorySeparator(p)), o, progress, ct));
                else if (p.EndsWith(".ezkb", StringComparison.OrdinalIgnoreCase))
                    await EzkbPack.ImportAsync(p, _importer, named ? sourceName : null, progress, ct, o);
                else loose.Add(p);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add((p, ex.Message)); }
        }
        if (loose.Count > 0)
        {
            var r = await _importer.ImportFilesAsync(loose, named ? sourceName : DocumentsSource, progress, ct, o);
            errors.AddRange(r.Errors);
        }
        Touch();
        return new IntakeResult(o.Added, o.AlreadyWaiting, o.RejectedBefore, o.Unchanged, errors) { NewIds = o.NewIds.ToList() };
    }, ct);

    private async Task<List<(string, string)>> AddFolderAsync(string folder, string name, IntakeOptions o,
        IProgress<(int done, int total, string item)>? progress, CancellationToken ct)
    {
        var errors = new List<(string, string)>();
        var all = new List<string>();
        CollectFiles(folder, all);
        bool IsMarkdown(string f) => Path.GetExtension(f).Equals(".md", StringComparison.OrdinalIgnoreCase);
        var vault = all.Any(IsMarkdown);
        var docs = all.Where(f => !(vault && IsMarkdown(f)) && DocumentParsers.For(f) != null).ToList();
        var unsupported = all.Where(f => DocumentParsers.For(f) == null).ToList();

        if (vault) await _importer.ImportFolderAsync(folder, name, progress, ct, o);
        if (docs.Count > 0)
        {
            var r = await _importer.ImportFolderFilesAsync(docs, name, vault ? "vault" : KnowledgeImporter.FilesKind, folder, progress, ct, o);
            errors.AddRange(r.Errors);
        }
        if (unsupported.Count > 0)
        {
            var kinds = unsupported.Select(f => Path.GetExtension(f).ToLowerInvariant()).Select(e => e.Length == 0 ? "(no extension)" : e).Distinct().OrderBy(e => e);
            errors.Add((folder, $"Skipped {unsupported.Count} unsupported file(s): {string.Join(", ", kinds)}"));
        }
        return errors;
    }

    // ---- check for new, selective add

    private static bool IsPack(string f) => f.EndsWith(".ezkb", StringComparison.OrdinalIgnoreCase);

    private static bool IsMarkdown(string f) => Path.GetExtension(f).Equals(".md", StringComparison.OrdinalIgnoreCase);

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>
    /// Saves a folder as a source right away (kind: vault when it holds Markdown notes, else documents), with no notes yet.
    /// Check for new can then list it, and it is still there after a restart. A folder that is already a source returns that source.
    /// </summary>
    public SourceInfo SaveSource(string folder, string? name = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var existing = SavedSources().FirstOrDefault(s => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.Path)), full, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var all = new List<string>();
        CollectFiles(full, all);
        var kind = all.Any(IsMarkdown) ? "vault" : KnowledgeImporter.FilesKind;
        var id = _importer.SaveSource(string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name!, kind, full);
        Touch();
        return _store.Sources().First(s => s.Id == id);
    }

    /// <summary>Saved sources whose folder still exists (the ones Check for new can look at).</summary>
    public IReadOnlyList<SourceInfo> SavedSources() =>
        _store.Sources().Where(s => s.Path.Length > 0 && Directory.Exists(s.Path)).ToList();

    /// <summary>Compares every file of a saved source's folder with the store: new, changed, already in, rejected before or unsupported.</summary>
    public Task<IReadOnlyList<IntakeFile>> CheckForNewAsync(long sourceId, CancellationToken ct, IProgress<(int done, int total)>? progress = null) => Task.Run<IReadOnlyList<IntakeFile>>(() =>
    {
        var src = _store.Sources().FirstOrDefault(s => s.Id == sourceId) ?? throw new InvalidOperationException("Unknown source.");
        if (src.Path.Length == 0 || !Directory.Exists(src.Path))
            throw new DirectoryNotFoundException($"The folder of {src.Name} is not there any more: {src.Path}");
        return CheckFolder(src.Path, _store.NotesOf(sourceId), ct, progress);
    }, ct);

    /// <summary>Same check for dropped or picked files and folders that are not a saved source yet.</summary>
    public Task<IReadOnlyList<IntakeFile>> CheckFilesAsync(IReadOnlyList<string> paths, CancellationToken ct) => Task.Run<IReadOnlyList<IntakeFile>>(() =>
    {
        var result = new List<IntakeFile>();
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(p)) result.AddRange(CheckFolder(p, null, ct));
            else if (File.Exists(p)) result.Add(CheckOne(p, Path.GetFileName(p), false, null));
        }
        return result;
    }, ct);

    private List<IntakeFile> CheckFolder(string folder, Dictionary<string, (long Id, string Hash)>? existing, CancellationToken ct, IProgress<(int done, int total)>? progress = null)
    {
        var all = new List<string>();
        CollectFiles(folder, all);
        var vault = all.Any(IsMarkdown);
        var list = new List<IntakeFile>();
        foreach (var f in all)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(CheckOne(f, Rel(folder, f), vault && IsMarkdown(f), existing));
            progress?.Report((list.Count, all.Count));
        }
        return list;
    }

    private IntakeFile CheckOne(string full, string rel, bool vaultNote, Dictionary<string, (long Id, string Hash)>? existing)
    {
        long size = 0;
        try { size = new FileInfo(full).Length; } catch (IOException) { }
        var title = Path.GetFileNameWithoutExtension(full);
        if (IsPack(full)) return new IntakeFile(full, rel, title, size, IntakeFileState.New, null);
        if (!vaultNote && DocumentParsers.For(full) == null) return new IntakeFile(full, rel, title, size, IntakeFileState.Unsupported, null);
        string hash;
        try
        {
            if (vaultNote)
            {
                var n = MarkdownVaultReader.Parse(rel, File.ReadAllText(full));
                hash = n.Hash;
                title = n.Title;
            }
            else hash = KnowledgeImporter.HashFile(full);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new IntakeFile(full, rel, title, size, IntakeFileState.Unsupported, null); }

        var hits = _store.NotesWithHash(hash);
        if (hits.Count > 0)
        {
            var known = hits.Any(h => h.Status != Rejected);
            return new IntakeFile(full, rel, title, size, known ? IntakeFileState.AlreadyIn : IntakeFileState.RejectedBefore, hits[0].Id);
        }
        // the importer stores documents under their file name, vault notes under the folder-relative path
        var key = vaultNote ? rel : Path.GetFileName(full);
        if (existing != null && existing.TryGetValue(key, out var e))
            return new IntakeFile(full, rel, title, size, IntakeFileState.Changed, e.Id);
        return new IntakeFile(full, rel, title, size, IntakeFileState.New, null);
    }

    /// <summary>
    /// Adds only these files, as pending, with the normal intake (cleaning, flags, concepts, duplicates, replacements). With
    /// <paramref name="sourceFolder"/> the files belong to that folder's source (created or updated); otherwise they go to
    /// <paramref name="sourceName"/> or "Documents". Nothing of the source is ever removed. Returns the ids of the pending notes created.
    /// </summary>
    public Task<(IntakeResult Result, IReadOnlyList<long> NoteIds)> AddSelectedAsync(string? sourceName, string? sourceFolder, IReadOnlyList<string> files,
        IProgress<(int done, int total, string item)>? progress, CancellationToken ct) => Task.Run<(IntakeResult, IReadOnlyList<long>)>(async () =>
    {
        var o = new IntakeOptions();
        var errors = new List<(string, string)>();
        var named = !string.IsNullOrWhiteSpace(sourceName);
        var supported = new List<string>();
        var packs = new List<string>();
        foreach (var f in files)
        {
            if (!File.Exists(f)) errors.Add((f, "File not found"));
            else if (IsPack(f)) packs.Add(f);
            else if (DocumentParsers.For(f) == null && !IsMarkdown(f)) errors.Add((f, "Unsupported file type: " + Path.GetExtension(f)));
            else supported.Add(f);
        }

        if (!string.IsNullOrEmpty(sourceFolder))
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
            var name = named ? sourceName! : Path.GetFileName(folder);
            var all = new List<string>();
            CollectFiles(folder, all);
            var vault = all.Any(IsMarkdown);
            var mds = vault ? supported.Where(IsMarkdown).ToList() : new List<string>();
            var docs = supported.Except(mds).ToList();
            if (mds.Count > 0)
            {
                var notes = new List<VaultNote>();
                foreach (var f in mds)
                {
                    ct.ThrowIfCancellationRequested();
                    try { notes.Add(MarkdownVaultReader.Parse(Rel(folder, f), File.ReadAllText(f))); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add((f, ex.Message)); }
                }
                await _importer.ImportVaultSubsetAsync(folder, name, notes, progress, ct, o);
            }
            if (docs.Count > 0)
                errors.AddRange((await _importer.ImportFolderFilesAsync(docs, name, vault ? "vault" : KnowledgeImporter.FilesKind, folder, progress, ct, o)).Errors);
        }
        else if (supported.Count > 0)
            errors.AddRange((await _importer.ImportFilesAsync(supported, named ? sourceName! : DocumentsSource, progress, ct, o)).Errors);

        foreach (var pack in packs)
        {
            ct.ThrowIfCancellationRequested();
            try { await EzkbPack.ImportAsync(pack, _importer, named ? sourceName : null, progress, ct, o); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add((pack, ex.Message)); }
        }

        Touch();
        return (new IntakeResult(o.Added, o.AlreadyWaiting, o.RejectedBefore, o.Unchanged, errors), o.NewIds.ToList());
    }, ct);

    /// <summary>Files under the folder, hidden folders (such as .obsidian) skipped, like the vault reader.</summary>
    private static void CollectFiles(string dir, List<string> files)
    {
        files.AddRange(Directory.EnumerateFiles(dir));
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (!Path.GetFileName(d).StartsWith('.')) CollectFiles(d, files);
    }

    // ---- review actions

    /// <summary>Makes the notes searchable. A note that replaces an older one deletes it and takes over its path.</summary>
    public void Accept(IReadOnlyList<long> ids) => AcceptCore(ids);

    /// <summary>Accepts every pending note of the source in one transaction; returns how many.</summary>
    public int AcceptAllFromSource(long sourceId)
    {
        var ids = _store.NotesByStatus(Pending, sourceId).Select(n => n.Id).ToList();
        AcceptCore(ids);
        return ids.Count;
    }

    private void AcceptCore(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        var metas = ids.Select(_store.Meta).Where(m => m != null).Select(m => m!).ToList();
        foreach (var m in metas.Where(m => m.Replaces != null))
        {
            // distilled notes of the old version now belong to the new one
            foreach (var kid in _store.Children(m.Replaces!.Value)) _store.SetParent(kid, m.Id);
            _store.DeleteNote(m.Replaces!.Value);
            if (m.Path.IndexOf(KnowledgeImporter.PendingMark, StringComparison.Ordinal) is var at and > 0)
            {
                _store.SetPath(m.Id, m.Path[..at]);
                _importer.SwapPendingCopy(m, m.Path[..at]);
            }
        }
        SetStatus(metas.Select(m => m.Id), Accepted);
        foreach (var sid in metas.Select(m => m.SourceId).Distinct())
            try { _importer.RefreshDocumentLinks(sid, metas.Where(m => m.SourceId == sid).Select(m => m.Id).ToList()); } catch (Exception ex) { KnowledgeLog.Info("Links could not be rebuilt: " + ex.Message); }
        Touch();
    }

    /// <summary>Soft delete: the note (and its stored copy) stays until Empty rejected, so Undo then Accept works in full.</summary>
    public void Reject(IReadOnlyList<long> ids)
    {
        SetStatus(ids, Rejected);
        Touch();
    }

    /// <summary>Puts a rejected note back in the queue.</summary>
    public void Undo(long id)
    {
        if (_store.Meta(id)?.Status != Rejected) return;
        SetStatus(new[] { id }, Pending);
        Touch();
    }

    /// <summary>Deletes every rejected note and its stored copy for good; returns how many.</summary>
    public int EmptyRejected()
    {
        var rejected = _store.NotesByStatus(Rejected);
        foreach (var n in rejected)
        {
            _store.DeleteNote(n.Id);
            _importer.DropCopy(n);
        }
        Touch();
        return rejected.Count;
    }

    /// <summary>Duplicate decision: keeps <paramref name="keepId"/>, rejects <paramref name="otherId"/> whatever its status.</summary>
    public void KeepOneOf(long keepId, long otherId)
    {
        if (keepId == otherId) return;
        SetStatus(new[] { otherId }, Rejected);
        Touch();
    }

    private void SetStatus(IEnumerable<long> ids, string status) =>
        _store.SetStatusMany(ids, status, CurrentIdentity.Name, DateTime.UtcNow.ToString("o"));

    /// <summary>Tells readers that content changed (status changes do not change the note count).</summary>
    private void Touch()
    {
        _store.SetMeta("rev", Guid.NewGuid().ToString("N"));
        Changed?.Invoke();
    }

    // ---- concept backfill

    /// <summary>True while accepted notes (for example migrated ones) have no concepts and the backfill has not run.</summary>
    public bool NeedsConceptBackfill =>
        _store.GetMeta(BackfillKey) != "1" && MissingConcepts().Count > 0;

    private List<long> MissingConcepts()
    {
        var have = _store.NoteConcepts().Select(c => c.NoteId).ToHashSet();
        return _store.Notes(status: Accepted).Select(n => n.Id).Where(id => !have.Contains(id)).ToList();
    }

    /// <summary>Extracts concepts for accepted notes that have none, then marks the backfill done.</summary>
    public Task BackfillConceptsAsync(IProgress<(int, int, string)>? progress, CancellationToken ct) => Task.Run(() =>
    {
        if (_store.GetMeta(BackfillKey) == "1") return;
        var todo = MissingConcepts();
        var chunks = _store.Chunks(status: Accepted).GroupBy(c => c.NoteId).ToDictionary(g => g.Key, g => g.Select(c => c.Text).ToList());
        var done = 0;
        foreach (var id in todo)
        {
            ct.ThrowIfCancellationRequested();
            _importer.SaveConcepts(id, chunks.GetValueOrDefault(id) ?? new List<string>());
            progress?.Report((++done, todo.Count, _store.Meta(id)?.Title ?? ""));
        }
        _store.SetMeta(BackfillKey, "1");
    }, ct);
}
