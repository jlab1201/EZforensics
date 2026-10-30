namespace EzForensics.Core.Processes;

public enum PidResolutionKind { Found, Ambiguous, NotFound }

/// <summary>Outcome of <see cref="ProcessPidResolver.Resolve"/>: one node (Found), several candidates (Ambiguous: the analyst picks) or none.</summary>
public sealed record PidResolution(PidResolutionKind Kind, IReadOnlyList<ProcessNode> Nodes);

/// <summary>Maps a PID seen in an evidence row to process instance(s) of the host graph. Facts only: never picks between instances without a fact that tells them apart.</summary>
public static class ProcessPidResolver
{
    private const ProcessSources MemorySources = ProcessSources.PsList | ProcessSources.PsScan | ProcessSources.PsTree;

    /// <summary>A Volatility plugin CSV of a host (<c>Memory_*.csv</c>).</summary>
    public static bool IsMemoryCsv(string? path) =>
        !string.IsNullOrEmpty(path) && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(path).StartsWith("Memory_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The merged process list of a host (<c>&lt;host&gt;\Processes\Processes.csv</c>): one row is exactly one process instance.</summary>
    public static bool IsProcessListCsv(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.GetFileName(path).Equals(ProcessCorrelator.ListCsv, StringComparison.OrdinalIgnoreCase)) return false;
        var dir = Path.GetFileName(Path.GetDirectoryName(path)?.TrimEnd('\\', '/'));
        return string.Equals(dir, ProcessCorrelator.ListFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 0. The merged process list selects the instance with that PID and exactly that CreateTime (to the second; a blank CreateTime matches only instances without one);
    /// several or none left: ambiguous, never a guess.
    /// 1. A memory plugin CSV selects the instance seen in memory (pslist / psscan / pstree) with that PID; several: the one whose CreateTime equals <paramref name="rowCreateTime"/> (to the second).
    /// 2. Otherwise a row time selects the instances whose known lifetime contains it (CreateTime &lt;= t &lt;= ExitTime, open end allowed; no CreateTime never contains).
    /// 3. Otherwise a single instance with that PID is the result.
    /// 4. Otherwise all candidates are returned as ambiguous. Ghost nodes are never results.
    /// </summary>
    public static PidResolution Resolve(IEnumerable<ProcessNode> nodes, int pid, DateTime? rowTime, string? sourceCsvPath, DateTime? rowCreateTime = null)
    {
        var all = nodes.Where(n => !n.IsGhost && n.Pid == pid).ToList();
        if (all.Count == 0) return new PidResolution(PidResolutionKind.NotFound, Array.Empty<ProcessNode>());

        if (IsProcessListCsv(sourceCsvPath))
        {
            var same = all.Where(n => rowCreateTime is { } rc ? n.CreateTime is { } c && SameSecond(c, rc) : n.CreateTime == null).ToList();
            if (same.Count == 1) return Found(same[0]);
            return new PidResolution(PidResolutionKind.Ambiguous, same.Count > 1 ? same : all);
        }

        if (IsMemoryCsv(sourceCsvPath))
        {
            var mem = all.Where(n => (n.Sources & MemorySources) != 0).ToList();
            if (mem.Count == 1) return Found(mem[0]);
            if (mem.Count > 1)
            {
                if (rowCreateTime is { } ct)
                {
                    var eq = mem.Where(n => n.CreateTime is { } c && SameSecond(c, ct)).ToList();
                    if (eq.Count == 1) return Found(eq[0]);
                }
                return new PidResolution(PidResolutionKind.Ambiguous, mem);
            }
        }

        if (rowTime is { } t)
        {
            var inside = all.Where(n => n.CreateTime is { } c && c <= t && (n.ExitTime is not { } x || t <= x)).ToList();
            if (inside.Count == 1) return Found(inside[0]);
            if (inside.Count > 1) return new PidResolution(PidResolutionKind.Ambiguous, inside);
        }

        return all.Count == 1 ? Found(all[0]) : new PidResolution(PidResolutionKind.Ambiguous, all);
    }

    private static PidResolution Found(ProcessNode n) => new(PidResolutionKind.Found, new[] { n });

    private static bool SameSecond(DateTime a, DateTime b) => a.Ticks / TimeSpan.TicksPerSecond == b.Ticks / TimeSpan.TicksPerSecond;
}
