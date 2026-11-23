using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.Core.Tests.Knowledge;

public class StoreImporterTests : IDisposable
{
    private readonly TestVault _v = new();
    private readonly KnowledgeStore _store;
    private readonly FakeEmbedder _emb = new();

    public StoreImporterTests() => _store = new KnowledgeStore(_v.DbPath);

    public void Dispose() { _store.Dispose(); _v.Dispose(); }

    private KnowledgeImporter Importer => new(_store, _emb);

    [Fact]
    public void Store_creates_schema_and_meta()
    {
        File.Exists(_v.DbPath).Should().BeTrue();
        _store.GetMeta("schema_version").Should().Be(KnowledgeStore.SchemaVersion.ToString());
        _store.NoteCount().Should().Be(0);
    }

    [Fact]
    public void Embedding_blob_round_trips()
    {
        var v = new[] { 0.5f, -1.25f, 3f };
        KnowledgeStore.FromBlob(KnowledgeStore.ToBlob(v)).Should().Equal(v);
    }

    [Fact]
    public async Task Import_stores_notes_chunks_vectors_and_links()
    {
        var r = await Importer.ImportFolderAsync(_v.Vault, "test", null, default);
        r.Added.Should().Be(7);
        _store.NoteCount().Should().Be(7);
        _store.Chunks().Should().OnlyContain(c => c.Embedding != null && c.Embedding.Length == 64);
        _store.GetMeta("embed_model").Should().Be("fake-hash");
        var titles = _store.Notes().ToDictionary(n => n.Id, n => n.Title);
        var links = _store.Links().Select(l => (titles[l.Src], titles[l.Dst], l.Kind)).ToList();
        links.Should().Contain(("Windows MOC", "Prefetch", LinkKinds.Moc));
        links.Should().Contain(("Windows MOC", "Amcache", LinkKinds.Moc));
        links.Should().Contain(("Windows MOC", "Shimcache", LinkKinds.Moc));
        links.Should().Contain(("Zeek", "Beaconing", LinkKinds.Wikilink));
        links.Should().Contain(l => l.Kind == LinkKinds.Tag);
    }

    [Fact]
    public async Task Second_import_of_an_unchanged_folder_changes_nothing()
    {
        await Importer.ImportFolderAsync(_v.Vault, "test", null, default);
        var ids = _store.Notes().Select(n => n.Id).ToList();
        var chunkIds = _store.Chunks().Select(c => c.Id).ToList();
        var calls = _emb.TextsEmbedded;

        var r = await Importer.ImportFolderAsync(_v.Vault, "test", null, default);

        r.NothingChanged.Should().BeTrue();
        r.Unchanged.Should().Be(7);
        _emb.TextsEmbedded.Should().Be(calls);
        _store.Notes().Select(n => n.Id).Should().Equal(ids);
        _store.Chunks().Select(c => c.Id).Should().Equal(chunkIds);
    }

    [Fact]
    public async Task Edited_note_is_rechunked_and_deleted_note_is_removed()
    {
        await Importer.ImportFolderAsync(_v.Vault, "test", null, default);
        var before = _emb.TextsEmbedded;
        File.AppendAllText(Path.Combine(_v.Vault, "Network", "Zeek.md"), "\n## Extra\n\nNew section about files.log.\n");
        File.Delete(Path.Combine(_v.Vault, "Memory", "Volatility.md"));

        var r = await Importer.ImportFolderAsync(_v.Vault, "test", null, default);

        r.Updated.Should().Be(1);
        r.Removed.Should().Be(1);
        r.Added.Should().Be(0);
        _emb.TextsEmbedded.Should().Be(before + 2); // only the two chunks of the edited note
        _store.NoteCount().Should().Be(6);
        _store.Notes().Select(n => n.Title).Should().NotContain("Volatility");
        _store.Chunks().Should().Contain(c => c.Heading == "Extra");
    }

    [Fact]
    public async Task Removing_a_source_cascades()
    {
        await Importer.ImportFolderAsync(_v.Vault, "test", null, default);
        await Importer.RemoveSourceAsync("test");
        _store.NoteCount().Should().Be(0);
        _store.ChunkCount().Should().Be(0);
        _store.LinkCount().Should().Be(0);
        _store.Sources().Should().BeEmpty();
        _store.SearchChunks("\"prefetch\"", 10).Should().BeEmpty();
    }

    [Fact]
    public async Task Import_without_an_embedder_stores_text_only()
    {
        await new KnowledgeImporter(_store, null).ImportFolderAsync(_v.Vault, "test", null, default);
        _store.Chunks().Should().OnlyContain(c => c.Embedding == null);
        // embedder appears later: vectors are filled in without changing notes
        var r = await Importer.ImportFolderAsync(_v.Vault, "test", null, default);
        r.Updated.Should().Be(7);
        _store.Chunks().Should().OnlyContain(c => c.Embedding != null);
    }

    [Fact]
    public async Task Progress_is_reported_per_changed_note()
    {
        var items = new List<string>();
        await Importer.ImportFolderAsync(_v.Vault, "test", new Progress<(int, int, string)>(p => items.Add(p.Item3)), default);
        await Task.Delay(100);
        items.Should().HaveCount(7);
    }
}
