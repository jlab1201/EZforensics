using System.Windows;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.Core.Settings;

namespace EzForensics.App.Controls;

/// <summary>
/// One piece of the stage journey's connecting line: a horizontal stroke through the middle, solid between two stages with evidence and
/// dashed where a stage has none.
/// </summary>
public sealed class JourneyLine : FrameworkElement
{
    public static readonly DependencyProperty DashedProperty = DependencyProperty.Register(nameof(Dashed), typeof(bool), typeof(JourneyLine),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Dashed { get => (bool)GetValue(DashedProperty); set => SetValue(DashedProperty, value); }

    public JourneyLine()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(AppTheme _) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(0, 3);

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 1) return;
        var brush = TryFindResource(Dashed ? "Brush.BorderStrong" : "Brush.TextSecondary") as Brush ?? Brushes.Gray;
        var pen = Dashed ? new Pen(brush, 1.5) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) } : new Pen(brush, 2.5);
        pen.Freeze();
        double y = Math.Round(ActualHeight / 2) + (Dashed ? 0.25 : 0);
        dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
    }
}
