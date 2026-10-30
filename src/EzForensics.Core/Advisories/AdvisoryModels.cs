namespace EzForensics.Core.Advisories;

/// <summary>How the evidence the tool parsed was acquired. Local to the advisories engine on purpose
/// (Triage defines its own AcquisitionKind; map between them at the call site).</summary>
public enum EvidenceAcquisition
{
    Unknown,
    /// <summary>Tools ran directly against a live, running volume.</summary>
    LiveVolume,
    /// <summary>A mounted forensic image or a raw (sector/NTFS-level) copy: file-system times are preserved.</summary>
    MountedImageOrRawCopy,
    /// <summary>Files were copied out (KAPE, robocopy, manual copy, zip ...): file-system Created/Accessed times are usually the copy time.</summary>
    CopiedCollection,
    /// <summary>Files were extracted from an archive (zip, 7z, tar ...): treated as a declared copy; only some times were restored from the archive.</summary>
    ExtractedArchive,
}

public enum AdvisorySeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>File-system timestamps of one input file the tool parsed (e.g. one .pf / .lnk), as seen at parse time.</summary>
public sealed record InputTimestampFact(string Path, DateTime? CreatedUtc, DateTime? ModifiedUtc, DateTime? AccessedUtc = null);

/// <summary>Everything the advisor knows about one CSV the user opened.</summary>
public sealed record AdvisoryContext
{
    /// <summary>Advisory artifact id (e.g. "prefetch", "lnk"); null = identify from headers / file name.</summary>
    public string? ArtifactId { get; init; }
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    public string CsvFileName { get; init; } = "";
    public EvidenceAcquisition Acquisition { get; init; } = EvidenceAcquisition.Unknown;
    /// <summary>File-system times of the input files the tool parsed; null/empty = not recorded.</summary>
    public IReadOnlyList<InputTimestampFact>? InputTimestampFacts { get; init; }
    /// <summary>A registry hive was dirty and its transaction logs were replayed (RECmd/AmcacheParser/AppCompatCacheParser).</summary>
    public bool HiveWasDirtyReplayed { get; init; }
    /// <summary>An ESE database (SRUDB.dat) was dirty / not cleanly shut down.</summary>
    public bool EseDatabaseDirty { get; init; }
    /// <summary>For <see cref="EvidenceAcquisition.ExtractedArchive"/>: ModifiedOnly, All, Mixed or DosLocal (null = unknown, treated as ModifiedOnly).</summary>
    public string? ArchiveTimestampCoverage { get; init; }
    /// <summary>Memory image format name (MemoryFormat: Raw, LiME, VMware, Hibernation, WindowsCrashDump ...); null = unknown.</summary>
    public string? MemoryFormat { get; init; }
    /// <summary>Crash dump kind (DumpKind: Full, Kernel, Bitmap, BitmapFull, BitmapKernel, Active); null = not a crash dump / unknown.</summary>
    public string? MemoryDumpKind { get; init; }
    /// <summary>
    /// Crash dumps only: true when the dump was written from a running system (bug check code 0 = made by an acquisition tool,
    /// 0x161 LIVE_SYSTEM_DUMP), false when Windows wrote it at a real bug check with the system halted (no page smear);
    /// null = unknown (treated as live).
    /// </summary>
    public bool? MemoryLiveDump { get; init; }
    /// <summary>RITA outputs: hours between the first and last analysed connection (manifest detail rita.dataSpanHours); null = unknown.</summary>
    public double? RitaDataSpanHours { get; init; }
}

/// <summary>One plain-language advisory for the banner / per-column warning icons.</summary>
public sealed record Advisory
{
    public required string Id { get; init; }
    public AdvisorySeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    /// <summary>Columns (as spelled in the CSV header) whose values this advisory casts doubt on / explains.</summary>
    public IReadOnlyList<string> AffectedColumns { get; init; } = Array.Empty<string>();
    /// <summary>Columns (as spelled in the CSV header) that remain reliable under this advisory's condition.</summary>
    public IReadOnlyList<string> ReliableColumns { get; init; } = Array.Empty<string>();
    public string? ReferenceUrl { get; init; }
}

public enum CopyConfidence
{
    None,
    Medium,
    High,
}

/// <summary>Result of the copy-detection heuristics over <see cref="AdvisoryContext.InputTimestampFacts"/>.</summary>
public sealed record CopyAssessment
{
    public bool Suspected { get; init; }
    public CopyConfidence Confidence { get; init; }
    /// <summary>True when the acquisition was declared as <see cref="EvidenceAcquisition.CopiedCollection"/>.</summary>
    public bool Declared { get; init; }
    /// <summary>Inputs with both Created and Modified times (the heuristics' sample size).</summary>
    public int SampleSize { get; init; }
    public int CreatedAfterModified { get; init; }
    public bool CreatedAfterModifiedHit { get; init; }
    /// <summary>Largest number of Created times inside one cluster window.</summary>
    public int CreatedClustered { get; init; }
    public bool CreatedClusterHit { get; init; }
    public DateTime? ClusterStartUtc { get; init; }
    public DateTime? ClusterEndUtc { get; init; }
    public TimeSpan? ModifiedSpan { get; init; }
    /// <summary>Plain-language evidence statement ("23 of 25 prefetch files have Created later than Modified ..."); empty when nothing was found.</summary>
    public string Summary { get; init; } = "";

    public static CopyAssessment NotSuspected { get; } = new();
}

/// <summary>Full advisor output for one CSV.</summary>
public sealed record AdvisoryReport
{
    /// <summary>Identified advisory artifact id, or null when the CSV was not recognised.</summary>
    public string? ArtifactId { get; init; }
    public string? ArtifactLabel { get; init; }
    public CopyAssessment Copy { get; init; } = CopyAssessment.NotSuspected;
    /// <summary>Sorted by severity (highest first), then rule order.</summary>
    public IReadOnlyList<Advisory> Advisories { get; init; } = Array.Empty<Advisory>();

    public AdvisorySeverity? HighestSeverity => Advisories.Count == 0 ? null : Advisories.Max(a => a.Severity);

    /// <summary>Highest severity per affected column (case-insensitive keys) for per-column icons.</summary>
    public IReadOnlyDictionary<string, AdvisorySeverity> ColumnSeverities
    {
        get
        {
            var map = new Dictionary<string, AdvisorySeverity>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in Advisories)
                foreach (var c in a.AffectedColumns)
                    if (!map.TryGetValue(c, out var s) || a.Severity > s) map[c] = a.Severity;
            return map;
        }
    }

    /// <summary>Advisories that mention the column (for the column-header tooltip).</summary>
    public IEnumerable<Advisory> ForColumn(string column) =>
        Advisories.Where(a => a.AffectedColumns.Contains(column, StringComparer.OrdinalIgnoreCase));
}
