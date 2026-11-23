using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Controls;

/// <summary>
/// Findings per ATT&amp;CK tactic as a radar (spider) chart. The 12 tactics are fixed axes in kill-chain order, so the shape can be compared
/// between computers. Each value is a finding count, drawn on a square-root scale of the largest axis (so small counts stay visible).
/// Concentric rings, spokes, a translucent polygon with a solid edge, dots on the vertices and a soft glow in the middle. Labels are upper
/// case outside the rings; a click on a label or a vertex (or Enter / Space on the focused one, arrows to move) runs <see cref="Command"/>
/// with the tactic name. The active tactic is highlighted.
/// </summary>
public sealed class TacticRadar : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(TacticRadarData), typeof(TacticRadar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public TacticRadarData? Data { get => (TacticRadarData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>The tactic whose cards are shown (highlighted); null for none.</summary>
    public static readonly DependencyProperty ActiveAxisProperty = DependencyProperty.Register(nameof(ActiveAxis), typeof(string), typeof(TacticRadar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public string? ActiveAxis { get => (string?)GetValue(ActiveAxisProperty); set => SetValue(ActiveAxisProperty, value); }

    /// <summary>Runs with the tactic name when a label or vertex is clicked.</summary>
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(TacticRadar));
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public const int Rings = 4;
    private const double LabelFont = 10, LabelGap = 8, SidePad = 6, IconSize = 18, IconR = 13, ChipGap = 5;
    private const double ChipOffset = ChipGap + IconR;   // chip centre beyond the outer ring, along the spoke

    private int _focus = -1;
    private int _hover = -1;
    private readonly HoverZoom _zoom;
    internal const double ChipZoomMax = 1.35;
    internal static double ChipScale(double zoom) => 1 + (ChipZoomMax - 1) * zoom;
    private readonly List<(int Index, Rect Bounds)> _hits = new();
    private readonly Dictionary<int, Rect> _labelBoxes = new();

    public TacticRadar()
    {
        _zoom = new HoverZoom(this);
        Focusable = true;
        SnapsToDevicePixels = false;
        FocusVisualStyle = null;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 150);
        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(AppTheme _) => InvalidateVisual();

    // ------------------------------------------------------------------ geometry (internal for tests)

    /// <summary>Angle of axis <paramref name="i"/> of <paramref name="n"/> in radians: the first axis points up, then clockwise.</summary>
    internal static double AngleOf(int i, int n) => -Math.PI / 2 + 2 * Math.PI * i / Math.Max(1, n);

    /// <summary>Share of the radius for a count: square root of count over the largest count (0 when there is nothing).</summary>
    internal static double Scale(int count, int max) => max <= 0 || count <= 0 ? 0 : Math.Sqrt(Math.Min(1.0, (double)count / max));

    /// <summary>Where the value of axis <paramref name="i"/> sits for a centre and radius.</summary>
    internal static Point VertexAt(Point centre, double radius, int i, int n, int count, int max)
    {
        var a = AngleOf(i, n);
        var r = radius * Scale(count, max);
        return new Point(centre.X + r * Math.Cos(a), centre.Y + r * Math.Sin(a));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 420 : availableSize.Width;
        return new Size(w, Math.Clamp(w * 0.78, 250, 340));
    }

    private (Point Centre, double Radius) Frame(Size size)
    {
        // room outside the rings for the longest labels left and right and one line above and below
        double sideRoom = 112 + 2 * IconR + ChipGap;
        double radius = Math.Max(30, Math.Min((size.Width - 2 * sideRoom) / 2, (size.Height - 2 * (LabelFont + 4 + LabelGap + 2 * IconR + ChipGap + 2)) / 2));
        return (new Point(size.Width / 2, size.Height / 2), radius);
    }

    // ------------------------------------------------------------------ painting

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    private static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Orange;
    private static Brush WithOpacity(Brush b, double opacity) { var c = b.Clone(); c.Opacity = opacity; c.Freeze(); return c; }

    private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface((TryFindResource("Font.UI") as FontFamily) ?? new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hits.Clear(); _labelBoxes.Clear();
        var data = Data;
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // so the empty parts take mouse input too
        if (data == null || data.Axes.Count == 0 || w < 60 || h < 60) return;

        int n = data.Axes.Count, max = data.Max;
        var (centre, radius) = Frame(new Size(w, h));
        var text = Res("Brush.Text", Brushes.White);
        var dim = Res("Brush.TextSecondary", Brushes.Gray);
        var border = Res("Brush.Border", Brushes.DimGray);
        var strong = Res("Brush.BorderStrong", Brushes.Gray);
        var tone = Res("Brush.Orange", Res("Brush.Accent", Brushes.Orange));
        var toneColor = ColorOf(tone);

        Point P(double r, int i) { var a = AngleOf(i, n); return new Point(centre.X + r * Math.Cos(a), centre.Y + r * Math.Sin(a)); }

        // soft glow in the middle
        var glow = new RadialGradientBrush(Color.FromArgb(70, toneColor.R, toneColor.G, toneColor.B), Color.FromArgb(0, toneColor.R, toneColor.G, toneColor.B));
        glow.Freeze();
        dc.DrawEllipse(glow, null, centre, radius * 0.75, radius * 0.75);

        // rings (polygons) and spokes
        var ringPen = new Pen(WithOpacity(border, 0.9), 1); ringPen.Freeze();
        for (int k = 1; k <= Rings; k++)
        {
            var fig = new PathFigure { StartPoint = P(radius * k / Rings, 0), IsClosed = true };
            for (int i = 1; i < n; i++) fig.Segments.Add(new LineSegment(P(radius * k / Rings, i), true));
            var g = new PathGeometry(new[] { fig }); g.Freeze();
            dc.DrawGeometry(null, k == Rings ? new Pen(strong, 1) : ringPen, g);
        }
        var spokePen = new Pen(WithOpacity(border, 0.8), 1); spokePen.Freeze();
        for (int i = 0; i < n; i++) dc.DrawLine(spokePen, centre, P(radius, i));

        // the shape
        if (max > 0)
        {
            var pts = Enumerable.Range(0, n).Select(i => VertexAt(centre, radius, i, n, data.Counts[i], max)).ToList();
            var fig = new PathFigure { StartPoint = pts[0], IsClosed = true };
            for (int i = 1; i < n; i++) fig.Segments.Add(new LineSegment(pts[i], true));
            var g = new PathGeometry(new[] { fig }); g.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(72, toneColor.R, toneColor.G, toneColor.B)); fill.Freeze();
            var edge = new Pen(tone, 2) { LineJoin = PenLineJoin.Round }; edge.Freeze();
            dc.DrawGeometry(fill, edge, g);
        }

        // tactic icons on a round surface chip just beyond the vertex of the outer polygon (along the spoke), so they never cover the vertex dot
        var chipFill = Res("Brush.Surface", Brushes.Black);
        var iconFace = new Typeface((TryFindResource("Font.Icon") as FontFamily) ?? new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var chipPen = new Pen(strong, 1); chipPen.Freeze();
        var chipPenOn = new Pen(tone, 1.5); chipPenOn.Freeze();
        // the zoomed chip goes last so it draws over its neighbours
        foreach (int i in Enumerable.Range(0, n).OrderBy(k => _zoom.Of(k) > 0 ? 1 : 0))
        {
            bool on = IsActive(data.Axes[i]);
            var brush = on ? tone : data.Counts[i] > 0 ? text : dim;
            var pos = P(radius + ChipOffset, i);
            double z = ChipScale(_zoom.Of(i));
            Pen pen = on ? chipPenOn : chipPen;
            if (z > 1) { pen = new Pen(on ? tone : strong, (on ? 1.5 : 1) * z); pen.Freeze(); }
            dc.DrawEllipse(chipFill, pen, pos, IconR * z, IconR * z);
            var iconFt = new FormattedText(StoryGraphics.Glyph(data.Axes[i]), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                iconFace, IconSize * 0.72 * z, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(iconFt, new Point(pos.X - iconFt.Width / 2, pos.Y - iconFt.Height / 2));
            _hits.Add((i, new Rect(pos.X - IconR, pos.Y - IconR, 2 * IconR, 2 * IconR)));
        }

        // data points above the icons so one on the outer ring stays visible
        if (max > 0)
        {
            var ring = new Pen(chipFill, 1.5); ring.Freeze();
            for (int i = 0; i < n; i++)
            {
                if (data.Counts[i] <= 0) continue;
                bool on = IsActive(data.Axes[i]);
                dc.DrawEllipse(on ? text : tone, ring, VertexAt(centre, radius, i, n, data.Counts[i], max), on ? 5.5 : 4, on ? 5.5 : 4);
            }
        }

        // labels, upper case, outside the outer ring
        for (int i = 0; i < n; i++)
        {
            string axis = data.Axes[i];
            bool on = IsActive(axis);
            int c = data.Counts[i];
            var ft = Text(axis.ToUpperInvariant(), LabelFont, on ? tone : c > 0 ? text : dim, bold: on || c > 0);
            var a = AngleOf(i, n);
            double cos = Math.Cos(a), sin = Math.Sin(a);
            var anchor = P(radius + ChipOffset + IconR + LabelGap, i);
            double x;
            double y = Math.Abs(sin) < 0.2 ? anchor.Y - ft.Height / 2 : sin > 0 ? anchor.Y : anchor.Y - ft.Height;
            double full = ft.Width;
            x = Math.Abs(cos) < 0.2 ? anchor.X - full / 2 : cos > 0 ? anchor.X : anchor.X - full;
            x = Math.Clamp(x, SidePad, Math.Max(SidePad, w - full - SidePad));
            var box = new Rect(x - 3, y - 2, full + 6, ft.Height + 4);
            if (on) dc.DrawRoundedRectangle(WithOpacity(tone, 0.14), new Pen(WithOpacity(tone, 0.6), 1), box, 4, 4);
            if (_focus == i && IsKeyboardFocused) dc.DrawRoundedRectangle(null, new Pen(text, 1.5) { DashStyle = DashStyles.Dot }, Rect.Inflate(box, 2, 2), 4, 4);
            dc.DrawText(ft, new Point(x, y));
            _hits.Add((i, box)); _labelBoxes[i] = box;
            var v = max > 0 ? VertexAt(centre, radius, i, n, c, max) : centre;
            if (c > 0) _hits.Add((i, new Rect(v.X - 9, v.Y - 9, 18, 18)));
        }

        // the hovered point: bigger, with its value in a small bubble beside it
        if (_hover >= 0 && _hover < n && max > 0)
        {
            int c = data.Counts[_hover];
            var v = VertexAt(centre, radius, _hover, n, c, max);
            dc.DrawEllipse(tone, new Pen(text, 1.5), v, 6.5, 6.5);
            var ft = Text(c == 1 ? "1 finding" : $"{c} findings", LabelFont + 1, text, bold: true);
            double bx = Math.Clamp(v.X + 10, SidePad, Math.Max(SidePad, w - ft.Width - 12 - SidePad));
            double by = Math.Clamp(v.Y - ft.Height - 12, 2, Math.Max(2, h - ft.Height - 8));
            var bubble = new Rect(bx, by, ft.Width + 12, ft.Height + 6);
            dc.DrawRoundedRectangle(Res("Brush.Surface", Brushes.Black), new Pen(tone, 1), bubble, 4, 4);
            dc.DrawText(ft, new Point(bx + 6, by + 3));
        }
    }

    /// <summary>Snapshot hook: the chip of axis <paramref name="i"/> at full zoom (and its value bubble), no animation.</summary>
    internal void SetHoverForTest(int i) { _hover = i; _zoom.SetFull(i); }

    private bool IsActive(string axis) => ActiveAxis != null && string.Equals(axis, ActiveAxis, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ input

    private int IndexAt(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Bounds.Contains(p)) return _hits[i].Index;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.GetPosition(this));
        if (i == _hover) return;
        _hover = i;
        _zoom.SetHover(i);
        Cursor = i >= 0 && Data is { } d && d.Counts[i] > 0 ? Cursors.Hand : null;
        InvalidateVisual();   // the value is drawn beside the point
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1; _zoom.SetHover(-1); Cursor = null; InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Focus();
        int i = IndexAt(e.GetPosition(this));
        if (i < 0) return;
        _focus = i;
        Invoke(i);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (_focus < 0) _focus = 0;
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int n = Data?.Axes.Count ?? 0;
        if (n == 0) return;
        switch (e.Key)
        {
            case Key.Right: case Key.Down: _focus = (Math.Max(_focus, -1) + 1) % n; break;
            case Key.Left: case Key.Up: _focus = (Math.Max(_focus, 0) - 1 + n) % n; break;
            case Key.Home: _focus = 0; break;
            case Key.End: _focus = n - 1; break;
            case Key.Enter: case Key.Space: Invoke(Math.Max(0, _focus)); e.Handled = true; return;
            default: return;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    internal void Invoke(int i)
    {
        if (Data is not { } d || i < 0 || i >= d.Axes.Count) return;
        if (Command is { } cmd && cmd.CanExecute(d.Axes[i])) cmd.Execute(d.Axes[i]);
    }

    /// <summary>The label box of each axis (for tests and automation), in axis order, in this control's coordinates.</summary>
    internal IReadOnlyList<Rect> LabelBoxes()
    {
        var boxes = new Rect[Data?.Axes.Count ?? 0];
        foreach (var (i, b) in _labelBoxes) if (i < boxes.Length) boxes[i] = b;
        return boxes;
    }

    // ------------------------------------------------------------------ automation

    protected override AutomationPeer OnCreateAutomationPeer() => new RadarPeer(this);

    internal string AxisName(int i) => Data is { } d && i >= 0 && i < d.Axes.Count ? $"{d.Axes[i]}, {(d.Counts[i] == 1 ? "1 finding" : d.Counts[i] + " findings")}" : "";

    private sealed class RadarPeer : FrameworkElementAutomationPeer
    {
        private readonly TacticRadar _r;
        private List<AxisPeer>? _kids;
        public RadarPeer(TacticRadar r) : base(r) => _r = r;
        protected override string GetClassNameCore() => nameof(TacticRadar);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override List<AutomationPeer> GetChildrenCore()
        {
            int n = _r.Data?.Axes.Count ?? 0;
            if (_kids == null || _kids.Count != n) _kids = Enumerable.Range(0, n).Select(i => new AxisPeer(this, _r, i)).ToList();
            return _kids.Cast<AutomationPeer>().ToList();
        }
    }

    private sealed class AxisPeer : AutomationPeer, IInvokeProvider
    {
        private readonly RadarPeer _parent; private readonly TacticRadar _r; private readonly int _i;
        public AxisPeer(RadarPeer parent, TacticRadar r, int i) { _parent = parent; _r = r; _i = i; }
        protected override string GetAutomationIdCore() => "Findings.Overview.Tactic.Axis";
        protected override string GetNameCore() => _r.AxisName(_i);
        protected override string GetClassNameCore() => "TacticAxis";
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override Rect GetBoundingRectangleCore()
        {
            var boxes = _r.LabelBoxes();
            if (_i >= boxes.Count || !_r.IsVisible) return Rect.Empty;
            var tl = _r.PointToScreen(boxes[_i].TopLeft);
            return new Rect(tl, boxes[_i].Size);
        }
        protected override Point GetClickablePointCore() { var b = GetBoundingRectangleCore(); return b.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(b.X + b.Width / 2, b.Y + b.Height / 2); }
        protected override string GetHelpTextCore() => "Show only the key events at this tactic";
        protected override string GetAcceleratorKeyCore() => "";
        protected override string GetAccessKeyCore() => "";
        protected override string GetItemStatusCore() => _r.IsActive(_i) ? "Active" : "";
        protected override string GetItemTypeCore() => "Tactic";
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
        protected override bool HasKeyboardFocusCore() => _r.IsKeyboardFocused && _r._focus == _i;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        protected override bool IsEnabledCore() => true;
        protected override bool IsKeyboardFocusableCore() => true;
        protected override bool IsOffscreenCore() => !_r.IsVisible;
        protected override bool IsPasswordCore() => false;
        protected override bool IsRequiredForFormCore() => false;
        protected override void SetFocusCore() { _r._focus = _i; _r.Focus(); _r.InvalidateVisual(); }
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : null;
        public void Invoke() => _r.Dispatcher.BeginInvoke(() => _r.Invoke(_i));
    }

    private bool IsActive(int i) => Data is { } d && i >= 0 && i < d.Axes.Count && IsActive(d.Axes[i]);
}
