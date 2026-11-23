using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EzForensics.App.Services;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Controls;

/// <summary>One key event in the attack flow graph.</summary>
public sealed record FlowNode(string Id, int Number, string Title, DateTimeOffset? Time, string Tactic, string Severity, string Glyph, string? Host, IReadOnlyList<string>? Details = null);

/// <summary>Where one node sits (column = tactic, row = position in time) and the arrows between them.</summary>
public sealed record FlowLayout(IReadOnlyList<string> Columns, IReadOnlyList<FlowPlacement> Items, IReadOnlyList<(int From, int To)> Edges, double Width, double Height);

public sealed record FlowPlacement(FlowNode Node, int Column, int Row, Rect Bounds);

/// <summary>
/// The key events as a flow graph. Columns are the ATT&amp;CK tactics present, in kill-chain order; every event is a card in its tactic's column,
/// one row per event in time order. Arrows join each event to the next one in time: they show the order only ("then"), not a cause.
/// Cards and arrows appear one by one when the view opens (see <see cref="Replay"/>); hover lights a card and its arrows; click selects
/// (<see cref="NodeClicked"/>), double click opens it in Details (<see cref="NodeDoubleClicked"/>).
/// </summary>
public sealed class AttackFlowGraph : FrameworkElement
{
    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(nameof(Nodes), typeof(IReadOnlyList<FlowNode>), typeof(AttackFlowGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((AttackFlowGraph)d).OnNodesChanged()));
    public IReadOnlyList<FlowNode>? Nodes { get => (IReadOnlyList<FlowNode>?)GetValue(NodesProperty); set => SetValue(NodesProperty, value); }

    /// <summary>Shows the computer on each card (when the page shows all computers).</summary>
    public static readonly DependencyProperty ShowHostProperty = DependencyProperty.Register(nameof(ShowHost), typeof(bool), typeof(AttackFlowGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ShowHost { get => (bool)GetValue(ShowHostProperty); set => SetValue(ShowHostProperty, value); }

    public static readonly DependencyProperty SelectedIdProperty = DependencyProperty.Register(nameof(SelectedId), typeof(string), typeof(AttackFlowGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public string? SelectedId { get => (string?)GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }

    /// <summary>Animation position: card i is in at i + 0.6, the arrow after it at i + 1. Equal to the node count when finished.</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(AttackFlowGraph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((AttackFlowGraph)d).UpdateMotion()));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public event Action<string>? NodeClicked;
    public event Action<string>? NodeDoubleClicked;

    internal const double CardW = 196, CardH = 60, HostExtra = 14, ColGap = 28, RowGap = 26, HeaderH = 30, Pad = 16;

    private readonly List<(int Index, Rect Bounds)> _hits = new();
    private readonly List<(int Edge, List<Point> Points)> _edgeHits = new();
    private int _hover = -1, _hoverEdge = -1;
    private Point _mouse;
    private FlowLayout _layout = Build(Array.Empty<FlowNode>(), false);

    public AttackFlowGraph()
    {
        SnapsToDevicePixels = false;
        Focusable = false;
        Loaded += (_, _) => { ThemeManager.Changed += OnThemeChanged; Replay(); UpdateMotion(); };
        Unloaded += (_, _) => { ThemeManager.Changed -= OnThemeChanged; UpdateMotion(); };
        IsVisibleChanged += (_, _) => UpdateMotion();
    }

    private void OnThemeChanged(AppTheme _) { ClearCaches(); InvalidateVisual(); }

    // ------------------------------------------------------------------ motion (pulse, flowing dashes, arrival glow)

    internal const double EdgeSeconds = 1.8, EndPause = 1.2, GlowSeconds = 0.9, TrailStep = 0.08;

    private readonly Stopwatch _clock = new();
    private double _t, _lastTick;           // seconds on the pulse cycle's own clock; _lastTick = _clock reading at the previous frame
    private bool _subscribed, _forceMotion;

    /// <summary>One pulse cycle: the pulse crosses each edge in turn, then everything rests for <see cref="EndPause"/>.</summary>
    internal static double CycleSeconds(int edgeCount) => edgeCount * EdgeSeconds + EndPause;

    /// <summary>Whether continuous motion can run at all: two or more nodes and animations on.</summary>
    internal static bool MotionPossible(int nodeCount, bool animationsOn) => nodeCount >= 2 && animationsOn;

    private static double EaseInOut(double u) => u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;

    /// <summary>Where the pulse is at time <paramref name="t"/> (wrapped into the cycle): the edge it is on and how far along it (eased, 0..1); null in the end pause.</summary>
    internal static (int Edge, double Along, double Raw)? PulseAt(double t, int edgeCount)
    {
        if (edgeCount <= 0) return null;
        double cyc = CycleSeconds(edgeCount);
        t %= cyc; if (t < 0) t += cyc;
        if (t >= edgeCount * EdgeSeconds) return null;
        int edge = Math.Min(edgeCount - 1, (int)(t / EdgeSeconds));
        double raw = (t - edge * EdgeSeconds) / EdgeSeconds;
        return (edge, EaseInOut(raw), raw);
    }

    /// <summary>The arrival glow of card <paramref name="node"/> at time <paramref name="t"/>: 1 as the pulse reaches it, fading to 0 over <see cref="GlowSeconds"/>.</summary>
    internal static double GlowAt(int node, double t, int edgeCount)
    {
        if (edgeCount <= 0) return 0;
        double cyc = CycleSeconds(edgeCount);
        t %= cyc; if (t < 0) t += cyc;
        double age = t - node * EdgeSeconds;
        return age < 0 || age >= GlowSeconds ? 0 : 1 - age / GlowSeconds;
    }

    /// <summary>The point at fraction <paramref name="s"/> (0..1) of the polyline's length.</summary>
    internal static Point PointAlong(IReadOnlyList<Point> pts, double s)
    {
        if (pts.Count == 0) return default;
        if (pts.Count == 1 || s <= 0) return pts[0];
        double total = 0;
        for (int i = 1; i < pts.Count; i++) total += (pts[i] - pts[i - 1]).Length;
        if (s >= 1 || total < 1e-9) return pts[^1];
        double want = s * total, acc = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var seg = pts[i] - pts[i - 1]; double len = seg.Length;
            if (acc + len >= want) return len < 1e-9 ? pts[i] : pts[i - 1] + seg * ((want - acc) / len);
            acc += len;
        }
        return pts[^1];
    }

    private bool MotionOn => _forceMotion || (Progress >= _layout.Items.Count && MotionPossible(_layout.Items.Count, Motion.Enabled));

    /// <summary>Snapshot hook: shows the motion frozen at <paramref name="seconds"/> into the cycle (no timer, ignores the animation setting).</summary>
    internal void SetAnimationClock(double seconds)
    {
        _forceMotion = true; _t = seconds;
        Unsubscribe();
        InvalidateVisual();
    }

    /// <summary>Starts or stops the frame timer: runs only while loaded, visible, finished building, not hovered, with two or more nodes and animations on.</summary>
    private void UpdateMotion()
    {
        bool run = !_forceMotion && IsLoaded && IsVisible && _hover < 0 && _hoverEdge < 0 && MotionOn;
        if (run && !_subscribed)
        {
            _clock.Restart(); _lastTick = 0; _subscribed = true;
            CompositionTarget.Rendering += OnFrame;
        }
        else if (!run && _subscribed) Unsubscribe();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnFrame;
        _subscribed = false; _clock.Stop();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        _t += Math.Min(0.1, now - _lastTick);
        _lastTick = now;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ caches (nothing is allocated per frame)

    private readonly Dictionary<(Brush, double), Brush> _fades = new();
    private readonly Dictionary<(Brush, double), Pen> _pens = new();
    private readonly Dictionary<Color, Brush> _glows = new(), _solids = new();
    private readonly Dictionary<(string, double, Brush, bool, double, bool), FormattedText> _texts = new();
    private Pen? _dashPen; private DashStyle? _dashStyle;
    private FlowLayout? _geoFor;
    private PathGeometry[] _edgeGeo = Array.Empty<PathGeometry>(), _headGeo = Array.Empty<PathGeometry>();
    private List<Point>[] _edgePts = Array.Empty<List<Point>>();

    private void ClearCaches() { _fades.Clear(); _pens.Clear(); _glows.Clear(); _solids.Clear(); _texts.Clear(); _dashPen = null; _dashStyle = null; }

    private Brush Fade(Brush b, double o)
    {
        if (!_fades.TryGetValue((b, o), out var f)) _fades[(b, o)] = f = WithOpacity(b, o);
        return f;
    }

    private Pen PenOf(Brush b, double th)
    {
        if (!_pens.TryGetValue((b, th), out var p)) { p = new Pen(b, th) { LineJoin = PenLineJoin.Round }; _pens[(b, th)] = p; }
        return p;
    }

    private Brush SolidOf(Color c)
    {
        if (!_solids.TryGetValue(c, out var b)) { b = new SolidColorBrush(c); b.Freeze(); _solids[c] = b; }
        return b;
    }

    private Brush GlowOf(Color c)
    {
        if (_glows.TryGetValue(c, out var b)) return b;
        var g = new RadialGradientBrush();
        g.GradientStops.Add(new GradientStop(Color.FromArgb(235, c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(110, c.R, c.G, c.B), 0.5));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        g.Freeze(); _glows[c] = g;
        return g;
    }

    /// <summary>Edge polylines, their frozen outlines and arrowheads, rebuilt only when the layout changes.</summary>
    private void EnsureGeometry(FlowLayout lay)
    {
        if (ReferenceEquals(_geoFor, lay)) return;
        _geoFor = lay;
        int n = lay.Edges.Count;
        _edgeGeo = new PathGeometry[n]; _headGeo = new PathGeometry[n]; _edgePts = new List<Point>[n];
        for (int e = 0; e < n; e++)
        {
            var pts = EdgePoints(lay.Items[lay.Edges[e].From].Bounds, lay.Items[lay.Edges[e].To].Bounds);
            _edgePts[e] = pts;
            var fig = new PathFigure { StartPoint = pts[0] };
            for (int i = 1; i < pts.Count; i++) fig.Segments.Add(new LineSegment(pts[i], true));
            var g = new PathGeometry(new[] { fig }); g.Freeze(); _edgeGeo[e] = g;
            var tip = pts[^1]; var v = tip - pts[^2]; v.Normalize();   // along the last segment
            var nrm = new Vector(-v.Y, v.X);
            var head = new PathFigure { StartPoint = tip, IsClosed = true };
            head.Segments.Add(new LineSegment(tip - v * 9 + nrm * 4.5, true));
            head.Segments.Add(new LineSegment(tip - v * 9 - nrm * 4.5, true));
            var hg = new PathGeometry(new[] { head }); hg.Freeze(); _headGeo[e] = hg;
        }
    }

    // ------------------------------------------------------------------ layout (internal for tests)

    internal FlowLayout Layout => _layout;

    /// <summary>Events in time order (undated last), one column per tactic present in kill-chain order, one row per event; edges join event i to i + 1.</summary>
    internal static FlowLayout Build(IEnumerable<FlowNode> nodes, bool showHost)
    {
        var sorted = nodes.OrderBy(n => n.Time == null ? 1 : 0).ThenBy(n => n.Time).ThenBy(n => n.Number).ToList();
        int Order(string t) { int i = IndexOfTactic(t); return i < 0 ? StoryGraphics.StageOrder.Count : i; }
        var columns = sorted.Select(n => Canonical(n.Tactic)).Distinct().OrderBy(Order).ToList();
        double h = CardH + (showHost ? HostExtra : 0);
        var items = new List<FlowPlacement>();
        for (int i = 0; i < sorted.Count; i++)
        {
            int col = columns.IndexOf(Canonical(sorted[i].Tactic));
            var r = new Rect(Pad + col * (CardW + ColGap), Pad + HeaderH + i * (h + RowGap), CardW, h);
            items.Add(new FlowPlacement(sorted[i], col, i, r));
        }
        var edges = Enumerable.Range(0, Math.Max(0, sorted.Count - 1)).Select(i => (i, i + 1)).ToList();
        double w = columns.Count == 0 ? 0 : 2 * Pad + columns.Count * CardW + (columns.Count - 1) * ColGap;
        double ht = sorted.Count == 0 ? 0 : 2 * Pad + HeaderH + sorted.Count * h + (sorted.Count - 1) * RowGap;
        return new FlowLayout(columns, items, edges, w, ht);
    }

    private static int IndexOfTactic(string t) => StoryGraphics.StageOrder.ToList().FindIndex(s => string.Equals(s, t, StringComparison.OrdinalIgnoreCase));
    private static string Canonical(string? t)
    {
        var name = PlainLanguage.Tactic(t);
        return IndexOfTactic(name) >= 0 ? StoryGraphics.StageOrder[IndexOfTactic(name)] : PlainLanguage.OtherActivity;
    }

    /// <summary>Arrow from card <paramref name="a"/> to card <paramref name="b"/>: bottom centre to top centre, a vertical line in one column, an S curve across columns.</summary>
    internal static List<Point> EdgePoints(Rect a, Rect b, int samples = 24)
    {
        var p0 = new Point(a.X + a.Width / 2, a.Bottom);
        var p3 = new Point(b.X + b.Width / 2, b.Y - 2);
        double dy = Math.Max(10, (p3.Y - p0.Y) * 0.6);
        var p1 = new Point(p0.X, p0.Y + dy);
        var p2 = new Point(p3.X, p3.Y - dy);
        var pts = new List<Point>(samples + 1);
        for (int i = 0; i <= samples; i++)
        {
            double t = (double)i / samples, u = 1 - t;
            pts.Add(new Point(
                u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y));
        }
        return pts;
    }

    // ------------------------------------------------------------------ data + animation

    private void OnNodesChanged()
    {
        _layout = Build(Nodes ?? Array.Empty<FlowNode>(), ShowHost);
        _hover = -1; _hoverEdge = -1;
        _texts.Clear(); _t = 0;
        if (IsLoaded) Replay();
        else Progress = _layout.Items.Count;
        UpdateMotion();
    }

    /// <summary>Plays the build-up again: cards pop in one by one in time order and each arrow draws itself (about 250 ms a step, 4 s at most). Instant with animations off.</summary>
    public void Replay()
    {
        _layout = Build(Nodes ?? Array.Empty<FlowNode>(), ShowHost);
        int n = _layout.Items.Count;
        BeginAnimation(ProgressProperty, null);
        if (n == 0 || !Motion.Enabled) { Progress = n; return; }
        Progress = 0;
        var ms = Math.Min(4000, 250 * n);
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, n, new Duration(TimeSpan.FromMilliseconds(ms))) { FillBehavior = FillBehavior.HoldEnd });
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private double CardIn(int i) => Clamp01((Progress - i) / 0.6);
    private double EdgeIn(int i) => Clamp01((Progress - i - 0.4) / 0.6);

    protected override Size MeasureOverride(Size availableSize)
    {
        _layout = Build(Nodes ?? Array.Empty<FlowNode>(), ShowHost);
        return new Size(_layout.Width, _layout.Height);
    }

    // ------------------------------------------------------------------ painting

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    private static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Orange;
    private static Brush WithOpacity(Brush b, double o) { var c = b.Clone(); c.Opacity = o; c.Freeze(); return c; }

    private Brush SeverityBrush(string? severity) => SeverityBrushes.Of(severity, Res);

    private FormattedText Text(string s, double size, Brush brush, bool bold = false, double maxWidth = 0, bool icon = false, bool cache = true)
    {
        if (!cache) return MakeText(s, size, brush, bold, maxWidth, icon);
        var key = (s, size, brush, bold, maxWidth, icon);
        if (!_texts.TryGetValue(key, out var ft))
        {
            if (_texts.Count > 600) _texts.Clear();
            _texts[key] = ft = MakeText(s, size, brush, bold, maxWidth, icon);
        }
        return ft;
    }

    private FormattedText MakeText(string s, double size, Brush brush, bool bold, double maxWidth, bool icon)
    {
        var family = icon ? (TryFindResource("Font.Icon") as FontFamily ?? new FontFamily("Segoe MDL2 Assets")) : (TryFindResource("Font.UI") as FontFamily ?? new FontFamily("Segoe UI"));
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        return ft;
    }

    internal static string TimeText(DateTimeOffset? t) => t is { } x ? x.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "No date";

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hits.Clear(); _edgeHits.Clear();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var lay = _layout;
        if (lay.Items.Count == 0) return;

        var text = Res("Brush.Text", Brushes.White);
        var dim = Res("Brush.TextSecondary", Brushes.Gray);
        var border = Res("Brush.Border", Brushes.DimGray);
        var surface = Res("Brush.Surface", Brushes.Black);
        var accent = Res("Brush.Accent", Brushes.DodgerBlue);

        // columns: a faint band and the tactic name on top
        for (int c = 0; c < lay.Columns.Count; c++)
        {
            double x = Pad + c * (CardW + ColGap);
            dc.DrawRoundedRectangle(Fade(border, 0.18), null, new Rect(x - 6, Pad, CardW + 12, h - 2 * Pad), 8, 8);
            var ft = Text(lay.Columns[c].ToUpperInvariant(), 10, dim, true, CardW);
            dc.DrawText(ft, new Point(x + (CardW - ft.Width) / 2, Pad + 6));
        }

        // arrows (a faint dash offset marches along each edge from source to target)
        EnsureGeometry(lay);
        bool motion = MotionOn;
        int edgeN = lay.Edges.Count;
        var hoverItem = _hover;
        var edgeBase = PenOf(Fade(dim, 0.55), 1.5);
        var edgeHot = PenOf(accent, 2.5);
        Pen? dashPen = null;
        if (motion)
        {
            if (_dashPen == null)
            {
                _dashStyle = new DashStyle(new[] { 2.0, 4.0 }, 0);
                _dashPen = new Pen(Fade(text, 0.4), 1.5) { DashStyle = _dashStyle, LineJoin = PenLineJoin.Round };
            }
            _dashStyle!.Offset = -_t * 5;
            dashPen = _dashPen;
        }
        for (int e = 0; e < edgeN; e++)
        {
            int from = lay.Edges[e].From;
            double f = EdgeIn(from);
            if (f <= 0) continue;
            var pts = _edgePts[e];
            bool hot = hoverItem == from || hoverItem == lay.Edges[e].To || _hoverEdge == from;
            var pen = hot ? edgeHot : edgeBase;
            if (f >= 0.999)
            {
                dc.DrawGeometry(null, pen, _edgeGeo[e]);
                if (dashPen != null && !hot) dc.DrawGeometry(null, dashPen, _edgeGeo[e]);
                dc.DrawGeometry(pen.Brush, null, _headGeo[e]);
                _edgeHits.Add((from, pts));
            }
            else
            {
                int last = Math.Max(1, (int)Math.Round(f * (pts.Count - 1)));
                var fig = new PathFigure { StartPoint = pts[0] };
                for (int i = 1; i <= last; i++) fig.Segments.Add(new LineSegment(pts[i], true));
                var g = new PathGeometry(new[] { fig }); g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }

        // cards
        for (int i = 0; i < lay.Items.Count; i++)
        {
            double a = CardIn(i);
            if (a <= 0) continue;
            var it = lay.Items[i]; var n = it.Node; var r = it.Bounds;
            var sev = SeverityBrush(n.Severity);
            bool hot = i == hoverItem, sel = n.Id == SelectedId;
            double scale = 0.85 + 0.15 * a;
            double glow = motion ? GlowAt(i, _t, edgeN) : 0;
            dc.PushOpacity(a);
            dc.PushTransform(new ScaleTransform(scale, scale, r.X + r.Width / 2, r.Y + r.Height / 2));
            if (glow > 0)
            {
                // soft outer glow (stepped rings, no blur effect) that fades after the pulse arrives
                for (int k = 3; k >= 1; k--) { dc.PushOpacity(glow * (0.28 - 0.07 * k)); dc.DrawRoundedRectangle(sev, null, Rect.Inflate(r, 3 * k, 3 * k), 8 + 3 * k, 8 + 3 * k); dc.Pop(); }
            }
            if (hot || sel) dc.DrawRoundedRectangle(Fade(sev, 0.22), null, Rect.Inflate(r, 4, 4), 12, 12);
            dc.DrawRoundedRectangle(surface, PenOf(hot || sel || glow > 0.5 ? sev : Fade(sev, 0.7), hot || sel ? 2 : glow > 0.5 ? 2 : 1.25), r, 8, 8);
            dc.PushClip(new RectangleGeometry(r, 8, 8));
            dc.DrawRectangle(sev, null, new Rect(r.X, r.Y, 6, r.Height));
            dc.Pop();
            var glyph = Text(n.Glyph, 16, sev, false, 0, true);
            dc.DrawText(glyph, new Point(r.X + 16, r.Y + 9));
            double tx = r.X + 42, tw = r.Width - 50;
            dc.DrawText(Text(n.Title, 12, text, true, tw), new Point(tx, r.Y + 8));
            dc.DrawText(Text(TimeText(n.Time), 10, dim, false, tw), new Point(tx, r.Y + 28));
            if (ShowHost && !string.IsNullOrEmpty(n.Host)) dc.DrawText(Text(n.Host!, 10, dim, false, tw), new Point(tx, r.Y + 43));
            dc.Pop(); dc.Pop();
            _hits.Add((i, r));
        }

        if (motion) DrawPulse(dc, lay, surface);

        // tooltip on the canvas
        if (_hover >= 0 && _hover < lay.Items.Count && lay.Items[_hover].Node.Details is { Count: > 0 } details) DrawTip(dc, w, h, details.ToArray(), wrapped: true);
        else if (_hover >= 0 && _hover < lay.Items.Count) DrawTip(dc, w, h, new[] { lay.Items[_hover].Node.Title, TimeText(lay.Items[_hover].Node.Time), "Tactic: " + Canonical(lay.Items[_hover].Node.Tactic) });
        else if (_hoverEdge >= 0 && _hoverEdge < lay.Edges.Count) DrawTip(dc, w, h, new[] { "then (by time)" });
    }

    /// <summary>The travelling pulse: a glowing disc with the source card's tactic icon and a short fading trail, on the edge it is crossing.</summary>
    private void DrawPulse(DrawingContext dc, FlowLayout lay, Brush surface)
    {
        if (PulseAt(_t, lay.Edges.Count) is not { } p) return;
        var pts = _edgePts[p.Edge];
        var node = lay.Items[lay.Edges[p.Edge].From].Node;
        var sev = SeverityBrush(node.Severity);
        var col = ColorOf(sev);
        var solid = SolidOf(col);
        for (int k = 6; k >= 1; k--)
        {
            double raw = Math.Max(0, p.Raw - k * TrailStep / EdgeSeconds * 1.6);
            var tp = PointAlong(pts, EaseInOut(raw));
            dc.PushOpacity(0.45 * (1 - k / 7.0));
            dc.DrawEllipse(solid, null, tp, 10 - k * 0.9, 10 - k * 0.9);
            dc.Pop();
        }
        var pos = PointAlong(pts, p.Along);
        dc.DrawEllipse(GlowOf(col), null, pos, 30, 30);
        dc.DrawEllipse(surface, PenOf(sev, 2), pos, 15, 15);
        if (!string.IsNullOrEmpty(node.Glyph))
        {
            var g = Text(node.Glyph, 17, sev, false, 0, true);
            dc.DrawText(g, new Point(pos.X - g.Width / 2, pos.Y - g.Height / 2));
        }
    }

    /// <summary>The hover tip: the finding's details (wrapped, at most two lines each) or a short text with a bold first line.</summary>
    internal void DrawTip(DrawingContext dc, double w, double h, string[] lines, bool wrapped = false)
    {
        var text = Res("Brush.Text", Brushes.White);
        var fts = wrapped
            ? lines.Select(l => { var ft = Text(l, 11, text, false, 320, false, false); ft.MaxLineCount = 2; return ft; }).ToList()
            : lines.Select((l, i) => Text(l, i == 0 ? 12 : 11, text, i == 0, 320, false, false)).ToList();
        double tw = fts.Max(f => f.Width) + 20, th = fts.Sum(f => f.Height) + 14;
        double x = Math.Min(Math.Max(4, _mouse.X + 14), Math.Max(4, w - tw - 4)), y = _mouse.Y + 18;
        if (y + th > h) y = Math.Max(4, _mouse.Y - th - 10);
        dc.DrawRoundedRectangle(Res("Brush.Surface", Brushes.Black), new Pen(Res("Brush.BorderStrong", Brushes.Gray), 1), new Rect(x, y, tw, th), 6, 6);
        double yy = y + 7;
        foreach (var f in fts) { dc.DrawText(f, new Point(x + 10, yy)); yy += f.Height; }
    }

    // ------------------------------------------------------------------ mouse

    private int CardAt(Point p) { for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Bounds.Contains(p)) return _hits[i].Index; return -1; }

    private int EdgeAt(Point p)
    {
        foreach (var (edge, pts) in _edgeHits)
            for (int i = 1; i < pts.Count; i++)
                if (DistToSegment(p, pts[i - 1], pts[i]) < 6) return edge;
        return -1;
    }

    private static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a; double len2 = ab.LengthSquared;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(((p - a) * ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        int c = CardAt(_mouse), ed = c >= 0 ? -1 : EdgeAt(_mouse);
        _hover = c; _hoverEdge = ed;
        Cursor = c >= 0 ? Cursors.Hand : null;
        UpdateMotion();
        InvalidateVisual();
    }

    /// <summary>Shows a node's details as if hovered (snapshot tests); false when the node was not drawn.</summary>
    internal bool HoverNode(string id)
    {
        var hit = _hits.FirstOrDefault(x => _layout.Items[x.Index].Node.Id == id);
        if (hit.Bounds.IsEmpty) return false;
        _hover = hit.Index; _hoverEdge = -1;
        UpdateMotion();
        _mouse = new Point(hit.Bounds.X + hit.Bounds.Width * 0.5, hit.Bounds.Y + hit.Bounds.Height * 0.6);
        InvalidateVisual();
        return true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1; _hoverEdge = -1;
        UpdateMotion();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        int c = CardAt(e.GetPosition(this));
        if (c < 0) return;
        var id = _layout.Items[c].Node.Id;
        SelectedId = id;
        if (e.ClickCount >= 2) NodeDoubleClicked?.Invoke(id); else NodeClicked?.Invoke(id);
        e.Handled = true;
    }

    internal void RaiseNodeClick(string id, bool dbl) { SelectedId = id; if (dbl) NodeDoubleClicked?.Invoke(id); else NodeClicked?.Invoke(id); }
}
