using System.Runtime.InteropServices;
using EzForensics.Core.Settings;
using Microsoft.Data.Sqlite;

namespace EzForensics.Core.Knowledge;

/// <summary>A source (imported vault or pack) in the store.</summary>
public sealed record SourceInfo(long Id, string Name, string Kind, string Path, string ImportedAt);

/// <summary>The AI/user labels of a note (schema v4); <c>ParentId</c> points at the distilled parent note.</summary>
public sealed record NoteLabels(string? Summary, string? Category, string? Type, IReadOnlyList<string> Tags, string AiState,
    string? AiModel, string? AiAt, long? ParentId);

/// <summary>A stored chunk with its note, as loaded for search and for writing packs.</summary>
public sealed record ChunkRow(long Id, long NoteId, int Ord, string Heading, string Text, float[]? Embedding);

/// <summary>
/// The one SQLite file of the knowledge engine: sources, notes, chunks (with embeddings), links, meta, and FTS5 indexes over
/// chunks and note titles. Creates and migrates the schema on open. Not thread safe: use one store per thread of work.
/// </summary>
public sealed class KnowledgeStore : IDisposable
{
    public const int SchemaVersion = 4;

    /// <summary><c>&lt;DataRoot&gt;\knowledge\knowledge.db</c>.</summary>
    public static string DefaultPath => System.IO.Path.Combine(AppPaths.DataRoot, "knowledge", "knowledge.db");

    private readonly SqliteConnection _db;

    public string FilePath { get; }

    public KnowledgeStore(string? path = null)
    {
        FilePath = path ?? DefaultPath;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Migrate();
    }

    public void Dispose() => _db.Dispose();

    private void Migrate()
    {
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);");
        var v = int.TryParse(GetMeta("schema_version"), out var n) ? n : 0;
        if (v < 1)
        {
            Exec(@"
CREATE TABLE sources(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, path TEXT NOT NULL, imported_at TEXT NOT NULL);
CREATE TABLE notes(id INTEGER PRIMARY KEY, source_id INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
  path TEXT NOT NULL, title TEXT NOT NULL, topic TEXT NOT NULL, tags TEXT NOT NULL, hash TEXT NOT NULL, body TEXT NOT NULL,
  UNIQUE(source_id, path));
CREATE TABLE chunks(id INTEGER PRIMARY KEY, note_id INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
  ord INTEGER NOT NULL, heading TEXT NOT NULL, text TEXT NOT NULL, embedding BLOB);
CREATE INDEX chunks_note ON chunks(note_id);
CREATE TABLE links(src_note INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
  dst_note INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE, kind TEXT NOT NULL, PRIMARY KEY(src_note, dst_note, kind));
CREATE INDEX links_dst ON links(dst_note);
CREATE VIRTUAL TABLE chunks_fts USING fts5(text, heading, content='chunks', content_rowid='id');
CREATE TRIGGER chunks_ai AFTER INSERT ON chunks BEGIN
  INSERT INTO chunks_fts(rowid, text, heading) VALUES (new.id, new.text, new.heading); END;
CREATE TRIGGER chunks_ad AFTER DELETE ON chunks BEGIN
  INSERT INTO chunks_fts(chunks_fts, rowid, text, heading) VALUES ('delete', old.id, old.text, old.heading); END;
CREATE VIRTUAL TABLE notes_fts USING fts5(title, content='notes', content_rowid='id');
CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
  INSERT INTO notes_fts(rowid, title) VALUES (new.id, new.title); END;
CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
  INSERT INTO notes_fts(notes_fts, rowid, title) VALUES ('delete', old.id, old.title); END;");
            SetMeta("schema_version", "1");
        }
        if (v < 2)
        {
            InTransaction(() =>
            {
                Exec(@"
ALTER TABLE notes ADD COLUMN status TEXT NOT NULL DEFAULT 'accepted';
ALTER TABLE notes ADD COLUMN added_at TEXT;
ALTER TABLE notes ADD COLUMN added_by TEXT;
ALTER TABLE notes ADD COLUMN reviewed_by TEXT;
ALTER TABLE notes ADD COLUMN reviewed_at TEXT;
ALTER TABLE notes ADD COLUMN flags TEXT NOT NULL DEFAULT '[]';
ALTER TABLE notes ADD COLUMN clean_ratio REAL;
ALTER TABLE notes ADD COLUMN replaces INTEGER;
UPDATE notes SET added_at=(SELECT imported_at FROM sources WHERE sources.id=notes.source_id);
CREATE INDEX notes_status ON notes(status);
CREATE INDEX notes_hash ON notes(hash);
CREATE TABLE concepts(id INTEGER PRIMARY KEY, kind TEXT, key TEXT, label TEXT, UNIQUE(kind,key));
CREATE TABLE note_concepts(note_id INTEGER REFERENCES notes(id) ON DELETE CASCADE,
  concept_id INTEGER REFERENCES concepts(id) ON DELETE CASCADE, count INTEGER, PRIMARY KEY(note_id, concept_id));
CREATE TABLE layout(node_key TEXT PRIMARY KEY, x REAL, y REAL);");
                SetMeta("schema_version", "2");
            });
        }
        if (v < 3)
        {
            // the Brain is 3D: a depth per node. Rows saved by the 2D Brain keep z NULL, which counts as "no position".
            InTransaction(() =>
            {
                Exec("ALTER TABLE layout ADD COLUMN z REAL;");
                SetMeta("schema_version", "3");
            });
        }
        if (v < 4)
        {
            // knowledge feed: AI labels, parent (distilled) notes, and the summary in the title index.
            InTransaction(() =>
            {
                Exec(@"
ALTER TABLE notes ADD COLUMN summary TEXT;
ALTER TABLE notes ADD COLUMN category TEXT;
ALTER TABLE notes ADD COLUMN note_type TEXT;
ALTER TABLE notes ADD COLUMN parent_id INTEGER;
ALTER TABLE notes ADD COLUMN ai_state TEXT NOT NULL DEFAULT 'none';
ALTER TABLE notes ADD COLUMN ai_model TEXT;
ALTER TABLE notes ADD COLUMN ai_at TEXT;
CREATE INDEX notes_parent ON notes(parent_id);
DROP TRIGGER IF EXISTS notes_ai;
DROP TRIGGER IF EXISTS notes_ad;
DROP TABLE IF EXISTS notes_fts;
CREATE VIRTUAL TABLE notes_fts USING fts5(title, summary, content='notes', content_rowid='id');
CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
  INSERT INTO notes_fts(rowid, title, summary) VALUES (new.id, new.title, new.summary); END;
CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
  INSERT INTO notes_fts(notes_fts, rowid, title, summary) VALUES ('delete', old.id, old.title, old.summary); END;
INSERT INTO notes_fts(notes_fts) VALUES('rebuild');");
                SetMeta("schema_version", SchemaVersion.ToString());
            });
        }
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = Cmd(sql, args);
        c.ExecuteNonQuery();
    }

    private SqliteCommand Cmd(string sql, params (string, object?)[] args)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c;
    }

    private T Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var c = Cmd(sql, args);
        var o = c.ExecuteScalar();
        return o is null or DBNull ? default! : (T)Convert.ChangeType(o, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private SqliteTransaction? _tx;

    /// <summary>Runs <paramref name="work"/> in one transaction (rolled back when it throws). Nested calls join the outer transaction.</summary>
    public void InTransaction(Action work)
    {
        if (_tx != null) { work(); return; }
        using var tx = _db.BeginTransaction();
        _tx = tx;
        try
        {
            work();
            tx.Commit();
        }
        finally { _tx = null; }
    }

    // ---- meta

    public string? GetMeta(string key) => Scalar<string?>("SELECT value FROM meta WHERE key=$k", ("$k", key));

    public void SetMeta(string key, string value) =>
        Exec("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));

    // ---- sources

    /// <summary>
    /// Finds or creates the source <paramref name="name"/> of this <paramref name="kind"/>. An existing source never changes kind:
    /// when the name is taken by another kind (or <paramref name="clashes"/> says the existing source is a different one), the next free
    /// name "<c>name (2)</c>", "<c>name (3)</c>"... is used. A blank <paramref name="path"/> keeps the stored path.
    /// </summary>
    public long UpsertSource(string name, string kind, string path, Func<SourceInfo, bool>? clashes = null)
    {
        var now = DateTime.UtcNow.ToString("o");
        for (var i = 1; ; i++)
        {
            var n = i == 1 ? name : $"{name} ({i})";
            var cur = SourceByName(n);
            if (cur == null)
            {
                Exec("INSERT INTO sources(name,kind,path,imported_at) VALUES($n,$k,$p,$t)", ("$n", n), ("$k", kind), ("$p", path), ("$t", now));
                return Scalar<long>("SELECT id FROM sources WHERE name=$n", ("$n", n));
            }
            if (cur.Kind == kind && !(clashes?.Invoke(cur) ?? false))
            {
                Exec("UPDATE sources SET path=CASE WHEN $p='' THEN path ELSE $p END, imported_at=$t WHERE id=$i", ("$p", path), ("$t", now), ("$i", cur.Id));
                return cur.Id;
            }
        }
    }

    public SourceInfo? SourceByName(string name)
    {
        using var c = Cmd("SELECT id,name,kind,path,imported_at FROM sources WHERE name=$n", ("$n", name));
        using var r = c.ExecuteReader();
        return r.Read() ? new SourceInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)) : null;
    }

    public long? FindSource(string name) =>
        Scalar<long?>("SELECT id FROM sources WHERE name=$n", ("$n", name)) is { } id and > 0 ? id : null;

    public List<SourceInfo> Sources()
    {
        var list = new List<SourceInfo>();
        using var c = Cmd("SELECT id,name,kind,path,imported_at FROM sources ORDER BY name");
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new SourceInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    /// <summary>Deletes the source with its notes, chunks, links and index entries.</summary>
    public void DeleteSource(long id) => InTransaction(() =>
    {
        Exec("DELETE FROM chunks WHERE note_id IN (SELECT id FROM notes WHERE source_id=$s)", ("$s", id));
        Exec("DELETE FROM links WHERE src_note IN (SELECT id FROM notes WHERE source_id=$s) OR dst_note IN (SELECT id FROM notes WHERE source_id=$s)", ("$s", id));
        Exec("DELETE FROM notes WHERE source_id=$s", ("$s", id));
        Exec("DELETE FROM sources WHERE id=$s", ("$s", id));
    });

    // ---- notes

    /// <summary>Path to (id, hash) of the notes of a source.</summary>
    public Dictionary<string, (long Id, string Hash)> NotesOf(long sourceId)
    {
        var d = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        using var c = Cmd("SELECT id,path,hash FROM notes WHERE source_id=$s", ("$s", sourceId));
        using var r = c.ExecuteReader();
        while (r.Read()) d[r.GetString(1)] = (r.GetInt64(0), r.GetString(2));
        return d;
    }

    /// <summary>Ids of notes of the source that have a chunk without a vector.</summary>
    public HashSet<long> NotesWithoutEmbeddings(long sourceId)
    {
        var s = new HashSet<long>();
        using var c = Cmd("SELECT DISTINCT c.note_id FROM chunks c JOIN notes n ON n.id=c.note_id WHERE n.source_id=$s AND c.embedding IS NULL", ("$s", sourceId));
        using var r = c.ExecuteReader();
        while (r.Read()) s.Add(r.GetInt64(0));
        return s;
    }

    /// <summary>Lower-case title to note id for a source (first note wins on duplicates).</summary>
    public Dictionary<string, long> TitleIndex(long sourceId)
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var c = Cmd("SELECT id,title FROM notes WHERE source_id=$s ORDER BY id", ("$s", sourceId));
        using var r = c.ExecuteReader();
        while (r.Read()) d.TryAdd(r.GetString(1), r.GetInt64(0));
        return d;
    }

    /// <summary>Inserts a note and its chunks; <paramref name="embeddings"/> (one per chunk) may be null. Notes are accepted unless <paramref name="status"/> says otherwise.</summary>
    public long InsertNote(long sourceId, VaultNote note, IReadOnlyList<ChunkPiece> chunks, IReadOnlyList<float[]>? embeddings, string status = "accepted")
    {
        long id = 0;
        InTransaction(() =>
        {
            Exec("INSERT INTO notes(source_id,path,title,topic,tags,hash,body,status) VALUES($s,$p,$t,$o,$g,$h,$b,$st)",
                ("$st", status), ("$s", sourceId), ("$p", note.RelPath), ("$t", note.Title), ("$o", note.Topic),
                ("$g", string.Join(",", note.Tags)), ("$h", note.Hash), ("$b", note.Body));
            id = Scalar<long>("SELECT last_insert_rowid()");
            for (var i = 0; i < chunks.Count; i++)
                Exec("INSERT INTO chunks(note_id,ord,heading,text,embedding) VALUES($n,$o,$h,$t,$e)",
                    ("$n", id), ("$o", chunks[i].Ord), ("$h", chunks[i].Heading), ("$t", chunks[i].Text),
                    ("$e", embeddings != null && i < embeddings.Count ? ToBlob(embeddings[i]) : null));
        });
        return id;
    }

    public void DeleteNote(long id) => InTransaction(() =>
    {
        Exec("DELETE FROM chunks WHERE note_id=$n", ("$n", id));
        Exec("DELETE FROM links WHERE src_note=$n OR dst_note=$n", ("$n", id));
        Exec("DELETE FROM notes WHERE id=$n", ("$n", id));
        Exec("DELETE FROM meta WHERE key=$k", ("$k", EditedKey(id))); // ids can be reused
    });

    /// <summary>Replaces all links whose source note belongs to the source.</summary>
    public void ReplaceLinks(long sourceId, IEnumerable<(long Src, long Dst, string Kind)> links) => InTransaction(() =>
    {
        Exec("DELETE FROM links WHERE src_note IN (SELECT id FROM notes WHERE source_id=$s)", ("$s", sourceId));
        foreach (var (s, d, k) in links)
            if (s != d) Exec("INSERT OR IGNORE INTO links(src_note,dst_note,kind) VALUES($s,$d,$k)", ("$s", s), ("$d", d), ("$k", k));
    });

    /// <summary>Adds links, keeping every existing one.</summary>
    public void AddLinks(IEnumerable<(long Src, long Dst, string Kind)> links) => InTransaction(() =>
    {
        foreach (var (s, d, k) in links)
            if (s != d) Exec("INSERT OR IGNORE INTO links(src_note,dst_note,kind) VALUES($s,$d,$k)", ("$s", s), ("$d", d), ("$k", k));
    });

    public List<(long Src, long Dst, string Kind)> Links()
    {
        var list = new List<(long, long, string)>();
        using var c = Cmd("SELECT src_note,dst_note,kind FROM links");
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2)));
        return list;
    }

    public int NoteCount(long? sourceId = null) =>
        sourceId is null ? Scalar<int>("SELECT COUNT(*) FROM notes") : Scalar<int>("SELECT COUNT(*) FROM notes WHERE source_id=$s", ("$s", sourceId));

    public int ChunkCount() => Scalar<int>("SELECT COUNT(*) FROM chunks");

    public int ChunkCount(long sourceId) => Scalar<int>("SELECT COUNT(*) FROM chunks WHERE note_id IN (SELECT id FROM notes WHERE source_id=$s)", ("$s", sourceId));

    public int LinkCount() => Scalar<int>("SELECT COUNT(*) FROM links");

    /// <summary>All notes (id, title, path, source id), in id order.</summary>
    public List<(long Id, string Title, string Path, long SourceId)> Notes(long? sourceId = null, string? status = null)
    {
        var list = new List<(long, string, string, long)>();
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (sourceId != null) { where.Add("source_id=$s"); args.Add(("$s", sourceId)); }
        if (status != null) { where.Add("status=$st"); args.Add(("$st", status)); }
        using var c = Cmd("SELECT id,title,path,source_id FROM notes" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id",
            args.ToArray());
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
        return list;
    }

    public (string Title, string Path, string Body, string Tags, string Topic)? Note(long id)
    {
        using var c = Cmd("SELECT title,path,body,tags,topic FROM notes WHERE id=$i", ("$i", id));
        using var r = c.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)) : null;
    }

    /// <summary>Chunks (optionally of one source) in note then ord order.</summary>
    public List<ChunkRow> Chunks(long? sourceId = null, string? status = null)
    {
        var list = new List<ChunkRow>();
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (sourceId != null) { where.Add("n.source_id=$s"); args.Add(("$s", sourceId)); }
        if (status != null) { where.Add("n.status=$st"); args.Add(("$st", status)); }
        using var c = Cmd("SELECT c.id,c.note_id,c.ord,c.heading,c.text,c.embedding FROM chunks c JOIN notes n ON n.id=c.note_id" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY c.note_id,c.ord",
            args.ToArray());
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new ChunkRow(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : FromBlob((byte[])r[5])));
        return list;
    }

    // ---- note status, intake info, concepts, layout (schema v2)

    /// <summary>Review/intake state of a note (the body and chunks are read with <see cref="Note"/> and <see cref="Chunks"/>).</summary>
    public sealed record NoteMeta(long Id, long SourceId, string Title, string Path, string Hash, string Status, string? AddedAt,
        string? AddedBy, IReadOnlyList<string> Flags, double? CleanRatio, long? Replaces,
        string? Category = null, string? NoteType = null, string AiState = "none", long? ParentId = null);

    private const string MetaSelect = "SELECT id,source_id,title,path,hash,status,added_at,added_by,flags,clean_ratio,replaces,category,note_type,ai_state,parent_id FROM notes";

    private static NoteMeta ReadMeta(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), ParseFlags(r.GetString(8)),
        r.IsDBNull(9) ? null : r.GetDouble(9), r.IsDBNull(10) ? null : r.GetInt64(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetString(13), r.IsDBNull(14) ? null : r.GetInt64(14));

    private static IReadOnlyList<string> ParseFlags(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (System.Text.Json.JsonException) { return new List<string>(); }
    }

    public NoteMeta? Meta(long noteId)
    {
        using var c = Cmd(MetaSelect + " WHERE id=$i", ("$i", noteId));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadMeta(r) : null;
    }

    public List<NoteMeta> NotesByStatus(string status, long? sourceId = null)
    {
        var list = new List<NoteMeta>();
        using var c = Cmd(MetaSelect + " WHERE status=$st" + (sourceId != null ? " AND source_id=$s" : "") + " ORDER BY id",
            sourceId != null ? new (string, object?)[] { ("$st", status), ("$s", sourceId) } : new (string, object?)[] { ("$st", status) });
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(ReadMeta(r));
        return list;
    }

    /// <summary>First note (lowest id) with this content hash, whatever its status.</summary>
    public NoteMeta? FindByHash(string hash)
    {
        using var c = Cmd(MetaSelect + " WHERE hash=$h ORDER BY id LIMIT 1", ("$h", hash));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadMeta(r) : null;
    }

    /// <summary>All notes (any status) with this content hash, in id order.</summary>
    public List<NoteMeta> NotesWithHash(string hash)
    {
        var list = new List<NoteMeta>();
        using var c = Cmd(MetaSelect + " WHERE hash=$h ORDER BY id", ("$h", hash));
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(ReadMeta(r));
        return list;
    }

    /// <summary>Id of an accepted note whose body is exactly <paramref name="body"/> (null when none).</summary>
    public long? FindAcceptedByBody(string body) =>
        Scalar<long?>("SELECT id FROM notes WHERE status='accepted' AND body=$b ORDER BY id LIMIT 1", ("$b", body)) is { } id and > 0 ? id : null;

    /// <summary>Pending notes that are waiting to replace the accepted note <paramref name="noteId"/>.</summary>
    public List<NoteMeta> PendingReplacing(long noteId)
    {
        var list = new List<NoteMeta>();
        using var c = Cmd(MetaSelect + " WHERE status='pending' AND replaces=$r ORDER BY id", ("$r", noteId));
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(ReadMeta(r));
        return list;
    }

    public void SetReplaces(long noteId, long? replaces) => Exec("UPDATE notes SET replaces=$r WHERE id=$i", ("$r", replaces), ("$i", noteId));

    /// <summary>Renames a note and replaces its tags (the title index follows), and records that the user edited them (see <see cref="IsEdited"/>).</summary>
    public void SetTitleTags(long noteId, string title, IReadOnlyList<string> tags) => InTransaction(() =>
    {
        if (!FtsDelete(noteId)) return;
        Exec("UPDATE notes SET title=$t, tags=$g WHERE id=$i", ("$t", title), ("$g", string.Join(",", tags)), ("$i", noteId));
        FtsInsert(noteId);
        SetMeta(EditedKey(noteId), "1");
    });

    /// <summary>Removes the note's current (title, summary) pair from the title index; false when the note does not exist.</summary>
    private bool FtsDelete(long noteId)
    {
        string title;
        string? summary;
        using (var c = Cmd("SELECT title, summary FROM notes WHERE id=$i", ("$i", noteId)))
        using (var r = c.ExecuteReader())
        {
            if (!r.Read()) return false;
            title = r.GetString(0);
            summary = r.IsDBNull(1) ? null : r.GetString(1);
        }
        Exec("INSERT INTO notes_fts(notes_fts, rowid, title, summary) VALUES ('delete', $i, $t, $s)", ("$i", noteId), ("$t", title), ("$s", summary));
        return true;
    }

    /// <summary>Adds the note's current (title, summary) pair to the title index.</summary>
    private void FtsInsert(long noteId) =>
        Exec("INSERT INTO notes_fts(rowid, title, summary) SELECT id, title, summary FROM notes WHERE id=$i", ("$i", noteId));

    // ---- AI labels and parent notes (schema v4)

    /// <summary>Labels of a note (an unknown note reads as unlabelled).</summary>
    public NoteLabels Labels(long noteId)
    {
        using var c = Cmd("SELECT summary,category,note_type,tags,ai_state,ai_model,ai_at,parent_id FROM notes WHERE id=$i", ("$i", noteId));
        using var r = c.ExecuteReader();
        if (!r.Read()) return new NoteLabels(null, null, null, Array.Empty<string>(), KnowledgeLabels.None, null, null, null);
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var tags = (S(3) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new NoteLabels(S(0), S(1), S(2), tags, r.GetString(4), S(5), S(6), r.IsDBNull(7) ? null : r.GetInt64(7));
    }

    /// <summary>Stores AI labels (normalized to the vocabulary) and marks the note labelled. This is not a user edit, so <see cref="IsEdited"/> stays as it was.</summary>
    public void SetLabels(long noteId, string? summary, string category, string type, IReadOnlyList<string> tags, string aiModel, string aiAt) =>
        InTransaction(() =>
        {
            if (!FtsDelete(noteId)) return;
            Exec("UPDATE notes SET summary=$s, category=$c, note_type=$y, tags=$g, ai_state='labelled', ai_model=$m, ai_at=$a WHERE id=$i",
                ("$s", summary), ("$c", KnowledgeLabels.Category(category)), ("$y", KnowledgeLabels.Type(type)),
                ("$g", string.Join(",", KnowledgeLabels.Tags(tags))), ("$m", aiModel), ("$a", aiAt), ("$i", noteId));
            FtsInsert(noteId);
        });

    public void SetAiFailed(IEnumerable<long> ids) => InTransaction(() =>
    {
        foreach (var id in ids) Exec("UPDATE notes SET ai_state='failed' WHERE id=$i", ("$i", id));
    });

    /// <summary>Sets the category and/or type of several notes; a null argument leaves that label unchanged.</summary>
    public void SetCategoryType(IEnumerable<long> ids, string? category, string? type) => InTransaction(() =>
    {
        foreach (var id in ids)
        {
            if (category != null) Exec("UPDATE notes SET category=$c WHERE id=$i", ("$c", KnowledgeLabels.Category(category)), ("$i", id));
            if (type != null) Exec("UPDATE notes SET note_type=$y WHERE id=$i", ("$y", KnowledgeLabels.Type(type)), ("$i", id));
        }
    });

    public void SetParent(long noteId, long parentId) => Exec("UPDATE notes SET parent_id=$p WHERE id=$i", ("$p", parentId), ("$i", noteId));

    /// <summary>Ids of the notes whose parent is <paramref name="parentId"/>, in id order.</summary>
    public List<long> Children(long parentId)
    {
        var list = new List<long>();
        using var c = Cmd("SELECT id FROM notes WHERE parent_id=$p ORDER BY id", ("$p", parentId));
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private static string EditedKey(long noteId) => "edited:" + noteId;

    /// <summary>True when the title and tags of the note were edited by the user (a re-index keeps them).</summary>
    public bool IsEdited(long noteId) => GetMeta(EditedKey(noteId)) == "1";

    /// <summary>
    /// Replaces the text of a note in place (title, hash, body, clean ratio and chunks), keeping its id, status, tags, intake info,
    /// links and layout. Used by Re-index.
    /// </summary>
    public void ReplaceNoteContent(long noteId, string title, string hash, string body, double cleanRatio,
        IReadOnlyList<ChunkPiece> chunks, IReadOnlyList<float[]>? embeddings) => InTransaction(() =>
    {
        if (!FtsDelete(noteId)) throw new InvalidOperationException("Unknown note: " + noteId);
        Exec("UPDATE notes SET title=$t, hash=$h, body=$b, clean_ratio=$c WHERE id=$i",
            ("$t", title), ("$h", hash), ("$b", body), ("$c", cleanRatio), ("$i", noteId));
        FtsInsert(noteId);
        Exec("DELETE FROM chunks WHERE note_id=$n", ("$n", noteId));
        for (var i = 0; i < chunks.Count; i++)
            Exec("INSERT INTO chunks(note_id,ord,heading,text,embedding) VALUES($n,$o,$h,$t,$e)",
                ("$n", noteId), ("$o", chunks[i].Ord), ("$h", chunks[i].Heading), ("$t", chunks[i].Text),
                ("$e", embeddings != null && i < embeddings.Count ? ToBlob(embeddings[i]) : null));
    });

    public void SetPath(long noteId, string path) => Exec("UPDATE notes SET path=$p WHERE id=$i", ("$p", path), ("$i", noteId));

    public string? NoteBody(long noteId) => Scalar<string?>("SELECT body FROM notes WHERE id=$i", ("$i", noteId));

    public void SetStatus(long noteId, string status, string who, string when) => SetStatusMany(new[] { noteId }, status, who, when);

    /// <summary>Sets the status (and reviewer) of several notes in one transaction.</summary>
    public void SetStatusMany(IEnumerable<long> ids, string status, string who, string when) => InTransaction(() =>
    {
        foreach (var id in ids)
            Exec("UPDATE notes SET status=$st, reviewed_by=$w, reviewed_at=$t WHERE id=$i", ("$st", status), ("$w", who), ("$t", when), ("$i", id));
    });

    public void SetIntakeInfo(long noteId, string addedAt, string addedBy, IReadOnlyList<string> flags, double cleanRatio, long? replaces) =>
        Exec("UPDATE notes SET added_at=$a, added_by=$b, flags=$f, clean_ratio=$c, replaces=$r WHERE id=$i",
            ("$a", addedAt), ("$b", addedBy), ("$f", System.Text.Json.JsonSerializer.Serialize(flags)), ("$c", cleanRatio), ("$r", replaces), ("$i", noteId));

    public long UpsertConcept(string kind, string key, string label)
    {
        Exec("INSERT INTO concepts(kind,key,label) VALUES($k,$y,$l) ON CONFLICT(kind,key) DO UPDATE SET label=$l",
            ("$k", kind), ("$y", key), ("$l", label));
        return Scalar<long>("SELECT id FROM concepts WHERE kind=$k AND key=$y", ("$k", kind), ("$y", key));
    }

    public void ReplaceNoteConcepts(long noteId, IReadOnlyDictionary<long, int> conceptCounts) => InTransaction(() =>
    {
        Exec("DELETE FROM note_concepts WHERE note_id=$n", ("$n", noteId));
        foreach (var (cid, count) in conceptCounts)
            Exec("INSERT INTO note_concepts(note_id,concept_id,count) VALUES($n,$c,$k)", ("$n", noteId), ("$c", cid), ("$k", count));
    });

    public List<(long NoteId, long ConceptId, int Count)> NoteConcepts()
    {
        var list = new List<(long, long, int)>();
        using var c = Cmd("SELECT note_id,concept_id,count FROM note_concepts");
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt32(2)));
        return list;
    }

    /// <summary>Labels of the concepts a note mentions, most frequent first (at most <paramref name="limit"/>).</summary>
    public List<string> ConceptLabels(long noteId, int limit = 10)
    {
        var list = new List<string>();
        using var c = Cmd("SELECT c.label FROM note_concepts nc JOIN concepts c ON c.id=nc.concept_id WHERE nc.note_id=$n ORDER BY nc.count DESC, c.id LIMIT $l",
            ("$n", noteId), ("$l", limit));
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<(long Id, string Kind, string Key, string Label)> Concepts()
    {
        var list = new List<(long, string, string, string)>();
        using var c = Cmd("SELECT id,kind,key,label FROM concepts ORDER BY id");
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    /// <summary>Saved Brain positions. Z is null for a row saved by the old 2D Brain (the graph treats that as unset).</summary>
    public Dictionary<string, (double X, double Y, double? Z)> Layout()
    {
        var d = new Dictionary<string, (double, double, double?)>(StringComparer.Ordinal);
        using var c = Cmd("SELECT node_key,x,y,z FROM layout");
        using var r = c.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = (r.GetDouble(1), r.GetDouble(2), r.IsDBNull(3) ? null : r.GetDouble(3));
        return d;
    }

    public void SaveLayout(IReadOnlyDictionary<string, (double X, double Y, double? Z)> pos) => InTransaction(() =>
    {
        foreach (var (k, p) in pos)
            Exec("INSERT INTO layout(node_key,x,y,z) VALUES($k,$x,$y,$z) ON CONFLICT(node_key) DO UPDATE SET x=$x, y=$y, z=$z",
                ("$k", k), ("$x", p.X), ("$y", p.Y), ("$z", p.Z));
    });

    /// <summary>2D positions (z stays NULL).</summary>
    public void SaveLayout(IReadOnlyDictionary<string, (double X, double Y)> pos) =>
        SaveLayout(pos.ToDictionary(e => e.Key, e => (e.Value.X, e.Value.Y, (double?)null), StringComparer.Ordinal));

    public void ClearLayout() => Exec("DELETE FROM layout");

    // ---- search primitives

    /// <summary>Chunk ids ranked by bm25 for a sanitized FTS5 query.</summary>
    public List<long> SearchChunks(string ftsQuery, int limit) => Ids(
        "SELECT chunks_fts.rowid FROM chunks_fts JOIN chunks c ON c.id=chunks_fts.rowid JOIN notes n ON n.id=c.note_id " +
        "WHERE chunks_fts MATCH $q AND n.status='accepted' ORDER BY bm25(chunks_fts) LIMIT $l", ftsQuery, limit);

    /// <summary>Accepted note ids ranked by bm25 on the title.</summary>
    public List<long> SearchTitles(string ftsQuery, int limit) => Ids(
        "SELECT notes_fts.rowid FROM notes_fts JOIN notes n ON n.id=notes_fts.rowid " +
        "WHERE notes_fts MATCH $q AND n.status='accepted' ORDER BY bm25(notes_fts) LIMIT $l", ftsQuery, limit);

    private List<long> Ids(string sql, string q, int limit)
    {
        var list = new List<long>();
        using var c = Cmd(sql, ("$q", q), ("$l", limit));
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    // ---- embeddings

    public static byte[] ToBlob(float[] v)
    {
        var b = new byte[v.Length * 4];
        MemoryMarshal.AsBytes(v.AsSpan()).CopyTo(b);
        return b;
    }

    public static float[] FromBlob(byte[] b)
    {
        var v = new float[b.Length / 4];
        MemoryMarshal.Cast<byte, float>(b.AsSpan(0, v.Length * 4)).CopyTo(v);
        return v;
    }
}
