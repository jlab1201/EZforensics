using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EzForensics.Core.Results;

namespace EzForensics.App.Views.Controls;

/// <summary>
/// Attached property that renders filter-expression text with syntax colouring.
/// Usage: <c>&lt;TextBlock local:FilterHighlighter.Text="{Binding FilterText}"/&gt;</c>.
/// Brushes come from resource keys Filter.Column/Function/Operator/String/Number/Logical/Not/Paren (theme-aware
/// via SetResourceReference), with Timeline-Explorer-like fallbacks when a key is missing.
/// </summary>
public static class FilterHighlighter
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(FilterHighlighter), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        Rebuild(tb, e.NewValue as string);
    }

    private static readonly Brush ColumnFallback = Frozen(0x1F, 0x4E, 0xC9);
    private static readonly Brush FunctionFallback = Frozen(0x8B, 0x2F, 0xB5);
    private static readonly Brush OperatorFallback = Frozen(0x60, 0x60, 0x60);
    private static readonly Brush StringFallback = Frozen(0x9C, 0x1C, 0x1C);
    private static readonly Brush NumberFallback = Frozen(0x00, 0x80, 0x80);
    private static readonly Brush LogicalFallback = Frozen(0xE0, 0x7B, 0x00);
    private static readonly Brush NotFallback = Frozen(0xD0, 0x20, 0x20);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private static (string? key, Brush? fallback, bool bold) Style(FilterTokenKind k) => k switch
    {
        FilterTokenKind.Column => ("Filter.Column", ColumnFallback, false),
        FilterTokenKind.Function => ("Filter.Function", FunctionFallback, false),
        FilterTokenKind.Operator => ("Filter.Operator", OperatorFallback, false),
        FilterTokenKind.String => ("Filter.String", StringFallback, false),
        FilterTokenKind.Number => ("Filter.Number", NumberFallback, false),
        FilterTokenKind.Date => ("Filter.Number", NumberFallback, false),
        FilterTokenKind.Logical => ("Filter.Logical", LogicalFallback, true),
        FilterTokenKind.Not => ("Filter.Not", NotFallback, true),
        FilterTokenKind.Paren => ("Filter.Paren", OperatorFallback, false),
        FilterTokenKind.Comma => ("Filter.Paren", OperatorFallback, false),
        _ => (null, null, false),
    };

    private static void Rebuild(TextBlock tb, string? text)
    {
        tb.Inlines.Clear();
        if (string.IsNullOrEmpty(text)) return;
        foreach (var t in FilterTokenizer.Tokenize(text))
        {
            var run = new Run(t.Text);
            var (key, fallback, bold) = Style(t.Kind);
            if (key != null)
            {
                if (tb.TryFindResource(key) is Brush)
                    run.SetResourceReference(TextElement.ForegroundProperty, key);
                else
                    run.Foreground = fallback;
                if (bold) run.FontWeight = FontWeights.Bold;
            }
            tb.Inlines.Add(run);
        }
    }
}
