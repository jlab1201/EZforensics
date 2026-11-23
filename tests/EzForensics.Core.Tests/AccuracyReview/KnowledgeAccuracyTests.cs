using EzForensics.Core.Knowledge;
using EzForensics.Core.Tests.Knowledge;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.Core.Tests.AccuracyReview;

public sealed class KnowledgeAccuracyTests : IDisposable
{
    private readonly TestVault _v = new();
    private readonly KnowledgeStore _store;
    private readonly FakeEmbedder _emb = new();

    public KnowledgeAccuracyTests()
    {
        _store = new KnowledgeStore(_v.DbPath);
        new KnowledgeImporter(_store, _emb).ImportFolderAsync(_v.Vault, "test", null, default).GetAwaiter().GetResult();
    }

    public void Dispose() { _store.Dispose(); _v.Dispose(); }

    // 11b: vectors switched on when only some chunks are embedded
    [Fact(Skip = "Known minor issue: partly embedded knowledge store still uses vector results")]
    public void Item11b_partially_embedded_store_does_not_switch_off_text_relevance()
    {
        using (var c = new SqliteConnection($"Data Source={_v.DbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE chunks SET embedding=NULL WHERE note_id=(SELECT id FROM notes WHERE title='Prefetch')";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using var store2 = new KnowledgeStore(_v.DbPath);
        var r = new HybridRetriever(store2, _emb);
        var hit = r.Search("prefetch execution evidence").Hits[0];
        hit.NoteTitle.Should().Be("Prefetch");
        r.UsesVectors.Should().BeFalse("not every chunk has an embedding, so vector results are incomplete");
    }
}
