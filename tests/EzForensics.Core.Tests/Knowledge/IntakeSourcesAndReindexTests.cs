using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.Core.Tests.Knowledge;

/// <summary>Final review fixes: one source per folder/pack, folders of documents, safe Re-index, soft Reject.</summary>
public class IntakeSourcesAndReindexTests : IDisposable
{
    private readonly TestVault _v = new();
    private readonly KnowledgeStore _store;
    private readonly string _docs;

    public IntakeSourcesAndReindexTests()
    {
        _store = new KnowledgeStore(_v.DbPath);
        _docs = Path.Combine(_v.Root, "docs");
        Directory.CreateDirectory(_docs);
    }

    public void Dispose() { _store.Dispose(); _v.Dispose(); }

    private KnowledgeIntake Intake() => new(_store, new KnowledgeImporter(_store, null), null);

    private KnowledgeImporter Importer() => new(_store, null);

    /// <summary>Adds the way the Inbox does: no source name.</summary>
    private static Task<IntakeResult> Add(KnowledgeIntake k, params string[] paths) => k.AddAsync(paths, "", null, CancellationToken.None);

    private string Doc(string name, string text, string? dir = null)
    {
        var p = Path.Combine(dir ?? _docs, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private static string Words(int n, string prefix = "w") => string.Join(" ", Enumerable.Range(0, n).Select(i => prefix + i));

    private void AcceptAll(KnowledgeIntake k) => k.Accept(_store.NotesByStatus("pending").Select(n => n.Id).ToList());

    private SourceInfo Source(string name) => _store.Sources().Single(s => s.Name == name);

    private string CopyOf(string source, string notePath) => Path.Combine(Importer().FilesDir(Source(source).Id), notePath);

    // ---- C1: sources

    [Fact]
    public async Task Add_Folder_Pack_AndLooseFiles_EachGetTheirOwnSource()
    {
        var pack = Path.Combine(_v.Root, "pack.ezkb");
        EzkbPack.Write(_v.Vault, pack, "Field Guide", null);
        File.WriteAllText(Path.Combine(_v.Vault, "Windows", "Prefetch.md"), "# Prefetch\nEdited so the pack and the vault differ. " + Words(10));

        var r = await Add(Intake(), _v.Vault, pack, Doc("a.txt", "Loose document text " + Words(30)));

        r.Errors.Should().BeEmpty();
        _store.Sources().Select(s => (s.Name, s.Kind)).Should().BeEquivalentTo(new[]
        {
            ("vault", "vault"), ("Field Guide", "pack"), (KnowledgeIntake.DocumentsSource, KnowledgeImporter.FilesKind),
        });
        Source("vault").Path.Should().Be(_v.Vault);
        _store.NotesByStatus("pending", Source("vault").Id).Should().HaveCount(7);
        _store.NotesByStatus("pending", Source(KnowledgeIntake.DocumentsSource).Id).Should().ContainSingle();
    }

    [Fact]
    public async Task Ezkb_pack_is_checked_as_new_and_AddSelected_imports_it_returning_pending_ids()
    {
        var pack = Path.Combine(_v.Root, "pack.ezkb");
        EzkbPack.Write(_v.Vault, pack, "Field Guide", null);
        var k = Intake();

        var check = await k.CheckFilesAsync(new[] { pack }, CancellationToken.None);
        check.Should().ContainSingle().Which.State.Should().Be(IntakeFileState.New);

        var (r, ids) = await k.AddSelectedAsync(null, null, new[] { pack }, null, CancellationToken.None);
        r.Errors.Should().BeEmpty();
        ids.Should().HaveCount(7);
        _store.NotesByStatus("pending", Source("Field Guide").Id).Should().HaveCount(7);
    }

    [Fact]
    public async Task AddSelected_bad_pack_is_an_error_not_an_exception()
    {
        var pack = Path.Combine(_v.Root, "broken.ezkb");
        File.WriteAllText(pack, "not a zip");
        var (r, ids) = await Intake().AddSelectedAsync(null, null, new[] { pack }, null, CancellationToken.None);
        r.Errors.Should().ContainSingle().Which.File.Should().Be(pack);
        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task Add_VaultThenLooseFile_ThenReindexDocuments_VaultNotesSurvive()
    {
        var k = Intake();
        await Add(k, _v.Vault);
        AcceptAll(k);
        await Add(k, Doc("a.txt", "Loose document text " + Words(30)));
        AcceptAll(k);

        var rr = await Importer().ReindexFilesAsync(KnowledgeIntake.DocumentsSource, null, CancellationToken.None);

        rr.Errors.Should().BeEmpty();
        Source("vault").Kind.Should().Be("vault", "adding a loose file never changes a vault source");
        Source("vault").Path.Should().Be(_v.Vault);
        _store.NotesByStatus("accepted", Source("vault").Id).Should().HaveCount(7);
        _store.NotesByStatus("accepted").Should().HaveCount(8);
    }

    [Fact]
    public void UpsertSource_NeverChangesKind_AClashGetsANewName()
    {
        var a = _store.UpsertSource("Documents", "vault", @"C:\v");
        var b = _store.UpsertSource("Documents", "files", "");
        var again = _store.UpsertSource("Documents", "files", "");

        b.Should().NotBe(a);
        again.Should().Be(b);
        _store.Sources().Select(s => (s.Name, s.Kind, s.Path)).Should().BeEquivalentTo(new[]
        {
            ("Documents", "vault", @"C:\v"), ("Documents (2)", "files", ""),
        });
    }

    [Fact]
    public async Task Add_TwoVaultsWithTheSameNameAndPaths_StayApart_NoReplacements()
    {
        var one = Path.Combine(_v.Root, "a", "notes");
        var two = Path.Combine(_v.Root, "b", "notes");
        Doc("README.md", "# Readme one\nFirst vault text about prefetch. " + Words(20), one);
        Doc("README.md", "# Readme two\nSecond vault text about shellbags. " + Words(20, "s"), two);
        var k = Intake();

        await Add(k, one);
        AcceptAll(k);
        await Add(k, two);

        var pending = _store.NotesByStatus("pending").Should().ContainSingle().Subject;
        pending.Replaces.Should().BeNull("a note never replaces one of another source");
        AcceptAll(k);
        _store.Sources().Select(s => s.Name).Should().BeEquivalentTo("notes", "notes (2)");
        _store.NotesByStatus("accepted").Should().HaveCount(2);
        Source("notes (2)").Path.Should().Be(two);
    }

    [Fact]
    public async Task Add_SameVaultAgain_UsesItsSource()
    {
        var k = Intake();
        await Add(k, _v.Vault);
        AcceptAll(k);
        File.AppendAllText(Path.Combine(_v.Vault, "Windows", "Prefetch.md"), "\nOne more line. " + Words(5, "x"));

        var r = await Add(k, _v.Vault);

        r.Added.Should().Be(1);
        _store.Sources().Should().ContainSingle();
        _store.NotesByStatus("pending").Single().Replaces.Should().NotBeNull();
    }

    // ---- I1: folders of documents

    [Fact]
    public async Task Add_FolderOfDocumentsWithoutMarkdown_AddsThemAsFiles_UnderTheFolderName()
    {
        var dir = Path.Combine(_v.Root, "Reports");
        Doc("one.txt", "Report one text about lateral movement " + Words(30), dir);
        Doc("two.html", "<html><body><h1>Two</h1><p>Report two about persistence " + Words(30, "h") + "</p></body></html>", dir);
        Doc("img.png", "not an image", dir);

        var r = await Add(Intake(), dir);

        r.Added.Should().Be(2);
        r.Errors.Should().ContainSingle().Which.Error.Should().Contain("Skipped 1 unsupported file(s): .png");
        var src = Source("Reports");
        src.Kind.Should().Be(KnowledgeImporter.FilesKind);
        _store.NotesByStatus("pending", src.Id).Select(n => n.Path).Should().BeEquivalentTo("one.txt", "two.html");
    }

    [Fact]
    public async Task Add_VaultWithOtherDocuments_AddsBoth_IntoTheVaultSource()
    {
        Doc("Report.txt", "A text report kept in the vault folder " + Words(30), Path.Combine(_v.Vault, "Attachments"));
        Doc("pic.jpg", "x", Path.Combine(_v.Vault, "Attachments"));

        var r = await Add(Intake(), _v.Vault);

        r.Added.Should().Be(8);
        r.Errors.Should().ContainSingle().Which.Error.Should().Contain(".jpg");
        _store.Sources().Should().ContainSingle().Which.Kind.Should().Be("vault");
        _store.NotesByStatus("pending").Select(n => n.Path).Should().Contain("Report.txt");
    }

    // ---- I2: re-index never loses accepted notes

    private async Task<Dictionary<string, long>> AcceptedDocs(params (string Name, string Text)[] docs)
    {
        var k = Intake();
        await Add(k, docs.Select(d => Doc(d.Name, d.Text)).ToArray());
        AcceptAll(k);
        return _store.NotesByStatus("accepted").ToDictionary(n => n.Path, n => n.Id);
    }

    [Fact]
    public async Task Reindex_MissingCopy_KeepsTheNote_AndReportsIt()
    {
        var by = await AcceptedDocs(("a.txt", "Alpha text about prefetch " + Words(30)), ("b.txt", "Bravo text about amcache " + Words(30, "b")));
        File.Delete(CopyOf(KnowledgeIntake.DocumentsSource, "b.txt"));

        var r = await Importer().ReindexFilesAsync(KnowledgeIntake.DocumentsSource, null, CancellationToken.None);

        r.Errors.Should().ContainSingle().Which.Error.Should().Contain("missing").And.Contain("kept");
        _store.NotesByStatus("accepted").Select(n => n.Id).Should().BeEquivalentTo(by.Values);
        _store.SearchChunks("amcache", 10).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reindex_ParseError_KeepsTheNote()
    {
        await AcceptedDocs(("a.txt", "Alpha text about prefetch " + Words(30)));
        File.WriteAllText(CopyOf(KnowledgeIntake.DocumentsSource, "a.txt"), "");

        var r = await Importer().ReindexFilesAsync(KnowledgeIntake.DocumentsSource, null, CancellationToken.None);

        r.Errors.Should().ContainSingle();
        _store.SearchChunks("prefetch", 10).Should().NotBeEmpty();
    }

    private sealed class CancelOnFirst(CancellationTokenSource cts) : IProgress<(int, int, string)>
    {
        public void Report((int, int, string) value) => cts.Cancel();
    }

    [Fact]
    public async Task Reindex_Cancelled_PartWay_LosesNothing()
    {
        var by = await AcceptedDocs(("a.txt", "Alpha text about prefetch " + Words(30)), ("b.txt", "Bravo text about amcache " + Words(30, "b")));
        using var cts = new CancellationTokenSource();

        var act = () => Importer().ReindexFilesAsync(KnowledgeIntake.DocumentsSource, new CancelOnFirst(cts), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.NotesByStatus("accepted").Select(n => n.Id).Should().BeEquivalentTo(by.Values);
        _store.SearchChunks("prefetch", 10).Should().NotBeEmpty();
        _store.SearchChunks("amcache", 10).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reindex_CopyWithNoNote_ComesBackAsPending()
    {
        var by = await AcceptedDocs(("a.txt", "Alpha text about prefetch " + Words(30)), ("b.txt", "Bravo text about amcache " + Words(30, "b")));
        _store.DeleteNote(by["b.txt"]); // as an older re-index left it: the copy is still there

        var r = await Importer().ReindexFilesAsync(KnowledgeIntake.DocumentsSource, null, CancellationToken.None);

        r.Result.Added.Should().Be(1);
        _store.NotesByStatus("pending").Single().Path.Should().Be("b.txt");
        _store.NotesByStatus("accepted").Single().Path.Should().Be("a.txt");
    }

    // ---- I3: re-index keeps ids, edits, concepts, cleanup

    [Fact]
    public async Task Reindex_KeepsIdEditedTitleTagsAndLayout_RunsCleanerAndConcepts()
    {
        var by = await AcceptedDocs(("a.txt", "Prefetch files and Event ID 4624 matter.\n" + Words(30) + "\nPage 3 of 9\n" + Words(10, "z")));
        var id = by["a.txt"];
        _store.SetTitleTags(id, "My title", new[] { "triage", "windows" });
        _store.SaveLayout(new Dictionary<string, (double, double)> { ["n:" + id] = (1, 2) });
        _store.ReplaceNoteConcepts(id, new Dictionary<long, int>()); // as a legacy re-index left it
        _store.IsEdited(id).Should().BeTrue();

        var r = await Importer().ReindexFilesAsync(KnowledgeIntake.DocumentsSource, null, CancellationToken.None);

        r.Errors.Should().BeEmpty();
        r.Result.Updated.Should().Be(1);
        var n = _store.Note(id);
        n.Should().NotBeNull("the note keeps its id");
        n!.Value.Title.Should().Be("My title");
        n.Value.Tags.Should().Be("triage,windows");
        n.Value.Body.Should().NotContain("Page 3 of 9", "the cleaner runs on re-index");
        _store.Meta(id)!.Status.Should().Be("accepted");
        _store.NoteConcepts().Should().Contain(c => c.NoteId == id);
        _store.Layout().Should().ContainKey("n:" + id);
        _store.SearchTitles("\"My title\"", 10).Should().Contain(id);
    }

    [Fact]
    public async Task Reindex_NotEdited_TakesTheParsedTitle()
    {
        var by = await AcceptedDocs(("a.txt", "Alpha text about prefetch " + Words(30)));
        _store.IsEdited(by["a.txt"]).Should().BeFalse();

        await Importer().ReindexFilesAsync(KnowledgeIntake.DocumentsSource, null, CancellationToken.None);

        _store.Note(by["a.txt"])!.Value.Title.Should().Be("a");
    }

    // ---- I4: soft reject

    [Fact]
    public async Task RejectUndoAcceptOfAReplacement_SwapsTheCopy_ReindexKeepsTheNewText()
    {
        var k = Intake();
        var p = Doc("a.txt", "Version one text " + Words(20));
        await Add(k, p);
        AcceptAll(k);
        File.WriteAllText(p, "Version two text " + Words(20, "v"));
        await Add(k, p);
        var pending = _store.NotesByStatus("pending").Single();

        k.Reject(new[] { pending.Id });
        k.Undo(pending.Id);
        k.Accept(new[] { pending.Id });

        File.ReadAllText(CopyOf(KnowledgeIntake.DocumentsSource, "a.txt")).Should().StartWith("Version two");
        await Importer().ReindexFilesAsync(KnowledgeIntake.DocumentsSource, null, CancellationToken.None);
        _store.NoteBody(pending.Id).Should().StartWith("Version two");
    }

    [Fact]
    public async Task EmptyRejected_DeletesTheStoredCopies()
    {
        var k = Intake();
        await Add(k, Doc("a.txt", "Alpha text about prefetch " + Words(30)));
        k.Reject(_store.NotesByStatus("pending").Select(n => n.Id).ToList());
        var copy = CopyOf(KnowledgeIntake.DocumentsSource, "a.txt");
        File.Exists(copy).Should().BeTrue("reject keeps the copy");

        k.EmptyRejected().Should().Be(1);

        File.Exists(copy).Should().BeFalse();
    }
}
