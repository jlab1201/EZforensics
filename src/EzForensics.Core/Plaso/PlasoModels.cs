namespace EzForensics.Core.Plaso;

/// <summary>How plaso is started.</summary>
public enum PlasoEngineKind
{
    /// <summary>log2timeline.exe / psort.exe console scripts (pip venv, PyInstaller build).</summary>
    Executable,
    /// <summary><c>python -m plaso.scripts.log2timeline</c> or <c>python log2timeline.py</c>.</summary>
    Python,
    /// <summary><c>docker run log2timeline/plaso log2timeline ...</c>.</summary>
    Docker,
}

/// <summary>Where the install was found (discovery order).</summary>
public enum PlasoOrigin { Configured, ManagedVenv, Path, Docker }

/// <summary>An executable plus the arguments that always precede the tool arguments.</summary>
public sealed record PlasoCommand(string Exe, IReadOnlyList<string> PrefixArgs)
{
    public static PlasoCommand Of(string exe, params string[] prefix) => new(exe, prefix);
}

/// <summary>A usable plaso installation.</summary>
public sealed record PlasoInstall(
    PlasoOrigin Origin,
    PlasoEngineKind Kind,
    PlasoCommand Log2Timeline,
    PlasoCommand Psort,
    string? Version,
    string Location,
    string DockerImage = PlasoLocator.DockerImage)
{
    public string KindLabel => Kind switch
    {
        PlasoEngineKind.Executable => "Executables",
        PlasoEngineKind.Python => "Python module",
        _ => "Docker",
    };

    public string OriginLabel => Origin switch
    {
        PlasoOrigin.Configured => "configured path",
        PlasoOrigin.ManagedVenv => "EZ Forensics managed venv",
        PlasoOrigin.Path => "PATH",
        _ => "Docker image " + DockerImage,
    };

    /// <summary>"20260720" style version as a number (0 when unknown).</summary>
    public long VersionNumber => PlasoLocator.VersionNumber(Version);

    public string Summary => $"plaso {Version ?? "(unknown version)"} · {KindLabel} · {OriginLabel}";
}

/// <summary>Result of <see cref="PlasoLocator.Discover"/>.</summary>
public sealed record PlasoDiscovery(PlasoInstall? Selected, IReadOnlyList<string> Notes, bool DockerAvailable, bool DockerImagePresent)
{
    public bool IsAvailable => Selected != null;
}

/// <summary>A Python interpreter found on this machine.</summary>
public sealed record PythonInfo(string Exe, Version Version, string Origin)
{
    public const string PrivateOrigin = "EZ Forensics private Python";

    /// <summary>3.10 - 3.13: every plaso dependency has Windows wheels for these.</summary>
    public bool IsSupported => Version >= PlasoLocator.MinPython && Version <= PlasoLocator.MaxPython;
    public bool IsPrivate => Origin == PrivateOrigin;
}

public enum PlasoOutputFormat
{
    /// <summary>psort <c>dynamic</c>: ISO 8601 <c>datetime</c> (sortable), macb, source, message, parser... (plaso's recommended format).</summary>
    Dynamic,
    /// <summary>psort <c>l2tcsv</c>: the legacy 17-column log2timeline CSV (date,time,timezone,MACB,...); second precision.</summary>
    L2tCsv,
}

public enum PlasoVssMode
{
    /// <summary>Do not process Volume Shadow Copies (<c>--vss_stores none</c>, or <c>--no_vss</c> on old versions).</summary>
    None,
    /// <summary>Process every snapshot (<c>--vss_stores all</c>).</summary>
    All,
}

/// <summary>Options of one plaso super-timeline run.</summary>
public sealed class PlasoRunOptions
{
    /// <summary>Raw evidence: folder (KAPE target output, mounted drive) or image file (E01, raw/dd, VHD(X), VMDK...).</summary>
    public string Source { get; set; } = "";
    /// <summary>
    /// Further evidence folders / images of the same timeline (e.g. the extracted roots of several archives). log2timeline takes one source
    /// per run, so each one is processed by its own log2timeline run that appends a new session to the same storage file (plaso opens an
    /// existing storage file for appending); psort then exports all sessions at once. Empty = <see cref="Source"/> only.
    /// </summary>
    public IReadOnlyList<string> AdditionalSources { get; set; } = Array.Empty<string>();
    /// <summary><see cref="Source"/> followed by <see cref="AdditionalSources"/>.</summary>
    public IReadOnlyList<string> AllSources => new[] { Source }.Concat(AdditionalSources).ToList();
    public string CaseDir { get; set; } = "";
    /// <summary>UTC stamp used in the file names (default now).</summary>
    public string Stamp { get; set; } = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
    /// <summary>Parser filter expression / preset ("win7", "win_gen,!filestat"); null or empty = plaso's auto selection.</summary>
    public string? Parsers { get; set; }
    /// <summary>Pass <c>--timezone UTC</c> for timestamps stored without a time zone.</summary>
    public bool TimezoneUtc { get; set; } = true;
    public int? Workers { get; set; }
    public PlasoVssMode Vss { get; set; } = PlasoVssMode.None;
    /// <summary><c>--unattended</c>: never prompt (stdin is closed anyway), fail instead.</summary>
    public bool Unattended { get; set; } = true;
    public PlasoOutputFormat Format { get; set; } = PlasoOutputFormat.Dynamic;
    /// <summary>psort event filter bounds (inclusive, UTC).</summary>
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    /// <summary>
    /// Folder of the timeline output (the CSV, and <c>plaso\</c> with the storage file and logs); null or empty =
    /// <c>&lt;case&gt;\Timeline</c>. The Ingest page sets the host's folder, <c>&lt;case&gt;\&lt;Host&gt;\Timeline</c>.
    /// </summary>
    public string? OutputDir { get; set; }
    /// <summary>Additional log2timeline arguments (Tools page, <see cref="Commands.ExtraArgsStore.PlasoLog2Timeline"/>), placed before the source.</summary>
    public IReadOnlyList<string> ExtraLog2TimelineArgs { get; set; } = Array.Empty<string>();
    /// <summary>Additional psort arguments (<see cref="Commands.ExtraArgsStore.PlasoPsort"/>), placed before <c>-w</c> and the storage file.</summary>
    public IReadOnlyList<string> ExtraPsortArgs { get; set; } = Array.Empty<string>();
    /// <summary>Provenance recorded on the log2timeline and psort jobs (host folder, system root...); null = none.</summary>
    public Execution.JobProvenance? Provenance { get; set; }

    public string TimelineDir => !string.IsNullOrWhiteSpace(OutputDir) ? OutputDir! : Path.Combine(CaseDir, Timeline.TimelineFiles.TimelineFolderName);
    public string PlasoDir => Path.Combine(TimelineDir, "plaso");
    public string StorageFile => Path.Combine(PlasoDir, Stamp + ".plaso");
    public string OutputCsv => Path.Combine(TimelineDir, $"{Timeline.TimelineFiles.OutputPrefix}plaso_{Stamp}.csv");
    public string Log2TimelineLog => Path.Combine(PlasoDir, Stamp + ".log2timeline.log.gz");
    public string PsortLog => Path.Combine(PlasoDir, Stamp + ".psort.log.gz");
    public bool SourceIsFile => File.Exists(Source);
    /// <summary>log2timeline log of the <paramref name="index"/>-th source (0 = <see cref="Log2TimelineLog"/>, then <c>&lt;stamp&gt;.log2timeline-2.log.gz</c>...).</summary>
    public string Log2TimelineLogFor(int index) => index <= 0 ? Log2TimelineLog : Path.Combine(PlasoDir, $"{Stamp}.log2timeline-{index + 1}.log.gz");

    /// <summary>Separator between several sources in the plaso source text box.</summary>
    public const string SourceSeparator = "; ";

    /// <summary>Several sources as one line for the source text box (<c>a; b</c>).</summary>
    public static string JoinSources(IEnumerable<string> sources) =>
        string.Join(SourceSeparator, sources.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));

    /// <summary>
    /// The source text box value as a list: the whole text when it names an existing folder / file (a path may contain ';') or has no ';',
    /// else the ';'-separated parts (trimmed, empty parts dropped, duplicates removed). Existence of the parts is not checked.
    /// </summary>
    public static IReadOnlyList<string> ParseSources(string? text, Func<string, bool>? exists = null)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0) return Array.Empty<string>();
        exists ??= p => Directory.Exists(p) || File.Exists(p);
        if (!t.Contains(';') || exists(t)) return new[] { t };
        return t.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>One process to start: exe + argv (ArgumentList) and the cleanup command for cancel.</summary>
public sealed record PlasoInvocation(string Exe, IReadOnlyList<string> Args, string? ContainerName, IReadOnlyList<string>? CancelCommand)
{
    public string Display => PlasoCommandBuilder.Display(Exe, Args);
}

public enum PlasoPhase { Starting, Extracting, Exporting, Done }

/// <summary>Parsed linear status line (log2timeline / psort <c>--status_view linear</c>).</summary>
public sealed record PlasoStatus(PlasoPhase Phase, string? Status, string? ProcessingTime, long? Events, long? EventData, string? CurrentFile, int? Warnings)
{
    public override string ToString()
    {
        var parts = new List<string> { Phase == PlasoPhase.Exporting ? "psort: exporting" : Phase == PlasoPhase.Extracting ? "log2timeline: " + (Status ?? "extracting") : Phase.ToString() };
        if (ProcessingTime != null) parts.Add(ProcessingTime);
        if (Events != null) parts.Add($"{Events:N0} events");
        if (!string.IsNullOrEmpty(CurrentFile)) parts.Add(CurrentFile!);
        return string.Join("  ·  ", parts);
    }
}

public sealed class PlasoRunResult
{
    public bool Succeeded { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public string StorageFile { get; set; } = "";
    public string OutputCsv { get; set; } = "";
    public long Events { get; set; }
    public long ExtractedEvents { get; set; }
    public int? Warnings { get; set; }
    public TimeSpan ExtractElapsed { get; set; }
    public TimeSpan ExportElapsed { get; set; }
    public List<Execution.Job> Jobs { get; } = new();
}
