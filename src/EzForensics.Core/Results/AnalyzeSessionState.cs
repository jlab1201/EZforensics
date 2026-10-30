using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Results;

/// <summary>Serializable form of a <see cref="FilterNode"/> (flat DTO, no polymorphic JSON).</summary>
public sealed class AnalyzeFilterNode
{
    /// <summary><c>group</c>, <c>condition</c>, <c>values</c> or <c>dates</c>.</summary>
    public string Type { get; set; } = "";
    // group
    public bool Or { get; set; }
    public bool Negate { get; set; }
    public List<AnalyzeFilterNode>? Children { get; set; }
    // condition / values / dates
    public string Column { get; set; } = "";
    public ColumnFilterMode Op { get; set; }
    public string Value { get; set; } = "";
    public string? Value2 { get; set; }
    public List<string>? Values { get; set; }
    /// <summary>Days as <c>yyyy-MM-dd</c>.</summary>
    public List<string>? Days { get; set; }
    public bool IncludeBlanks { get; set; }

    public static AnalyzeFilterNode From(FilterNode n) => n switch
    {
        FilterGroup g => new AnalyzeFilterNode { Type = "group", Or = g.Kind == FilterGroupKind.Or, Negate = g.Negate, Children = g.Children.Select(From).ToList() },
        FilterCondition c => new AnalyzeFilterNode { Type = "condition", Column = c.Column, Op = c.Op, Value = c.Value, Value2 = c.Value2 },
        FilterValueSet v => new AnalyzeFilterNode { Type = "values", Column = v.Column, Values = v.Values.ToList(), IncludeBlanks = v.IncludeBlanks },
        FilterDateSet d => new AnalyzeFilterNode
        {
            Type = "dates", Column = d.Column, IncludeBlanks = d.IncludeBlanks,
            Days = d.Days.OrderBy(x => x).Select(x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList(),
        },
        _ => throw new NotSupportedException(n.GetType().Name),
    };

    /// <summary>Rebuilds the node; null when the DTO is not recognised.</summary>
    public FilterNode? ToNode()
    {
        switch (Type)
        {
            case "group":
                return new FilterGroup(Or ? FilterGroupKind.Or : FilterGroupKind.And,
                    (Children ?? new()).Select(c => c.ToNode()).Where(c => c != null).Cast<FilterNode>()) { Negate = Negate };
            case "condition": return new FilterCondition(Column ?? "", Op, Value ?? "", Value2);
            case "values": return new FilterValueSet(Column ?? "", Values ?? new(), IncludeBlanks);
            case "dates":
                var days = new List<DateOnly>();
                foreach (var s in Days ?? new())
                    if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) days.Add(d);
                return new FilterDateSet(Column ?? "", days, IncludeBlanks);
            default: return null;
        }
    }
}

/// <summary>Filter-row cell of one column (operator + text) and its funnel filter.</summary>
public sealed class AnalyzeCellFilter
{
    public string Column { get; set; } = "";
    public ColumnFilterMode Op { get; set; }
    public string Value { get; set; } = "";
    public AnalyzeFilterNode? Header { get; set; }
}

/// <summary>Everything the Analyze grid shows for one CSV, except column widths / hidden columns (kept in settings by header signature).</summary>
public sealed class AnalyzeFileState
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string HeadSha256 { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    /// <summary>Name of the saved filter the current filters came from, if any.</summary>
    public string? SavedFilterName { get; set; }
    /// <summary>Name of the saved column view last applied / saved for this file, if any.</summary>
    public string? SavedViewName { get; set; }
    public string GlobalText { get; set; } = "";
    public bool FiltersEnabled { get; set; } = true;
    /// <summary>Flag filter: null = all rows, true = flagged only, false = unflagged only.</summary>
    public bool? FlagFilter { get; set; }
    public List<string> FlagIds { get; set; } = new();
    public List<AnalyzeCellFilter> Cells { get; set; } = new();
    /// <summary>Stacked / builder conditions (ANDed).</summary>
    public AnalyzeFilterNode? Extra { get; set; }
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }
    /// <summary>CSV column indices in display order (empty = default order).</summary>
    public List<int> ColumnOrder { get; set; } = new();
    /// <summary>Position (in the shown rows) and CSV row of the selected row; -1 = none.</summary>
    public int SelectedPosition { get; set; } = -1;
    public long SelectedSource { get; set; } = -1;
    /// <summary>Index of the first visible row.</summary>
    public int ScrollOffset { get; set; }
    /// <summary>Pivot (group by) columns, outermost first; empty = no pivot.</summary>
    public List<PivotGroupSpec> GroupColumns { get; set; } = new();
    /// <summary>Keys (<see cref="PivotNode.Key"/>) of the collapsed pivot groups.</summary>
    public List<string> CollapsedGroups { get; set; } = new();
}

/// <summary>Analyze page state saved between sessions (<c>analyze-session.json</c>).</summary>
public sealed class AnalyzeSessionState
{
    public const int CurrentVersion = 1;
    public const int MaxFiles = 50;

    public int Version { get; set; } = CurrentVersion;
    public string? CaseFolder { get; set; }
    public string? SelectedFile { get; set; }
    public bool FocusMode { get; set; }
    /// <summary>Per-file state, most recently used first.</summary>
    public List<AnalyzeFileState> Files { get; set; } = new();

    /// <summary>Adds or replaces the state of <paramref name="f"/>.Path as the most recent one; evicts the oldest beyond <see cref="MaxFiles"/>.</summary>
    public void Put(AnalyzeFileState f)
    {
        Files.RemoveAll(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase));
        Files.Insert(0, f);
        if (Files.Count > MaxFiles) Files.RemoveRange(MaxFiles, Files.Count - MaxFiles);
    }

    /// <summary>The saved state of <paramref name="path"/> when the file is unchanged; otherwise null (a stale entry is removed and <paramref name="dropReason"/> says why).</summary>
    public AnalyzeFileState? TryGet(string path, CsvFingerprint fp, out string? dropReason)
    {
        dropReason = null;
        var f = Files.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (f == null) return null;
        if (fp.Matches(f.Size, f.LastWriteUtc, f.HeadSha256)) return f;
        Files.Remove(f);
        dropReason = $"{System.IO.Path.GetFileName(path)} changed since its view was saved";
        return null;
    }

    /// <summary>Drops the state of files that no longer exist; returns the dropped paths.</summary>
    public List<string> PruneMissing(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var gone = Files.Where(f => !exists(f.Path)).Select(f => f.Path).ToList();
        Files.RemoveAll(f => gone.Contains(f.Path));
        return gone;
    }
}

/// <summary>Reads and writes <see cref="AnalyzeSessionState"/> (atomic write; a missing or corrupt file gives an empty state).</summary>
public static class AnalyzeSessionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly object Gate = new();

    public static string DefaultPath => System.IO.Path.Combine(AppPaths.DataRoot, "analyze-session.json");

    public static AnalyzeSessionState Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AnalyzeSessionState();
            var s = JsonSerializer.Deserialize<AnalyzeSessionState>(File.ReadAllText(path), Json);
            if (s == null || s.Version > AnalyzeSessionState.CurrentVersion) return new AnalyzeSessionState();
            s.Files ??= new();
            s.Files.RemoveAll(f => f == null || string.IsNullOrEmpty(f.Path));
            foreach (var f in s.Files)
            {
                f.GroupColumns = (f.GroupColumns ?? new()).Where(g => g != null && !string.IsNullOrEmpty(g.Column)).ToList();
                f.CollapsedGroups = (f.CollapsedGroups ?? new()).Where(k => k != null).ToList();
            }
            if (s.Files.Count > AnalyzeSessionState.MaxFiles) s.Files.RemoveRange(AnalyzeSessionState.MaxFiles, s.Files.Count - AnalyzeSessionState.MaxFiles);
            return s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new AnalyzeSessionState();
        }
    }

    /// <summary>Writes the state atomically (temp file, then rename). Serialises writers.</summary>
    public static void Save(AnalyzeSessionState state, string? path = null)
    {
        path ??= DefaultPath;
        lock (Gate)
        {
            var json = JsonSerializer.Serialize(state, Json);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
