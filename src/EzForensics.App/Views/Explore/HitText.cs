using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EzForensics.App.ViewModels.Explore;

namespace EzForensics.App.Views.Explore;

/// <summary>Attached behavior: fills a TextBlock with runs from search-hit segments; matched pieces use the theme accent.</summary>
public static class HitText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<TextSegment>), typeof(HitText), new PropertyMetadata(null, OnSegmentsChanged));

    public static IReadOnlyList<TextSegment>? GetSegments(DependencyObject d) => (IReadOnlyList<TextSegment>?)d.GetValue(SegmentsProperty);
    public static void SetSegments(DependencyObject d, IReadOnlyList<TextSegment>? v) => d.SetValue(SegmentsProperty, v);

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        Fill(tb, e.NewValue as IReadOnlyList<TextSegment>);
    }

    internal static void Fill(TextBlock tb, IReadOnlyList<TextSegment>? segments)
    {
        tb.Inlines.Clear();
        if (segments == null) return;
        foreach (var seg in segments)
        {
            var run = new Run(seg.Text);
            if (seg.IsMatch)
            {
                run.SetResourceReference(TextElement.BackgroundProperty, "Brush.Accent");
                run.SetResourceReference(TextElement.ForegroundProperty, "Brush.AccentText");
                run.FontWeight = FontWeights.SemiBold;
            }
            tb.Inlines.Add(run);
        }
    }
}
