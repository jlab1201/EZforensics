using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Animation;
using EzForensics.App.Services;
using EzForensics.Core.Settings;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Controls;

/// <summary>
/// A ring of the findings by severity (most severe first, clockwise from the top) with the total in the centre. The arcs sweep in when it
/// loads (at once when animations are off). Colour only: the page shows each severity's word and count beside it.
/// </summary>
public sealed class SeverityDonut : FrameworkElement
{
    /// <summary>Counts by severity rank (index = rank in <see cref="SpectreFinding.Severities"/>: Info, Low, Medium, High, Critical).</summary>
    public static readonly DependencyProperty CountsProperty = DependencyProperty.Register(nameof(Counts), typeof(IReadOnlyList<int>), typeof(SeverityDonut),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SeverityDonut)d).OnCountsChanged()));
    public IReadOnlyList<int>? Counts { get => (IReadOnlyList<int>?)GetValue(CountsProperty); set => SetValue(CountsProperty, value); }

    /// <summary>The word under the total ("findings").</summary>
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(SeverityDonut),
        new FrameworkPropertyMetadata("findings", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    /// <summary>How much of the ring is drawn (0 to 1); animated on load.</summary>
    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(nameof(Sweep), typeof(double), typeof(SeverityDonut),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(SeverityDonut),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private string _shownKey = "";
    private readonly HoverZoom _zoom;
    private int _hoverRank = -1;
    /// <summary>Hovered segment: +6 px thicker and pushed 4 px out; Pad keeps room around the ring for it.</summary>
    internal const double ZoomGrow = 6, ZoomPush = 4, Pad = 10;

    public SeverityDonut()
    {
        _zoom = new HoverZoom(this);
        SnapsToDevicePixels = true;
        Loaded += (_, _) => { ThemeManager.Changed += OnThemeChanged; SweepIn(); };
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(AppTheme _) => InvalidateVisual();

    private void OnCountsChanged()
    {
        if (IsLoaded) SweepIn();
    }

    /// <summary>Sweeps the arcs in once per distinct set of counts (a Confirm that does not change them leaves the ring alone).</summary>
    private void SweepIn()
    {
        var key = Counts == null ? "" : string.Join(",", Counts);
        if (key == _shownKey) return;
        _shownKey = key;
        if (!Motion.Enabled) { BeginAnimation(SweepProperty, null); Sweep = 1; return; }
        BeginAnimation(SweepProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>The severity rank under a point of this control, or -1.</summary>
    internal int RankAt(Point p)
    {
        var counts = Counts; int total = counts?.Sum() ?? 0;
        double size = Math.Min(ActualWidth, ActualHeight);
        if (counts == null || total <= 0 || size < 20) return -1;
        double dx = p.X - ActualWidth / 2, dy = p.Y - ActualHeight / 2, dist = Math.Sqrt(dx * dx + dy * dy);
        double r = size / 2 - Thickness / 2 - 1 - Pad;
        if (dist < r - Thickness / 2 - 2 || dist > r + Thickness / 2 + ZoomGrow + ZoomPush) return -1;
        double ang = Math.Atan2(dy, dx) * 180 / Math.PI + 90;   // clockwise from 12 o'clock
        if (ang < 0) ang += 360;
        double acc = 0;
        for (int rank = counts.Count - 1; rank >= 0; rank--)
        {
            if (counts[rank] <= 0) continue;
            double span = 360.0 * counts[rank] / total;
            if (ang >= acc && ang < acc + span) return rank;
            acc += span;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHover(RankAt(e.GetPosition(this))); }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); SetHover(-1); }

    private void SetHover(int rank)
    {
        if (rank == _hoverRank) return;
        _hoverRank = rank;
        _zoom.SetHover(rank);
    }

    /// <summary>Snapshot hook: severity <paramref name="rank"/> at full zoom, no animation.</summary>
    internal void SetHoverForTest(int rank) { _hoverRank = rank; _zoom.SetFull(rank); }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    internal Brush SeverityBrush(int rank) => SeverityBrushes.Of(rank, Res);

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsNaN(Width) ? 120 : Width, h = double.IsNaN(Height) ? 120 : Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20) return;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double t = Thickness, r = size / 2 - t / 2 - 1 - Pad;
        var track = new Pen(Res("Brush.SurfaceAlt", Brushes.LightGray), t); track.Freeze();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));   // takes mouse input
        dc.DrawEllipse(null, track, centre, r, r);

        var counts = Counts ?? Array.Empty<int>();
        int total = counts.Sum();
        if (total > 0)
        {
            // most severe first, clockwise from 12 o'clock; a small gap between arcs so Critical and High (same colour) stay apart
            int arcs = counts.Count(c => c > 0);
            double gap = arcs > 1 ? 3.0 : 0, sweepAll = 360 * Math.Clamp(Sweep, 0, 1), start = -90;
            for (int rank = counts.Count - 1; rank >= 0; rank--)
            {
                if (counts[rank] <= 0) continue;
                double span = 360.0 * counts[rank] / total;
                double a0 = start, a1 = Math.Min(start + span - gap, -90 + sweepAll);
                start += span;
                if (a1 <= a0) continue;
                double z = _zoom.Of(rank);
                var pen = new Pen(SeverityBrush(rank), t + ZoomGrow * z) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };   // inner edge stays, outer grows
                pen.Freeze();
                double mid = (a0 + a1) / 2 * Math.PI / 180, push = ZoomPush * z;
                var c = new Point(centre.X + push * Math.Cos(mid), centre.Y + push * Math.Sin(mid));
                DrawArc(dc, pen, c, r + ZoomGrow * z / 2, a0, a1);
            }
        }

        var family = TryFindResource("Font.UI") as FontFamily ?? new FontFamily("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var num = new FormattedText(total.ToString("N0", CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), Math.Max(14, size * 0.22), Res("Brush.Text", Brushes.Black), dpi);
        var cap = new FormattedText(Caption ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), Math.Max(9, size * 0.095), Res("Brush.TextSecondary", Brushes.Gray), dpi);
        double hgt = num.Height + cap.Height - 2;
        dc.DrawText(num, new Point(centre.X - num.Width / 2, centre.Y - hgt / 2));
        dc.DrawText(cap, new Point(centre.X - cap.Width / 2, centre.Y - hgt / 2 + num.Height - 2));
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double a0, double a1)
    {
        if (a1 - a0 >= 359.9) { dc.DrawEllipse(null, pen, c, r, r); return; }
        Point P(double deg) { var rad = deg * Math.PI / 180; return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad)); }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(a0), false, false);
            ctx.ArcTo(P(a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
