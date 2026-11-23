using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Processes;

namespace EzForensics.App.ViewModels;

/// <summary>One button of the PID chooser strip: "name · started &lt;time&gt;" (or "started unknown").</summary>
public sealed class PidCandidateViewModel
{
    public PidCandidateViewModel(ProcessNodeViewModel node)
    {
        Node = node;
        var n = node.Node;
        var name = string.IsNullOrWhiteSpace(n.Name) ? "(no name)" : n.Name;
        var started = n.CreateTime is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "unknown";
        var exited = n.ExitTime is { } x ? x.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "still running / unknown";
        _text = $"{name} · started {started} · exited {exited}";
    }

    /// <summary>The instance that was alive when the child started (the most likely parent); set by the chooser.</summary>
    public bool IsLikely { get; init; }

    public ProcessNodeViewModel Node { get; }
    private readonly string _text;
    public string Text => IsLikely ? _text + " · most likely (alive when the child started)" : _text;
}

/// <summary>Clickable PIDs: selects the process instance a grid row's PID belongs to (<see cref="ProcessPidResolver"/>), or offers the candidates when facts cannot tell.</summary>
public sealed partial class ProcessTreeViewModel
{
    /// <summary>Candidates of an ambiguous PID click (the chooser strip at the top of the pane); empty = no strip.</summary>
    public ObservableCollection<PidCandidateViewModel> PidCandidates { get; } = new();

    /// <summary>"PID 1234 matches 3 processes:"</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPidChooser))]
    private string _pidChooserTitle = "";

    public bool HasPidChooser => PidChooserTitle.Length > 0;

    /// <summary>
    /// Selects the instance of <paramref name="pid"/> a row points at: Found = the path view (Trace up) of it, selected and centred; Ambiguous = the chooser strip, nothing selected;
    /// NotFound = the status line says so and the graph stays as it was. <paramref name="sourceCsv"/> / <paramref name="rowCreateTime"/>: see <see cref="ProcessPidResolver.Resolve"/>.
    /// </summary>
    public PidResolutionKind? SelectPid(int pid, DateTime? rowTime, string? sourceCsv, DateTime? rowCreateTime, ProcessNode? child = null)
    {
        DismissPidChooser();
        if (_disposed || _graph == null) return null;
        var r = ProcessPidResolver.Resolve(_all.Select(v => v.Node), pid, rowTime, sourceCsv, rowCreateTime);
        var p = pid.ToString(CultureInfo.InvariantCulture);
        if (child != null && child.Ppid == pid)
        {
            // a PPID link: the parent edge the tree already draws wins; otherwise only the instances alive when the child started are plausible
            if (child.Parent is { IsGhost: false } par && par.Pid == pid && ViewModelOf(par) is { } pvm)
            {
                if (ReferenceEquals(Selected?.Node, child)) RevealAndSelect(pvm); else OpenPath(pvm);
                return PidResolutionKind.Found;
            }
            if (r.Kind == PidResolutionKind.Ambiguous)
            {
                var alive = r.Nodes.Where(n => AliveAt(n, child.CreateTime)).ToList();
                if (alive.Count == 1 && ViewModelOf(alive[0]) is { } avm) { OpenPath(avm); return PidResolutionKind.Found; }
                foreach (var n in r.Nodes.OrderBy(n => n.CreateTime ?? DateTime.MaxValue))
                    if (ViewModelOf(n) is { } c) PidCandidates.Add(new PidCandidateViewModel(c) { IsLikely = alive.Contains(n) });
                PidChooserTitle = $"PID {p} matches {PidCandidates.Count:N0} processes:";
                return r.Kind;
            }
        }
        switch (r.Kind)
        {
            case PidResolutionKind.Found:
                if (ViewModelOf(r.Nodes[0]) is { } vm) OpenPath(vm);
                break;
            case PidResolutionKind.Ambiguous:
                foreach (var n in r.Nodes.OrderBy(n => n.CreateTime ?? DateTime.MaxValue))
                    if (ViewModelOf(n) is { } c) PidCandidates.Add(new PidCandidateViewModel(c));
                PidChooserTitle = $"PID {p} matches {PidCandidates.Count:N0} processes:";
                break;
            default:
                StatusText = $"PID {p} is not in the process tree of {HostName}.";
                break;
        }
        return r.Kind;
    }

    /// <summary>Started before (or at) <paramref name="at"/> and not exited before it; unknown times do not rule a node out only when no time is known for the child.</summary>
    internal static bool AliveAt(ProcessNode n, DateTime? at)
    {
        if (at is not { } t) return false;
        return n.CreateTime is { } c && c <= t && (n.ExitTime is not { } x || x >= t);
    }

    /// <summary>The single instance of <paramref name="pid"/> a row points at (null when none or several).</summary>
    public ProcessNode? FindNode(int pid, DateTime? rowTime, string? sourceCsv, DateTime? rowCreateTime)
    {
        if (_disposed || _graph == null) return null;
        var r = ProcessPidResolver.Resolve(_all.Select(v => v.Node), pid, rowTime, sourceCsv, rowCreateTime);
        return r.Kind == PidResolutionKind.Found ? r.Nodes[0] : null;
    }

    private ProcessNodeViewModel? ViewModelOf(ProcessNode node)
    {
        foreach (var v in _all) if (ReferenceEquals(v.Node, node)) return v;
        return null;
    }

    /// <summary>Chooser button: opens the path view of that instance and closes the strip.</summary>
    [RelayCommand]
    private void PickPidCandidate(PidCandidateViewModel? c)
    {
        if (c == null) return;
        DismissPidChooser();
        OpenPath(c.Node);
    }

    /// <summary>Chooser × (also cleared by the next PID click).</summary>
    [RelayCommand]
    private void DismissPidChooser()
    {
        PidCandidates.Clear();
        PidChooserTitle = "";
    }

    /// <summary>A PID cell of a details card row grid: resolved with the row's source CSV and time (and its CreateTime cell, for memory rows).</summary>
    public void SelectPidFromCard(int pid, ProcessActivityRow? row, IReadOnlyList<string>? headers)
    {
        DateTime? create = row != null && headers != null ? PidLinks.RowCreateTime(headers, row.Cells) : null;
        var child = Selected?.Node is { } sel && sel.Ppid == pid && !(row != null && headers != null && PidLinks.RowChildPid(headers, row.Cells, pid) is { } cp && cp != sel.Pid) ? sel : null;
        SelectPid(pid, row?.Time, row?.Origin?.CsvPath, create, child);
    }
}
