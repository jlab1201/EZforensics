using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Processes;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>
/// One key/value row of the Overview ("—" when there is no data, then <see cref="Hint"/> says what fills it). <see cref="IsLink"/>: Parent Process selects the parent.
/// <see cref="CopyCommand"/> copies <see cref="Value"/> (through <see cref="Copier"/>) and shows a check mark on <see cref="IsCopied"/> for <see cref="CopiedFor"/>.
/// </summary>
public sealed partial class ProcessDetailField : ObservableObject
{
    public const string None = "—";

    private int _copyGeneration;

    public ProcessDetailField(string label, string value, bool isLink = false, string id = "", string hint = "")
    {
        Label = label;
        Value = value;
        IsLink = isLink;
        Id = id;
        Hint = hint;
    }

    public string Label { get; }
    public string Value { get; }
    public bool IsLink { get; }
    public string Id { get; }
    public string Hint { get; }
    /// <summary>Drawn in the warning colour (a role whose parent breaks the roles.json rule).</summary>
    public bool IsWarning { get; init; }

    /// <summary>Puts the text on the clipboard (the card sets it; null = the copy does nothing).</summary>
    public Action<string>? Copier { get; set; }
    /// <summary>How long the check mark shows after a copy.</summary>
    public TimeSpan CopiedFor { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Longer than <see cref="LongLength"/> characters or multi-line: the card clamps it to 3 lines with a "more…" link (<see cref="ToggleExpandedCommand"/>).</summary>
    public const int LongLength = 160;
    public bool IsLong => Value.Length > LongLength || Value.Contains('\n');
    public string MoreText => IsExpanded ? "less" : "more…";

    public bool CanCopy => Value != None;
    public string CopyTip => "Copy " + Label;

    [ObservableProperty] private bool _isCopied;
    [NotifyPropertyChangedFor(nameof(MoreText))]
    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public static ProcessDetailField Of(string label, string? value, string id, bool isLink = false, string hint = "") =>
        new(label, string.IsNullOrWhiteSpace(value) ? None : value!, isLink && !string.IsNullOrWhiteSpace(value), id, string.IsNullOrWhiteSpace(value) ? hint : "");

    [RelayCommand]
    private void Copy()
    {
        if (!CanCopy) return;
        Copier?.Invoke(Value);
        IsCopied = true;
        _ = ResetCopiedAsync(++_copyGeneration);
    }

    private async Task ResetCopiedAsync(int generation)
    {
        await Task.Delay(CopiedFor); // resume on the UI thread: IsCopied raises bindings and command state
        if (generation == _copyGeneration) IsCopied = false;
    }
}

/// <summary>Columns of a row-set grid: the tier chip column (only when some row is a link), the source chip column, then one text column per header.</summary>
public sealed record ProcessGridSpec(IReadOnlyList<string> Headers, bool ShowTier);

/// <summary>One sub-tab of a card tab: the rows of one source (<see cref="ProcessRowSet"/>), or the reason there are none.</summary>
public sealed partial class RowSetViewModel : ObservableObject
{
    public RowSetViewModel(ProcessRowSet set)
    {
        Set = set;
        Spec = new ProcessGridSpec(set.Headers, set.Rows.Any(r => r.HasTier));
        var src = set.SourceLabel.Length > 0 ? "Source: " + set.SourceLabel + " · " : "";
        Note = !set.HasRows ? "" : (set.Truncated
            ? $"{src}showing the first {set.Rows.Count:N0} of {set.TotalRows:N0} rows — open them in the table to see all."
            : $"{src}{set.TotalRows:N0} rows") + (set.Remark.Length > 0 ? " · " + set.Remark : "");
    }

    public ProcessRowSet Set { get; }
    public string Title => Set.Title;
    public string AutomationKey => Set.Id;
    public ProcessGridSpec Spec { get; }
    public IReadOnlyList<ProcessActivityRow> Rows => Set.Rows;
    public bool HasRows => Set.HasRows;
    public string Message => Set.Message;
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
    /// <summary>Dimmed sub-tab: the source has no rows for this process.</summary>
    public bool IsDim => !Set.HasRows;
    public string StatusText => Set.Status.ToString();
    public bool CanOpenInGrid => Set.Rows.Any(r => r.Origin != null);

    /// <summary>Row selected in the grid: Open rows in grid prefers its CSV.</summary>
    [ObservableProperty] private ProcessActivityRow? _selectedRow;
}

/// <summary>One icon of the card's rail (<see cref="ProcessTab"/>): its sub-tabs, loaded off the UI thread on first view, and its row count (dimmed at 0).</summary>
public sealed partial class DetailTabViewModel : ObservableObject
{
    private bool _loadStarted;

    public DetailTabViewModel(ProcessTab tab, string glyph)
    {
        Tab = tab;
        Glyph = glyph;
    }

    public ProcessTab Tab { get; }
    public string Id => Tab.ToString();
    public string Title => ProcessActivity.TabTitle(Tab);
    public string Glyph { get; }
    public bool IsOverview => Tab == ProcessTab.Overview;
    public bool IsRows => !IsOverview;

    public ObservableCollection<RowSetViewModel> Sets { get; } = new();
    public bool HasSubTabs => Sets.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsDim), nameof(ToolTipText), nameof(HasCount), nameof(HasData))]
    private int? _count;

    [ObservableProperty] private RowSetViewModel? _selectedSet;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = "";

    public bool HasCount => Count != null && Tab != ProcessTab.Overview;
    public string CountText => Count is { } c ? (c > 999 ? "999+" : c.ToString(CultureInfo.InvariantCulture)) : "";
    /// <summary>A data tab with nothing linked: drawn dimmed, never hidden.</summary>
    public bool IsDim => Count == 0 && Tab != ProcessTab.Overview;
    /// <summary>A data tab with linked rows: its count is drawn in the accent colour.</summary>
    public bool HasData => Count > 0 && Tab != ProcessTab.Overview;
    public string ToolTipText => Count is { } c && Tab != ProcessTab.Overview ? $"{Title} ({c:N0})" : Title;

    /// <summary>Raised when the visible sub-tab changes (the card refreshes Open rows in grid).</summary>
    public event Action? SetChanged;
    partial void OnSelectedSetChanged(RowSetViewModel? value) => SetChanged?.Invoke();

    internal void Reset()
    {
        _loadStarted = false;
        Sets.Clear();
        SelectedSet = null;
        Message = "";
        OnPropertyChanged(nameof(HasSubTabs));
    }

    /// <summary>Reads the tab once (off the UI thread). <paramref name="load"/> returns its row sets.</summary>
    internal async Task EnsureLoadedAsync(Func<IReadOnlyList<ProcessRowSet>> load, Func<bool> stillCurrent)
    {
        if (_loadStarted || IsOverview) return;
        _loadStarted = true;
        IsLoading = true;
        Message = "Loading…";
        try
        {
            var sets = await Task.Run(load);
            if (!stillCurrent()) return;
            Sets.Clear();
            foreach (var s in sets) Sets.Add(new RowSetViewModel(s));
            OnPropertyChanged(nameof(HasSubTabs));
            // first sub-tab with rows, else the first
            SelectedSet = Sets.FirstOrDefault(s => s.HasRows) ?? Sets.FirstOrDefault();
            Message = Sets.Count == 0 ? "Nothing to show." : "";
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read the process details", ex);
            Message = "Could not read the rows: " + ex.Message;
            _loadStarted = false;
        }
        finally { IsLoading = false; }
    }
}

/// <summary>
/// Details card of the selected process (Tanium style): an icon rail with Overview, Network, Files, Registry, Modules and Identity. Each tab reads its rows from the correlator's files (<see cref="ProcessActivityStore"/>, an <see cref="IProcessDetailSource"/>) on first view; every row carries a tier chip and a source chip.
/// </summary>
public sealed partial class ProcessDetailViewModel : ObservableObject
{
    /// <summary>Rail order and glyphs (Segoe MDL2).</summary>
    public static readonly IReadOnlyList<(ProcessTab Tab, string Glyph)> RailTabs = new[]
    {
        (ProcessTab.Overview, ""), (ProcessTab.Network, ""), (ProcessTab.Files, ""),
        (ProcessTab.Registry, ""), (ProcessTab.Modules, ""), (ProcessTab.Identity, ""),
    };

    private readonly Action<string, FilterNode> _openRowsInGrid;
    private readonly Action<ProcessNodeViewModel> _selectNode;
    private readonly Action<string> _copyText;
    private IProcessDetailSource? _activity;
    private int _generation;

    private readonly Func<ProcessNodeViewModel, Task>? _flagRows;
    /// <summary>The card shows a tab someone asked for (a chip, the user's last rail pick, a rail click): the first-tab-with-data pick no longer applies.</summary>
    private bool _tabFixed;
    private bool _settingTab;

    /// <param name="initialTab">Open on this tab (a node's data chip, or the tab the user last picked); null = the first data tab with rows once the counts are known, else Overview.</param>
    /// <param name="flagRows">"Flag rows": flags every source row of the process (null = the button is disabled).</param>
    public ProcessDetailViewModel(ProcessNodeViewModel node, IProcessDetailSource? activity, Action<string, FilterNode> openRowsInGrid,
        Action<ProcessNodeViewModel> selectNode, Action<string>? copyText = null, ProcessTab? initialTab = null, Func<ProcessNodeViewModel, Task>? flagRows = null,
        ProcessTab? preferredTab = null)
    {
        Node = node;
        _activity = activity;
        _openRowsInGrid = openRowsInGrid;
        _selectNode = selectNode;
        _flagRows = flagRows;
        _copyText = copyText ?? CopyToClipboard;
        var n = node.Node;
        PidLine = $"PID {n.Pid.ToString(CultureInfo.InvariantCulture)} · PPID {n.Ppid.ToString(CultureInfo.InvariantCulture)}"
            + (n.SessionId is { } s ? $" · Session {s.ToString(CultureInfo.InvariantCulture)}" : "")
            + (n.Wow64 == true ? " · Wow64" : "");
        CreateText = Format(n.CreateTime);
        ExitText = Format(n.ExitTime);
        // badges up top; one explanation per marker: a marker with an Indicator row (which quotes the evidence) gets no generic box
        var withEvidence = n.Indicators.Select(i => i.Kind.ToString()).ToHashSet();
        MarkerLines = Enum.GetValues<ProcessMarkers>().Where(v => v != ProcessMarkers.None && n.Markers.HasFlag(v) && !withEvidence.Contains(v.ToString())).Select(ProcessNodeViewModel.MarkerText).ToList();
        Badges = node.Badges;

        foreach (var (tab, glyph) in RailTabs)
        {
            var t = new DetailTabViewModel(tab, glyph);
            t.SetChanged += () => { if (t == SelectedTab) OpenRowsInGridCommand.NotifyCanExecuteChanged(); };
            Tabs.Add(t);
        }
        _selectedTab = Tabs[0];
        _preferred = preferredTab is { } pt ? Tabs.FirstOrDefault(t => t.Tab == pt) : null;
        if (initialTab is { } it && Tabs.FirstOrDefault(t => t.Tab == it) is { } chosen)
        {
            _selectedTab = chosen;
            _tabFixed = true;
        }
        else if (_preferred != null) _selectedTab = _preferred;
        // the tree's count pass already knows the rail counts: show them and pick the tab before anything is read
        _constructing = true;
        if (node.DataCounts is { } known) ApplyCounts(known);
        _constructing = false;
        BuildFields(null, null, null);
        Reload();
    }

    private readonly DetailTabViewModel? _preferred;
    private bool _constructing;

    /// <summary>The user picked a tab on the rail (not a programmatic pick): the tree opens the next card on it.</summary>
    public event Action<ProcessTab>? TabChosen;

    /// <summary>Sum of the rail's data tab counts (null until known): the coloured total at the top right of the title bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText), nameof(HasTotal), nameof(TotalToolTip))]
    private int? _totalCount;
    public bool HasTotal => TotalCount != null;
    public string TotalText => TotalCount is { } c ? c.ToString("N0", CultureInfo.InvariantCulture) : "";
    public string TotalToolTip => TotalCount is { } c
        ? $"{c:N0} linked row{(c == 1 ? "" : "s")}: " + string.Join(" · ", Tabs.Where(t => t.IsRows).Select(t => $"{t.Title} {(t.Count ?? 0):N0}"))
        : "";

    /// <summary>Shows <paramref name="tab"/> (a node's data chip on the selected process).</summary>
    public void ShowTab(ProcessTab tab)
    {
        if (Tabs.FirstOrDefault(t => t.Tab == tab) is not { } t) return;
        _tabFixed = true;
        SetTab(t);
    }

    private void SetTab(DetailTabViewModel t)
    {
        _settingTab = true;
        try { SelectedTab = t; }
        finally { _settingTab = false; }
    }

    /// <summary>
    /// Rail counts and total. Without a tab asked for, the card shows the user's last rail pick when that is Overview or has rows, else the first data tab with rows,
    /// else Overview.
    /// </summary>
    private void ApplyCounts(IReadOnlyDictionary<ProcessTab, int> counts)
    {
        foreach (var t in Tabs) t.Count = counts.GetValueOrDefault(t.Tab);
        TotalCount = Tabs.Where(t => t.IsRows).Sum(t => t.Count ?? 0);
        if (_tabFixed) return;
        _tabFixed = true;
        var pick = _preferred is { } p && (p.IsOverview || p.HasData) ? p : Tabs.FirstOrDefault(t => t.HasData) ?? Tabs[0];
        if (ReferenceEquals(pick, SelectedTab)) return;
        if (_constructing) _selectedTab = pick;   // the constructor's Reload loads it
        else SetTab(pick);
    }

    private bool CanFlagRows() => _flagRows != null && !IsFlagging;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FlagRowsCommand))]
    private bool _isFlagging;

    /// <summary>"Flag rows": every source row of this process (all tabs, the process lists) gets the last used flag in its CSV.</summary>
    [RelayCommand(CanExecute = nameof(CanFlagRows))]
    private async Task FlagRows()
    {
        if (_flagRows == null) return;
        IsFlagging = true;
        try { await _flagRows(Node); }
        finally { IsFlagging = false; }
    }

    public ProcessNodeViewModel Node { get; }
    public string Name => Node.Name;
    public string PidLine { get; }
    public string CreateText { get; }
    public string ExitText { get; }
    public IReadOnlyList<string> MarkerLines { get; }
    public bool HasMarkers => MarkerLines.Count > 0;
    public IReadOnlyList<ProcessBadge> Badges { get; }
    public bool HasBadges => Badges.Count > 0;
    public ObservableCollection<DetailTabViewModel> Tabs { get; } = new();

    [ObservableProperty] private IReadOnlyList<ProcessDetailField> _fields = Array.Empty<ProcessDetailField>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRowsInGridCommand))]
    private DetailTabViewModel _selectedTab;


    partial void OnSelectedTabChanged(DetailTabViewModel value)
    {
        if (!_settingTab && value != null)
        {
            _tabFixed = true;
            TabChosen?.Invoke(value.Tab);
        }
        LoadVisible();
    }

    /// <summary>A new source of the card data: every tab is read again.</summary>
    internal void Attach(IProcessDetailSource activity)
    {
        _activity = activity;
        Reload();
    }

    private void Reload()
    {
        _generation++;
        foreach (var t in Tabs) t.Reset();
        _ = LoadOverviewAsync();
        LoadVisible();
    }

    private void LoadVisible()
    {
        OpenRowsInGridCommand.NotifyCanExecuteChanged();
        var tab = SelectedTab;
        var activity = _activity;
        if (tab == null || tab.IsOverview) return;
        if (activity == null) { tab.Message = "Reading the process sources…"; return; }
        int gen = _generation;
        var node = Node.Node;
        Func<IReadOnlyList<ProcessRowSet>> load = () => activity.SetsFor(node, tab.Tab);
        _ = tab.EnsureLoadedAsync(load, () => gen == _generation)
            .ContinueWith(_ => AppServices.OnUi(OpenRowsInGridCommand.NotifyCanExecuteChanged), TaskScheduler.Default);
    }

    /// <summary>Overview roll-up off the UI thread: tab counts (rail), Sysmon 7 signature, first / last seen over the timeline.</summary>
    private async Task LoadOverviewAsync()
    {
        var activity = _activity;
        if (activity == null) return;
        int gen = _generation;
        var node = Node.Node;
        try
        {
            var (counts, sig, timeline) = await Task.Run(() => (activity.Counts(node), activity.SignatureText(node), activity.Timeline(node)));
            if (gen != _generation) return;
            ApplyCounts(counts);
            var (first, last) = ProcessActivity.Seen(timeline);
            BuildFields(sig, first, last);
        }
        catch (Exception ex) { AppLog.Error("Could not read the process overview", ex); }
    }

    private void BuildFields(string? signature, DateTime? first, DateTime? last)
    {
        var n = Node.Node;
        var parent = Node.Parent;
        var fields = new List<ProcessDetailField>
        {
            ProcessDetailField.Of("Start Time", CreateText, "StartTime", hint: "Filled by windows.pslist / psscan or a process creation event"),
        };
        // End Time: the exit time when a source has one; "in the active process list" is stated only for a process pslist walked
        if (ExitText.Length > 0) fields.Add(ProcessDetailField.Of("End Time", ExitText, "EndTime"));
        else if (n.Sources.HasFlag(ProcessSources.PsList)) fields.Add(ProcessDetailField.Of("End Time", "No exit time (in the active process list at capture)", "EndTime"));
        if (n.User.Length > 0)
            fields.Add(ProcessDetailField.Of("User", n.User + (n.UserSource.Length > 0 ? " (" + n.UserSource + ")" : ""), "User"));
        if (n.Integrity.Trim() is { Length: > 0 } integ && integ is not ("-" or "?") && !integ.Equals("unknown", StringComparison.OrdinalIgnoreCase) && !integ.Equals("N/A", StringComparison.OrdinalIgnoreCase)) fields.Add(ProcessDetailField.Of("Integrity", n.Integrity, "Integrity"));
        fields.Add(ProcessDetailField.Of("Process Path", n.Path, "Path", hint: "Filled by windows.pstree or a Security 4688 / Sysmon 1 event"));
        // role and indicators (facts from roles.json and the process rules, each with its evidence); a role says "by name only" when no path backed it
        if (n.Role.Length > 0)
        {
            var role = ProcessDetailField.Of("Role", ProcessRoles.RoleLabel(n) + (n.RoleBasis == RoleBasis.NameOnly ? " (by name only)" : ""), "Role",
                hint: "");
            fields.Add(ProcessRoles.HasUnexpectedParent(n) ? new ProcessDetailField(role.Label, role.Value, false, role.Id,
                "The parent is not one roles.json expects (see the Suspicious parent indicator)") { IsWarning = true } : role);
        }
        fields.Add(ProcessDetailField.Of("Command Line", n.CmdLine, "CmdLine", hint: "Filled by windows.cmdline or a Security 4688 / Sysmon 1 event"));
        fields.Add(ProcessDetailField.Of("Parent Process", parent == null
            ? (n.Ppid > 0 ? $"{(n.ParentImage.Length > 0 ? ProcessLogText.FileNameOf(n.ParentImage) + " " : "")}PID {n.Ppid.ToString(CultureInfo.InvariantCulture)} ({(n.Markers.HasFlag(ProcessMarkers.FromLogsOnly) ? "not in the logs" : "not in the image")})" : null)
            : $"{parent.Name} (PID {parent.Pid.ToString(CultureInfo.InvariantCulture)})", "Parent", isLink: parent != null, hint: "No parent PID in any source"));
        if (!string.IsNullOrWhiteSpace(signature)) fields.Add(ProcessDetailField.Of("Signature", signature, "Signature"));
        if (n.Hash.Length > 0)
            fields.Add(ProcessDetailField.Of("Process Hash", n.Hash + (n.HashSource.Length > 0 ? " (" + n.HashSource + ")" : ""), "Hash"));
        var memory = new List<string>();
        if (n.SessionId is { } sid) memory.Add("Session " + sid.ToString(CultureInfo.InvariantCulture));
        if (n.Wow64 == true) memory.Add("WoW64 (32-bit)");
        if (n.Offset.Length > 0) memory.Add("offset " + n.Offset);
        if (memory.Count > 0) fields.Add(ProcessDetailField.Of("Memory", string.Join(" · ", memory), "Memory"));
        if (first != null)
            fields.Add(ProcessDetailField.Of("Activity", last == null || last == first ? Format(first) : Format(first) + " → " + Format(last), "Activity"));
        foreach (var (ind, i) in n.Indicators.Select((x, i) => (x, i + 1)))
            fields.Add(ProcessDetailField.Of("Indicator", $"{ProcessNavigatorGroups.KindTitle(ind.Kind)} — {ind.Evidence}", "Indicator" + i.ToString(CultureInfo.InvariantCulture)));
        foreach (var f in fields) f.Copier = _copyText;
        Fields = fields;
    }

    private static string Format(DateTime? t) => t == null ? "" : t.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>Parent Process link: selects the parent on the graph and reveals it.</summary>
    [RelayCommand]
    private void SelectParent()
    {
        if (Node.Parent is { } p) _selectNode(p);
    }

    private RowSetViewModel? VisibleSet => SelectedTab is { IsOverview: false } t ? t.SelectedSet : null;

    private bool CanOpenRows() => VisibleSet is { CanOpenInGrid: true };

    /// <summary>Opens the visible rows' CSV in the Analyze grid with a filter that shows them (memory: the PID; events: EventId + second; Prefetch: the .pf; Zeek: uid).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenRows))]
    private void OpenRowsInGrid()
    {
        var set = VisibleSet;
        if (set == null) return;
        var target = ProcessRowLocator.GridTarget(set.Rows, set.SelectedRow?.Origin?.CsvPath);
        if (target == null) return;
        _openRowsInGrid(target.CsvPath, target.Filter);
    }

    /// <summary>Clipboard copy shared by the card (COM clipboard errors are logged, never thrown).</summary>
    internal static void CopyToClipboard(string text)
    {
        try { System.Windows.Clipboard.SetDataObject(text, true); }
        catch (Exception ex) { AppLog.Error("Could not copy to the clipboard", ex); }
    }

    [RelayCommand]
    private void CopyDetails()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Name}  ({PidLine})");
        foreach (var f in Fields) sb.AppendLine($"{f.Label}: {f.Value}");
        foreach (var m in MarkerLines) sb.AppendLine("- " + m);
        try { System.Windows.Clipboard.SetDataObject(sb.ToString().TrimEnd(), true); }
        catch (Exception ex) { AppLog.Error("Could not copy process details", ex); }
    }
}
