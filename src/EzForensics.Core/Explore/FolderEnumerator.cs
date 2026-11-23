namespace EzForensics.Core.Explore;

/// <summary>One file or folder in an Explore listing (metadata from the directory enumeration; the file is never opened).</summary>
public sealed record FileEntry(
    string FullPath,
    string Name,
    bool IsDirectory,
    long Length,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    DateTime AccessedUtc,
    FileAttributes Attributes)
{
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name);
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    /// <summary>Metadata of a single path without following a reparse point; null when it does not exist or cannot be read.</summary>
    public static FileEntry? FromPath(string path)
    {
        try
        {
            FileSystemInfo i = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!i.Exists) return null;
            return From(i);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    internal static FileEntry From(FileSystemInfo i) => new(
        i.FullName, i.Name, i is DirectoryInfo,
        i is FileInfo f ? f.Length : 0,
        i.CreationTimeUtc, i.LastWriteTimeUtc, i.LastAccessTimeUtc, i.Attributes);
}

/// <summary>A page of a folder listing.</summary>
/// <param name="HasMore">More entries follow this page.</param>
/// <param name="Error">Why the folder could not be listed (missing, access denied, a reparse point); null on success.</param>
public sealed record FolderPage(IReadOnlyList<FileEntry> Entries, bool HasMore, string? Error = null);

public sealed record FolderEnumerationOptions
{
    /// <summary>List reparse points (junctions, symlinks, OneDrive placeholders, dedup files) as entries. Default true: a listing shows every child. Folder links are never descended into.</summary>
    public bool IncludeReparsePoints { get; init; } = true;

    /// <summary>Child names to leave out (case-insensitive), e.g. <c>_explore</c> in the case folder.</summary>
    public IReadOnlyCollection<string>? ExcludeNames { get; init; }

    public static FolderEnumerationOptions Default { get; } = new();

    /// <summary>For the case folder itself: hides Explore's own <c>_explore</c> folder.</summary>
    public static FolderEnumerationOptions CaseRoot { get; } = new() { ExcludeNames = new[] { ExplorePaths.FolderName } };
}

/// <summary>
/// Lazy, read-only listing of one folder's children. Hidden, system and reparse-point entries are all listed (evidence; cloud placeholders and
/// dedup files carry the flag but are ordinary files), folder links (junctions, symlinks) are never followed, inaccessible entries are ignored and a folder that cannot be opened yields an empty result instead of throwing.
/// </summary>
public static class FolderEnumerator
{
    private static readonly EnumerationOptions Enum = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>Children of <paramref name="dir"/>, in file-system order, produced as they are read.</summary>
    public static IEnumerable<FileEntry> Enumerate(string dir, FolderEnumerationOptions? options = null) =>
        EnumerateCore(dir, options ?? FolderEnumerationOptions.Default, out _);

    /// <summary>Entries <paramref name="skip"/>..<paramref name="skip"/>+<paramref name="take"/> of the listing.</summary>
    public static FolderPage Page(string dir, int skip, int take, FolderEnumerationOptions? options = null)
    {
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));
        var list = new List<FileEntry>(Math.Min(take, 4096));
        bool more = false;
        var items = EnumerateCore(dir, options ?? FolderEnumerationOptions.Default, out var error);
        if (error != null) return new FolderPage(list, false, error);
        int i = 0;
        foreach (var e in items)
        {
            if (i++ < skip) continue;
            if (list.Count == take) { more = true; break; }
            list.Add(e);
        }
        return new FolderPage(list, more);
    }

    /// <summary>The whole listing in pages of <paramref name="pageSize"/> (the last page may be shorter).</summary>
    public static IEnumerable<IReadOnlyList<FileEntry>> EnumeratePages(string dir, int pageSize, FolderEnumerationOptions? options = null,
        CancellationToken ct = default)
    {
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var page = new List<FileEntry>(pageSize);
        foreach (var e in Enumerate(dir, options))
        {
            ct.ThrowIfCancellationRequested();
            page.Add(e);
            if (page.Count == pageSize) { yield return page; page = new List<FileEntry>(pageSize); }
        }
        if (page.Count > 0) yield return page;
    }

    private static IEnumerable<FileEntry> EnumerateCore(string dir, FolderEnumerationOptions o, out string? error)
    {
        error = null;
        DirectoryInfo di;
        try
        {
            di = new DirectoryInfo(dir);
            if (!di.Exists) { error = "Folder not found."; return Array.Empty<FileEntry>(); }
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0 && di.Parent != null && IsFolderLink(di))
            {
                error = "The folder is a reparse point (junction or symbolic link) and is not followed.";
                return Array.Empty<FileEntry>();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return Array.Empty<FileEntry>();
        }
        return Iterate(di, o);
    }

    /// <summary>True for a junction or symbolic link (has a link target); other reparse folders (cloud placeholders, mount points) are ordinary folders.</summary>
    private static bool IsFolderLink(DirectoryInfo di)
    {
        try { return di.LinkTarget != null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static IEnumerable<FileEntry> Iterate(DirectoryInfo di, FolderEnumerationOptions o)
    {
        IEnumerator<FileSystemInfo> it;
        try { it = di.EnumerateFileSystemInfos("*", Enum).GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        using (it)
        {
            while (true)
            {
                FileEntry? entry = null;
                try
                {
                    if (!it.MoveNext()) yield break;
                    var i = it.Current;
                    if (o.ExcludeNames != null && o.ExcludeNames.Contains(i.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if ((i.Attributes & FileAttributes.ReparsePoint) != 0 && !o.IncludeReparsePoints) continue;
                    try { entry = FileEntry.From(i); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        // One entry whose metadata cannot be read (vanished, odd placeholder): still list it, without size and times.
                        entry = new FileEntry(i.FullName, i.Name, i is DirectoryInfo, 0, default, default, default, 0);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The folder vanished or became unreadable mid-listing: stop quietly with what was read.
                    yield break;
                }
                yield return entry;
            }
        }
    }
}
