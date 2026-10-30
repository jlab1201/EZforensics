using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace EzForensics.App.Views.Analyze;

/// <summary>Shared look for code-built menus (group captions and Segoe Fluent icons); the XAML menus use the same styles from Themes/Controls.xaml.</summary>
internal static class MenuUi
{
    // Segoe Fluent Icons glyphs (pivot: ChevronDown, ChevronUp, GroupList)
    public const string Expand = "", Collapse = "", GroupBy = "";
    public const string View = "", Filter = "", FilterOff = "", More = "", Copy = "", Flag = "",
        Clear = "", Columns = "", Fit = "", Reset = "", Keyboard = "", Export = "",
        Open = "", Folder = "", Hide = "";

    /// <summary>Group caption: small, muted, SemiBold, not focusable or clickable.</summary>
    public static MenuItem Caption(string text)
    {
        var mi = new MenuItem { Header = text, Style = (Style)Application.Current.FindResource("MenuCaptionItem") };
        AutomationProperties.SetAutomationId(mi, "Results.Menu.Caption." + text.Replace(' ', '_'));
        AutomationProperties.SetName(mi, text);
        return mi;
    }

    /// <summary>Icon for the Icon slot; <paramref name="kind"/> is "", "Info", "Accent", "Warn", "Error" or "Success" (theme brushes).</summary>
    public static TextBlock Icon(string glyph, string kind = "") =>
        new() { Text = glyph, Style = (Style)Application.Current.FindResource("MenuIcon" + kind) };

    /// <summary>Caption preceded by a separator (omitted for the first group).</summary>
    public static void Group(ItemCollection items, string caption)
    {
        if (items.Count > 0) items.Add(new Separator());
        items.Add(Caption(caption));
    }
}
