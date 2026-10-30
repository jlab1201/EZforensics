using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Processes;

namespace EzForensics.App.ViewModels;

/// <summary>
/// One row of the Navigator tree. Exposes the properties the shared guide-line tree style (Themes/TreeStyles.xaml, GuideTreeItem) binds to:
/// IsExpanded, IsSelected, IsLast / IsFirst (position among siblings), IsTopLevel (sections) and the highlight flags.
/// </summary>
public abstract partial class NavigatorTreeItem : ObservableObject
{
    private static readonly IReadOnlyList<NavigatorTreeItem> NoChildren = Array.Empty<NavigatorTreeItem>();

    /// <summary>Name for automation (the row's text).</summary>
    public virtual string DisplayName => "";
    public virtual string AutomationId => "";
    /// <summary>Child rows: section -> groups (or one note), group -> members (+ "more" note), member -> none.</summary>
    public virtual IReadOnlyList<NavigatorTreeItem> Children => NoChildren;
    /// <summary>A disabled text line (empty-section text, "+ N more").</summary>
    public virtual bool IsNote => false;
    public bool IsTopLevel { get; set; }
    public bool IsFirst { get; set; }
    public bool IsLast { get; set; }
    /// <summary>Selection-coloured row (the selected group).</summary>
    public virtual bool IsOpenHighlighted => false;
    public bool IsMultiSelected => false;
    /// <summary>The TreeViewItem's own selection; unused (a click runs <see cref="Activate"/> instead).</summary>
    public bool RowSelected { get; set; }

    [ObservableProperty] private bool _isExpanded;
    /// <summary>Groups: this is the selected group (members highlighted on the canvas).</summary>
    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(IsOpenHighlighted));

    /// <summary>Row click: group highlights / clears, member reveals, section folds / unfolds.</summary>
    public virtual void Activate() { }

    /// <summary>Sets IsFirst / IsLast on a sibling list.</summary>
    public static void MarkSiblings(IReadOnlyList<NavigatorTreeItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            items[i].IsFirst = i == 0;
            items[i].IsLast = i == items.Count - 1;
        }
    }
}

/// <summary>A disabled text line in the tree.</summary>
public sealed class NavigatorNoteViewModel : NavigatorTreeItem
{
    public NavigatorNoteViewModel(string text, string automationId) { Text = text; AutomationId = automationId; }
    public string Text { get; }
    public override string DisplayName => Text;
    public override string AutomationId { get; }
    public override bool IsNote => true;
}

/// <summary>One process listed under an opened Navigator group (click: select it on the canvas and centre it).</summary>
public sealed class NavigatorMemberViewModel : NavigatorTreeItem
{
    public NavigatorMemberViewModel(ProcessNodeViewModel node, string evidence, Action<ProcessNodeViewModel> select)
    {
        Node = node;
        Evidence = evidence;
        SelectCommand = new RelayCommand(() => select(node));
    }

    public ProcessNodeViewModel Node { get; }
    public string Name => Node.Name;
    public string PidText => Node.PidText;
    public string StartText => Node.Node.CreateTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "start unknown";
    /// <summary>Indicator / data gap text (empty for roles and sessions).</summary>
    public string Evidence { get; }
    public bool HasEvidence => Evidence.Length > 0;
    public string ToolTipText => Evidence.Length > 0 ? Evidence : Node.ToolTipText;
    public override string DisplayName => Name;
    public override string AutomationId => "Results.ProcessTree.Navigator.Member." + Node.Pid.ToString(CultureInfo.InvariantCulture);
    public IRelayCommand SelectCommand { get; }
    public override void Activate() => SelectCommand.Execute(null);
}

/// <summary>One group of a Navigator section: title, count, flagged count; its members are listed under it.</summary>
public sealed class NavigatorGroupViewModel : NavigatorTreeItem
{
    /// <summary>Members listed at most (the canvas highlight still covers all of them).</summary>
    public const int MaxListedMembers = 500;

    public NavigatorGroupViewModel(NavigatorGroup group, IReadOnlyList<ProcessNodeViewModel> nodes, IReadOnlyList<string> evidence,
        Action<NavigatorGroupViewModel> select, Action<ProcessNodeViewModel> selectMember)
    {
        Group = group;
        Nodes = nodes;
        Title = group.Title;
        Count = nodes.Count;
        FlaggedCount = group.FlaggedCount;
        for (int i = 0; i < nodes.Count && i < MaxListedMembers; i++) Members.Add(new NavigatorMemberViewModel(nodes[i], evidence[i], selectMember));
        MoreText = nodes.Count > MaxListedMembers ? $"+ {nodes.Count - MaxListedMembers:N0} more (all are highlighted on the canvas)" : "";
        var children = new List<NavigatorTreeItem>(Members);
        if (HasMore) children.Add(new NavigatorNoteViewModel(MoreText, $"Results.ProcessTree.Navigator.More.{group.Section}.{group.Key}"));
        MarkSiblings(children);
        Children = children;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public NavigatorGroup Group { get; }
    /// <summary>Every member's node view model (ghosts never).</summary>
    public IReadOnlyList<ProcessNodeViewModel> Nodes { get; }
    public string Title { get; }
    public int Count { get; }
    public int FlaggedCount { get; }
    public string CountText => FlaggedCount > 0 && Group.Section == NavigatorSection.Roles
        ? $"{Count:N0} ({FlaggedCount:N0} flagged)" : Count.ToString("N0", CultureInfo.InvariantCulture);
    public List<NavigatorMemberViewModel> Members { get; } = new();
    public string MoreText { get; }
    public bool HasMore => MoreText.Length > 0;
    public override string DisplayName => Title;
    public override string AutomationId => $"Results.ProcessTree.Navigator.Group.{Group.Section}.{Group.Key}";
    public override IReadOnlyList<NavigatorTreeItem> Children { get; }
    public override bool IsOpenHighlighted => IsSelected;
    /// <summary>Roles section: the role's key (<see cref="ProcessNodeViewModel.RoleKeyOf"/>), so the row shows the diamond fill colour as a swatch (the legend); else empty.</summary>
    public string RoleKey => Group.Section == NavigatorSection.Roles ? ProcessNodeViewModel.RoleKeyOf(Title) : "";
    public bool HasRoleSwatch => RoleKey.Length > 0;
    public IRelayCommand SelectCommand { get; }
    public override void Activate() => SelectCommand.Execute(null);
}

/// <summary>A top-level Navigator section (Indicators, Data gaps, Roles, Sessions &amp; users).</summary>
public sealed class NavigatorSectionViewModel : NavigatorTreeItem
{
    public NavigatorSectionViewModel(NavigatorSection section, string title, IReadOnlyList<NavigatorGroupViewModel> groups, bool expanded)
    {
        Section = section;
        Title = title;
        Groups = groups;
        IsExpanded = expanded;
        IsTopLevel = true;
        EmptyText = section == NavigatorSection.Indicators ? "No indicators" : "Nothing here";
        MarkSiblings(groups);
        Children = groups.Count > 0
            ? groups
            : new NavigatorTreeItem[] { new NavigatorNoteViewModel(EmptyText, "Results.ProcessTree.Navigator.Empty." + section) { IsFirst = true, IsLast = true } };
    }

    /// <summary>Marks IsFirst / IsLast on the section list (the sections are top level).</summary>
    public static void Link(IReadOnlyList<NavigatorSectionViewModel> sections) => MarkSiblings(sections);

    public NavigatorSection Section { get; }
    public string Title { get; }
    public IReadOnlyList<NavigatorGroupViewModel> Groups { get; }
    public bool HasGroups => Groups.Count > 0;
    public string EmptyText { get; }
    /// <summary>Header count: processes carried by the section's groups (indicators: indicator rows).</summary>
    public int Total => Groups.Sum(g => g.Count);
    public string HeaderText => Section is NavigatorSection.Indicators or NavigatorSection.DataGaps
        ? $"{Title} ({Total:N0})" : $"{Title} ({Groups.Count:N0})";
    public override string DisplayName => Title;
    public override string AutomationId => "Results.ProcessTree.Navigator.Section." + Section;
    public override IReadOnlyList<NavigatorTreeItem> Children { get; }
    public override void Activate() => IsExpanded = !IsExpanded;
}
