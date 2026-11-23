using EzForensics.Core.Knowledge;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.Core.Tests.Knowledge;

public class KnowledgeStoreV2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-kv2-" + Guid.NewGuid().ToString("N"));

    public KnowledgeStoreV2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Db => Path.Combine(_dir, "k.db");

    private static VaultNote Note(string path, string title, string body) =>
        new(path, title, "t", Array.Empty<string>(), "h-" + path, body, Array.Empty<VaultLink>());

    private static long Add(KnowledgeStore s, long src, string path, string title, string body) =>
        s.InsertNote(src, Note(path, title, body), new[] { new ChunkPiece(0, "", body, body) }, null);

    [Fact]
    public void Migrate_V1Db_AllNotesAccepted_AddedAtFromSource()
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
CREATE TABLE links(src_note INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
  dst_note INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE, kind TEXT NOT NULL, PRIMARY KEY(src_note, dst_note, kind));
CREATE VIRTUAL TABLE chunks_fts USING fts5(text, heading, content='chunks', content_rowid='id');
CREATE VIRTUAL TABLE notes_fts USING fts5(title, content='notes', content_rowid='id');
INSERT INTO meta VALUES('schema_version','1');
INSERT INTO sources VALUES(1,'old','vault','p','2026-01-02T03:04:05.0000000Z');
INSERT INTO notes VALUES(1,1,'a.md','A','t','','h1','body a');
INSERT INTO notes VALUES(2,1,'b.md','B','t','','h2','body b');";
            cmd.ExecuteNonQuery();
        }

        using var s = new KnowledgeStore(Db);

        s.GetMeta("schema_version").Should().Be(KnowledgeStore.SchemaVersion.ToString());
        var notes = s.NotesByStatus("accepted");
        notes.Should().HaveCount(2);
        notes.Should().OnlyContain(n => n.AddedAt == "2026-01-02T03:04:05.0000000Z" && n.Flags.Count == 0);
        s.Meta(1)!.Title.Should().Be("A");
    }

    [Fact]
    public void Search_IgnoresPendingAndRejected()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var ok = Add(s, src, "a.md", "Alpha", "zebra stripes evidence");
        var pending = Add(s, src, "b.md", "Beta", "zebra pending evidence");
        var rejected = Add(s, src, "c.md", "Gamma", "zebra rejected evidence");
        s.SetStatus(pending, "pending", "me", "t1");
        s.SetStatus(rejected, "rejected", "me", "t2");

        s.SearchChunks("\"zebra\"", 10).Should().HaveCount(1);
        s.SearchTitles("\"Beta\"", 10).Should().BeEmpty();
        var r = new HybridRetriever(s, null);
        r.Search("zebra").Hits.Select(h => h.NoteId).Should().Equal(ok);

        s.SetStatus(pending, "accepted", "me", "t3");
        r.Invalidate();
        r.Search("zebra").Hits.Select(h => h.NoteId).Should().BeEquivalentTo(new[] { ok, pending });
    }

    [Fact]
    public void SetStatusMany_OneTransaction_AllChanged()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var ids = new[] { Add(s, src, "a.md", "A", "x"), Add(s, src, "b.md", "B", "y"), Add(s, src, "c.md", "C", "z") };

        s.SetStatusMany(ids, "pending", "alice", "2026-11-13");

        s.NotesByStatus("pending").Select(n => n.Id).Should().Equal(ids);
        s.NotesByStatus("accepted").Should().BeEmpty();
    }

    [Fact]
    public void FindByHash_ReturnsStatus()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var id = Add(s, src, "a.md", "A", "x");
        s.SetStatus(id, "rejected", "me", "t");
        s.SetIntakeInfo(id, "2026-11-13", "bob", new[] { "dup", "short" }, 0.75, null);

        var m = s.FindByHash("h-a.md")!;
        m.Id.Should().Be(id);
        m.Status.Should().Be("rejected");
        m.AddedBy.Should().Be("bob");
        m.Flags.Should().Equal("dup", "short");
        m.CleanRatio.Should().Be(0.75);
        s.FindByHash("nope").Should().BeNull();
    }

    [Fact]
    public void Layout_RoundTrips_AndClears()
    {
        using var s = new KnowledgeStore(Db);
        s.SaveLayout(new Dictionary<string, (double X, double Y)> { ["n1"] = (1.5, -2), ["c1"] = (3, 4) });
        s.SaveLayout(new Dictionary<string, (double X, double Y)> { ["n1"] = (9, 9) });

        var l = s.Layout();
        l.Should().HaveCount(2);
        l["n1"].Should().Be((9.0, 9.0, (double?)null));
        l["c1"].Should().Be((3.0, 4.0, (double?)null));

        s.ClearLayout();
        s.Layout().Should().BeEmpty();
    }

    [Fact]
    public void Layout_Z_RoundTrips()
    {
        using var s = new KnowledgeStore(Db);
        s.SaveLayout(new Dictionary<string, (double X, double Y, double? Z)> { ["n1"] = (1.5, -2, 7.25), ["c1"] = (3, 4, null) });
        s.SaveLayout(new Dictionary<string, (double X, double Y, double? Z)> { ["c1"] = (5, 6, -8) });

        var l = s.Layout();
        l["n1"].Should().Be((1.5, -2.0, (double?)7.25));
        l["c1"].Should().Be((5.0, 6.0, (double?)-8.0));
    }

    [Fact]
    public void Migrate_V2Db_AddsLayoutZ_OldRowsHaveNoZ()
    {
        using (var s = new KnowledgeStore(Db)) { }   // a current database, then turned back into v2 by hand
        using (var c = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
DROP TABLE layout;
CREATE TABLE layout(node_key TEXT PRIMARY KEY, x REAL, y REAL);
INSERT INTO layout VALUES('n1', 10, 20);
DROP INDEX notes_parent;
DROP TRIGGER notes_ai;
DROP TRIGGER notes_ad;
DROP TABLE notes_fts;
ALTER TABLE notes DROP COLUMN summary;
ALTER TABLE notes DROP COLUMN category;
ALTER TABLE notes DROP COLUMN note_type;
ALTER TABLE notes DROP COLUMN parent_id;
ALTER TABLE notes DROP COLUMN ai_state;
ALTER TABLE notes DROP COLUMN ai_model;
ALTER TABLE notes DROP COLUMN ai_at;
UPDATE meta SET value='2' WHERE key='schema_version';";
            cmd.ExecuteNonQuery();
        }

        using var store = new KnowledgeStore(Db);

        store.GetMeta("schema_version").Should().Be(KnowledgeStore.SchemaVersion.ToString());
        store.Layout()["n1"].Should().Be((10.0, 20.0, (double?)null));
        store.SaveLayout(new Dictionary<string, (double X, double Y, double? Z)> { ["n1"] = (1, 2, 3) });
        store.Layout()["n1"].Should().Be((1.0, 2.0, (double?)3.0));
    }

    [Fact]
    public void Concepts_UpsertAndReplace()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("v", "vault", "p");
        var n = Add(s, src, "a.md", "A", "x");
        var c1 = s.UpsertConcept("tool", "zeek", "Zeek");
        s.UpsertConcept("tool", "zeek", "Zeek 2").Should().Be(c1);
        var c2 = s.UpsertConcept("event", "4624", "4624");

        s.ReplaceNoteConcepts(n, new Dictionary<long, int> { [c1] = 3, [c2] = 1 });
        s.ReplaceNoteConcepts(n, new Dictionary<long, int> { [c2] = 5 });

        s.NoteConcepts().Should().Equal((n, c2, 5));
        s.Concepts().Should().Contain((c1, "tool", "zeek", "Zeek 2"));
    }

    [Fact]
    public void SetTitleTags_UpdatesNoteAndTitleSearch()
    {
        using var s = new KnowledgeStore(Db);
        var src = s.UpsertSource("S", "files", "");
        var id = Add(s, src, "a.txt", "Old title", "body text");

        s.SetTitleTags(id, "Prefetch basics", new[] { "dfir", "windows" });

        var n = s.Note(id)!.Value;
        n.Title.Should().Be("Prefetch basics");
        n.Tags.Should().Be("dfir,windows");
        s.SearchTitles("prefetch", 10).Should().Contain(id);
        s.SearchTitles("old", 10).Should().NotContain(id);
    }
}
