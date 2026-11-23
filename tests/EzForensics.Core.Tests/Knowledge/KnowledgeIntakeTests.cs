using System.Diagnostics;
using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.Core.Tests.Knowledge;

public class KnowledgeIntakeTests : IDisposable
{
    private readonly TestVault _v = new();
    private readonly KnowledgeStore _store;
    private readonly string _docs;

    public KnowledgeIntakeTests()
    {
        _store = new KnowledgeStore(_v.DbPath);
        _docs = Path.Combine(_v.Root, "docs");
        Directory.CreateDirectory(_docs);
    }

    public void Dispose() { _store.Dispose(); _v.Dispose(); }

    /// <summary>Every text gets the same vector: any two notes are 100% alike.</summary>
    private sealed class ConstEmbedder : IEmbedder
    {
        public string ModelId => "const";
        public int Dims => 4;
        public float[][] Embed(IReadOnlyList<string> texts, CancellationToken ct) => texts.Select(_ => new[] { 1f, 0f, 0f, 0f }).ToArray();
        public float[] EmbedQuery(string text, CancellationToken ct) => new[] { 1f, 0f, 0f, 0f };
    }

    private KnowledgeIntake Intake(IEmbedder? emb = null) => new(_store, new KnowledgeImporter(_store, emb), emb);

    private string Doc(string name, string text)
    {
        var p = Path.Combine(_docs, name);
        File.WriteAllText(p, text);
        return p;
    }

    private static string Words(int n, string prefix = "w") => string.Join(" ", Enumerable.Range(0, n).Select(i => prefix + i));

    private static Task<IntakeResult> Add(KnowledgeIntake k, params string[] paths) => k.AddAsync(paths, "Docs", null, CancellationToken.None);

    private static VaultNote Note(string path, string title, string body) =>
        new(path, title, "t", Array.Empty<string>(), "h-" + path, body, Array.Empty<VaultLink>());

    private long AddRaw(long src, string path, string title, string body, string status) =>
        _store.InsertNote(src, Note(path, title, body), new[] { new ChunkPiece(0, "", body, body) }, null, status);

    [Fact]
    public async Task Add_NewFile_IsPending_NotSearchable()
    {
        var k = Intake();
        var r = await Add(k, Doc("a.txt", "Prefetch files record program execution evidence for the examiner."));

        r.Added.Should().Be(1);
        r.Errors.Should().BeEmpty();
        var n = _store.NotesByStatus("pending").Should().ContainSingle().Subject;
        n.AddedBy.Should().NotBeNullOrEmpty();
        _store.SearchChunks("prefetch", 10).Should().BeEmpty();
        _store.SearchTitles("a", 10).Should().BeEmpty();

        k.Accept(new[] { n.Id });
        _store.SearchChunks("prefetch", 10).Should().NotBeEmpty();
        _store.Meta(n.Id)!.Status.Should().Be("accepted");
    }

    [Fact]
    public async Task Add_SameFileWhilePending_AlreadyWaiting_NoNewRow()
    {
        var k = Intake();
        var p = Doc("a.txt", "Shimcache entries survive a reboot on most Windows versions.");
        await Add(k, p);
        var r = await Add(k, p);

        r.Added.Should().Be(0);
        r.AlreadyWaiting.Should().Be(1);
        _store.NoteCount().Should().Be(1);
    }

    [Fact]
    public async Task Add_RejectedFileAgain_RejectedBefore()
    {
        var k = Intake();
        var p = Doc("a.txt", "Amcache stores the SHA1 of executables that ran on the machine.");
        await Add(k, p);
        k.Reject(new[] { _store.NotesByStatus("pending")[0].Id });
        var r = await Add(k, p);

        r.RejectedBefore.Should().Be(1);
        r.Added.Should().Be(0);
        _store.NoteCount().Should().Be(1);
    }

    [Fact]
    public async Task Add_AcceptedFileAgain_Unchanged()
    {
        var k = Intake();
        var p = Doc("a.txt", "Jump lists record recently opened files per application.");
        await Add(k, p);
        k.Accept(new[] { _store.NotesByStatus("pending")[0].Id });
        var r = await Add(k, p);

        r.Unchanged.Should().Be(1);
        _store.NoteCount().Should().Be(1);
    }

    [Fact]
    public async Task Accepting_a_replacement_moves_the_distilled_children_to_the_new_note()
    {
        var k = Intake();
        var p = Doc("a.txt", "Original text about zebrafoo artefacts in the registry.");
        await Add(k, p);
        var oldId = _store.NotesByStatus("pending")[0].Id;
        k.Accept(new[] { oldId });
        await Add(k, Doc("kid.txt", "A distilled idea about registry hives and their transaction logs."));
        var kid = _store.NotesByStatus("pending").Single().Id;
        _store.SetParent(kid, oldId);

        File.WriteAllText(p, "Rewritten text about quokkabar artefacts in the registry.");
        await Add(k, p);
        var replacement = _store.NotesByStatus("pending").Single(n => n.Replaces == oldId).Id;
        k.Accept(new[] { replacement });

        _store.Meta(oldId).Should().BeNull();
        _store.Labels(kid).ParentId.Should().Be(replacement);
        _store.Children(replacement).Should().Equal(kid);
    }

    [Fact]
    public async Task Add_ChangedFile_PendingWithReplaces_OldStillSearchable()
    {
        var k = Intake();
        var p = Doc("a.txt", "Original text about zebrafoo artefacts in the registry.");
        await Add(k, p);
        var oldId = _store.NotesByStatus("pending")[0].Id;
        k.Accept(new[] { oldId });

        File.WriteAllText(p, "Rewritten text about quokkabar artefacts in the registry.");
        var r = await Add(k, p);

        r.Added.Should().Be(1);
        var pending = _store.NotesByStatus("pending").Should().ContainSingle().Subject;
        pending.Replaces.Should().Be(oldId);
        _store.SearchChunks("zebrafoo", 10).Should().NotBeEmpty();
        _store.SearchChunks("quokkabar", 10).Should().BeEmpty();

        k.Accept(new[] { pending.Id });
        _store.Meta(oldId).Should().BeNull();
        _store.SearchChunks("zebrafoo", 10).Should().BeEmpty();
        _store.SearchChunks("quokkabar", 10).Should().NotBeEmpty();
        _store.Meta(pending.Id)!.Path.Should().Be("a.txt");
        _store.NoteCount().Should().Be(1);
    }

    [Fact]
    public async Task Add_NearCopy_FlagsNear95()
    {
        var emb = new ConstEmbedder();
        var k = Intake(emb);
        await Add(k, Doc("a.txt", "First document " + Words(60)));
        var first = _store.NotesByStatus("pending")[0];
        k.Accept(new[] { first.Id });

        var r = await Add(k, Doc("b.txt", "Second document " + Words(60, "x")));

        r.Added.Should().Be(1);
        _store.NotesByStatus("pending").Single().Flags.Should().Contain($"near:{first.Id}:100");
    }

    [Fact]
    public async Task Add_NoEmbedder_UsesJaccard_FlagsNear()
    {
        var k = Intake(null);
        await Add(k, Doc("a.txt", Words(100)));
        var first = _store.NotesByStatus("pending")[0];
        k.Accept(new[] { first.Id });

        await Add(k, Doc("b.txt", Words(99) + " changed"));
        var near = _store.NotesByStatus("pending").Single().Flags.Single(f => f.StartsWith("near:"));
        near.Should().StartWith($"near:{first.Id}:");

        await Add(k, Doc("c.txt", Words(100, "unrelated")));
        _store.NotesByStatus("pending").Single(n => n.Path == "c.txt").Flags.Should().NotContain(f => f.StartsWith("near:"));
    }

    [Fact]
    public async Task Add_IdenticalBodyOtherBytes_FlagsSame_AndShortTextFlag()
    {
        var k = Intake(null);
        await Add(k, Doc("a.txt", Words(100)));
        var first = _store.NotesByStatus("pending")[0];
        k.Accept(new[] { first.Id });

        await Add(k, Doc("b.txt", Words(100) + "\n\n\n\n\n\n\n"));
        var b = _store.NotesByStatus("pending").Single();
        b.Flags.Should().Contain($"same:{first.Id}");

        await Add(k, Doc("tiny.txt", "short"));
        _store.NotesByStatus("pending").Single(n => n.Path == "tiny.txt").Flags.Should().Contain("little-text");
    }

    [Fact]
    public void AcceptAllFromSource_1000Notes_OneTransaction_UnderTenSeconds()
    {
        var sid = _store.UpsertSource("Bulk", "vault", "");
        for (var i = 0; i < 1000; i++) AddRaw(sid, $"n{i}.md", "Note " + i, "body " + i, "pending");
        var k = Intake();

        var sw = Stopwatch.StartNew();
        var n = k.AcceptAllFromSource(sid);
        sw.Stop();

        n.Should().Be(1000);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        _store.NotesByStatus("accepted", sid).Should().HaveCount(1000);
        _store.NotesByStatus("pending").Should().BeEmpty();
    }

    [Fact]
    public void Reject_Undo_BackToPending()
    {
        var sid = _store.UpsertSource("S", "vault", "");
        var id = AddRaw(sid, "a.md", "A", "alpha body", "pending");
        var k = Intake();

        k.Reject(new[] { id });
        _store.Meta(id)!.Status.Should().Be("rejected");
        k.Undo(id);
        _store.Meta(id)!.Status.Should().Be("pending");
    }

    [Fact]
    public void EmptyRejected_DeletesRows()
    {
        var sid = _store.UpsertSource("S", "vault", "");
        var a = AddRaw(sid, "a.md", "A", "alpha body", "rejected");
        var b = AddRaw(sid, "b.md", "B", "beta body", "pending");

        Intake().EmptyRejected().Should().Be(1);
        _store.Meta(a).Should().BeNull();
        _store.Meta(b).Should().NotBeNull();
        _store.ChunkCount().Should().Be(1);
    }

    [Fact]
    public void KeepOneOf_RejectsOther()
    {
        var sid = _store.UpsertSource("S", "vault", "");
        var keep = AddRaw(sid, "a.md", "A", "alpha body", "pending");
        var other = AddRaw(sid, "b.md", "B", "beta body", "accepted");

        Intake().KeepOneOf(keep, other);

        _store.Meta(other)!.Status.Should().Be("rejected");
        _store.Meta(keep)!.Status.Should().Be("pending");
    }

    [Fact]
    public async Task Backfill_FillsConceptsForMigratedNotes_Once()
    {
        var sid = _store.UpsertSource("S", "vault", "");
        var id = AddRaw(sid, "a.md", "A", "Prefetch files and Event ID 4624 matter. See T1059 too.", "accepted");
        var k = Intake();

        k.NeedsConceptBackfill.Should().BeTrue();
        await k.BackfillConceptsAsync(null, CancellationToken.None);

        _store.NoteConcepts().Should().Contain(c => c.NoteId == id);
        k.NeedsConceptBackfill.Should().BeFalse();
        _store.GetMeta("concepts_backfilled").Should().Be("1");

        // a second run does nothing: wipe the concepts and see they stay wiped
        _store.ReplaceNoteConcepts(id, new Dictionary<long, int>());
        await k.BackfillConceptsAsync(null, CancellationToken.None);
        _store.NoteConcepts().Should().BeEmpty();
    }

    [Fact]
    public void Service_Suggest_GetNote_Related_IgnorePendingNotes()
    {
        var sid = _store.UpsertSource("S", "vault", "");
        var acc = AddRaw(sid, "a.md", "Zebra Accepted Guide", "alpha body", "accepted");
        var pen = AddRaw(sid, "b.md", "Zebra Pending Guide", "beta body", "pending");
        _store.ReplaceLinks(sid, new[] { (acc, pen, LinkKinds.Mention) });
        _store.Dispose();

        using var svc = new KnowledgeService(_v.DbPath, () => null);

        svc.Suggest("Zebra").Should().Equal("Zebra Accepted Guide");
        svc.GetNote("Zebra Pending Guide").Should().BeNull();
        svc.GetNote("Zebra Accepted Guide").Should().NotBeNull();
        svc.Related("Zebra Accepted Guide").Should().BeEmpty();
        svc.Related("Zebra Pending Guide").Should().BeEmpty();
    }

    private string FilesDirOf(string source = "Docs") => new KnowledgeImporter(_store, null).FilesDir(_store.FindSource(source)!.Value);

    [Fact]
    public async Task Reindex_KeepsPendingAndRejected_NotAccepted()
    {
        var k = Intake();
        await Add(k, Doc("a.txt", "Accepted text about prefetch artefacts " + Words(30)), Doc("b.txt", "Pending text about shimcache " + Words(30, "p")),
            Doc("c.txt", "Rejected text about amcache " + Words(30, "r")));
        var by = _store.NotesByStatus("pending").ToDictionary(n => n.Path, n => n.Id);
        k.Accept(new[] { by["a.txt"] });
        k.Reject(new[] { by["c.txt"] });

        var rr = await new KnowledgeImporter(_store, null).ReindexFilesAsync("Docs", null, CancellationToken.None); rr.Errors.Should().BeEmpty();

        _store.NotesByStatus("accepted").Select(n => n.Path).Should().Equal("a.txt");
        _store.NotesByStatus("pending").Select(n => n.Path).Should().Equal("b.txt");
        _store.NotesByStatus("rejected").Select(n => n.Path).Should().Equal("c.txt");
        _store.SearchChunks("shimcache", 10).Should().BeEmpty();
        _store.SearchChunks("amcache", 10).Should().BeEmpty();
        _store.SearchChunks("prefetch", 10).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Links_MentionAndSemantic_IgnorePendingTargets()
    {
        var k = Intake(new ConstEmbedder());
        await Add(k, Doc("Alpha Bravo Guide.txt", "Alpha text " + Words(40)), Doc("Charlie Delta Notes.txt", "Charlie text " + Words(40, "c")),
            Doc("Echo Report.txt", "Mentions Alpha Bravo Guide and also Charlie Delta Notes. " + Words(40, "e")));
        var by = _store.NotesByStatus("pending").ToDictionary(n => n.Path, n => n.Id);

        k.Accept(new[] { by["Alpha Bravo Guide.txt"], by["Echo Report.txt"] });

        var links = _store.Links();
        links.Should().Contain(l => l.Src == by["Echo Report.txt"] && l.Dst == by["Alpha Bravo Guide.txt"] && l.Kind == LinkKinds.Mention);
        links.Should().Contain(l => l.Src == by["Echo Report.txt"] && l.Dst == by["Alpha Bravo Guide.txt"] && l.Kind == LinkKinds.Semantic);
        links.Should().NotContain(l => l.Dst == by["Charlie Delta Notes.txt"]);
    }

    [Fact]
    public async Task Add_ChangedFile_KeepsOldCopyUntilAccept_RejectKeepsPendingCopyUntilEmptied()
    {
        var k = Intake();
        var p = Doc("a.txt", "Version one text " + Words(20));
        await Add(k, p);
        k.Accept(new[] { _store.NotesByStatus("pending")[0].Id });
        var dir = FilesDirOf();

        File.WriteAllText(p, "Version two text " + Words(20, "v"));
        await Add(k, p);
        File.ReadAllText(Path.Combine(dir, "a.txt")).Should().StartWith("Version one");
        Directory.GetFiles(dir).Should().HaveCount(2);

        var pending = _store.NotesByStatus("pending").Single();
        k.Reject(new[] { pending.Id });
        Directory.GetFiles(dir).Should().HaveCount(2, "reject is a soft delete: the pending copy stays for Undo");
        File.ReadAllText(Path.Combine(dir, "a.txt")).Should().StartWith("Version one");

        // a new change accepted: the copy is swapped
        File.WriteAllText(p, "Version three text " + Words(20, "t"));
        await Add(k, p);
        k.Accept(new[] { _store.NotesByStatus("pending").Single().Id });
        File.ReadAllText(Path.Combine(dir, "a.txt")).Should().StartWith("Version three");

        // Empty rejected deletes the rejected version's copy
        k.EmptyRejected();
        Directory.GetFiles(dir).Should().ContainSingle();
        File.ReadAllText(Path.Combine(dir, "a.txt")).Should().StartWith("Version three");
    }

    [Fact]
    public async Task Add_SameContentTwiceInOneCall_SecondIsAlreadyWaiting()
    {
        var r = await Add(Intake(), Doc("a.txt", "Same words here " + Words(20)), Doc("b.txt", "Same words here " + Words(20)));

        r.Added.Should().Be(1);
        r.AlreadyWaiting.Should().Be(1);
        _store.NoteCount().Should().Be(1);
    }

    [Fact]
    public async Task Add_SecondChangeWhileReplacementPending_SupersedesAndAcceptWorks()
    {
        var k = Intake();
        var p = Doc("a.txt", "Version one text " + Words(20));
        await Add(k, p);
        var oldId = _store.NotesByStatus("pending")[0].Id;
        k.Accept(new[] { oldId });

        File.WriteAllText(p, "Version two text " + Words(20, "v"));
        await Add(k, p);
        File.WriteAllText(p, "Version three text " + Words(20, "t"));
        await Add(k, p);

        var pending = _store.NotesByStatus("pending").Should().ContainSingle().Subject;
        pending.Replaces.Should().Be(oldId);
        Directory.GetFiles(FilesDirOf()).Should().HaveCount(2);

        k.Accept(new[] { pending.Id });
        _store.NoteCount().Should().Be(1);
        _store.Meta(pending.Id)!.Path.Should().Be("a.txt");
        _store.Meta(pending.Id)!.Status.Should().Be("accepted");
    }
}
