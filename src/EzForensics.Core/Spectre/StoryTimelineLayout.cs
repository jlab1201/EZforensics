using System.Globalization;

namespace EzForensics.Core.Spectre;

/// <summary>Sizes for <see cref="StoryTimelineLayout.Build"/>; all in device-independent pixels.</summary>
/// <param name="Width">Width of the axis itself (breaks included).</param>
/// <param name="BreakWidth">Fixed width of a collapsed empty gap.</param>
/// <param name="GapFraction">Span fallback only: an empty gap longer than this share of the whole span becomes a break.</param>
/// <param name="ClusterSeconds">Story scale: moments less than this far apart in time share one point (moments at the very same instant always do). The view passes <see cref="StoryTimelineLayout.ClusterSecondsFor"/> of its zoom, so groups split as you zoom in.</param>
/// <param name="ClusterPx">Story scale: neighbouring points that would end up closer than this are merged into one.</param>
/// <param name="MinGap">Story scale: base distance between two neighbouring points (before fitting to <paramref name="Width"/>).</param>
/// <param name="LogGap">Story scale: extra distance per decade of the time between them (log10 of 1 + minutes).</param>
/// <param name="EdgePad">Story scale: room left at both ends of the axis.</param>
public sealed record StoryTimelineOptions(
    double Width = 1000, double BreakWidth = 36, double CardWidth = 180, double CardHeight = 72, double RowGap = 8,
    int Rows = 3, double CardGap = 8, double GapFraction = 0.15,
    double ClusterSeconds = 120, double ClusterPx = 10, double MinGap = 24, double LogGap = 24, double EdgePad = 24);

/// <summary>A stretch of the axis that shows real time. Times map linearly onto <see cref="X0"/>..<see cref="X1"/>.</summary>
public sealed record AxisSegment(DateTimeOffset Start, DateTimeOffset End, double X0, double X1)
{
    public double Width => X1 - X0;
    public bool IsPoint => End <= Start;
}

/// <summary>
/// A collapsed empty gap between two segments. <see cref="Label"/> says how long it was ("15 yrs", "3 mo", "4 days", "6 h").
/// <see cref="Squiggle"/>: draw it as a zig-zag break (long gaps); false = just the label on a plain line (1 to 30 days on the story scale).
/// </summary>
public sealed record AxisBreak(double X0, double X1, DateTimeOffset From, DateTimeOffset To, string Label, bool Squiggle = true);

/// <summary>One point of the story scale: the key events (in time order) that share <see cref="AnchorX"/>. Empty ids = only where a range ends.</summary>
public sealed record StoryCluster(double AnchorX, IReadOnlyList<string> MomentIds, DateTimeOffset Start, DateTimeOffset End)
{
    public bool IsGroup => MomentIds.Count > 1;
}

public sealed record StoryTick(double X, DateTimeOffset Time, string Label);

/// <summary>One key event as a card above the line. <see cref="Rect"/> is in callout-area coordinates (y = 0 at the top; row 0 is the row nearest the line).</summary>
public sealed record StoryCallout(
    string MomentId, int Number, double AnchorX, double BarX0, double BarX1, int Row, StoryRect Rect, string Phase)
{
    public bool IsRange => BarX1 > BarX0;
}

/// <summary>Plain rectangle so Core needs no WPF types.</summary>
public readonly record struct StoryRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Overlaps(StoryRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
}

/// <summary>A run of neighbouring moments with the same plain phase, as a band along the axis.</summary>
public sealed record PhaseBand(string Phase, double X0, double X1);

/// <summary>The part of the axis that is on screen, in axis x coordinates.</summary>
public readonly record struct ViewWindow(double Start, double End)
{
    public double Width => End - Start;
}

/// <summary>
/// Pure layout for the story timeline. With dated key events it uses the story scale: only the moments shape the axis, moments seconds apart
/// share a point, and the distance between points grows with the log of the time between them (so minutes, hours, days and months all stay
/// readable). Without dated moments it falls back to the span layout (long empty stretches become fixed-width breaks). Cards never overlap.
/// No WPF; the view only draws the numbers. All times are UTC.
/// </summary>
public sealed class StoryTimelineLayout
{
    public StoryTimelineOptions Options { get; }
    public IReadOnlyList<AxisSegment> Segments { get; }
    public IReadOnlyList<AxisBreak> Breaks { get; }
    public IReadOnlyList<StoryTick> Ticks { get; }
    public IReadOnlyList<StoryCallout> Callouts { get; }
    public IReadOnlyList<PhaseBand> Phases { get; }

    /// <summary>Story scale points, left to right (empty for the span fallback).</summary>
    public IReadOnlyList<StoryCluster> Clusters { get; }

    /// <summary>True when the axis is built from the key events (story scale); false for the span fallback.</summary>
    public bool IsStoryScale => _knots != null;

    /// <summary>Dated findings that start before the first key event (story scale only).</summary>
    public int EarlierCount { get; }

    /// <summary>Dated findings that start after the last key event (story scale only).</summary>
    public int LaterCount { get; }

    /// <summary>Story scale mapping: (time, x) pairs, both increasing (x repeats inside a point).</summary>
    private readonly List<(DateTimeOffset T, double X)>? _knots;

    /// <summary>Full width: the axis, or wider when cards had to be pushed past its right end.</summary>
    public double TotalWidth { get; }

    /// <summary>Height of the card area above the line (rows actually used).</summary>
    public double CalloutAreaHeight { get; }

    /// <summary>Nothing dated to place (no moments and no dated findings).</summary>
    public bool IsEmpty => Segments.Count == 0;

    private StoryTimelineLayout(StoryTimelineOptions o, List<AxisSegment> segs, List<AxisBreak> breaks, List<StoryTick> ticks,
        List<StoryCallout> callouts, List<PhaseBand> phases, double total, double areaHeight,
        List<(DateTimeOffset T, double X)>? knots = null, List<StoryCluster>? clusters = null, int earlier = 0, int later = 0)
    {
        Options = o; Segments = segs; Breaks = breaks; Ticks = ticks; Callouts = callouts; Phases = phases; TotalWidth = total; CalloutAreaHeight = areaHeight;
        _knots = knots; Clusters = clusters ?? new List<StoryCluster>(); EarlierCount = earlier; LaterCount = later;
    }

    /// <summary>Grouping window at zoom 1 (seconds).</summary>
    public const double BaseClusterSeconds = 120;

    /// <summary>The grouping window at a zoom: shrinks as 1/zoom, so a group only ever splits (never re-merges) as you zoom in.</summary>
    public static double ClusterSecondsFor(double zoom) => BaseClusterSeconds / Math.Max(1, double.IsFinite(zoom) ? zoom : 1);

    // ------------------------------------------------------------------ mapping

    /// <summary>
    /// X for a time. Story scale: piecewise linear between the points, clamped to the first and last point. Span fallback: times before the first
    /// or after the last segment clamp to the axis ends; times inside a break clamp to the nearer edge.
    /// </summary>
    public double X(DateTimeOffset t)
    {
        if (_knots is { } k)
        {
            if (k.Count == 0) return 0;
            if (t <= k[0].T) return k[0].X;
            if (t >= k[^1].T) return k[^1].X;
            int lo = 0, hi = k.Count - 1;   // k[lo].T <= t < k[hi].T
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (k[mid].T <= t) lo = mid; else hi = mid; }
            var span = (k[hi].T - k[lo].T).TotalSeconds;
            return span <= 0 ? k[lo].X : k[lo].X + (t - k[lo].T).TotalSeconds / span * (k[hi].X - k[lo].X);
        }
        if (Segments.Count == 0) return 0;
        var first = Segments[0];
        if (t <= first.Start) return first.IsPoint ? (first.X0 + first.X1) / 2 : first.X0;
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            if (t >= s.Start && t <= s.End) return XIn(s, t);
            if (i + 1 < Segments.Count && t > s.End && t < Segments[i + 1].Start)
            {
                var n = Segments[i + 1];
                return (t - s.End) <= (n.Start - t) ? XIn(s, s.End) : XIn(n, n.Start);
            }
        }
        var last = Segments[^1];
        return last.IsPoint ? (last.X0 + last.X1) / 2 : last.X1;
    }

    private static double XIn(AxisSegment s, DateTimeOffset t) =>
        s.IsPoint ? (s.X0 + s.X1) / 2 : s.X0 + (t - s.Start).TotalSeconds / (s.End - s.Start).TotalSeconds * s.Width;

    /// <summary>
    /// Inverse of <see cref="X"/>. Story scale: between two points it interpolates, on a point it gives the point's first time, outside the axis
    /// its first or last time. Span fallback: an x in a break gives the nearer segment edge.
    /// </summary>
    public DateTimeOffset Time(double x)
    {
        if (_knots is { } k)
        {
            if (k.Count == 0) return DateTimeOffset.MinValue;
            if (x <= k[0].X) return k[0].T;
            if (x >= k[^1].X) return k[^1].T;
            for (int i = 0; i + 1 < k.Count; i++)
            {
                if (x == k[i].X) return k[i].T;
                if (x > k[i].X && x < k[i + 1].X)
                    return k[i].T + TimeSpan.FromSeconds((x - k[i].X) / (k[i + 1].X - k[i].X) * (k[i + 1].T - k[i].T).TotalSeconds);
            }
            return k[^1].T;
        }
        if (Segments.Count == 0) return DateTimeOffset.MinValue;
        if (x <= Segments[0].X0) return Segments[0].Start;
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            if (x >= s.X0 && x <= s.X1) return s.IsPoint || s.Width <= 0 ? s.Start : s.Start + TimeSpan.FromSeconds((x - s.X0) / s.Width * (s.End - s.Start).TotalSeconds);
            if (i + 1 < Segments.Count && x > s.X1 && x < Segments[i + 1].X0)
                return (x - s.X1) <= (Segments[i + 1].X0 - x) ? s.End : Segments[i + 1].Start;
        }
        return Segments[^1].End;
    }

    // ------------------------------------------------------------------ slide window

    /// <summary>
    /// The window for sliding to a moment: <paramref name="zoom"/> 1 shows everything, 2 shows half the width, ...; centred on the card's anchor and
    /// clamped to the axis. An unknown id gives the whole width.
    /// </summary>
    public ViewWindow CenterOn(string momentId, double zoom)
    {
        var c = Callouts.FirstOrDefault(c => c.MomentId == momentId);
        if (c == null) return new ViewWindow(0, TotalWidth);
        return CenterOnX(c.AnchorX, zoom);
    }

    /// <summary>A window of 1/<paramref name="zoom"/> of the width centred on axis x, clamped to the axis.</summary>
    public ViewWindow CenterOnX(double x, double zoom)
    {
        var w = TotalWidth / Math.Max(1, double.IsFinite(zoom) ? zoom : 1);
        var start = Math.Clamp(x - w / 2, 0, Math.Max(0, TotalWidth - w));
        return new ViewWindow(start, start + w);
    }

    /// <summary>The story-scale point holding a moment, or null.</summary>
    public StoryCluster? ClusterOf(string momentId) => Clusters.FirstOrDefault(c => c.MomentIds.Contains(momentId));

    // ------------------------------------------------------------------ build

    public static StoryTimelineLayout Build(SpectreTimeline tl, StoryTimelineOptions? options = null)
    {
        var o = options ?? new StoryTimelineOptions();
        var moments = tl.Moments.Where(m => m.Time != null).OrderBy(m => m.Time).ThenBy(m => m.Number).ToList();
        return moments.Count > 0 ? BuildByMoments(tl, moments, o) : BuildBySpan(tl, moments, o);
    }

    /// <summary>Story scale: the axis is made of the key events only (see the class summary).</summary>
    private static StoryTimelineLayout BuildByMoments(SpectreTimeline tl, List<SpectreMoment> moments, StoryTimelineOptions o)
    {
        // 1. points in time: each moment's start (with its id) and, for ranges, its end
        var pts = new List<(DateTimeOffset T, string? Id)>();
        foreach (var m in moments)
        {
            pts.Add((m.Time!.Value, m.Id));
            if (m.IsRange) pts.Add((m.End!.Value, null));
        }
        pts = pts.OrderBy(p => p.T).ThenBy(p => p.Id == null ? 1 : 0).ToList();

        // 2. group by time: within ClusterSeconds of the group's first time
        var groups = new List<List<(DateTimeOffset T, string? Id)>>();
        foreach (var p in pts)
        {
            if (groups.Count > 0 && (p.T - groups[^1][0].T).TotalSeconds is var dt && (dt <= 0 || dt < o.ClusterSeconds)) groups[^1].Add(p);
            else groups.Add(new() { p });
        }

        // 3. gaps ~ MinGap + LogGap * log10(1 + minutes), fitted to the width; neighbours that would be closer than ClusterPx merge
        double pad = Math.Min(Math.Max(0, o.EdgePad), o.Width / 4);
        double avail = Math.Max(1, o.Width - 2 * pad);
        double Weight(int i) => o.MinGap + o.LogGap * Math.Log10(1 + Math.Max(0, (groups[i + 1][0].T - groups[i][^1].T).TotalSeconds) / 60.0);
        double scale = 1;
        while (groups.Count > 1)
        {
            var w = Enumerable.Range(0, groups.Count - 1).Select(Weight).ToList();
            scale = avail / Math.Max(1e-9, w.Sum());
            int worst = 0;
            for (int i = 1; i < w.Count; i++) if (w[i] < w[worst]) worst = i;
            if (w[worst] * scale >= o.ClusterPx) break;
            groups[worst].AddRange(groups[worst + 1]);
            groups.RemoveAt(worst + 1);
        }
        var anchors = new double[groups.Count];
        if (groups.Count == 1) anchors[0] = o.Width / 2;
        else
        {
            anchors[0] = pad;
            for (int i = 1; i < groups.Count; i++) anchors[i] = anchors[i - 1] + Weight(i - 1) * scale;
        }

        var knots = new List<(DateTimeOffset T, double X)>();
        var clusters = new List<StoryCluster>();
        for (int i = 0; i < groups.Count; i++)
        {
            DateTimeOffset s = groups[i][0].T, e = groups[i][^1].T;
            knots.Add((s, anchors[i]));
            if (e > s) knots.Add((e, anchors[i]));
            clusters.Add(new StoryCluster(anchors[i], groups[i].Where(p => p.Id != null).Select(p => p.Id!).ToList(), s, e));
        }

        // 4. gap labels for a day or more (zig-zag from 30 days); the line runs between the zig-zags
        var breaks = new List<AxisBreak>();
        var segs = new List<AxisSegment>();
        double segX = anchors[0]; var segT = clusters[0].Start;
        for (int i = 0; i + 1 < clusters.Count; i++)
        {
            var gap = clusters[i + 1].Start - clusters[i].End;
            if (gap.TotalDays < 1) continue;
            double mid = (anchors[i] + anchors[i + 1]) / 2, half = Math.Max(0, Math.Min(o.BreakWidth / 2, (anchors[i + 1] - anchors[i]) / 2 - 3));
            bool squiggle = gap.TotalDays >= 30;
            var b = new AxisBreak(mid - half, mid + half, clusters[i].End, clusters[i + 1].Start, GapLabel(gap), squiggle);
            breaks.Add(b);
            if (!squiggle) continue;
            segs.Add(new AxisSegment(segT, b.From, segX, b.X0));
            segX = b.X1; segT = b.To;
        }
        segs.Add(new AxisSegment(segT, clusters[^1].End, segX, anchors[^1]));

        // 5. one tick per point, thinned so labels do not overlap (rough width: Core has no font metrics)
        var inv = CultureInfo.InvariantCulture;
        var ticks = new List<StoryTick>();
        double lastRight = double.NegativeInfinity; DateTime? prev = null;
        foreach (var c in clusters)
        {
            var d = c.Start.UtcDateTime;
            string label = prev is { } pd && pd.Date == d.Date ? d.ToString("HH:mm", inv)
                : prev == null || prev.Value.Year != d.Year ? d.ToString("d MMM yyyy HH:mm", inv) : d.ToString("d MMM HH:mm", inv);
            double half = (label.Length * 5.6 + 10) / 2;
            if (c.AnchorX - half < lastRight) continue;
            ticks.Add(new StoryTick(c.AnchorX, c.Start, label));
            lastRight = c.AnchorX + half; prev = d;
        }

        var first = clusters[0].Start; var last = clusters[^1].End;
        int earlier = tl.Findings.Count(f => f.Start is { } fs && fs < first);
        int later = tl.Findings.Count(f => f.Start is { } fs && fs > last);

        var axis = new StoryTimelineLayout(o, segs, breaks, ticks, new(), new(), o.Width, 0, knots, clusters, earlier, later);
        var (callouts, total, area) = PlaceCallouts(axis, moments, o);
        return new StoryTimelineLayout(o, segs, breaks, ticks, callouts, BuildPhases(callouts, total), total, area, knots, clusters, earlier, later);
    }

    /// <summary>Span fallback (no dated moments): every dated finding shapes the axis; long empty stretches become fixed-width breaks.</summary>
    private static StoryTimelineLayout BuildBySpan(SpectreTimeline tl, List<SpectreMoment> moments, StoryTimelineOptions o)
    {
        // 1. every dated interval
        var intervals = new List<(DateTimeOffset s, DateTimeOffset e)>();
        foreach (var f in tl.Findings)
            if (f.Start is { } fs) intervals.Add((fs, f.EffectiveEnd is { } fe && fe > fs ? fe : fs));
        foreach (var m in moments) intervals.Add((m.Time!.Value, m.End is { } me && me > m.Time ? me : m.Time!.Value));
        if (intervals.Count == 0) return new StoryTimelineLayout(o, new(), new(), new(), new(), new(), o.Width, 0);

        // 2. merge overlapping intervals, then join those separated by a small gap
        intervals.Sort((a, b) => a.s.CompareTo(b.s));
        var merged = new List<(DateTimeOffset s, DateTimeOffset e)> { intervals[0] };
        foreach (var iv in intervals.Skip(1))
        {
            var last = merged[^1];
            if (iv.s <= last.e) merged[^1] = (last.s, iv.e > last.e ? iv.e : last.e);
            else merged.Add(iv);
        }
        var totalSpan = (merged[^1].e - merged[0].s).TotalSeconds;
        var threshold = totalSpan * o.GapFraction;
        var runs = new List<(DateTimeOffset s, DateTimeOffset e)> { merged[0] };
        var gaps = new List<(DateTimeOffset from, DateTimeOffset to)>();
        foreach (var iv in merged.Skip(1))
        {
            var last = runs[^1];
            if ((iv.s - last.e).TotalSeconds > threshold && totalSpan > 0) { gaps.Add((last.e, iv.s)); runs.Add(iv); }
            else runs[^1] = (last.s, iv.e);
        }

        // 3. pixels: breaks are fixed, the rest is shared by span (a point-like run gets a small share so it stays visible)
        var available = Math.Max(o.Width - gaps.Count * o.BreakWidth, runs.Count * 8.0);
        var minShare = totalSpan > 0 ? totalSpan * 0.01 : 1;
        var weights = runs.Select(r => Math.Max((r.e - r.s).TotalSeconds, minShare)).ToList();
        var wsum = weights.Sum();
        var segs = new List<AxisSegment>();
        var breaks = new List<AxisBreak>();
        double x = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            var w = available * weights[i] / wsum;
            segs.Add(new AxisSegment(runs[i].s, runs[i].e, x, x + w));
            x += w;
            if (i < gaps.Count)
            {
                breaks.Add(new AxisBreak(x, x + o.BreakWidth, gaps[i].from, gaps[i].to, GapLabel(gaps[i].to - gaps[i].from)));
                x += o.BreakWidth;
            }
        }

        var ticks = segs.SelectMany(Ticks_).ToList();

        // 4. callouts
        var layout0 = new StoryTimelineLayout(o, segs, breaks, ticks, new(), new(), x, 0);
        var (callouts, total, area) = PlaceCallouts(layout0, moments, o);
        var phases = BuildPhases(callouts, total);
        return new StoryTimelineLayout(o, segs, breaks, ticks, callouts, phases, total, area);
    }

    private static (List<StoryCallout>, double total, double area) PlaceCallouts(StoryTimelineLayout axis, List<SpectreMoment> moments, StoryTimelineOptions o)
    {
        var rows = Math.Max(1, o.Rows);
        var rowRight = new double[rows];
        Array.Fill(rowRight, double.NegativeInfinity);
        var placed = new List<(SpectreMoment m, double ax, double b0, double b1, int row, double x)>();
        double total = axis.TotalWidth;
        for (int i = 0; i < moments.Count; i++)
        {
            var m = moments[i];
            var b0 = axis.X(m.Time!.Value);
            var b1 = m.IsRange ? Math.Max(b0, axis.X(m.End!.Value)) : b0;
            var anchor = (b0 + b1) / 2;
            var desired = Math.Max(0, Math.Min(anchor - o.CardWidth / 2, Math.Max(0, axis.TotalWidth - o.CardWidth)));
            // pick the row where the card has to move least from where it wants to be; ties go to the alternating row
            int pref = i % rows, best = -1; double bestX = 0, bestDist = double.MaxValue;
            for (int k = 0; k < rows; k++)
            {
                int r = (pref + k) % rows;
                var cx = Math.Max(desired, rowRight[r] + o.CardGap);
                var dist = cx - desired;
                if (dist < bestDist - 1e-9) { best = r; bestX = cx; bestDist = dist; }
            }
            rowRight[best] = bestX + o.CardWidth;
            total = Math.Max(total, bestX + o.CardWidth);
            placed.Add((m, anchor, b0, b1, best, bestX));
        }
        int used = placed.Count == 0 ? 0 : placed.Max(p => p.row) + 1;
        var rowH = o.CardHeight + o.RowGap;
        var list = placed.Select(p => new StoryCallout(p.m.Id, p.m.Number, p.ax, p.b0, p.b1, p.row,
            new StoryRect(p.x, (used - 1 - p.row) * rowH, o.CardWidth, o.CardHeight),
            p.m.Phase.Length > 0 ? p.m.Phase : PlainLanguage.OtherActivity)).ToList();
        return (list, total, used * rowH);
    }

    private static List<PhaseBand> BuildPhases(List<StoryCallout> callouts, double total)
    {
        var bands = new List<PhaseBand>();
        if (callouts.Count == 0) return bands;
        var ordered = callouts.OrderBy(c => c.AnchorX).ToList();
        var runStart = 0;
        for (int i = 1; i <= ordered.Count; i++)
        {
            if (i < ordered.Count && ordered[i].Phase == ordered[runStart].Phase) continue;
            double x0 = runStart == 0 ? 0 : (ordered[runStart - 1].AnchorX + ordered[runStart].AnchorX) / 2;
            double x1 = i == ordered.Count ? total : (ordered[i - 1].AnchorX + ordered[i].AnchorX) / 2;
            bands.Add(new PhaseBand(ordered[runStart].Phase, x0, x1));
            runStart = i;
        }
        return bands;
    }

    // ------------------------------------------------------------------ labels and ticks

    /// <summary>"15 yrs", "3 mo", "4 days", "6 h", "20 min".</summary>
    public static string GapLabel(TimeSpan gap)
    {
        var days = gap.TotalDays;
        if (days >= 365) { var y = (int)Math.Round(days / 365.25); y = Math.Max(1, y); return y == 1 ? "1 yr" : $"{y} yrs"; }
        if (days >= 30) { var m = Math.Max(1, (int)Math.Round(days / 30.4375)); return $"{m} mo"; }
        if (days >= 1) { var d = (int)Math.Round(days); return d == 1 ? "1 day" : $"{d} days"; }
        if (gap.TotalHours >= 1) return $"{(int)Math.Round(gap.TotalHours)} h";
        return $"{Math.Max(1, (int)Math.Round(gap.TotalMinutes))} min";
    }

    /// <summary>Nice tick steps, coarse to fine.</summary>
    private static readonly (double Seconds, int Kind)[] NiceSteps =
    {
        (86400, 0), (43200, 1), (21600, 1), (10800, 1), (3600, 1), (1800, 1), (900, 1), (600, 1), (300, 1), (60, 1),
        (30, 2), (10, 2), (5, 2), (1, 2),
    };

    /// <summary>
    /// Splunk-style time buckets: fainter ticks at a round interval (1d .. 1s, UTC) between the points, chosen from the local pixels per second so
    /// they are at least <paramref name="targetPx"/> apart (axis units). Only inside one stretch of real time (never across a collapsed gap of 30 days
    /// or more), only for axis x in <paramref name="fromX"/>..<paramref name="toX"/>, never on the real-time points' own ticks or each other's labels, at most 200.
    /// The mapping is the layout's own (piecewise between the points), so the ticks sit where <see cref="X"/> puts those times.
    /// </summary>
    public IReadOnlyList<StoryTick> MinorTicks(double targetPx, double fromX, double toX)
    {
        var res = new List<StoryTick>();
        if (!(targetPx > 0) || IsEmpty) return res;
        var inv = CultureInfo.InvariantCulture;
        var pieces = Pieces();
        bool secs = UseSeconds(pieces, targetPx, fromX, toX);

        // things already labelled: the points' own ticks, and the gap labels' spans
        var taken = MajorTicks(targetPx, fromX, toX).Select(t => (L: t.X - (t.Label.Length * 5.6 + 10) / 2, R: t.X + (t.Label.Length * 5.6 + 10) / 2)).ToList();
        taken.AddRange(Breaks.Select(b => (L: b.X0, R: b.X1)));
        DateTime? prev = null; double lastRight = double.NegativeInfinity;
        foreach (var p in pieces)
        {
            if (p.X1 < fromX || p.X0 > toX) continue;
            double pxPerSec = (p.X1 - p.X0) / (p.T1 - p.T0).TotalSeconds;
            var step = StepFor(p, targetPx);
            if (step.Seconds <= 0) continue;
            double a = Math.Max(p.X0, fromX), b = Math.Min(p.X1, toX);
            double sa = (p.T0 - DateTimeOffset.UnixEpoch).TotalSeconds + (a - p.X0) / pxPerSec, sb = (p.T0 - DateTimeOffset.UnixEpoch).TotalSeconds + (b - p.X0) / pxPerSec;
            for (double s = Math.Ceiling(sa / step.Seconds) * step.Seconds; s <= sb + 1e-6 && res.Count < 200; s += step.Seconds)
            {
                var t = DateTimeOffset.UnixEpoch.AddSeconds(s);
                if (t <= p.T0 || t >= p.T1) continue;   // the points' own times belong to their ticks
                double x = p.X0 + (s - (p.T0 - DateTimeOffset.UnixEpoch).TotalSeconds) * pxPerSec;
                var d = t.UtcDateTime;
                bool midnight = d.TimeOfDay == TimeSpan.Zero;
                string label = step.Kind == 0 || midnight ? d.ToString("d MMM", inv)
                    : prev == null || prev.Value.Date != d.Date ? d.ToString(secs ? "d MMM HH:mm:ss" : "d MMM HH:mm", inv)
                    : d.ToString(secs ? "HH:mm:ss" : "HH:mm", inv);
                double half = (label.Length * 5.6 + 10) / 2, l = x - half, r = x + half;
                if (l < lastRight || taken.Any(u => l < u.R && r > u.L)) continue;
                res.Add(new StoryTick(x, t, label));
                lastRight = r; prev = d;
            }
        }
        return res;
    }

    private List<(DateTimeOffset T0, DateTimeOffset T1, double X0, double X1)> Pieces()
    {
        // linear pieces of the time -> x mapping
        var pieces = new List<(DateTimeOffset T0, DateTimeOffset T1, double X0, double X1)>();
        if (_knots is { } k)
        {
            for (int i = 0; i + 1 < k.Count; i++)
                if (k[i + 1].X > k[i].X && k[i + 1].T > k[i].T && (k[i + 1].T - k[i].T).TotalDays < 30) pieces.Add((k[i].T, k[i + 1].T, k[i].X, k[i + 1].X));
        }
        else foreach (var s in Segments) if (!s.IsPoint && s.Width > 0) pieces.Add((s.Start, s.End, s.X0, s.X1));
        return pieces;
    }

    private static (double Seconds, int Kind) StepFor((DateTimeOffset T0, DateTimeOffset T1, double X0, double X1) p, double targetPx)
    {
        double pxPerSec = (p.X1 - p.X0) / (p.T1 - p.T0).TotalSeconds;
        return NiceSteps.Where(n => n.Seconds * pxPerSec >= targetPx).DefaultIfEmpty((0, 0)).Last();   // finest that is still wide enough
    }

    /// <summary>True when the visible axis is finer than a minute: every time label on it (points and buckets) then carries seconds, otherwise none does.</summary>
    private static bool UseSeconds(List<(DateTimeOffset T0, DateTimeOffset T1, double X0, double X1)> pieces, double targetPx, double fromX, double toX)
    {
        foreach (var p in pieces)
        {
            if (p.X1 < fromX || p.X0 > toX) continue;
            var step = StepFor(p, targetPx);
            if (step.Seconds > 0 && step.Seconds < 60) return true;
        }
        return false;
    }

    /// <summary>
    /// The points' own ticks for a view, in the same time format as the buckets (<see cref="MinorTicks"/>): HH:mm, or HH:mm:ss when the visible axis is
    /// finer than a minute. A label equal to the one before it is dropped, and so is one that would overlap it.
    /// </summary>
    public IReadOnlyList<StoryTick> MajorTicks(double targetPx, double fromX, double toX)
    {
        if (!(targetPx > 0) || IsEmpty) return Ticks;
        var inv = CultureInfo.InvariantCulture;
        string tf = UseSeconds(Pieces(), targetPx, fromX, toX) ? "HH:mm:ss" : "HH:mm";
        var res = new List<StoryTick>(); DateTime? prev = null; double lastRight = double.NegativeInfinity; string? lastLabel = null;
        foreach (var t in Ticks)
        {
            var d = t.Time.UtcDateTime;
            string label = prev is { } pd && pd.Date == d.Date ? d.ToString(tf, inv)
                : prev == null || prev.Value.Year != d.Year ? d.ToString("d MMM yyyy " + tf, inv) : d.ToString("d MMM " + tf, inv);
            double half = (label.Length * 5.6 + 10) / 2;
            if (label == lastLabel || t.X - half < lastRight) continue;
            res.Add(new StoryTick(t.X, t.Time, label));
            lastRight = t.X + half; prev = d; lastLabel = label;
        }
        return res;
    }

    private static IEnumerable<StoryTick> Ticks_(AxisSegment s)
    {
        var inv = CultureInfo.InvariantCulture;
        if (s.IsPoint)
        {
            yield return new StoryTick((s.X0 + s.X1) / 2, s.Start, s.Start.UtcDateTime.ToString("d MMM yyyy HH:mm", inv));
            yield break;
        }
        var span = s.End - s.Start;
        var start = s.Start.UtcDateTime;
        var end = s.End.UtcDateTime;
        DateTime first; Func<DateTime, DateTime> next; Func<DateTime, string> label; int guard = 0;
        if (span.TotalDays >= 730) { first = new DateTime(start.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc); next = d => d.AddYears(1); label = d => d.ToString("yyyy", inv); }
        else if (span.TotalDays >= 60) { first = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc); next = d => d.AddMonths(1); label = d => d.ToString("MMM yyyy", inv); }
        else if (span.TotalDays >= 2) { first = new DateTime(start.Year, start.Month, start.Day, 0, 0, 0, DateTimeKind.Utc); next = d => d.AddDays(1); label = d => d.ToString("d MMM", inv); }
        else { first = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc); next = d => d.AddHours(1); label = d => d.ToString("HH:mm", inv); }

        var all = new List<DateTime>();
        for (var d = first; d <= end && guard++ < 5000; d = next(d))
            if (d >= start) all.Add(d);
        int step = Math.Max(1, (int)Math.Ceiling(all.Count / 12.0));
        for (int i = 0; i < all.Count; i += step)
        {
            var t = new DateTimeOffset(all[i], TimeSpan.Zero);
            yield return new StoryTick(XIn(s, t), t, label(all[i]));
        }
    }
}
