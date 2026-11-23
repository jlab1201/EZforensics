using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Controls;

/// <summary>
/// The case as a story in time (Findings page): swimlanes (tactic, host or artifact) over a UTC axis, findings as severity-coloured dots or
/// range bars, cluster bands with their count, numbered key-moment pins with captions, "+N" bubbles where findings crowd (click = zoom into
/// them), an overview strip with the visible window, and a "No time" tray for undated findings. Geometry comes from
/// <see cref="FindingsTimelineLayout"/>; this control only paints it (the HexCanvas pattern) and turns the mouse and keys into view changes.
/// Mouse: wheel zooms around the cursor, drag pans, click selects. Keys: Left/Right pan, +/- zoom, Home fits, [ and ] step through findings in time.
/// </summary>
public sealed class FindingsTimeline : FrameworkElement
{
    private const double LabelW = 112, TrayW = 96, PinRowH = 18, AxisH = 20, OverviewH = 26, MaxPinRows = 4, DragThreshold = 4;

    public static readonly DependencyProperty TimelineProperty = DependencyProperty.Register(nameof(Timeline), typeof(SpectreTimeline), typeof(FindingsTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FindingsTimeline)d).OnTimelineChanged()));
    public static readonly DependencyProperty LaneModeProperty = DependencyProperty.Register(nameof(LaneMode), typeof(TimelineLaneMode), typeof(FindingsTimeline),
        new FrameworkPropertyMetadata(TimelineLaneMode.Tactic, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewRangeProperty = DependencyProperty.Register(nameof(ViewRange), typeof(TimeRange), typeof(FindingsTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SelectedFindingIdProperty = DependencyProperty.Register(nameof(SelectedFindingId), typeof(string), typeof(FindingsTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((FindingsTimeline)d).OnSelectionChanged(e.NewValue as string)));
    public static readonly DependencyProperty SelectedMomentIdProperty = DependencyProperty.Register(nameof(SelectedMomentId), typeof(string), typeof(FindingsTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SpectreTimeline? Timeline { get => (SpectreTimeline?)GetValue(TimelineProperty); set => SetValue(TimelineProperty, value); }
    public TimelineLaneMode LaneMode { get => (TimelineLaneMode)GetValue(LaneModeProperty); set => SetValue(LaneModeProperty, value); }
    /// <summary>The visible window (UTC). Null = fit everything; the control writes it back when the examiner zooms or pans.</summary>
    public TimeRange? ViewRange { get => (TimeRange?)GetValue(ViewRangeProperty); set => SetValue(ViewRangeProperty, value); }
    /// <summary>Id (or finding id) of the selected finding; a click sets it, a change from outside scrolls it into view.</summary>
    public string? SelectedFindingId { get => (string?)GetValue(SelectedFindingIdProperty); set => SetValue(SelectedFindingIdProperty, value); }
    /// <summary>Id of the selected key event (its pin is ringed).</summary>
    public string? SelectedMomentId { get => (string?)GetValue(SelectedMomentIdProperty); set => SetValue(SelectedMomentIdProperty, value); }

    /// <summary>Raised when the examiner clicks a key-moment pin (its id).</summary>
    public event Action<string>? MomentClicked;

    private enum HitKind { Item, Bubble, Pin, Band, Tray, TrayDot, Overview, Lane }
    private sealed record Hit(HitKind Kind, Rect Bounds, string? Id = null, LayoutBubble? Bubble = null, LayoutPin? Pin = null, LayoutBand? Band = null);

    private readonly List<Hit> _hits = new();
    private Dictionary<string, SpectreTimelineFinding> _byId = new(StringComparer.OrdinalIgnoreCase);
    private Hit? _hover;
    private Point _hoverPoint, _downPoint;
    private TimeRange? _downRange;
    private bool _down, _dragging, _overviewDrag;
    private Rect _plot, _overview;
    private TimeRange? _fullRange, _drawnRange;
    private int _trayIndex = -1;

    public FindingsTimeline()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(AppTheme _) => InvalidateVisual();

    private void OnTimelineChanged()
    {
        var tl = Timeline;
        _byId = new(StringComparer.OrdinalIgnoreCase);
        if (tl != null)
            foreach (var f in tl.Findings.Concat(tl.Undated))
            {
                _byId.TryAdd(f.Id, f);
                if (f.FindingId.Length > 0) _byId.TryAdd(f.FindingId, f);
            }
        _hover = null;
        _trayIndex = -1;
    }

    private void OnSelectionChanged(string? id)
    {
        // a finding picked elsewhere (moment card, list, chat) is scrolled into view, keeping the zoom
        if (id == null || !_byId.TryGetValue(id, out var f) || f.Start is not { } t) return;
        var range = ViewRange ?? _drawnRange;
        if (range == null) return;
        if (t < range.From || t > range.To) ViewRange = FindingsTimelineLayout.CenterOn(range, t);
    }

    /// <summary>Shows every dated finding.</summary>
    public void Fit()
    {
        if (Timeline is { } tl) ViewRange = FindingsTimelineLayout.FitRange(tl);
    }

    private TimeRange? CurrentRange() => ViewRange ?? (Timeline is { } tl ? FindingsTimelineLayout.FitRange(tl) : null);

    private void SetRange(TimeRange r)
    {
        ViewRange = r;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);

    // ------------------------------------------------------------------ painting

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static Brush Faded(Brush b, double opacity)
    {
        var c = b.Clone();
        c.Opacity = opacity;
        c.Freeze();
        return c;
    }

    private Brush SeverityBrush(string? severity) => SeverityBrushes.Of(severity, Res);

    private FormattedText Text(string s, double size, Brush brush, bool bold = false, double maxWidth = 0)
    {
        var family = TryFindResource("Font.UI") as FontFamily ?? new FontFamily("Segoe UI");
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 1) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        double w = ActualWidth, h = ActualHeight;
        var bg = Res("Brush.Surface", Brushes.Black);
        var text = Res("Brush.Text", Brushes.White);
        var dim = Res("Brush.TextSecondary", Brushes.Gray);
        var border = Res("Brush.Border", Brushes.DimGray);
        var accent = Res("Brush.Accent", Brushes.SteelBlue);
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        if (w < 80 || h < 60) return;

        var tl = Timeline;
        bool tray = tl != null && tl.Undated.Count > 0;
        double plotL = LabelW, plotR = w - (tray ? TrayW + 8 : 8);
        double plotW = Math.Max(20, plotR - plotL);
        double overviewTop = h - OverviewH, axisTop = overviewTop - AxisH - 4;

        if (tray) DrawTray(dc, tl!, new Rect(plotR + 8, 4, TrayW - 4, Math.Max(20, axisTop - 4)), text, dim, border);

        var range = CurrentRange();
        _drawnRange = range;
        if (tl == null || (tl.Findings.Count == 0 && tl.Undated.Count == 0))
        {
            Centered(dc, "No timeline yet. It appears when Spectre has analysed the case.", new Rect(0, 0, w, h), dim);
            DrawFocus(dc, w, h, accent);
            return;
        }
        if (range == null)
        {
            Centered(dc, "None of the findings has a time, so there is nothing to place on the axis.", new Rect(0, 0, plotR, h), dim);
            DrawFocus(dc, w, h, accent);
            DrawTooltip(dc, w, h, text, border);
            return;
        }

        var first = FindingsTimelineLayout.Compute(tl, LaneMode, range, plotW, 20);
        int pinRows = (int)Math.Min(MaxPinRows, first.PinRows);
        double lanesTop = 4 + pinRows * PinRowH + (pinRows > 0 ? 6 : 0);
        double lanesH = Math.Max(10, axisTop - lanesTop);
        int nLanes = first.Lanes.Count;
        double laneH = nLanes == 0 ? lanesH : Math.Clamp(lanesH / nLanes, 12, 28);
        int shownLanes = nLanes == 0 ? 0 : Math.Min(nLanes, (int)Math.Floor(lanesH / laneH + 0.001));
        var lay = FindingsTimelineLayout.Compute(tl, LaneMode, range, plotW, laneH);
        _plot = new Rect(plotL, lanesTop, plotW, Math.Max(1, axisTop - lanesTop));
        double lanesBottom = lanesTop + shownLanes * laneH;

        // lanes
        var rowAlt = Res("Brush.RowAlt", Faded(border, 0.25));
        foreach (var lane in lay.Lanes)
        {
            if (lane.Index >= shownLanes) break;
            double y = lanesTop + lane.Y;
            if (lane.Index % 2 == 1) dc.DrawRectangle(rowAlt, null, new Rect(plotL, y, plotW, laneH));
            var label = Text(PlainLane(LaneMode, lane.Name), 11, dim, maxWidth: LabelW - 10);
            dc.DrawText(label, new Point(4, y + (laneH - label.Height) / 2));
            _hits.Add(new Hit(HitKind.Lane, new Rect(0, y, LabelW - 4, laneH), Id: lane.Name));
        }
        if (shownLanes < nLanes)
            dc.DrawText(Text($"+{nLanes - shownLanes} more lanes", 10, dim, maxWidth: LabelW - 10), new Point(4, lanesBottom + 1));

        dc.PushClip(new RectangleGeometry(new Rect(plotL, 0, plotW, axisTop + 1)));

        // ticks (grid lines)
        var tickPen = new Pen(Faded(border, 0.6), 1); tickPen.Freeze();
        var majorPen = new Pen(Res("Brush.BorderStrong", border), 1); majorPen.Freeze();
        foreach (var t in lay.Ticks)
            dc.DrawLine(t.Major ? majorPen : tickPen, new Point(plotL + t.X + 0.5, lanesTop), new Point(plotL + t.X + 0.5, axisTop));

        // cluster bands
        var bandFill = Faded(accent, 0.12);
        foreach (var b in lay.Bands)
        {
            var r = new Rect(plotL + b.X1, lanesTop, Math.Max(2, b.X2 - b.X1), Math.Max(1, lanesBottom - lanesTop));
            dc.DrawRectangle(bandFill, null, r);
            if (r.Width >= 16 && b.Count > 0) dc.DrawText(Text(b.Count.ToString(CultureInfo.InvariantCulture), 9, accent), new Point(r.X + 2, r.Y + 1));
            _hits.Add(new Hit(HitKind.Band, r, Band: b));
        }

        // pins: vertical guide lines first, so dots draw over them
        var guide = new Pen(Faded(accent, 0.55), 1) { DashStyle = DashStyles.Dash }; guide.Freeze();
        foreach (var p in lay.Pins)
        {
            double cy = PinY(lanesTop, p.Row);
            dc.DrawLine(guide, new Point(plotL + p.X + 0.5, cy + 7), new Point(plotL + p.X + 0.5, lanesBottom));
        }

        // range bars and dots
        var sel = SelectedTimelineId();
        var ring = new Pen(text, 2); ring.Freeze();
        foreach (var it in lay.Items)
        {
            if (it.LaneIndex >= shownLanes) continue;
            var brush = SeverityBrush(it.Severity);
            double y = lanesTop + it.Y;
            bool isSel = sel != null && string.Equals(it.FindingId, sel, StringComparison.OrdinalIgnoreCase);
            Rect bounds;
            if (it.IsRange)
            {
                bounds = new Rect(plotL + it.X, y - 3.5, Math.Max(4, it.X2 - it.X), 7);
                dc.DrawRoundedRectangle(brush, isSel ? ring : null, bounds, 3, 3);
            }
            else
            {
                double r = SpectreFinding.Rank(it.Severity) >= 4 ? 5.5 : 4.5;
                dc.DrawEllipse(brush, null, new Point(plotL + it.X, y), r, r);
                if (isSel) dc.DrawEllipse(null, ring, new Point(plotL + it.X, y), r + 3, r + 3);
                bounds = new Rect(plotL + it.X - r - 2, y - r - 2, 2 * r + 4, 2 * r + 4);
            }
            _hits.Add(new Hit(HitKind.Item, bounds, Id: it.FindingId));
        }

        // "+N" bubbles
        foreach (var b in lay.Bubbles)
        {
            if (b.LaneIndex >= shownLanes) continue;
            var brush = SeverityBrush(b.MaxSeverity);
            var label = Text("+" + b.Count.ToString(CultureInfo.InvariantCulture), 9.5, text, bold: true);
            double rw = Math.Max(18, label.Width + 8), rh = Math.Min(laneH - 2, 16);
            var r = new Rect(plotL + b.X - rw / 2, lanesTop + b.Y - rh / 2, rw, rh);
            var pen = new Pen(brush, 1.2); pen.Freeze();
            dc.DrawRoundedRectangle(Faded(brush, 0.28), pen, r, rh / 2, rh / 2);
            dc.DrawText(label, new Point(r.X + (rw - label.Width) / 2, r.Y + (rh - label.Height) / 2));
            if (sel != null && b.FindingIds.Contains(sel, StringComparer.OrdinalIgnoreCase)) dc.DrawRoundedRectangle(null, ring, Inflate(r, 2), rh / 2 + 2, rh / 2 + 2);
            _hits.Add(new Hit(HitKind.Bubble, r, Bubble: b));
        }

        // pins with numbers and captions
        var accentText = Res("Brush.AccentText", Brushes.White);
        foreach (var p in lay.Pins)
        {
            double cy = PinY(lanesTop, p.Row), cx = plotL + p.X;
            bool isSel = (SelectedMomentId != null && p.MomentId == SelectedMomentId) ||
                         (sel != null && p.PrimaryFindingId != null && _byId.TryGetValue(p.PrimaryFindingId, out var pf) && string.Equals(pf.Id, sel, StringComparison.OrdinalIgnoreCase));
            dc.DrawEllipse(accent, isSel ? ring : null, new Point(cx, cy), 7.5, 7.5);
            var num = Text(p.Number.ToString(CultureInfo.InvariantCulture), 10, accentText, bold: true);
            dc.DrawText(num, new Point(cx - num.Width / 2, cy - num.Height / 2));
            var cap = Text(p.Label, 11, isSel ? text : dim, bold: isSel, maxWidth: Math.Max(10, p.LabelWidth - 14));
            dc.DrawText(cap, new Point(cx + 11, cy - cap.Height / 2));
            _hits.Add(new Hit(HitKind.Pin, new Rect(cx - 9, cy - 9, Math.Max(18, p.LabelWidth), 18), Pin: p));
        }
        dc.Pop();

        // axis
        var axisPen = new Pen(Res("Brush.BorderStrong", border), 1); axisPen.Freeze();
        dc.DrawLine(axisPen, new Point(plotL, axisTop + 0.5), new Point(plotR, axisTop + 0.5));
        dc.DrawText(Text(FindingsTimelineLayout.AxisLabel, 11, text, bold: true), new Point(4, axisTop + 3));
        double lastRight = double.NegativeInfinity;
        foreach (var t in lay.Ticks)
        {
            var label = Text(t.Label, 10, t.Major ? text : dim, bold: t.Major);
            double x = plotL + t.X + 3;
            if (x < lastRight + 6 || x + label.Width > plotR) continue;
            dc.DrawText(label, new Point(x, axisTop + 3));
            lastRight = x + label.Width;
        }

        DrawOverview(dc, tl, range, new Rect(plotL, overviewTop, plotW, OverviewH - 4), text, dim, border, accent);
        DrawFocus(dc, w, h, accent);
        DrawTooltip(dc, w, h, text, border);
    }

    private static double PinY(double lanesTop, int row) => lanesTop - 6 - (Math.Min(row, MaxPinRows - 1) + 0.5) * PinRowH;

    private static Rect Inflate(Rect r, double by) { r.Inflate(by, by); return r; }

    private string? SelectedTimelineId()
    {
        var id = SelectedFindingId;
        if (id == null) return null;
        return _byId.TryGetValue(id, out var f) ? f.Id : id;
    }

    private void Centered(DrawingContext dc, string s, Rect r, Brush brush)
    {
        var ft = Text(s, 12, brush);
        ft.MaxTextWidth = Math.Max(40, r.Width - 24);
        ft.TextAlignment = TextAlignment.Center;
        dc.DrawText(ft, new Point(r.X + 12, r.Y + (r.Height - ft.Height) / 2));
    }

    private void DrawTray(DrawingContext dc, SpectreTimeline tl, Rect r, Brush text, Brush dim, Brush border)
    {
        var pen = new Pen(border, 1) { DashStyle = DashStyles.Dash }; pen.Freeze();
        dc.DrawRoundedRectangle(null, pen, r, 4, 4);
        var title = Text($"No time ({tl.Undated.Count})", 11, text, bold: true, maxWidth: r.Width - 8);
        dc.DrawText(title, new Point(r.X + 5, r.Y + 4));
        _hits.Add(new Hit(HitKind.Tray, new Rect(r.X, r.Y, r.Width, title.Height + 8)));
        double x0 = r.X + 9, y = r.Y + title.Height + 14, x = x0;
        var sel = SelectedTimelineId();
        var ring = new Pen(text, 2); ring.Freeze();
        foreach (var f in tl.Undated.OrderByDescending(f => f.SeverityRank))
        {
            if (y > r.Bottom - 6) { dc.DrawText(Text("…", 11, dim), new Point(r.Right - 14, r.Bottom - 16)); break; }
            dc.DrawEllipse(SeverityBrush(f.Severity), null, new Point(x, y), 4.5, 4.5);
            if (sel != null && string.Equals(f.Id, sel, StringComparison.OrdinalIgnoreCase)) dc.DrawEllipse(null, ring, new Point(x, y), 7.5, 7.5);
            _hits.Add(new Hit(HitKind.TrayDot, new Rect(x - 6, y - 6, 12, 12), Id: f.Id));
            x += 15;
            if (x > r.Right - 8) { x = x0; y += 15; }
        }
    }

    private void DrawOverview(DrawingContext dc, SpectreTimeline tl, TimeRange view, Rect r, Brush text, Brush dim, Brush border, Brush accent)
    {
        _overview = r;
        var fit = FindingsTimelineLayout.FitRange(tl) ?? view;
        var full = new TimeRange(fit.From < view.From ? fit.From : view.From, fit.To > view.To ? fit.To : view.To);
        _fullRange = full;
        double ms = Math.Max(1, full.Duration.TotalMilliseconds);
        double X(DateTimeOffset t) => r.X + (t - full.From).TotalMilliseconds / ms * r.Width;
        var pen = new Pen(border, 1); pen.Freeze();
        dc.DrawRoundedRectangle(Faded(border, 0.18), pen, r, 3, 3);
        dc.DrawText(Text(view.From.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 10, dim, maxWidth: LabelW - 8), new Point(4, r.Y + (r.Height - 13) / 2));
        foreach (var f in tl.Findings)
        {
            if (f.Start is not { } s) continue;
            var p = new Pen(Faded(SeverityBrush(f.Severity), 0.75), 1); p.Freeze();
            double x = Math.Round(X(s)) + 0.5;
            dc.DrawLine(p, new Point(x, r.Y + 5), new Point(x, r.Bottom - 5));
        }
        foreach (var m in tl.Moments)
        {
            if (m.Time is not { } t) continue;
            double x = X(t);
            var tri = new StreamGeometry();
            using (var g = tri.Open()) { g.BeginFigure(new Point(x - 3.5, r.Y + 1), true, true); g.LineTo(new Point(x + 3.5, r.Y + 1), true, false); g.LineTo(new Point(x, r.Y + 6), true, false); }
            tri.Freeze();
            dc.DrawGeometry(accent, null, tri);
        }
        double x1 = X(view.From), x2 = X(view.To);
        if (x2 - x1 < 4) { double c = (x1 + x2) / 2; x1 = c - 2; x2 = c + 2; }
        var winPen = new Pen(accent, 1.5); winPen.Freeze();
        dc.DrawRectangle(Faded(Res("Brush.Selection", accent), 0.35), winPen, new Rect(x1, r.Y + 1, x2 - x1, r.Height - 2));
    }

    private void DrawFocus(DrawingContext dc, double w, double h, Brush accent)
    {
        if (!IsKeyboardFocused) return;
        var pen = new Pen(accent, 1) { DashStyle = DashStyles.Dot }; pen.Freeze();
        dc.DrawRectangle(null, pen, new Rect(0.5, 0.5, w - 1, h - 1));
    }

    private void DrawTooltip(DrawingContext dc, double w, double h, Brush text, Brush border)
    {
        if (_hover == null || _down) return;
        var lines = TooltipLines(_hover);
        if (lines.Count == 0) return;
        var fts = lines.Select((l, i) => Text(l, i == 0 ? 12 : 11, i == 0 ? text : Res("Brush.TextSecondary", text), bold: i == 0, maxWidth: 360)).ToList();
        double tw = fts.Max(f => f.Width) + 16, th = fts.Sum(f => f.Height) + 10;
        double x = _hoverPoint.X + 14, y = _hoverPoint.Y + 16;
        if (x + tw > w - 2) x = Math.Max(2, _hoverPoint.X - tw - 8);
        if (y + th > h - 2) y = Math.Max(2, _hoverPoint.Y - th - 8);
        var pen = new Pen(border, 1); pen.Freeze();
        dc.DrawRoundedRectangle(Res("Brush.SurfaceAlt", Brushes.Black), pen, new Rect(x, y, tw, th), 4, 4);
        double yy = y + 5;
        foreach (var ft in fts) { dc.DrawText(ft, new Point(x + 8, yy)); yy += ft.Height; }
    }

    internal static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    private List<string> TooltipLines(Hit hit)
    {
        var lines = new List<string>();
        switch (hit.Kind)
        {
            case HitKind.Item or HitKind.TrayDot when hit.Id != null && _byId.TryGetValue(hit.Id, out var f):
                lines.Add($"{f.Id} · {f.Title}");
                lines.Add(f.Start is not { } s ? "No time" : f.IsRange ? $"{Utc(s)} to {Utc(f.End!.Value)}" : Utc(s));
                lines.Add($"Severity: {f.Severity}" + (f.OccurrenceCount > 1 ? $" · seen {f.OccurrenceCount} times" : "") + $" · {LaneWithTerm(LaneMode, f.Lane(LaneMode))}");
                break;
            case HitKind.Bubble when hit.Bubble is { } b:
                lines.Add($"{b.Count} findings close together");
                lines.Add(b.From == b.To ? Utc(b.From) : $"{Utc(b.From)} to {Utc(b.To)}");
                lines.Add($"Highest severity: {b.MaxSeverity} · click to zoom in");
                break;
            case HitKind.Pin when hit.Pin is { } p:
                var m = Timeline?.Moments.FirstOrDefault(x => x.Id == p.MomentId);
                lines.Add($"Key event {p.Number}: {p.Label}");
                if (m?.Time is { } mt) lines.Add(Utc(mt));
                if (!string.IsNullOrWhiteSpace(m?.Reason)) lines.Add(m!.Reason!);
                break;
            case HitKind.Band when hit.Band is { } band:
                lines.Add(string.IsNullOrWhiteSpace(band.Label) ? $"Cluster of {band.Count} findings" : band.Label!);
                lines.Add($"{band.Kind} cluster · {band.Count} findings");
                break;
            case HitKind.Lane when hit.Id is { } lane:
                lines.Add(PlainLane(LaneMode, lane));
                lines.Add(LaneMode switch { TimelineLaneMode.Tactic => "MITRE tactic: ", TimelineLaneMode.Artifact => "Artifact type: ", _ => "Host: " } + lane);
                break;
            case HitKind.Tray when Timeline is { } tl:
                lines.Add($"{tl.Undated.Count} findings without a time");
                lines.Add("Click to select them one after the other");
                break;
        }
        return lines;
    }

    /// <summary>The lane's name in plain words ("Stealing passwords", "Windows security log"); hosts stay as they are.</summary>
    internal static string PlainLane(TimelineLaneMode mode, string lane) => mode switch
    {
        TimelineLaneMode.Tactic => lane == SpectreTimeline.NoLane ? PlainLanguage.OtherActivity : PlainLanguage.Tactic(lane),
        TimelineLaneMode.Artifact => lane == SpectreTimeline.NoLane ? "Other source" : PlainLanguage.Artifact(lane),
        _ => lane == SpectreTimeline.NoLane ? "Unknown host" : lane,
    };

    /// <summary>"Stealing passwords (Credential Access)": the plain name with the technical term when they differ.</summary>
    internal static string LaneWithTerm(TimelineLaneMode mode, string lane)
    {
        var plain = PlainLane(mode, lane);
        return string.Equals(plain, lane, StringComparison.OrdinalIgnoreCase) || lane == SpectreTimeline.NoLane ? plain : $"{plain} ({lane})";
    }

    // ------------------------------------------------------------------ input

    private Hit? HitAt(Point p)
    {
        // pins and findings before bubbles before bands (drawn later = on top)
        foreach (var kind in new[] { HitKind.Pin, HitKind.Item, HitKind.TrayDot, HitKind.Bubble, HitKind.Tray, HitKind.Band, HitKind.Lane })
            for (int i = _hits.Count - 1; i >= 0; i--)
                if (_hits[i].Kind == kind && _hits[i].Bounds.Contains(p)) return _hits[i];
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _downPoint = e.GetPosition(this);
        _downRange = CurrentRange();
        _down = true; _dragging = false;
        _overviewDrag = _overview.Contains(_downPoint) && _fullRange != null;
        if (_overviewDrag) CenterFromOverview(_downPoint.X);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_down)
        {
            if (_overviewDrag) { CenterFromOverview(p.X); return; }
            if (!_dragging && Math.Abs(p.X - _downPoint.X) < DragThreshold) return;
            _dragging = true;
            Cursor = Cursors.SizeWE;
            if (_downRange != null) SetRange(FindingsTimelineLayout.PanByPixels(_downRange, p.X - _downPoint.X, _plot.Width));
            return;
        }
        var hit = HitAt(p);
        _hoverPoint = p;
        if (!Equals(hit, _hover) || hit != null)
        {
            _hover = hit;
            Cursor = hit is { Kind: HitKind.Item or HitKind.Pin or HitKind.Bubble or HitKind.TrayDot or HitKind.Tray } ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null) { _hover = null; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool click = _down && !_dragging && !_overviewDrag;
        _down = false; _dragging = false; _overviewDrag = false;
        Cursor = null;
        ReleaseMouseCapture();
        if (click) Click(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    private void Click(Point p)
    {
        switch (HitAt(p))
        {
            case { Kind: HitKind.Pin, Pin: { } pin }:
                if (pin.PrimaryFindingId != null) SelectedFindingId = _byId.TryGetValue(pin.PrimaryFindingId, out var f) ? f.Id : pin.PrimaryFindingId;
                MomentClicked?.Invoke(pin.MomentId);
                break;
            case { Kind: HitKind.Item or HitKind.TrayDot, Id: { } id }:
                SelectedFindingId = id;
                break;
            case { Kind: HitKind.Bubble, Bubble: { } b }:
                ZoomInto(b.From, b.To);
                break;
            case { Kind: HitKind.Tray }:
                SelectNextUndated();
                break;
        }
    }

    private void ZoomInto(DateTimeOffset from, DateTimeOffset to)
    {
        if (CurrentRange() is not { } range) return;
        var span = to - from;
        if (span < TimeSpan.FromSeconds(2)) { SetRange(FindingsTimelineLayout.Zoom(range, from, 8)); return; }
        var pad = TimeSpan.FromTicks(span.Ticks / 4);
        SetRange(new TimeRange(from - pad, to + pad));
    }

    private void SelectNextUndated()
    {
        if (Timeline is not { Undated.Count: > 0 } tl) return;
        var list = tl.Undated.OrderByDescending(f => f.SeverityRank).ToList();
        _trayIndex = (_trayIndex + 1) % list.Count;
        SelectedFindingId = list[_trayIndex].Id;
    }

    private void CenterFromOverview(double x)
    {
        if (_fullRange is not { } full || CurrentRange() is not { } range || _overview.Width <= 0) return;
        var t = FindingsTimelineLayout.TimeAt(full, Math.Clamp(x - _overview.X, 0, _overview.Width), _overview.Width);
        SetRange(FindingsTimelineLayout.CenterOn(range, t));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (CurrentRange() is not { } range) return;
        var p = e.GetPosition(this);
        var anchor = _plot.Width > 0 && p.X >= _plot.X && p.X <= _plot.Right
            ? FindingsTimelineLayout.TimeAt(range, p.X - _plot.X, _plot.Width)
            : range.From + TimeSpan.FromTicks(range.Duration.Ticks / 2);
        SetRange(FindingsTimelineLayout.Zoom(range, anchor, e.Delta > 0 ? 1.25 : 0.8));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (CurrentRange() is not { } range) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var center = range.From + TimeSpan.FromTicks(range.Duration.Ticks / 2);
        switch (e.Key)
        {
            case Key.Left: SetRange(FindingsTimelineLayout.Pan(range, shift ? -0.5 : -0.1)); break;
            case Key.Right: SetRange(FindingsTimelineLayout.Pan(range, shift ? 0.5 : 0.1)); break;
            case Key.OemPlus or Key.Add or Key.Up: SetRange(FindingsTimelineLayout.Zoom(range, center, 1.25)); break;
            case Key.OemMinus or Key.Subtract or Key.Down: SetRange(FindingsTimelineLayout.Zoom(range, center, 0.8)); break;
            case Key.Home or Key.D0 or Key.NumPad0: Fit(); break;
            case Key.OemOpenBrackets: StepSelection(-1); break;
            case Key.OemCloseBrackets: StepSelection(1); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Selects the previous or next dated finding in time order.</summary>
    private void StepSelection(int dir)
    {
        if (Timeline is not { } tl) return;
        var ordered = tl.Findings.Where(f => f.Start != null).OrderBy(f => f.Start).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0) return;
        var cur = SelectedTimelineId();
        int i = cur == null ? -1 : ordered.FindIndex(f => string.Equals(f.Id, cur, StringComparison.OrdinalIgnoreCase));
        i = i < 0 ? (dir > 0 ? 0 : ordered.Count - 1) : Math.Clamp(i + dir, 0, ordered.Count - 1);
        SelectedFindingId = ordered[i].Id;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
}
