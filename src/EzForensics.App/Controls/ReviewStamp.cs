using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EzForensics.App.Controls;

/// <summary>
/// The review feedback on a story card. <c>Reviewed</c> settles the card faint; each new <c>Token</c> plays the stamp element
/// (<c>Stamp</c>, hidden at rest) once: it lands, holds, and fades while the card stays fully visible, then the card settles. With
/// animations off nothing plays and the card shows its end state at once.
/// </summary>
public static class ReviewStamp
{
    /// <summary>The opacity a reviewed card settles at.</summary>
    public const double ReviewedOpacity = 0.72;

    public static readonly DependencyProperty ReviewedProperty =
        DependencyProperty.RegisterAttached("Reviewed", typeof(bool), typeof(ReviewStamp), new PropertyMetadata(false, OnReviewedChanged));
    public static bool GetReviewed(DependencyObject d) => (bool)d.GetValue(ReviewedProperty);
    public static void SetReviewed(DependencyObject d, bool v) => d.SetValue(ReviewedProperty, v);

    public static readonly DependencyProperty TokenProperty =
        DependencyProperty.RegisterAttached("Token", typeof(int), typeof(ReviewStamp), new PropertyMetadata(0, OnTokenChanged));
    public static int GetToken(DependencyObject d) => (int)d.GetValue(TokenProperty);
    public static void SetToken(DependencyObject d, int v) => d.SetValue(TokenProperty, v);

    public static readonly DependencyProperty StampProperty =
        DependencyProperty.RegisterAttached("Stamp", typeof(UIElement), typeof(ReviewStamp), new PropertyMetadata(null));
    public static UIElement? GetStamp(DependencyObject d) => (UIElement?)d.GetValue(StampProperty);
    public static void SetStamp(DependencyObject d, UIElement? v) => d.SetValue(StampProperty, v);

    private static void OnReviewedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement el) el.Opacity = e.NewValue is true ? ReviewedOpacity : 1;
    }

    private static void OnTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement card || (int)e.NewValue <= 0 || !card.IsLoaded || !Motion.Enabled) return;
        if (GetStamp(card) is not { } stamp) return;
        Play(card, stamp);
    }

    /// <summary>Times (ms) of the stamp: lands by 220, holds to 800, gone by 1050; the card settles faint from 900 to 1200.</summary>
    private static void Play(FrameworkElement card, UIElement stamp)
    {
        var land = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
        var smooth = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(-8);
        stamp.RenderTransform = new TransformGroup { Children = { scale, rotate } };
        stamp.RenderTransformOrigin = new Point(0.5, 0.5);

        var s = Keys((0, 1.9, null), (220, 1, land));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
        stamp.BeginAnimation(UIElement.OpacityProperty, Keys((0, 0, null), (140, 1, smooth), (800, 1, null), (1050, 0, smooth)));
        card.BeginAnimation(UIElement.OpacityProperty, Keys((0, 1, null), (900, 1, null), (1200, card.Opacity, smooth)));
    }

    private static DoubleAnimationUsingKeyFrames Keys(params (int Ms, double Value, IEasingFunction? Ease)[] keys)
    {
        var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (ms, v, ease) in keys)
        {
            var t = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
            a.KeyFrames.Add(ease == null ? new DiscreteDoubleKeyFrame(v, t) : new EasingDoubleKeyFrame(v, t, ease));
        }
        return a;
    }
}
