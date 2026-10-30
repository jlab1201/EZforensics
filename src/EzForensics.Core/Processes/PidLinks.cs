using System.Globalization;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Processes;

/// <summary>A PID found inside a text cell: <see cref="Start"/> / <see cref="Length"/> span the number part only ("0x1a2c" of "NewProcessId: 0x1a2c").</summary>
public readonly record struct PidToken(int Start, int Length, int Pid);

/// <summary>Which grid cells are process ID links: PID columns, PID tokens inside text cells, and the time column a row's PID is resolved against. Pure.</summary>
public static class PidLinks
{
    private static readonly HashSet<string> PidColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "PID", "PPID", "ProcessId", "ParentProcessId", "NewProcessId", "TargetPID", "ParentPID", "Process ID", "OwningProcess",
    };

    private static readonly Regex Suffix = new(@"\s*[\(\[][^\)\]]*[\)\]]\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TokenRx = new(@"\b(ProcessId|NewProcessId|ParentProcessId|ProcessID|PID|PPID)\s*[:=]\s*(0x[0-9a-fA-F]+|\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Time column names in priority order.</summary>
    private static readonly string[] TimeColumns =
    {
        "TimeCreated", "CreateTime", "DateTimeUtc", "Timestamp", "TimeStamp", "Time", "datetime", "EventTime", "SourceModified",
    };

    /// <summary>A column whose cells are process IDs (PID, PPID, ProcessId, ParentProcessId, NewProcessId, TargetPID, ParentPID, "Process ID", OwningProcess); a "(PID)" style suffix is ignored. ThreadId / TID are not.</summary>
    public static bool IsPidColumn(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        var h = header.Trim();
        if (PidColumns.Contains(h)) return true;
        var stripped = Suffix.Replace(h, "");
        return stripped.Length > 0 && stripped.Length != h.Length && PidColumns.Contains(stripped);
    }

    /// <summary>
    /// Text columns scanned for "ProcessId: 0x1a2c" style tokens: the EvtxECmd map output (the only parser in the repo that writes such tokens; PayloadData1-6 hold
    /// "Key: value" cells, ExecutableInfo the command line). Hayabusa / Chainsaw (Details, ExtraFieldInfo, Message) are not integrated, so their columns are not listed;
    /// add them here with the parser. Keep this list tight: every listed column costs a token scan per cell, and it decides whether a file shows PID links at all.
    /// </summary>
    private static readonly HashSet<string> PidTokenColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "PayloadData1", "PayloadData2", "PayloadData3", "PayloadData4", "PayloadData5", "PayloadData6", "ExecutableInfo",
    };

    /// <summary>A text column whose cells may carry "ProcessId: 0x…" style tokens (<see cref="PidTokenColumns"/>); the grid gives only these the token scan.</summary>
    public static bool IsPidTokenColumn(string? header) => !string.IsNullOrWhiteSpace(header) && PidTokenColumns.Contains(header.Trim());

    /// <summary>
    /// Whether the grid can show PID links for a CSV with these headers (given its host has process data): a <see cref="IsPidColumn"/> or a <see cref="IsPidTokenColumn"/>
    /// column. Registry, prefetch and similar CSVs without either have none, so the process graph closes on them.
    /// </summary>
    public static bool HasPidSource(IReadOnlyList<string> headers)
    {
        foreach (var h in headers)
            if (IsPidColumn(h) || IsPidTokenColumn(h)) return true;
        return false;
    }

    /// <summary>Decimal or 0x-hex PID of a cell; 0, blank, negative and unparsable cells are not links. PID 4 is valid.</summary>
    public static bool TryParsePid(string? cell, out int pid)
    {
        pid = 0;
        if (string.IsNullOrWhiteSpace(cell)) return false;
        var t = cell.Trim();
        long v;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(t.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v)) return false;
        }
        else if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return false;
        if (v <= 0 || v > int.MaxValue) return false;
        pid = (int)v;
        return true;
    }

    /// <summary>PID tokens ("NewProcessId: 0x1a2c", "PID=412") inside a text cell, in order; PID 0 is skipped. Cheap for text without "id".</summary>
    public static IReadOnlyList<PidToken> FindTokens(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 5 || text.IndexOf("id", StringComparison.OrdinalIgnoreCase) < 0) return Array.Empty<PidToken>();
        List<PidToken>? list = null;
        foreach (Match m in TokenRx.Matches(text))
        {
            var g = m.Groups[2];
            if (!TryParsePid(g.Value, out var pid)) continue;
            (list ??= new()).Add(new PidToken(g.Index, g.Length, pid));
        }
        return list ?? (IReadOnlyList<PidToken>)Array.Empty<PidToken>();
    }

    /// <summary>Index of the column the row time is read from (first match by priority of <see cref="TimeColumns"/>); -1 when there is none.</summary>
    public static int TimeColumnIndex(IReadOnlyList<string> headers)
    {
        foreach (var name in TimeColumns)
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i]?.Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Row time a PID is resolved against: the <see cref="TimeColumnIndex"/> cell parsed like the grid parses dates (invariant culture, UTC assumed,
    /// <see cref="Results.CsvValueParser.TryParseDate"/>); null without a time column or with a blank / unparsable cell.
    /// </summary>
    public static DateTime? RowTime(IReadOnlyList<string> headers, IReadOnlyList<string> cells) => ParseCell(cells, TimeColumnIndex(headers));

    /// <summary>The row's <c>CreateTime</c> cell (a Volatility process list row: tells several instances with one PID apart); null when there is none.</summary>
    public static DateTime? RowCreateTime(IReadOnlyList<string> headers, IReadOnlyList<string> cells)
    {
        for (int i = 0; i < headers.Count; i++)
            if (string.Equals(headers[i]?.Trim(), "CreateTime", StringComparison.OrdinalIgnoreCase)) return ParseCell(cells, i);
        return null;
    }

    private static DateTime? ParseCell(IReadOnlyList<string> cells, int i) =>
        i >= 0 && i < cells.Count && cells[i] is { } s && Results.CsvValueParser.TryParseDate(s.Trim(), out var t) ? t : null;

    /// <summary>
    /// Host folder whose process tree a CSV's PIDs open: the top folder under <paramref name="caseRoot"/> that contains the CSV (<c>case\HOST\Category\x.csv</c>,
    /// <c>case\HOST\Memory\x.csv</c>). Null for a CSV directly in the case root (super timeline), under <c>_Case</c> or a dot / logs folder. A CSV outside
    /// <paramref name="caseRoot"/> (or without one): its folder, or the parent of a <c>Memory</c> / <c>.ezprocesses</c> folder. The caller still checks the
    /// folder has process data.
    /// </summary>
    public static string? HostFolderOfCsv(string? caseRoot, string csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) return null;
        string full;
        try { full = Path.GetFullPath(csvPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!string.IsNullOrWhiteSpace(caseRoot))
        {
            string root;
            try { root = Path.GetFullPath(caseRoot).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { root = ""; }
            if (root.Length > 0 && full.Length > root.Length + 1 && full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full[root.Length] is '\\' or '/')
            {
                var rest = full[(root.Length + 1)..];
                int sep = rest.IndexOfAny(new[] { '\\', '/' });
                if (sep <= 0) return null; // a file in the case root
                var first = rest[..sep];
                if (Results.CaseTreeOrder.IsCaseWide(first) || Results.CaseTreeOrder.IsHiddenFolder(first)) return null;
                return Path.Combine(root, first);
            }
        }
        var dir = Path.GetDirectoryName(full) ?? "";
        var folder = Path.GetFileName(dir);
        return string.Equals(folder, "Memory", StringComparison.OrdinalIgnoreCase) || string.Equals(folder, ProcessCorrelator.ListFolderName, StringComparison.OrdinalIgnoreCase) || string.Equals(folder, ProcessCorrelator.FolderName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dir) ?? dir : dir;
    }
}
