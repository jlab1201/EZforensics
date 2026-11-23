using EzForensics.Core.Knowledge;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.Core.Tests.Knowledge;

public class KnowledgeStoreV4Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-kv4-" + Guid.NewGuid().ToString("N"));

    public KnowledgeStoreV4Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Db => Path.Combine(_dir, "k.db");

    private static long Add(KnowledgeStore s, long src, string path, string title, string body) =>
        s.InsertNote(src, new VaultNote(path, title, "t", Array.Empty<string>(), "h-" + path, body, Array.Empty<VaultLink>()),
            new[] { new ChunkPiece(0, "", body, body) }, null);

    [Fact]
    public void Migration_from_v3_keeps_accepted_notes_searchable_and_sets_ai_state_none()
    {
        using (var c = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
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
  INSERT INTO notes_fts(notes_fts, rowid, title) VALUES ('delete', old.id, old.title); END;
ALTER TABLE notes ADD COLUMN status TEXT NOT NULL DEFAULT 'accepted';
ALTER TABLE notes ADD COLUMN added_at TEXT;
ALTER TABLE notes ADD COLUMN added_by TEXT;
ALTER TABLE notes ADD COLUMN reviewed_by TEXT;
ALTER TABLE notes ADD COLUMN reviewed_at TEXT;
ALTER TABLE notes ADD COLUMN flags TEXT NOT NULL DEFAULT '[]';
ALTER TABLE notes ADD COLUMN clean_ratio REAL;
ALTER TABLE notes ADD COLUMN replaces INTEGER;
CREATE INDEX notes_status ON notes(status);
CREATE INDEX notes_hash ON notes(hash);
CREATE TABLE concepts(id INTEGER PRIMARY KEY, kind TEXT, key TEXT, label TEXT, UNIQUE(kind,key));
CREATE TABLE note_concepts(note_id INTEGER REFERENCES notes(id) ON DELETE CASCADE,
  concept_id INTEGER REFERENCES concepts(id) ON DELETE CASCADE, count INTEGER, PRIMARY KEY(note_id, concept_id));
CREATE TABLE layout(node_key TEXT PRIMARY KEY, x REAL, y REAL, z REAL);
INSERT INTO meta VALUES('schema_version','3');
INSERT INTO sources VALUES(1,'old','vault','p','2026-01-02T03:04:05.0000000Z');
INSERT INTO notes(id,source_id,path,title,topic,tags,hash,body) VALUES(1,1,'a.md','Zebra stripes','t','','h1','zebra body');
INSERT INTO notes_fts(rowid,title) VALUES(1,'Zebra stripes');
INSERT INTO chunks(note_id,ord,heading,text) VALUES(1,0,'','zebra body');";
            cmd.ExecuteNonQuery();
        }

        using var s = new KnowledgeStore(Db);

        s.GetMeta("schema_version").Should().Be("4");
        s.Labels(1).AiState.Should().Be("none");
        new HybridRetriever(s, null).Search("zebra").Hits.Select(h => h.NoteId).Should().Contain(1);
        s.SearchTitles("zebra", 10).Should().Contain(1);
    }

    [Fact]
    public void SetLabels_round_trips_and_normalizes()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var id = Add(s, src, "a.md", "A", "body");

        s.SetLabels(id, "a summary", "memory forensics", "Reference",
            new[] { "a", "A", " b ", "c", "d", "e", "f", "g" }, "model-x", "2026-11-14T00:00:00Z");

        var l = s.Labels(id);
        l.Category.Should().Be("General");
        l.Type.Should().Be("Reference");
        l.Summary.Should().Be("a summary");
        l.Tags.Count.Should().BeLessThanOrEqualTo(6);
        l.Tags.Should().Equal("a", "b", "c", "d", "e", "f");
        l.AiState.Should().Be("labelled");
        l.AiModel.Should().Be("model-x");
        l.AiAt.Should().Be("2026-11-14T00:00:00Z");
        s.IsEdited(id).Should().BeFalse();
    }

    [Fact]
    public void Summary_is_found_by_title_search()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var id = Add(s, src, "a.md", "A note", "body");
        s.SetLabels(id, "lateral movement via psexec", "Malware", "Reference", Array.Empty<string>(), "m", "t");

        s.SearchTitles("psexec", 10).Should().Contain(id);
    }

    [Fact]
    public void SetTitleTags_keeps_summary_in_search()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var id = Add(s, src, "a.md", "A note", "body");
        s.SetLabels(id, "lateral movement via psexec", "Malware", "Reference", Array.Empty<string>(), "m", "t");

        s.SetTitleTags(id, "Renamed note", new[] { "x" });

        s.SearchTitles("psexec", 10).Should().Contain(id);
        s.SearchTitles("Renamed", 10).Should().Contain(id);
    }

    [Fact]
    public void Children_returns_notes_with_parent()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var p = Add(s, src, "p.md", "Parent", "pb");
        var c1 = Add(s, src, "c1.md", "C1", "b1");
        var c2 = Add(s, src, "c2.md", "C2", "b2");
        Add(s, src, "o.md", "Other", "b3");
        s.SetParent(c1, p);
        s.SetParent(c2, p);

        s.Children(p).Should().Equal(c1, c2);
        s.Labels(c1).ParentId.Should().Be(p);
        s.Meta(c1)!.ParentId.Should().Be(p);
    }

    [Fact]
    public void SetAiFailed_and_SetCategoryType()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var a = Add(s, src, "a.md", "A", "b");
        var b = Add(s, src, "b.md", "B", "b2");
        s.SetAiFailed(new[] { a, b });
        s.Labels(a).AiState.Should().Be("failed");
        s.SetCategoryType(new[] { a, b }, "Network", null);
        s.Labels(a).Category.Should().Be("Network");
        s.Labels(a).Type.Should().BeNull();
        s.SetCategoryType(new[] { a }, null, "Template / checklist");
        s.Labels(a).Type.Should().Be("Template / checklist");
        s.Labels(a).Category.Should().Be("Network");
    }

    [Fact]
    public void ReplaceNoteContent_keeps_summary_in_search()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var id = Add(s, src, "a.md", "A note", "body");
        s.SetLabels(id, "lateral movement via psexec", "Malware", "Reference", Array.Empty<string>(), "m", "t");
        s.ReplaceNoteContent(id, "New title", "h2", "new body", 1.0, new[] { new ChunkPiece(0, "", "new body", "new body") }, null);
        s.SearchTitles("psexec", 10).Should().Contain(id);
        s.SearchTitles("New", 10).Should().Contain(id);
    }
}
