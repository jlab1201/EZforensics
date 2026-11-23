using EzForensics.Core.Knowledge;
using FluentAssertions;

namespace EzForensics.Core.Tests.Knowledge;

/// <summary>Check for new (classify the files of a folder against the store) and adding only the ticked ones.</summary>
public class IntakeCheckForNewTests : IDisposable
{
    private readonly TestVault _v = new();
    private readonly KnowledgeStore _store;

    public IntakeCheckForNewTests() => _store = new KnowledgeStore(_v.DbPath);

    public void Dispose() { _store.Dispose(); _v.Dispose(); }

    private KnowledgeIntake Intake() => new(_store, new KnowledgeImporter(_store, null), null);

    private string P(string rel) => Path.Combine(_v.Vault, rel.Replace('/', Path.DirectorySeparatorChar));

    private long VaultId() => _store.Sources().Single(s => s.Name == "vault").Id;

    private long NoteId(string rel) => _store.NotesOf(VaultId())[rel].Id;

    [Fact]
    public async Task CheckForNew_classifies_new_changed_already_in_rejected()
    {
        var k = Intake();
        await k.AddAsync(new[] { _v.Vault }, "", null, CancellationToken.None);
        k.Accept(new[] { NoteId("Windows/Prefetch.md") });
        k.Reject(new[] { NoteId("Windows/Amcache.md") });
        File.AppendAllText(P("Windows/Shimcache.md"), "\nEdited on disk with some extra words.\n");
        File.WriteAllText(P("Network/Brand new.md"), "# Brand new\nSomething not seen before.");
        File.WriteAllText(P("notes.bin"), "x");

        var files = await k.CheckForNewAsync(VaultId(), CancellationToken.None);

        IntakeFile F(string rel) => files.Single(f => f.RelPath == rel);
        F("Windows/Prefetch.md").State.Should().Be(IntakeFileState.AlreadyIn);
        F("Network/Zeek.md").State.Should().Be(IntakeFileState.AlreadyIn); // pending
        F("Windows/Amcache.md").State.Should().Be(IntakeFileState.RejectedBefore);
        F("Windows/Shimcache.md").State.Should().Be(IntakeFileState.Changed);
        F("Windows/Shimcache.md").ExistingNoteId.Should().Be(NoteId("Windows/Shimcache.md"));
        F("Network/Brand new.md").State.Should().Be(IntakeFileState.New);
        F("Network/Brand new.md").Title.Should().Be("Brand new");
        F("notes.bin").State.Should().Be(IntakeFileState.Unsupported);
    }

    [Fact]
    public async Task CheckForNew_reports_progress_and_can_be_cancelled()
    {
        var k = Intake();
        await k.AddAsync(new[] { _v.Vault }, "", null, CancellationToken.None);
        var seen = new List<(int done, int total)>();
        var files = await k.CheckForNewAsync(VaultId(), CancellationToken.None, new SyncProgress<(int done, int total)>(seen.Add));
        seen.Should().NotBeEmpty();
        seen[^1].Should().Be((files.Count, files.Count));
        seen.Select(x => x.done).Should().BeInAscendingOrder();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await k.CheckForNewAsync(VaultId(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class SyncProgress<T>(Action<T> on) : IProgress<T>
    {
        public void Report(T value) => on(value);
    }

    [Fact]
    public async Task AddSelected_imports_only_ticked_files_and_removes_nothing()
    {
        var k = Intake();
        await k.AddAsync(new[] { _v.Vault }, "", null, CancellationToken.None);
        k.Accept(_store.NotesByStatus("pending").Select(n => n.Id).ToList());
        var before = _store.NotesOf(VaultId()).ToDictionary(e => e.Key, e => e.Value);
        File.AppendAllText(P("Windows/Prefetch.md"), "\nPrefetch edit one.\n");
        File.AppendAllText(P("Windows/Amcache.md"), "\nAmcache edit two.\n");

        var (r, ids) = await k.AddSelectedAsync(null, _v.Vault, new[] { P("Windows/Prefetch.md") }, null, CancellationToken.None);

        r.Added.Should().Be(1);
        ids.Should().HaveCount(1);
        _store.Meta(ids[0])!.Status.Should().Be("pending");
        _store.Meta(ids[0])!.Replaces.Should().Be(before["Windows/Prefetch.md"].Id);
        var now = _store.NotesOf(VaultId());
        foreach (var (rel, e) in before) now[rel].Should().Be(e);
        _store.NotesByStatus("accepted", VaultId()).Should().HaveCount(before.Count);
        _store.NotesByStatus("pending", VaultId()).Should().ContainSingle();
    }

    [Fact]
    public async Task AddSelected_is_idempotent()
    {
        var k = Intake();
        await k.AddAsync(new[] { _v.Vault }, "", null, CancellationToken.None);
        File.WriteAllText(P("Network/Brand new.md"), "# Brand new\nSomething not seen before, with enough words to be real notes.");
        var sel = new[] { P("Network/Brand new.md") };

        var (r1, ids1) = await k.AddSelectedAsync(null, _v.Vault, sel, null, CancellationToken.None);
        var (r2, ids2) = await k.AddSelectedAsync(null, _v.Vault, sel, null, CancellationToken.None);

        r1.Added.Should().Be(1);
        ids1.Should().HaveCount(1);
        r2.Added.Should().Be(0);
        r2.AlreadyWaiting.Should().Be(1);
        ids2.Should().BeEmpty();
    }

    [Fact]
    public async Task AddSelected_loose_files_go_to_Documents_and_a_new_folder_creates_a_source()
    {
        var k = Intake();
        var docs = Path.Combine(_v.Root, "docs");
        Directory.CreateDirectory(docs);
        var f = Path.Combine(docs, "a.txt");
        File.WriteAllText(f, "Loose document text " + string.Join(" ", Enumerable.Range(0, 30).Select(i => "w" + i)));

        var (r, ids) = await k.AddSelectedAsync(null, null, new[] { f }, null, CancellationToken.None);
        r.Added.Should().Be(1);
        ids.Should().HaveCount(1);
        _store.Sources().Should().ContainSingle(s => s.Name == KnowledgeIntake.DocumentsSource);

        var (r2, _) = await k.AddSelectedAsync("My docs", docs, new[] { f }, null, CancellationToken.None);
        r2.Added.Should().Be(0); // same content already waiting
        k.SavedSources().Should().Contain(s => s.Name == "My docs" && s.Kind == KnowledgeImporter.FilesKind);
        k.SavedSources().Should().NotContain(s => s.Name == KnowledgeIntake.DocumentsSource);
    }

    [Fact]
    public async Task CheckFiles_expands_folder_and_marks_unsupported()
    {
        var k = Intake();
        File.WriteAllText(P("data.bin"), "x");
        var loose = Path.Combine(_v.Root, "loose.txt");
        File.WriteAllText(loose, "hello");

        var files = await k.CheckFilesAsync(new[] { _v.Vault, loose }, CancellationToken.None);

        files.Where(f => f.RelPath.EndsWith(".md")).Should().HaveCount(7).And.OnlyContain(f => f.State == IntakeFileState.New);
        files.Single(f => f.RelPath == "data.bin").State.Should().Be(IntakeFileState.Unsupported);
        files.Single(f => f.FullPath == loose).RelPath.Should().Be("loose.txt");
    }

    [Fact]
    public async Task CheckForNew_missing_folder_throws_plain_message()
    {
        var k = Intake();
        await k.AddAsync(new[] { _v.Vault }, "", null, CancellationToken.None);
        var id = VaultId();
        Directory.Delete(_v.Vault, true);

        var act = () => k.CheckForNewAsync(id, CancellationToken.None);

        (await act.Should().ThrowAsync<DirectoryNotFoundException>()).WithMessage("*folder*");
    }

    [Fact]
    public async Task AddSelected_keeps_existing_links_and_links_nothing_to_the_new_pending_note()
    {
        var k = Intake();
        await k.AddAsync(new[] { _v.Vault }, "", null, CancellationToken.None);
        k.Accept(_store.NotesByStatus("pending").Select(n => n.Id).ToList());
        var before = _store.Links();
        before.Should().NotBeEmpty();
        var gone = NoteId("Network/Zeek.md");
        File.Delete(P("Network/Zeek.md")); // a note whose file is gone keeps its links
        File.AppendAllText(P("Windows/Prefetch.md"), "\nEdited, mentions Beaconing and Windows MOC.\n");
        File.AppendAllText(P("Windows/Amcache.md"), "\nEdited but not ticked.\n");

        var (_, ids) = await k.AddSelectedAsync(null, _v.Vault, new[] { P("Windows/Prefetch.md") }, null, CancellationToken.None);

        var after = _store.Links();
        after.Should().BeEquivalentTo(before);
        after.Should().NotContain(l => l.Src == ids[0] || l.Dst == ids[0]);
        after.Should().Contain(l => l.Src == gone || l.Dst == gone);
    }

    [Fact]
    public async Task Nested_document_edit_is_Changed_and_AddSelected_replaces_it()
    {
        var k = Intake();
        var sub = Path.Combine(_v.Vault, "sub");
        Directory.CreateDirectory(sub);
        var f = Path.Combine(sub, "a.txt");
        File.WriteAllText(f, "Nested document text " + string.Join(" ", Enumerable.Range(0, 40).Select(i => "w" + i)));
        await k.AddAsync(new[] { _v.Vault }, "", null, CancellationToken.None);
        k.Accept(_store.NotesByStatus("pending").Select(n => n.Id).ToList());
        var oldId = _store.NotesOf(VaultId())["a.txt"].Id;
        File.AppendAllText(f, " edited later");

        var files = await k.CheckForNewAsync(VaultId(), CancellationToken.None);
        var one = files.Single(x => x.RelPath == "sub/a.txt");
        one.State.Should().Be(IntakeFileState.Changed);
        one.ExistingNoteId.Should().Be(oldId);

        var (_, ids) = await k.AddSelectedAsync(null, _v.Vault, new[] { f }, null, CancellationToken.None);
        ids.Should().HaveCount(1);
        _store.Meta(ids[0])!.Replaces.Should().Be(oldId);
    }

    [Fact]
    public async Task Accepting_a_subset_note_adds_its_outgoing_link_and_keeps_existing_links()
    {
        var k = Intake();
        await k.AddAsync(new[] { _v.Vault }, "", null, CancellationToken.None);
        k.Accept(_store.NotesByStatus("pending").Select(n => n.Id).ToList());
        var before = _store.Links();
        File.WriteAllText(P("Network/Newone.md"), "# Newone topic\nSee [[Beaconing]] for the pattern, with enough words to be a real note here.");

        var (_, ids) = await k.AddSelectedAsync(null, _v.Vault, new[] { P("Network/Newone.md") }, null, CancellationToken.None);
        _store.Links().Should().NotContain(l => l.Src == ids[0] || l.Dst == ids[0]); // pending: no links
        k.Accept(ids);

        var beaconing = NoteId("Network/Beaconing.md");
        var after = _store.Links();
        after.Should().Contain((ids[0], beaconing, LinkKinds.Wikilink));
        after.Should().Contain(before);
    }

    [Fact]
    public async Task Accepting_a_subset_note_adds_the_incoming_link_from_an_accepted_note()
    {
        var k = Intake();
        File.WriteAllText(P("Windows/Early.md"), "# Early\nPoints at [[Late Title]] which is not there yet, with enough words to be a note.");
        await k.AddAsync(new[] { _v.Vault }, "", null, CancellationToken.None);
        k.Accept(_store.NotesByStatus("pending").Select(n => n.Id).ToList());
        var before = _store.Links();
        File.WriteAllText(P("Network/Late Title.md"), "# Late Title\nThe late note, with enough words to be a real note in the vault.");

        var (_, ids) = await k.AddSelectedAsync(null, _v.Vault, new[] { P("Network/Late Title.md") }, null, CancellationToken.None);
        _store.Links().Should().NotContain(l => l.Src == ids[0] || l.Dst == ids[0]);
        k.Accept(ids);

        var after = _store.Links();
        after.Should().Contain((NoteId("Windows/Early.md"), ids[0], LinkKinds.Wikilink));
        after.Should().Contain(before);
    }
}
