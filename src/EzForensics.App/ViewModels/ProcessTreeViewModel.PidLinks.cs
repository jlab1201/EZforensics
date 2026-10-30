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
        Text = $"{name} · started {started}";
    }

    public ProcessNodeViewModel Node { get; }
    public string Text { get; }
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
    public PidResolutionKind? SelectPid(int pid, DateTime? rowTime, string? sourceCsv, DateTime? rowCreateTime)
    {
        DismissPidChooser();
        if (_disposed || _graph == null) return null;
        var r = ProcessPidResolver.Resolve(_all.Select(v => v.Node), pid, rowTime, sourceCsv, rowCreateTime);
        var p = pid.ToString(CultureInfo.InvariantCulture);
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
        SelectPid(pid, row?.Time, row?.Origin?.CsvPath, create);
    }
}
