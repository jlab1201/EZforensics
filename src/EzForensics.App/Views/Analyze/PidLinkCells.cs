using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using EzForensics.Core.Processes;

namespace EzForensics.App.Views.Analyze;

/// <summary>
/// Clickable process IDs in grid cells (events grid, details card row grids). A PID column's TextBlock gets <see cref="ColumnTextProperty"/>: the text, and the link
/// look when the cell parses as a PID (<see cref="PidLinks.TryParsePid"/>). Other text columns get <see cref="TokenTextProperty"/>: plain text, and only when
/// <see cref="PidLinks.FindTokens"/> finds "NewProcessId: 0x1a2c" style tokens, Run + Hyperlink inlines for the numbers. Nothing is allocated for plain cells and
/// nothing is cached. Clicks are tracked on the grid (<see cref="Track"/>), not by the Hyperlink, so cell selection, drags and Ctrl / Shift clicks behave as before.
/// </summary>
public static class PidLinkCells
{
    /// <summary>PID of a link (a PID column TextBlock or a token Hyperlink); 0 = not a link.</summary>
    public static readonly DependencyProperty PidProperty = DependencyProperty.RegisterAttached("Pid", typeof(int), typeof(PidLinkCells), new PropertyMetadata(0));
    public static int GetPid(DependencyObject d) => (int)d.GetValue(PidProperty);
    public static void SetPid(DependencyObject d, int value) => d.SetValue(PidProperty, value);

    /// <summary>True on a PID column TextBlock whose text is a PID (style triggers: accent foreground, hand cursor, underline on hover).</summary>
    public static readonly DependencyProperty IsLinkProperty = DependencyProperty.RegisterAttached("IsLink", typeof(bool), typeof(PidLinkCells), new PropertyMetadata(false));
    public static bool GetIsLink(DependencyObject d) => (bool)d.GetValue(IsLinkProperty);
    public static void SetIsLink(DependencyObject d, bool value) => d.SetValue(IsLinkProperty, value);

    /// <summary>Cell text of a PID column (bound OneTime / OneWay instead of TextBlock.Text).</summary>
    public static readonly DependencyProperty ColumnTextProperty = DependencyProperty.RegisterAttached("ColumnText", typeof(string), typeof(PidLinkCells),
        new PropertyMetadata(null, OnColumnTextChanged));
    public static string? GetColumnText(DependencyObject d) => (string?)d.GetValue(ColumnTextProperty);
    public static void SetColumnText(DependencyObject d, string? value) => d.SetValue(ColumnTextProperty, value);

    /// <summary>Cell text of another text column: PID tokens in it become Hyperlink inlines.</summary>
    public static readonly DependencyProperty TokenTextProperty = DependencyProperty.RegisterAttached("TokenText", typeof(string), typeof(PidLinkCells),
        new PropertyMetadata(null, OnTokenTextChanged));
    public static string? GetTokenText(DependencyObject d) => (string?)d.GetValue(TokenTextProperty);
    public static void SetTokenText(DependencyObject d, string? value) => d.SetValue(TokenTextProperty, value);

    public static string LinkName(int pid) => "Open PID " + pid.ToString(CultureInfo.InvariantCulture) + " in the process tree";

    private static void OnColumnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var s = e.NewValue as string ?? "";
        tb.Text = s;
        bool link = PidLinks.TryParsePid(s, out var pid);
        SetPid(tb, link ? pid : 0);
        SetIsLink(tb, link);
    }

    private static void OnTokenTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var s = e.NewValue as string ?? "";
        var tokens = PidLinks.FindTokens(s);
        if (tokens.Count == 0) { tb.Text = s; return; } // plain cell: one string, no inlines
        var inlines = tb.Inlines;
        inlines.Clear();
        int at = 0;
        foreach (var t in tokens)
        {
            if (t.Start > at) inlines.Add(new Run(s.Substring(at, t.Start - at)));
            var h = new Hyperlink(new Run(s.Substring(t.Start, t.Length))) { Style = LinkStyle, Focusable = false };
            SetPid(h, t.Pid);
            AutomationProperties.SetName(h, LinkName(t.Pid));
            inlines.Add(h);
            at = t.Start + t.Length;
        }
        if (at < s.Length) inlines.Add(new Run(s.Substring(at)));
    }

    private static Style? s_linkStyle;

    /// <summary>Token hyperlinks: accent text, underline only on hover (the app's Hyperlink style always underlines).</summary>
    private static Style LinkStyle => s_linkStyle ??= BuildLinkStyle();

    private static Style BuildLinkStyle()
    {
        var s = new Style(typeof(Hyperlink));
        s.Setters.Add(new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension("Brush.Accent")));
        s.Setters.Add(new Setter(Inline.TextDecorationsProperty, null));
        s.Setters.Add(new Setter(FrameworkContentElement.CursorProperty, Cursors.Hand));
        var hover = new Trigger { Property = ContentElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Inline.TextDecorationsProperty, TextDecorations.Underline));
        hover.Setters.Add(new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension("Brush.AccentHover")));
        s.Triggers.Add(hover);
        s.Seal();
        return s;
    }

    /// <summary>
    /// ElementStyle of a PID column: the cell text through <see cref="ColumnTextProperty"/> (<paramref name="binding"/>), left-aligned so only the number is the link,
    /// plus the link look while <see cref="IsLinkProperty"/> is set. Keeps <paramref name="basedOn"/>'s setters (e.g. TextTrimming).
    /// </summary>
    public static Style ColumnStyle(BindingBase binding, Style? basedOn)
    {
        var s = new Style(typeof(TextBlock), basedOn);
        s.Setters.Add(new Setter(ColumnTextProperty, binding));
        s.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left));
        var link = new Trigger { Property = IsLinkProperty, Value = true };
        link.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Brush.Accent")));
        link.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        s.Triggers.Add(link);
        var hover = new MultiTrigger();
        hover.Conditions.Add(new System.Windows.Condition(IsLinkProperty, true));
        hover.Conditions.Add(new System.Windows.Condition(UIElement.IsMouseOverProperty, true));
        hover.Setters.Add(new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Underline));
        hover.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Brush.AccentHover")));
        s.Triggers.Add(hover);
        return s;
    }

    /// <summary>ElementStyle of another text column: the cell text through <see cref="TokenTextProperty"/> (<paramref name="binding"/>).</summary>
    public static Style TokenStyle(BindingBase binding, Style? basedOn)
    {
        var s = new Style(typeof(TextBlock), basedOn);
        s.Setters.Add(new Setter(TokenTextProperty, binding));
        return s;
    }

    /// <summary>
    /// The PID link under <paramref name="source"/> (a mouse event's OriginalSource): a token Hyperlink (or its Run) or a PID column TextBlock; returns the PID and
    /// the element whose DataContext is the row. 0 = no link.
    /// </summary>
    public static int HitTest(object? source, out FrameworkElement? host)
    {
        host = null;
        for (var d = source as DependencyObject; d != null;)
        {
            switch (d)
            {
                case Hyperlink h when GetPid(h) > 0:
                    for (DependencyObject? p = h; p != null; p = LogicalTreeHelper.GetParent(p))
                        if (p is FrameworkElement fe) { host = fe; return GetPid(h); }
                    return 0;
                case TextBlock tb:
                    if (GetIsLink(tb) && GetPid(tb) > 0) { host = tb; return GetPid(tb); }
                    return 0;
                case FrameworkContentElement fce:
                    d = fce.Parent;
                    break;
                default:
                    return 0;
            }
        }
        return 0;
    }

    /// <summary>
    /// Watches plain left clicks on PID links inside <paramref name="grid"/>: a press without Ctrl / Shift / Alt on a link, released without moving past the drag distance,
    /// calls <paramref name="open"/>(the link's DataContext = the row, pid) after the grid has handled the click (the row selection may change).
    /// </summary>
    public static void Track(UIElement grid, Action<object?, int> open)
    {
        FrameworkElement? pressHost = null;
        int pressPid = 0;
        Point start = default;
        grid.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            pressHost = null; pressPid = 0;
            if (e.ClickCount != 1 || Keyboard.Modifiers != ModifierKeys.None) return;
            var pid = HitTest(e.OriginalSource, out var host);
            if (pid <= 0 || host == null) return;
            pressHost = host; pressPid = pid; start = e.GetPosition(grid);
        }), true);
        grid.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            if (pressHost == null) return;
            var p = e.GetPosition(grid);
            if (e.LeftButton != MouseButtonState.Pressed
                || Math.Abs(p.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(p.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance)
            { pressHost = null; pressPid = 0; }
        }), true);
        grid.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            var host = pressHost; var pid = pressPid;
            pressHost = null; pressPid = 0;
            if (host == null || pid <= 0 || Keyboard.Modifiers != ModifierKeys.None) return;
            var row = host.DataContext;
            // after the grid's own mouse-up handling (selection, Hyperlink capture release)
            grid.Dispatcher.BeginInvoke(new Action(() => open(row, pid)), System.Windows.Threading.DispatcherPriority.Input);
        }), true);
    }
}
