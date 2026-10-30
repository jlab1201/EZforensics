using System.Globalization;

namespace EzForensics.Core.Advisories;

/// <summary>Thresholds for <see cref="CopyHeuristics"/>.</summary>
public sealed record CopyHeuristicOptions
{
    /// <summary>Minimum inputs (with both Created and Modified) before the Created&gt;Modified heuristic is trusted.</summary>
    public int MinSampleCreatedAfterModified { get; init; } = 3;
    /// <summary>Share of inputs with Created later than Modified that indicates a copy.</summary>
    public double CreatedAfterModifiedRatio { get; init; } = 0.6;
    /// <summary>Created must exceed Modified by more than this to count (absorbs clock/precision noise).</summary>
    public TimeSpan CreatedAfterModifiedTolerance { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Minimum inputs before the clustering heuristic is trusted.</summary>
    public int MinSampleCluster { get; init; } = 5;
    public TimeSpan ClusterWindow { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Share of Created times inside one <see cref="ClusterWindow"/> that indicates a bulk copy.</summary>
    public double ClusterRatio { get; init; } = 0.9;
    /// <summary>Modified times must span at least this long for clustered Created times to look like a copy.</summary>
    public TimeSpan MinModifiedSpread { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>Detects collections whose file-system timestamps were reset by copying (pure, no I/O).</summary>
public static class CopyHeuristics
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm 'UTC'";

    public static CopyAssessment Assess(
        IReadOnlyList<InputTimestampFact>? facts,
        EvidenceAcquisition acquisition,
        string inputNoun = "input files",
        CopyHeuristicOptions? options = null)
    {
        options ??= new CopyHeuristicOptions();
        var archive = acquisition == EvidenceAcquisition.ExtractedArchive;
        var declared = acquisition == EvidenceAcquisition.CopiedCollection || archive;
        var usable = (facts ?? Array.Empty<InputTimestampFact>())
            .Where(f => f.CreatedUtc is not null && f.ModifiedUtc is not null)
            .ToList();
        var n = usable.Count;

        // (1) Created later than Modified: a copy stamps Created with the copy time but keeps Modified.
        var cam = usable.Count(f => f.CreatedUtc!.Value - f.ModifiedUtc!.Value > options.CreatedAfterModifiedTolerance);
        var camHit = n >= options.MinSampleCreatedAfterModified && cam >= options.CreatedAfterModifiedRatio * n;

        // (2) Created times bunched in a short window while Modified times are spread out.
        int clustered = 0;
        DateTime? clusterStart = null, clusterEnd = null;
        TimeSpan? modSpan = null;
        var clusterHit = false;
        if (n > 0)
        {
            var created = usable.Select(f => f.CreatedUtc!.Value).OrderBy(t => t).ToList();
            (clustered, clusterStart, clusterEnd) = LargestWindow(created, options.ClusterWindow);
            var modified = usable.Select(f => f.ModifiedUtc!.Value).OrderBy(t => t).ToList();
            modSpan = modified[^1] - modified[0];
            var (modClustered, _, _) = LargestWindow(modified, options.ClusterWindow);
            clusterHit = n >= options.MinSampleCluster
                         && clustered >= options.ClusterRatio * n
                         && modSpan >= options.MinModifiedSpread
                         && modClustered < options.ClusterRatio * n;
        }

        var hits = (camHit ? 1 : 0) + (clusterHit ? 1 : 0);
        var confidence = declared || hits == 2 ? CopyConfidence.High
            : hits == 1 ? CopyConfidence.Medium
            : CopyConfidence.None;

        var parts = new List<string>();
        if (archive)
            parts.Add("The evidence was extracted from an archive, so it is a copy: file-system times of the extracted files were set during extraction.");
        else if (declared)
            parts.Add("The evidence source was recorded as a copied collection (e.g. KAPE, robocopy or a manual copy).");
        if (acquisition == EvidenceAcquisition.MountedImageOrRawCopy && hits > 0)
            parts.Add("Although the evidence was recorded as an image or raw copy, its file times look copied:");
        if (camHit)
            parts.Add($"{cam} of {n} {inputNoun} have a Created time later than their Modified time.");
        if (clusterHit)
            parts.Add($"{clustered} of {n} {inputNoun} were created within {Minutes(options.ClusterWindow)} of each other " +
                      $"({clusterStart!.Value.ToString(TimeFormat, CultureInfo.InvariantCulture)} to {clusterEnd!.Value.ToString(TimeFormat, CultureInfo.InvariantCulture)}) " +
                      $"while their Modified times span {Describe(modSpan!.Value)}.");
        if (hits > 0)
            parts.Add(hits == 2 || declared
                ? "This is typical of a file copy."
                : "This suggests, but does not prove, that the files were copied.");

        return new CopyAssessment
        {
            Suspected = declared || hits > 0,
            Confidence = confidence,
            Declared = declared,
            SampleSize = n,
            CreatedAfterModified = cam,
            CreatedAfterModifiedHit = camHit,
            CreatedClustered = clustered,
            CreatedClusterHit = clusterHit,
            ClusterStartUtc = clusterStart,
            ClusterEndUtc = clusterEnd,
            ModifiedSpan = modSpan,
            Summary = string.Join(" ", parts),
        };
    }

    /// <summary>Largest number of sorted timestamps that fit in one window (two-pointer sweep).</summary>
    private static (int Count, DateTime? Start, DateTime? End) LargestWindow(List<DateTime> sorted, TimeSpan window)
    {
        int best = 0, bestI = 0, bestJ = -1, i = 0;
        for (var j = 0; j < sorted.Count; j++)
        {
            while (sorted[j] - sorted[i] > window) i++;
            if (j - i + 1 > best) { best = j - i + 1; bestI = i; bestJ = j; }
        }
        return best == 0 ? (0, null, null) : (best, sorted[bestI], sorted[bestJ]);
    }

    private static string Minutes(TimeSpan t) =>
        t.TotalMinutes == 1 ? "1 minute" : $"{t.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes";

    private static string Describe(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{(int)t.TotalDays} days"
        : t.TotalHours >= 2 ? $"{(int)t.TotalHours} hours"
        : $"{(int)Math.Max(1, t.TotalMinutes)} minutes";
}
