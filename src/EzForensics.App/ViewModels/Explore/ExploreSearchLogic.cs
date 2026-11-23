using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Explore.Search;

namespace EzForensics.App.ViewModels.Explore;

public enum SizeUnit { Bytes, KB, MB, GB }

/// <summary>Tri-state attribute filter: no opinion, the attribute must be set, or it must not be set.</summary>
public enum AttrFilter { Any, Must, MustNot }

/// <summary>A piece of a line: plain text or a highlighted match.</summary>
public readonly record struct TextSegment(string Text, bool IsMatch);

/// <summary>What the Search panel edits. Plain values only; <see cref="ExploreSearchLogic.Build"/> turns it into <see cref="SearchOptions"/>.</summary>
public sealed partial class SearchCriteria : ObservableObject
{
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private NameMatchMode _nameMode = NameMatchMode.Contains;
    [ObservableProperty] private bool _nameMatchCase;
    [ObservableProperty] private SearchTarget _target = SearchTarget.Files;

    [ObservableProperty] private string _contentText = "";
    [ObservableProperty] private ContentMatchMode _contentMode = ContentMatchMode.Literal;
    [ObservableProperty] private bool _contentMatchCase;
    [ObservableProperty] private bool _contentWholeWord;

    public System.Collections.ObjectModel.ObservableCollection<string> Roots { get; } = new();
    [ObservableProperty] private bool _includeSubfolders = true;
    /// <summary>Folders not to enter, separated by <c>;</c>.</summary>
    [ObservableProperty] private string _excludeFolders = "";

    [ObservableProperty] private string _minSize = "";
    [ObservableProperty] private SizeUnit _minSizeUnit = SizeUnit.MB;
    [ObservableProperty] private string _maxSize = "";
    [ObservableProperty] private SizeUnit _maxSizeUnit = SizeUnit.MB;

    [ObservableProperty] private DateTime? _modifiedAfter;
    [ObservableProperty] private DateTime? _modifiedBefore;
    [ObservableProperty] private DateTime? _createdAfter;
    [ObservableProperty] private DateTime? _createdBefore;
    [ObservableProperty] private DateTime? _accessedAfter;
    [ObservableProperty] private DateTime? _accessedBefore;

    [ObservableProperty] private AttrFilter _hidden;
    [ObservableProperty] private AttrFilter _system;
    [ObservableProperty] private AttrFilter _readOnly;
    [ObservableProperty] private AttrFilter _compressed;
    [ObservableProperty] private AttrFilter _encrypted;

    [ObservableProperty] private ContentEncoding _encoding = ContentEncoding.Auto;
    [ObservableProperty] private bool _skipBinary = true;
    [ObservableProperty] private string _contextLines = "2";
    [ObservableProperty] private string _maxResults = "100000";
}

/// <summary>Pure logic behind the Explore Search panel (testable without WPF): criteria to options, size units, highlight segments, status text.</summary>
public static class ExploreSearchLogic
{
    public const int MaxHitsShown = 500;

    // ------------------------------------------------------------------------------ sizes

    public static long UnitBytes(SizeUnit u) => u switch
    {
        SizeUnit.KB => 1024L,
        SizeUnit.MB => 1024L * 1024,
        SizeUnit.GB => 1024L * 1024 * 1024,
        _ => 1L,
    };

    /// <summary>Blank gives null (no limit). A number (decimals allowed) times the unit gives bytes. False for text, negatives and overflow.</summary>
    public static bool TryParseSize(string? text, SizeUnit unit, out long? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) &&
            !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) return false;
        var b = v * UnitBytes(unit);
        if (b >= long.MaxValue) return false;
        bytes = (long)Math.Round(b);
        return true;
    }

    /// <summary>Splits a byte count into the largest unit that divides it evenly (for loading a saved search back into the boxes).</summary>
    public static (string Text, SizeUnit Unit) FormatSize(long? bytes)
    {
        if (bytes is not { } b) return ("", SizeUnit.MB);
        foreach (var u in new[] { SizeUnit.GB, SizeUnit.MB, SizeUnit.KB })
            if (b >= UnitBytes(u) && b % UnitBytes(u) == 0) return ((b / UnitBytes(u)).ToString(CultureInfo.InvariantCulture), u);
        return (b.ToString(CultureInfo.InvariantCulture), SizeUnit.Bytes);
    }

    // ------------------------------------------------------------------------------ criteria <-> options

    /// <summary>The "Look in" box: folders separated by <c>;</c>, trimmed, surrounding quotes dropped, blanks and repeats removed.</summary>
    public static IReadOnlyList<string> ParseLookIn(string? text) =>
        SplitFolders(text).Select(s => s.Trim('"').Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> SplitFolders(string? text) =>
        (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0).ToList();

    /// <summary>A DatePicker date (no time) as the start of that UTC day; evidence times are shown and filtered in UTC.</summary>
    public static DateTime? StartOfDayUtc(DateTime? d) =>
        d is { } x ? DateTime.SpecifyKind(x.Date, DateTimeKind.Utc) : null;

    /// <summary>A DatePicker date as the very end of that UTC day, so "before 5 May" includes all of 5 May.</summary>
    public static DateTime? EndOfDayUtc(DateTime? d) =>
        d is { } x ? DateTime.SpecifyKind(x.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc) : null;

    private static FileAttributes Attr(AttrFilter f, FileAttributes a, bool must) =>
        (f == AttrFilter.Must && must) || (f == AttrFilter.MustNot && !must) ? a : 0;

    /// <summary>
    /// Builds the options and returns every problem found, readable and in one list: unreadable numbers here, then the Core validation
    /// (bad regex, missing folders, ...). Options are null when something was wrong.
    /// </summary>
    public static (SearchOptions? Options, IReadOnlyList<string> Errors) Build(SearchCriteria c, bool checkRootsExist = true)
    {
        var errors = new List<string>();
        if (!TryParseSize(c.MinSize, c.MinSizeUnit, out var min)) errors.Add("Minimum size is not a number.");
        if (!TryParseSize(c.MaxSize, c.MaxSizeUnit, out var max)) errors.Add("Maximum size is not a number.");
        if (!int.TryParse(c.ContextLines.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ctx) || ctx < 0) { ctx = 2; errors.Add("Context lines must be a whole number, 0 or more."); }
        if (!int.TryParse(c.MaxResults.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRes) || maxRes < 1) { maxRes = 100_000; errors.Add("Max results must be a whole number, 1 or more."); }

        var must = Attr(c.Hidden, FileAttributes.Hidden, true) | Attr(c.System, FileAttributes.System, true) | Attr(c.ReadOnly, FileAttributes.ReadOnly, true)
                   | Attr(c.Compressed, FileAttributes.Compressed, true) | Attr(c.Encrypted, FileAttributes.Encrypted, true);
        var mustNot = Attr(c.Hidden, FileAttributes.Hidden, false) | Attr(c.System, FileAttributes.System, false) | Attr(c.ReadOnly, FileAttributes.ReadOnly, false)
                      | Attr(c.Compressed, FileAttributes.Compressed, false) | Attr(c.Encrypted, FileAttributes.Encrypted, false);

        var o = new SearchOptions
        {
            Roots = c.Roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList(),
            IncludeSubfolders = c.IncludeSubfolders,
            ExcludeFolders = SplitFolders(c.ExcludeFolders),
            NameText = string.IsNullOrWhiteSpace(c.NameText) ? null : c.NameText.Trim(),
            NameMode = c.NameMode,
            NameMatchCase = c.NameMatchCase,
            Target = c.Target,
            ContentText = string.IsNullOrEmpty(c.ContentText) ? null : c.ContentText,
            ContentMode = c.ContentMode,
            ContentMatchCase = c.ContentMatchCase,
            ContentWholeWord = c.ContentWholeWord,
            Encoding = c.Encoding,
            SkipBinary = c.SkipBinary,
            ContextLines = Math.Min(ctx, 50),
            MinSize = min,
            MaxSize = max,
            ModifiedAfter = StartOfDayUtc(c.ModifiedAfter),
            ModifiedBefore = EndOfDayUtc(c.ModifiedBefore),
            CreatedAfter = StartOfDayUtc(c.CreatedAfter),
            CreatedBefore = EndOfDayUtc(c.CreatedBefore),
            AccessedAfter = StartOfDayUtc(c.AccessedAfter),
            AccessedBefore = EndOfDayUtc(c.AccessedBefore),
            AttributesMustHave = must,
            AttributesMustNotHave = mustNot,
            MaxResults = maxRes,
        };
        if (errors.Count == 0) errors.AddRange(SearchValidation.Validate(o, checkRootsExist));
        return (errors.Count == 0 ? o : null, errors);
    }

    /// <summary>Fills the criteria from saved options. Roots are replaced.</summary>
    public static void Apply(SearchCriteria c, SearchOptions o)
    {
        c.Roots.Clear();
        foreach (var r in o.Roots) c.Roots.Add(r);
        c.IncludeSubfolders = o.IncludeSubfolders;
        c.ExcludeFolders = string.Join("; ", o.ExcludeFolders);
        c.NameText = o.NameText ?? "";
        c.NameMode = o.NameMode;
        c.NameMatchCase = o.NameMatchCase;
        c.Target = o.Target;
        c.ContentText = o.ContentText ?? "";
        c.ContentMode = o.ContentMode;
        c.ContentMatchCase = o.ContentMatchCase;
        c.ContentWholeWord = o.ContentWholeWord;
        c.Encoding = o.Encoding;
        c.SkipBinary = o.SkipBinary;
        c.ContextLines = o.ContextLines.ToString(CultureInfo.InvariantCulture);
        c.MaxResults = o.MaxResults.ToString(CultureInfo.InvariantCulture);
        (c.MinSize, c.MinSizeUnit) = FormatSize(o.MinSize);
        (c.MaxSize, c.MaxSizeUnit) = FormatSize(o.MaxSize);
        c.ModifiedAfter = LocalDate(o.ModifiedAfter); c.ModifiedBefore = LocalDate(o.ModifiedBefore);
        c.CreatedAfter = LocalDate(o.CreatedAfter); c.CreatedBefore = LocalDate(o.CreatedBefore);
        c.AccessedAfter = LocalDate(o.AccessedAfter); c.AccessedBefore = LocalDate(o.AccessedBefore);
        c.Hidden = State(o, FileAttributes.Hidden); c.System = State(o, FileAttributes.System); c.ReadOnly = State(o, FileAttributes.ReadOnly);
        c.Compressed = State(o, FileAttributes.Compressed); c.Encrypted = State(o, FileAttributes.Encrypted);
    }

    private static DateTime? LocalDate(DateTime? utc) => utc is { } u ? u.ToUniversalTime().Date : null;

    private static AttrFilter State(SearchOptions o, FileAttributes a) =>
        (o.AttributesMustHave & a) != 0 ? AttrFilter.Must : (o.AttributesMustNotHave & a) != 0 ? AttrFilter.MustNot : AttrFilter.Any;

    // ------------------------------------------------------------------------------ highlighting

    /// <summary>
    /// Cuts <paramref name="text"/> into plain and matched pieces. Spans may overlap, touch, be unordered or reach past the end: they are
    /// clamped and merged, and empty ones dropped. The pieces always join back into the original text.
    /// </summary>
    public static IReadOnlyList<TextSegment> SplitSegments(string? text, IReadOnlyList<MatchSpan>? spans)
    {
        text ??= "";
        if (text.Length == 0) return Array.Empty<TextSegment>();
        var ranges = new List<(int S, int E)>();
        foreach (var sp in spans ?? Array.Empty<MatchSpan>())
        {
            int s = Math.Max(0, sp.Start), e = Math.Min(text.Length, sp.Start + sp.Length);
            if (sp.Length > 0 && e > s) ranges.Add((s, e));
        }
        ranges.Sort((a, b) => a.S.CompareTo(b.S));
        var merged = new List<(int S, int E)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.S <= merged[^1].E) merged[^1] = (merged[^1].S, Math.Max(merged[^1].E, r.E));
            else merged.Add(r);
        }
        var result = new List<TextSegment>();
        int pos = 0;
        foreach (var (s, e) in merged)
        {
            if (s > pos) result.Add(new TextSegment(text[pos..s], false));
            result.Add(new TextSegment(text[s..e], true));
            pos = e;
        }
        if (pos < text.Length) result.Add(new TextSegment(text[pos..], false));
        return result;
    }

    // ------------------------------------------------------------------------------ status

    public static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds < 60 ? t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}"
        : $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>"N found · X files scanned · Y skipped · elapsed", plus " · limit reached" / " · cancelled" notes.</summary>
    public static string StatusText(SearchProgress p)
    {
        var s = $"{p.Matches:N0} found · {p.FilesScanned:N0} files scanned · {p.Skipped:N0} skipped · {FormatElapsed(p.Elapsed)}";
        if (p.LimitReached) s += " · limit reached";
        if (p.Cancelled) s += " · cancelled";
        return s;
    }

    public static string ReasonText(SkipReason r) => r switch
    {
        SkipReason.FolderUnreadable => "Folder not readable",
        SkipReason.FileUnreadable => "File not readable",
        SkipReason.TooLarge => "Too large for content search",
        SkipReason.Binary => "Binary file skipped",
        SkipReason.LinkNotFollowed => "Link not followed",
        SkipReason.CloudPlaceholder => "Cloud file not downloaded",
        SkipReason.RegexTimeout => "Regex took too long",
        SkipReason.RootMissing => "Folder not found",
        _ => r.ToString(),
    };

    /// <summary>Hit count as shown in the grid: "12", or "1,000+" when the per-file cap stopped the read.</summary>
    public static string HitsText(SearchResult r) =>
        r.HitCount == 0 ? "" : r.HitCount.ToString("N0") + (r.HitsTruncated ? "+" : "");
}
