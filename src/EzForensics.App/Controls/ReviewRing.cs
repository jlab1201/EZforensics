using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EzForensics.App.Services;
using EzForensics.Core.Settings;

namespace EzForensics.App.Controls;

/// <summary>
/// Review progress: a ring filled for <see cref="Value"/> of <see cref="Maximum"/> with "3/7" in the centre. It fills from empty on load
/// and glides when the value changes (at once when animations are off). Green when everything is reviewed.
/// </summary>
public sealed class ReviewRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(ReviewRing),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ReviewRing)d).Glide()));
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(ReviewRing),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ReviewRing)d).Glide()));
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>The drawn fraction (0 to 1), animated towards Value / Maximum.</summary>
    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(nameof(Shown), typeof(double), typeof(ReviewRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Shown { get => (double)GetValue(ShownProperty); set => SetValue(ShownProperty, value); }

    public ReviewRing()
    {
        SnapsToDevicePixels = true;
        Loaded += (_, _) => { ThemeManager.Changed += OnThemeChanged; Glide(fromZero: true); };
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(AppTheme _) => InvalidateVisual();

    private double Target => Maximum > 0 ? Math.Clamp((double)Value / Maximum, 0, 1) : 0;

    private void Glide(bool fromZero = false)
    {
        var to = Target;
        if (!Motion.Enabled || !IsLoaded)
        {
            BeginAnimation(ShownProperty, null);
            Shown = to;
            return;
        }
        var from = fromZero ? 0 : Shown;
        BeginAnimation(ShownProperty, new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(fromZero ? 900 : 500)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override Size MeasureOverride(Size availableSize) => new(double.IsNaN(Width) ? 72 : Width, double.IsNaN(Height) ? 72 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 16) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double t = Math.Max(5, size * 0.11), r = size / 2 - t / 2 - 1;
        var track = new Pen(Res("Brush.SurfaceAlt", Brushes.LightGray), t); track.Freeze();
        dc.DrawEllipse(null, track, c, r, r);
        bool done = Maximum > 0 && Value >= Maximum;
        var fill = new Pen(Res(done ? "Brush.Success" : "Brush.Accent", Brushes.DodgerBlue), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        fill.Freeze();
        double f = Math.Clamp(Shown, 0, 1);
        if (f >= 0.999) dc.DrawEllipse(null, fill, c, r, r);
        else if (f > 0.001)
        {
            double a0 = -90, a1 = -90 + 360 * f;
            Point P(double deg) { var rad = deg * Math.PI / 180; return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad)); }
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(P(a0), false, false);
                ctx.ArcTo(P(a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, fill, g);
        }
        var family = TryFindResource("Font.UI") as FontFamily ?? new FontFamily("Segoe UI");
        var text = new FormattedText($"{Value.ToString(CultureInfo.InvariantCulture)}/{Maximum.ToString(CultureInfo.InvariantCulture)}", CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), Math.Max(11, size * 0.21),
            Res("Brush.Text", Brushes.Black), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
    }
}
