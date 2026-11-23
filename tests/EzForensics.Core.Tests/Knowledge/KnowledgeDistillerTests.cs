using EzForensics.Core.Analysis;
using EzForensics.Core.Knowledge;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.Core.Tests.Knowledge;

public class KnowledgeDistillerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-kdis-" + Guid.NewGuid().ToString("N"));

    public KnowledgeDistillerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private KnowledgeStore NewStore() => new(Path.Combine(_dir, "k.db"));

    private static long Add(KnowledgeStore s, long src, string name, string body, string status = "accepted") =>
        s.InsertNote(src, new VaultNote(name + ".md", name, "t", Array.Empty<string>(), "h-" + name, body, Array.Empty<VaultLink>()),
            new[] { new ChunkPiece(0, "", body, body) }, null, status);

    private static string Notes(params (string Title, string Body)[] n) =>
        "[" + string.Join(",", n.Select(x => $"{{\"title\":\"{x.Title}\",\"body\":\"{x.Body}\",\"category\":\"Memory\",\"type\":\"Reference\",\"tags\":[\"vol\",\"ram\"]}}")) + "]";

    private static string Long(int chars) => string.Join("\n\n", Enumerable.Range(0, chars / 40 + 1).Select(i => "Paragraph " + i + " about artifacts and evidence."));

    [Fact]
    public async Task Distill_creates_pending_children_with_parent_and_structure_link()
    {
        using var s = NewStore();
        var src = s.UpsertSource("v", "vault", Path.Combine(_dir, "v"));
        var parent = Add(s, src, "Big", "Some long document text about memory forensics and volatility plugins.");
        var d = new KnowledgeDistiller((_, _) => Task.FromResult(Notes(("Pslist", "Pslist walks the process list of a memory image."), ("Malfind", "Malfind finds injected code regions in processes."))), "m1");
        var res = await d.DistillAsync(s, new KnowledgeImporter(s, new FakeEmbedder()), parent, false, null, CancellationToken.None);
        res.Should().Be(new DistillResult(2, 1, 0));
        var kids = s.Children(parent);
        kids.Should().HaveCount(2);
        foreach (var k in kids)
        {
            var m = s.Meta(k)!;
            m.Status.Should().Be("pending");
            m.ParentId.Should().Be(parent);
            m.SourceId.Should().Be(src);
            m.AddedBy.Should().Be(CurrentIdentity.Name);
            m.Path.Should().StartWith("Big.md#distilled-");
            s.Labels(k).AiState.Should().Be("labelled");
            s.Labels(k).Category.Should().Be("Memory");
            s.Labels(k).Tags.Should().Equal("vol", "ram");
            s.Chunks(status: "pending").Any(c => c.NoteId == k && c.Embedding != null).Should().BeTrue();
            s.Links().Should().Contain((k, parent, LinkKinds.Structure));
        }
        kids.Select(k => s.Meta(k)!.Path).Distinct().Should().HaveCount(2);
        d.PendingChildren(s, parent).Should().Be(2);
    }

    [Fact]
    public async Task Parent_unchanged()
    {
        using var s = NewStore();
        var src = s.UpsertSource("v", "vault", Path.Combine(_dir, "v"));
        var parent = Add(s, src, "Big", "Parent body text.");
        var before = s.Meta(parent)!;
        var d = new KnowledgeDistiller((_, _) => Task.FromResult(Notes(("A", "Alpha idea is explained here in plain words."))), "m1");
        await d.DistillAsync(s, new KnowledgeImporter(s, null), parent, false, null, CancellationToken.None);
        var after = s.Meta(parent)!;
        after.Status.Should().Be(before.Status);
        after.Hash.Should().Be(before.Hash);
        s.Note(parent)!.Value.Body.Should().Be("Parent body text.");
    }

    [Fact]
    public async Task Rerun_without_replace_throws_and_with_replace_keeps_accepted_children()
    {
        using var s = NewStore();
        var src = s.UpsertSource("v", "vault", Path.Combine(_dir, "v"));
        var parent = Add(s, src, "Big", "Parent body text.");
        var imp = new KnowledgeImporter(s, null);
        var round = 0;
        var d = new KnowledgeDistiller((_, _) => { round++; return Task.FromResult(Notes(($"T{round}a", $"First distilled idea number {round} stated plainly."), ($"T{round}b", $"Second distilled idea number {round} stated plainly."))); }, "m1");
        await d.DistillAsync(s, imp, parent, false, null, CancellationToken.None);
        var first = s.Children(parent);
        s.SetStatus(first[0], "accepted", "me", DateTime.UtcNow.ToString("o"));

        var act = () => d.DistillAsync(s, imp, parent, false, null, CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*has pending notes*");
        s.Children(parent).Should().Equal(first);

        var res = await d.DistillAsync(s, imp, parent, true, null, CancellationToken.None);
        res.Created.Should().Be(2);
        var now = s.Children(parent);
        now.Should().Contain(first[0]);
        now.Select(k => s.Meta(k)!.Title).Should().NotContain("T1b"); // ids can be reused, so check by title
        now.Should().HaveCount(3);
        now.Select(k => s.Meta(k)!.Path).Distinct().Should().HaveCount(3);
        d.PendingChildren(s, parent).Should().Be(2);
    }

    [Fact]
    public async Task Empty_array_creates_nothing()
    {
        using var s = NewStore();
        var src = s.UpsertSource("v", "vault", Path.Combine(_dir, "v"));
        var parent = Add(s, src, "Big", "Parent body.");
        var d = new KnowledgeDistiller((_, _) => Task.FromResult("[]"), "m1");
        var res = await d.DistillAsync(s, new KnowledgeImporter(s, null), parent, false, null, CancellationToken.None);
        res.Should().Be(new DistillResult(0, 1, 0));
        s.Children(parent).Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_section_counted_others_kept()
    {
        using var s = NewStore();
        var src = s.UpsertSource("v", "vault", Path.Combine(_dir, "v"));
        var parent = Add(s, src, "Big", Long(14000));
        var calls = 0;
        var d = new KnowledgeDistiller((_, _) =>
        {
            var n = ++calls;
            if (n == 2) throw new InvalidOperationException("boom");
            return Task.FromResult(Notes(($"S{n}", $"Section {n} idea written out in a few plain words.")));
        }, "m1");
        var res = await d.DistillAsync(s, new KnowledgeImporter(s, null), parent, false, null, CancellationToken.None);
        res.Sections.Should().BeGreaterThanOrEqualTo(3);
        res.FailedSections.Should().Be(1);
        res.Created.Should().Be(res.Sections - 1);
        s.Children(parent).Should().HaveCount(res.Created);
    }

    [Fact]
    public async Task Child_duplicate_of_existing_note_is_flagged()
    {
        using var s = NewStore();
        var src = s.UpsertSource("v", "vault", Path.Combine(_dir, "v"));
        var parent = Add(s, src, "Big", "Parent body.");
        var other = Add(s, src, "Other", "Pslist walks the process list of a memory image.");
        var d = new KnowledgeDistiller((_, _) => Task.FromResult(Notes(("Pslist", "Pslist walks the process list of a memory image."))), "m1");
        var res = await d.DistillAsync(s, new KnowledgeImporter(s, null), parent, false, null, CancellationToken.None);
        res.Created.Should().Be(1);
        s.Meta(s.Children(parent).Single())!.Flags.Should().Contain("same:" + other);
    }

    [Fact]
    public async Task Full_reimport_does_not_remove_accepted_distilled_children()
    {
        using var v = new TestVault();
        using var s = NewStore();
        var imp = new KnowledgeImporter(s, null);
        await imp.ImportFolderAsync(v.Vault, "vault", null, CancellationToken.None);
        var sid = s.FindSource("vault")!.Value;
        var parent = s.NotesByStatus("accepted", sid).First();
        var kid = Add(s, sid, "kid", "A distilled body.", "accepted");
        s.SetParent(kid, parent.Id);
        await imp.ImportFolderAsync(v.Vault, "vault", null, CancellationToken.None);
        s.Meta(kid).Should().NotBeNull();
    }
}
