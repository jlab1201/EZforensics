using System.Collections.Concurrent;
using System.Globalization;
using EzForensics.Core.Execution;

namespace EzForensics.Core.Memory;

/// <summary>Volatility could not resolve the symbols for an image (raised by <see cref="MemorySymbolGate.WaitAsync(CancellationToken)"/> after a failed info run).</summary>
public sealed class MemorySymbolsMissingException : Exception
{
    public MemorySymbolsMissingException(string message) : base(message) { }
}

/// <summary>
/// One per memory image. The <c>windows.info</c> job warms the symbol cache and then opens the gate (or fails it when Volatility reported
/// missing symbols); the image's other plugin jobs wait for it in their PreProcess and fail fast with the same plain message.
/// The gate also caps the number of Volatility processes running on the image at once (<see cref="MaxConcurrent"/>).
/// <para>
/// No infinite wait: the gate knows its info jobs (<see cref="RegisterInfo"/>, called when an info job is enqueued). A waiting plugin job that
/// sees no live info job for <see cref="OrphanGrace"/> (none was queued, or every one ended without a decision) opens the gate itself and runs.
/// </para>
/// </summary>
public sealed class MemorySymbolGate
{
    /// <summary>Soft cap of concurrent vol processes per image.</summary>
    public const int MaxConcurrent = 2;

    /// <summary>Plain message shown when symbols are missing.</summary>
    public const string SymbolsMissingMessage =
        "Volatility could not find the symbols (ISF) for this memory image. Connect to the internet (symbols are downloaded automatically), " +
        "install the Windows symbol pack in Tools, or use Import ISF… to add them, then run the memory jobs again.";

    /// <summary>Status text of a plugin job waiting for the symbol check (followed by ": &lt;info job status&gt;" when known).</summary>
    public const string WaitingForInfoReason = "Waiting for windows.info (symbol check)";
    /// <summary>Status text of a job waiting for one of the image's Volatility process slots.</summary>
    public static readonly string WaitingForSlotReason = $"Waiting for a free Volatility slot (at most {MaxConcurrent} per image)";
    /// <summary>Decision note when no info job was queued for the image.</summary>
    public const string NoInfoJobNote = "No windows.info job is queued for this image; starting without the symbol check";
    /// <summary>Decision note when every info job ended without deciding the gate.</summary>
    public const string InfoEndedNote = "The windows.info job ended without a symbol check result; starting anyway";

    private readonly object _lock = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Guid, byte> _holders = new();
    private readonly List<Job> _infoJobs = new();
    private long _checkSequence;   // QueueSequence of the info job that armed the current check (0 = the first check)
    private string? _infoStage;
    private Guid _infoStageJob;

    public MemorySymbolGate() : this(MaxConcurrent) { }

    public MemorySymbolGate(int maxConcurrent) => _slots = new SemaphoreSlim(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent));

    /// <summary>How often a waiting job re-checks its info job (status text, orphan detection).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a waiting job tolerates "no live info job" before opening the gate itself (covers the enqueue order of one run).</summary>
    public TimeSpan OrphanGrace { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsOpen { get { lock (_lock) return _tcs.Task.IsCompleted && !Failed; } }
    public bool Failed { get; private set; }
    public string? Message { get; private set; }
    /// <summary>How the last open decision came about (e.g. "windows.info finished", <see cref="NoInfoJobNote"/>); null when undecided or failed.</summary>
    public string? DecisionNote { get; private set; }
    /// <summary>True once the gate was opened or failed (until <see cref="Rearm"/>).</summary>
    public bool IsDecided { get { lock (_lock) return _tcs.Task.IsCompleted; } }
    /// <summary><see cref="Job.QueueSequence"/>-ordered moment of the last decision (0 = undecided).</summary>
    public long DecidedSequence { get; private set; }

    /// <summary>The info jobs enqueued for this gate (oldest first).</summary>
    public IReadOnlyList<Job> InfoJobs { get { lock (_lock) return _infoJobs.ToList(); } }

    /// <summary>
    /// An info job was enqueued (called synchronously by <see cref="JobQueue.Enqueue"/> through <see cref="JobSpec.Enqueued"/>): the gate tracks it, and a
    /// decision older than the job is forgotten (<see cref="Rearm"/>), so plugin jobs queued after it wait for the new check.
    /// </summary>
    public void RegisterInfo(Job infoJob)
    {
        ArgumentNullException.ThrowIfNull(infoJob);
        lock (_lock) { if (!_infoJobs.Contains(infoJob)) _infoJobs.Add(infoJob); }
        Rearm(infoJob);
    }

    /// <summary>Volatility progress line of an info job (stderr): updates the status shown by the jobs waiting for it.</summary>
    public void ReportInfoProgress(Job infoJob, string line)
    {
        var stage = StageOf(line);
        if (stage == null) return;
        lock (_lock) { _infoStage = stage; _infoStageJob = infoJob.Id; }
    }

    /// <summary>Plain-language stage of a Volatility progress line ("downloading symbols 45%", "finding the kernel"...); null for other lines.</summary>
    public static string? StageOf(string? line)
    {
        var pct = VolatilityProgressParser.Parse(line);
        if (pct == null) return null;
        var text = VolatilityProgressParser.Status(line) ?? "";
        var p = ((int)Math.Floor(pct.Value)).ToString(CultureInfo.InvariantCulture) + "%";
        if (text.Contains("Download", StringComparison.OrdinalIgnoreCase)) return "downloading symbols " + p;
        if (text.Contains("PDB", StringComparison.OrdinalIgnoreCase)) return "finding the kernel " + p;
        if (text.Contains("Symbol", StringComparison.OrdinalIgnoreCase)) return "reading symbols " + p;
        return "running " + p;
    }

    /// <summary>Status text for a job waiting on this gate: <see cref="WaitingForInfoReason"/> plus what the info job is doing.</summary>
    public string WaitingStatus()
    {
        Job? info;
        string? stage;
        Guid stageJob;
        lock (_lock)
        {
            info = _infoJobs.LastOrDefault(j => j.QueueSequence >= _checkSequence && !j.Recorded.IsCompleted);
            stage = _infoStage;
            stageJob = _infoStageJob;
        }
        if (info == null) return WaitingForInfoReason;
        if (info.State == JobState.Running) return $"{WaitingForInfoReason}: {(stageJob == info.Id && stage != null ? stage : "running")}…";
        if (info.IsFinished) return $"{WaitingForInfoReason}: finishing…";
        return $"{WaitingForInfoReason}: windows.info is queued";
    }

    /// <summary>Waits until the info job has opened or failed the gate; throws <see cref="MemorySymbolsMissingException"/> when it failed.</summary>
    public Task WaitAsync(CancellationToken ct) => WaitAsync(null, ct);

    /// <summary>
    /// Waits until the gate is decided; <paramref name="onStatus"/> receives <see cref="WaitingStatus"/> on every poll. When no live info job is
    /// tracked for <see cref="OrphanGrace"/>, the gate is opened (<see cref="NoInfoJobNote"/> / <see cref="InfoEndedNote"/>): never waits for ever.
    /// Throws <see cref="MemorySymbolsMissingException"/> when the gate failed.
    /// </summary>
    public async Task WaitAsync(Action<string>? onStatus, CancellationToken ct)
    {
        DateTime? orphanSince = null;
        while (true)
        {
            Task t;
            lock (_lock) t = _tcs.Task;
            if (!t.IsCompleted)
            {
                onStatus?.Invoke(WaitingStatus());
                await Task.WhenAny(t, Task.Delay(PollInterval, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!t.IsCompleted)
                {
                    if (HasLiveInfoJob()) orphanSince = null;
                    else
                    {
                        orphanSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - orphanSince.Value >= OrphanGrace) DecideOrphan();
                    }
                    continue;
                }
            }
            lock (_lock)
                if (Failed && ReferenceEquals(t, _tcs.Task)) throw new MemorySymbolsMissingException(Message ?? SymbolsMissingMessage);
            return;
        }
    }

    /// <summary>True while an info job of the current check is still queued or running (its Finally step decides the gate).</summary>
    public bool HasLiveInfoJob()
    {
        lock (_lock) return _infoJobs.Any(j => j.QueueSequence >= _checkSequence && !j.Recorded.IsCompleted);
    }

    private void DecideOrphan()
    {
        lock (_lock)
        {
            if (_tcs.Task.IsCompleted || _infoJobs.Any(j => j.QueueSequence >= _checkSequence && !j.Recorded.IsCompleted)) return;
            DecideLocked(open: true, _infoJobs.Count == 0 ? NoInfoJobNote : InfoEndedNote, null);
        }
    }

    /// <summary>
    /// Plugin job queued after the gate failed (Retry in the Jobs list on Ingest, Run selected again after importing symbols): the failure is from an
    /// earlier symbol check, so the job must not fail fast on it.
    /// </summary>
    public bool FailedBefore(Job job)
    {
        lock (_lock) return Failed && _tcs.Task.IsCompleted && DecidedSequence < job.QueueSequence;
    }

    /// <summary>
    /// A new info job starts a new symbol check: a decision taken before <paramref name="infoJob"/> was queued is forgotten and the plugin jobs
    /// queued with it wait for the new one; older info jobs can no longer decide. No effect while undecided or when the decision is newer than the job.
    /// </summary>
    public void Rearm(Job infoJob)
    {
        lock (_lock)
        {
            if (!_tcs.Task.IsCompleted || DecidedSequence > infoJob.QueueSequence) return;
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Failed = false;
            Message = null;
            DecisionNote = null;
            DecidedSequence = 0;
            _checkSequence = Math.Max(_checkSequence, infoJob.QueueSequence);
        }
    }

    /// <summary>Lets the waiting plugin jobs run. No effect once decided.</summary>
    public void Open() => Open(null, null);

    /// <summary>
    /// Opens the gate on behalf of <paramref name="by"/> (an info job; null = the planner). No effect once decided, or when <paramref name="by"/> is older
    /// than the info job that armed the current check (a stale run must not decide a newer check).
    /// </summary>
    public void Open(Job? by, string? note)
    {
        lock (_lock)
        {
            if (_tcs.Task.IsCompleted || (by != null && by.QueueSequence < _checkSequence)) return;
            DecideLocked(open: true, note, null);
        }
    }

    /// <summary>Fails the waiting plugin jobs with <paramref name="message"/>. No effect once decided.</summary>
    public void Fail(string message) => Fail(message, null);

    /// <summary>Fails the gate on behalf of <paramref name="by"/> (same rules as <see cref="Open(Job?, string?)"/>).</summary>
    public void Fail(string message, Job? by)
    {
        lock (_lock)
        {
            if (_tcs.Task.IsCompleted || (by != null && by.QueueSequence < _checkSequence)) return;
            DecideLocked(open: false, null, message);
        }
    }

    private void DecideLocked(bool open, string? note, string? message)
    {
        Failed = !open;
        Message = open ? null : message;
        DecisionNote = open ? note : null;
        DecidedSequence = Job.NextSequence();
        _tcs.TrySetResult();
    }

    /// <summary>True when Volatility's stderr says the symbol tables could not be resolved.</summary>
    public static bool IsSymbolError(string? line) =>
        line != null && (line.Contains("Unsatisfied requirement", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("symbol_table_name", StringComparison.OrdinalIgnoreCase));
    /// <summary>Takes a slot without waiting; false when all slots are busy (race-free, unlike checking <see cref="Running"/>).</summary>
    public bool TryAcquireSlot(Job job)
    {
        if (_holders.ContainsKey(job.Id)) return true;
        if (!_slots.Wait(0)) return false;
        if (!_holders.TryAdd(job.Id, 0)) _slots.Release();
        return true;
    }

    /// <summary>Takes one of the image's process slots for <paramref name="job"/> (released by <see cref="ReleaseSlot"/>).</summary>
    public async Task AcquireSlotAsync(Job job, CancellationToken ct)
    {
        if (_holders.ContainsKey(job.Id)) return;
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        if (!_holders.TryAdd(job.Id, 0)) _slots.Release();
    }

    /// <summary>Releases <paramref name="job"/>'s slot (no effect when it holds none).</summary>
    public void ReleaseSlot(Job job)
    {
        if (_holders.TryRemove(job.Id, out _)) _slots.Release();
    }

    /// <summary>Number of vol processes currently holding a slot.</summary>
    public int Running => _holders.Count;

    /// <summary>"12 s", "3 min 05 s".</summary>
    public static string FormatElapsed(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))} s";
}
