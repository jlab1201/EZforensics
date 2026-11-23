using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EzForensics.Core.Execution;

namespace EzForensics.App;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool x => x,
            null => false,
            string s => !string.IsNullOrEmpty(s),
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) != Invert;
}

/// <summary>
/// The "More" toggle of a one-line banner: visible only when the message has several lines or is wider than the box shows.
/// Values: the message, the box's ActualWidth (re-evaluates on resize), the box itself (font for measuring).
/// </summary>
public sealed class TextOverflowToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not string s || s.Length == 0) return Visibility.Collapsed;
        if (s.Contains('\n')) return Visibility.Visible;
        if (values[1] is not double width || width <= 0 || values[2] is not System.Windows.Controls.Control box) return Visibility.Collapsed;
        var text = new FormattedText(s, culture, box.FlowDirection, new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch),
            box.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(box).PixelsPerDip);
        // a few pixels for the text box's own inner margin
        return text.WidthIncludingTrailingWhitespace > width - 6 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>JobState → brush resource (chip colour).</summary>
public sealed class JobStateBrushConverter : IValueConverter
{
    public bool Background { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            JobState.Running => Background ? "Brush.InfoBg" : "Brush.Info",
            JobState.Succeeded => Background ? "Brush.SuccessBg" : "Brush.Success",
            JobState.Failed => Background ? "Brush.ErrorBg" : "Brush.Error",
            JobState.TimedOut or JobState.Cancelled or JobState.Skipped => Background ? "Brush.WarningBg" : "Brush.Warning",
            _ => Background ? "Brush.Control" : "Brush.TextSecondary",
        };
        return Application.Current.TryFindResource(key) as Brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SeverityGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ViewModels.InfoSeverity.Error => "",
        ViewModels.InfoSeverity.Warning => "",
        ViewModels.InfoSeverity.Success => "",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>value == ConverterParameter (compared as strings) → true / Visible; ConvertBack (bool) → parameter as int.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public bool ToVisibility { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool eq = string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), parameter as string, StringComparison.OrdinalIgnoreCase);
        return ToVisibility ? (eq ? Visibility.Visible : Visibility.Collapsed) : eq;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter as string, out var i) ? i : Binding.DoNothing;
}

/// <summary>
/// Results case tree: width of a row's Name cell so the stats columns line up at every depth.
/// Values: [0] Name column width, [1] node depth. Each level indents by <see cref="Indent"/> and the chevron takes one more.
/// </summary>
public sealed class TreeNameWidthConverter : IMultiValueConverter
{
    public const double Indent = 18;

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        double width = values.Length > 0 && values[0] is double w ? w : 240;
        int depth = values.Length > 1 && values[1] is int d ? d : 0;
        return Math.Max(24, width - Indent * (depth + 1));
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A <see cref="GridLength"/> that depends on a bool (e.g. a details row that fills the page or only takes its content).</summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public GridLength TrueLength { get; set; } = new(1, GridUnitType.Star);
    public GridLength FalseLength { get; set; } = GridLength.Auto;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? TrueLength : FalseLength;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A <see cref="GridLength"/> that depends on whether the bound value is null (e.g. a log row that shrinks while nothing is selected).</summary>
public sealed class NullToGridLengthConverter : IValueConverter
{
    public GridLength NullLength { get; set; } = GridLength.Auto;
    public GridLength NotNullLength { get; set; } = new(1, GridUnitType.Star);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value == null ? NullLength : NotNullLength;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
