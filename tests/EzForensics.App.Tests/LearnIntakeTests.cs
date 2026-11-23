using System.IO;
using EzForensics.App.ViewModels.Learn;
using EzForensics.Core.Knowledge;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.App.Tests;

/// <summary>The Learn Intake tab over a temp knowledge base with a fake AI.</summary>
[Collection("WpfApp")]
public sealed class LearnIntakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-intake-" + Guid.NewGuid().ToString("N"));
    private readonly KnowledgeWorkspace _ws;

    public LearnIntakeTests()
    {
        Directory.CreateDirectory(_dir);
        _ws = new KnowledgeWorkspace(Path.Combine(_dir, "k.db"), () => null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Reply = "[{\"index\":0,\"summary\":\"About prefetch.\",\"category\":\"Windows artifacts\",\"type\":\"Reference\",\"tags\":[\"prefetch\"]}]";

    private static KnowledgeCompletion Fake(string reply = Reply) => (_, _) => Task.FromResult(reply);

    private IntakeViewModel NewIntake(KnowledgeCompletion? ai, bool saveSource = false) =>
        new(_ws, () => (ai, "fake-model")) { ConfirmSaveSource = _ => saveSource };

    private string Doc(string name, string text, string? sub = null)
    {
        var dir = sub == null ? _dir : Path.Combine(_dir, sub);
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private const string Prefetch = "Prefetch files record program execution evidence for the examiner. They show when a program first ran and how often.";
    private const string Shellbags = "Shellbags record folders the user browsed, even after the folder is deleted. Useful for finding removable media use.";

    private T Read<T>(Func<KnowledgeStore, T> f) { using var s = new KnowledgeStore(_ws.DbPath); return f(s); }

    [Fact]
    public async Task SendToReview_disabled_without_ai_and_shows_hint()
    {
        var vm = NewIntake(null);
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });

        vm.Rows.Should().ContainSingle().Which.IsSelected.Should().BeTrue();
        vm.HasAi.Should().BeFalse();
        vm.AiHint.Should().Be("Set up local AI… (Tools → AI)");
        vm.SendToReviewCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CheckForNew_lists_new_and_greys_already_in()
    {
        var vault = Path.Combine(_dir, "vault");
        Doc("one.md", "# One\n\n" + Prefetch + new string('a', 200), "vault");
        var vm = NewIntake(Fake(), saveSource: true);
        await vm.AddPathsAsync(new[] { vault });
        vm.Rows.Should().ContainSingle();
        await vm.SendToReviewCommand.ExecuteAsync(null);
        vm.Rows.Should().BeEmpty();

        Doc("two.md", "# Two\n\n" + Shellbags + new string('b', 200), "vault");
        await vm.OnShownAsync();
        vm.Sources.Should().ContainSingle();
        await vm.CheckForNewCommand.ExecuteAsync(null);

        vm.Rows.Should().HaveCount(2);
        vm.Rows.Single(r => r.Title == "Two").IsSelectable.Should().BeTrue();
        var old = vm.Rows.Single(r => r.Title == "One");
        old.IsSelectable.Should().BeFalse();
        old.IsSelected.Should().BeFalse();
        old.StateText.Should().Be("Already in");
    }

    [Fact]
    public async Task Send_removes_sent_rows_and_labels_notes()
    {
        var vm = NewIntake(Fake());
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        vm.SendToReviewCommand.CanExecute(null).Should().BeTrue();

        await vm.SendToReviewCommand.ExecuteAsync(null);

        vm.Rows.Should().BeEmpty();
        vm.Message.Should().StartWith("Sent 1 for review");
        var pending = Read(s => s.NotesByStatus(KnowledgeIntake.Pending));
        pending.Should().ContainSingle();
        Read(s => s.Labels(pending[0].Id)).AiState.Should().Be("labelled");
    }

    [Fact]
    public async Task Send_with_failing_ai_keeps_notes_pending_and_reports_retry()
    {
        var vm = NewIntake((_, _) => throw new InvalidOperationException("ollama is down"));
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });

        await vm.SendToReviewCommand.ExecuteAsync(null);

        vm.Rows.Should().BeEmpty();
        vm.Message.Should().Contain("couldn't be labelled, retry in To review");
        var id = Read(s => s.NotesByStatus(KnowledgeIntake.Pending)).Single().Id;
        Read(s => s.Labels(id)).AiState.Should().Be("failed");
    }

    [Fact]
    public async Task Cancel_keeps_unsent_rows()
    {
        var started = new TaskCompletionSource();
        KnowledgeCompletion blocking = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        var vault = Path.Combine(_dir, "vault");
        Doc("one.md", "# One\n\n" + Shellbags + new string('b', 200), "vault");
        var vm = NewIntake(blocking, saveSource: true);
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch), vault });   // the folder rows come first, so the loose file is the unsent one
        vm.Rows.Should().HaveCount(2);

        var send = vm.SendToReviewCommand.ExecuteAsync(null);
        await started.Task;
        vm.CancelCommand.Execute(null);
        await send;

        vm.Rows.Should().ContainSingle().Which.Title.Should().Be("a");
        vm.IsBusy.Should().BeFalse();
        vm.Message.Should().StartWith("Cancelled");
        Read(s => s.NotesByStatus(KnowledgeIntake.Pending)).Should().ContainSingle("the note already added stays pending");
    }

    [Fact]
    public async Task Dropped_rows_survive_reopen()
    {
        var a = Doc("a.txt", Prefetch);
        var first = NewIntake(Fake());
        await first.AddPathsAsync(new[] { a });

        var second = NewIntake(Fake());
        second.Rows.Should().BeEmpty();
        await second.OnShownAsync();

        second.Rows.Should().ContainSingle().Which.FullPath.Should().Be(a);
        second.WaitingCount.Should().Be(1);
    }

    [Fact]
    public async Task OnShown_rereads_ai_settings()
    {
        KnowledgeCompletion? ai = null;
        var vm = new IntakeViewModel(_ws, () => (ai, "m"));
        vm.HasAi.Should().BeFalse();
        ai = Fake();
        await vm.OnShownAsync();
        vm.HasAi.Should().BeTrue();
    }

    [Fact]
    public void Learn_tabs_are_Graph_Intake_Inbox_Library()
    {
        Enum.GetNames<LearnTab>().Should().Equal("Brain", "Intake", "Inbox", "Library");
        ((int)LearnTab.Inbox).Should().Be(2);
        ((int)LearnTab.Library).Should().Be(3);
    }

    [Fact]
    public async Task Skipped_files_stay_with_a_reason_and_are_not_counted_as_sent()
    {
        var a = Doc("a.txt", Prefetch);
        var b = Doc("b.txt", Shellbags);
        var vm = NewIntake(Fake());
        await vm.AddPathsAsync(new[] { a, b });
        vm.Rows.Should().HaveCount(2);

        // another Intake sends a first, after this list was checked
        var other = NewIntake(Fake());
        await other.AddPathsAsync(new[] { a });
        await other.SendToReviewCommand.ExecuteAsync(null);

        await vm.SendToReviewCommand.ExecuteAsync(null);

        vm.Message.Should().StartWith("Sent 1 for review").And.Contain("1 skipped");
        var left = vm.Rows.Should().ContainSingle().Subject;
        left.FullPath.Should().Be(a);
        left.Problem.Should().NotBeEmpty();
        Read(s => s.NotesByStatus(KnowledgeIntake.Pending)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Inbox_add_buttons_go_to_Intake()
    {
        var learn = new LearnViewModel(_ws, () => (Fake(), "m"));
        learn.Inbox.PickFiles = (_, _) => new[] { Doc("a.txt", Prefetch) };
        learn.SelectedTab = LearnTab.Inbox;

        await learn.Inbox.AddFilesCommand.ExecuteAsync(null);

        learn.SelectedTab.Should().Be(LearnTab.Intake);
        learn.Intake.Rows.Should().ContainSingle();
        learn.Inbox.Rows.Should().BeEmpty();
        File.Exists(_ws.DbPath).Should().BeTrue();
        Read(s => s.NotesByStatus(KnowledgeIntake.Pending)).Should().BeEmpty("nothing is added before Send to review");
    }

    [Fact]
    public async Task Ezkb_pack_can_be_added_in_Intake_sent_and_is_labelled()
    {
        var vault = Path.Combine(_dir, "packvault");
        Doc("one.md", "# One" + Environment.NewLine + Environment.NewLine + Prefetch + new string('a', 200), "packvault");
        var pack = Path.Combine(_dir, "guide.ezkb");
        EzkbPack.Write(vault, pack, "Guide", null);
        var vm = NewIntake(Fake());

        await vm.AddPathsAsync(new[] { pack });
        var row = vm.Rows.Should().ContainSingle().Subject;
        row.IsSelectable.Should().BeTrue();
        row.StateText.Should().Be("New");

        await vm.SendToReviewCommand.ExecuteAsync(null);

        vm.Rows.Should().BeEmpty();
        var pending = Read(s => s.NotesByStatus(KnowledgeIntake.Pending));
        pending.Should().ContainSingle();
        Read(s => s.Labels(pending[0].Id).AiState).Should().Be("labelled");
    }

    [Fact]
    public async Task Unreachable_local_AI_disables_Send_with_a_hint_and_recovers()
    {
        var up = false;
        var vm = new IntakeViewModel(_ws, () => (Fake(), "m"), null, () => Task.FromResult(up));
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        await vm.OnShownAsync();
        vm.HasAi.Should().BeFalse();
        vm.AiHint.Should().Be("Local AI isn't running. Start Ollama or check Tools → AI.");
        vm.SendToReviewCommand.CanExecute(null).Should().BeFalse();

        up = true;
        await vm.OnShownAsync();
        vm.HasAi.Should().BeTrue();
        vm.AiHint.Should().BeEmpty();
        vm.SendToReviewCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Send_probes_again_and_refuses_when_the_server_went_away()
    {
        var up = true;
        var vm = new IntakeViewModel(_ws, () => (Fake(), "m"), null, () => Task.FromResult(up));
        await vm.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        await vm.OnShownAsync();
        vm.SendToReviewCommand.CanExecute(null).Should().BeTrue();
        up = false;
        await vm.SendToReviewCommand.ExecuteAsync(null);
        vm.Message.Should().Be(KnowledgeAiGate.UnreachableHint);
        vm.Rows.Should().ContainSingle();
        Read(s => s.NotesByStatus(KnowledgeIntake.Pending)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "http://localhost:11434")]
    [InlineData("http://box:11434/v1/", "http://box:11434")]
    [InlineData("http://box:11434", "http://box:11434")]
    public void Ollama_probe_root_comes_from_the_base_url(string? baseUrl, string root) =>
        EzForensics.App.Services.KnowledgeAi.OllamaRoot(baseUrl).Should().Be(root);

    [Fact]
    public async Task One_Learn_job_at_a_time_across_Intake_Inbox_and_Library()
    {
        var gate = new TaskCompletionSource<string>();
        var learn = new LearnViewModel(_ws, () => ((_, _) => gate.Task, "m"));
        await learn.Intake.AddPathsAsync(new[] { Doc("a.txt", Prefetch) });
        await learn.Inbox.AddPathsAsync(new[] { Doc("b.txt", Shellbags) });
        await learn.Inbox.RefreshAsync();
        learn.Inbox.SetSelected(learn.Inbox.Rows.ToList());
        await learn.Library.RefreshAsync();
        learn.Inbox.LabelWithAiCommand.CanExecute(null).Should().BeTrue();
        learn.Inbox.AcceptCommand.CanExecute(null).Should().BeTrue();

        var sending = learn.Intake.SendToReviewCommand.ExecuteAsync(null);   // blocks in the AI call
        learn.Intake.IsBusy.Should().BeTrue();

        learn.Inbox.LabelWithAiCommand.CanExecute(null).Should().BeFalse();
        learn.Inbox.DistillCommand.CanExecute(null).Should().BeFalse();
        learn.Inbox.AcceptCommand.CanExecute(null).Should().BeFalse();
        learn.Inbox.AddFilesCommand.CanExecute(null).Should().BeFalse();
        learn.Intake.AddFilesCommand.CanExecute(null).Should().BeFalse();
        foreach (var row in learn.Library.Sources)
        {
            learn.Library.ReindexCommand.CanExecute(row).Should().BeFalse();
            learn.Library.RemoveCommand.CanExecute(row).Should().BeFalse();
        }
        learn.Library.EmptyRejectedCommand.CanExecute(null).Should().BeFalse();

        gate.SetResult("[]");
        await sending;
        learn.Intake.IsBusy.Should().BeFalse();
        learn.Workspace.IsBusy.Should().BeFalse();
        learn.Inbox.AcceptCommand.CanExecute(null).Should().BeTrue();
        learn.Inbox.AddFilesCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Unsent_source_rows_keep_their_source_after_a_restart()
    {
        var vault = Path.Combine(_dir, "vault");
        Doc("one.md", "# One" + Environment.NewLine + Environment.NewLine + Prefetch + new string('a', 200), "vault");
        var loose = Doc("loose.txt", Shellbags);
        var vm = NewIntake(Fake(), saveSource: true);
        await vm.AddPathsAsync(new[] { vault, loose });
        vm.Rows.Should().HaveCount(2);
        Read(s => s.Sources()).Should().ContainSingle("the source is saved when the user says yes, before anything is sent");

        // restart: a new view model over the same knowledge base
        var again = NewIntake(Fake());
        await again.OnShownAsync();

        again.Sources.Should().ContainSingle().Which.Name.Should().Be("vault");
        again.Rows.Should().HaveCount(2);
        again.Rows.Single(r => r.FullPath.EndsWith("one.md")).Origin.SavedSourceId.Should().Be(again.Sources[0].Id);
        again.Rows.Single(r => r.FullPath.EndsWith("loose.txt")).Origin.SavedSourceId.Should().BeNull();

        await again.SendToReviewCommand.ExecuteAsync(null);
        Read(s => s.Sources().Select(x => x.Name).OrderBy(n => n).ToList()).Should().Equal("Documents", "vault");
        Read(s => s.NotesByStatus(KnowledgeIntake.Pending)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failing_file_marks_only_its_row_and_earlier_notes_are_labelled_as_they_go()
    {
        var paths = Enumerable.Range(0, 8).Select(i => Doc($"f{i}.txt", $"Document {i}: " + Prefetch + $" unique{i}")).ToList();
        var labelledAtSeventh = -1;
        var reply = "[" + string.Join(",", Enumerable.Range(0, 6).Select(i => $"{{\"index\":{i},\"summary\":\"s\",\"category\":\"Memory\",\"type\":\"Reference\",\"tags\":[]}}")) + "]";
        var vm = NewIntake(Fake(reply));
        await vm.AddPathsAsync(paths);
        vm.AddProbe = p =>
        {
            if (p.EndsWith("f6.txt")) labelledAtSeventh = Read(s => s.NotesByStatus(KnowledgeIntake.Pending).Count(n => s.Labels(n.Id).AiState == "labelled"));
            if (p.EndsWith("f7.txt")) throw new UnauthorizedAccessException("access denied");
        };

        await vm.SendToReviewCommand.ExecuteAsync(null);

        labelledAtSeventh.Should().Be(6, "the first six notes are labelled before the seventh file is read");
        var left = vm.Rows.Should().ContainSingle().Subject;
        left.FullPath.Should().EndWith("f7.txt");
        left.Problem.Should().Be("access denied");
        Read(s => s.NotesByStatus(KnowledgeIntake.Pending)).Should().HaveCount(7);
    }

    [Fact]
    public async Task Check_for_new_can_be_cancelled_and_keeps_the_list()
    {
        var vault = Path.Combine(_dir, "vault");
        Doc("one.md", "# One" + Environment.NewLine + Environment.NewLine + Prefetch + new string('a', 200), "vault");
        var vm = NewIntake(Fake(), saveSource: true);
        await vm.AddPathsAsync(new[] { vault });
        await vm.OnShownAsync();
        var before = vm.Rows.Count;
        Doc("two.md", "# Two" + Environment.NewLine + Environment.NewLine + Shellbags + new string('b', 200), "vault");
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IntakeViewModel.IsBusy) && vm.IsBusy) vm.CancelCommand.Execute(null); };

        await vm.CheckForNewCommand.ExecuteAsync(null);

        vm.Message.Should().StartWith("Cancelled");
        vm.Rows.Should().HaveCount(before, "a cancelled check leaves the list as it was");
        vm.IsBusy.Should().BeFalse();
    }
}
