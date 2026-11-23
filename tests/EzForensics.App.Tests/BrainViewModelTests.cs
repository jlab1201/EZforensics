using System.IO;
using System.Text.Json;
using EzForensics.App.ViewModels.Learn;
using EzForensics.Core.Knowledge;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.App.Tests;

/// <summary>The Brain tab's view model over a temp knowledge base: graph messages, the side panel, Ask Spectre and the layout.</summary>
[Collection("WpfApp")]
public sealed class BrainViewModelTests : IDisposable
{
    private const string Page = "https://brain.local/index.html";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-brain-" + Guid.NewGuid().ToString("N"));
    private readonly KnowledgeWorkspace _ws;
    private readonly List<string> _posted = new();
    private readonly List<(string Draft, IReadOnlyList<long> Ids)> _asked = new();
    private int _intakeShown;

    public BrainViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _ws = new KnowledgeWorkspace(Path.Combine(_dir, "k.db"), () => null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private BrainViewModel NewBrain()
    {
        var vm = new BrainViewModel(_ws, (d, ids) => _asked.Add((d, ids)), showIntake: () => _intakeShown++);
        vm.PostToPage += _posted.Add;
        return vm;
    }

    private T Store<T>(Func<KnowledgeStore, T> f) { using var s = new KnowledgeStore(_ws.DbPath); return f(s); }

    /// <summary>Two documents in "vault-a": A mentions T1059.001 once, B five times and links to A.</summary>
    private (long A, long B) Fixture() => Store(s =>
    {
        var src = s.UpsertSource("vault-a", "vault", "p");
        long Doc(string title, string body) => s.InsertNote(src,
            new VaultNote(title + ".md", title, "t", Array.Empty<string>(), "h-" + title, body, Array.Empty<VaultLink>()),
            new[] { new ChunkPiece(0, "", body, body) }, null);
        var a = Doc("Prefetch notes", "Prefetch shows program execution. " + new string('x', 900));
        var b = Doc("PowerShell hunting", "PowerShell logging and script block events.");
        var t = s.UpsertConcept("technique", "T1059.001", "T1059.001");
        s.ReplaceNoteConcepts(a, new Dictionary<long, int> { [t] = 1 });
        s.ReplaceNoteConcepts(b, new Dictionary<long, int> { [t] = 5 });
        s.ReplaceLinks(src, new[] { (b, a, "wikilink") });
        return (a, b);
    });

    private static string Type(string json) => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString()!;

    [Fact]
    public async Task Brain_EmptyStore_ShowsEmptyState()
    {
        var vm = NewBrain();
        await vm.RefreshAsync();

        vm.IsEmpty.Should().BeTrue();
        vm.EmptyText.Should().Be("Nothing in the graph yet. Add documents in Intake.");
        vm.ShowIntakeCommand.Execute(null);
        _intakeShown.Should().Be(1);
        File.Exists(_ws.DbPath).Should().BeFalse("looking at an empty brain must not create the database");
    }

    [Fact]
    public async Task Brain_Refresh_PostsGraph()
    {
        Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();

        vm.IsEmpty.Should().BeFalse();
        var graph = _posted.Select(p => JsonDocument.Parse(p)).Last(d => d.RootElement.GetProperty("type").GetString() == "graph");
        graph.RootElement.GetProperty("data").GetProperty("nodes").GetArrayLength().Should().Be(3);
        _posted.Should().NotContain(p => Type(p) == "filter", "the Brain has no filters any more");
    }

    [Fact]
    public async Task Brain_Select_DocNode_PanelHasPreviewAndConnections()
    {
        var (a, b) = Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();

        await vm.HandlePageMessageAsync(Page, $$"""{"type":"select","key":"n:{{a}}"}""");

        var sel = vm.Selected!;
        sel.Title.Should().Be("Prefetch notes");
        sel.KindOrSource.Should().Be("vault-a");
        sel.Preview.Should().StartWith("Prefetch shows program execution.");
        sel.Preview.Length.Should().BeLessThanOrEqualTo(601);
        sel.Connections.Should().Contain(c => c.Key == "c:technique:T1059.001" && c.Reason == "mentions");
        sel.Connections.Should().Contain(c => c.Key == $"n:{b}" && c.Reason == "links to");
    }

    [Fact]
    public async Task Details_show_summary_category_type()
    {
        var (a, b) = Fixture();
        Store(s => { s.SetLabels(a, "How prefetch proves execution.", "Windows artifacts", "Reference", Array.Empty<string>(), "m", "t"); return 0; });
        var vm = NewBrain();
        await vm.RefreshAsync();

        await vm.HandlePageMessageAsync(Page, $$"""{"type":"select","key":"n:{{a}}"}""");
        var sel = vm.Selected!;
        sel.Summary.Should().Be("How prefetch proves execution.");
        sel.Category.Should().Be("Windows artifacts");
        sel.Type.Should().Be("Reference");
        sel.HasSummary.Should().BeTrue();

        await vm.HandlePageMessageAsync(Page, $$"""{"type":"select","key":"n:{{b}}"}""");
        vm.Selected!.HasSummary.Should().BeFalse();
        vm.Selected.HasCategory.Should().BeFalse();
        vm.Selected.HasType.Should().BeFalse();
    }

    [Fact]
    public async Task Brain_Select_ConceptNode_ListsDocsBestFirst()
    {
        var (a, b) = Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();

        await vm.HandlePageMessageAsync(Page, """{"type":"select","key":"c:technique:T1059.001"}""");

        var sel = vm.Selected!;
        sel.Title.Should().Be("T1059.001");
        sel.KindOrSource.Should().Be("ATT&CK technique");
        sel.Preview.Should().Be("Command and Scripting Interpreter: PowerShell");
        sel.Connections.Select(c => c.Key).Should().Equal($"n:{b}", $"n:{a}");
        sel.Connections.Should().OnlyContain(c => c.Reason == "mentions");
    }

    [Fact]
    public async Task Brain_AskSpectre_CallsAskAboutWithNoteIds()
    {
        var (a, b) = Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();
        vm.AskSpectreCommand.CanExecute(null).Should().BeFalse();

        await vm.HandlePageMessageAsync(Page, """{"type":"select","key":"c:technique:T1059.001"}""");
        vm.AskSpectreCommand.Execute(null);

        var asked = _asked.Should().ContainSingle().Subject;
        asked.Draft.Should().Be("Tell me about T1059.001");
        asked.Ids.Should().Equal(b, a);

        await vm.HandlePageMessageAsync(Page, $$"""{"type":"select","key":"n:{{a}}"}""");
        vm.AskSpectreCommand.Execute(null);
        _asked[1].Draft.Should().Be("Tell me about Prefetch notes");
        _asked[1].Ids.Should().Equal(a);
    }

    [Fact]
    public async Task Brain_LayoutMessage_Saved()
    {
        var (a, _) = Fixture();
        var vm = NewBrain();
        var changed = 0;
        _ws.Changed += () => changed++;
        await vm.RefreshAsync();

        await vm.HandlePageMessageAsync(Page, $$$"""{"type":"layout","positions":{"n:{{{a}}}":[12.5,-3,4.25],"c:technique:T1059.001":[0,0,0],"c:bogus":[1,2,3]}}""");

        var layout = Store(s => s.Layout());
        layout[$"n:{a}"].Should().Be((12.5, -3.0, (double?)4.25));
        layout.Should().ContainKey("c:technique:T1059.001");
        layout.Should().NotContainKey("c:bogus", "only nodes of the current graph are saved");
        changed.Should().Be(0, "saving the layout must not make the Learn tabs rebuild");
    }

    [Fact]
    public async Task Brain_ResetLayout_ClearsSavedPositions_AndResendsGraph()
    {
        var (a, _) = Fixture();
        Store(s => { s.SaveLayout(new Dictionary<string, (double, double)> { [$"n:{a}"] = (1, 1) }); return 0; });
        var vm = NewBrain();
        await vm.RefreshAsync();
        _posted.Clear();

        await vm.ResetLayoutCommand.ExecuteAsync(null);

        Store(s => s.Layout()).Should().BeEmpty();
        var graph = JsonDocument.Parse(_posted.Single(p => Type(p) == "graph")).RootElement;
        graph.GetProperty("reset").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Brain_IgnoresMessagesFromOtherOrigins()
    {
        var (a, _) = Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();

        await vm.HandlePageMessageAsync("https://evil.example/index.html", $$"""{"type":"select","key":"n:{{a}}"}""");
        await vm.HandlePageMessageAsync("http://brain.local/index.html", $$"""{"type":"select","key":"n:{{a}}"}""");
        await vm.HandlePageMessageAsync("https://brain.local.evil.example/", $$$"""{"type":"layout","positions":{"n:{{{a}}}":[1,1]}}""");
        await vm.HandlePageMessageAsync(null, $$"""{"type":"select","key":"n:{{a}}"}""");

        vm.Selected.Should().BeNull();
        Store(s => s.Layout()).Should().BeEmpty();
    }

    [Fact]
    public async Task Brain_Search_FocusesFirstLabelMatch()
    {
        Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();
        _posted.Clear();

        vm.Search("powershell");

        var focus = JsonDocument.Parse(_posted.Single()).RootElement;
        focus.GetProperty("type").GetString().Should().Be("focus");
        focus.GetProperty("key").GetString().Should().StartWith("n:");
        vm.Selected!.Title.Should().Be("PowerShell hunting");
    }

    [Fact]
    public async Task Brain_PageReady_ReplaysThemeAndGraph()
    {
        Fixture();
        var vm = NewBrain();
        vm.IsDarkTheme = () => false;
        await vm.RefreshAsync();
        _posted.Clear();

        await vm.HandlePageMessageAsync(Page, """{"type":"ready"}""");

        _posted.Select(Type).Should().Equal("theme", "graph");
        JsonDocument.Parse(_posted[0]).RootElement.GetProperty("dark").GetBoolean().Should().BeFalse();
        JsonDocument.Parse(_posted[1]).RootElement.GetProperty("data").GetProperty("nodes").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Brain_ProblemMessage_ShowsWebGlText_OnlyFromOwnPage()
    {
        Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();

        await vm.HandlePageMessageAsync("https://evil.example/", """{"type":"problem","reason":"webgl"}""");
        vm.PageProblem.Should().BeNull();

        await vm.HandlePageMessageAsync(Page, """{"type":"problem","reason":"webgl"}""");
        vm.PageProblem.Should().Be(BrainViewModel.WebGlProblem).And.StartWith("The graph needs WebGL");
        vm.ShowGraph.Should().BeFalse();

        await vm.HandlePageMessageAsync(Page, """{"type":"problem","reason":"start"}""");
        vm.PageProblem.Should().Be(BrainViewModel.StartProblem);
    }

    [Fact]
    public async Task Brain_ClosingThePanel_TellsThePageToClear()
    {
        var (a, _) = Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();
        await vm.HandlePageMessageAsync(Page, """{"type":"ready"}""");
        await vm.HandlePageMessageAsync(Page, $$"""{"type":"select","key":"n:{{a}}"}""");
        _posted.Clear();

        vm.ClearSelectionCommand.Execute(null);

        _posted.Select(Type).Should().Equal("clear");
    }

    [Fact]
    public async Task Brain_PageProblem_HidesGraph_UntilCleared()
    {
        Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();
        vm.ShowGraph.Should().BeTrue();

        vm.PageProblem = "The Brain could not be started: boom";
        vm.ShowGraph.Should().BeFalse();
        vm.HasPageProblem.Should().BeTrue();

        vm.PageProblem = null;   // the view clears it when it tries again
        vm.ShowGraph.Should().BeTrue();
        vm.HasPageProblem.Should().BeFalse();
    }
    [Fact]
    public async Task Brain_Changed_RebuildsOnNextRefresh()
    {
        Fixture();
        var vm = NewBrain();
        await vm.RefreshAsync();
        _posted.Clear();
        await vm.RefreshAsync();
        _posted.Should().BeEmpty("nothing changed");

        Store(s => { s.SetMeta("rev", "other"); return 0; });
        await vm.RefreshAsync();
        _posted.Should().Contain(p => Type(p) == "graph");
    }

    // ---- adding from the Brain: always through the Inbox, as pending

    private (BrainViewModel Brain, IntakeViewModel Intake) NewBrainWithInbox()
    {
        var inbox = new IntakeViewModel(_ws, () => (null, "")) { ConfirmSaveSource = _ => false };
        var vm = new BrainViewModel(_ws, (d, ids) => _asked.Add((d, ids)), inbox, () => _intakeShown++);
        vm.PostToPage += _posted.Add;
        return (vm, inbox);
    }

    private string MakeDocsFolder()
    {
        var docs = Path.Combine(_dir, "docs");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "one.md"), "# One\n\nPrefetch shows program execution on Windows. " + new string('a', 300));
        File.WriteAllText(Path.Combine(docs, "two.md"), "# Two\n\nShimcache records executables seen by the system. " + new string('b', 300));
        return docs;
    }

    [Fact]
    public async Task Brain_AddFolder_GoesToIntake()
    {
        var (vm, inbox) = NewBrainWithInbox();
        var docs = MakeDocsFolder();
        inbox.PickFolder = _ => docs;

        await vm.AddFolderCommand.ExecuteAsync(null);

        inbox.WaitingCount.Should().Be(2);
        await vm.RefreshAsync(force: true);
        vm.IsEmpty.Should().BeTrue("nothing reaches the brain before the examiner accepts it");
        vm.HasBanner.Should().BeTrue();
        vm.BannerText.Should().Be("2 waiting in Intake");
        vm.ReviewNowCommand.Execute(null);
        _intakeShown.Should().Be(1);
    }

    [Fact]
    public async Task Brain_DropMessageWithPaths_AddsToIntake()
    {
        var (vm, inbox) = NewBrainWithInbox();
        var docs = MakeDocsFolder();

        await vm.HandleDropAsync(Page, new[] { Path.Combine(docs, "one.md") });
        inbox.WaitingCount.Should().Be(1);
        vm.BannerText.Should().Be("1 waiting in Intake");

        await vm.HandleDropAsync(Page, new[] { Path.Combine(docs, "one.md") });   // again: already waiting
        inbox.WaitingCount.Should().Be(1, "the same file is not listed twice");
    }

    [Fact]
    public async Task Brain_DropMessageFromOtherOrigin_Ignored()
    {
        var (vm, inbox) = NewBrainWithInbox();
        var docs = MakeDocsFolder();

        await vm.HandleDropAsync("https://evil.example/index.html", new[] { Path.Combine(docs, "one.md") });
        await vm.HandleDropAsync(null, new[] { Path.Combine(docs, "one.md") });

        inbox.WaitingCount.Should().Be(0);
        vm.HasBanner.Should().BeFalse();
        File.Exists(_ws.DbPath).Should().BeFalse();
    }
}
