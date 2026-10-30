namespace EzForensics.Core.Results;

public enum PivotSort { ValueAsc, ValueDesc, CountDesc, CountAsc }

/// <summary>Group sort helpers: a chip click flips the direction, the chip menu switches between value and count.</summary>
public static class PivotSortExtensions
{
    public static bool IsCount(this PivotSort s) => s is PivotSort.CountAsc or PivotSort.CountDesc;
    public static bool IsAscending(this PivotSort s) => s is PivotSort.ValueAsc or PivotSort.CountAsc;

    /// <summary>Same key, opposite direction (value A→Z ↔ Z→A, count smallest ↔ largest first).</summary>
    public static PivotSort Flip(this PivotSort s) => s switch
    {
        PivotSort.ValueAsc => PivotSort.ValueDesc,
        PivotSort.ValueDesc => PivotSort.ValueAsc,
        PivotSort.CountAsc => PivotSort.CountDesc,
        _ => PivotSort.CountAsc,
    };

    /// <summary>Sort by value or by count, keeping the direction.</summary>
    public static PivotSort WithKey(this PivotSort s, bool byCount) => (byCount, s.IsAscending()) switch
    {
        (true, true) => PivotSort.CountAsc,
        (true, false) => PivotSort.CountDesc,
        (false, true) => PivotSort.ValueAsc,
        _ => PivotSort.ValueDesc,
    };
}

public sealed class PivotGroupSpec
{
    public string Column { get; set; } = "";
    public PivotSort Sort { get; set; } = PivotSort.ValueAsc;
}

public sealed class PivotNode
{
    public string Column { get; init; } = "";
    /// <summary>Group value; blank cells give "" (the UI renders "(blank)").</summary>
    public string Value { get; init; } = "";
    public int Count { get; set; }
    public int Depth { get; init; }
    public List<PivotNode> Children { get; } = new();
    /// <summary>Row indices (incoming map order); only set on last-level groups.</summary>
    public int[] Rows { get; set; } = Array.Empty<int>();
    /// <summary>Stable id of the group path, e.g. <c>col=val/col2=val2</c> ('/', '=' and '\' escaped with '\').</summary>
    public string Key { get; init; } = "";
}

/// <summary>A flattened pivot line: a group header (<see cref="Node"/> set) or a data row (<see cref="Row"/>).</summary>
public readonly record struct PivotLine(PivotNode? Node, int Row, int Depth)
{
    public bool IsGroup => Node != null;
}

public static class PivotEngine
{
    private const int ChunkRows = 5000;

    public static Task<IReadOnlyList<PivotNode>> GroupAsync(CsvIndex idx, int[] rows, IReadOnlyList<PivotGroupSpec> groups,
        IProgress<double>? progress, CancellationToken ct)
        => Task.Run(() => Group(idx, rows, groups, progress, ct), ct);

    private static IReadOnlyList<PivotNode> Group(CsvIndex idx, int[] rows, IReadOnlyList<PivotGroupSpec> groups,
        IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cols = new int[groups.Count];
        for (int g = 0; g < cols.Length; g++)
        {
            cols[g] = idx.ColumnIndex(groups[g].Column);
            if (cols[g] < 0) throw new ArgumentException("Unknown column: " + groups[g].Column, nameof(groups));
        }
        if (groups.Count == 0) { progress?.Report(1); return new List<PivotNode>(); }

        int n = rows.Length;
        var keys = new string[cols.Length][];
        for (int g = 0; g < cols.Length; g++) keys[g] = new string[n];

        int chunks = (n + ChunkRows - 1) / ChunkRows;
        long done = 0;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, c =>
        {
            int s = c * ChunkRows, e = Math.Min(n, s + ChunkRows);
            int min = int.MaxValue, max = int.MinValue;
            for (int i = s; i < e; i++) { if (rows[i] < min) min = rows[i]; if (rows[i] > max) max = rows[i]; }
            long span = (long)max - min + 1;
            string[][]? block = null;
            if (e > s && span <= (long)(e - s) * 4 && span <= int.MaxValue) block = idx.ReadRows(min, (int)span);
            for (int i = s; i < e; i++)
            {
                var row = block != null ? block[rows[i] - min] : idx.ReadRow(rows[i]);
                for (int g = 0; g < cols.Length; g++) keys[g][i] = cols[g] < row.Length ? row[cols[g]] : "";
            }
            progress?.Report(0.6 * Interlocked.Add(ref done, e - s) / Math.Max(1, n));
        });
        ct.ThrowIfCancellationRequested();

        var all = new int[n];
        for (int i = 0; i < n; i++) all[i] = i;
        var ctx = new Ctx(rows, keys, groups, ct, progress, n);
        var roots = Build(ctx, all, 0, "");
        progress?.Report(1);
        return roots;
    }

    private sealed class Ctx
    {
        public Ctx(int[] rows, string[][] keys, IReadOnlyList<PivotGroupSpec> groups, CancellationToken ct, IProgress<double>? progress, int total)
        { Rows = rows; Keys = keys; Groups = groups; Ct = ct; Progress = progress; Total = total; }
        public readonly int[] Rows;
        public readonly string[][] Keys;
        public readonly IReadOnlyList<PivotGroupSpec> Groups;
        public readonly CancellationToken Ct;
        public readonly IProgress<double>? Progress;
        public readonly int Total;
        public long Placed;
    }

    private static List<PivotNode> Build(Ctx ctx, int[] positions, int level, string parentKey)
    {
        ctx.Ct.ThrowIfCancellationRequested();
        var spec = ctx.Groups[level];
        var col = ctx.Keys[level];
        bool last = level == ctx.Groups.Count - 1;

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var vals = new List<string>();
        var lists = new List<List<int>>();
        for (int i = 0; i < positions.Length; i++)
        {
            if ((i & 8191) == 0) ctx.Ct.ThrowIfCancellationRequested();
            int p = positions[i];
            var v = col[p];
            if (!map.TryGetValue(v, out int gi)) { gi = vals.Count; map[v] = gi; vals.Add(v); lists.Add(new List<int>()); }
            lists[gi].Add(p);
        }

        var order = SortOrder(vals, lists, spec.Sort);
        var result = new List<PivotNode>(vals.Count);
        foreach (int gi in order)
        {
            var key = (parentKey.Length == 0 ? "" : parentKey + "/") + Esc(spec.Column) + "=" + Esc(vals[gi]);
            var node = new PivotNode { Column = spec.Column, Value = vals[gi], Depth = level, Key = key, Count = lists[gi].Count };
            if (last)
            {
                var pos = lists[gi];
                var r = new int[pos.Count];
                for (int i = 0; i < r.Length; i++) r[i] = ctx.Rows[pos[i]];
                node.Rows = r;
                if (ctx.Progress != null)
                {
                    ctx.Placed += r.Length;
                    ctx.Progress.Report(0.6 + 0.4 * ctx.Placed / Math.Max(1, ctx.Total));
                }
            }
            else
            {
                node.Children.AddRange(Build(ctx, lists[gi].ToArray(), level + 1, key));
            }
            lists[gi] = null!;
            result.Add(node);
        }
        return result;
    }

    private static int[] SortOrder(List<string> vals, List<List<int>> lists, PivotSort sort)
    {
        int m = vals.Count;
        var order = new int[m];
        for (int i = 0; i < m; i++) order[i] = i;

        // value comparison mode: numbers, else dates, else text
        bool allNum = true, allDate = true;
        foreach (var v in vals)
        {
            if (v.Length == 0) continue;
            if (allNum && !CsvValueParser.TryParseNumber(v, out _)) allNum = false;
            if (allDate && !CsvValueParser.TryParseDate(v, out _)) allDate = false;
            if (!allNum && !allDate) break;
        }
        Comparison<int> valueCmp;
        if (allNum)
        {
            var d = new double[m];
            for (int i = 0; i < m; i++) CsvValueParser.TryParseNumber(vals[i], out d[i]);
            valueCmp = (a, b) => d[a].CompareTo(d[b]);
        }
        else if (allDate)
        {
            var d = new long[m];
            for (int i = 0; i < m; i++) d[i] = CsvValueParser.TryParseDate(vals[i], out var t) ? t.Ticks : 0;
            valueCmp = (a, b) => d[a].CompareTo(d[b]);
        }
        else valueCmp = (a, b) => string.Compare(vals[a], vals[b], StringComparison.OrdinalIgnoreCase);

        bool desc = sort == PivotSort.ValueDesc;
        // blanks always last for value sorts; ties fall back to first appearance
        Comparison<int> byValue = (a, b) =>
        {
            bool ba = vals[a].Length == 0, bb = vals[b].Length == 0;
            if (ba || bb) return ba == bb ? 0 : (ba ? 1 : -1);
            int r = valueCmp(a, b);
            return desc ? -r : r;
        };
        Comparison<int> full = sort switch
        {
            PivotSort.CountDesc => (a, b) => { int r = lists[b].Count.CompareTo(lists[a].Count); return r != 0 ? r : Tie(byValue, a, b); },
            PivotSort.CountAsc => (a, b) => { int r = lists[a].Count.CompareTo(lists[b].Count); return r != 0 ? r : Tie(byValue, a, b); },
            _ => (a, b) => Tie(byValue, a, b),
        };
        Array.Sort(order, full);
        return order;
    }

    private static int Tie(Comparison<int> byValue, int a, int b)
    {
        int r = byValue(a, b);
        return r != 0 ? r : a.CompareTo(b);
    }

    private static string Esc(string s)
        => s.IndexOfAny(new[] { '\\', '/', '=' }) < 0 ? s : s.Replace("\\", "\\\\").Replace("/", "\\/").Replace("=", "\\=");

    /// <summary>
    /// Flattens the tree into display lines: each group header followed (unless its Key is in
    /// <paramref name="collapsedKeys"/>) by its child groups or, on the last level, its data rows.
    /// </summary>
    public static List<PivotLine> Flatten(IReadOnlyList<PivotNode> roots, ISet<string> collapsedKeys)
    {
        long cap = 0;
        foreach (var r in roots) cap += Estimate(r, collapsedKeys);
        var list = new List<PivotLine>((int)Math.Min(cap, int.MaxValue / 2));
        foreach (var r in roots) Emit(r, collapsedKeys, list);
        return list;
    }

    /// <summary>Keys of every group in the tree (used to start with all groups collapsed).</summary>
    public static IEnumerable<string> AllKeys(IReadOnlyList<PivotNode> roots)
    {
        var stack = new Stack<PivotNode>(roots.Reverse());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n.Key;
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    private static long Estimate(PivotNode n, ISet<string> collapsed)
    {
        long c = 1;
        if (collapsed.Contains(n.Key)) return c;
        if (n.Children.Count > 0) foreach (var ch in n.Children) c += Estimate(ch, collapsed);
        else c += n.Rows.Length;
        return c;
    }

    private static void Emit(PivotNode n, ISet<string> collapsed, List<PivotLine> list)
    {
        list.Add(new PivotLine(n, -1, n.Depth));
        if (collapsed.Contains(n.Key)) return;
        if (n.Children.Count > 0)
            foreach (var ch in n.Children) Emit(ch, collapsed, list);
        else
        {
            int d = n.Depth + 1;
            foreach (var r in n.Rows) list.Add(new PivotLine(null, r, d));
        }
    }
}
