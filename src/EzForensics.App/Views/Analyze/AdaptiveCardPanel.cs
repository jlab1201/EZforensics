using System.Windows;
using System.Windows.Controls;

namespace EzForensics.App.Views.Analyze;

/// <summary>
/// Lays children out in as many equal-width columns as fit (each at least <see cref="MinColumnWidth"/>, so 1 column when narrow and
/// 2+ when wide); every child goes into the currently shortest column, so cards of different heights pack without gaps.
/// </summary>
public sealed class AdaptiveCardPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(AdaptiveCardPanel),
        new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveCardPanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private int _columns = 1;
    private double _columnWidth;

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinColumnWidth : available.Width;
        _columns = Math.Max(1, (int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing)));
        _columnWidth = Math.Max(0, (width - Spacing * (_columns - 1)) / _columns);
        var heights = new double[_columns];
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(_columnWidth, double.PositiveInfinity));
            int col = ShortestColumn(heights);
            heights[col] += (heights[col] > 0 ? Spacing : 0) + child.DesiredSize.Height;
        }
        return new Size(width, heights.Max());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var heights = new double[_columns];
        foreach (UIElement child in InternalChildren)
        {
            int col = ShortestColumn(heights);
            double y = heights[col] + (heights[col] > 0 ? Spacing : 0);
            child.Arrange(new Rect(col * (_columnWidth + Spacing), y, _columnWidth, child.DesiredSize.Height));
            heights[col] = y + child.DesiredSize.Height;
        }
        return finalSize;
    }

    private static int ShortestColumn(double[] heights)
    {
        int best = 0;
        for (int i = 1; i < heights.Length; i++) if (heights[i] < heights[best]) best = i;
        return best;
    }
}
