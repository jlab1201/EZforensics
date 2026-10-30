using System.Globalization;

namespace EzForensics.App.Controls;

/// <summary>One role's share of a region (<see cref="Key"/> is the Brush.Role.{Key} colour key).</summary>
public readonly record struct RegionRole(string Text, string Key, int Count);

/// <summary>
/// One region of the zoomed-out process graph: the drawn subtrees hanging off a boot-chain <see cref="Parent"/> (its non-boot children and everything under
/// them), one <see cref="Groups"/> entry per subtree (head first).
/// <see cref="Orphaned"/>: the one region of every tree whose root is not boot chain (its parent was not captured); <see cref="Parent"/> is the first such root.
/// </summary>
public sealed record ProcessRegion<T>(T Parent, IReadOnlyList<IReadOnlyList<T>> Groups, int Processes, bool Orphaned = false)
{
    /// <summary>Every item of every group.</summary>
    public IReadOnlyList<T> Items => Groups.SelectMany(g => g).ToList();
}

/// <summary>
/// Google-Maps style semantic zoom of the process graph (pure maths, no WPF elements): which drawn items are the boot chain (always shown) and which subtrees
/// are grouped into a region per boot-chain parent, plus the region's role-based label. Computed with a layout, never on zoom / pan.
/// </summary>
public static class ProcessRegions
{
    /// <summary>Image names of the Windows boot / logon chain (case-insensitive).</summary>
    public static readonly IReadOnlySet<string> BootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "userinit.exe", "explorer.exe",
    };
    /// <summary>Without any boot-chain name in the drawn tree, the top layers (depth 0..2) stand in for it.</summary>
    public const int FallbackDepth = 2;
    /// <summary>The second role is shown too when its count is at least this share of the top one.</summary>
    public const double CloseShare = 0.8;

    public static bool IsBootName(string? name) => !string.IsNullOrEmpty(name) && BootNames.Contains(name);

    /// <summary>
    /// The boot chain of a drawn forest: an item whose name is a boot name and whose parent is boot chain (or that is a root). Without a single match anywhere,
    /// every item at depth ≤ <see cref="FallbackDepth"/>.
    /// </summary>
    public static HashSet<T> BootChain<T>(IEnumerable<T> roots, Func<T, IReadOnlyList<T>> children, Func<T, string?> name) where T : notnull
    {
        var rootList = roots.ToList();
        var boot = new HashSet<T>();
        var stack = new Stack<T>();
        foreach (var r in rootList) if (IsBootName(name(r))) { boot.Add(r); stack.Push(r); }
        while (stack.Count > 0)
            foreach (var c in children(stack.Pop()))
                if (IsBootName(name(c)) && boot.Add(c)) stack.Push(c);
        if (boot.Count > 0 || AnyBootName(rootList, children, name)) return boot;
        var level = new Stack<(T Item, int Depth)>();
        foreach (var r in rootList) level.Push((r, 0));
        while (level.Count > 0)
        {
            var (item, depth) = level.Pop();
            if (!boot.Add(item) || depth >= FallbackDepth) continue;
            foreach (var c in children(item)) level.Push((c, depth + 1));
        }
        return boot;
    }

    private static bool AnyBootName<T>(IEnumerable<T> roots, Func<T, IReadOnlyList<T>> children, Func<T, string?> name)
    {
        var stack = new Stack<T>(roots);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (IsBootName(name(i))) return true;
            foreach (var c in children(i)) stack.Push(c);
        }
        return false;
    }

    /// <summary>
    /// One region per boot-chain parent: all drawn items of the subtrees of its children that are not boot chain. <see cref="ProcessRegion{T}.Processes"/> sums
    /// <paramref name="weight"/> (a stack counts as its members); regions with fewer than 2 processes are dropped. Plus one <see cref="ProcessRegion{T}.Orphaned"/>
    /// region of all trees whose root is not boot chain (one group per tree; kept from 1 process). Largest first.
    /// </summary>
    public static List<ProcessRegion<T>> Build<T>(IEnumerable<T> roots, Func<T, IReadOnlyList<T>> children, ISet<T> boot, Func<T, int> weight) where T : notnull
    {
        var regions = new List<ProcessRegion<T>>();
        var stack = new Stack<T>(roots.Where(boot.Contains));
        var seen = new HashSet<T>();
        while (stack.Count > 0)
        {
            var parent = stack.Pop();
            if (!seen.Add(parent)) continue;
            var groups = new List<IReadOnlyList<T>>();
            int processes = 0;
            foreach (var c in children(parent))
            {
                if (boot.Contains(c)) { stack.Push(c); continue; }
                var items = new List<T>();
                groups.Add(items);
                var sub = new Stack<T>();
                sub.Push(c);
                while (sub.Count > 0)
                {
                    var i = sub.Pop();
                    items.Add(i);
                    processes += weight(i);
                    foreach (var k in children(i)) sub.Push(k);
                }
            }
            if (processes >= 2) regions.Add(new ProcessRegion<T>(parent, groups, processes));
        }
        var orphanGroups = new List<IReadOnlyList<T>>();
        int orphans = 0;
        T? first = default;
        foreach (var root in roots.Where(r => !boot.Contains(r)))
        {
            if (orphanGroups.Count == 0) first = root;
            var items = new List<T>();
            var sub = new Stack<T>();
            sub.Push(root);
            while (sub.Count > 0)
            {
                var i = sub.Pop();
                items.Add(i);
                orphans += weight(i);
                foreach (var k in children(i)) sub.Push(k);
            }
            orphanGroups.Add(items);
        }
        if (orphans >= 1) regions.Add(new ProcessRegion<T>(first!, orphanGroups, orphans, Orphaned: true));
        return regions.OrderByDescending(r => r.Processes).ToList();
    }

    /// <summary>Roots in display order with the boot-chain roots first and every other (orphaned) root after them, each part in its own order; unchanged without a boot root.</summary>
    public static List<T> BootFirst<T>(IEnumerable<T> roots, Func<T, bool> isBootRoot)
    {
        var list = roots.ToList();
        return list.Any(isBootRoot) ? list.Where(isBootRoot).Concat(list.Where(r => !isBootRoot(r))).ToList() : list;
    }

    /// <summary>The most common role, plus the runner-up when it is within 20 % of it (most common first; empty without roles).</summary>
    public static IReadOnlyList<RegionRole> Dominant(IEnumerable<RegionRole> roles)
    {
        var sorted = roles.Where(r => r.Count > 0).OrderByDescending(r => r.Count).ThenBy(r => r.Text, StringComparer.Ordinal).Take(2).ToList();
        if (sorted.Count == 2 && sorted[1].Count < sorted[0].Count * CloseShare) sorted.RemoveAt(1);
        return sorted;
    }
}
