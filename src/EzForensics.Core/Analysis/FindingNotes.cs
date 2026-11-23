namespace EzForensics.Core.Analysis;

/// <summary>
/// A finding has one notes field, so the editor's Evidence (one line per row) and Notes are stored together in it:
/// <c>Evidence:</c> line, the evidence lines, a blank line, then the notes. Text without that header is all notes.
/// The Report and Findings pages show the field as it is, so the evidence reads as part of the notes.
/// </summary>
public static class FindingNotes
{
    public const string Header = "Evidence:";

    public static string Compose(string? evidence, string? notes)
    {
        var ev = string.Join("\n", (evidence ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        var n = (notes ?? "").Trim();
        if (ev.Length == 0) return n;
        return n.Length == 0 ? Header + "\n" + ev : Header + "\n" + ev + "\n\n" + n;
    }

    private static readonly string[] KeyColumns =
    {
        "EventId", "Message", "Image", "CommandLine", "ProcessName", "Process", "ParentImage", "TargetFilename", "FileName", "Name", "Path", "FullPath",
        "Description", "Provider", "Channel", "UserName", "User", "RemoteHost", "DestinationIp", "SourceIp",
    };

    /// <summary>
    /// "2026-01-02 08:16:00 | PC1 | 4688 | cmd.exe /c ..." for one grid row: the time column, the host column and up to three key columns
    /// (well known names first, else the first filled ones). Each value is cut to 80 characters.
    /// </summary>
    public static string EvidenceLine(IReadOnlyList<string> headers, IReadOnlyList<string> cells)
    {
        string At(int i) => i >= 0 && i < cells.Count ? (cells[i] ?? "").Trim().Replace("\r", " ").Replace("\n", " ") : "";
        static string Cut(string v) => v.Length > 80 ? v[..80] + "…" : v;
        int time = Find(headers, h => h.Contains("time", StringComparison.OrdinalIgnoreCase) || h.Contains("date", StringComparison.OrdinalIgnoreCase));
        int host = Find(headers, h => h.Equals("Computer", StringComparison.OrdinalIgnoreCase) || h.StartsWith("Host", StringComparison.OrdinalIgnoreCase)
                                       || h.Equals("MachineName", StringComparison.OrdinalIgnoreCase) || h.Equals("Machine", StringComparison.OrdinalIgnoreCase));
        var used = new HashSet<int> { time, host };
        var parts = new List<string>();
        foreach (var i in new[] { time, host }) if (At(i).Length > 0) parts.Add(Cut(At(i)));
        int keys = 0;
        foreach (var k in KeyColumns)
        {
            if (keys == 3) break;
            int i = Find(headers, h => h.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || !used.Add(i) || At(i).Length == 0) continue;
            parts.Add(Cut(At(i))); keys++;
        }
        for (int i = 0; keys < 3 && i < headers.Count; i++)
            if (used.Add(i) && At(i).Length > 0) { parts.Add(Cut(At(i))); keys++; }
        return string.Join(" | ", parts);
    }

    private static int Find(IReadOnlyList<string> headers, Func<string, bool> pred)
    {
        for (int i = 0; i < headers.Count; i++) if (pred(headers[i] ?? "")) return i;
        return -1;
    }

    public static (string Evidence, string Notes) Split(string? stored)
    {
        var t = (stored ?? "").Replace("\r", "");
        if (!t.StartsWith(Header + "\n", StringComparison.Ordinal) && t != Header) return ("", t.Trim());
        var body = t.Length > Header.Length ? t[(Header.Length + 1)..] : "";
        var cut = body.IndexOf("\n\n", StringComparison.Ordinal);
        return cut < 0 ? (body.Trim(), "") : (body[..cut].Trim(), body[(cut + 2)..].Trim());
    }
}
