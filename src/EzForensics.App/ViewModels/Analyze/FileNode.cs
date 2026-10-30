using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

public sealed partial class FileNode : ObservableObject
{
    public FileNode(string name, string fullPath, bool isFile)
    {
        Name = name;
        FullPath = fullPath;
        IsFile = isFile;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsFile { get; }
    /// <summary>Host folder of a layout v2 case (top level, named like one of <c>CaseIndex.Hosts</c>): computer glyph.</summary>
    public bool IsHost { get; set; }
    /// <summary>The <c>_Case</c> folder at the top of the tree (results whose source host could not be identified): shown as "Case-wide (unattributed)".</summary>
    public bool IsCaseWide { get; set; }
    public string DisplayName => IsCaseWide ? CaseTreeOrder.CaseWideDisplayName : Name;
    public string RowTip => IsCaseWide ? CaseTreeOrder.CaseWideTooltip + Environment.NewLine + FullPath : FullPath;
    /// <summary>Evidence kind of a category folder (<c>EvidenceKinds.ForFolder</c>): picks its icon and colour. Files and other folders: Other.</summary>
    public EvidenceKind Kind { get; set; }
    public string Glyph => IsFile ? "" : IsHost ? "" : KindGlyph(Kind);
    /// <summary>Icon colour, the same theme tones as the menu icons: "Info", "Warn", "Error", "Success" or "" (muted).</summary>
    public string IconTone => IsFile ? "Success" : IsHost ? "Info" : KindTone(Kind);

    private static string KindGlyph(EvidenceKind k) => k switch
    {
        EvidenceKind.Timeline => "",         // history clock
        EvidenceKind.EventLogs => "",        // pulse
        EvidenceKind.ProgramExecution => "", // play
        EvidenceKind.FileSystem => "",       // hard drive
        EvidenceKind.FileFolderAccess => "", // open file
        EvidenceKind.FileDeletion => "",     // recycle bin
        EvidenceKind.Registry => "",         // stacked hives
        EvidenceKind.UserActivity => "",     // person
        EvidenceKind.Database => "",         // database
        EvidenceKind.WebServer => "",        // globe
        EvidenceKind.Network => "",          // network
        EvidenceKind.Persistence => "",      // pin
        EvidenceKind.Accounts => "",         // people
        EvidenceKind.Logins => "",           // key
        EvidenceKind.ShellHistory => "",     // command prompt
        EvidenceKind.Services => "",         // gears
        EvidenceKind.ScheduledTasks => "",   // calendar
        EvidenceKind.Packages => "",         // package
        EvidenceKind.SystemInfo => "",       // info
        EvidenceKind.Memory => "",           // chip
        EvidenceKind.Processes => "\uE8FD",  // process tree (the glyph of the Process tree buttons)
        _ => "",                             // folder
    };

    private static string KindTone(EvidenceKind k) => k switch
    {
        EvidenceKind.Timeline or EvidenceKind.EventLogs or EvidenceKind.Registry or EvidenceKind.Database or EvidenceKind.WebServer
            or EvidenceKind.Network or EvidenceKind.SystemInfo or EvidenceKind.Memory or EvidenceKind.UserActivity => "Info",
        EvidenceKind.ProgramExecution or EvidenceKind.ShellHistory or EvidenceKind.Packages or EvidenceKind.Processes => "Success",
        EvidenceKind.FileSystem or EvidenceKind.FileFolderAccess or EvidenceKind.Accounts or EvidenceKind.Logins
            or EvidenceKind.Services or EvidenceKind.ScheduledTasks => "Warn",
        EvidenceKind.FileDeletion or EvidenceKind.Persistence => "Error",
        _ => "",
    };
    /// <summary>Tool of a file (<c>CaseIndex.Describe(path).ToolId</c>; null when unknown).</summary>
    public string? Tool { get; set; }
    /// <summary>Tool column text: the file's tool; a folder shows its files' one tool, "Mixed (n)" or nothing.</summary>
    [ObservableProperty] private string _toolText = "";
    /// <summary>Ctrl/Shift multi-selection of the tree (drives the selection highlight; plain clicks make it the single node).</summary>
    [ObservableProperty] private bool _isMultiSelected;
    /// <summary>The file shown in the events pane; it keeps the selection highlight unless a Ctrl/Shift multi-selection is active.</summary>
    [ObservableProperty] private bool _isOpenHighlighted;
    /// <summary>Hidden by the tree search box (neither it nor a descendant matches).</summary>
    [ObservableProperty] private bool _isFilteredOut;
    /// <summary>Content search hit badge ("1,234 hits", "10,000+ hits"; "" = no badge).</summary>
    [ObservableProperty] private string _hitText = "";
    /// <summary>Lines of the file that contain the search keyword (0 = none / file name match only).</summary>
    public int HitCount { get; set; }
    /// <summary>Tooltip of the hit pill ("413 hits").</summary>
    [ObservableProperty] private string _hitTip = "Lines containing the search text";

    /// <summary>Sets <see cref="ToolText"/> bottom-up (files: <see cref="Tool"/>; folders: <c>CaseTreeFilter.AggregateTool</c> over their files).</summary>
    public IReadOnlyCollection<string> AggregateTools()
    {
        if (IsFile)
        {
            ToolText = Tool ?? "";
            return string.IsNullOrEmpty(Tool) ? Array.Empty<string>() : new[] { Tool! };
        }
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Children) set.UnionWith(c.AggregateTools());
        ToolText = CaseTreeFilter.AggregateTool(set);
        return set;
    }
    /// <summary>File length and last-write time from the listing (files only; folders aggregate in <see cref="Summary"/>).</summary>
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    /// <summary>Position in the default order among its siblings (header sort falls back to it).</summary>
    public int DefaultIndex { get; set; }
    public ObservableCollection<FileNode> Children { get; } = new();
    public FileNode? Parent { get; private set; }
    /// <summary>0 = top-level section (Timeline, category folders).</summary>
    public int Depth { get; private set; }
    public bool IsTopLevel => Depth == 0;
    /// <summary>First / last among its siblings (tree guide lines and section separators).</summary>
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;
    /// <summary>Collapsed by default; only the path to the selected file is expanded.</summary>
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Collapsing a folder drops its (now hidden) descendants from the multi-selection, so no operation acts on unseen items.</summary>
    partial void OnIsExpandedChanged(bool value)
    {
        if (value || IsFile) return;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                if (d.IsMultiSelected) d.IsMultiSelected = false;
    }

    /// <summary>True when an ancestor folder is collapsed (the node is not shown).</summary>
    public bool IsHiddenByCollapse
    {
        get
        {
            for (var p = Parent; p != null; p = p.Parent) if (!p.IsExpanded) return true;
            return false;
        }
    }

    // ---- stats columns (Earliest | Latest | Events | Size)
    /// <summary>Computed stats of a file (null while pending, or when they could not be computed).</summary>
    public FileStats? Stats { get; set; }
    /// <summary>File: stats requested but not arrived yet. Folder: some descendant is pending.</summary>
    public bool IsPending { get; set; }
    /// <summary>Values of this row (a folder aggregates its descendants).</summary>
    public StatsSummary Summary { get; private set; }
    [ObservableProperty] private string _earliestText = FileStatsFormat.Pending;
    [ObservableProperty] private string _latestText = FileStatsFormat.Pending;
    [ObservableProperty] private string _eventsText = FileStatsFormat.Pending;
    [ObservableProperty] private string _sizeText = "";
    /// <summary>UIA ItemStatus: all column values in one string (tests, screen readers).</summary>
    [ObservableProperty] private string _statsStatus = "";
    /// <summary>Tooltip of the Earliest / Latest cells: "(UTC)" plus the implausible-timestamp note.</summary>
    [ObservableProperty] private string _earliestTip = "Earliest (UTC)";
    [ObservableProperty] private string _latestTip = "Latest (UTC)";

    /// <summary>Sets Parent / Depth / IsFirst / IsLast for this subtree (call once the children are final).</summary>
    public static void Link(IReadOnlyList<FileNode> siblings, FileNode? parent, int depth)
    {
        for (int i = 0; i < siblings.Count; i++)
        {
            var n = siblings[i];
            n.Parent = parent;
            n.Depth = depth;
            n.IsFirst = i == 0;
            n.IsLast = i == siblings.Count - 1;
            Link(n.Children, n, depth + 1);
        }
    }

    /// <summary>Recomputes IsFirst / IsLast among the siblings the search filter leaves visible (guide lines, section separators).</summary>
    public static void LinkVisible(IReadOnlyList<FileNode> siblings)
    {
        var vis = siblings.Where(n => !n.IsFilteredOut).ToList();
        for (int i = 0; i < vis.Count; i++)
        {
            vis[i].IsFirst = i == 0;
            vis[i].IsLast = i == vis.Count - 1;
            LinkVisible(vis[i].Children);
        }
    }

    /// <summary>Recomputes <see cref="Summary"/> / <see cref="IsPending"/> bottom-up and refreshes the column texts.</summary>
    public void Aggregate(string? dateFormat)
    {
        if (IsFile)
            Summary = Stats is { } s ? s.Summary with { Size = Length } : new StatsSummary(null, null, null, Length);
        else
        {
            bool pending = false;
            foreach (var c in Children)
            {
                c.Aggregate(dateFormat);
                pending |= c.IsPending;
            }
            IsPending = pending;
            Summary = FileStatsAggregator.Combine(Children.Select(c => c.Summary));
        }
        UpdateTexts(dateFormat);
    }

    public void UpdateTexts(string? dateFormat)
    {
        var s = Summary;
        EarliestText = IsPending ? FileStatsFormat.Pending : FileStatsFormat.Timestamp(s.Earliest, dateFormat);
        LatestText = IsPending ? FileStatsFormat.Pending : FileStatsFormat.Timestamp(s.Latest, dateFormat);
        var note = TimestampPlausibility.Describe(s.Implausible);
        EarliestTip = note.Length == 0 ? "Earliest (UTC)" : "Earliest (UTC)" + Environment.NewLine + note;
        LatestTip = note.Length == 0 ? "Latest (UTC)" : "Latest (UTC)" + Environment.NewLine + note;
        EventsText = IsPending ? FileStatsFormat.Pending : FileStatsFormat.Events(s.Events);
        SizeText = FileStatsFormat.Size(s.Size);
        BuildStatus();
    }

    partial void OnHitTextChanged(string value) => BuildStatus();
    partial void OnToolTextChanged(string value) => BuildStatus();

    private void BuildStatus()
    {
        StatsStatus = $"Earliest: {EarliestText}; Latest: {LatestText}; Events: {EventsText}; Size: {SizeText}; Tool: {ToolText}"
                      + (HitText.Length > 0 ? $"; Hits: {HitText}" : "");
    }

    /// <summary>Header-sort key of a column (null = no value, sorted last).</summary>
    public IComparable? SortKey(string column) => column switch
    {
        TreeColumnsViewModel.Earliest => IsPending ? null : Summary.Earliest,
        TreeColumnsViewModel.Latest => IsPending ? null : Summary.Latest,
        TreeColumnsViewModel.Events => IsPending ? null : Summary.Events,
        TreeColumnsViewModel.Size => Summary.Size,
        TreeColumnsViewModel.Tool => string.IsNullOrEmpty(ToolText) ? null : ToolText,
        _ => null,
    };

    public IEnumerable<FileNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants()) yield return d;
    }
}
