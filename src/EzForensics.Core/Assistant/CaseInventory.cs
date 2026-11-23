using System.Text;
using EzForensics.Core.Case;

namespace EzForensics.Core.Assistant;

/// <summary>
/// Compact text of what the case holds, one line per CSV with a hint of what it answers, for the <c>&lt;app_state&gt;</c> block of each
/// chat message. Small models do not reliably call <c>case_list_files</c>, so the files are put in front of them. Capped; cached per case
/// until the CSV count, a modified time or a manifest changes.
/// </summary>
public static class CaseInventory
{
    public const int MaxLines = 40;
    public const int MaxChars = 3000;

    private static readonly object Lock = new();
    private static string? _cachedDir, _cachedKey, _cachedText;

    /// <summary>The "Case files" block of the app state: the inventory, or a short note when the case has no CSV.</summary>
    public static string AppStateText(string dir) => Build(dir) is { Length: > 0 } inv ? "Case files:\n" + inv : "Case CSV files: none";

    /// <summary>Inventory text; empty when the folder is missing or has no CSV. Cheap to call on every message.</summary>
    public static string Build(string? caseDir)
    {
        if (string.IsNullOrEmpty(caseDir) || !Directory.Exists(caseDir)) return "";
        var files = new List<(string Full, FileInfo Info)>();
        var o = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var f in Directory.EnumerateFiles(caseDir, "*.csv", o).Take(20_000)) files.Add((f, new FileInfo(f)));
        }
        catch (IOException) { }
        var key = Key(caseDir, files);
        lock (Lock)
            if (_cachedText != null && _cachedDir == caseDir && _cachedKey == key) return _cachedText;
        var text = Render(caseDir, files);
        lock (Lock) { _cachedDir = caseDir; _cachedKey = key; _cachedText = text; }
        return text;
    }

    private static string Key(string dir, List<(string Full, FileInfo Info)> files)
    {
        long h = files.Count;
        foreach (var (_, i) in files) h = unchecked(h * 31 + i.LastWriteTimeUtc.Ticks + i.Length);
        foreach (var m in CaseIndex.ManifestFiles(dir)) h = unchecked(h * 31 + File.GetLastWriteTimeUtc(m).Ticks);
        return h.ToString();
    }

    private static string Render(string dir, List<(string Full, FileInfo Info)> files)
    {
        if (files.Count == 0) return "";
        var index = CaseIndex.Load(dir);
        var rows = new List<(int Rank, string Rel, string Line)>();
        foreach (var (full, info) in files)
        {
            var p = index.Describe(full);
            var rel = Path.GetRelativePath(dir, full);
            var hint = ArtifactGuide.For(p.ToolId, p.ArtifactId, Path.GetFileName(full));
            var kind = string.Join("/", new[] { p.ToolId, p.ArtifactId }.Where(s => !string.IsNullOrEmpty(s)));
            var sb = new StringBuilder(rel);
            if (kind.Length > 0) sb.Append(" — ").Append(kind);
            if (p.Host != null) sb.Append(" — ").Append(p.Host);
            else if (p.IsCaseWide) sb.Append(" — case-wide");
            sb.Append(" — ").Append(RowsText(full, info.Length));
            if (hint != null) sb.Append(" — ").Append(hint.Hint);
            rows.Add((hint?.Rank ?? 9, rel, sb.ToString()));
        }
        rows.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase));

        var outp = new StringBuilder();
        outp.Append(files.Count).Append(" CSV file(s), most relevant to execution first. Format: path — tool/artifact — host — rows — what it answers");
        if (index.Hosts.Count > 0) outp.Append("\nHosts: ").Append(string.Join(", ", index.Hosts.Take(10)));
        int shown = 0;
        foreach (var (_, _, line) in rows)
        {
            if (shown >= MaxLines || outp.Length + line.Length + 1 > MaxChars) break;
            outp.Append('\n').Append(line); shown++;
        }
        if (shown < rows.Count) outp.Append("\n+").Append(rows.Count - shown).Append(" more, use case_list_files");
        return outp.ToString();
    }

    /// <summary>Exact rows for files that fit in 64 KB (quote-aware); larger files are estimated from the first 64 KB (average line length), so none is read whole.</summary>
    private static string RowsText(string path, long size)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[65536];
            int n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) return "rows 0";
            if (n >= size) return "rows " + Math.Max(0, CountRecords(buf, n) - 1);
            int lines = 0;
            for (int i = 0; i < n; i++) if (buf[i] == (byte)'\n') lines++;
            if (lines < 2) return "rows ?";
            long est = (long)(size / ((double)n / lines)) - 1;
            return "rows≈" + Math.Max(0, est);
        }
        catch (IOException) { return "rows ?"; }
        catch (UnauthorizedAccessException) { return "rows ?"; }
    }

    /// <summary>CSV records including the header: line breaks inside quoted cells do not count, nor do blank lines; a last record without a trailing newline does.</summary>
    internal static int CountRecords(byte[] buf, int n)
    {
        int records = 0;
        bool inQuotes = false, pending = false;
        for (int i = 0; i < n; i++)
        {
            var b = buf[i];
            if (b == (byte)'"') { inQuotes = !inQuotes; pending = true; }
            else if (b == (byte)'\n' && !inQuotes) { if (pending) records++; pending = false; }
            else if (b != (byte)'\r' || inQuotes) pending = true;
        }
        return pending ? records + 1 : records;
    }
}
