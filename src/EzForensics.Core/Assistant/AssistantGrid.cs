using EzForensics.Core.Results;

namespace EzForensics.Core.Assistant;

/// <summary>One grid row: its source row id in the CSV and its cell values.</summary>
public sealed record GridSampleRow(long Source, IReadOnlyList<string> Cells);

/// <summary>What the examiner currently has open in the results grid.</summary>
public sealed record GridContext(string? CsvPath, IReadOnlyList<string> Headers, long RowCount, long FilteredCount,
    string FilterText, IReadOnlyList<GridSampleRow> SampleRows, IReadOnlyList<GridSampleRow>? SelectedRows = null)
{
    /// <summary>
    /// What the examiner is looking at, as compact text sent with each chat message (so the model never has to ask).
    /// Columns that are empty in every listed row are left out to keep it small. <paramref name="flags"/> (id, label) are listed so
    /// the model can name a flag without asking.
    /// </summary>
    public string Describe(int maxSampleRows = 10, IReadOnlyList<(string Id, string Label)>? flags = null)
    {
        if (CsvPath == null) return "No CSV is open in the results table.";
        var sb = new System.Text.StringBuilder();
        sb.Append("File: ").AppendLine(Path.GetFileName(CsvPath));
        sb.Append("Rows: ").Append(FilteredCount).Append(" shown of ").Append(RowCount).AppendLine();
        sb.Append("Filter: ").AppendLine(string.IsNullOrWhiteSpace(FilterText) ? "(none)" : FilterText);
        var selected = SelectedRows ?? Array.Empty<GridSampleRow>();
        var top = SampleRows.Take(maxSampleRows).ToList();
        var listed = selected.Concat(top).ToList();
        var cols = Enumerable.Range(0, Headers.Count).Where(i => listed.Any(r => i < r.Cells.Count && !string.IsNullOrWhiteSpace(r.Cells[i]))).ToList();
        if (cols.Count == 0) cols = Enumerable.Range(0, Headers.Count).ToList();
        sb.Append("Columns: ").AppendLine(string.Join(", ", Headers));
        if (flags is { Count: > 0 }) sb.Append("Flags (id = label): ").AppendLine(string.Join(", ", flags.Select(f => $"{f.Id} = {f.Label}")));
        void Rows(string title, IReadOnlyList<GridSampleRow> rows)
        {
            if (rows.Count == 0) return;
            sb.AppendLine(title + " (row | " + string.Join(" | ", cols.Select(i => Headers[i])) + "):");
            foreach (var r in rows)
                sb.Append(AssistantGrid.Shown(r.Source)).Append(" | ").AppendLine(string.Join(" | ", cols.Select(i => i < r.Cells.Count ? r.Cells[i].ReplaceLineEndings(" ") : "")));
        }
        Rows("Selected rows", selected);
        Rows(selected.Count > 0 ? "Top rows" : "Top rows (nothing selected)", top);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Validation of assistant-proposed grid actions. Nothing here applies anything.</summary>
public static class AssistantGrid
{
    /// <summary>Largest number of rows one flag proposal may name.</summary>
    public const int MaxFlagRows = 1000;

    /// <summary>The grid filter syntax in one line, with examples; shown in the tool description and after a syntax error so the model can retry.</summary>
    public const string FilterSyntaxHelp =
        "Syntax: column names in [brackets], every value in 'single quotes'. Examples: [EventId] = '4688' | " +
        "[EventId] In ('4624', '4625') | Contains([ExecutableInfo], 'powershell') | [EventId] = '4688' And Not Contains([Computer], 'DC'). " +
        "Operators: = <> > < >= <= Like In Between; functions Contains, StartsWith, EndsWith, Regex; And, Or, Not, parentheses.";

    /// <summary>Parses filter text; rejects syntax errors and columns that are not in <paramref name="headers"/>.</summary>
    public static (FilterGroup? Group, string? Error) ParseFilter(string? text, IReadOnlyList<string> headers)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, "The filter is empty. " + FilterSyntaxHelp);
        if (!FilterSyntax.TryParse(text, out var node, out var error) || node == null)
            return (null, (error ?? "The filter could not be parsed").TrimEnd('.') + ". " + FilterSyntaxHelp);
        var group = node as FilterGroup ?? FilterGroup.And(node);
        return MissingColumns(group, headers) is { } err ? (null, err) : (group, null);
    }

    /// <summary>Null when every column the filter names is in <paramref name="headers"/>; else an error naming the unknown ones.</summary>
    public static string? MissingColumns(FilterGroup group, IReadOnlyList<string> headers)
    {
        var known = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        var missing = Columns(group).Where(c => c != FilterNode.AnyColumn && !known.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return missing.Count > 0 ? "Unknown column(s) in the open CSV: " + string.Join(", ", missing) + ". Columns: " + string.Join(", ", headers) : null;
    }

    /// <summary>The exact header named by <paramref name="name"/> (case-insensitive), or null with an error listing the valid columns.</summary>
    public static string? ResolveColumn(string? name, IReadOnlyList<string> headers, out string? error)
    {
        var hit = string.IsNullOrWhiteSpace(name) ? null : headers.FirstOrDefault(h => string.Equals(h, name.Trim(), StringComparison.OrdinalIgnoreCase));
        error = hit == null ? $"Unknown column '{name}'. Valid: {string.Join(", ", headers)}" : null;
        return hit;
    }

    /// <summary>Null when <paramref name="flagId"/> is one of the user's configured flag ids.</summary>
    public static string? ValidateFlag(string? flagId, IEnumerable<string> knownIds) =>
        flagId != null && knownIds.Contains(flagId, StringComparer.Ordinal) ? null : $"Unknown flag '{flagId}'.";

    /// <summary>
    /// The configured flag id meant by <paramref name="name"/>: an id, or a label, ignoring case ("suspicious", "Suspicious"); a label
    /// may also be named by its first word ("benign" for "Benign / Ruled out") when only one label starts with it. Null when none.
    /// </summary>
    public static string? ResolveFlag(string? name, IReadOnlyList<(string Id, string Label)> flags)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        foreach (var cmp in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            if (flags.FirstOrDefault(f => string.Equals(f.Id, n, cmp)) is { Id: { } id }) return id;
        if (flags.FirstOrDefault(f => string.Equals(f.Label.Trim(), n, StringComparison.OrdinalIgnoreCase)) is { Id: { } byLabel }) return byLabel;
        var starts = flags.Where(f => f.Label.TrimStart().StartsWith(n, StringComparison.OrdinalIgnoreCase)).ToList();
        return starts.Count == 1 ? starts[0].Id : null;
    }

    /// <summary>The row number the examiner sees in the grid (1-based) for a source row id (0-based). The model only ever sees these.</summary>
    public static long Shown(long source) => source + 1;

    /// <summary>
    /// Turns row numbers as the examiner sees them (1..RowCount) into source row ids; null error when all are valid.
    /// </summary>
    public static string? ToSourceRows(IReadOnlyCollection<long> shown, long rowCount, out List<long> sources)
    {
        sources = shown.Select(r => r - 1).ToList();
        if (shown.Count == 0) return "No rows given.";
        if (shown.Count > MaxFlagRows) return $"Too many rows (max {MaxFlagRows}).";
        var bad = shown.Where(r => r < 1 || r > rowCount).Take(5).ToList();
        return bad.Count > 0 ? $"Row number(s) out of range (valid 1..{rowCount}): {string.Join(", ", bad)}." : null;
    }

    /// <summary>Null when every row id is a valid source row (0..RowCount-1) of the open CSV and the count is within limits.</summary>
    public static string? ValidateRows(IReadOnlyCollection<long> rows, long rowCount)
    {
        if (rows.Count == 0) return "No rows given.";
        if (rows.Count > MaxFlagRows) return $"Too many rows (max {MaxFlagRows}).";
        var bad = rows.Where(r => r < 0 || r >= rowCount).Take(5).ToList();
        return bad.Count > 0 ? $"Row id(s) out of range (valid 0..{rowCount - 1}): {string.Join(", ", bad)}." : null;
    }

    private static IEnumerable<string> Columns(FilterNode n) => n switch
    {
        FilterGroup g => g.Children.SelectMany(Columns),
        FilterCondition c => new[] { c.Column },
        FilterValueSet v => new[] { v.Column },
        FilterDateSet d => new[] { d.Column },
        _ => Array.Empty<string>(),
    };
}
