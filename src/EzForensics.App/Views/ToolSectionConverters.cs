using System.Globalization;
using System.Windows.Data;
using EzForensics.App.ViewModels;

namespace EzForensics.App;

/// <summary>Colour slot (1..7 = Brush.Cat{n}Bg / Fg) and icon of a Tools list section, by its header text. One stable colour per section.</summary>
public static class ToolSectionStyle
{
    private static readonly string[] CategoryKeys =
        { "EventLogs", "Registry", "FileSystem", "ProgramExecution", "FileFolderAccess", "FileDeletion", "SQLDatabases", "SRUMDatabase", "SUMDatabase", "Timeline", "WebServers" };

    public static int Index(string header)
    {
        switch (header)
        {
            case "Disk": return 1;
            case "Memory": return 2;
            case "Local AI & documents": return 3;
            case "Timeline": return 4;
            case "Spectre engine tools": return 5;
            case "Network": return 6;
        }
        foreach (var k in CategoryKeys)
            if (ToolCategoryBadge.Label(k) == header) { var i = ToolCategoryBadge.Index(k); if (i is >= 1 and <= 7) return i; }
        int h = 0;
        foreach (var ch in header) h = unchecked(h * 31 + ch);
        return Math.Abs(h % 7) + 1;
    }

    public static string Glyph(string header) => header switch
    {
        "Disk" => "",
        "Memory" => "",
        "Network" => "",
        "Timeline" => "",
        "Local AI & documents" => "",
        "Spectre engine tools" => "",
        "Event logs" => "",
        "Registry" => "",
        "File system" => "",
        "Execution" => "",
        "File access" => "",
        "Deletion" => "",
        "Databases" => "",
        "System usage" => "",
        "Web servers" => "",
        _ => "",
    };
}

/// <summary>Section name to its colour slot, for the header triggers.</summary>
public sealed class SectionIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ToolSectionStyle.Index(value as string ?? "");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SectionGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ToolSectionStyle.Glyph(value as string ?? "");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"18 tools · 3 installed" from the group, its live item count and a stamp that changes whenever install state does.</summary>
public sealed class SectionSummaryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not CollectionViewGroup g) return "";
        int total = 0, installed = 0;
        foreach (var o in g.Items) if (o is ToolListItem t) { total++; if (t.Installed) installed++; }
        return $"{total} {(total == 1 ? "tool" : "tools")} · {installed} installed";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
