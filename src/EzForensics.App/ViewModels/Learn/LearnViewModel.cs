using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Knowledge;

namespace EzForensics.App.ViewModels.Learn;

public enum LearnTab { Brain, Intake, Inbox, Library }

/// <summary>
/// The Learn page: what the assistant knows. Brain (the knowledge graph), Inbox (new documents waiting for a decision) and
/// Library (the sources). Everything runs on the local knowledge base; nothing leaves the computer.
/// </summary>
public sealed partial class LearnViewModel : PageViewModel
{
    public override string Title => PageNames.Learn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTab))]
    private int _selectedTabIndex;

    public LearnViewModel(KnowledgeWorkspace? workspace = null, Func<(EzForensics.Core.Knowledge.KnowledgeCompletion? Complete, string Model)>? ai = null, Func<Task<bool>>? probe = null)
    {
        Workspace = workspace ?? new KnowledgeWorkspace();
        Intake = new IntakeViewModel(Workspace, ai, (m, s) => ShowMessage(m, s), probe);
        Inbox = new InboxViewModel(Workspace, (m, s) => ShowMessage(m, s), ai, probe);
        Library = new LibraryViewModel(Workspace, AddAndShowIntakeAsync, (m, s) => ShowMessage(m, s), ai, probe);
        Inbox.AddToIntake = AddAndShowIntakeAsync;
        Brain = new BrainViewModel(Workspace, intake: Intake, showIntake: () => SelectedTab = LearnTab.Intake);
        Inbox.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InboxViewModel.PendingCount)) OnPropertyChanged(nameof(InboxHeader));
        };
        Intake.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IntakeViewModel.WaitingCount)) OnPropertyChanged(nameof(IntakeHeader));
        };
        Workspace.Changed += () => { Brain.MarkStale(); PendingRefresh = RefreshHiddenAsync(); };
    }

    /// <summary>The latest refresh of the tabs that are not on screen (tests await it).</summary>
    public Task PendingRefresh { get; private set; } = Task.CompletedTask;

    private bool _autoPicking, _tabChosen;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (!_autoPicking) _tabChosen = true;   // a click, a Spectre request or a drop: it wins over the first-open choice
        PendingRefresh = RefreshTabAsync((LearnTab)value);
    }

    /// <summary>True when the knowledge base holds at least one accepted note (the graph has something to draw).</summary>
    public bool HasAcceptedNotes()
    {
        try { return Workspace.Exists && Workspace.Run(s => s.Store.Notes(status: KnowledgeIntake.Accepted).Count > 0); }
        catch (Exception ex) { AppLog.Warn("Learn: could not count accepted notes: " + ex.Message); return true; }   // unknown: keep the Graph
    }

    /// <summary>
    /// On opening the page, until the user (or Spectre) has picked a tab: no accepted notes -> Intake (the graph would be empty),
    /// only notes waiting -> To review, otherwise the Graph.
    /// </summary>
    private void PickFirstTab(bool hasNotes)
    {
        if (_tabChosen) return;
        var tab = hasNotes ? LearnTab.Brain : Inbox.PendingCount > 0 ? LearnTab.Inbox : LearnTab.Intake;
        if (SelectedTab == tab) return;
        _autoPicking = true;
        try { SelectedTab = tab; }
        finally { _autoPicking = false; }
    }

    private async Task RefreshHiddenAsync()
    {
        // The tab on screen refreshes itself after its own operations, so an inline edit is never rebuilt under the cursor.
        // The Brain has no inline edits: on screen it is rebuilt at once, otherwise when it is shown.
        foreach (var t in new[] { LearnTab.Inbox, LearnTab.Library })
            if (t != SelectedTab) await RefreshTabAsync(t);
        if (SelectedTab == LearnTab.Brain) await RefreshTabAsync(LearnTab.Brain);
    }

    private Task RefreshTabAsync(LearnTab tab) => tab switch
    {
        LearnTab.Intake => GuardAsync(Intake.OnShownAsync, "Could not read Intake"),
        LearnTab.Inbox => GuardAsync(Inbox.RefreshAsync, "Could not read To review"),
        LearnTab.Library => GuardAsync(Library.RefreshAsync, "Could not read the Library"),
        LearnTab.Brain => GuardAsync(() => Brain.RefreshAsync(), "Could not build the graph"),
        _ => Task.CompletedTask,
    };

    public KnowledgeWorkspace Workspace { get; }
    public IntakeViewModel Intake { get; }
    public InboxViewModel Inbox { get; }
    public LibraryViewModel Library { get; }
    public BrainViewModel Brain { get; }

    public LearnTab SelectedTab
    {
        get => (LearnTab)SelectedTabIndex;
        set => SelectedTabIndex = (int)value;
    }

    public string IntakeHeader => Intake.WaitingCount > 0 ? $"Intake ({Intake.WaitingCount})" : "Intake";

    public string InboxHeader => Inbox.PendingCount > 0 ? $"To review ({Inbox.PendingCount})" : "To review";

    /// <summary>The latest page open (tests await it).</summary>
    public Task Shown { get; private set; } = Task.CompletedTask;

    public override void OnNavigatedTo() => Shown = ShowAsync();

    private async Task ShowAsync()
    {
        await GuardAsync(async () =>
        {
            await Inbox.RefreshAsync();
            PickFirstTab(await Task.Run(HasAcceptedNotes));
            if (SelectedTab == LearnTab.Brain) await Brain.RefreshAsync();   // draw the graph first when it is on screen
            await Intake.OnShownAsync();
            await Library.OnShownAsync();
        }, "Could not open the knowledge base");
    }

    /// <summary>Files or folders dropped on the page: they go to Intake, to be read and labelled.</summary>
    public Task AddAndShowIntakeAsync(IEnumerable<string> paths)
    {
        SelectedTab = LearnTab.Intake;
        return Intake.AddPathsAsync(paths);
    }
}
