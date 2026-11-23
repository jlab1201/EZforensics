using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.App.Views;
using EzForensics.Core.Processes;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Processes page tree of one host (ProcessTreePane): Show exited, Navigator, and the details card. Everything is read from the process
/// correlator's files (<see cref="ProcessActivityStore"/>: Processes.csv, Processes_Sets.csv, Processes_Activity.csv); the App never correlates itself. Missing or
/// stale files: the correlator job is queued (the Jobs list on Ingest) and the tree loads when it has written them. <see cref="Dispose"/> cancels and releases the CSVs.
/// </summary>
public sealed partial class ProcessTreeViewModel : ObservableObject, IProcessGraphSource, IDisposable
{
    /// <summary>Depth whose nodes start expanded (0 = roots): roots and their children are open, so three levels show.</summary>
    public const int InitialExpandDepth = 2;
    /// <summary>A Navigator group reveals (expands the path to) at most this many members.</summary>
    public const int MaxRevealedMatches = 300;

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string, FilterNode> _openRowsInGrid;
    private readonly Action _close;
    private readonly Func<string, CancellationToken, Task<string?>> _correlate;
    private ProcessActivityStore? _store;
    /// <summary>The drawn graph (Processes.csv); the details read <see cref="_activity"/> (the same store).</summary>
    private ProcessGraph? _graph;
    private IProcessDetailSource? _activity;
    private List<ProcessNodeViewModel> _all = new();
    private List<ProcessNodeViewModel> _roots = new();
    private IReadOnlyList<ProcessNodeViewModel> _visibleRoots = Array.Empty<ProcessNodeViewModel>();
    private bool _disposed;
    private Task _navigator = Task.CompletedTask;
    /// <summary>Navigator open / closed, kept for the rest of the app session (a new tree opens like the last one was left).</summary>
    private static bool s_navigatorOpen = false;
    /// <summary>View › Show grid, kept for the rest of the app session like <see cref="s_navigatorOpen"/>.</summary>
    private static bool s_showGrid = true;

    /// <param name="openRowsInGrid">(csv path, filter): opens the CSV in the Analyze grid with the filter that shows the card's rows.</param>
    /// <param name="close">Close (x): releases the tree; the events grid fills the pane.</param>
    /// <param name="correlate">(host folder, cancellation): runs the process correlator job for the host and completes when it has finished; null = written, else why not.</param>
    public ProcessTreeViewModel(string hostFolder, string hostName, Action<string, FilterNode> openRowsInGrid, Action close,
        Func<string, CancellationToken, Task<string?>> correlate)
    {
        _correlate = correlate;
        HostFolder = hostFolder;
        HostName = hostName;
        _openRowsInGrid = openRowsInGrid;
        _close = close;
    }

    /// <summary>The running / finished <see cref="LoadAsync"/> (set by the host that started it; callers that select a process await it).</summary>
    public Task LoadTask { get; set; } = Task.CompletedTask;

    public string HostFolder { get; }
    public string HostName { get; }
    public string Title => "Process tree · " + HostName;

    public event Action? LayoutInvalidated;
    public event Action<ProcessNodeViewModel>? CenterRequested;
    public event Action? FitRequested;
    public event Action? TrailChanged;

    private int _layoutGeneration;
    public int LayoutGeneration => _layoutGeneration;
    /// <summary>The next layout pass lays the whole graph out again (explicit actions only; expand / collapse keep what is drawn in place).</summary>
    private void Relayout() => _layoutGeneration++;

    /// <summary>Top-level processes after the Show exited / Only flagged / Only this group filters (Tree mode, before stacking).</summary>
    public IReadOnlyList<ProcessNodeViewModel> VisibleRoots => _visibleRoots;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _statusText = "Reading the process correlation…";
    [ObservableProperty] private string _sourceSummary = "";
    [ObservableProperty] private string _warningsText = "";
    [ObservableProperty] private bool _showExited = true;
    /// <summary>The canvas draws its floor grid (View › Show grid).</summary>
    [ObservableProperty] private bool _showGrid = s_showGrid;
    [ObservableProperty] private ProcessNodeViewModel? _selected;
    [ObservableProperty] private ProcessDetailViewModel? _detail;
    [ObservableProperty] private int _flaggedCount;

    // ------------------------------------------------------------ navigator

    /// <summary>Navigator side panel (Indicators, Data gaps, Roles, Sessions &amp; users); empty until the graph is shown.</summary>
    public ObservableCollection<NavigatorSectionViewModel> NavigatorSections { get; } = new();
    [ObservableProperty] private bool _isNavigatorOpen = s_navigatorOpen;
    [ObservableProperty] private NavigatorGroupViewModel? _selectedGroup;
    [ObservableProperty] private bool _hasIndicators;
    public bool HasSelectedGroup => SelectedGroup != null;
    public bool HasNoIndicators => !HasIndicators;
    /// <summary>A ghost parent is selected: a minimal note instead of the details card.</summary>
    public string GhostNote => Selected is { IsGhost: true } g ? $"Parent not captured: PID {g.Pid.ToString(CultureInfo.InvariantCulture)}" : "";
    public bool HasGhostNote => GhostNote.Length > 0;
    public bool ShowSelectHint => Selected == null;

    partial void OnIsNavigatorOpenChanged(bool value) => s_navigatorOpen = value;

    /// <summary>Navigator search box: lists only the processes whose name, PID, user, command line or path contains it.</summary>
    [ObservableProperty] private string _navigatorSearch = "";
    partial void OnNavigatorSearchChanged(string value) { foreach (var s in NavigatorSections) s.ApplyFilter(value); }

    [RelayCommand] private void ClearNavigatorSearch() => NavigatorSearch = "";
    partial void OnShowGridChanged(bool value) => s_showGrid = value;
    partial void OnHasIndicatorsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoIndicators));
        FlagAllIndicatorsCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedGroupChanged(NavigatorGroupViewModel? value) => OnPropertyChanged(nameof(HasSelectedGroup));
    partial void OnSelectedChanged(ProcessNodeViewModel? value)
    {
        OnPropertyChanged(nameof(GhostNote));
        OnPropertyChanged(nameof(HasGhostNote));
        OnPropertyChanged(nameof(ShowSelectHint));
    }

    /// <summary>Groups the graph off the UI thread (ghosts excluded) and fills the Navigator.</summary>
    private async Task BuildNavigatorAsync(ProcessGraph graph, List<ProcessNodeViewModel> nodes, CancellationToken ct)
    {
        var sections = await Task.Run(() =>
        {
            var byNode = new Dictionary<ProcessNode, ProcessNodeViewModel>(nodes.Count);
            foreach (var n in nodes) byNode[n.Node] = n;
            var g = ProcessNavigatorGroups.Build(graph);
            NavigatorGroupViewModel Make(NavigatorGroup grp)
            {
                var members = grp.Members.Where(m => byNode.ContainsKey(m.Node)).ToList();
                return new NavigatorGroupViewModel(grp, members.Select(m => byNode[m.Node]).ToList(), members.Select(m => m.Evidence).ToList(), SelectGroup, SelectNavigatorMember);
            }
            NavigatorSectionViewModel Sec(NavigatorSection s, string title, IReadOnlyList<NavigatorGroup> groups, bool open) =>
                new(s, title, groups.Select(Make).Where(x => x.Count > 0).ToList(), open);
            return new List<NavigatorSectionViewModel>
            {
                Sec(NavigatorSection.Indicators, "Indicators", g.Indicators, true),
                Sec(NavigatorSection.Roles, "Roles", g.Roles, false),
                Sec(NavigatorSection.Sessions, "Sessions & users", g.Sessions, false),
                Sec(NavigatorSection.DataGaps, "Data gaps", g.DataGaps, false),
            };
        }, ct);
        if (_disposed || !ReferenceEquals(_graph, graph)) return;
        NavigatorSectionViewModel.Link(sections);
        NavigatorSections.Clear();
        foreach (var s in sections) NavigatorSections.Add(s);
        HasIndicators = sections[0].HasGroups;
        if (!string.IsNullOrWhiteSpace(NavigatorSearch)) foreach (var s in sections) s.ApplyFilter(NavigatorSearch);
    }

    /// <summary>
    /// Click on a group: highlight its members, dim the rest, reveal their paths, list them. Clicking the selected group again clears it. In the path view
    /// (Focus) a member that the path does not draw would stay invisible, so the graph goes back to the whole tree first and centres on the first member.
    /// </summary>
    public void SelectGroup(NavigatorGroupViewModel? group)
    {
        if (group != null && ReferenceEquals(group, SelectedGroup)) group = null;
        if (SelectedGroup != null) { SelectedGroup.IsSelected = false; SelectedGroup.IsExpanded = false; }
        var revealed = group == null ? new List<ProcessNodeViewModel>() : group.Nodes.Take(MaxRevealedMatches).ToList();
        bool leftFocus = false;
        if (_focus != null && revealed.Any(m => !IsDrawn(m)))
        {
            // the traced route stays: the whole tree draws it too, and a later Parent link / ancestor selection extends it (#10)
            var (origin, top) = (_trailOrigin, _trailTop);
            ExitFocus(center: null);
            if (origin != null && top != null) SetTrail(origin, top);
            leftFocus = true;
        }
        var members = group == null ? null : new HashSet<ProcessNodeViewModel>(group.Nodes);
        foreach (var n in _all)
        {
            n.IsGroupMember = members != null && members.Contains(n);
            n.IsDimmed = members != null && !members.Contains(n);
        }
        SelectedGroup = group;
        if (group != null)
        {
            group.IsSelected = true;
            group.IsExpanded = true;
            // no ExpandStackContaining: group members are pinned (IsPinnedSelf), so they are drawn on their own and never sit in a stack
            foreach (var m in revealed) Reveal(m);
        }
        LayoutInvalidated?.Invoke();
        if (leftFocus && revealed.Count > 0) CenterRequested?.Invoke(revealed[0]);
    }

    [RelayCommand] private void ClearGroup() => SelectGroup(null);

    /// <summary>Click on a listed member: selects it on the canvas and centres it.</summary>
    private void SelectNavigatorMember(ProcessNodeViewModel node) => RevealAndSelect(node);

    public bool HasWarnings => WarningsText.Length > 0;
    partial void OnWarningsTextChanged(string value) => OnPropertyChanged(nameof(HasWarnings));

    // ------------------------------------------------------------ load

    /// <summary>
    /// One path: when the correlator's output is up to date the tree and the card are read from it (<see cref="ProcessActivityStore"/>); when it is missing or stale the
    /// correlator job is queued ("Correlating processes…", see the Jobs list on Ingest), awaited, and the new files are read. Nothing is correlated here.
    /// </summary>
    public async Task LoadAsync()
    {
        var ct = _cts.Token;
        var host = HostFolder;
        try
        {
            var store = await ProcessActivityStore.OpenAsync(host, ct);
            if (store == null)
            {
                if (_disposed) return;
                StatusText = "Correlating processes… (the process correlator job is listed in the Jobs list on Ingest)";
                var error = await _correlate(host, ct);
                if (_disposed) return;
                store = error == null ? await ProcessActivityStore.OpenAsync(host, ct) : null;
                if (store == null)
                {
                    StatusText = "The process correlation could not be built: " + (error ?? "its files are still missing or out of date") + ". See Jobs, or use Rebuild process correlation on the host in Analyze.";
                    return;
                }
            }
            if (_disposed) { store.Dispose(); return; }
            _store = store;
            _activity = store;
            await ShowGraphAsync(store.Graph, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("Could not read the process correlation", ex);
            StatusText = "Could not read the process correlation: " + ex.Message;
        }
        finally { if (!_disposed) IsLoading = false; }
    }

    /// <summary>Tests: the card / count / flag source without a correlator store (call before <see cref="ShowGraphAsync"/>).</summary>
    internal void UseDetailSource(IProcessDetailSource source)
    {
        _activity = source;
        FlagAllIndicatorsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The running count pass of <see cref="ShowGraphAsync"/> (tests await it).</summary>
    internal Task DataCountsTask { get; private set; } = Task.CompletedTask;

    /// <summary>Builds the node view models off the UI thread and shows the tree.</summary>
    internal async Task ShowGraphAsync(ProcessGraph source, CancellationToken ct)
    {
        try
        {
            var (graph, nodes, roots) = await Task.Run(() =>
            {
                var g = source;
                ct.ThrowIfCancellationRequested();
                var all = new List<ProcessNodeViewModel>(g.AllNodes.Count);
                var top = new List<ProcessNodeViewModel>();
                // iterative: roots first, then children (deep chains must not overflow the stack)
                var stack = new Stack<(ProcessNode Node, ProcessNodeViewModel? Parent, int Depth)>();
                for (int i = g.Roots.Count - 1; i >= 0; i--) stack.Push((g.Roots[i], null, 0));
                var seen = new HashSet<ProcessNode>();
                while (stack.Count > 0)
                {
                    var (n, parent, depth) = stack.Pop();
                    if (!seen.Add(n)) continue;
                    var vm = new ProcessNodeViewModel(n, parent, Toggle, ExpandUp) { IsExpanded = depth < InitialExpandDepth, OpenTab = OpenDetailTab };
                    all.Add(vm);
                    if (parent == null) top.Add(vm); else parent.AllChildren.Add(vm);
                    for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push((n.Children[i], vm, depth + 1));
                }
                foreach (var vm in all.Where(v => v.IsFlagged))
                    foreach (var a in vm.Ancestors()) a.IsExpanded = true;
                return (g, all, top);
            }, ct);
            if (_disposed) return;
            _all = nodes;
            _roots = roots;
            _graph = graph;
            FlaggedCount = nodes.Count(n => n.IsFlagged);
            _navigator = BuildNavigatorAsync(graph, nodes, ct);
            SourceSummary = Summarize(graph) + " · from Processes.csv";
            WarningsText = string.Join(Environment.NewLine, graph.Warnings);
            StatusText = graph.IsEmpty
                ? "No processes found: run windows.pslist, windows.psscan or windows.pstree for this image (Tools › Volatility), or ingest event logs with Security 4688 / Sysmon 1 events."
                : $"{graph.AllNodes.Count:N0} processes, {FlaggedCount:N0} flagged";
            RecomputeVisible();
            _ = RefreshServicesAsync();
            DataCountsTask = RefreshDataCountsAsync();
            await _navigator;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("Could not build the process tree", ex);
            StatusText = "Could not build the process tree: " + ex.Message;
        }
        finally { if (!_disposed) IsLoading = false; }
    }

    private static string Summarize(ProcessGraph g)
    {
        static string Label(ProcessSources s) => s switch
        {
            ProcessSources.PsList => "pslist",
            ProcessSources.PsScan => "psscan",
            ProcessSources.PsTree => "pstree",
            ProcessSources.Evtx4688 => "4688",
            ProcessSources.Sysmon1 => "Sysmon 1",
            _ => s.ToString(),
        };
        var parts = g.SourceCounts.OrderBy(kv => kv.Key).Select(kv => $"{Label(kv.Key)} {kv.Value:N0}");
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------ visibility / expansion

    partial void OnShowExitedChanged(bool value) => RecomputeVisible();

    private void RecomputeVisible()
    {
        _visibleRoots = Filter(_roots, null);
        if (Selected != null && Mode == ProcessCanvasMode.Tree && !IsVisible(Selected)) SelectNode(null);
        Relayout();
        LayoutInvalidated?.Invoke();
    }

    /// <summary>Children that pass the filters; a hidden exited process passes its own children up so live descendants stay visible.</summary>
    private IReadOnlyList<ProcessNodeViewModel> Filter(IEnumerable<ProcessNodeViewModel> kids, HashSet<ProcessNodeViewModel>? keep)
    {
        var list = new List<ProcessNodeViewModel>();
        foreach (var k in kids)
        {
            if (!ShowExited && k.IsExited)
            {
                // hidden: its children move up a level (it keeps them for Focus mode, where its real chain is drawn)
                var sub = Filter(k.AllChildren, keep);
                k.SetVisibleChildren(sub);
                list.AddRange(sub);
                continue;
            }
            if (keep != null && !keep.Contains(k)) continue;
            k.SetVisibleChildren(Filter(k.AllChildren, keep));
            list.Add(k);
        }
        return list;
    }

    private IEnumerable<ProcessNodeViewModel> VisibleNodes()
    {
        var stack = new Stack<ProcessNodeViewModel>(_visibleRoots.Reverse());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (int i = n.VisibleChildren.Count - 1; i >= 0; i--) stack.Push(n.VisibleChildren[i]);
        }
    }

    private bool IsVisible(ProcessNodeViewModel n) => VisibleNodes().Contains(n);

    private void Toggle(ProcessNodeViewModel n)
    {
        n.IsExpanded = !n.IsExpanded;
        LayoutInvalidated?.Invoke();
    }

    /// <summary>Expands every ancestor so the node is drawn.</summary>
    private static void Reveal(ProcessNodeViewModel n)
    {
        foreach (var a in n.Ancestors()) a.IsExpanded = true;
    }

    /// <summary>Fit: a full re-layout (closes the gaps that expand / collapse left), then zoom to fit.</summary>
    [RelayCommand]
    private void Fit()
    {
        Relayout();
        LayoutInvalidated?.Invoke();
        FitRequested?.Invoke();
    }
    [RelayCommand] private void Close() => _close();

    // ------------------------------------------------------------ selection / detail

    public void SelectNode(ProcessNodeViewModel? node)
    {
        if (Selected == node) return;
        if (Selected != null) Selected.IsSelected = false;
        Selected = node;
        if (node != null) node.IsSelected = true;
        if (node != null) AdvanceTrail(node);
        // a ghost parent has no evidence: the pane shows a one-line note (GhostNote) instead of the card
        var tab = _openOnTab;
        _openOnTab = null;
        if (Detail != null) Detail.TabChosen -= OnDetailTabChosen;
        Detail = node == null || node.IsGhost ? null
            : new ProcessDetailViewModel(node, _activity, _openRowsInGrid, RevealAndSelect, initialTab: tab, flagRows: FlagRowsOf, preferredTab: _chosenTab);
        if (Detail != null) Detail.TabChosen += OnDetailTabChosen;
    }

    /// <summary>The tab the user last picked on the card's rail: the next card opens on it when it has rows (or is Overview); else the first tab with data.</summary>
    private ProcessTab? _chosenTab;
    /// <summary>The tab the next <see cref="SelectNode"/> opens the card on (a node's data chip), ahead of <see cref="_chosenTab"/>.</summary>
    private ProcessTab? _openOnTab;

    private void OnDetailTabChosen(ProcessTab tab) => _chosenTab = tab;

    /// <summary>A node's data chip: selects the node and shows its details on <paramref name="tab"/>.</summary>
    public void OpenDetailTab(ProcessNodeViewModel node, ProcessTab tab)
    {
        if (ReferenceEquals(Selected, node) && Detail != null) { Detail.ShowTab(tab); return; }
        _openOnTab = tab;
        SelectNode(node);
        _openOnTab = null;
    }

    /// <summary>Selects a node that may be collapsed or filtered away (Parent Process link): reveals it and centres it.</summary>
    public void RevealAndSelect(ProcessNodeViewModel node)
    {
        LeaveFocusUnlessDrawn(node);
        if (!ShowExited && node.IsExited) ShowExited = true;
        Reveal(node);
        ExpandStackContaining(node);
        LayoutInvalidated?.Invoke();
        SelectNode(node);
        CenterRequested?.Invoke(node);
    }

    /// <summary>Close (X) on the detail card.</summary>
    [RelayCommand] private void ClearSelection() => SelectNode(null);

    void IProcessGraphSource.SelectNode(ProcessNodeViewModel node) => SelectNode(node);

    // ------------------------------------------------------------ layout items: same-name stacks (Tree), Focus projection

    private readonly Dictionary<(ProcessNodeViewModel? Parent, string Key), ProcessStackViewModel> _stacks = new();
    private readonly HashSet<(ProcessNodeViewModel? Parent, string Key)> _expandedStacks = new();
    private readonly Dictionary<object, IReadOnlyList<object>> _layoutCache = new(ReferenceEqualityComparer.Instance);
    /// <summary>Pinned processes (never stacked) and their ancestors, for the current layout pass.</summary>
    private HashSet<ProcessNodeViewModel> _pinned = new();

    private static string StackKey(string name) => name.ToLowerInvariant();

    /// <summary>
    /// Flagged, with an indicator or in the selected Navigator group: drawn on its own, never in a stack. The selected process is not pinned (only its ancestors
    /// are): clicking a stack member would otherwise split it off, and the stack would collapse back as ×N-1 plus a separate process.
    /// </summary>
    private static bool IsPinnedSelf(ProcessNodeViewModel n) =>
        n.IsFlagged || n.Node.Indicators.Count > 0 || n.IsGroupMember;

    private HashSet<ProcessNodeViewModel> ComputePinned(ProcessNodeViewModel? except = null)
    {
        var set = new HashSet<ProcessNodeViewModel>();
        if (Selected is { } sel && !ReferenceEquals(sel, except))
            foreach (var a in sel.Ancestors()) if (!set.Add(a)) break;
        foreach (var n in _all)
        {
            if (ReferenceEquals(n, except) || !IsPinnedSelf(n) || !set.Add(n)) continue;
            // a stack hides its members' subtrees: the path to a pinned process is pinned too
            foreach (var a in n.Ancestors()) if (!set.Add(a)) break;
        }
        return set;
    }

    IReadOnlyList<object> IProcessGraphSource.LayoutRoots()
    {
        _layoutCache.Clear();
        _pinned = ComputePinned();
        if (_focus is { } f)
        {
            _pinned.Add(f.Node);
            foreach (var a in f.Ancestors) _pinned.Add(a);   // the chain is never stacked
            // path view: the chain starts at the topmost drawn ancestor (the parent at first), which carries the "↑ N more" pill
            int top = TopLevel(f);
            var item = top < f.Ancestors.Count ? f.Ancestors[top] : f.Node;
            SetExpandUp(top > 0 ? item : null, top);
            return new List<object> { item };
        }
        SetExpandUp(null, 0);
        return Arrange(null, _visibleRoots);
    }

    /// <summary>
    /// The items right of a drawn chain ancestor: the next chain item only while the ancestor is collapsed (its "N children" pill counts all its visible
    /// children); expanded, all its visible children in their real order (with same-name stacks), the chain item always among them.
    /// </summary>
    private IReadOnlyList<object> ChainChildren(ProcessNodeViewModel parent, ProcessNodeViewModel next)
    {
        if (!parent.IsExpanded) return new List<object> { next };
        var visible = new HashSet<ProcessNodeViewModel>(parent.VisibleChildren, ReferenceEqualityComparer.Instance);
        if (visible.Contains(next)) return Arrange(parent, parent.VisibleChildren);
        return Arrange(parent, parent.AllChildren.Where(c => ReferenceEquals(c, next) || visible.Contains(c)).ToList());
    }

    IReadOnlyList<object> IProcessGraphSource.LayoutChildren(object item)
    {
        if (_layoutCache.TryGetValue(item, out var cached)) return cached;
        IReadOnlyList<object> result = Array.Empty<object>();
        if (item is ProcessNodeViewModel n)
        {
            int i = _focus == null ? -1 : IndexOfAncestor(_focus, n);
            if (i >= 0)
            {
                var f = _focus!;
                var next = i + 1 < f.Ancestors.Count ? f.Ancestors[i + 1] : f.Node;
                result = ChainChildren(n, next);
            }
            else if (n.IsExpanded) result = Arrange(n, n.VisibleChildren);
        }
        _layoutCache[item] = result;
        return result;
    }

    private static int IndexOfAncestor(FocusProjection<ProcessNodeViewModel> f, ProcessNodeViewModel n)
    {
        for (int i = 0; i < f.Ancestors.Count; i++) if (ReferenceEquals(f.Ancestors[i], n)) return i;
        return -1;
    }

    /// <summary>Siblings with same-name stacks (<see cref="SiblingStacks"/>); an expanded stack is drawn as its "collapse" pill followed by the members.</summary>
    private IReadOnlyList<object> Arrange(ProcessNodeViewModel? parent, IReadOnlyList<ProcessNodeViewModel> siblings)
    {
        var list = new List<object>(siblings.Count);
        if (siblings.Count < SiblingStacks.Threshold) { list.AddRange(siblings); return list; }
        var pinned = _pinned;
        bool dim = SelectedGroup != null;
        foreach (var item in SiblingStacks.Group(siblings, s => s.StackName, pinned.Contains))
        {
            if (!item.IsStack) { list.Add(item.Single!); continue; }
            var key = (parent, StackKey(item.Name));
            if (!_stacks.TryGetValue(key, out var stack)) _stacks[key] = stack = new ProcessStackViewModel(parent, key.Item2, ToggleStack);
            bool expanded = _expandedStacks.Contains(key);
            stack.Update(item.Name, item.Members, expanded);
            stack.IsDimmed = dim;
            list.Add(stack);
            if (expanded) list.AddRange(item.Members);
        }
        return list;
    }

    private void ToggleStack(ProcessStackViewModel stack)
    {
        var key = (stack.Parent, stack.Key);
        if (!_expandedStacks.Remove(key)) _expandedStacks.Add(key);
        LayoutInvalidated?.Invoke();
    }

    /// <summary>The target of a reveal (Navigator member, Parent Process link) would sit in a collapsed stack: expand that stack.</summary>
    private void ExpandStackContaining(ProcessNodeViewModel node)
    {
        var siblings = node.Parent == null ? _visibleRoots : node.Parent.VisibleChildren;
        if (!siblings.Contains(node)) return;
        var pinned = ComputePinned(except: node);
        var members = SiblingStacks.StackContaining(siblings, node, s => s.StackName, pinned.Contains);
        if (members.Count > 0) _expandedStacks.Add((node.Parent, StackKey(node.StackName)));
    }

    // ------------------------------------------------------------ modes: Tree | Focus (path view)

    private FocusProjection<ProcessNodeViewModel>? _focus;
    /// <summary>Tree expansion when Focus was entered; restored when leaving it.</summary>
    private Dictionary<ProcessNodeViewModel, bool>? _treeExpansion;

    [ObservableProperty] private ProcessCanvasMode _mode = ProcessCanvasMode.Tree;
    public bool IsFocusMode => Mode == ProcessCanvasMode.Focus;
    partial void OnModeChanged(ProcessCanvasMode value)
    {
        OnPropertyChanged(nameof(IsFocusMode));
        OnPropertyChanged(nameof(CanShowWholeTree));
        OnPropertyChanged(nameof(ShowWholeTreeChecked));
        OnPropertyChanged(nameof(CanToggleWholeTree));
        ShowWholeTreeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Focus / path view: back to the whole tree, centred on the selected process.</summary>
    [RelayCommand(CanExecute = nameof(CanShowWholeTree))]
    private void ShowWholeTree() => ExitFocus(center: Selected);

    public bool CanShowWholeTree => IsFocusMode;

    /// <summary>View › Show whole tree: ticked in Tree mode. Ticking leaves the path view; unticking returns to the last path view (re-opened with its trail if it came from a PID click).</summary>
    public bool ShowWholeTreeChecked
    {
        get => !IsFocusMode;
        set
        {
            if (value == !IsFocusMode) return;
            if (value) ExitFocus(center: Selected);
            else if (_lastPathNode != null)
            {
                if (_lastPathWasOpenPath) OpenPath(_lastPathNode);
                else EnterFocus(_lastPathNode, _lastPathScope);
            }
            OnPropertyChanged();   // reverts the checkbox when there was no path to return to
        }
    }

    /// <summary>The Show whole tree checkbox is enabled in the path view, or in Tree mode once a path view has been opened.</summary>
    public bool CanToggleWholeTree => IsFocusMode || _lastPathNode != null;

    private ProcessNodeViewModel? _lastPathNode;
    private FocusScope _lastPathScope;
    private bool _lastPathWasOpenPath;

    // ------------------------------------------------------------ Fit to screen (View menu checkbox)

    /// <summary>View › Fit to screen: while ticked the graph is refitted after each re-layout and when the canvas resizes; any user zoom / pan unticks it (the canvas writes it back).</summary>
    [ObservableProperty] private bool _fitToScreen;
    partial void OnFitToScreenChanged(bool value) { if (value) Fit(); }

    /// <summary>A PID click: the path view (Trace up) of that process, centred and selected; the filters that would hide it are cleared first.</summary>
    public void OpenPath(ProcessNodeViewModel node)
    {
        if (!ShowExited && node.IsExited) ShowExited = true;
        EnterFocus(node, FocusScope.TraceUp);
        SetTrail(node, node);
        _lastPathWasOpenPath = true;
        CenterRequested?.Invoke(node);
    }

    // ------------------------------------------------------------ trail (the route traced by a PID click)

    private ProcessNodeViewModel? _trailOrigin, _trailTop;
    private IReadOnlyList<ProcessNodeViewModel> _trail = Array.Empty<ProcessNodeViewModel>();

    /// <summary>Real parent links from the furthest ancestor reached (<c>_trailTop</c>) down to the clicked process; empty outside the path view.</summary>
    public IReadOnlyList<ProcessNodeViewModel> TrailNodes => _trail;

    private void SetTrail(ProcessNodeViewModel? origin, ProcessNodeViewModel? top)
    {
        _trailOrigin = origin;
        _trailTop = top;
        var list = new List<ProcessNodeViewModel>();
        if (origin != null && top != null)
            for (var p = origin; p != null; p = p.Parent)
            {
                list.Add(p);
                if (ReferenceEquals(p, top)) break;
            }
        if (list.Count == 0 || !ReferenceEquals(list[^1], top)) list.Clear();
        if (_trail.Count == 0 && list.Count == 0) return;
        _trail = list;
        TrailChanged?.Invoke();
    }

    /// <summary>The trail only grows upwards: selecting (or revealing) an ancestor of the clicked process above the current top moves the top there.</summary>
    private void AdvanceTrail(ProcessNodeViewModel node)
    {
        if (_trailOrigin == null || _trailTop == null || ReferenceEquals(node, _trailTop)) return;
        for (var p = _trailTop.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, node)) { SetTrail(_trailOrigin, node); return; }
    }

    /// <summary>Shows only <paramref name="node"/>'s real ancestor chain and / or subtree (see <see cref="FocusProjection"/>); the Tree expansion is saved first.</summary>
    public void EnterFocus(ProcessNodeViewModel node, FocusScope scope)
    {
        SetTrail(null, null);   // OpenPath starts its trail after this
        (_lastPathNode, _lastPathScope, _lastPathWasOpenPath) = (node, scope, false);
        if (_treeExpansion == null) _treeExpansion = _all.ToDictionary(n => n, n => n.IsExpanded);
        else foreach (var (n, e) in _treeExpansion) n.IsExpanded = e;   // a new focus starts from the Tree state, not from the last focus
        var f = FocusProjection.Compute(node, n => n.Parent, n => n.AllChildren, _roots, scope);
        // the chain ancestors start collapsed: each draws only the next chain item until its "N children" pill shows all its children
        foreach (var a in f.Ancestors) a.IsExpanded = false;
        if (f.ExpandsSubtree)
        {
            node.IsExpanded = true;
            foreach (var d in FocusProjection.Subtree(node, n => n.AllChildren)) d.IsExpanded = true;
        }
        else if (f.IncludesSubtree) node.IsExpanded = true;
        if (!f.IncludesSubtree)
        {
            // Trace up: the node starts collapsed (its expand pill opens one level in place, staying in Focus)
            node.IsExpanded = false;
            foreach (var d in FocusProjection.Subtree(node, n => n.AllChildren)) d.IsExpanded = false;
        }
        _focus = f;
        // Trace up (a PID click) draws the process and its parent only; the other scopes draw the whole chain
        _shownAncestors = f.Scope == FocusScope.TraceUp ? Math.Min(1, f.Ancestors.Count) : f.Ancestors.Count;
        Mode = ProcessCanvasMode.Focus;
        SelectNode(node);
        Relayout();
        LayoutInvalidated?.Invoke();
        FitRequested?.Invoke();
    }

    /// <summary>Back to Tree mode with the Tree expansion from before Focus.</summary>
    private void ExitFocus(ProcessNodeViewModel? center)
    {
        if (_treeExpansion != null) foreach (var (n, e) in _treeExpansion) n.IsExpanded = e;
        _treeExpansion = null;
        _focus = null;
        SetTrail(null, null);
        Mode = ProcessCanvasMode.Tree;
        if (center != null) { Reveal(center); ExpandStackContaining(center); }
        Relayout();
        LayoutInvalidated?.Invoke();
        if (center != null) CenterRequested?.Invoke(center); else FitRequested?.Invoke();
    }

    /// <summary>Reveal of a process that Focus does not draw: back to Tree mode first.</summary>
    private void LeaveFocusUnlessDrawn(ProcessNodeViewModel node)
    {
        if (_focus is { } f && IsHiddenAncestor(node))
        {
            // a Parent Process link above the drawn path: draw the chain up to it (stays in the path view)
            _shownAncestors = f.Ancestors.Count - IndexOfAncestor(f, node);
            return;
        }
        if (_focus != null && !IsDrawn(node))
        {
            // a target on the trail or above its top (a Parent link) keeps the route: leaving the path view must not wipe it; SelectNode then extends it upwards
            var (origin, top) = (_trailOrigin, _trailTop);
            bool keep = origin != null && top != null && (_trail.Contains(node) || IsAncestorOf(node, top));
            ExitFocus(center: null);
            if (keep) SetTrail(origin, top);
        }
    }

    private static bool IsAncestorOf(ProcessNodeViewModel ancestor, ProcessNodeViewModel node)
    {
        for (var p = node.Parent; p != null; p = p.Parent) if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    /// <summary>Path view (Trace up): how many ancestors are drawn above the node, nearest first (the parent at first; "↑ N more" adds one per click).</summary>
    private int _shownAncestors;
    private ProcessNodeViewModel? _expandUpNode;

    /// <summary>Index into the chain (ancestors, then the node) of the topmost drawn item.</summary>
    private int TopLevel(FocusProjection<ProcessNodeViewModel> f) => Math.Max(0, f.Ancestors.Count - _shownAncestors);

    private void SetExpandUp(ProcessNodeViewModel? node, int hidden)
    {
        if (_expandUpNode != null && !ReferenceEquals(_expandUpNode, node)) _expandUpNode.HiddenAncestors = 0;
        _expandUpNode = node;
        if (node != null) node.HiddenAncestors = hidden;
    }

    /// <summary>"↑ N more" on the topmost drawn ancestor: one more ancestor is drawn (in its own free cell, nothing drawn moves).</summary>
    private void ExpandUp(ProcessNodeViewModel node)
    {
        if (_focus is not { } f || _shownAncestors >= f.Ancestors.Count) return;
        _shownAncestors++;
        AdvanceTrail(f.Ancestors[TopLevel(f)]);
        LayoutInvalidated?.Invoke();
    }

    /// <summary>An ancestor above the drawn part of the path view.</summary>
    private bool IsHiddenAncestor(ProcessNodeViewModel node) =>
        _focus is { } f && IndexOfAncestor(f, node) is var i && i >= 0 && i < TopLevel(f);

    /// <summary>
    /// Focus drew this process (or will once its ancestors are expanded): on the chain, the Trace up node's descendants, or (Trace up) a descendant of a drawn
    /// chain ancestor (shown by that ancestor's "N children" pill).
    /// </summary>
    private bool IsDrawn(ProcessNodeViewModel node)
    {
        if (_focus is not { } f) return false;
        if (IsHiddenAncestor(node)) return false;
        if (f.Items.Contains(node)) return true;
        if (f.Scope != FocusScope.TraceUp) return false;
        for (var p = node; p != null; p = p.Parent)
        {
            if (!ReferenceEquals(p, node) && ReferenceEquals(p, f.Node)) return true;
            if (p.Parent == null) return false;
            int above = IndexOfAncestor(f, p.Parent);
            if (above >= 0) return above >= TopLevel(f);
        }
        return false;
    }

    // ------------------------------------------------------------ svchost service chips

    private int _servicesVersion;

    /// <summary>"3 services" chips from svcscan (the correlator's Services column), set off the UI thread when the drawn nodes change.</summary>
    private async Task RefreshServicesAsync()
    {
        var activity = _activity;
        var nodes = _all;
        if (activity == null || nodes.Count == 0) return;
        int version = ++_servicesVersion;
        var ct = _cts.Token;
        List<(ProcessNodeViewModel Node, IReadOnlyList<string> Names)> found;
        try
        {
            found = await Task.Run(() =>
            {
                var list = new List<(ProcessNodeViewModel Node, IReadOnlyList<string> Names)>();
                foreach (var n in nodes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (n.IsGhost) continue;
                    var names = activity.ServiceNames(n.Node, ProcessFacetIndex.MaxRows);
                    if (names.Count > 0) list.Add((n, names));
                }
                return list;
            }, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { AppLog.Error("Could not read the svcscan services for the process tree", ex); return; }
        if (_disposed || version != _servicesVersion || !ReferenceEquals(nodes, _all)) return;
        foreach (var (n, names) in found) n.SetServices(names);
    }

    // ------------------------------------------------------------ data chips: linked rows per card tab on every node

    /// <summary>
    /// Count pass after the graph is shown: every node's rail tab counts (the correlator's Processes.csv columns, the same rows the card lists: an in-memory lookup,
    /// no CSV is read again) become its data chips. Off the UI thread, dropped when the graph changed or the tree was disposed.
    /// </summary>
    private async Task RefreshDataCountsAsync()
    {
        var activity = _activity;
        var store = _store;
        var nodes = _all;
        if (activity == null || nodes.Count == 0) return;
        var ct = _cts.Token;
        List<(ProcessNodeViewModel Node, IReadOnlyDictionary<ProcessTab, int> Counts)> found;
        try
        {
            found = await Task.Run(() =>
            {
                var list = new List<(ProcessNodeViewModel, IReadOnlyDictionary<ProcessTab, int>)>(nodes.Count);
                foreach (var n in nodes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (n.IsGhost) continue;
                    list.Add((n, store != null ? store.TabCounts(n.Node) : activity.Counts(n.Node)));
                }
                return list;
            }, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { AppLog.Error("Could not count the linked rows for the process tree", ex); return; }
        if (_disposed || !ReferenceEquals(nodes, _all)) return;
        foreach (var (n, c) in found) n.SetDataCounts(c, ProcessDetailViewModel.RailTabs);
    }

    // ------------------------------------------------------------ Flag all (Navigator Indicators) / Flag rows (details card)

    /// <summary>Above this many rows "Flag all" / "Flag rows" ask first.</summary>
    public const int FlagConfirmRows = 1000;

    /// <summary>
    /// Applies the rows to the Analyze flags (per CSV, with the last used flag; the open grid refreshes when it shows one of them) and returns how many rows changed.
    /// Set by the Analyze host; null = flagging is not available (the buttons are disabled).
    /// </summary>
    public Func<IReadOnlyList<ProcessCsvRows>, int>? FlagRowsSink
    {
        get => _flagRowsSink;
        set { _flagRowsSink = value; FlagAllIndicatorsCommand.NotifyCanExecuteChanged(); }
    }
    private Func<IReadOnlyList<ProcessCsvRows>, int>? _flagRowsSink;
    /// <summary>Asked before more than <see cref="FlagConfirmRows"/> rows are flagged; true = go ahead. Tests replace it; the default shows an EzDialog.</summary>
    public Func<string, bool> ConfirmFlag { get; set; } = message =>
        Dialogs.Confirm("Flag rows", message, "Flag", "Cancel", EzDialogSeverity.Question, defaultNo: true);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FlagAllIndicatorsCommand))]
    private bool _isFlagging;

    /// <summary>Processes listed under the Navigator's Indicators (the same rule checks; ghosts excluded).</summary>
    public IReadOnlyList<ProcessNodeViewModel> IndicatorNodes => NavigatorSections.Where(s => s.Section == NavigatorSection.Indicators)
        .SelectMany(s => s.Groups).SelectMany(g => g.Nodes).Where(n => !n.IsGhost).Distinct().ToList();

    private bool CanFlagAll() => !IsFlagging && FlagRowsSink != null && _activity != null && HasIndicators;

    /// <summary>Navigator "Flag all": flags every source row of every process listed under Indicators.</summary>
    [RelayCommand(CanExecute = nameof(CanFlagAll))]
    private Task FlagAllIndicatorsAsync() => FlagProcessesAsync(IndicatorNodes, "the processes with indicators");

    /// <summary>Details card "Flag rows" of one process.</summary>
    private Task FlagRowsOf(ProcessNodeViewModel node) => FlagProcessesAsync(new[] { node }, node.Name + " (" + node.PidText + ")");

    /// <summary>Finds the processes' rows in every source CSV off the UI thread (<see cref="ProcessRowLocator.CollectRowsAsync"/>), confirms a large batch, flags them.</summary>
    internal async Task FlagProcessesAsync(IReadOnlyList<ProcessNodeViewModel> nodes, string what)
    {
        var activity = _activity;
        var sink = FlagRowsSink;
        if (activity == null || sink == null || nodes.Count == 0 || IsFlagging) return;
        IsFlagging = true;
        var ct = _cts.Token;
        try
        {
            StatusText = $"Finding the rows of {what}…";
            var host = HostFolder;
            var real = nodes.Select(n => n.Node).ToList();
            var files = await Task.Run(async () =>
            {
                var lists = new List<string>();
                try
                {
                    var f = ProcessGraphBuilder.FindFiles(host);
                    lists.AddRange(f.PsList.Concat(f.PsScan).Concat(f.PsTree));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { AppLog.Warn("Flag rows: could not list the process CSVs: " + ex.Message); }
                return await ProcessRowLocator.CollectRowsAsync(activity, real, lists, ct);
            }, ct);
            if (_disposed) return;
            int total = files.Sum(f => f.Rows.Count);
            if (total == 0) { StatusText = $"No source rows found for {what}."; return; }
            if (total > FlagConfirmRows && !ConfirmFlag($"Flag {total:N0} rows in {files.Count:N0} file{(files.Count == 1 ? "" : "s")} for {what}?"))
            {
                StatusText = "Flagging cancelled.";
                return;
            }
            int changed = sink(files);
            AppLog.Info($"Process tree: flagged {total:N0} rows ({changed:N0} changed) in {files.Count} files for {nodes.Count} processes.");
            StatusText = $"Flagged {total:N0} row{(total == 1 ? "" : "s")} in {files.Count:N0} file{(files.Count == 1 ? "" : "s")} for {what}"
                + (changed < total ? $" ({(total - changed):N0} already had that flag)." : ".");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("Could not flag the process rows", ex);
            StatusText = "Could not flag the rows: " + ex.Message;
        }
        finally { if (!_disposed) IsFlagging = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        Detail = null;
        _activity = null;
        _store?.Dispose();
        _store = null;
    }
}

