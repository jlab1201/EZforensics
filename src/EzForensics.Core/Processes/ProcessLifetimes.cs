namespace EzForensics.Core.Processes;

/// <summary>When a row time must fall relative to a process for a <see cref="LinkTier.PathTime"/> link.</summary>
public enum TimeRole
{
    /// <summary>The row records the launch (Prefetch run time, Task Scheduler 200 / 201): <c>CreateTime + MinSec &lt;= t &lt;= CreateTime + MaxSec</c>.</summary>
    Start,
    /// <summary>The row happened while the process existed (Defender detection): <c>CreateTime &lt;= t &lt;= end</c> (see <see cref="ProcessLifetimes"/>).</summary>
    During,
}

/// <summary>A source's time rule: the role and, for <see cref="TimeRole.Start"/>, the accepted offsets from the process start in seconds.</summary>
public sealed record TimeFit(TimeRole Role, double MinSec = -2, double MaxSec = 15)
{
    public static readonly TimeFit During = new(TimeRole.During, 0, 0);
}

/// <summary>
/// Lifetimes of the process instances of one graph: decides, on facts only, whether a PID or a (full path, time) pair names a given instance.
/// <see cref="PidFits"/> checks a reused PID against the instance lifetime; <see cref="IsUniquePathTime"/> is true only when the path matches, the time fits
/// and no other instance with the same path also fits (an ambiguous match is never linked).
/// </summary>
public sealed class ProcessLifetimes
{
    /// <summary>Tolerance around a lifetime for PID rows (clock and logging granularity).</summary>
    public const int SlackSeconds = 2;

    private readonly Dictionary<string, List<ProcessNode>> _byPath = new();
    private readonly Dictionary<int, List<ProcessNode>> _byPid = new();
    // the next instance of a node's PID / path is looked up once per node: IsUniquePathTime calls TimeFits for every instance of a path on every evidence row
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProcessNode, DateTime?> _nextByPid = new(ReferenceEqualityComparer.Instance);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProcessNode, DateTime?> _nextByPath = new(ReferenceEqualityComparer.Instance);

    /// <summary>When memory was acquired (or the latest evidence time) when known: the end of an instance without exit time. Null: the next instance with the same path ends it.</summary>
    public DateTime? EvidenceEnd { get; }

    public ProcessLifetimes(IEnumerable<ProcessNode> nodes, DateTime? evidenceEnd = null)
    {
        EvidenceEnd = evidenceEnd;
        foreach (var n in nodes)
        {
            var key = PathKeyOf(n);
            if (key.Length > 0) Add(_byPath, key, n);
            if (n.Pid > 0) Add(_byPid, n.Pid, n);
        }
    }

    private static void Add<K>(Dictionary<K, List<ProcessNode>> d, K key, ProcessNode n) where K : notnull
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<ProcessNode>();
        l.Add(n);
    }

    public static string PathKeyOf(ProcessNode n) => ProcessLogText.PathKey(ProcessLogText.EffectivePath(n));

    /// <summary>
    /// True when a row of this PID at <paramref name="t"/> belongs to <paramref name="node"/>: the time is inside the lifetime (start - 2 s, exit + 2 s, or up to the next
    /// instance of the same PID). A row without time, or a node without start time, has nothing to compare and the PID stands.
    /// </summary>
    public bool PidFits(ProcessNode node, DateTime? t)
    {
        if (t == null || node.CreateTime == null) return true;
        if (t < node.CreateTime.Value.AddSeconds(-SlackSeconds)) return false;
        if (node.ExitTime != null) return t <= node.ExitTime.Value.AddSeconds(SlackSeconds);
        var next = _nextByPid.GetOrAdd(node, n => NextInstance(_byPid.GetValueOrDefault(n.Pid), n));
        return next == null || t < next.Value.AddSeconds(-SlackSeconds);
    }

    /// <summary>Does <paramref name="t"/> fit this instance under <paramref name="fit"/>? Needs a start time.</summary>
    public bool TimeFits(ProcessNode node, DateTime t, TimeFit fit)
    {
        if (node.CreateTime == null) return false;
        var create = node.CreateTime.Value;
        if (fit.Role == TimeRole.Start) return t >= create.AddSeconds(fit.MinSec) && t <= create.AddSeconds(fit.MaxSec);
        if (t < create) return false;
        if (node.ExitTime != null) return t <= node.ExitTime.Value;
        if (EvidenceEnd != null) return t <= EvidenceEnd.Value;
        var next = _nextByPath.GetOrAdd(node, n => NextInstance(_byPath.GetValueOrDefault(PathKeyOf(n)), n));
        return next == null || t < next.Value;
    }

    /// <summary>
    /// PathTime rule: <paramref name="node"/> has a full path, <paramref name="t"/> fits it, and no other instance with the same path fits (or has an unknown start, which
    /// cannot be ruled out).
    /// </summary>
    public bool IsUniquePathTime(ProcessNode node, DateTime t, TimeFit fit)
    {
        var key = PathKeyOf(node);
        if (key.Length == 0 || !TimeFits(node, t, fit)) return false;
        if (!_byPath.TryGetValue(key, out var same)) return true;
        foreach (var other in same)
        {
            if (ReferenceEquals(other, node)) continue;
            if (other.CreateTime == null || TimeFits(other, t, fit)) return false;
        }
        return true;
    }

    /// <summary>Start of the earliest instance in <paramref name="group"/> that begins after <paramref name="node"/>.</summary>
    private static DateTime? NextInstance(List<ProcessNode>? group, ProcessNode node)
    {
        DateTime? best = null;
        if (group == null || node.CreateTime == null) return null;
        foreach (var o in group)
        {
            if (ReferenceEquals(o, node) || o.CreateTime == null || o.CreateTime <= node.CreateTime) continue;
            if (best == null || o.CreateTime < best) best = o.CreateTime;
        }
        return best;
    }
}
