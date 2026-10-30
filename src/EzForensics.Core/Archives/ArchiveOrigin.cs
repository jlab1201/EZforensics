namespace EzForensics.Core.Archives;

/// <summary>Archive container format (detected by magic bytes).</summary>
public enum ArchiveFormat { Zip, SevenZip, Rar, Tar, GZip, TarGz, Vhd, Vhdx, Unknown }

/// <summary>Which file times the archive carried (and were restored on the extracted files).</summary>
public enum TimestampCoverage { ModifiedOnly, All, Mixed, DosLocal }

/// <summary>Where extracted evidence came from. Never holds a password.</summary>
/// <param name="Chain">Outer to inner archive paths for nested archives; just the archive path otherwise.</param>
/// <param name="Parent">Appended: the archive this one was extracted from (nested archives; null for the outermost), so the evidence the user
/// supplied (the outer archive, with its SHA-256) is recorded even when only the inner archive's copy is scanned.</param>
public sealed record ArchiveOrigin(string ArchivePath, string Sha256, ArchiveFormat Format, bool Encrypted, string? EncryptionMethod, string ExtractedTo,
    IReadOnlyList<string> Chain, TimestampCoverage TimestampCoverage, ArchiveOrigin? Parent = null)
{
    /// <summary>This origin and its parents, innermost first.</summary>
    public IEnumerable<ArchiveOrigin> SelfAndParents()
    {
        for (var o = this; o != null; o = o.Parent) yield return o;
    }

    /// <summary>The origin whose extracted folder holds <paramref name="path"/> (the deepest match), or null.</summary>
    public static ArchiveOrigin? ForPath(IEnumerable<ArchiveOrigin> origins, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return null; }
        return origins
            .Where(o => o.ExtractedTo.Length > 0 && (full.StartsWith(o.ExtractedTo.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)
                                                     || string.Equals(full.TrimEnd('\\', '/'), o.ExtractedTo.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(o => o.ExtractedTo.Length)
            .FirstOrDefault();
    }
}

/// <summary>Archive provenance for jobs whose input has no discovered system (a memory image or a capture alone in an archive).</summary>
public static class ArchiveProvenance
{
    /// <summary>
    /// <paramref name="spec"/> with <see cref="Execution.JobProvenance.Archive"/> set to the origin whose extracted folder holds
    /// <paramref name="inputPath"/>, when the planner left it null; otherwise <paramref name="spec"/> unchanged.
    /// </summary>
    public static Execution.JobSpec Attach(Execution.JobSpec spec, string? inputPath, IEnumerable<ArchiveOrigin>? origins)
    {
        if (origins == null || spec.Provenance is not { Archive: null } prov) return spec;
        var o = ArchiveOrigin.ForPath(origins, inputPath);
        return o == null ? spec : spec with { Provenance = prov with { Archive = o } };
    }
}
