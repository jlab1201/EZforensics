namespace EzForensics.Core.Processes;

/// <summary>One item of a sibling row after stacking: a single sibling (<see cref="Single"/>) or a stack of same-name siblings (<see cref="Members"/>, in sibling order).</summary>
public sealed class SiblingItem<T> where T : class
{
    private SiblingItem(T? single, string name, IReadOnlyList<T> members)
    {
        Single = single;
        Name = name;
        Members = members;
    }

    public static SiblingItem<T> One(T item) => new(item, "", Array.Empty<T>());
    public static SiblingItem<T> Stack(string name, IReadOnlyList<T> members) => new(null, name, members);

    public T? Single { get; }
    public bool IsStack => Single == null;
    /// <summary>The shared image name (as the first member spells it); empty for a single item.</summary>
    public string Name { get; }
    public IReadOnlyList<T> Members { get; }
}

/// <summary>
/// Same-name sibling stacks (pure, no WPF): under one real parent (or at root level) the siblings that share an image name (case-insensitive) collapse into one
/// stack item when at least <see cref="Threshold"/> of them are visible. Pinned siblings (flagged, indicator, group member, search match) are never
/// stacked; they stay as separate items in their own place. A stack needs at least two unpinned members and sits where its first member was. It is a view
/// construct only: every member keeps its real parent edge.
/// </summary>
public static class SiblingStacks
{
    /// <summary>Minimum number of visible same-name siblings (pinned ones included) for a stack.</summary>
    public const int Threshold = 3;

    /// <param name="siblings">Visible siblings in display order.</param>
    /// <param name="name">Image name of a sibling; empty means "never stack" (ghost parents, unknown names).</param>
    /// <param name="pinned">Siblings that must stay separate.</param>
    /// <param name="expanded">Names (any case) whose stack the user expanded: those siblings are not stacked.</param>
    public static IReadOnlyList<SiblingItem<T>> Group<T>(IReadOnlyList<T> siblings, Func<T, string> name, Func<T, bool> pinned,
        Func<string, bool>? expanded = null, int threshold = Threshold) where T : class
    {
        var result = new List<SiblingItem<T>>(siblings.Count);
        if (siblings.Count < threshold)
        {
            foreach (var s in siblings) result.Add(SiblingItem<T>.One(s));
            return result;
        }
        var byName = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in siblings)
        {
            var n = name(s);
            if (string.IsNullOrEmpty(n)) continue;
            counts[n] = counts.GetValueOrDefault(n) + 1;
            if (pinned(s)) continue;
            if (!byName.TryGetValue(n, out var l)) byName[n] = l = new List<T>();
            l.Add(s);
        }
        var stackOf = new Dictionary<T, string>(ReferenceEqualityComparer.Instance);
        foreach (var (n, members) in byName)
        {
            if (counts[n] < threshold || members.Count < 2 || (expanded != null && expanded(n))) continue;
            foreach (var m in members) stackOf[m] = n;
        }
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in siblings)
        {
            if (!stackOf.TryGetValue(s, out var n)) { result.Add(SiblingItem<T>.One(s)); continue; }
            if (!emitted.Add(n)) continue;
            var members = byName[n];
            result.Add(SiblingItem<T>.Stack(name(members[0]), members));
        }
        return result;
    }

    /// <summary>The siblings of <paramref name="siblings"/> that <see cref="Group"/> would stack together with <paramref name="target"/>, or empty when it stays separate.</summary>
    public static IReadOnlyList<T> StackContaining<T>(IReadOnlyList<T> siblings, T target, Func<T, string> name, Func<T, bool> pinned,
        Func<string, bool>? expanded = null, int threshold = Threshold) where T : class
    {
        foreach (var item in Group(siblings, name, pinned, expanded, threshold))
            if (item.IsStack && item.Members.Contains(target)) return item.Members;
        return Array.Empty<T>();
    }
}
