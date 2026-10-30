namespace EzForensics.Core.Archives;

/// <summary>KAPE output zips are extracted as is; the <c>*_CopyLog.csv</c> (original paths and times) is recorded for a later phase.</summary>
public static class KapeLayout
{
    public const string Name = "kape";

    public static bool IsMatch(ArchiveInspection insp) =>
        insp.Entries.Any(e => !e.IsDirectory && e.Name.EndsWith("_CopyLog.csv", StringComparison.OrdinalIgnoreCase));

    /// <summary>The extracted path (relative to the extracted folder) of the first CopyLog, or null.</summary>
    public static string? FindCopyLog(IEnumerable<ExtractionEntry> entries) =>
        entries.Where(e => e.ExtractedPath.Length > 0 && e.ExtractedPath.EndsWith("_CopyLog.csv", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.ExtractedPath).OrderBy(p => p.Length).FirstOrDefault();
}
