using System.Globalization;

namespace EzForensics.Core.Spectre;

/// <summary>A visible time window (UTC).</summary>
public sealed record TimeRange(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Duration => To - From;
}

public sealed record LayoutLane(string Name, int Index, double Y, double Height);

/// <summary>A finding drawn as a dot (<see cref="IsRange"/> false, X only) or a bar (X..X2). <see cref="Y"/> is the lane centre.</summary>
public sealed record LayoutItem(string FindingId, double X, double X2, int LaneIndex, double Y, string Severity, bool IsRange);

/// <summary>Several findings of one lane merged into a "+N" bubble because they sit closer than the gap.</summary>
public sealed record LayoutBubble(int LaneIndex, double X, double Y, int Count, IReadOnlyList<string> FindingIds, DateTimeOffset From, DateTimeOffset To, string MaxSeverity);

/// <summary>A numbered key-moment pin. <see cref="Row"/> (0 = closest to the axis) keeps captions from overlapping; the caption starts at <see cref="X"/> and is about <see cref="LabelWidth"/> wide.</summary>
public sealed record LayoutPin(string MomentId, int Number, double X, int Row, double LabelWidth, string Label, string? PrimaryFindingId);

/// <summary>A shaded cluster band.</summary>
public sealed record LayoutBand(string ClusterId, string Kind, double X1, double X2, int Count, string? Label);

public sealed record AxisTick(double X, DateTimeOffset Time, string Label, bool Major);

public sealed record TimelineLayoutResult(
    IReadOnlyList<LayoutLane> Lanes, IReadOnlyList<LayoutItem> Items, IReadOnlyList<LayoutBubble> Bubbles, IReadOnlyList<LayoutPin> Pins,
    IReadOnlyList<LayoutBand> Bands, IReadOnlyList<AxisTick> Ticks, int PinRows, double TotalHeight, TimeRange Range, double Width);

public sealed record TimelineLayoutOptions
{
    /// <summary>Items of one lane closer than this many pixels merge into a bubble.</summary>
    public double BubbleGapPx { get; init; } = 10;
    /// <summary>Estimated width of one caption character.</summary>
    public double LabelCharWidth { get; init; } = 6.5;
    /// <summary>Width reserved for the numbered circle in front of a caption.</summary>
    public double PinGlyphWidth { get; init; } = 20;
    public double MaxLabelWidth { get; init; } = 240;
    public double LabelGapPx { get; init; } = 6;
    /// <summary>Target spacing of axis ticks.</summary>
    public double MinTickSpacingPx { get; init; } = 80;
    public double MinBandWidthPx { get; init; } = 2;
}

/// <summary>
/// Pure geometry of the findings timeline: swimlanes, dots, bars, "+N" bubbles, key-moment pins, cluster bands and the UTC axis,
/// for a time window and a pixel width. No WPF types; the drawing control only paints the result.
/// </summary>
public static class FindingsTimelineLayout
{
    public const string AxisLabel = "UTC";
    public static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(365.25 * 200);

    public static TimelineLayoutResult Compute(SpectreTimeline timeline, TimelineLaneMode mode, TimeRange range, double width, double laneHeight,
        TimelineLayoutOptions? options = null)
    {
        var o = options ?? new TimelineLayoutOptions();
        width = Math.Max(1, width);
        if (range.Duration < MinSpan) range = new TimeRange(range.From, range.From + MinSpan);
        double ms = range.Duration.TotalMilliseconds;
        double X(DateTimeOffset t) => (t - range.From).TotalMilliseconds / ms * width;

        // lanes that hold at least one dated finding keep their row at every zoom level
        var used = new HashSet<string>(timeline.Findings.Where(f => f.Start != null).Select(f => f.Lane(mode)), StringComparer.OrdinalIgnoreCase);
        var lanes = timeline.LanesFor(mode).Where(used.Contains).Select((n, i) => new LayoutLane(n, i, i * laneHeight, laneHeight)).ToList();
        var laneIndex = lanes.ToDictionary(l => l.Name, l => l.Index, StringComparer.OrdinalIgnoreCase);

        var items = new List<LayoutItem>();
        var bubbles = new List<LayoutBubble>();
        var perLane = new Dictionary<int, List<(SpectreTimelineFinding F, double X)>>();
        foreach (var f in timeline.Findings)
        {
            if (f.Start is not { } start) continue;
            var end = f.EffectiveEnd ?? start;
            if (end < range.From || start > range.To) continue;
            if (!laneIndex.TryGetValue(f.Lane(mode), out var li)) continue;
            var lane = lanes[li];
            double cy = lane.Y + laneHeight / 2;
            double x = X(start);
            if (f.IsRange) items.Add(new LayoutItem(f.Id, x, X(end), li, cy, f.Severity, true));
            else
            {
                if (!perLane.TryGetValue(li, out var l)) perLane[li] = l = new();
                l.Add((f, x));
            }
        }
        foreach (var (li, list) in perLane.OrderBy(k => k.Key))
        {
            double cy = lanes[li].Y + laneHeight / 2;
            list.Sort((a, b) => a.X.CompareTo(b.X));
            int i = 0;
            while (i < list.Count)
            {
                int j = i + 1;
                while (j < list.Count && list[j].X - list[j - 1].X < o.BubbleGapPx) j++;
                var group = list.GetRange(i, j - i);
                if (group.Count == 1)
                    items.Add(new LayoutItem(group[0].F.Id, group[0].X, group[0].X, li, cy, group[0].F.Severity, false));
                else
                {
                    var top = group.OrderByDescending(g => g.F.SeverityRank).First().F.Severity;
                    bubbles.Add(new LayoutBubble(li, group.Average(g => g.X), cy, group.Count, group.Select(g => g.F.Id).ToList(),
                        group.Min(g => g.F.Start!.Value), group.Max(g => g.F.Start!.Value), top));
                }
                i = j;
            }
        }

        var pins = PlacePins(timeline, range, width, o, X, out int rows);

        var bands = new List<LayoutBand>();
        foreach (var c in timeline.Clusters)
        {
            if (c.Start is not { } cs) continue;
            var ce = c.End ?? cs;
            if (ce < range.From || cs > range.To) continue;
            double x1 = X(cs), x2 = Math.Max(X(ce), x1 + o.MinBandWidthPx);
            bands.Add(new LayoutBand(c.Id, c.Kind, x1, x2, c.FindingIds.Count, c.Label));
        }

        return new TimelineLayoutResult(lanes, items, bubbles, pins, bands, Ticks(range, width, o.MinTickSpacingPx), rows,
            lanes.Count * laneHeight, range, width);
    }

    private static List<LayoutPin> PlacePins(SpectreTimeline tl, TimeRange range, double width, TimelineLayoutOptions o, Func<DateTimeOffset, double> x, out int rows)
    {
        var rowEnds = new List<double>();
        var pins = new List<LayoutPin>();
        var visible = tl.Moments.Where(m => m.Time is { } t && t >= range.From && t <= range.To)
            .Select(m => (M: m, X: x(m.Time!.Value))).OrderBy(p => p.X).ThenBy(p => p.M.Number);
        foreach (var (m, px) in visible)
        {
            var label = !string.IsNullOrWhiteSpace(m.Caption) ? m.Caption! : m.Headline;
            double w = Math.Min(o.MaxLabelWidth, o.PinGlyphWidth + label.Length * o.LabelCharWidth);
            int row = rowEnds.FindIndex(e => e + o.LabelGapPx <= px);
            if (row < 0) { rowEnds.Add(0); row = rowEnds.Count - 1; }
            rowEnds[row] = px + w;
            pins.Add(new LayoutPin(m.Id, m.Number, px, row, w, label, m.PrimaryFindingId));
        }
        rows = rowEnds.Count;
        return pins;
    }

    // ---- axis ----

    private static readonly TimeSpan[] FixedSteps =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
        TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7),
    };
    private static readonly int[] MonthSteps = { 1, 3, 6, 12, 24, 60, 120, 240, 600 };

    /// <summary>Ticks aligned to round UTC times. The step is the smallest one that leaves at least <paramref name="minSpacingPx"/> between ticks.</summary>
    public static IReadOnlyList<AxisTick> Ticks(TimeRange range, double width, double minSpacingPx = 80)
    {
        var ticks = new List<AxisTick>();
        double msTotal = Math.Max(1, range.Duration.TotalMilliseconds);
        double pxPerMs = Math.Max(1, width) / msTotal;
        TimeSpan? step = FixedSteps.Cast<TimeSpan?>().FirstOrDefault(s => s!.Value.TotalMilliseconds * pxPerMs >= minSpacingPx);
        double x(DateTimeOffset t) => (t - range.From).TotalMilliseconds * pxPerMs;
        if (step is { } st)
        {
            long stepTicks = st.Ticks;
            long origin = st >= TimeSpan.FromDays(7) ? TimeSpan.FromDays(4).Ticks : 0; // Monday-aligned weeks (1970-01-01 is a Thursday)
            long first = FloorDiv(range.From.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks - origin, stepTicks) * stepTicks + origin + DateTimeOffset.UnixEpoch.UtcTicks;
            for (long t = first; ticks.Count < 2000; t += stepTicks)
            {
                var time = new DateTimeOffset(t, TimeSpan.Zero);
                if (time > range.To) break;
                if (time < range.From) continue;
                bool midnight = time.TimeOfDay == TimeSpan.Zero;
                bool dateLabel = st >= TimeSpan.FromDays(1) || (midnight && st < TimeSpan.FromDays(1));
                ticks.Add(new AxisTick(x(time), time, dateLabel ? time.ToString("MMM dd", CultureInfo.InvariantCulture) : st < TimeSpan.FromMinutes(1) ? time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : time.ToString("HH:mm", CultureInfo.InvariantCulture),
                    midnight));
            }
            return ticks;
        }
        // months and years
        int months = MonthSteps.FirstOrDefault(m => m * 30.44 * 86400000 * pxPerMs >= minSpacingPx);
        if (months == 0) months = MonthSteps[^1];
        var from = range.From.UtcDateTime;
        int monthIndex = from.Year * 12 + from.Month - 1;
        monthIndex = monthIndex / months * months;
        for (int guard = 0; guard < 2000; guard++, monthIndex += months)
        {
            int year = monthIndex / 12, month = monthIndex % 12 + 1;
            if (year < 1 || year > 9998) break;
            var time = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
            if (time > range.To) break;
            if (time < range.From) continue;
            ticks.Add(new AxisTick(x(time), time, months >= 12 ? time.ToString("yyyy", CultureInfo.InvariantCulture) : time.ToString("yyyy-MM", CultureInfo.InvariantCulture), month == 1));
        }
        return ticks;
    }

    private static long FloorDiv(long a, long b) => a / b - (a % b != 0 && (a < 0) != (b < 0) ? 1 : 0);

    // ---- view helpers ----

    /// <summary>A window that holds every dated finding, range end, moment and cluster, padded by <paramref name="padFraction"/> each side; null when nothing is dated.</summary>
    public static TimeRange? FitRange(SpectreTimeline timeline, double padFraction = 0.05)
    {
        var times = new List<DateTimeOffset>();
        foreach (var f in timeline.Findings)
        {
            if (f.Start is { } s) times.Add(s);
            if (f.End is { } e) times.Add(e);
        }
        foreach (var m in timeline.Moments) if (m.Time is { } t) times.Add(t);
        foreach (var c in timeline.Clusters)
        {
            if (c.Start is { } s) times.Add(s);
            if (c.End is { } e) times.Add(e);
        }
        if (times.Count == 0) return null;
        var min = times.Min(); var max = times.Max();
        var span = max - min;
        if (span < MinSpan) return new TimeRange(min - TimeSpan.FromMinutes(30), max + TimeSpan.FromMinutes(30));
        var pad = TimeSpan.FromTicks((long)(span.Ticks * Math.Max(0, padFraction)));
        return new TimeRange(min - pad, max + pad);
    }

    /// <summary>Zooms around <paramref name="anchor"/> (kept at the same screen position). <paramref name="factor"/> above 1 zooms in, below 1 out.</summary>
    public static TimeRange Zoom(TimeRange range, DateTimeOffset anchor, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor)) return range;
        if (anchor < range.From) anchor = range.From;
        if (anchor > range.To) anchor = range.To;
        var newSpan = TimeSpan.FromTicks((long)Math.Clamp(range.Duration.Ticks / factor, MinSpan.Ticks, MaxSpan.Ticks));
        double frac = range.Duration.Ticks == 0 ? 0.5 : (double)(anchor - range.From).Ticks / range.Duration.Ticks;
        var from = anchor - TimeSpan.FromTicks((long)(newSpan.Ticks * frac));
        return new TimeRange(from, from + newSpan);
    }

    /// <summary>Moves the window by <paramref name="fraction"/> of its length (positive = later).</summary>
    public static TimeRange Pan(TimeRange range, double fraction)
    {
        var d = TimeSpan.FromTicks((long)(range.Duration.Ticks * fraction));
        return new TimeRange(range.From + d, range.To + d);
    }

    /// <summary>Moves the window for a drag: dragging right by <paramref name="deltaPx"/> pixels shows earlier times.</summary>
    public static TimeRange PanByPixels(TimeRange range, double deltaPx, double width) =>
        width <= 0 ? range : Pan(range, -deltaPx / width);

    public static DateTimeOffset TimeAt(TimeRange range, double x, double width) =>
        range.From + TimeSpan.FromTicks((long)(range.Duration.Ticks * (width <= 0 ? 0 : x / width)));

    /// <summary>Centres the window on <paramref name="time"/> keeping its length.</summary>
    public static TimeRange CenterOn(TimeRange range, DateTimeOffset time)
    {
        var half = TimeSpan.FromTicks(range.Duration.Ticks / 2);
        return new TimeRange(time - half, time + half);
    }
}
