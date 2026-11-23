using System.Text.Json.Serialization;

namespace EzForensics.Core.Explore.Search;

/// <summary>How <see cref="SearchOptions.NameText"/> is matched against a file or folder name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NameMatchMode
{
    /// <summary>
    /// Substring of the name. Boolean expression: bare terms are AND'ed (<c>report final</c> = both), explicit uppercase
    /// <c>AND</c> / <c>OR</c> / <c>NOT</c>, <c>( )</c> groups, <c>"..."</c> is one term with spaces. A <c>;</c> is an ordinary character.
    /// </summary>
    Contains,
    /// <summary>
    /// Wildcards <c>*</c> and <c>?</c> against the whole name. Terms separated by space or <c>;</c> are OR'ed (<c>*.log;*.txt</c>), like
    /// Agent Ransack; <c>AND</c> / <c>NOT</c> / <c>( )</c> / quotes work as in Contains. A term without <c>*</c> or <c>?</c> matches as a substring.
    /// </summary>
    Wildcard,
    /// <summary>.NET regular expression searched in the name (use <c>^...$</c> to anchor).</summary>
    Regex,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SearchTarget { Files, Folders, Both }

/// <summary>How <see cref="SearchOptions.ContentText"/> is matched inside a file. Matching is per line (no multi-line regex).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentMatchMode
{
    /// <summary>The text exactly (a phrase with spaces is one literal).</summary>
    Literal,
    /// <summary>.NET regular expression, applied to each line separately (multi-line patterns are not supported; zero-length matches are ignored).</summary>
    Regex,
    /// <summary>
    /// <c>AND</c> / <c>OR</c> / <c>NOT</c> of literals evaluated per file (<c>password AND NOT example</c>); bare terms are AND'ed,
    /// <c>"..."</c> quotes a phrase. Hits are the lines holding a term that is not under NOT.
    /// </summary>
    Boolean,
}

/// <summary>Text encoding used to read file content.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentEncoding
{
    /// <summary>BOM, else UTF-16 when many NULs sit at alternate positions, else UTF-8 with Windows-1252 for invalid byte sequences.</summary>
    Auto,
    Utf8,
    Utf16LE,
    Utf16BE,
    /// <summary>Windows-1252.</summary>
    Ansi,
}

/// <summary>
/// Everything a file search needs (Agent Ransack style). JSON-serializable for saved searches; all properties have defaults, so an old
/// saved search missing newer properties still loads. Validate with <see cref="SearchValidation.Validate"/>.
/// </summary>
public sealed record SearchOptions
{
    // ---- Where ----
    /// <summary>Folders to search (and/or single files, which are tested as they are).</summary>
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();
    public bool IncludeSubfolders { get; init; } = true;
    /// <summary>
    /// Folders not to enter. A pattern with <c>\</c> or <c>:</c> is a full path (wildcards allowed, e.g. <c>C:\Windows\WinSxS</c>); otherwise it
    /// matches a folder name (e.g. <c>node_modules</c>, <c>.git</c>, <c>temp*</c>). Case-insensitive.
    /// </summary>
    public IReadOnlyList<string> ExcludeFolders { get; init; } = Array.Empty<string>();

    // ---- Name ----
    /// <summary>Empty = every name.</summary>
    public string? NameText { get; init; }
    public NameMatchMode NameMode { get; init; } = NameMatchMode.Contains;
    public bool NameMatchCase { get; init; }
    public SearchTarget Target { get; init; } = SearchTarget.Files;
    /// <summary>
    /// Match <see cref="NameText"/> with <see cref="NameFilter.Matches"/> (the Explore toolbar filter: <c>;</c> alternatives, wildcards or contains)
    /// instead of <see cref="NameMode"/>. Always case-insensitive.
    /// </summary>
    public bool NameAsFilter { get; init; }

    // ---- Content ----
    /// <summary>Empty = no content criteria. Not allowed with <see cref="SearchTarget.Folders"/>; folders never match on content.</summary>
    public string? ContentText { get; init; }
    public ContentMatchMode ContentMode { get; init; } = ContentMatchMode.Literal;
    public bool ContentMatchCase { get; init; }
    /// <summary>Matches must not touch a letter, digit or underscore on either side.</summary>
    public bool ContentWholeWord { get; init; }
    public ContentEncoding Encoding { get; init; } = ContentEncoding.Auto;
    /// <summary>Skip files with NUL bytes in their first 8 KB (unless they look like UTF-16). When false, binaries are read as Windows-1252.</summary>
    public bool SkipBinary { get; init; } = true;
    /// <summary>Larger files are not content-searched (skipped with a reason).</summary>
    public long MaxFileSizeForContent { get; init; } = 256L * 1024 * 1024;
    /// <summary>Lines of context kept before and after each hit (0..50).</summary>
    public int ContextLines { get; init; } = 2;

    // ---- Filters (all inclusive bounds, UTC) ----
    /// <summary>Size bounds apply to files only.</summary>
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTime? ModifiedAfter { get; init; }
    public DateTime? ModifiedBefore { get; init; }
    public DateTime? CreatedAfter { get; init; }
    public DateTime? CreatedBefore { get; init; }
    public DateTime? AccessedAfter { get; init; }
    public DateTime? AccessedBefore { get; init; }
    /// <summary>All of these attributes must be set.</summary>
    public FileAttributes AttributesMustHave { get; init; }
    /// <summary>None of these attributes may be set.</summary>
    public FileAttributes AttributesMustNotHave { get; init; }
    /// <summary>When false, hidden items are left out and hidden folders are not entered.</summary>
    public bool IncludeHidden { get; init; } = true;
    /// <summary>When false, system items are left out and system folders are not entered.</summary>
    public bool IncludeSystem { get; init; } = true;

    // ---- Limits ----
    public int MaxResults { get; init; } = 100_000;
    /// <summary>Matches counted per file; the file stops being read once reached (<see cref="SearchResult.HitsTruncated"/>).</summary>
    public int MaxHitsPerFile { get; init; } = 1_000;
    /// <summary>Files content-searched at the same time (1..16).</summary>
    public int MaxParallelism { get; init; } = 4;

    [JsonIgnore] public bool HasContentCriteria => !string.IsNullOrEmpty(ContentText);
    [JsonIgnore] public bool HasNameCriteria => !string.IsNullOrWhiteSpace(NameText);

    /// <summary>Attributes the UI offers as must-have / must-not-have filters.</summary>
    public static IReadOnlyList<FileAttributes> FilterableAttributes { get; } = new[]
    {
        FileAttributes.Hidden, FileAttributes.System, FileAttributes.ReadOnly, FileAttributes.Archive,
        FileAttributes.Compressed, FileAttributes.Encrypted, FileAttributes.ReparsePoint,
    };
}
