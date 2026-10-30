using System.ComponentModel;
using System.Runtime.CompilerServices;
using EzForensics.Core.Tools;

namespace EzForensics.Core.Execution;

public enum JobState { Queued, Running, Succeeded, Failed, TimedOut, Cancelled, Skipped }

public sealed class Job : INotifyPropertyChanged
{
    private readonly SynchronizationContext? _ctx;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _logLock = new();
    private StreamWriter? _log;
    private JobState _state = JobState.Queued;
    private DateTime? _startUtc, _endUtc;
    private int? _exitCode;
    private string? _exePath, _logPath, _commandLine, _statusReason;

    internal Job(JobSpec spec, string caseDir, SynchronizationContext? ctx)
    {
        Spec = spec;
        CaseDir = caseDir;
        _ctx = ctx;
        Id = Guid.NewGuid();
        QueueSequence = NextSequence();
        _logPath = Path.Combine(caseDir, "logs", $"{Id:N}_{spec.ToolId}.log");
        if (spec.ManifestDetails != null) foreach (var (k, v) in spec.ManifestDetails) _details[k] = v;
    }

    private readonly Dictionary<string, string> _details = new(StringComparer.Ordinal);

    /// <summary>Extra facts recorded in the run manifest (from <see cref="JobSpec.ManifestDetails"/> plus post-processing).</summary>
    public IReadOnlyDictionary<string, string> Details
    {
        get { lock (_details) return new Dictionary<string, string>(_details, StringComparer.Ordinal); }
    }

    internal void SetDetail(string key, string value)
    {
        lock (_details) _details[key] = value;
    }

    public Guid Id { get; }

    private static long s_sequence;
    /// <summary>Process-wide increasing number (also used for events such as a symbol gate decision): orders "queued before / after".</summary>
    internal static long NextSequence() => Interlocked.Increment(ref s_sequence);
    /// <summary>When the job was queued, as a <see cref="NextSequence"/> value.</summary>
    public long QueueSequence { get; }
    public JobSpec Spec { get; }
    public string CaseDir { get; }
    public JobState State
    {
        get => _state;
        internal set { if (Set(ref _state, value)) Raise(nameof(IsFinished)); }
    }
    public DateTime? StartUtc { get => _startUtc; internal set => Set(ref _startUtc, value); }
    public DateTime? EndUtc { get => _endUtc; internal set => Set(ref _endUtc, value); }
    public int? ExitCode { get => _exitCode; internal set => Set(ref _exitCode, value); }
    public string? ExePath { get => _exePath; internal set => Set(ref _exePath, value); }
    public string? LogPath { get => _logPath; internal set => Set(ref _logPath, value); }
    /// <summary>Human-readable explanation of how the job ended (set when it finishes).</summary>
    public string? StatusReason { get => _statusReason; internal set => Set(ref _statusReason, value); }
    public BuildFlavor? Flavor { get; internal set; }
    public string? ExeVersion { get; internal set; }
    /// <summary>Args after token expansion ({toolDir}).</summary>
    public IReadOnlyList<string>? ResolvedArgs { get; internal set; }
    /// <summary>The command line as run (exe + args).</summary>
    public string? CommandLine { get => _commandLine; internal set => Set(ref _commandLine, value); }
    /// <summary>Output files renamed after the run (run-time prefix removed); empty when nothing was renamed.</summary>
    public IReadOnlyList<OutputRename> OutputRenames { get; internal set; } = Array.Empty<OutputRename>();
    /// <summary>Case layout v2: files moved from the staging folder to <see cref="JobSpec.OutputDir"/> (with their artifact ids).</summary>
    public IReadOnlyList<PublishedOutput> PublishedOutputs { get; internal set; } = Array.Empty<PublishedOutput>();
    /// <summary>Input file facts captured before the tool started (<see cref="JobSpec.Provenance"/>).</summary>
    public IReadOnlyList<ManifestInputFact> InputFacts { get; internal set; } = Array.Empty<ManifestInputFact>();
    public string? InputFactsNote { get; internal set; }
    /// <summary>Case layout v2: files this job produced that are still in its staging folder (publishing failed for them).</summary>
    public IReadOnlyList<string> StagingLeftovers { get; internal set; } = Array.Empty<string>();
    /// <summary>The job was cancelled / timed out and its outputs were published with the <c>_PARTIAL</c> qualifier.</summary>
    public bool PartialOutputs { get; internal set; }
    /// <summary>The queue ran this job with <see cref="ToolLogLevel.Debug"/>: its log carries the extra "# Debug:" lines.</summary>
    public bool DebugLogging { get; internal set; }
    /// <summary>This job claimed its staging folder (released when it ends).</summary>
    internal bool OwnsStaging { get; set; }
    /// <summary>This job currently holds a queue (parallelism) slot.</summary>
    internal bool SlotHeld { get; set; }

    private readonly TaskCompletionSource _recorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>
    /// Completes once the job has finished AND its run-manifest record has been written (or the write failed). <see cref="State"/> turns final
    /// before that, so code that reads the manifest from disk (e.g. memory-only computer details reading windows.info details) awaits this.
    /// </summary>
    public Task Recorded => _recorded.Task;
    internal void MarkRecorded() => _recorded.TrySetResult();

    public bool IsFinished => State is JobState.Succeeded or JobState.Failed or JobState.TimedOut or JobState.Cancelled or JobState.Skipped;

    /// <summary>Raised for every output line (any thread). Args: job, line.</summary>
    public event Action<Job, string>? Output;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal CancellationToken Token => _cts.Token;

    internal void OpenLog()
    {
        lock (_logLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath!)!);
            _log = new StreamWriter(new FileStream(_logPath!, FileMode.Create, FileAccess.Write, FileShare.Read),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    internal void Log(string line, bool raise = true, bool write = true)
    {
        // Called from the process output callbacks: a write error (disk full, case drive removed) must never escape there, where it
        // would end the whole app. The line still reaches the Jobs page through Output.
        if (write)
            lock (_logLock)
            {
                try { _log?.WriteLine(line); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        if (raise) Output?.Invoke(this, line);
    }

    internal void CloseLog()
    {
        lock (_logLock) { _log?.Dispose(); _log = null; }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise(string name)
    {
        var h = PropertyChanged;
        if (h == null) return;
        var args = new PropertyChangedEventArgs(name);
        if (_ctx == null || SynchronizationContext.Current == _ctx) h(this, args);
        else _ctx.Post(_ => h(this, args), null);
    }
}
