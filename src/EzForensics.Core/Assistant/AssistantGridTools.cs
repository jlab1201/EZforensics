using System.Text.Json.Nodes;

namespace EzForensics.Core.Assistant;

/// <summary>The results-grid tools: one read tool and proposal tools (the examiner applies them, or auto-mode does, through the host).</summary>
/// <summary>The case CSVs as the flag tools need them; implemented by <see cref="AssistantCaseTools"/>.</summary>
public interface ICaseFiles
{
    /// <summary>A case CSV (relative path, file name or full path inside the case) and, with a filter, its matching rows.</summary>
    Task<(CaseFileRows? Rows, string? Error)> ResolveRowsAsync(string file, string? filter, CancellationToken ct);
    /// <summary>Full path of the CSV the model last read rows from; null = none.</summary>
    string? LastQueriedFile { get; }
    void ForgetLastQueried();
}

public static class AssistantGridTools
{
    public const string GetContext = "grid_get_context";
    public const string ApplyFilter = "grid_apply_filter";
    public const string FlagRows = "grid_flag_rows";
    public const string ClearFilter = "grid_clear_filter";
    public const string UnflagRows = "grid_unflag_rows";
    public const string Sort = "grid_sort";
    public const string GoToRow = "grid_go_to_row";

    /// <summary>
    /// Ends every action result: small models follow the latest tool result more than the system prompt, so the "one line" rule
    /// for the reply is repeated here.
    /// </summary>
    public const string ReplyRule = " Now reply in ONE short sentence saying what was done, e.g. \"Filtered to EventId 4688 (4 rows).\" " +
        "No row lists, tables, headings or next steps: the examiner sees the table.";

    /// <summary>Returned by proposal tools so the model does not assume the change happened.</summary>
    public const string ProposedText = "Shown to the examiner as a card with an Apply button; NOT applied yet. Do not assume it was applied." + ReplyRule;

    /// <summary>Returned when auto-mode applied the proposal.</summary>
    public const string AppliedText = "Applied by auto-mode (the examiner can Undo it)." + ReplyRule;

    /// <param name="context">Current grid state (called on every use).</param>
    /// <param name="knownFlagIds">Ids of the flags the user has configured.</param>
    /// <param name="host">Receives proposals.</param>
    /// <param name="flagLabels">(id, label) of those flags, so the model may name a flag by its label; null = ids only.</param>
    /// <param name="caseFiles">Resolves another case CSV (file, optional filter) so rows can be flagged in a file that is not open, or
    /// by filter (usually the <see cref="AssistantCaseTools"/>); null = only row numbers of the open file.</param>
    public static IReadOnlyList<IAssistantTool> Create(Func<GridContext> context, Func<IReadOnlyList<string>> knownFlagIds, IAssistantHost host,
        Func<IReadOnlyList<(string Id, string Label)>>? flagLabels = null, ICaseFiles? caseFiles = null)
    {
        flagLabels ??= () => knownFlagIds().Select(id => (id, id)).ToList();
        return new IAssistantTool[]
        {
            new GetContextTool(context, knownFlagIds),
            new FilterTool(context, host),
            new FlagTool(context, flagLabels, host, caseFiles),
            new ClearFilterTool(context, host),
            new UnflagTool(context, host, caseFiles),
            new SortTool(context, host),
            new GoToRowTool(context, host),
        };
    }

    internal static ToolSpec Spec(string name, string description, ToolKind kind, JsonObject props, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        var req = new JsonArray(); foreach (var r in required) req.Add(r);
        schema["required"] = req;
        return new ToolSpec(name, description, schema, kind);
    }

    private sealed class GetContextTool : IAssistantTool
    {
        private readonly Func<GridContext> _ctx; private readonly Func<IReadOnlyList<string>> _flags;
        public GetContextTool(Func<GridContext> ctx, Func<IReadOnlyList<string>> flags) { _ctx = ctx; _flags = flags; }
        public ToolSpec Spec { get; } = AssistantGridTools.Spec(GetContext,
            "Describe the CSV open in the results table: file, column names, row counts, the active filter, flag ids and a sample of rows with their row ids.",
            ToolKind.Read, new JsonObject());

        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var c = _ctx();
            if (c.CsvPath == null) return Task.FromResult(new ToolOutcome("No CSV is open in the results table.", true));
            var o = new JsonObject
            {
                ["csv"] = c.CsvPath,
                ["columns"] = new JsonArray(c.Headers.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
                ["rowCount"] = c.RowCount,
                ["filteredCount"] = c.FilteredCount,
                ["activeFilter"] = c.FilterText,
                ["flagIds"] = new JsonArray(_flags().Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                ["sampleRows"] = new JsonArray(c.SampleRows.Select(r => (JsonNode?)new JsonObject
                {
                    ["row"] = AssistantGrid.Shown(r.Source),
                    ["cells"] = new JsonArray(r.Cells.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                }).ToArray()),
            };
            return Task.FromResult(new ToolOutcome(o.ToJsonString(), false));
        }
    }

    private sealed class FilterTool : IAssistantTool
    {
        private readonly Func<GridContext> _ctx; private readonly IAssistantHost _host;
        public FilterTool(Func<GridContext> ctx, IAssistantHost host) { _ctx = ctx; _host = host; }
        public ToolSpec Spec { get; } = AssistantGridTools.Spec(ApplyFilter,
            "Filter the rows of the CSV open in the table. Call it whenever the examiner asks to filter, show only or hide rows. " +
            "Use only columns of the open CSV. " + AssistantGrid.FilterSyntaxHelp,
            ToolKind.Proposal, new JsonObject { ["filter"] = new JsonObject { ["type"] = "string", ["description"] = "Filter text, e.g. [EventId] = '4688'" } }, "filter");

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var c = _ctx();
            if (c.CsvPath == null) return new ToolOutcome("No CSV is open in the results table.", true);
            var (group, error) = AssistantGrid.ParseFilter(Str(call.Input, "filter"), c.Headers);
            if (group == null) return new ToolOutcome("Filter rejected: " + error, true);
            return new ToolOutcome(await _host.HandleProposalAsync(new FilterProposal(call.Id, group, group.ToDisplayString(), c.CsvPath)).ConfigureAwait(false), false);
        }
    }

    /// <summary>
    /// The file and source rows a flag/unflag call names. <c>csv</c> may be omitted (the open file), the open file's name, or with
    /// <paramref name="caseFiles"/> any case CSV (relative path or file name). Rows are row numbers as the grid and the case tools
    /// show them (1-based), or a <c>filter</c> matching every row to change. Small models also send a single row as a number, and
    /// numbers as strings ("3"); both are accepted. Error is a text without prefix, or null.
    /// </summary>
    internal static async Task<(string? Csv, List<long> Rows, string? Error)> ResolveTargetAsync(JsonObject input, GridContext c, ICaseFiles? caseFiles, CancellationToken ct)
    {
        var csv = Str(input, "csv");
        var filter = Str(input, "filter");
        var hasRows = input["rows"] is JsonArray { Count: > 0 } or JsonValue;
        string target; long rowCount;
        if (string.IsNullOrWhiteSpace(csv) || SameFileName(csv, c.CsvPath))
        {
            if (c.CsvPath == null) return (null, new(), "no CSV is open in the results table; name the file in csv.");
            // "flag them" right after reading rows of another file: the row numbers are that file's, so make the model say which
            if (string.IsNullOrWhiteSpace(csv) && caseFiles?.LastQueriedFile is { } last && !SameFileName(last, c.CsvPath))
                return (null, new(), $"the rows you last read are in {Path.GetFileName(last)}, but {Path.GetFileName(c.CsvPath)} is open. " +
                    $"Call again with csv set to \"{Path.GetFileName(last)}\" (rows from that file) or \"{Path.GetFileName(c.CsvPath)}\" (rows of the open file).");
            target = c.CsvPath; rowCount = c.RowCount;
        }
        else
        {
            if (caseFiles == null) return (null, new(), $"rows are for {FileName(csv)} but {Path.GetFileName(c.CsvPath)} is open; use row numbers of the open CSV.");
            var (other, err) = await caseFiles.ResolveRowsAsync(csv.Trim(), null, ct).ConfigureAwait(false);
            if (other == null) return (null, new(), err);
            target = other.Path; rowCount = other.RowCount;
        }
        if (!hasRows && !string.IsNullOrWhiteSpace(filter))
        {
            if (caseFiles == null) return (null, new(), "flagging by filter is not available here; give rows.");
            var (hit, err) = await caseFiles.ResolveRowsAsync(target, filter, ct).ConfigureAwait(false);
            if (hit?.Matches == null) return (null, new(), err ?? "the filter could not be run.");
            if (hit.Matches.Count == 0) return (null, new(), "no rows match that filter.");
            if (hit.Matches.Count > AssistantGrid.MaxFlagRows) return (null, new(), $"{hit.Matches.Count:N0} rows match; the most is {AssistantGrid.MaxFlagRows}. Narrow the filter.");
            return (target, hit.Matches.ToList(), null);
        }
        var items = input["rows"] switch { JsonArray a => a.ToList(), JsonValue v => new List<JsonNode?> { v }, _ => null };
        if (items == null) return (null, new(), "give rows (row numbers, e.g. [3]) or a filter.");
        var shown = new List<long>();
        foreach (var n in items)
        {
            if (AsLong(n) is { } l) shown.Add(l);
            else return (null, new(), "rows must be row numbers (integers), e.g. [3].");
        }
        var rowErr = AssistantGrid.ToSourceRows(shown, rowCount, out var rows);
        return rowErr != null ? (null, new(), rowErr) : (target, rows, null);
    }

    /// <summary>An integer from a JSON number or a numeric string; null otherwise.</summary>
    public static long? AsLong(JsonNode? n)
    {
        if (n is not JsonValue jv) return null;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 1e15) return (long)d;
        if (jv.TryGetValue<string>(out var s) && long.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    private static string FileName(string path) => path.Trim().Replace('/', '\\').Split('\\').Last();

    private static bool SameFileName(string given, string? open) =>
        open != null && string.Equals(FileName(given), FileName(open), StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonObject input, string name) => input[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Input schema of the flag / unflag tools. Filter mode needs the case resolver.</summary>
    private static JsonObject RowProps(bool caseFiles, bool withFlag)
    {
        var p = new JsonObject
        {
            ["rows"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "Row numbers, e.g. [3, 7]." },
        };
        if (caseFiles) p["filter"] = new JsonObject { ["type"] = "string", ["description"] = "Instead of rows: every row matching this filter, e.g. [EventId] = '4688'." };
        if (withFlag) p["flag"] = new JsonObject { ["type"] = "string", ["description"] = "Flag id or label from the view's Flags line, e.g. suspicious." };
        p["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Short reason." };
        p["csv"] = new JsonObject { ["type"] = "string", ["description"] = caseFiles ? "File the rows are in (relative path or file name). Omit for the open file." : "Optional: the open file name." };
        return p;
    }

    private sealed class FlagTool : IAssistantTool
    {
        private readonly Func<GridContext> _ctx; private readonly Func<IReadOnlyList<(string Id, string Label)>> _flags; private readonly IAssistantHost _host;
        private readonly ICaseFiles? _files;
        public FlagTool(Func<GridContext> ctx, Func<IReadOnlyList<(string Id, string Label)>> flags, IAssistantHost host, ICaseFiles? files)
        {
            _ctx = ctx; _flags = flags; _host = host; _files = files;
            Spec = AssistantGridTools.Spec(FlagRows,
                "Flag rows. Call it whenever the examiner asks to flag or mark rows. Give rows (row numbers as the view or csv_query shows them)" +
                (files != null ? " or a filter (flags every matching row). csv = the file the rows are in (omit for the open file); it may be any case CSV, Apply opens it." : "."),
                ToolKind.Proposal, RowProps(files != null, withFlag: true), "flag");
        }
        public ToolSpec Spec { get; }

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            ToolOutcome Err(string m) => new("Flag proposal rejected: " + m, true);
            var (target, rows, rowErr) = await ResolveTargetAsync(call.Input, _ctx(), _files, ct).ConfigureAwait(false);
            if (rowErr != null) return Err(rowErr);
            var flags = _flags();
            var name = Str(call.Input, "flag");
            if (AssistantGrid.ResolveFlag(name, flags) is not { } flag)
                return Err($"unknown flag '{name}'. Valid (id = label): " + string.Join(", ", flags.Select(f => $"{f.Id} = {f.Label}")));
            var reason = Str(call.Input, "reason") ?? "";
            _files?.ForgetLastQueried();
            return new ToolOutcome(await _host.HandleProposalAsync(new FlagProposal(call.Id, target, rows.Distinct().ToList(), flag, reason)).ConfigureAwait(false), false);
        }
    }

    private sealed class ClearFilterTool : IAssistantTool
    {
        private readonly Func<GridContext> _ctx; private readonly IAssistantHost _host;
        public ClearFilterTool(Func<GridContext> ctx, IAssistantHost host) { _ctx = ctx; _host = host; }
        public ToolSpec Spec { get; } = AssistantGridTools.Spec(ClearFilter,
            "Clear every filter (and the search box) of the open CSV so all rows show again. Call it when the examiner asks to clear, reset or remove the filter.", ToolKind.Proposal, new JsonObject());

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var c = _ctx();
            if (c.CsvPath == null) return new ToolOutcome("No CSV is open in the results table.", true);
            return new ToolOutcome(await _host.HandleProposalAsync(new ClearFilterProposal(call.Id, c.CsvPath)).ConfigureAwait(false), false);
        }
    }

    private sealed class UnflagTool : IAssistantTool
    {
        private readonly Func<GridContext> _ctx; private readonly IAssistantHost _host; private readonly ICaseFiles? _files;
        public UnflagTool(Func<GridContext> ctx, IAssistantHost host, ICaseFiles? files)
        {
            _ctx = ctx; _host = host; _files = files;
            Spec = AssistantGridTools.Spec(UnflagRows,
                "Remove the flag from rows. Call it when the examiner asks to unflag rows. Give rows (row numbers as shown)" + (files != null ? " or a filter." : "."),
                ToolKind.Proposal, RowProps(files != null, withFlag: false));
        }
        public ToolSpec Spec { get; }

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            ToolOutcome Err(string m) => new("Unflag proposal rejected: " + m, true);
            var (target, rows, rowErr) = await ResolveTargetAsync(call.Input, _ctx(), _files, ct).ConfigureAwait(false);
            if (rowErr != null) return Err(rowErr);
            _files?.ForgetLastQueried();
            return new ToolOutcome(await _host.HandleProposalAsync(new UnflagProposal(call.Id, target, rows.Distinct().ToList(), Str(call.Input, "reason") ?? "")).ConfigureAwait(false), false);
        }
    }

    private sealed class SortTool : IAssistantTool
    {
        private readonly Func<GridContext> _ctx; private readonly IAssistantHost _host;
        public SortTool(Func<GridContext> ctx, IAssistantHost host) { _ctx = ctx; _host = host; }
        public ToolSpec Spec { get; } = AssistantGridTools.Spec(Sort,
            "Sort the CSV open in the table by one column. Call it when the examiner asks to sort or order rows (newest first = descending).",
            ToolKind.Proposal, new JsonObject
            {
                ["column"] = new JsonObject { ["type"] = "string", ["description"] = "A column name of the open CSV." },
                ["descending"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = newest / largest first. Default false (ascending)." },
            }, "column");

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var c = _ctx();
            if (c.CsvPath == null) return new ToolOutcome("No CSV is open in the results table.", true);
            var column = AssistantGrid.ResolveColumn(Str(call.Input, "column"), c.Headers, out var colErr);
            if (column == null) return new ToolOutcome("Sort proposal rejected: " + colErr, true);
            var desc = call.Input["descending"] is JsonValue d && ((d.TryGetValue<bool>(out var b) && b)
                || (d.TryGetValue<string>(out var ds) && ds.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)));
            return new ToolOutcome(await _host.HandleProposalAsync(new SortProposal(call.Id, c.CsvPath, column, desc)).ConfigureAwait(false), false);
        }
    }

    private sealed class GoToRowTool : IAssistantTool
    {
        private readonly Func<GridContext> _ctx; private readonly IAssistantHost _host;
        public GoToRowTool(Func<GridContext> ctx, IAssistantHost host) { _ctx = ctx; _host = host; }
        public ToolSpec Spec { get; } = AssistantGridTools.Spec(GoToRow,
            "Select one row of the CSV open in the table and scroll to it. Call it when the examiner asks to go to, jump to or show a row. The row must be visible with the current filter.",
            ToolKind.Proposal, new JsonObject { ["row"] = new JsonObject { ["type"] = "integer", ["description"] = "Row id as shown in the view." } }, "row");

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var c = _ctx();
            if (c.CsvPath == null) return new ToolOutcome("No CSV is open in the results table.", true);
            if (AsLong(call.Input["row"]) is not { } row)
                return new ToolOutcome("Go-to-row proposal rejected: row must be a row id (integer).", true);
            if (AssistantGrid.ToSourceRows(new[] { row }, c.RowCount, out var source) is { } err) return new ToolOutcome("Go-to-row proposal rejected: " + err, true);
            return new ToolOutcome(await _host.HandleProposalAsync(new GoToRowProposal(call.Id, c.CsvPath, source[0])).ConfigureAwait(false), false);
        }
    }
}
