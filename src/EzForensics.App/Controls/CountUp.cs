using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace EzForensics.App.Controls;

/// <summary>
/// <c>c:CountUp.Value="{Binding N}"</c> on a TextBlock shows the number counting up from 0 when it loads, and from the old value when it
/// changes. With animations off the number shows at once.
/// </summary>
public static class CountUp
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.RegisterAttached("Value", typeof(int), typeof(CountUp), new PropertyMetadata(int.MinValue, OnValueChanged));   // not 0: a bound 0 must still write "0"
    public static int GetValue(DependencyObject d) => (int)d.GetValue(ValueProperty);
    public static void SetValue(DependencyObject d, int v) => d.SetValue(ValueProperty, v);

    /// <summary>The number being shown (animated); writes the TextBlock's text.</summary>
    private static readonly DependencyProperty ShownProperty =
        DependencyProperty.RegisterAttached("Shown", typeof(double), typeof(CountUp), new PropertyMetadata(0.0, OnShownChanged));

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(CountUp), new PropertyMetadata(false));

    public const int DurationMs = 700;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        if (!(bool)tb.GetValue(HookedProperty))
        {
            tb.SetValue(HookedProperty, true);
            tb.Loaded += (_, _) => Animate(tb, 0);   // count up from 0 each time the page shows
        }
        Animate(tb, (double)tb.GetValue(ShownProperty));
    }

    private static void Animate(TextBlock tb, double from)
    {
        int to = Math.Max(0, GetValue(tb));
        if (!Motion.Enabled || !tb.IsLoaded || Math.Abs(from - to) < 0.5)
        {
            tb.BeginAnimation(ShownProperty, null);
            tb.SetValue(ShownProperty, (double)to);
            Write(tb, to);
            return;
        }
        var ms = Math.Min(DurationMs, 250 + 60 * Math.Abs(to - (int)from));
        tb.BeginAnimation(ShownProperty, new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock tb) Write(tb, (int)Math.Round((double)e.NewValue));
    }

    private static void Write(TextBlock tb, int n) => tb.Text = n.ToString("N0", CultureInfo.InvariantCulture);
}
