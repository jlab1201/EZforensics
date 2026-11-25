using System.IO;
using EzForensics.App.ViewModels;
using EzForensics.App.ViewModels.Learn;
using EzForensics.Core.Knowledge;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.App.Tests;

/// <summary>The Learn page's Inbox and Library over a temp knowledge base (no embedder, no dialogs).</summary>
[Collection("WpfApp")]
public sealed class LearnInboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-learn-" + Guid.NewGuid().ToString("N"));
    private readonly KnowledgeWorkspace _ws;

    public LearnInboxTests()
    {
        Directory.CreateDirectory(_dir);
        _ws = new KnowledgeWorkspace(Path.Combine(_dir, "k.db"), () => null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Doc(string name, string text)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private const string Prefetch = "Prefetch files record program execution evidence for the examiner. They show when a program first ran and how often.";
    private const string Shellbags = "Shellbags record folders the user browsed, even after the folder is deleted. Useful for finding removable media use.";

    private InboxViewModel NewInbox(Func<(KnowledgeCompletion? Complete, string Model)>? ai = null) =>
        new(_ws, null, ai ?? (() => (null, ""))) { ConfirmRejectAccepted = _ => true, ConfirmReplaceChildren = _ => true, ConfirmDistill = _ => true };

    private static async Task RejectSelected(InboxViewModel vm)
    {
        vm.RejectCommand.Execute(null);
        await vm.ConfirmRejectCommand.ExecuteAsync(null);
    }

    private LibraryViewModel NewLibrary() => new(_ws, _ => Task.CompletedTask) { ConfirmEmptyRejected = _ => true, ConfirmRemove = _ => true };

    private T Read<T>(Func<KnowledgeStore, T> f) { using var s = new KnowledgeStore(_ws.DbPath); return f(s); }

    [Fact]
    public void Nav_HasLearnAfterReview()
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            var prev = Services.AppServices.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                var s = new Services.AppServices(new Core.Settings.JsonSettingsStore(Path.Combine(_dir, "settings.json")), new Core.Settings.AppSettings());
                Services.AppServices.Current = s;
                var shell = new ShellViewModel(s);
                var titles = shell.MainItems.Select(i => i.Title).ToList();
                titles.Should().Equal(Services.PageNames.Ingest, "Analyze", Services.PageNames.Explore, Services.PageNames.Findings);
                shell.FooterItems.Select(i => i.Title).Should().Equal(Services.PageNames.Learn, "Tools", "Options");
                Services.PageNames.Learn.Should().Be("AI");
                shell.Items.Should().Contain(i => i.Page is LearnViewModel);
            }
            catch (Exception ex) { failure = ex; }
            finally { Services.AppServices.Current = prev; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("STA test failed", failure);
    }

    [Fact]
    public async Task Inbox_AddFile_ShowsPendingRow()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.SourceName.Should().Be(InboxViewModel.DefaultSourceName);
        vm.Message.Should().Contain("Added 1");
        Read(s => s.SearchChunks("prefetch", 10)).Should().BeEmpty("pending notes are not searchable");
    }

    [Fact]
    public async Task Inbox_AddSameFileAgainAfterReject_SaysRejectedBefore()
    {
        var vm = NewInbox();
        var p = Doc("a.txt", Prefetch);
        await vm.AddPathsAsync(new[] { p });
        vm.SetSelected(vm.Rows.ToList());
        await RejectSelected(vm);

        await vm.AddPathsAsync(new[] { p });

        vm.Rows.Should().BeEmpty();
        vm.Message.Should().Contain("Rejected before");
        vm.Rejected.Should().ContainSingle();
    }

    [Fact]
    public async Task Inbox_Accept_RowLeaves_NoteSearchable()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        vm.SetSelected(vm.Rows.ToList());
        vm.HasSelection.Should().BeTrue();

        await vm.AcceptCommand.ExecuteAsync(null);

        vm.Rows.Should().BeEmpty();
        vm.HasSelection.Should().BeFalse();
        Read(s => s.SearchChunks("prefetch", 10)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Bulk_reject_needs_inline_confirm()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        vm.Rows.Should().HaveCount(2);
        vm.SetSelected(vm.Rows.ToList());

        vm.RejectCommand.Execute(null);

        vm.IsConfirmingReject.Should().BeTrue();
        vm.RejectAsk.Should().Be("Reject 2?");
        vm.Rows.Should().HaveCount(2, "nothing is rejected before yes");
        vm.CancelRejectCommand.Execute(null);
        vm.IsConfirmingReject.Should().BeFalse();
        vm.Rows.Should().HaveCount(2);

        await RejectSelected(vm);
        vm.Rows.Should().BeEmpty();
        vm.Rejected.Should().HaveCount(2);
    }

    [Fact]
    public async Task Failed_accept_rows_stay_selected_with_message()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        var bad = vm.Rows[1].Id;
        vm.ReviewProbe = id => { if (id == bad) throw new InvalidOperationException("boom"); };
        vm.SetSelected(vm.Rows.ToList());

        await vm.AcceptCommand.ExecuteAsync(null);

        vm.Message.Should().Be("1 of 2 failed — boom");
        vm.Rows.Should().ContainSingle().Which.Id.Should().Be(bad);
        vm.SelectedRows.Should().ContainSingle().Which.Id.Should().Be(bad);
        Read(s => s.NotesByStatus("accepted")).Should().ContainSingle();
    }

    [Fact]
    public async Task Bulk_accept_goes_through_in_one_batch_and_blocks_other_commands_meanwhile()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags), Doc("c.txt", "Amcache records installed programs with their SHA1 hashes, useful for proving a file existed on the machine.") });
        var batches = new List<IReadOnlyList<long>>();
        var busyDuring = false;
        vm.BulkProbe = ids => { batches.Add(ids); busyDuring = vm.IsBusy; };
        vm.SetSelected(vm.Rows.ToList());

        await vm.AcceptCommand.ExecuteAsync(null);

        batches.Should().ContainSingle().Which.Should().HaveCount(3);
        busyDuring.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
        Read(s => s.NotesByStatus("accepted")).Should().HaveCount(3);
        vm.Message.Should().Be("Accepted 3.");
    }

    [Fact]
    public async Task AI_and_Distilled_filters_pick_labelled_failed_unlabelled_and_distilled_rows()
    {
        var reply = "[{\"index\":0,\"summary\":\"s\",\"category\":\"Memory\",\"type\":\"Reference\",\"tags\":[]}]";
        var vm = NewInbox(() => ((_, _) => Task.FromResult(reply), "m"));
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags), Doc("c.txt", "Amcache keeps program execution traces with hashes of the binaries that ran.") });
        var (a, b, c) = (vm.Rows[0], vm.Rows[1], vm.Rows[2]);
        a.NotLabelled.Should().BeTrue();
        vm.SetSelected(new[] { a });
        await vm.LabelWithAiCommand.ExecuteAsync(null);
        Read(s => s.Labels(a.Id).AiState).Should().Be("labelled");
        using (var st = new KnowledgeStore(_ws.DbPath)) { st.SetAiFailed(new[] { b.Id }); st.SetParent(c.Id, a.Id); }
        await vm.RefreshAsync();

        vm.FacetValues("AI").Should().Equal("Failed", "Labelled", "Not labelled");
        vm.FacetValues("Distilled").Should().Equal(InboxViewModel.DistilledNotes, InboxViewModel.WholeDocuments);

        vm.AddFilter("AI", "Failed");
        vm.PageRows.Should().ContainSingle().Which.HasAiFailed.Should().BeTrue();
        vm.ClearFilters();
        vm.AddFilter("AI", "Labelled");
        vm.PageRows.Should().ContainSingle().Which.Id.Should().Be(a.Id);
        vm.ClearFilters();
        vm.AddFilter("Distilled", InboxViewModel.DistilledNotes);
        vm.PageRows.Should().ContainSingle().Which.Id.Should().Be(c.Id);
        vm.PageRows[0].NotLabelled.Should().BeTrue();
    }

    [Fact]
    public async Task Filter_change_clears_selection_and_resets_page()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        vm.PageSize = 1;
        vm.Page = 2;
        vm.PageRows.Should().ContainSingle();
        vm.SetSelected(vm.PageRows.ToList());
        vm.HasSelection.Should().BeTrue();

        vm.AddFilter("Source", InboxViewModel.DefaultSourceName);

        vm.HasSelection.Should().BeFalse();
        vm.Page.Should().Be(1);

        vm.Page = 2;
        vm.SetSelected(vm.PageRows.ToList());
        vm.Search = "prefetch";
        vm.HasSelection.Should().BeFalse();
        vm.Page.Should().Be(1);
        vm.ClearFiltersCommand.Execute(null);
        vm.Filters.Should().BeEmpty();
    }

    [Fact]
    public async Task Facet_values_come_from_present_rows()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        var first = vm.Rows[0].Id;
        using (var st = new KnowledgeStore(_ws.DbPath))
            st.SetLabels(first, "About prefetch", "Windows artifacts", "Reference", new[] { "windows", "execution" }, "m", DateTime.UtcNow.ToString("o"));
        await vm.RefreshAsync();

        vm.FacetValues("Category").Should().Equal("Windows artifacts");
        vm.FacetValues("Type").Should().Equal("Reference");
        vm.FacetValues("Tag").Should().Equal("execution", "windows");
        vm.FacetValues("Source").Should().Equal(InboxViewModel.DefaultSourceName);
        vm.FacetValues("Flag").Should().Equal("Little text");
    }

    [Fact]
    public async Task Search_matches_summary()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        var first = vm.Rows[0].Id;
        using (var st = new KnowledgeStore(_ws.DbPath))
            st.SetLabels(first, "Timeline of program starts", "Windows artifacts", "Reference", Array.Empty<string>(), "m", DateTime.UtcNow.ToString("o"));
        await vm.RefreshAsync();

        vm.Search = "timeline";
        vm.PageRows.Should().ContainSingle().Which.Id.Should().Be(first);

        vm.Search = "removable media";   // only in the body of the other document
        await vm.PendingBodies;
        vm.PageRows.Should().ContainSingle().Which.Id.Should().NotBe(first);

        vm.Search = "nothing like this";
        await vm.PendingBodies;
        vm.PageRows.Should().BeEmpty();
        vm.NoMatches.Should().BeTrue();
    }

    [Fact]
    public async Task Bulk_set_category_saves_every_selected_row()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        vm.SetSelected(vm.Rows.ToList());

        vm.SetCategoryCommand.Execute("memory");
        vm.SetTypeCommand.Execute("Template / checklist");

        Read(s => vm.Rows.Select(r => (s.Labels(r.Id).Category, s.Labels(r.Id).Type)).ToList())
            .Should().OnlyContain(x => x.Category == "Memory" && x.Type == "Template / checklist");
        vm.Rows.Should().OnlyContain(r => r.Category == "Memory");
        // a row chip saves just that row
        vm.Rows[0].SetCategoryCommand.Execute("Network");
        Read(s => s.Labels(vm.Rows[0].Id).Category).Should().Be("Network");
        Read(s => s.Labels(vm.Rows[1].Id).Category).Should().Be("Memory");
    }

    [Fact]
    public async Task Label_with_ai_disabled_without_ai()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        vm.SetSelected(vm.Rows.ToList());

        vm.HasAi.Should().BeFalse();
        vm.AiHint.Should().Be("Use local AI (Tools → AI)");
        vm.LabelWithAiCommand.CanExecute(null).Should().BeFalse();
        vm.DistillCommand.CanExecute(null).Should().BeFalse();
        vm.RetryAiCommand.CanExecute(vm.Rows[0]).Should().BeFalse();

        var on = NewInbox(() => ((_, _) => Task.FromResult("[]"), "m"));
        on.SetSelected(Array.Empty<InboxRow>());
        on.HasAi.Should().BeTrue();
        on.LabelWithAiCommand.CanExecute(null).Should().BeFalse("nothing is selected");
    }

    [Fact]
    public async Task Distill_asks_before_replacing_pending_children()
    {
        const string reply = "[{\"title\":\"Prefetch basics\",\"body\":\"Prefetch records program execution.\",\"category\":\"Windows artifacts\",\"type\":\"Reference\",\"tags\":[\"prefetch\"]}]";
        var vm = NewInbox(() => ((_, _) => Task.FromResult(reply), "m"));
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        var parent = vm.Rows[0];
        vm.SetSelected(new[] { parent });
        var asked = new List<string>();
        vm.ConfirmReplaceChildren = m => { asked.Add(m); return false; };

        await vm.DistillCommand.ExecuteAsync(null);
        asked.Should().BeEmpty("no children yet");
        vm.Message.Should().Be("Distilled 1 note from 1 document.");
        var kids = Read(s => s.Children(parent.Id));
        kids.Should().ContainSingle();
        vm.Rows.Should().HaveCount(2);
        vm.Rows.Single(r => r.Id == kids[0]).ParentTitle.Should().Be(parent.Title);

        vm.SetSelected(new[] { vm.Rows.Single(r => r.Id == parent.Id) });
        await vm.DistillCommand.ExecuteAsync(null);
        asked.Should().ContainSingle().Which.Should().Be("Replace 1 pending note from 1 document?");
        Read(s => s.Children(parent.Id)).Should().Equal(kids, "declined: nothing replaced");

        vm.ConfirmReplaceChildren = _ => true;
        vm.SetSelected(new[] { vm.Rows.Single(r => r.Id == parent.Id) });
        await vm.DistillCommand.ExecuteAsync(null);
        var after = Read(s => s.Children(parent.Id));
        after.Should().ContainSingle("the pending child was replaced, not added to");
    }

    [Fact]
    public async Task Paging_range_text()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags), Doc("c.txt", "Amcache records executables that ran on the system.") });
        vm.PageSize.Should().Be(25);
        vm.RangeText.Should().Be("1–3 of 3");

        vm.PageSize = 2;
        vm.PageCount.Should().Be(2);
        vm.RangeText.Should().Be("1–2 of 3");
        vm.NextPageCommand.Execute(null);
        vm.RangeText.Should().Be("3–3 of 3");
        vm.PageRows.Should().ContainSingle();

        vm.Search = "prefetch";
        await vm.PendingBodies;
        vm.RangeText.Should().Be("1–1 of 1 (filtered from 3)");
        vm.Search = "";
        vm.PageSize = 0;
        vm.RangeText.Should().Be("1–3 of 3");
    }

    [Fact]
    public async Task Inbox_EditTitleAndTags_ArePersisted()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        var row = vm.Rows[0];

        row.Title = "Prefetch evidence";
        row.Tags = "windows, execution";

        var n = Read(s => s.Note(row.Id))!.Value;
        n.Title.Should().Be("Prefetch evidence");
        n.Tags.Should().Be("windows,execution");

        vm.SetSelected(new[] { row });
        vm.BulkTags = "dfir";
        vm.SetTagsCommand.Execute(null);
        Read(s => s.Note(row.Id))!.Value.Tags.Should().Be("dfir");
        row.Tags.Should().Be("dfir");
    }

    [Fact]
    public async Task Inbox_Duplicate_ShowsFlagText_AndKeepThisRejectsTheOther()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        vm.SetSelected(vm.Rows.ToList());
        await vm.AcceptCommand.ExecuteAsync(null);
        await vm.AddPathsAsync(new[] { Doc("copy.txt", Prefetch + "\n\n\n\n\n\n\n") });   // same text, other bytes

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.FlagTexts.Should().ContainSingle(t => t.StartsWith("Same as "));
        row.OtherId.Should().NotBeNull();
        var asked = 0;
        vm.ConfirmRejectAccepted = _ => { asked++; return true; };

        await vm.KeepThisCommand.ExecuteAsync(row);

        asked.Should().Be(1, "the other document was already accepted");
        Read(s => s.Meta(row.OtherId!.Value)!.Status).Should().Be("rejected");
        Read(s => s.Meta(row.Id)!.Status).Should().Be("accepted");
    }

    [Fact]
    public async Task Inbox_Undo_PutsRejectedRowBack()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        vm.SetSelected(vm.Rows.ToList());
        await RejectSelected(vm);

        await vm.UndoCommand.ExecuteAsync(vm.Rejected[0]);

        vm.Rejected.Should().BeEmpty();
        vm.Rows.Should().ContainSingle();
    }

    private string Vault()
    {
        var dir = Path.Combine(_dir, "My Vault");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Prefetch.md"), "# Prefetch\n" + Prefetch);
        File.WriteAllText(Path.Combine(dir, "Shellbags.md"), "# Shellbags\n" + Shellbags);
        return dir;
    }

    [Fact]
    public async Task Inbox_FolderPackAndFile_EachGetTheirOwnSource_ReindexOfDocumentsKeepsTheVault()
    {
        var vault = Vault();
        var pack = Path.Combine(_dir, "guide.ezkb");
        Directory.CreateDirectory(Path.Combine(_dir, "packsrc"));
        File.WriteAllText(Path.Combine(_dir, "packsrc", "Other.md"), "# Amcache\nAmcache records executables that ran or were present on the system.");
        EzkbPack.Write(Path.Combine(_dir, "packsrc"), pack, "Field Guide", null);
        var inbox = NewInbox();

        await inbox.AddPathsAsync(new[] { vault, pack });
        inbox.SetSelected(inbox.Rows.ToList());
        await inbox.AcceptCommand.ExecuteAsync(null);
        await inbox.AddPathsAsync(new[] { Doc("a.txt", "Loose report about lateral movement over SMB between two hosts.") });
        inbox.SetSelected(inbox.Rows.ToList());
        await inbox.AcceptCommand.ExecuteAsync(null);

        var lib = NewLibrary();
        await lib.RefreshAsync();
        lib.Sources.Select(r => (r.Name, r.Info.Kind)).Should().BeEquivalentTo(new[]
        {
            ("My Vault", "vault"), ("Field Guide", "pack"), (InboxViewModel.DefaultSourceName, KnowledgeImporter.FilesKind),
        });
        lib.Sources.Single(r => r.Name == "My Vault").Info.Path.Should().Be(vault);

        await lib.ReindexCommand.ExecuteAsync(lib.Sources.Single(r => r.Name == InboxViewModel.DefaultSourceName));

        lib.Sources.Single(r => r.Name == "My Vault").Accepted.Should().Be(2, "re-indexing the documents never touches the vault");
        Read(s => s.NotesByStatus("accepted")).Should().HaveCount(4);
    }

    [Fact]
    public async Task Library_reindex_of_a_vault_labels_the_new_pending_notes_and_needs_AI()
    {
        var vault = Vault();
        var inbox = NewInbox();
        await inbox.AddPathsAsync(new[] { vault });
        inbox.SetSelected(inbox.Rows.ToList());
        await inbox.AcceptCommand.ExecuteAsync(null);
        File.WriteAllText(Path.Combine(vault, "Prefetch.md"), "# Prefetch" + Environment.NewLine + Prefetch + " Edited with more detail about run counts and timestamps.");

        var off = new LibraryViewModel(_ws, _ => Task.CompletedTask, null, () => (null, ""));
        await off.RefreshAsync();
        off.ReindexCommand.CanExecute(off.Sources.Single()).Should().BeFalse("a re-index adds pending notes, which need the AI");
        off.AiHint.Should().Be(KnowledgeAiRun.NoAiHint);

        var reply = "[{\"index\":0,\"summary\":\"About prefetch.\",\"category\":\"Windows artifacts\",\"type\":\"Reference\",\"tags\":[\"prefetch\"]}]";
        var lib = new LibraryViewModel(_ws, _ => Task.CompletedTask, null, () => ((_, _) => Task.FromResult(reply), "m"));
        await lib.RefreshAsync();
        await lib.OnShownAsync();
        lib.ReindexCommand.CanExecute(lib.Sources.Single()).Should().BeTrue();
        await lib.ReindexCommand.ExecuteAsync(lib.Sources.Single());

        var pending = Read(s => s.NotesByStatus("pending"));
        pending.Should().ContainSingle();
        Read(s => s.Labels(pending[0].Id).AiState).Should().Be("labelled");
        lib.Message.Should().Contain("Labelled 1");
    }

    [Fact]
    public async Task Library_reindex_of_loose_documents_does_not_need_AI()
    {
        await AcceptTwoDocs();
        var off = new LibraryViewModel(_ws, _ => Task.CompletedTask, null, () => (null, ""));
        await off.RefreshAsync();
        off.ReindexCommand.CanExecute(off.Sources.Single()).Should().BeTrue();
    }

    [Fact]
    public async Task Library_label_and_distill_are_disabled_without_ai()
    {
        await AcceptTwoDocs();
        var off = new LibraryViewModel(_ws, _ => Task.CompletedTask, null, () => (null, ""));
        await off.RefreshAsync();
        off.HasAi.Should().BeFalse();
        off.AiHint.Should().Be("Use local AI (Tools → AI)");
        off.DistillCommand.CanExecute(off.Sources[0]).Should().BeFalse();
        off.LabelAiCommand.CanExecute(off.Sources[0]).Should().BeFalse();
    }

    private const string DistillReply = "[{\"title\":\"Prefetch basics\",\"body\":\"Prefetch records program execution.\",\"category\":\"Windows artifacts\",\"type\":\"Reference\",\"tags\":[\"prefetch\"]}]";

    private async Task AcceptTwoDocs()
    {
        var inbox = NewInbox();
        await inbox.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        inbox.SetSelected(inbox.Rows.ToList());
        await inbox.AcceptCommand.ExecuteAsync(null);
    }

    private sealed class ConstEmbedder : IEmbedder
    {
        public string ModelId => "const";
        public int Dims => 4;
        public float[][] Embed(IReadOnlyList<string> texts, CancellationToken ct) => texts.Select(_ => new[] { 1f, 0f, 0f, 0f }).ToArray();
        public float[] EmbedQuery(string text, CancellationToken ct) => new[] { 1f, 0f, 0f, 0f };
    }

    [Fact]
    public async Task Inbox_and_Library_AI_actions_are_off_when_the_local_server_is_down()
    {
        var inbox = new InboxViewModel(_ws, null, () => ((_, _) => Task.FromResult("[]"), "m"), () => Task.FromResult(false));
        await inbox.RefreshAsync();
        inbox.HasAi.Should().BeFalse();
        inbox.AiHint.Should().Be(KnowledgeAiGate.UnreachableHint);
        var lib = new LibraryViewModel(_ws, _ => Task.CompletedTask, null, () => ((_, _) => Task.FromResult("[]"), "m"), () => Task.FromResult(false));
        await lib.OnShownAsync();
        lib.HasAi.Should().BeFalse();
        lib.AiHint.Should().Be(KnowledgeAiGate.UnreachableHint);
        lib.LabelTip.Should().Be(KnowledgeAiGate.UnreachableHint);
    }

    [Fact]
    public async Task Distilled_notes_get_embeddings_when_an_embedder_is_present()
    {
        await AcceptTwoDocs();
        var ws = new KnowledgeWorkspace(_ws.DbPath, () => new ConstEmbedder());
        var ids = Read(s => s.NotesByStatus("accepted").Select(n => n.Id).ToList());
        var (_, problem) = await KnowledgeAiRun.DistillAsync(ws, (_, _) => Task.FromResult(DistillReply), "m", ids.Take(1).ToList(),
            _ => true, _ => true, false, new Progress<(int, int)>(), CancellationToken.None);
        problem.Should().BeFalse();
        var kids = Read(s => s.NotesByStatus("pending").Select(n => n.Id).ToList());
        kids.Should().HaveCount(1);
        Read(s => s.Chunks(status: "pending").Where(c => kids.Contains(c.NoteId)).ToList()).Should().NotBeEmpty().And.OnlyContain(c => c.Embedding != null);
    }

    [Fact]
    public async Task Library_distill_selected_documents_asks_once_with_the_section_count()
    {
        await AcceptTwoDocs();
        var starts = new List<string>();
        var replaces = new List<string>();
        var lib = new LibraryViewModel(_ws, _ => Task.CompletedTask, null, () => ((_, _) => Task.FromResult(DistillReply), "m"))
        { ConfirmDistill = m => { starts.Add(m); return true; }, ConfirmReplaceChildren = m => { replaces.Add(m); return true; } };
        await lib.RefreshAsync();
        var row = lib.Sources[0];
        lib.DistillCommand.CanExecute(row).Should().BeFalse("no document is selected");

        await lib.LoadDocumentsAsync(row);
        row.Documents.Should().HaveCount(2);
        row.SetSelectedDocs(row.Documents);
        lib.DistillCommand.CanExecute(row).Should().BeTrue();
        await lib.DistillCommand.ExecuteAsync(row);

        starts.Should().Equal("Distill 2 documents (about 2 AI calls)?");
        replaces.Should().BeEmpty();
        Read(s => s.NotesByStatus("pending")).Should().HaveCount(2);

        // both now have pending notes: one aggregate question, not one per document
        row = lib.Sources[0];
        await lib.LoadDocumentsAsync(row);
        row.Documents.Should().OnlyContain(d => d.Children == 1);
        row.SetSelectedDocs(row.Documents);
        await lib.DistillCommand.ExecuteAsync(row);

        starts.Should().HaveCount(2);
        replaces.Should().Equal("Replace 2 pending notes from 2 documents?");
        Read(s => s.NotesByStatus("pending")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Inbox_distill_of_several_rows_asks_one_replace_question()
    {
        var vm = NewInbox(() => ((_, _) => Task.FromResult(DistillReply), "m"));
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        var parents = vm.Rows.ToList();
        var starts = new List<string>();
        var replaces = new List<string>();
        vm.ConfirmDistill = m => { starts.Add(m); return true; };
        vm.ConfirmReplaceChildren = m => { replaces.Add(m); return true; };
        vm.SetSelected(parents);
        await vm.DistillCommand.ExecuteAsync(null);
        starts.Should().Equal("Distill 2 documents (about 2 AI calls)?");

        vm.SetSelected(vm.Rows.Where(r => parents.Any(p => p.Id == r.Id)).ToList());
        await vm.DistillCommand.ExecuteAsync(null);

        replaces.Should().Equal("Replace 2 pending notes from 2 documents?");
    }

    [Fact]
    public async Task Library_label_asks_first_with_the_call_estimate()
    {
        await AcceptTwoDocs();
        var asked = new List<string>();
        var lib = new LibraryViewModel(_ws, _ => Task.CompletedTask, null, () => ((_, _) => Task.FromResult("[]"), "m")) { ConfirmLabel = m => { asked.Add(m); return false; } };
        await lib.RefreshAsync();

        await lib.LabelAiCommand.ExecuteAsync(lib.Sources[0]);

        asked.Should().Equal("Label 2 unlabelled documents (about 1 AI call)?");
        lib.Message.Should().Be("Nothing labelled.");
    }

    [Fact]
    public async Task Filter_is_applied_again_after_a_category_change()
    {
        var vm = NewInbox();
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        vm.SetSelected(vm.Rows.ToList());
        vm.SetCategoryCommand.Execute("Memory");
        vm.AddFilter("Category", "Memory");
        vm.PageRows.Should().HaveCount(2);

        vm.Rows[0].SetCategoryCommand.Execute("Network");

        vm.PageRows.Should().ContainSingle().Which.Category.Should().Be("Memory");
        vm.RangeText.Should().Be("1–1 of 1 (filtered from 2)");
    }

    [Fact]
    public async Task Inbox_and_library_run_their_ai_actions_through_the_shared_runner()
    {
        await AcceptTwoDocs();
        var inbox = NewInbox(() => ((_, _) => Task.FromResult("[]"), "m"));
        await inbox.AddPathsAsync(new[] { Doc("c.txt", "Amcache records executables that ran on the system.") });
        var lib = new LibraryViewModel(_ws, _ => Task.CompletedTask, null, () => ((_, _) => Task.FromResult("[]"), "m")) { ConfirmLabel = _ => true };
        await lib.RefreshAsync();
        var before = KnowledgeAiRun.RunCount;

        inbox.SetSelected(inbox.Rows.ToList());
        await inbox.LabelWithAiCommand.ExecuteAsync(null);
        KnowledgeAiRun.RunCount.Should().Be(before + 1);
        inbox.IsBusy.Should().BeFalse();

        await lib.LabelAiCommand.ExecuteAsync(lib.Sources[0]);
        KnowledgeAiRun.RunCount.Should().Be(before + 2);
        lib.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Library_Refresh_WithNoKnowledgeBase_DoesNotCreateOne()
    {
        var lib = NewLibrary();

        await lib.RefreshAsync();

        File.Exists(_ws.DbPath).Should().BeFalse();
        lib.NoSources.Should().BeTrue();
    }

    [Fact]
    public async Task Library_EmptyRejected_RemovesThem()
    {
        var inbox = NewInbox();
        await inbox.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        inbox.SetSelected(inbox.Rows.ToList());
        await RejectSelected(inbox);
        var lib = NewLibrary();
        await lib.RefreshAsync();
        lib.RejectedCount.Should().Be(1);

        await lib.EmptyRejectedCommand.ExecuteAsync(null);

        lib.RejectedCount.Should().Be(0);
        Read(s => s.NotesByStatus("rejected")).Should().BeEmpty();
    }

    [Fact]
    public async Task Library_ListsSourcesWithCounts_RemoveAndExport()
    {
        var inbox = NewInbox();
        await inbox.AddPathsAsync(new[] { Doc("a.txt", Prefetch), Doc("b.txt", Shellbags) });
        inbox.SetSelected(new[] { inbox.Rows[0] });
        await inbox.AcceptCommand.ExecuteAsync(null);
        var lib = NewLibrary();
        await lib.RefreshAsync();

        var src = lib.Sources.Should().ContainSingle().Subject;
        (src.Accepted, src.Pending, src.Rejected).Should().Be((1, 1, 0));

        var pack = Path.Combine(_dir, "out.ezkb");
        await lib.ExportAsync(src, pack);
        File.Exists(pack).Should().BeTrue();
        EzkbPack.ReadManifest(pack).Notes.Should().Be(1, "only accepted notes are exported");

        await lib.RemoveCommand.ExecuteAsync(src);
        lib.Sources.Should().BeEmpty();
    }

    [Fact]
    public async Task Learn_InboxAccept_RefreshesLibraryCounts()
    {
        var learn = new LearnViewModel(_ws);
        learn.SelectedTab = LearnTab.Inbox;
        await learn.Inbox.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        learn.Inbox.SetSelected(learn.Inbox.Rows.ToList());
        await RejectSelected(learn.Inbox);
        await learn.PendingRefresh;

        learn.Library.RejectedCount.Should().Be(1);
        learn.Library.Sources.Should().ContainSingle().Which.Rejected.Should().Be(1);
    }

    [Fact]
    public async Task Learn_LibraryEmptyRejected_RefreshesInbox_AndUndoOfGoneNoteIsNoOp()
    {
        var learn = new LearnViewModel(_ws);
        learn.Library.ConfirmEmptyRejected = _ => true;
        await learn.Inbox.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        learn.Inbox.SetSelected(learn.Inbox.Rows.ToList());
        await RejectSelected(learn.Inbox);
        var stale = learn.Inbox.Rejected[0];
        learn.SelectedTab = LearnTab.Library;
        await learn.PendingRefresh;

        await learn.Library.EmptyRejectedCommand.ExecuteAsync(null);
        await learn.PendingRefresh;

        learn.Inbox.Rejected.Should().BeEmpty();
        await learn.Inbox.UndoCommand.ExecuteAsync(stale);
        learn.Inbox.Message.Should().Contain("gone");
        learn.Inbox.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Inbox_Busy_DisablesAcceptAndReject()
    {
        var vm = NewInbox();
        vm.SetSelected(new[] { new InboxRow(new KnowledgeStore.NoteMeta(1, 1, "t", "p", "h", "pending", null, null, new List<string>(), null, null), "s", "t", "", new List<string>(), null, "", "") });
        vm.AcceptCommand.CanExecute(null).Should().BeTrue();
        var changed = new List<string>();
        vm.AcceptCommand.CanExecuteChanged += (_, _) => changed.Add("accept");
        vm.RejectCommand.CanExecuteChanged += (_, _) => changed.Add("reject");

        vm.IsBusy = true;

        changed.Should().Contain(new[] { "accept", "reject" });
        vm.AcceptCommand.CanExecute(null).Should().BeFalse();
        vm.RejectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ToolsAi_HasManageInLearnLink_NoDocumentList()
    {
        var vm = new AiDocumentsViewModel(() => false, (_, _) => { });
        vm.ManageInLearnCommand.Should().NotBeNull();
        vm.UseLocalAiCommand.Should().NotBeNull();
        typeof(AiDocumentsViewModel).GetProperty("Sources").Should().BeNull();
        typeof(AiDocumentsViewModel).GetMethod("AddPathsAsync").Should().BeNull();
    }
}
