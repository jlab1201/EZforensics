namespace EzForensics.Core.Execution;

/// <summary>A fully-resolved request to run one EZ tool once.</summary>
/// <param name="ExePath">Run this executable directly instead of resolving <paramref name="ExeName"/> through the tool locator
/// (used for non-EZ engines such as plaso, Python or Docker).</param>
/// <param name="Timeout">Per-job timeout overriding the queue timeout (<see cref="System.Threading.Timeout.InfiniteTimeSpan"/> = none).</param>
/// <param name="CancelCommand">argv (exe first) run when the job is cancelled or times out, after the process tree is killed
/// (e.g. <c>docker stop &lt;name&gt;</c>, because killing the docker client does not stop the container).</param>
/// <param name="WorkingDir">Working directory (default: the folder that holds the exe).</param>
/// <param name="Environment">Extra environment variables for the process (e.g. PYTHONUNBUFFERED=1).</param>
/// <param name="ExeVersion">Version recorded in the manifest when <paramref name="ExePath"/> is used.</param>
/// <param name="PostProcess">Runs on the job's thread after the process exits (not after a cancel or timeout) and before the
/// outputs are classified and hashed, e.g. converting Zeek logs to CSV. An exception fails the job; cancellation cancels it.</param>
/// <param name="ManifestDetails">Extra facts recorded in the run manifest (engine, image digest, input hashes...).</param>
/// <param name="PreProcess">Runs on the job's thread after the output folder exists and before the process starts (e.g. building
/// a temporary read-only view of the evidence). An exception fails the job without starting the process; cancellation cancels it.</param>
/// <param name="Finally">Always runs once when the job ends (success, failure, cancel or timeout), e.g. deleting temporary
/// copies. Exceptions are logged and ignored.</param>
/// <param name="Output">Case layout v2 (<c>&lt;case&gt;\&lt;Host&gt;\&lt;Category&gt;\&lt;Artifact&gt;[_&lt;Qualifier&gt;].csv</c>): the tool writes
/// into <see cref="OutputPlan.StagingDir"/> and, after the run, every new file is moved into <paramref name="OutputDir"/> under its
/// convention name (<see cref="OutputPublisher"/>). Applies to every job (also <paramref name="ExePath"/> jobs and whatever
/// <see cref="JobQueue.StripTimestampPrefix"/> says). Null = legacy behaviour (tool writes into <paramref name="OutputDir"/>).</param>
/// <param name="Provenance">Where the job's input comes from (rule, host, system root, acquisition) and which input files to record
/// facts for (<see cref="ManifestJob.InputFacts"/>, captured before the tool starts).</param>
/// <param name="PrepareOutsideSlot">Run <paramref name="PreProcess"/> without holding a queue slot: the job shows "Waiting…" while it is
/// blocked there, takes a slot afterwards and measures its start time from then (memory jobs waiting for the symbol check).</param>
/// <param name="Enqueued">Called synchronously by <see cref="JobQueue.Enqueue"/> with the new job, before it can start (e.g. the memory
/// windows.info job registers itself with its image's symbol gate). Exceptions are ignored.</param>
/// <param name="StdoutPath">When set, the process's raw stdout is written to this file byte for byte (e.g. Volatility's CSV); only stderr
/// goes to the job log and <see cref="Job.Output"/>. Null = stdout is logged as usual.</param>
/// <param name="Hidden">Runs through the queue (ordering, Exclusive, Cancel, manifest) but is not listed in the Jobs list and not counted in the Jobs badge; its page shows its own progress.</param>
/// <param name="InProcess">Makes this an in-process job: no tool is located and no process is started; the queue runs this delegate in the job's
/// slot (<see cref="Exclusive"/>, parallelism, Cancel, log, manifest record and Jobs list all work as for any job). The delegate logs through
/// <see cref="JobPostProcessContext.Log"/> and may set <see cref="JobPostProcessContext.Summary"/>; an exception fails the job, cancellation cancels it
/// (and runs <paramref name="CancelCommand"/> if set). <paramref name="ExeName"/>, <paramref name="Args"/> and the output settings are ignored.</param>
public sealed record JobSpec(
    string ToolId,
    string ExeName,
    IReadOnlyList<string> Args,
    string OutputDir,
    string Label,
    string Category,
    bool Exclusive = false,
    string? ExePath = null,
    TimeSpan? Timeout = null,
    IReadOnlyList<string>? CancelCommand = null,
    string? WorkingDir = null,
    string? ExeVersion = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    Func<JobPostProcessContext, CancellationToken, Task>? PostProcess = null,
    IReadOnlyDictionary<string, string>? ManifestDetails = null,
    Func<JobPostProcessContext, CancellationToken, Task>? PreProcess = null,
    Action<Job>? Finally = null,
    OutputPlan? Output = null,
    JobProvenance? Provenance = null,
    string? StdoutPath = null,
    bool PrepareOutsideSlot = false,
    Action<Job>? Enqueued = null,
    Func<JobPostProcessContext, CancellationToken, Task>? InProcess = null,
    bool Hidden = false);

/// <summary>
/// New-layout output of one job: the tool writes into <see cref="StagingDir"/> (a hidden <c>&lt;case&gt;\.ezstaging\...</c> folder, so
/// parallel jobs sharing one category folder never see each other's files) and <see cref="OutputPublisher"/> moves the results into
/// the job's <see cref="JobSpec.OutputDir"/> as <c>&lt;ArtifactName&gt;[_&lt;Variant&gt;][_&lt;Qualifier&gt;].&lt;ext&gt;</c>.
/// </summary>
/// <param name="StagingDir">Folder the tool writes to (its <c>{out}</c>).</param>
/// <param name="ArtifactName">Base name, e.g. <c>Prefetch</c>, <c>Registry</c>, <c>LNK</c> (must satisfy <see cref="Platforms.ArtifactOutputLayout.IsValidOutputName"/> + ".csv").</param>
/// <param name="ArtifactId">Default artifact id recorded for files the rename table does not classify (e.g. <c>prefetch</c>).</param>
/// <param name="Qualifier">Already sanitised qualifier (alphanumeric parts joined by '_', e.g. <c>jdoe</c>, <c>NTUSER_jdoe</c>); null = none.</param>
/// <param name="FixedFileName">File name passed to the tool with <c>--csvf</c> (kept as is); null when the tool names its own files.</param>
public sealed record OutputPlan(string StagingDir, string ArtifactName, string ArtifactId, string? Qualifier = null, string? FixedFileName = null)
{
    /// <summary>Name of the hidden case-level folder that holds the per-job staging folders.</summary>
    public const string StagingFolderName = ".ezstaging";
}

/// <summary>Input file selection for <see cref="ManifestJob.InputFacts"/>: a file, or the files of a folder matching a mask.</summary>
public sealed record InputSelector(string Path, string? Mask = null, bool Recurse = false);

/// <summary>Where a job's evidence comes from (recorded in the run manifest; drives the case index and the advisories).</summary>
public sealed record JobProvenance
{
    /// <summary>Triage rule id (<c>prefetch</c>, <c>registry</c>, <c>persistence</c>, <c>pcap</c>...).</summary>
    public string RuleId { get; init; } = "";
    /// <summary>Default artifact id of the job's output (per-file ids come from the rename table).</summary>
    public string? ArtifactId { get; init; }
    /// <summary>Host folder name (<c>&lt;case&gt;\&lt;Host&gt;</c>).</summary>
    public string? Host { get; init; }
    /// <summary>"SYSTEM hive ComputerName", "folder name", "default", "live system"; null when unknown.</summary>
    public string? HostNameSource { get; init; }
    public string? SystemRoot { get; init; }
    /// <summary>Triage <c>AcquisitionKind</c> as text: LiveVolume, MountedImageOrRawCopy, CopiedCollection (null = unknown).</summary>
    public string? AcquisitionKind { get; init; }
    /// <summary>Platforms <c>OsFamily</c> as text (Windows, Linux, Unknown...).</summary>
    public string? OsFamily { get; init; }
    /// <summary>Inputs whose file-system facts are recorded before the tool starts (empty = none).</summary>
    public IReadOnlyList<InputSelector> Inputs { get; init; } = Array.Empty<InputSelector>();
    /// <summary>Also SHA-256 the inputs (files up to <see cref="InputFacts.MaxHashFileBytes"/>, at most <see cref="InputFacts.MaxHashBytesPerJob"/> per job).</summary>
    public bool HashInputs { get; init; } = true;
    /// <summary>Hash even on a live volume (off by default: reading content can update last-access times there).</summary>
    public bool HashOnLiveVolume { get; init; }
    /// <summary>Archive the evidence was extracted from (null = none).</summary>
    public Archives.ArchiveOrigin? Archive { get; init; }

    public const string LiveVolume = "LiveVolume";
    public bool IsLiveVolume => string.Equals(AcquisitionKind, LiveVolume, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a <see cref="JobSpec.PostProcess"/> step can see and do.</summary>
public sealed class JobPostProcessContext
{
    internal JobPostProcessContext(Job job, int exitCode) { Job = job; ExitCode = exitCode; }

    public Job Job { get; }
    /// <summary>Exit code of the process.</summary>
    public int ExitCode { get; }
    /// <summary>Folder the tool wrote to: the staging folder for new-layout jobs (<see cref="JobSpec.Output"/>), else <see cref="JobSpec.OutputDir"/>.</summary>
    public string OutputDir => Job.Spec.Output?.StagingDir ?? Job.Spec.OutputDir;
    /// <summary>Writes a line to the job log (and the live output).</summary>
    public void Log(string line) => Job.Log(line);
    /// <summary>Adds or replaces a fact recorded in the run manifest's <c>details</c>.</summary>
    public void SetDetail(string key, string value) => Job.SetDetail(key, value);
    /// <summary>Tool logging is set to Debug (Options > Installation): write the extra "# Debug:" detail (<see cref="ToolDebugLog"/>).</summary>
    public bool Debug => Job.DebugLogging;
    /// <summary>In-process jobs (<see cref="JobSpec.InProcess"/>): short result text shown as the job's status when it succeeds.</summary>
    public string? Summary { get; set; }
}
