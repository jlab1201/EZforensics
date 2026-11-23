using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EzForensics.Core.Case;
using EzForensics.Core.Results;

namespace EzForensics.Core.Assistant;

/// <summary>
/// Tools over every CSV of the open case, so the assistant can look things up on demand instead of being handed whole files.
/// Files are named by case-relative path (as <c>case_list_files</c> prints them); row ids are the source row numbers the
/// Analyze grid uses (0-based, same as <see cref="CsvIndex"/>). Read-only; every output is capped because local models have small contexts.
/// <c>analyze_open_file</c> is a proposal: the examiner decides whether Analyze opens it.
/// </summary>
/// <summary>A case CSV resolved for a grid action: full path, row count, and the matching source row ids when a filter was given.</summary>
public sealed record CaseFileRows(string Path, long RowCount, IReadOnlyList<long>? Matches);

public sealed class AssistantCaseTools : IDisposable, ICaseFiles
{
    public const string ListFiles = "case_list_files";
    public const string Search = "case_search";
    public const string Query = "csv_query";
    public const string ColumnValues = "csv_column_values";
    public const string GroupBy = "csv_group_by";
    public const string GetRows = "csv_get_rows";
    public const string OpenFile = "analyze_open_file";

    public const int MaxListed = 200;
    public const int MaxSearchFiles = 25;
    public const int MaxQueryRows = 100;
    public const int DefaultQueryRows = 20;
    public const int MaxGetRows = 50;
    public const int CellChars = 200;
    public const int MaxOutputChars = 8000;
    public const int MaxOpenIndexes = 3;
    private const int MaxEnumerated = 20_000;
    private const long MaxValueScanRows = 500_000;
    private const int MaxDistinct = 50_000;
    public const int MaxGroupShowColumns = 4;
    public const int MaxGroupValues = 3;

    private readonly Func<string?> _caseDir;
    private readonly IAssistantHost _host;
    private readonly bool _canOpenFile;
    private readonly Func<string?>? _openFile;
    private readonly List<CsvIndex> _open = new(); // most recently used last
    private readonly object _lock = new();
    private bool _disposed;

    /// <param name="caseDir">The open case folder (null = none); read on every use.</param>
    /// <param name="host">Receives status lines and the open-file proposal.</param>
    /// <param name="canOpenFile">Offer <c>analyze_open_file</c> (only where Analyze can apply it).</param>
    /// <param name="openFile">Full path of the CSV open on screen (null = none); the csv_* tools read it when "file" is left out.</param>
    public AssistantCaseTools(Func<string?> caseDir, IAssistantHost host, bool canOpenFile = false, Func<string?>? openFile = null)
    {
        _caseDir = caseDir; _host = host; _canOpenFile = canOpenFile; _openFile = openFile;
        var tools = new List<IAssistantTool>
        {
            new Tool(ListFiles, "List the CSV files of the open case: relative path, host, tool/artifact, size. Optional host and name filter.", ToolKind.Read,
                new JsonObject
                {
                    ["host"] = Str("Only this host."),
                    ["contains"] = Str("Only files whose path contains this text."),
                }, ListFilesAsync),
            new Tool(Search, "Find which case CSV files mention a keyword (plain text, case-insensitive) and how many lines match. Does not show rows; use csv_query on a file next.", ToolKind.Read,
                new JsonObject
                {
                    ["query"] = Str("Keyword."),
                    ["files"] = StrArray("Only these files (relative paths)."),
                    ["host"] = Str("Only this host."),
                }, SearchAsync, "query"),
            new Tool(Query, "Show rows of one case CSV that match a filter (table filter syntax, e.g. [EventId] = '4688' And Contains([Payload], 'powershell')), with the total match count. Keep limit small.", ToolKind.Read,
                new JsonObject
                {
                    ["file"] = Str(OpenFileDefault),
                    ["filter"] = Str("Filter text. Omit for all rows."),
                    ["columns"] = StrArray("Columns to show. Omit for all."),
                    ["limit"] = Int($"Rows to return (default {DefaultQueryRows}, max {MaxQueryRows})."),
                    ["offset"] = Int("Skip this many matching rows."),
                }, QueryAsync),
            new Tool(ColumnValues, "Most common values of one column in a case CSV, with counts, optionally only among rows matching a filter.", ToolKind.Read,
                new JsonObject
                {
                    ["file"] = Str(OpenFileDefault),
                    ["column"] = Str("Column name."),
                    ["filter"] = Str("Filter text."),
                    ["top"] = Int("How many values (default 15, max 50)."),
                }, ColumnValuesAsync, "column"),
            new Tool(GroupBy, "Group the rows of a case CSV by one column (e.g. PID): each group's row count and a few values of other columns, optionally only among rows matching a filter.", ToolKind.Read,
                new JsonObject
                {
                    ["file"] = Str(OpenFileDefault),
                    ["column"] = Str("Column to group by."),
                    ["show_columns"] = StrArray($"Other columns to show per group (up to {MaxGroupShowColumns}; first {MaxGroupValues} distinct values each)."),
                    ["filter"] = Str("Filter text."),
                    ["top"] = Int("How many groups, largest first (default 20, max 50)."),
                }, GroupByAsync, "column"),
            new Tool(GetRows, "Show specific rows of a case CSV by row number (max 50).", ToolKind.Read,
                new JsonObject
                {
                    ["file"] = Str(OpenFileDefault),
                    ["rows"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } },
                }, GetRowsAsync, "rows"),
        };
        if (canOpenFile)
            tools.Add(new Tool(OpenFile, "Propose that the examiner opens another case CSV in Analyze, optionally filtered (table filter syntax). The examiner decides; nothing opens until they click.", ToolKind.Proposal,
                new JsonObject
                {
                    ["file"] = Str("Relative path from case_list_files."),
                    ["filter"] = Str("Optional filter text."),
                    ["reason"] = Str("Why the examiner should look at it."),
                }, OpenFileAsync, "file", "reason"));
        Tools = tools;
    }

    public IReadOnlyList<IAssistantTool> Tools { get; }

    private const string OpenFileDefault = "Relative path from case_list_files. Omit for the file open on screen (the default).";

    /// <summary>The "file" argument, or the open file (case-relative) when the model left it out.</summary>
    private string? FileArg(ToolCall call)
    {
        var file = Text(call, "file");
        if (!string.IsNullOrWhiteSpace(file)) return file;
        var dir = _caseDir();
        return dir != null && _openFile?.Invoke() is { } open && RelativeTo(dir, open) is { } rel ? rel : file;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var i in _open) i.Dispose();
            _open.Clear(); _openWrite.Clear();
        }
    }

    /// <summary>How many CSV files the case holds (capped), for the app-state line sent with each message.</summary>
    public static int CountCsvFiles(string? caseDir) => caseDir == null || !Directory.Exists(caseDir) ? 0 : EnumerateCsv(caseDir).Count;

    // ------------------------------------------------------------------ paths

    private static List<string> EnumerateCsv(string caseDir)
    {
        var o = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(caseDir, "*.csv", o).Take(MaxEnumerated).ToList();
    }

    /// <summary>Path of <paramref name="full"/> relative to the case folder; null when it is not inside it.</summary>
    public static string? RelativeTo(string caseDir, string full)
    {
        try
        {
            var root = Path.GetFullPath(caseDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var f = Path.GetFullPath(full);
            return f.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? f[root.Length..] : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string Rel(string caseDir, string full) => Path.GetRelativePath(caseDir, full);

    /// <summary>Resolves a case-relative CSV path. Rejects absolute paths, <c>..</c>, anything outside the case and non-CSV files.</summary>
    public static (string? Full, string? Error) ResolveFile(string? caseDir, string? file)
    {
        if (string.IsNullOrEmpty(caseDir) || !Directory.Exists(caseDir)) return (null, "No case is open.");
        if (string.IsNullOrWhiteSpace(file)) return (null, "file is required (a relative path from case_list_files).");
        file = file.Trim();
        if (file.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return (null, "Invalid file path.");
        if (Path.IsPathRooted(file) || file.Split('/', '\\').Contains("..")) return (null, "Use a path inside the case, relative to the case folder (see case_list_files).");
        string root, full;
        try
        {
            root = Path.GetFullPath(caseDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            full = Path.GetFullPath(Path.Combine(root, file));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return (null, "Invalid file path."); }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return (null, "That path is outside the case.");
        if (!full.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return (null, "Only .csv files can be queried.");
        if (File.Exists(full)) return (full, null);
        // small models often give just the file name the view shows: accept it when exactly one case file has that name
        if (file.IndexOfAny(new[] { '/', '\\' }) < 0)
        {
            var hits = EnumerateCsv(caseDir).Where(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return (hits[0], null);
            if (hits.Count > 1) return (null, $"Several case files are named {file}: " + string.Join(", ", hits.Take(5).Select(h => Rel(caseDir, h))) + ". Give the relative path.");
        }
        return (null, $"No such file in the case: {file}. Use case_list_files to see the names.");
    }

    /// <summary>
    /// A case CSV for a grid action on a file that may not be open: its full path, row count and, when <paramref name="filter"/> is
    /// given, the source row ids that match it. <paramref name="file"/> is case-relative, a file name, or a full path inside the case
    /// (the open grid file).
    /// </summary>
    public async Task<(CaseFileRows? Rows, string? Error)> ResolveRowsAsync(string file, string? filter, CancellationToken ct)
    {
        var dir = _caseDir();
        if (Path.IsPathRooted(file))
        {
            if (dir == null || RelativeTo(dir, file) is not { } rel) return (null, "That file is not in the open case.");
            file = rel;
        }
        var (idx, err) = await OpenIndexAsync(file, ct, remember: false).ConfigureAwait(false);
        if (idx == null) return (null, err);
        if (string.IsNullOrWhiteSpace(filter)) return (new CaseFileRows(idx.Path, idx.RowCount, null), null);
        var (matches, ferr) = await MatchAsync(idx, filter, ct).ConfigureAwait(false);
        if (ferr != null) return (null, ferr);
        return (new CaseFileRows(idx.Path, idx.RowCount, (matches ?? Array.Empty<int>()).Select(i => (long)i).ToList()), null);
    }

    // ------------------------------------------------------------------ list / search

    private Task<ToolOutcome> ListFilesAsync(ToolCall call, CancellationToken ct)
    {
        var dir = _caseDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return Done("No case is open.", true);
        var host = Text(call, "host"); var contains = Text(call, "contains");
        var index = CaseIndex.Load(dir);
        var rows = new List<(string Rel, string Host, string Kind, long Size)>();
        foreach (var f in EnumerateCsv(dir))
        {
            ct.ThrowIfCancellationRequested();
            var p = index.Describe(f);
            var rel = Rel(dir, f);
            if (host != null && !string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
            if (contains != null && rel.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            long size = 0; try { size = new FileInfo(f).Length; } catch (IOException) { }
            rows.Add((rel, p.Host ?? (p.IsCaseWide ? "(case-wide)" : "-"), KindOf(p), size));
        }
        rows.Sort((a, b) => string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase));
        var sb = new StringBuilder();
        sb.Append(rows.Count).Append(" CSV file(s)");
        if (index.Hosts.Count > 0) sb.Append("; hosts: ").Append(string.Join(", ", index.Hosts.Take(20)));
        sb.AppendLine(". Format: path | host | tool/artifact | size");
        foreach (var r in rows.Take(MaxListed)) sb.Append(r.Rel).Append(" | ").Append(r.Host).Append(" | ").Append(r.Kind).Append(" | ").AppendLine(Size(r.Size));
        if (rows.Count > MaxListed) sb.Append("+").Append(rows.Count - MaxListed).AppendLine(" more; narrow with host or contains.");
        return Done(Cap(sb), false);
    }

    private static string KindOf(CsvProvenance p) =>
        string.Join("/", new[] { p.ToolId, p.ArtifactId }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } k ? k : "-";

    private async Task<ToolOutcome> SearchAsync(ToolCall call, CancellationToken ct)
    {
        var dir = _caseDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new("No case is open.", true);
        var query = Text(call, "query");
        if (query == null) return new("query is required.", true);
        List<string> files;
        if (call.Input["files"] is JsonArray arr && arr.Count > 0)
        {
            files = new();
            foreach (var n in arr)
            {
                var (full, err) = ResolveFile(dir, n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null);
                if (full == null) return new(err!, true);
                files.Add(full);
            }
        }
        else
        {
            files = EnumerateCsv(dir);
            if (Text(call, "host") is { } host)
            {
                var index = CaseIndex.Load(dir);
                files = files.Where(f => string.Equals(index.Describe(f).Host, host, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
        _host.Status($"Searching {files.Count} file(s) for '{Clip(query, 40)}'…");
        var hits = await CsvKeywordSearch.SearchToListAsync(files, query, null, ct);
        var sb = new StringBuilder();
        sb.Append("'").Append(Clip(query, 80)).Append("' appears in ").Append(hits.Count).Append(" of ").Append(files.Count).AppendLine(" file(s). Format: path | matching rows | first matching row (csv_get_rows number)");
        foreach (var h in hits.Take(MaxSearchFiles))
            sb.Append(Rel(dir, h.Path)).Append(" | ").Append(h.HitCount).Append(h.CountCapped ? "+" : "")
              .Append(" | ").AppendLine(h.FileNameMatch && h.HitCount == 0 ? "file name matches" : h.FirstHitRow.ToString());
        if (hits.Count > MaxSearchFiles) sb.Append("+").Append(hits.Count - MaxSearchFiles).AppendLine(" more files.");
        if (hits.Count > 0) sb.AppendLine("Next: csv_query a file with Contains([Column], 'text') to see rows.");
        return new(Cap(sb), false);
    }

    // ------------------------------------------------------------------ csv_query / values / rows

    private async Task<ToolOutcome> QueryAsync(ToolCall call, CancellationToken ct)
    {
        var (idx, err) = await OpenIndexAsync(FileArg(call), ct);
        if (idx == null) return new(err!, true);
        var (matches, ferr) = await MatchAsync(idx, Text(call, "filter"), ct);
        if (ferr != null) return new(ferr, true);

        var cols = new List<int>();
        if (call.Input["columns"] is JsonArray ca && ca.Count > 0)
        {
            foreach (var n in ca)
            {
                var name = n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                var ci = name == null ? -1 : idx.ColumnIndex(name);
                if (ci < 0) return new($"Unknown column '{name}'. Valid columns: {string.Join(", ", idx.Headers)}", true);
                cols.Add(ci);
            }
        }
        else cols.AddRange(Enumerable.Range(0, idx.Headers.Count));

        var limit = Math.Clamp(Number(call, "limit") ?? DefaultQueryRows, 1, MaxQueryRows);
        var offset = Math.Max(0, Number(call, "offset") ?? 0);
        long total = matches?.Length ?? idx.RowCount;
        var sb = new StringBuilder();
        sb.Append(Rel(_caseDir()!, idx.Path)).Append(": ").Append(total).Append(" of ").Append(idx.RowCount).AppendLine(" rows match.");
        sb.AppendLine("row | " + string.Join(" | ", cols.Select(c => idx.Headers[c])));
        int shown = 0;
        for (long i = offset; i < total && shown < limit; i++)
        {
            ct.ThrowIfCancellationRequested();
            long id = matches != null ? matches[i] : i;
            int before = sb.Length;
            AppendRow(sb, id, idx.ReadRow(id), cols);
            if (shown > 0 && sb.Length > MaxOutputChars - FooterReserve) { sb.Length = before; break; }
            shown++;
        }
        if (shown == 0) sb.AppendLine("(no rows in this range)");
        else if (offset + shown < total) sb.Append("Showing ").Append(shown).Append(" from offset ").Append(offset).Append("; ").Append(total - offset - shown).AppendLine(" more (use offset).");
        return new(Cap(sb), false);
    }

    private async Task<ToolOutcome> ColumnValuesAsync(ToolCall call, CancellationToken ct)
    {
        var (idx, err) = await OpenIndexAsync(FileArg(call), ct);
        if (idx == null) return new(err!, true);
        var column = Text(call, "column");
        var ci = column == null ? -1 : idx.ColumnIndex(column);
        if (ci < 0) return new($"Unknown column '{column}'. Valid columns: {string.Join(", ", idx.Headers)}", true);
        var (matches, ferr) = await MatchAsync(idx, Text(call, "filter"), ct);
        if (ferr != null) return new(ferr, true);

        var top = Math.Clamp(Number(call, "top") ?? 15, 1, 50);
        long total = matches?.Length ?? idx.RowCount;
        long scan = Math.Min(total, MaxValueScanRows);
        var counts = new TopCounter();
        Scan(idx, matches, scan, row => counts.Get(ci < row.Length ? row[ci] : "", () => new Group()).Rows++, ct);
        var sb = new StringBuilder();
        sb.Append(idx.Headers[ci]).Append(" in ").Append(Rel(_caseDir()!, idx.Path)).Append(": ");
        if (counts.Overflowed) sb.Append("more than ").Append(MaxDistinct.ToString("N0", CultureInfo.InvariantCulture)).Append(" distinct values; counts are approximate (≈) over ").Append(scan).Append(" row(s)");
        else sb.Append(counts.Main.Count).Append(" distinct value(s) over ").Append(scan).Append(" row(s)");
        if (scan < total) sb.Append(" (first ").Append(scan).Append(" of ").Append(total).Append(" matching rows)");
        sb.AppendLine(".");
        foreach (var kv in counts.All().OrderByDescending(k => k.Value.Rows).ThenBy(k => k.Key, StringComparer.Ordinal).Take(top))
            sb.Append(counts.Overflowed ? "≈" : "").Append(kv.Value.Rows).Append(" x ").AppendLine(kv.Key.Length == 0 ? "(empty)" : Clip(kv.Key.ReplaceLineEndings(" "), 120));
        return new(Cap(sb), false);
    }

    /// <summary>Calls <paramref name="each"/> for the first <paramref name="scan"/> rows (or matching rows), read in chunks.</summary>
    private static void Scan(CsvIndex idx, int[]? matches, long scan, Action<string[]> each, CancellationToken ct)
    {
        const int Chunk = 2000;
        for (long pos = 0; pos < scan; pos += Chunk)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(Chunk, scan - pos);
            if (matches == null)
            {
                foreach (var row in idx.ReadRows(pos, n)) each(row);
            }
            else
                for (int k = 0; k < n; k++) each(idx.ReadRow(matches[pos + k]));
        }
    }

    private sealed class Group
    {
        public int Rows;
        public long FirstRow = -1;
        public List<string>[] Values = Array.Empty<List<string>>();
    }

    private async Task<ToolOutcome> GroupByAsync(ToolCall call, CancellationToken ct)
    {
        var (idx, err) = await OpenIndexAsync(FileArg(call), ct);
        if (idx == null) return new(err!, true);
        var column = Text(call, "column");
        var ci = column == null ? -1 : idx.ColumnIndex(column);
        if (ci < 0) return new($"Unknown column '{column}'. Valid columns: {string.Join(", ", idx.Headers)}", true);
        var show = new List<int>();
        if (call.Input["show_columns"] is JsonArray sa)
            foreach (var n in sa)
            {
                var name = n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                var si = name == null ? -1 : idx.ColumnIndex(name);
                if (si < 0) return new($"Unknown column '{name}'. Valid columns: {string.Join(", ", idx.Headers)}", true);
                if (si != ci && !show.Contains(si) && show.Count < MaxGroupShowColumns) show.Add(si);
            }
        var (matches, ferr) = await MatchAsync(idx, Text(call, "filter"), ct);
        if (ferr != null) return new(ferr, true);

        var top = Math.Clamp(Number(call, "top") ?? 20, 1, 50);
        long total = matches?.Length ?? idx.RowCount;
        long scan = Math.Min(total, MaxValueScanRows);
        var groups = new TopCounter();
        long seen = 0;
        Scan(idx, matches, scan, row =>
        {
            long id = matches != null ? matches[seen] : seen;
            seen++;
            var key = ci < row.Length ? row[ci] : "";
            var g = groups.Get(key, () => new Group { FirstRow = id, Values = show.Select(_ => new List<string>()).ToArray() });
            g.Rows++;
            for (int k = 0; k < show.Count; k++)
            {
                var v = show[k] < row.Length ? row[show[k]] : "";
                var list = g.Values[k];
                if (list.Count < MaxGroupValues + 1 && !list.Contains(v, StringComparer.Ordinal)) list.Add(v);
            }
        }, ct);

        var sb = new StringBuilder();
        sb.Append(Rel(_caseDir()!, idx.Path)).Append(" grouped by ").Append(idx.Headers[ci]).Append(": ");
        if (groups.Overflowed) sb.Append("more than ").Append(MaxDistinct.ToString("N0", CultureInfo.InvariantCulture)).Append(" groups; counts are approximate (≈) over ").Append(scan).Append(" row(s)");
        else sb.Append(groups.Main.Count).Append(" group(s) over ").Append(scan).Append(" row(s)");
        if (scan < total) sb.Append(" (first ").Append(scan).Append(" of ").Append(total).Append(" matching rows)");
        sb.AppendLine(". Largest first.");
        sb.Append(idx.Headers[ci]).Append(" | rows | first row");
        foreach (var k in show) sb.Append(" | ").Append(idx.Headers[k]);
        sb.AppendLine();
        int groupsShown = 0;
        foreach (var kv in groups.All().OrderByDescending(k => k.Value.Rows).ThenBy(k => k.Value.FirstRow).Take(top))
        {
            int before = sb.Length;
            sb.Append(kv.Key.Length == 0 ? "(empty)" : Clip(kv.Key.ReplaceLineEndings(" "), 80)).Append(" | ").Append(groups.Overflowed ? "≈" : "").Append(kv.Value.Rows).Append(" | ").Append(kv.Value.FirstRow + 1);
            foreach (var list in kv.Value.Values)
                sb.Append(" | ").Append(string.Join(", ", list.Take(MaxGroupValues).Select(v => v.Length == 0 ? "(empty)" : Clip(v.ReplaceLineEndings(" "), 60))))
                  .Append(list.Count > MaxGroupValues ? ", …" : "");
            sb.AppendLine();
            if (groupsShown > 0 && sb.Length > MaxOutputChars - FooterReserve) { sb.Length = before; break; }
            groupsShown++;
        }
        if (!groups.Overflowed && groups.Main.Count > groupsShown) sb.Append("+").Append(groups.Main.Count - groupsShown).AppendLine(" smaller groups not shown.");
        return new(Cap(sb), false);
    }

    /// <summary>
    /// Exact counts for the first <see cref="MaxDistinct"/> distinct keys. Later keys go to a second table of the
    /// same size that is periodically pruned (Misra-Gries), so frequent late values still surface. Once that
    /// table exists the counts are approximate (late values can be undercounted) and <see cref="Overflowed"/> is true.
    /// </summary>
    private sealed class TopCounter
    {
        public readonly Dictionary<string, Group> Main = new(StringComparer.Ordinal);
        private Dictionary<string, Group>? _extra;
        public bool Overflowed => _extra != null;

        public Group Get(string key, Func<Group> make)
        {
            if (Main.TryGetValue(key, out var g)) return g;
            if (Main.Count < MaxDistinct) return Main[key] = make();
            _extra ??= new Dictionary<string, Group>(StringComparer.Ordinal);
            if (_extra.TryGetValue(key, out g)) return g;
            if (_extra.Count >= MaxDistinct) Prune();
            return _extra[key] = make();
        }

        /// <summary>Subtracts the median count from every late entry and drops those that reach zero.</summary>
        private void Prune()
        {
            var rows = _extra!.Values.Select(v => v.Rows).OrderBy(r => r).ToArray();
            int m = Math.Max(1, rows[rows.Length / 2]);
            foreach (var key in _extra.Keys.ToList())
            {
                var g = _extra[key];
                g.Rows -= m;
                if (g.Rows <= 0) _extra.Remove(key);
            }
        }

        public IEnumerable<KeyValuePair<string, Group>> All() => _extra == null ? Main : Main.Concat(_extra);
    }

    private async Task<ToolOutcome> GetRowsAsync(ToolCall call, CancellationToken ct)
    {
        var (idx, err) = await OpenIndexAsync(FileArg(call), ct);
        if (idx == null) return new(err!, true);
        if (call.Input["rows"] is not JsonArray arr || arr.Count == 0) return new("rows must be a non-empty array of row numbers.", true);
        if (arr.Count > MaxGetRows) return new($"Too many rows (max {MaxGetRows}).", true);
        var shown = new List<long>();
        foreach (var n in arr)
        {
            if (n is JsonValue v && ToLong(v) is { } l) shown.Add(l); else return new("rows must be integers.", true);
        }
        if (AssistantGrid.ToSourceRows(shown, idx.RowCount, out var ids) is { } rowErr) return new(rowErr, true);
        var cols = Enumerable.Range(0, idx.Headers.Count).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("row | " + string.Join(" | ", idx.Headers));
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            AppendRow(sb, id, idx.ReadRow(id), cols);
            if (sb.Length > MaxOutputChars) break;
        }
        return new(Cap(sb), false);
    }

    // ------------------------------------------------------------------ analyze_open_file

    private async Task<ToolOutcome> OpenFileAsync(ToolCall call, CancellationToken ct)
    {
        var dir = _caseDir();
        var (full, err) = ResolveFile(dir, Text(call, "file"));
        if (full == null) return new("Proposal rejected: " + err, true);
        FilterGroup? group = null;
        if (Text(call, "filter") is { } text)
        {
            string[] headers;
            try { headers = await Task.Run(() => CsvIndex.ReadHeaderNames(full), ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new("Proposal rejected: could not read the file's header.", true); }
            (group, var ferr) = AssistantGrid.ParseFilter(text, headers);
            if (group == null) return new($"Proposal rejected: {ferr} Valid columns: {string.Join(", ", headers)}", true);
        }
        var reason = Text(call, "reason") ?? "";
        var result = await _host.HandleProposalAsync(new OpenFileProposal(call.Id, full, group, group?.ToDisplayString() ?? "", reason)).ConfigureAwait(false);
        return new(result, false);
    }

    // ------------------------------------------------------------------ indexes

    /// <summary>Row indices (source row ids) that match <paramref name="filter"/>; null = no filter, every row.</summary>
    private async Task<(int[]? Matches, string? Error)> MatchAsync(CsvIndex idx, string? filter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filter)) return (null, null);
        var (group, err) = AssistantGrid.ParseFilter(filter, idx.Headers);
        if (group == null) return (null, $"Filter rejected: {err} Valid columns: {string.Join(", ", idx.Headers)}");
        _host.Status($"Filtering {Path.GetFileName(idx.Path)}…");
        return (await FilterEngine.FilterAsync(idx, new FilterSpec { Expression = group }, null, ct), null);
    }

    /// <summary>
    /// Full path of the case CSV the model last read rows from (csv_query, csv_get_rows, csv_column_values, csv_group_by); null when none, or
    /// after <see cref="ForgetLastQueried"/>. Lets the flag tools catch "flag them" sent without the file the rows came from.
    /// </summary>
    public string? LastQueriedFile { get; private set; }

    /// <summary>Called once a flag proposal named its file, so later asks about the open file are not held up.</summary>
    public void ForgetLastQueried() => LastQueriedFile = null;

    /// <summary>Last-write time of each cached index's file when it was opened (length alone misses same-size rewrites).</summary>
    private readonly Dictionary<CsvIndex, DateTime> _openWrite = new();

    /// <summary>The fully indexed CSV, from the small LRU cache (re-opened when the file changed).</summary>
    private async Task<(CsvIndex? Index, string? Error)> OpenIndexAsync(string? file, CancellationToken ct, bool remember = true)
    {
        var (full, err) = ResolveFile(_caseDir(), file);
        if (full == null) return (null, err);
        if (remember) LastQueriedFile = full;
        lock (_lock)
        {
            if (_disposed) return (null, "Spectre was closed.");
            var hit = _open.FirstOrDefault(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                bool stale;
                try { stale = new FileInfo(full) is var fi && (fi.Length != hit.FileLength || !_openWrite.TryGetValue(hit, out var w) || fi.LastWriteTimeUtc != w); } catch (IOException) { stale = true; }
                if (!stale) { _open.Remove(hit); _open.Add(hit); return (hit, null); }
                _open.Remove(hit); _openWrite.Remove(hit); hit.Dispose();
            }
        }
        var name = Path.GetFileName(full);
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(full); } catch (IOException) { written = default; }
        _host.Status($"Indexing {name}…");
        CsvIndex idx;
        try
        {
            idx = await CsvIndex.OpenAsync(full, new RowProgress(_host, name), ct);
            try { await idx.IndexingCompleted.WaitAsync(ct); }
            catch { idx.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, $"Could not read {name}."); }
        lock (_lock)
        {
            if (_disposed) { idx.Dispose(); return (null, "Spectre was closed."); }
            _open.Add(idx);
            _openWrite.Remove(idx); _openWrite[idx] = written;
            while (_open.Count > MaxOpenIndexes) { _openWrite.Remove(_open[0]); _open[0].Dispose(); _open.RemoveAt(0); }
        }
        return (idx, null);
    }

    private sealed class RowProgress : IProgress<long>
    {
        private readonly IAssistantHost _host; private readonly string _name; private long _last;
        public RowProgress(IAssistantHost host, string name) { _host = host; _name = name; }
        public void Report(long rows)
        {
            if (rows - _last < 200_000) return;
            _last = rows;
            _host.Status($"Indexing {_name}… {rows:N0} rows");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void AppendRow(StringBuilder sb, long id, string[] row, List<int> cols)
    {
        sb.Append(AssistantGrid.Shown(id)).Append(" | ");
        sb.AppendLine(string.Join(" | ", cols.Select(c => c < row.Length ? Clip(row[c].ReplaceLineEndings(" "), CellChars) : "")));
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Cap(StringBuilder sb) =>
        sb.Length <= MaxOutputChars ? sb.ToString().TrimEnd() : sb.ToString(0, MaxOutputChars).TrimEnd() + $"\n(output cut: {sb.Length - MaxOutputChars:N0} more characters not shown; use a smaller limit or fewer columns)";

    /// <summary>Same cap for text from outside (engine tool output), with a note saying how much was cut.</summary>
    public static string CapText(string text) =>
        text.Length <= MaxOutputChars ? text : text[..MaxOutputChars].TrimEnd() + $"\n(output cut: {text.Length - MaxOutputChars:N0} more characters not shown)";

    /// <summary>Room kept free for the "Showing N... / +N smaller groups" footer so <see cref="Cap"/> cannot cut it off.</summary>
    private const int FooterReserve = 200;

    private static string Size(long b) => b switch
    {
        < 1024 => b + " B",
        < 1024 * 1024 => (b / 1024.0).ToString("0.#") + " KB",
        < 1024L * 1024 * 1024 => (b / 1048576.0).ToString("0.#") + " MB",
        _ => (b / 1073741824.0).ToString("0.#") + " GB",
    };

    private static Task<ToolOutcome> Done(string text, bool error) => Task.FromResult(new ToolOutcome(text, error));

    private static string? Text(ToolCall c, string key) =>
        c.Input[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static int? Number(ToolCall c, string key) =>
        c.Input[key] is JsonValue v && ToLong(v) is { } l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

    private static long? ToLong(JsonValue v)
    {
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d)) return (long)d;
        return null;
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
    private static JsonObject StrArray(string description) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    private sealed class Tool : IAssistantTool
    {
        private readonly Func<ToolCall, CancellationToken, Task<ToolOutcome>> _run;
        public Tool(string name, string description, ToolKind kind, JsonObject props, Func<ToolCall, CancellationToken, Task<ToolOutcome>> run, params string[] required)
        {
            Spec = AssistantGridTools.Spec(name, description, kind, props, required);
            _run = run;
        }
        public ToolSpec Spec { get; }
        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct) => _run(call, ct);
    }
}
