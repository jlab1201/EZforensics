namespace EzForensics.Core.Processes;

/// <summary>What a Focus view of the process tree shows around the focused process.</summary>
public enum FocusScope
{
    /// <summary>Ancestor chain + the process + its subtree (as expanded).</summary>
    Focus,
    /// <summary>Ancestor chain + the process (card "Trace up").</summary>
    TraceUp,
    /// <summary>The process + its subtree, fully expanded (card "Trace down").</summary>
    TraceDown,
    /// <summary>Ancestor chain + the process + its subtree, fully expanded (card "Lineage").</summary>
    Lineage,
}

/// <summary>Result of <see cref="FocusProjection.Compute{T}"/>.</summary>
public sealed class FocusProjection<T> where T : class
{
    public required T Node { get; init; }
    public required FocusScope Scope { get; init; }
    /// <summary>Ancestors drawn above the node, root first (empty for <see cref="FocusScope.TraceDown"/>).</summary>
    public required IReadOnlyList<T> Ancestors { get; init; }
    /// <summary>For each element of <see cref="Ancestors"/> followed by <see cref="Node"/>: how many real siblings it has that are left out ("+N others"). Empty for <see cref="FocusScope.TraceDown"/>.</summary>
    public required IReadOnlyList<int> Others { get; init; }
    /// <summary>The full ancestor chain (root first) whatever the scope: the breadcrumb.</summary>
    public required IReadOnlyList<T> Breadcrumb { get; init; }
    public bool IncludesSubtree => Scope != FocusScope.TraceUp;
    public bool ExpandsSubtree => Scope is FocusScope.TraceDown or FocusScope.Lineage;
    /// <summary>Every item drawn (ancestors, node and, unless Trace up, the whole subtree under <c>children</c>).</summary>
    public required IReadOnlySet<T> Items { get; init; }
}

/// <summary>
/// Focus projection of a tree (pure, no WPF): the focused node's real ancestor chain, the node and its subtree. Siblings along the chain are not drawn but
/// counted (the "+N others" pills); nothing is invented, the chain is the real parent links.
/// </summary>
public static class FocusProjection
{
    /// <param name="parent">Real parent (null for a root).</param>
    /// <param name="children">Real children, used for the sibling counts and the subtree.</param>
    /// <param name="roots">Top-level items (the siblings of a root).</param>
    public static FocusProjection<T> Compute<T>(T node, Func<T, T?> parent, Func<T, IReadOnlyList<T>> children, IReadOnlyList<T> roots, FocusScope scope)
        where T : class
    {
        var chain = new List<T>();
        var guard = new HashSet<T>(ReferenceEqualityComparer.Instance) { node };
        for (var p = parent(node); p != null && guard.Add(p); p = parent(p)) chain.Add(p);
        chain.Reverse();

        bool up = scope != FocusScope.TraceDown;
        var ancestors = up ? chain : new List<T>();
        var others = new List<int>();
        if (up)
        {
            for (int i = 0; i <= ancestors.Count; i++)
            {
                var item = i < ancestors.Count ? ancestors[i] : node;
                var sibs = i == 0 ? roots : children(ancestors[i - 1]);
                others.Add(Math.Max(0, sibs.Count - (sibs.Contains(item) ? 1 : 0)));
            }
        }
        var items = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var a in ancestors) items.Add(a);
        items.Add(node);
        if (scope != FocusScope.TraceUp)
            foreach (var d in Subtree(node, children)) items.Add(d);
        return new FocusProjection<T> { Node = node, Scope = scope, Ancestors = ancestors, Others = others, Breadcrumb = chain, Items = items };
    }

    /// <summary>
    /// The siblings left out at chain level <paramref name="level"/> of a projection (index into <see cref="FocusProjection{T}.Ancestors"/> followed by the node, as <see cref="FocusProjection{T}.Others"/>):
    /// the children of the level above (<paramref name="roots"/> for level 0) other than the chain item, in their real order. Empty for a bad level or Trace down.
    /// </summary>
    public static IReadOnlyList<T> OtherSiblings<T>(FocusProjection<T> projection, int level, Func<T, IReadOnlyList<T>> children, IReadOnlyList<T> roots) where T : class
    {
        if (level < 0 || level > projection.Ancestors.Count || projection.Others.Count == 0) return Array.Empty<T>();
        var item = level < projection.Ancestors.Count ? projection.Ancestors[level] : projection.Node;
        var sibs = level == 0 ? roots : children(projection.Ancestors[level - 1]);
        return sibs.Where(s => !ReferenceEquals(s, item)).ToList();
    }

    /// <summary>Every descendant of <paramref name="node"/> (not the node itself), depth first; iterative and cycle safe.</summary>
    public static IReadOnlyList<T> Subtree<T>(T node, Func<T, IReadOnlyList<T>> children) where T : class
    {
        var list = new List<T>();
        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance) { node };
        var stack = new Stack<T>();
        var kids = children(node);
        for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n)) continue;
            list.Add(n);
            var k = children(n);
            for (int i = k.Count - 1; i >= 0; i--) stack.Push(k[i]);
        }
        return list;
    }
}
