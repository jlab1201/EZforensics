using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.Core.Tests.Knowledge;

public class RetrieverAndPackTests : IDisposable
{
    private readonly TestVault _v = new();
    private readonly KnowledgeStore _store;
    private readonly FakeEmbedder _emb = new();

    public RetrieverAndPackTests()
    {
        _store = new KnowledgeStore(_v.DbPath);
        new KnowledgeImporter(_store, _emb).ImportFolderAsync(_v.Vault, "test", null, default).GetAwaiter().GetResult();
    }

    public void Dispose() { _store.Dispose(); _v.Dispose(); }

    [Fact]
    public void Best_chunk_for_a_query_comes_first()
    {
        var r = new HybridRetriever(_store, _emb).Search("prefetch execution evidence");
        r.Hits[0].NoteTitle.Should().Be("Prefetch");
        r.Hits[0].NotePath.Should().Be("Windows/Prefetch.md");
        r.Hits.Select(h => h.Score).Should().BeInDescendingOrder();
    }

    [Fact]
    public void Fusion_uses_both_text_and_vectors()
    {
        var withVec = new HybridRetriever(_store, _emb);
        var textOnly = new HybridRetriever(_store, null);
        withVec.UsesVectors.Should().BeTrue();
        textOnly.UsesVectors.Should().BeFalse();
        // a chunk in both lists outranks a chunk in one list
        var q = "periodic callback traffic to a command and control server";
        withVec.Search(q).Hits[0].NoteTitle.Should().Be("Beaconing");
        withVec.Search(q).Hits[0].Score.Should().BeGreaterThan(textOnly.Search(q).Hits[0].Score);
    }

    [Fact]
    public void Linked_notes_are_returned_as_related()
    {
        var r = new HybridRetriever(_store, _emb).Search("beaconing", k: 1);
        r.Hits.Should().ContainSingle().Which.NoteTitle.Should().Be("Beaconing");
        r.Related.Should().Contain("Zeek");
        r.Related.Should().NotContain("Beaconing");
    }

    [Theory]
    [InlineData("\"unbalanced (quote")]
    [InlineData("NEAR(a b) AND OR NOT *")]
    [InlineData("col:value ' -dash ^caret")]
    [InlineData("C:\\Windows\\Prefetch\\*.pf")]
    [InlineData("")]
    [InlineData("   ")]
    public void Special_characters_in_the_query_do_not_throw(string query)
    {
        var act = () => new HybridRetriever(_store, _emb).Search(query);
        act.Should().NotThrow();
    }

    [Fact]
    public void Sanitized_query_quotes_every_term()
    {
        HybridRetriever.SanitizeFts("foo AND (bar)").Should().Be("\"foo\" OR \"AND\" OR \"bar\"");
        HybridRetriever.SanitizeFts("!!!").Should().BeEmpty();
    }

    [Fact]
    public void A_different_embedder_model_falls_back_to_text_and_graph()
    {
        var r = new HybridRetriever(_store, new FakeEmbedder("other-model"));
        r.UsesVectors.Should().BeFalse();
        r.Search("amcache hive").Hits[0].NoteTitle.Should().Be("Amcache");
    }

    [Fact]
    public async Task Pack_from_a_folder_round_trips_with_prebuilt_index()
    {
        var pack = Path.Combine(_v.Root, "out", "vault.ezkb");
        var m = EzkbPack.Write(_v.Vault, pack, "Fixture", _emb);
        m.Notes.Should().Be(7);
        m.EmbeddingModel.Should().Be("fake-hash");
        m.Dims.Should().Be(64);
        EzkbPack.ReadManifest(pack).Chunks.Should().Be(m.Chunks);

        using var store2 = new KnowledgeStore(Path.Combine(_v.Root, "kb2", "k.db"));
        var emb2 = new FakeEmbedder();
        var r = await EzkbPack.ImportAsync(pack, new KnowledgeImporter(store2, emb2), null, null, default);

        r.Added.Should().Be(7);
        emb2.TextsEmbedded.Should().Be(0); // same model: prebuilt vectors reused
        store2.Sources().Single().Name.Should().Be("Fixture");
        store2.ChunkCount().Should().Be(m.Chunks);
        store2.Chunks().Should().OnlyContain(c => c.Embedding != null);
        new HybridRetriever(store2, emb2).Search("prefetch execution").Hits[0].NoteTitle.Should().Be("Prefetch");
    }

    [Fact]
    public async Task Pack_with_another_model_is_rechunked_and_reembedded()
    {
        var pack = Path.Combine(_v.Root, "out", "vault.ezkb");
        var m = EzkbPack.Write(_v.Vault, pack, "Fixture", _emb);

        using var store2 = new KnowledgeStore(Path.Combine(_v.Root, "kb2", "k.db"));
        var emb2 = new FakeEmbedder("another-model");
        await EzkbPack.ImportAsync(pack, new KnowledgeImporter(store2, emb2), "Renamed", null, default);

        emb2.TextsEmbedded.Should().Be(m.Chunks);
        store2.GetMeta("embed_model").Should().Be("another-model");
        store2.Sources().Single().Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task Pack_without_embedder_has_notes_only_and_imports_as_text()
    {
        var pack = Path.Combine(_v.Root, "out", "text.ezkb");
        var m = EzkbPack.Write(_v.Vault, pack, "Text", null);
        m.EmbeddingModel.Should().BeNull();

        using var store2 = new KnowledgeStore(Path.Combine(_v.Root, "kb2", "k.db"));
        await EzkbPack.ImportAsync(pack, new KnowledgeImporter(store2, null), null, null, default);
        store2.NoteCount().Should().Be(7);
        store2.Chunks().Should().OnlyContain(c => c.Embedding == null);
    }

    [Fact]
    public async Task Pack_written_from_the_store_keeps_titles_tags_and_links()
    {
        var pack = Path.Combine(_v.Root, "out", "store.ezkb");
        EzkbPack.Write(_store, "test", pack);

        using var store2 = new KnowledgeStore(Path.Combine(_v.Root, "kb2", "k.db"));
        var emb2 = new FakeEmbedder();
        var r = await EzkbPack.ImportAsync(pack, new KnowledgeImporter(store2, emb2), null, null, default);

        r.Added.Should().Be(7);
        emb2.TextsEmbedded.Should().Be(0);
        store2.Notes().Select(n => n.Title).Should().BeEquivalentTo(_store.Notes().Select(n => n.Title));
        store2.Links().Count(l => l.Kind == LinkKinds.Moc).Should().Be(3);
    }
}
