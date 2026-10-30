using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Processes;

namespace EzForensics.App.ViewModels;

/// <summary>Small marker badge on a process node. <see cref="Kind"/> picks the theme brush in XAML (error / orange / warn / neutral / info).</summary>
public sealed record ProcessBadge(string Text, string Kind, string Tip);

/// <summary>Things the process graph canvas needs from its view model (kept small so the control stays reusable).</summary>
public interface IProcessGraphSource
{
    /// <summary>
    /// Top-level items to lay out (after the filters, stacks and the canvas mode): <see cref="ProcessNodeViewModel"/> or <see cref="ProcessStackViewModel"/>.
    /// Called once at the start of every layout pass.
    /// </summary>
    IReadOnlyList<object> LayoutRoots();
    /// <summary>Items drawn right of <paramref name="item"/> (empty for a collapsed process, a stack or a pill), in display order.</summary>
    IReadOnlyList<object> LayoutChildren(object item);
    /// <summary>Expansion, filters, stacks or the mode changed: lay the graph out again.</summary>
    event Action? LayoutInvalidated;
    /// <summary>Scroll the viewport so the node is in the middle (search Enter, reveal).</summary>
    event Action<ProcessNodeViewModel>? CenterRequested;
    /// <summary>Zoom so the whole expanded graph fits.</summary>
    event Action? FitRequested;
    /// <summary>
    /// Changes when the next layout pass must be a full re-layout (Fit, Show whole tree, a new path view, Show exited). While it stays
    /// the same, an expand / collapse keeps every drawn item where it is and only places the new ones in free cells.
    /// </summary>
    int LayoutGeneration { get; }
    /// <summary>
    /// The traced route of a PID click in the path view: the nodes from the furthest ancestor reached down to the clicked process (empty when there is none).
    /// The canvas draws the connectors between them in the trail accent; a change (<see cref="TrailChanged"/>) redraws the connectors only, never lays out.
    /// </summary>
    IReadOnlyList<ProcessNodeViewModel> TrailNodes { get; }
    event Action? TrailChanged;
    void SelectNode(ProcessNodeViewModel node);
}

/// <summary>One process on the graph canvas: a <see cref="ProcessNode"/> plus expansion, selection and search state.</summary>
public sealed partial class ProcessNodeViewModel : ObservableObject
{
    /// <summary>Markers that make a process "flagged" (Only flagged filter, initial auto-expand).</summary>
    public const ProcessMarkers FlagMarkers = ProcessNavigatorGroups.FlagMarkers;

    private readonly Action<ProcessNodeViewModel> _toggle;

    private readonly Action<ProcessNodeViewModel>? _expandUp;

    /// <param name="expandUp">Path view "↑ N more" pill on the topmost drawn ancestor: reveal one more ancestor.</param>
    public ProcessNodeViewModel(ProcessNode node, ProcessNodeViewModel? parent, Action<ProcessNodeViewModel> toggle, Action<ProcessNodeViewModel>? expandUp = null)
    {
        Node = node;
        Parent = parent;
        _toggle = toggle;
        _expandUp = expandUp;
        Badges = BuildBadges(node.Markers);
    }

    /// <summary>Path view: this is the topmost drawn process and this many real ancestors above it are not drawn ("↑ N more" pill); 0 elsewhere.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExpandUp), nameof(ExpandUpText))]
    private int _hiddenAncestors;
    public bool CanExpandUp => HiddenAncestors > 0;
    public string ExpandUpText => "↑ " + HiddenAncestors.ToString("N0", CultureInfo.InvariantCulture) + " more";
    public string ExpandUpToolTip => $"Show the parent of {Name} (PID {Pid.ToString(CultureInfo.InvariantCulture)})";
    [RelayCommand] private void ExpandUp() => _expandUp?.Invoke(this);

    public ProcessNode Node { get; }
    public ProcessNodeViewModel? Parent { get; }
    /// <summary>Every child (unfiltered), in creation order.</summary>
    public List<ProcessNodeViewModel> AllChildren { get; } = new();
    /// <summary>Children after the filters (an exited child hidden by Show exited passes its own children up). Set by the tree view model.</summary>
    public IReadOnlyList<ProcessNodeViewModel> VisibleChildren { get; private set; } = Array.Empty<ProcessNodeViewModel>();

    /// <summary>Ghost parent of an orphan: a placeholder for a process that is in no source.</summary>
    public bool IsGhost => Node.IsGhost;
    /// <summary>A ghost's name is stated only by a source (4688 / Sysmon 1 ParentImage): "smss.exe (not captured)", else "PID 384 · not captured".</summary>
    public string Name => IsGhost
        ? string.IsNullOrEmpty(Node.Name) ? "PID " + Node.Pid.ToString(CultureInfo.InvariantCulture) + " · not captured" : Node.Name + " (not captured)"
        : string.IsNullOrEmpty(Node.Name) ? "(unknown)" : Node.Name;
    public string PidText => "PID " + Node.Pid.ToString(CultureInfo.InvariantCulture);
    /// <summary>Name used for same-name sibling stacks; empty (never stacked) for ghosts and unnamed processes.</summary>
    public string StackName => IsGhost ? "" : Node.Name;
    public int Pid => Node.Pid;
    public IReadOnlyList<ProcessBadge> Badges { get; }
    public bool HasBadges => Badges.Count > 0;
    public bool IsExited => (Node.Markers & ProcessMarkers.Exited) != 0;
    public bool IsFlagged => !IsGhost && (Node.Markers & FlagMarkers) != 0;
    /// <summary>Strongest marker for the node outline: "error" (injected), "orange" (hidden), "warn" (suspicious parent), "info" (orphan / PID reuse), "".</summary>
    public string Severity =>
        (Node.Markers & (ProcessMarkers.Injected | ProcessMarkers.Hollowed | ProcessMarkers.Ghosted)) != 0 ? "error"
        : (Node.Markers & (ProcessMarkers.Hidden | ProcessMarkers.PebMasquerade)) != 0 ? "orange"
        : (Node.Markers & (ProcessMarkers.SuspiciousParent | ProcessMarkers.SuspiciousThreads)) != 0 ? "warn"
        : (Node.Markers & (ProcessMarkers.Orphan | ProcessMarkers.ParentPidReused)) != 0 ? "info" : "";

    /// <summary>Role from roles.json (<see cref="ProcessNode.Role"/>); "Other" when the correlator gave none. Empty for a ghost.</summary>
    public string RoleText => IsGhost ? "" : string.IsNullOrEmpty(Node.Role) ? ProcessRoles.OtherRole : Node.Role;
    /// <summary>Stable key of the role for the diamond fill (theme brush <c>Brush.Role.{Key}</c>, see <see cref="RoleKeyOf"/>).</summary>
    public string RoleKey => IsGhost ? "" : RoleKeyOf(Node.Role);

    /// <summary>
    /// The role keys, one per role of roles.json plus Other (the Navigator Roles groups use the same keys for their swatch). A role this list does not know,
    /// and an empty role, is "Other".
    /// </summary>
    public static string RoleKeyOf(string? role) => role switch
    {
        "System foundation" => "System",
        "Session & logon" => "Session",
        "Services" => "Services",
        "Security" => "Security",
        "Desktop" => "Desktop",
        "Shells & scripts" => "Shells",
        "Admin tools & LOLBins" => "Admin",
        "Remote access" => "Remote",
        "Office" => "Office",
        "Browsers" => "Browsers",
        _ => "Other",
    };

    public int ChildCount => VisibleChildren.Count;
    public bool HasChildren => VisibleChildren.Count > 0;
    public string ChevronText => VisibleChildren.Count.ToString(CultureInfo.InvariantCulture);
    /// <summary>Collapsed-group pill: "1 child" / "12 children".</summary>
    public string ChildrenText => VisibleChildren.Count == 1 ? "1 child" : VisibleChildren.Count.ToString("N0", CultureInfo.InvariantCulture) + " children";
    /// <summary>
    /// Node tooltip: "name · PID n", "Exited yyyy-MM-dd HH:mm:ss UTC" for an exited process (the node's stop marker), the command line (or path; capped at
    /// <see cref="MaxToolTipCommandLine"/> characters so the tooltip fits the screen, the detail card keeps the full text), "Role: …" (the diamond's fill colour),
    /// then one line per badge (the node itself only shows a severity dot). A ghost parent: "Parent not captured: PID n" (plus the stated name).
    /// </summary>
    public string ToolTipText => IsGhost ? "Parent not captured: PID " + Node.Pid.ToString(CultureInfo.InvariantCulture)
            + Environment.NewLine + "Dashed line: the parent process was not captured by any source"
            + (string.IsNullOrEmpty(Node.Name) ? "" : Environment.NewLine + "Name stated by the child's event (ParentImage): " + Node.Name)
        : Name + " · " + PidText
          + (IsExited ? Environment.NewLine + "Exited" + (Node.ExitTime is { } x ? " " + x.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "") : "")
          + (ToolTipCore == Name ? "" : Environment.NewLine + Cap(ToolTipCore, MaxToolTipCommandLine))
          + Environment.NewLine + "Role: " + RoleText
          + (Badges.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Badges.Select(b => "• " + b.Text + ": " + b.Tip)));
    private string ToolTipCore => string.IsNullOrEmpty(Node.CmdLine) ? (string.IsNullOrEmpty(Node.Path) ? Name : Node.Path) : Node.CmdLine;
    /// <summary>Longest command line (characters) the node tooltip shows before cutting it with "…".</summary>
    public const int MaxToolTipCommandLine = 500;
    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Member of the Navigator group that is selected (drawn highlighted).</summary>
    [ObservableProperty] private bool _isGroupMember;
    /// <summary>A Navigator group is selected and this process is not in it (drawn faded).</summary>
    [ObservableProperty] private bool _isDimmed;

    /// <summary>Services svcscan lists for this PID (memory): the "3 services" chip. Set off the UI thread's work by the tree view model.</summary>
    [ObservableProperty] private int _serviceCount;
    [ObservableProperty] private string _servicesTip = "";
    public bool HasServices => ServiceCount > 0;
    public string ServicesText => ServiceCount == 1 ? "1 service" : ServiceCount.ToString("N0", CultureInfo.InvariantCulture) + " services";
    partial void OnServiceCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasServices));
        OnPropertyChanged(nameof(ServicesText));
    }

    /// <summary>Shows the services chip: <paramref name="names"/> (all of them) for the count, the first <see cref="MaxServicesInTip"/> in the tooltip.</summary>
    internal void SetServices(IReadOnlyList<string> names)
    {
        ServicesTip = names.Count == 0 ? "" : "Services hosted in this process (svcscan):" + Environment.NewLine
            + string.Join(Environment.NewLine, names.Take(MaxServicesInTip))
            + (names.Count > MaxServicesInTip ? Environment.NewLine + $"… and {names.Count - MaxServicesInTip:N0} more" : "");
        ServiceCount = names.Count;
    }

    public const int MaxServicesInTip = 20;

    [RelayCommand] private void Toggle() => _toggle(this);

    internal void SetVisibleChildren(IReadOnlyList<ProcessNodeViewModel> kids)
    {
        VisibleChildren = kids;
        OnPropertyChanged(nameof(ChildCount));
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ChevronText));
        OnPropertyChanged(nameof(ChildrenText));
    }

    public IEnumerable<ProcessNodeViewModel> Ancestors()
    {
        for (var p = Parent; p != null; p = p.Parent) yield return p;
    }

    public static IReadOnlyList<ProcessBadge> BuildBadges(ProcessMarkers m)
    {
        var list = new List<ProcessBadge>();
        if ((m & ProcessMarkers.Injected) != 0) list.Add(new ProcessBadge("INJ", "error", MarkerText(ProcessMarkers.Injected)));
        if ((m & ProcessMarkers.Hollowed) != 0) list.Add(new ProcessBadge("HOLLOW", "error", MarkerText(ProcessMarkers.Hollowed)));
        if ((m & ProcessMarkers.Ghosted) != 0) list.Add(new ProcessBadge("GHOST", "error", MarkerText(ProcessMarkers.Ghosted)));
        if ((m & ProcessMarkers.Hidden) != 0) list.Add(new ProcessBadge("HID", "orange", MarkerText(ProcessMarkers.Hidden)));
        if ((m & ProcessMarkers.PebMasquerade) != 0) list.Add(new ProcessBadge("PEB", "orange", MarkerText(ProcessMarkers.PebMasquerade)));
        if ((m & ProcessMarkers.SuspiciousThreads) != 0) list.Add(new ProcessBadge("THREAD", "warn", MarkerText(ProcessMarkers.SuspiciousThreads)));
        if ((m & ProcessMarkers.SuspiciousParent) != 0) list.Add(new ProcessBadge("PARENT", "warn", MarkerText(ProcessMarkers.SuspiciousParent)));
        if ((m & ProcessMarkers.Orphan) != 0) list.Add(new ProcessBadge("ORPHAN", "info", MarkerText(ProcessMarkers.Orphan)));
        if ((m & ProcessMarkers.ParentPidReused) != 0) list.Add(new ProcessBadge("REUSED", "info", MarkerText(ProcessMarkers.ParentPidReused)));
        if ((m & ProcessMarkers.Exited) != 0) list.Add(new ProcessBadge("EXIT", "neutral", MarkerText(ProcessMarkers.Exited)));
        if ((m & ProcessMarkers.FromLogsOnly) != 0) list.Add(new ProcessBadge("LOGS", "neutral", MarkerText(ProcessMarkers.FromLogsOnly)));
        return list;
    }

    /// <summary>Plain-words explanation of one marker (badge tooltip, detail panel).</summary>
    public static string MarkerText(ProcessMarkers m) => m switch
    {
        ProcessMarkers.Injected => "Injected: malfind found executable memory that is not backed by a file (possible code injection).",
        ProcessMarkers.Hidden => "Hidden: found by the pool scan (psscan) but not in the active process list (pslist) - unlinked or already exited.",
        ProcessMarkers.SuspiciousParent => "Suspicious parent: this parent/child combination is unusual for a Windows system process.",
        ProcessMarkers.Orphan => "Orphan: the parent PID is not in any source (the parent exited or is missing from the image).",
        ProcessMarkers.ParentPidReused => "Parent PID reused: the only process with the parent PID started after this one, so the real parent is gone.",
        ProcessMarkers.Exited => "Exited: the process has an exit time.",
        ProcessMarkers.FromLogsOnly => "Seen in event logs only, not in memory.",
        ProcessMarkers.Hollowed => "Hollowed: malware.hollowprocesses found that the image in memory does not match the mapped file.",
        ProcessMarkers.Ghosted => "Ghosted: malware.processghosting found the image file object delete-pending / delete-on-close.",
        ProcessMarkers.PebMasquerade => "PEB masquerade: malware.pebmasquerade reports a spoofed PEB image path or command line.",
        ProcessMarkers.SuspiciousThreads => "Suspicious threads: malware.suspicious_threads found a thread starting outside any mapped image.",
        _ => m.ToString(),
    };
}
