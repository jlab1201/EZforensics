namespace EzForensics.Core.Processes;

/// <summary>Result of <see cref="ProcessGraphBuilder"/>: the forest of processes plus what was read.</summary>
public sealed class ProcessGraph
{
    /// <summary>Nodes without a parent in the graph, sorted by creation time.</summary>
    public List<ProcessNode> Roots { get; } = new();
    /// <summary>Every real process instance. Ghost parents (<see cref="ProcessNode.IsGhost"/>) are NOT in this list: they are in <see cref="Ghosts"/> and, as roots, in <see cref="Roots"/>.</summary>
    public List<ProcessNode> AllNodes { get; } = new();
    /// <summary>Synthetic parents of orphans, one per missing parent PID.</summary>
    public List<ProcessNode> Ghosts { get; } = new();
    /// <summary>Number of rows read per source (before merging).</summary>
    public Dictionary<ProcessSources, int> SourceCounts { get; } = new();
    /// <summary>Files that contributed rows.</summary>
    public List<string> SourceFiles { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>Event-log rows and disk artifacts of the host (never null; may hold nothing).</summary>
    public ProcessEvidence Evidence { get; set; } = new();

    public bool IsEmpty => AllNodes.Count == 0;
}
