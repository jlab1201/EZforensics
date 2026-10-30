using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EzForensics.App.ViewModels;

/// <summary>Canvas mode of the process tree. Lifetimes (start / exit bars) comes later.</summary>
public enum ProcessCanvasMode { Tree, Focus }

/// <summary>
/// Same-name sibling stack on the canvas (see <see cref="EzForensics.Core.Processes.SiblingStacks"/>): "svchost.exe ×24 (2 exited)" in place of the members under
/// one real parent (or at root level). A view construct only; every member keeps its real parent edge. Clicking it expands the members in place; while expanded
/// the same item is drawn as a small "collapse stack" pill in front of them.
/// </summary>
public sealed partial class ProcessStackViewModel : ObservableObject
{
    private readonly Action<ProcessStackViewModel> _toggle;

    public ProcessStackViewModel(ProcessNodeViewModel? parent, string key, Action<ProcessStackViewModel> toggle)
    {
        Parent = parent;
        Key = key;
        _toggle = toggle;
    }

    /// <summary>The real parent of every member; null at root level.</summary>
    public ProcessNodeViewModel? Parent { get; }
    /// <summary>Lower-case image name (stack identity under the parent).</summary>
    public string Key { get; }
    public IReadOnlyList<ProcessNodeViewModel> Members { get; private set; } = Array.Empty<ProcessNodeViewModel>();
    public string Name { get; private set; } = "";
    public int Count => Members.Count;
    public int ExitedCount { get; private set; }

    /// <summary>Members are shown as normal siblings; this item is the "collapse stack" pill.</summary>
    [ObservableProperty] private bool _isExpanded;
    /// <summary>A Navigator group is selected (members of a group are never stacked, so a stack is never in it).</summary>
    [ObservableProperty] private bool _isDimmed;

    public string AutomationId => "Results.ProcessTree.Stack." + (Parent == null ? "Root" : Parent.Pid.ToString(CultureInfo.InvariantCulture)) + "." + Key;
    public string CountText => "×" + Count.ToString("N0", CultureInfo.InvariantCulture);
    public string ExitedText => ExitedCount == 0 ? "" : $"({ExitedCount:N0} exited)";
    public bool HasExited => ExitedCount > 0;
    /// <summary>Accessible / tooltip text: "svchost.exe ×24 (2 exited)".</summary>
    public string Text => Name + " " + CountText + (HasExited ? " " + ExitedText : "");
    public string ToolTipText => IsExpanded
        ? $"Collapse the {Count:N0} {Name} processes back into one stack"
        : $"{Count:N0} {Name} processes under {(Parent == null ? "the top level" : Parent.Name + " (PID " + Parent.Pid.ToString(CultureInfo.InvariantCulture) + ")")}"
          + " (flagged or matching ones are drawn separately). Click to show them.";

    internal void Update(string name, IReadOnlyList<ProcessNodeViewModel> members, bool expanded)
    {
        bool changed = !ReferenceEquals(Members, members) || Name != name;
        Name = name;
        Members = members;
        ExitedCount = members.Count(m => m.IsExited);
        IsExpanded = expanded;
        if (changed)
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(ExitedText));
            OnPropertyChanged(nameof(HasExited));
        }
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(ToolTipText));
    }

    [RelayCommand] private void Toggle() => _toggle(this);
}
