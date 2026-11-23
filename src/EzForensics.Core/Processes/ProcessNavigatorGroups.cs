using System.Globalization;

namespace EzForensics.Core.Processes;

public enum NavigatorSection { Indicators, DataGaps, Roles, Sessions }

/// <summary>One process in a Navigator group; <see cref="Evidence"/> is the indicator / data gap text (empty for roles and sessions).</summary>
public sealed record NavigatorMember(ProcessNode Node, string Evidence);

/// <summary>One Navigator group: all members are real processes (ghost parents are never members). <see cref="Key"/> is stable and free of spaces.</summary>
public sealed record NavigatorGroup(NavigatorSection Section, string Key, string Title, IReadOnlyList<NavigatorMember> Members, int FlaggedCount)
{
    public int Count => Members.Count;
}

/// <summary>The four Navigator sections of a graph (empty groups are never created).</summary>
public sealed record NavigatorGroups(
    IReadOnlyList<NavigatorGroup> Indicators, IReadOnlyList<NavigatorGroup> DataGaps,
    IReadOnlyList<NavigatorGroup> Roles, IReadOnlyList<NavigatorGroup> Sessions);

/// <summary>Groups the processes of a graph for the Process tree Navigator: indicators by kind, data gaps, roles, sessions &amp; users. Pure; ghosts are excluded.</summary>
public static class ProcessNavigatorGroups
{
    /// <summary>Markers that make a process "flagged" (Only flagged filter, auto-expand, flagged counts).</summary>
    public const ProcessMarkers FlagMarkers = ProcessMarkers.Injected | ProcessMarkers.Hidden | ProcessMarkers.SuspiciousParent
        | ProcessMarkers.Orphan | ProcessMarkers.ParentPidReused | ProcessMarkers.Hollowed | ProcessMarkers.Ghosted | ProcessMarkers.PebMasquerade
        | ProcessMarkers.SuspiciousThreads;

    public static bool IsFlagged(ProcessNode n) => (n.Markers & FlagMarkers) != 0;

    public static string KindTitle(IndicatorKind k) => k switch
    {
        IndicatorKind.SuspiciousParent => "Suspicious parent",
        IndicatorKind.Injected => "Injected code",
        IndicatorKind.Hollowed => "Hollowed process",
        IndicatorKind.Ghosted => "Ghosted process",
        IndicatorKind.PebMasquerade => "PEB masquerade",
        IndicatorKind.SuspiciousThreads => "Suspicious threads",
        IndicatorKind.Hidden => "Hidden (psscan only)",
        IndicatorKind.ParentPidReused => "Parent PID reused",
        IndicatorKind.UnexpectedLocation => "Unexpected location",
        IndicatorKind.DuplicateSingleton => "Duplicate singleton",
        IndicatorKind.UnexpectedSession => "Unexpected session",
        IndicatorKind.EncodedCommand => "Encoded command",
        _ => k.ToString(),
    };

    public static string GapTitle(DataGapKind k) => k switch
    {
        DataGapKind.Orphan => "Orphans (parent not captured)",
        DataGapKind.FromLogsOnly => "From logs only",
        _ => k.ToString(),
    };

    /// <summary>The Orphans title also says how many processes their trees hold (as the canvas's Orphaned region counts them).</summary>
    private static string GapTitle(DataGapKind k, IEnumerable<ProcessNode> members)
    {
        if (k != DataGapKind.Orphan) return GapTitle(k);
        var roots = members.Distinct().ToList();
        var seen = new HashSet<ProcessNode>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<ProcessNode>(roots);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsGhost || !seen.Add(n)) continue;
            foreach (var c in n.Children) stack.Push(c);
        }
        return seen.Count > roots.Count ? $"{GapTitle(k)} · {seen.Count:N0} processes in their trees" : GapTitle(k);
    }

    public static NavigatorGroups Build(ProcessGraph graph)
    {
        var real = graph.AllNodes.Where(n => !n.IsGhost).ToList();

        var indicators = ProcessIndicators.Evaluate(graph).Where(i => !i.Node.IsGhost)
            .GroupBy(i => i.Kind).OrderBy(g => g.Key)
            .Select(g => Make(NavigatorSection.Indicators, g.Key.ToString(), KindTitle(g.Key), g.Select(i => new NavigatorMember(i.Node, i.Evidence))))
            .ToList();

        var gaps = ProcessIndicators.DataGaps(graph).Where(i => !i.Node.IsGhost)
            .GroupBy(i => i.Kind).OrderBy(g => g.Key)
            .Select(g => Make(NavigatorSection.DataGaps, g.Key.ToString(), GapTitle(g.Key, g.Select(i => i.Node)), g.Select(i => new NavigatorMember(i.Node, i.Text))))
            .ToList();

        var roles = real.Where(n => n.Role.Length > 0).GroupBy(n => n.Role)
            .OrderBy(g => g.Key == ProcessRoles.OtherRole ? 1 : 0).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => Make(NavigatorSection.Roles, Slug(g.Key), g.Key, g.Select(n => new NavigatorMember(n, "")), flaggedFirst: true))
            .ToList();

        var sessions = real.GroupBy(n => (n.SessionId, User: n.User.Trim()))
            .OrderBy(g => g.Key.SessionId ?? int.MaxValue).ThenBy(g => g.Key.User, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var s = g.Key.SessionId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "?";
                var u = g.Key.User.Length > 0 ? g.Key.User : "(unknown user)";
                return Make(NavigatorSection.Sessions, Slug(s + "_" + u), $"Session {s} · {u}", g.Select(n => new NavigatorMember(n, "")), flaggedFirst: true);
            })
            .ToList();

        return new NavigatorGroups(indicators, gaps, UniqueKeys(roles), UniqueKeys(sessions));
    }

    /// <summary>Slug() can map different titles to one key ("DOM\bob" / "DOM_bob"); keys must stay unique (AutomationIds, expand state).</summary>
    private static List<NavigatorGroup> UniqueKeys(List<NavigatorGroup> groups)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < groups.Count; i++)
        {
            var key = groups[i].Key;
            for (int n = 2; !seen.Add(key); n++) key = groups[i].Key + "_" + n.ToString(CultureInfo.InvariantCulture);
            if (key != groups[i].Key) groups[i] = groups[i] with { Key = key };
        }
        return groups;
    }

    private static NavigatorGroup Make(NavigatorSection section, string key, string title, IEnumerable<NavigatorMember> members, bool flaggedFirst = false)
    {
        // one row per process (a process can carry the same kind twice only by construction mistakes); creation order (roles and sessions: flagged first)
        var list = members.GroupBy(m => m.Node).Select(g => g.First())
            .OrderBy(m => flaggedFirst && IsFlagged(m.Node) ? 0 : 1)
            .ThenBy(m => m.Node.CreateTime ?? DateTime.MaxValue).ThenBy(m => m.Node.Pid).ToList();
        return new NavigatorGroup(section, key, title, list, list.Count(m => IsFlagged(m.Node)));
    }

    /// <summary>Letters and digits only (AutomationId-safe).</summary>
    public static string Slug(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
