namespace EzForensics.Core.Explore.Search;

/// <summary>A match inside <see cref="ContentHit.LineText"/> (character offsets into that text, for highlighting).</summary>
public readonly record struct MatchSpan(int Start, int Length);

/// <summary>One line holding at least one content match.</summary>
/// <param name="LineNumber">1-based. A line longer than about a million characters is read in segments that share its number.</param>
/// <param name="LineText">The line, or a ~400 character window of it around the first match when longer.</param>
/// <param name="LineTextOffset">Column (0-based) of <paramref name="LineText"/> within the full line; 0 unless the line was windowed.</param>
/// <param name="Spans">Matches within <paramref name="LineText"/>, in order.</param>
/// <param name="Before">Up to ContextLines lines before (each cut to 400 characters).</param>
/// <param name="After">Up to ContextLines lines after (each cut to 400 characters).</param>
public sealed record ContentHit(
    int LineNumber,
    string LineText,
    int LineTextOffset,
    IReadOnlyList<MatchSpan> Spans,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> After);

/// <summary>One matched file or folder.</summary>
public sealed record SearchResult(
    string FullPath,
    string Name,
    string Folder,
    bool IsDirectory,
    long Size,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    DateTime AccessedUtc,
    FileAttributes Attributes)
{
    /// <summary>Content matches counted (capped at MaxHitsPerFile); 0 for a name-only search.</summary>
    public int HitCount { get; init; }
    /// <summary>MaxHitsPerFile was reached and the rest of the file was not read.</summary>
    public bool HitsTruncated { get; init; }
    public IReadOnlyList<ContentHit> Hits { get; init; } = Array.Empty<ContentHit>();
    /// <summary>Encoding the content was read with (e.g. <c>UTF-16LE</c>); null when content was not searched.</summary>
    public string? EncodingName { get; init; }

    public string Extension => IsDirectory ? "" : Path.GetExtension(Name);

    internal static SearchResult From(FileEntry e) => new(
        e.FullPath, e.Name, Path.GetDirectoryName(e.FullPath) ?? "", e.IsDirectory, e.IsDirectory ? 0 : e.Length,
        e.CreatedUtc, e.ModifiedUtc, e.AccessedUtc, e.Attributes);
}

/// <summary>Why an item was not (fully) searched.</summary>
public enum SkipReason
{
    /// <summary>A folder could not be listed (access denied, vanished).</summary>
    FolderUnreadable,
    /// <summary>A file could not be opened or read (locked, access denied, I/O error).</summary>
    FileUnreadable,
    /// <summary>Larger than MaxFileSizeForContent.</summary>
    TooLarge,
    /// <summary>Looks binary and SkipBinary is on.</summary>
    Binary,
    /// <summary>Junction / symbolic link: listed, never followed (folders) or read through (files).</summary>
    LinkNotFollowed,
    /// <summary>Cloud placeholder (OneDrive etc.): reading would download it.</summary>
    CloudPlaceholder,
    /// <summary>The content regex took too long on a line.</summary>
    RegexTimeout,
    /// <summary>A search root does not exist.</summary>
    RootMissing,
}

public sealed record SkippedItem(string Path, SkipReason Reason, string? Message);

/// <summary>Snapshot of a running search (reported about 10 times a second, plus once at the end with <see cref="IsComplete"/>).</summary>
public sealed record SearchProgress(
    long FoldersScanned,
    long FilesScanned,
    long ContentSearched,
    long Matches,
    long Skipped,
    IReadOnlyDictionary<SkipReason, long> SkippedByReason,
    string? CurrentFolder,
    TimeSpan Elapsed,
    bool IsComplete,
    bool LimitReached,
    bool Cancelled);
