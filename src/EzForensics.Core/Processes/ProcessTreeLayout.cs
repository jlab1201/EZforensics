namespace EzForensics.Core.Processes;

/// <summary>Position of one laid-out tree item (top-left corner, in layout units).</summary>
public readonly record struct TreeLayoutPoint(double X, double Y);

/// <summary>Result of <see cref="ProcessTreeLayout.Compute{T}"/>: item positions, parent → child edges and the total extent.</summary>
public sealed class TreeLayoutResult<T> where T : notnull
{
    public Dictionary<T, TreeLayoutPoint> Positions { get; } = new();
    public List<(T Parent, T Child)> Edges { get; } = new();
    public double Width { get; internal set; }
    public double Height { get; internal set; }
    /// <summary><see cref="ProcessTreeLayout.Update{T}"/>: the items that had no previous position, in visiting order.</summary>
    public List<T> Added { get; } = new();
}

/// <summary>
/// Left-to-right tidy tree layout (pure, no WPF): depth decides the column, leaves take consecutive rows in visiting order and every parent is centred
/// on the span of its children, so subtrees never overlap. All items have the same size (<paramref name="nodeWidth"/> × <paramref name="nodeHeight"/>).
/// </summary>
public static class ProcessTreeLayout
{
    /// <param name="roots">Top-level items, in display order.</param>
    /// <param name="children">Visible children of an expanded item (empty for a collapsed one), in display order.</param>
    /// <param name="groupStart">A root that starts a separate group of roots (e.g. the orphaned trees after the boot tree): <paramref name="groupGap"/> extra rows before it (not before the first root).</param>
    public static TreeLayoutResult<T> Compute<T>(IEnumerable<T> roots, Func<T, IReadOnlyList<T>> children,
        double nodeWidth, double nodeHeight, double horizontalGap, double verticalGap, double rootGap = 0, Func<T, bool>? groupStart = null, double groupGap = 0) where T : notnull
    {
        var result = new TreeLayoutResult<T>();
        double nextY = 0, maxX = 0;
        bool first = true;
        // iterative post-order (deep trees must not overflow the stack)
        foreach (var root in roots)
        {
            if (!first) nextY += rootGap + (groupStart?.Invoke(root) == true ? groupGap : 0);
            first = false;
            var stack = new Stack<(T Item, int Depth, int Next)>();
            stack.Push((root, 0, 0));
            var visiting = new HashSet<T> { root };
            while (stack.Count > 0)
            {
                var (item, depth, next) = stack.Pop();
                var kids = children(item);
                if (next < kids.Count)
                {
                    stack.Push((item, depth, next + 1));
                    var kid = kids[next];
                    if (visiting.Add(kid) && !result.Positions.ContainsKey(kid)) stack.Push((kid, depth + 1, 0));
                    continue;
                }
                double x = depth * (nodeWidth + horizontalGap);
                double y;
                var placed = kids.Where(result.Positions.ContainsKey).ToList();
                if (placed.Count == 0)
                {
                    y = nextY;
                    nextY += nodeHeight + verticalGap;
                }
                else
                {
                    y = (result.Positions[placed[0]].Y + result.Positions[placed[^1]].Y) / 2;
                    foreach (var k in placed) result.Edges.Add((item, k));
                }
                result.Positions[item] = new TreeLayoutPoint(x, y);
                maxX = Math.Max(maxX, x + nodeWidth);
            }
        }
        result.Width = maxX;
        result.Height = result.Positions.Count == 0 ? 0 : Math.Max(0, nextY - verticalGap);
        return result;
    }

    /// <summary>
    /// Incremental layout (expand / collapse): items move only to avoid an overlap, and then only down, never above their base row (<paramref name="baseRows"/>:
    /// the row of the last full layout, or of the item's first placement; null = the previous row), so a collapse puts everything an expand pushed back. The drawn tree is walked in order (roots, then each
    /// item's children, depth-first). Every item has a minimum row, the frontier: the bottom row of every subtree finished before it + <paramref name="rowStep"/>
    /// (so a later sibling starts below the previous sibling's subtree; a later root also adds <paramref name="rootGap"/>; the first root 0); a new first child
    /// also starts no higher than its parent's row. An item with a <paramref name="previous"/> position keeps its x and takes max(its old row, the minimum); a
    /// new item takes the minimum, in its parent's child column (parent x + <paramref name="columnStep"/>). A tidy <see cref="Compute{T}"/> layout already
    /// meets every minimum, so an unchanged tree does not move. So an expanded subtree pushes everything below it down (later siblings, the ancestors' later
    /// siblings, later roots) while the expanded item and everything above stay put; a collapse leaves gaps (nothing moves up). A new root with a drawn child
    /// (an ancestor revealed above the drawn chain) goes one column left of that child, at the child's row (at least the minimum).
    /// </summary>
    public static TreeLayoutResult<T> Update<T>(IEnumerable<T> roots, Func<T, IReadOnlyList<T>> children, IReadOnlyDictionary<T, TreeLayoutPoint> previous,
        double columnStep, double rowStep, double rootGap = 0, IReadOnlyDictionary<T, double>? baseRows = null, Func<T, bool>? groupStart = null, double groupGap = 0) where T : notnull
    {
        var result = new TreeLayoutResult<T>();
        var seen = new HashSet<T>();
        double maxX = 0, maxY = 0;
        double? lastRootX = null;

        TreeLayoutPoint Place(T item, double min, TreeLayoutPoint? parent, IReadOnlyList<T> kids)
        {
            TreeLayoutPoint p;
            double baseRow = double.NegativeInfinity;
            bool hasBase = baseRows != null && baseRows.TryGetValue(item, out baseRow);
            if (previous.TryGetValue(item, out var old)) p = new TreeLayoutPoint(old.X, Math.Max(hasBase ? baseRow : old.Y, min));
            else
            {
                if (hasBase) min = Math.Max(min, baseRow);   // a returning item (a collapsed stack) goes back to its own row
                TreeLayoutPoint? kept = null;
                foreach (var k in kids) if (previous.TryGetValue(k, out var kp)) { kept = kp; break; }
                double x = parent is { } pp ? pp.X + columnStep : kept is { } kx ? kx.X - columnStep : lastRootX ?? 0;
                p = new TreeLayoutPoint(x, kept is { } ky && parent == null ? Math.Max(min, ky.Y) : min);
                result.Added.Add(item);
            }
            result.Positions[item] = p;
            maxX = Math.Max(maxX, p.X + columnStep);
            maxY = Math.Max(maxY, p.Y);
            return p;
        }

        // iterative depth-first walk (deep trees must not overflow the stack); Bottom = lowest row of the subtree so far
        var stack = new Stack<(T Item, TreeLayoutPoint Pos, IReadOnlyList<T> Kids, int Next, double Bottom)>();
        double frontier = double.NegativeInfinity;
        bool firstRoot = true;
        foreach (var root in roots)
        {
            if (!seen.Add(root)) continue;
            var rootKids = children(root);
            if (!firstRoot && groupStart?.Invoke(root) == true) frontier += groupGap;
            var rp = Place(root, firstRoot ? Math.Max(0, frontier) : frontier, null, rootKids);
            firstRoot = false;
            lastRootX = rp.X;
            stack.Push((root, rp, rootKids, 0, rp.Y));
            double rootBottom = rp.Y;
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                if (f.Next < f.Kids.Count)
                {
                    var kid = f.Kids[f.Next];
                    stack.Push(f with { Next = f.Next + 1 });
                    if (!seen.Add(kid)) continue;
                    var kk = children(kid);
                    double min = previous.ContainsKey(kid) || f.Next > 0 || baseRows?.ContainsKey(kid) == true ? frontier : Math.Max(frontier, f.Pos.Y);
                    var kp = Place(kid, min, f.Pos, kk);
                    result.Edges.Add((f.Item, kid));
                    stack.Push((kid, kp, kk, 0, kp.Y));
                    continue;
                }
                // subtree done: hand its bottom to the parent frame
                if (stack.Count == 0) { rootBottom = f.Bottom; break; }
                frontier = Math.Max(frontier, f.Bottom + rowStep);
                var parentFrame = stack.Pop();
                stack.Push(parentFrame with { Bottom = Math.Max(parentFrame.Bottom, f.Bottom) });
            }
            frontier = Math.Max(frontier, rootBottom + rowStep + rootGap);
        }
        result.Width = maxX;
        result.Height = maxY;
        return result;
    }
}
