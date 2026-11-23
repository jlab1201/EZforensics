using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EzForensics.App.ViewModels;
using EzForensics.App.Views.Analyze;

namespace EzForensics.App.Views.Controls;

/// <summary>
/// Highlights search hits in grid cell TextBlocks. Set <see cref="QueryProperty"/> on the DataGrid (it inherits to every cell) and
/// <see cref="EnabledProperty"/> in the cell's ElementStyle. Matches get inline runs (not on PID links: those only get the cell tint);
/// when the text is cut off (ellipsis / more lines than fit) and has a match, the whole cell gets a subtle tint.
/// Nothing happens while the query is empty.
/// </summary>
public static class CellSearchHit
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached("Query", typeof(string), typeof(CellSearchHit),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.Inherits, OnChanged));
    public static string GetQuery(DependencyObject d) => (string)d.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject d, string v) => d.SetValue(QueryProperty, v);

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(CellSearchHit),
        new PropertyMetadata(false, OnEnabled));
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(CellSearchHit), new PropertyMetadata(false));
    private static readonly DependencyProperty InlinedProperty = DependencyProperty.RegisterAttached("Inlined", typeof(bool), typeof(CellSearchHit), new PropertyMetadata(false));
    private static readonly DependencyProperty BusyProperty = DependencyProperty.RegisterAttached("Busy", typeof(bool), typeof(CellSearchHit), new PropertyMetadata(false));

    private static readonly DependencyPropertyDescriptor? TextDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    // parsed terms are shared by every cell of the same query
    private static string? s_query;
    private static IReadOnlyList<string> s_terms = Array.Empty<string>();
    private static IReadOnlyList<string> TermsOf(string q)
    {
        if (!string.Equals(q, s_query, StringComparison.Ordinal)) { s_terms = SearchHighlight.Terms(q); s_query = q; }
        return s_terms;
    }

    private static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb || e.NewValue is not true || (bool)tb.GetValue(HookedProperty)) return;
        tb.SetValue(HookedProperty, true);
        tb.SizeChanged += (_, _) => { if (NeedsWork(tb)) Update(tb); };
        tb.Loaded += (_, _) => { TextDescriptor?.AddValueChanged(tb, OnTextChanged); if (NeedsWork(tb)) Update(tb); };
        tb.Unloaded += (_, _) => TextDescriptor?.RemoveValueChanged(tb, OnTextChanged);
        if (tb.IsLoaded) TextDescriptor?.AddValueChanged(tb, OnTextChanged);
        if (NeedsWork(tb)) Update(tb);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock tb && !(bool)tb.GetValue(BusyProperty) && NeedsWork(tb)) Update(tb);
    }

    /// <summary>False for the common idle case: no query and nothing left to clean up.</summary>
    private static bool NeedsWork(TextBlock tb) => GetQuery(tb).Length > 0 || (bool)tb.GetValue(InlinedProperty) || tb.Background != null;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock tb && GetEnabled(tb) && NeedsWork(tb)) Update(tb);
    }

    private static void Update(TextBlock tb)
    {
        if ((bool)tb.GetValue(BusyProperty)) return;
        tb.SetValue(BusyProperty, true);
        try
        {
            var query = GetQuery(tb);
            bool wasInlined = (bool)tb.GetValue(InlinedProperty);
            var terms = query.Length == 0 ? Array.Empty<string>() : TermsOf(query);
            var text = tb.Text ?? "";
            if (terms.Count == 0)
            {
                if (wasInlined) { tb.Inlines.Clear(); tb.Text = text; tb.SetValue(InlinedProperty, false); }
                if (tb.Background != null) tb.ClearValue(TextBlock.BackgroundProperty);
                return;
            }
            var segs = SearchHighlight.Segments(text, terms);
            bool hit = SearchHighlight.HasMatch(segs);
            bool plain = !PidLinkCells.GetIsLink(tb) && !tb.Inlines.OfType<Hyperlink>().Any();
            if (hit && plain)
            {
                tb.Inlines.Clear();
                foreach (var s in segs)
                {
                    var run = new Run(s.Text);
                    if (s.IsMatch)
                    {
                        run.SetResourceReference(TextElement.BackgroundProperty, "Brush.SearchHit");
                        run.SetResourceReference(TextElement.ForegroundProperty, "Brush.SearchHitText");
                    }
                    tb.Inlines.Add(run);
                }
                tb.SetValue(InlinedProperty, true);
            }
            else if (wasInlined) { tb.Inlines.Clear(); tb.Text = text; tb.SetValue(InlinedProperty, false); }
            if (hit && IsCutOff(tb, text)) tb.SetResourceReference(TextBlock.BackgroundProperty, "Brush.SearchHitCell");
            else if (tb.Background != null) tb.ClearValue(TextBlock.BackgroundProperty);
        }
        finally { tb.ClearValue(BusyProperty); }
    }

    /// <summary>True when the visible cell cannot show all of the text (single line wider than the cell, or a multi-line value).</summary>
    private static bool IsCutOff(TextBlock tb, string text)
    {
        if (text.Contains('\n') || text.Contains('\r')) return true;
        double w = tb.ActualWidth;
        if (w <= 0) return false;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, tb.FlowDirection, new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace > w + 0.5;
    }
}
