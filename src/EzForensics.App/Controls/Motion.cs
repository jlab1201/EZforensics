using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EzForensics.App.Controls;

/// <summary>
/// Small entrance animations for the Findings story graphics: <c>c:Motion.PopIn="True"</c> fades and scales an element in when it loads,
/// after <c>c:Motion.DelayMs</c>. The element ends at its own opacity (faint items stay faint). Skipped when Windows animations are off.
/// </summary>
public static class Motion
{
    public static readonly DependencyProperty PopInProperty =
        DependencyProperty.RegisterAttached("PopIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPopInChanged));
    public static bool GetPopIn(DependencyObject d) => (bool)d.GetValue(PopInProperty);
    public static void SetPopIn(DependencyObject d, bool v) => d.SetValue(PopInProperty, v);

    public static readonly DependencyProperty DelayMsProperty =
        DependencyProperty.RegisterAttached("DelayMs", typeof(int), typeof(Motion), new PropertyMetadata(0));
    public static int GetDelayMs(DependencyObject d) => (int)d.GetValue(DelayMsProperty);
    public static void SetDelayMs(DependencyObject d, int v) => d.SetValue(DelayMsProperty, v);

    /// <summary>Overshoot of the scale (0 = none; markers use a slight one).</summary>
    public static readonly DependencyProperty OvershootProperty =
        DependencyProperty.RegisterAttached("Overshoot", typeof(bool), typeof(Motion), new PropertyMetadata(false));
    public static bool GetOvershoot(DependencyObject d) => (bool)d.GetValue(OvershootProperty);
    public static void SetOvershoot(DependencyObject d, bool v) => d.SetValue(OvershootProperty, v);

    /// <summary><c>c:Motion.DrawIn="True"</c>: the element grows from its left edge to full width after DelayMs (a line drawing itself).</summary>
    public static readonly DependencyProperty DrawInProperty =
        DependencyProperty.RegisterAttached("DrawIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnDrawInChanged));
    public static bool GetDrawIn(DependencyObject d) => (bool)d.GetValue(DrawInProperty);
    public static void SetDrawIn(DependencyObject d, bool v) => d.SetValue(DrawInProperty, v);

    /// <summary>How long DrawIn takes (ms).</summary>
    public static readonly DependencyProperty DurationMsProperty =
        DependencyProperty.RegisterAttached("DurationMs", typeof(int), typeof(Motion), new PropertyMetadata(120));
    public static int GetDurationMs(DependencyObject d) => (int)d.GetValue(DurationMsProperty);
    public static void SetDurationMs(DependencyObject d, int v) => d.SetValue(DurationMsProperty, v);

    /// <summary>
    /// <c>c:Motion.Pulse="True"</c>: a soft halo that grows and fades a few times after DelayMs, then stays hidden. Give the element
    /// Opacity 0: with animations off nothing shows.
    /// </summary>
    public static readonly DependencyProperty PulseProperty =
        DependencyProperty.RegisterAttached("Pulse", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPulseChanged));
    public static bool GetPulse(DependencyObject d) => (bool)d.GetValue(PulseProperty);
    public static void SetPulse(DependencyObject d, bool v) => d.SetValue(PulseProperty, v);

    /// <summary>
    /// <c>c:Motion.SlideIn="True"</c>: fades in and slides up a little when it loads. In an ItemsControl with an AlternationCount the
    /// item's index staggers it by <c>c:Motion.StaggerMs</c> (capped so long lists do not wait).
    /// </summary>
    public static readonly DependencyProperty SlideInProperty =
        DependencyProperty.RegisterAttached("SlideIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSlideInChanged));
    public static bool GetSlideIn(DependencyObject d) => (bool)d.GetValue(SlideInProperty);
    public static void SetSlideIn(DependencyObject d, bool v) => d.SetValue(SlideInProperty, v);

    public static readonly DependencyProperty StaggerMsProperty =
        DependencyProperty.RegisterAttached("StaggerMs", typeof(int), typeof(Motion), new PropertyMetadata(40));
    public static int GetStaggerMs(DependencyObject d) => (int)d.GetValue(StaggerMsProperty);
    public static void SetStaggerMs(DependencyObject d, int v) => d.SetValue(StaggerMsProperty, v);

    /// <summary><c>c:Motion.Lift="True"</c>: on hover the element rises 2 px with a soft shadow (at once when animations are off).</summary>
    public static readonly DependencyProperty LiftProperty =
        DependencyProperty.RegisterAttached("Lift", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLiftChanged));
    public static bool GetLift(DependencyObject d) => (bool)d.GetValue(LiftProperty);
    public static void SetLift(DependencyObject d, bool v) => d.SetValue(LiftProperty, v);

    private const int MaxStaggerMs = 480;

    private static void OnSlideInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnSlideInLoaded;
        if (e.NewValue is true) fe.Loaded += OnSlideInLoaded;
    }

    private static void OnSlideInLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        int index = 0;
        for (DependencyObject? p = fe; p != null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is System.Windows.Controls.ContentPresenter cp && System.Windows.Controls.ItemsControl.ItemsControlFromItemContainer(cp) != null)
            {
                index = System.Windows.Controls.ItemsControl.GetAlternationIndex(cp);
                break;
            }
        }
        SlideIn(fe, Math.Min(MaxStaggerMs, GetDelayMs(fe) + index * GetStaggerMs(fe)));
    }

    /// <summary>Fades in and slides up 14 px after <paramref name="delayMs"/>.</summary>
    public static void SlideIn(UIElement el, int delayMs, int durationMs = 300)
    {
        if (!Enabled) return;
        var shift = new TranslateTransform();
        el.RenderTransform = shift;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(UIElement.OpacityProperty, Frames(0, (double)el.GetValue(UIElement.OpacityProperty), delayMs, durationMs, ease));
        shift.BeginAnimation(TranslateTransform.YProperty, Frames(14, 0, delayMs, durationMs, ease));
    }

    private static void OnLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.MouseEnter -= OnLiftEnter;
        fe.MouseLeave -= OnLiftLeave;
        if (e.NewValue is not true) return;
        fe.MouseEnter += OnLiftEnter;
        fe.MouseLeave += OnLiftLeave;
    }

    private static void OnLiftEnter(object sender, System.Windows.Input.MouseEventArgs e) { if (sender is FrameworkElement fe) Lift(fe, true); }
    private static void OnLiftLeave(object sender, System.Windows.Input.MouseEventArgs e) { if (sender is FrameworkElement fe) Lift(fe, false); }

    private static void Lift(FrameworkElement fe, bool up)
    {
        if (fe.RenderTransform is not TranslateTransform t || t.IsFrozen)
        {
            t = new TranslateTransform();
            fe.RenderTransform = t;
        }
        if (fe.Effect is not System.Windows.Media.Effects.DropShadowEffect shadow)
        {
            shadow = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0 };
            fe.Effect = shadow;
        }
        double y = up ? -2 : 0, o = up ? 0.22 : 0;
        if (!Enabled)
        {
            t.BeginAnimation(TranslateTransform.YProperty, null); t.Y = y;
            shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null); shadow.Opacity = o;
            return;
        }
        var d = new Duration(TimeSpan.FromMilliseconds(140));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, d) { EasingFunction = ease });
        shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(o, d) { EasingFunction = ease });
    }

    private static void OnDrawInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnDrawInLoaded;
        if (e.NewValue is true) fe.Loaded += OnDrawInLoaded;
    }

    private static void OnDrawInLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) DrawIn(fe, GetDelayMs(fe), GetDurationMs(fe));
    }

    /// <summary>Scales the element's width from 0 (left edge) to full after <paramref name="delayMs"/>.</summary>
    public static void DrawIn(UIElement el, int delayMs, int durationMs)
    {
        if (!Enabled) return;
        var scale = new ScaleTransform(1, 1);
        el.RenderTransform = scale;
        el.RenderTransformOrigin = new Point(0, 0.5);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Frames(0, 1, delayMs, durationMs, new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
    }

    private static void OnPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnPulseLoaded;
        if (e.NewValue is true) fe.Loaded += OnPulseLoaded;
        else fe.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private static void OnPulseLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && GetPulse(fe)) Pulse(fe, GetDelayMs(fe));
    }

    /// <summary>Three soft pulses (grow 1 to 1.6, fade 0.55 to 0), starting after <paramref name="delayMs"/>.</summary>
    public static void Pulse(UIElement el, int delayMs, int times = 3)
    {
        if (!Enabled) return;
        var scale = new ScaleTransform(1, 1);
        el.RenderTransform = scale;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        const int period = 1300;
        var grow = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        for (int i = 0; i < times; i++)
        {
            var t0 = TimeSpan.FromMilliseconds(delayMs + i * period);
            var t1 = TimeSpan.FromMilliseconds(delayMs + i * period + 1100);
            grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(t0)));
            grow.KeyFrames.Add(new EasingDoubleKeyFrame(1.6, KeyTime.FromTimeSpan(t1), new CubicEase { EasingMode = EasingMode.EaseOut }));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(t0)));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(t1), new CubicEase { EasingMode = EasingMode.EaseOut }));
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        el.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Windows animations are on (and no test turned them off): every Findings animation checks this and shows its end state at once otherwise.</summary>
    public static bool Enabled => !ForceOff && SystemParameters.ClientAreaAnimation;

    /// <summary>Tests (snapshot renders) turn every animation off so the end state is drawn.</summary>
    internal static bool ForceOff { get; set; }

    private static void OnPopInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnLoaded;
        if (e.NewValue is true) fe.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) Pop(fe, GetDelayMs(fe), GetOvershoot(fe));
    }

    /// <summary>Fades (from 0 to the element's own opacity) and scales (0.6 to 1) the element in after <paramref name="delayMs"/>.</summary>
    public static void Pop(UIElement el, int delayMs = 0, bool overshoot = false, int durationMs = 320)
    {
        if (!Enabled) return;
        var scale = el.RenderTransform as ScaleTransform;
        if (scale == null || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1);
            el.RenderTransform = scale;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        IEasingFunction ease = overshoot ? new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
        // key frames from time 0 so the element stays hidden during the delay (a BeginTime would show it until then)
        var target = (double)el.GetValue(UIElement.OpacityProperty);
        el.BeginAnimation(UIElement.OpacityProperty, Frames(0, target, delayMs, durationMs, new CubicEase { EasingMode = EasingMode.EaseOut }));
        var sa = Frames(0.6, 1, delayMs, durationMs, ease);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
    }

    /// <summary>Holds <paramref name="from"/> for <paramref name="delayMs"/>, then eases to <paramref name="to"/>; the base value shows after.</summary>
    public static DoubleAnimationUsingKeyFrames Frames(double from, double to, int delayMs, int durationMs, IEasingFunction ease)
    {
        var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delayMs > 0) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(0, delayMs) + durationMs)), ease));
        return a;
    }
}
