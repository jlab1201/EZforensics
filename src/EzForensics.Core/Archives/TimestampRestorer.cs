namespace EzForensics.Core.Archives;

/// <summary>
/// Restores the archive's stored times on extracted files (after each file is closed) and directories (bottom-up at the end, because writing
/// a child changes the parent's mtime). mtime always; created/accessed only when the archive stored them.
/// </summary>
public static class TimestampRestorer
{
    public static bool ApplyFile(string path, ArchiveEntryInfo e)
    {
        if (e.ModifiedUtc == null && e.CreatedUtc == null && e.AccessedUtc == null) return false;
        try
        {
            if (e.CreatedUtc is { } c) File.SetCreationTimeUtc(path, c);
            if (e.ModifiedUtc is { } m) File.SetLastWriteTimeUtc(path, m);
            if (e.AccessedUtc is { } a) File.SetLastAccessTimeUtc(path, a);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { return false; }
    }

    public static bool ApplyDirectory(string path, ArchiveEntryInfo e)
    {
        if (e.ModifiedUtc == null && e.CreatedUtc == null && e.AccessedUtc == null) return false;
        try
        {
            if (e.CreatedUtc is { } c) Directory.SetCreationTimeUtc(path, c);
            if (e.ModifiedUtc is { } m) Directory.SetLastWriteTimeUtc(path, m);
            if (e.AccessedUtc is { } a) Directory.SetLastAccessTimeUtc(path, a);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>Applies directory times deepest first. Returns the paths that were restored.</summary>
    public static HashSet<string> ApplyDirectories(IEnumerable<(string Path, ArchiveEntryInfo Entry)> dirs)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, e) in dirs.OrderByDescending(d => d.Path.Count(ch => ch == '\\' || ch == '/')))
            if (ApplyDirectory(p, e)) done.Add(p);
        return done;
    }

    /// <summary>Coverage over the extracted files: All (created+accessed+modified), DosLocal (only DOS times), ModifiedOnly, or Mixed.</summary>
    public static TimestampCoverage Coverage(IEnumerable<ArchiveEntryInfo> files)
    {
        var kinds = files.Select(e => e.TimeSource == EntryTimeSource.DosLocal ? 2
                : e.ModifiedUtc != null && e.CreatedUtc != null && e.AccessedUtc != null ? 1 : 0)
            .Distinct().ToList();
        if (kinds.Count == 0) return TimestampCoverage.ModifiedOnly;
        if (kinds.Count > 1) return TimestampCoverage.Mixed;
        return kinds[0] switch { 1 => TimestampCoverage.All, 2 => TimestampCoverage.DosLocal, _ => TimestampCoverage.ModifiedOnly };
    }
}
