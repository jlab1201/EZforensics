using System.Collections.ObjectModel;
using EzForensics.Core.Tools;

namespace EzForensics.Core.Execution;

public sealed class JobQueue
{
    private readonly IToolLocator _locator;
    private volatile int _maxParallel;
    private long _timeoutTicks;
    private readonly SynchronizationContext? _ctx;
    private readonly object _gate = new();
    private int _running;
    private bool _heavyRunning;   // at most one Exclusive (heavy) job at a time; light jobs run alongside it within MaxParallel
    private TaskCompletionSource _changed = NewSignal();
    private readonly SemaphoreSlim _order = new(1, 1);
    private readonly SemaphoreSlim _heavyOrder = new(1, 1);
    private readonly List<Task> _tasks = new();
    private readonly List<Job> _all = new();
    private readonly Dictionary<string, ManifestWriter> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    // staging folders of running layout v2 jobs: two jobs must never share one (a second run of the same plan fails instead)
    private readonly HashSet<string> _activeStaging = new(StringComparer.OrdinalIgnoreCase);
    private string? _sourcePath;
    private IReadOnlyList<string>? _sourceRoots;

    public JobQueue(IToolLocator locator, int maxParallel, TimeSpan timeout)
    {
        _locator = locator;
        _maxParallel = Math.Max(1, maxParallel);
        _timeoutTicks = timeout.Ticks;
        _ctx = SynchronizationContext.Current;
    }

    /// <summary>Status text of a job blocked in its preparation step (<see cref="JobSpec.PrepareOutsideSlot"/>).</summary>
    public const string WaitingReason = "Waiting…";

    /// <summary>Status text of a <see cref="JobSpec.PrepareOutsideSlot"/> job that finished its preparation and waits for a queue slot.</summary>
    public const string WaitingForSlotReason = "Waiting for a free job slot";

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Max concurrent jobs. Can be changed at any time; affects jobs that have not started yet.</summary>
    public int MaxParallel
    {
        get => _maxParallel;
        set { _maxParallel = Math.Max(1, value); Signal(); }
    }

    /// <summary>Per-job timeout. Can be changed at any time; affects jobs that have not started yet.</summary>
    public TimeSpan Timeout
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _timeoutTicks));
        set => Interlocked.Exchange(ref _timeoutTicks, value.Ticks);
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_gate) { old = _changed; _changed = NewSignal(); }
        old.TrySetResult();
    }

    public ObservableCollection<Job> Jobs { get; } = new();

    /// <summary>Preferred flavor passed to the locator (null = automatic).</summary>
    public BuildFlavor? PreferredFlavor { get; set; }

    /// <summary>
    /// Remove the EZ run-time prefix (<c>20261007123456_</c>) from the files each EZ tool job creates (default true).
    /// Read when a job starts. Jobs that run an explicit exe (<see cref="JobSpec.ExePath"/>, e.g. plaso) are never renamed.
    /// </summary>
    public bool StripTimestampPrefix { get; set; } = true;

    /// <summary>Tool logging (Options > Installation): <see cref="ToolLogLevel.Debug"/> adds <see cref="ToolDebugLog"/> lines to every job log. Read when a job starts.</summary>
    public ToolLogLevel LogLevel { get; set; } = ToolLogLevel.Normal;

    /// <summary>Source path recorded in the manifests (the path the user chose). Setting it clears <see cref="SourceRoots"/>.</summary>
    public string? SourcePath
    {
        get => _sourcePath;
        set => SetSource(value, null);
    }

    /// <summary>
    /// The folders the jobs actually read when they differ from <see cref="SourcePath"/> (e.g. the extracted roots of archived evidence);
    /// null = the source path itself.
    /// </summary>
    public IReadOnlyList<string>? SourceRoots => _sourceRoots;

    /// <summary>Sets <see cref="SourcePath"/> and <see cref="SourceRoots"/> together (recorded in the manifests as sourcePath / sourceRoots).</summary>
    public void SetSource(string? path, IReadOnlyList<string>? roots)
    {
        var r = roots is { Count: > 0 } ? roots.ToList() : null;
        lock (_lock)
        {
            _sourcePath = path;
            _sourceRoots = r;
            foreach (var m in _manifests.Values) { m.SourcePath = path; m.SourceRoots = r; }
        }
    }

    /// <summary>Manifest path for a case dir (null if no job was enqueued for it yet).</summary>
    public string? GetManifestPath(string caseDir)
    {
        lock (_lock) return _manifests.TryGetValue(Path.GetFullPath(caseDir), out var m) ? m.Path : null;
    }

    public Job Enqueue(JobSpec spec, string caseDir)
    {
        caseDir = Path.GetFullPath(caseDir);
        var job = new Job(spec, caseDir, _ctx);
        lock (_lock) { _all.Add(job); GetManifest(caseDir).NoteOrigin(spec); }
        if (spec.Output != null)
        {
            // A case that receives layout v2 output says so (case.json; never rewritten once present).
            try { Case.CaseLayout.EnsureMarker(caseDir, Case.CaseLayout.CurrentVersion); } catch (Exception) { }
        }
        if (spec.Enqueued != null)
        {
            try { spec.Enqueued(job); }
            catch (Exception) { }   // best effort: never blocks the job from being queued
        }
        PostOrRun(() => Jobs.Add(job));
        // Take the queue position synchronously, in Enqueue order, so jobs start FIFO (Task.Run alone races).
        var acquire = AcquireAsync(spec.Exclusive, job.Token);
        var t = Task.Run(() => RunJobAsync(job, acquire));
        lock (_lock) _tasks.Add(t);
        return job;
    }

    /// <summary>
    /// Lists a job that could not be started (its tool is missing, its source is gone...) as already finished on the Jobs page, with the
    /// reason as its status and log, and records it in the run manifest. Nothing is run.
    /// </summary>
    public Job EnqueueRejected(JobSpec spec, string caseDir, string reason, JobState state = JobState.Failed)
    {
        caseDir = Path.GetFullPath(caseDir);
        var job = new Job(spec, caseDir, _ctx);
        lock (_lock) { _all.Add(job); GetManifest(caseDir).NoteOrigin(spec); }
        try
        {
            job.OpenLog();
            job.Log("# Not started: " + reason);
        }
        catch (Exception) { }
        finally { job.CloseLog(); }
        var now = DateTime.UtcNow;
        job.StartUtc = now;
        job.EndUtc = now;
        job.StatusReason = reason;
        job.State = state;
        PostOrRun(() => Jobs.Add(job));
        try { GetManifest(caseDir).RecordAndWrite(job); } catch { }
        job.MarkRecorded();
        return job;
    }

    public async Task WhenAllAsync()
    {
        while (true)
        {
            Task[] snap;
            lock (_lock) snap = _tasks.Where(t => !t.IsCompleted).ToArray();
            if (snap.Length == 0) return;
            await Task.WhenAll(snap).ConfigureAwait(false);
        }
    }

    public void CancelAll()
    {
        Job[] snap;
        lock (_lock) snap = _all.ToArray();
        foreach (var j in snap) if (!j.IsFinished) j.Cancel();
    }

    private void PostOrRun(Action a)
    {
        if (_ctx == null || SynchronizationContext.Current == _ctx) a();
        else _ctx.Post(_ => a(), null);
    }

    private async Task AcquireAsync(bool exclusive, CancellationToken ct)
    {
        // Heavy jobs queue FIFO for the heavy slot in their own line, outside _order, so a waiting
        // heavy job never blocks light jobs queued behind it.
        if (exclusive)
        {
            await _heavyOrder.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    Task wait;
                    lock (_gate)
                    {
                        if (!_heavyRunning) break;
                        wait = _changed.Task;
                    }
                    await wait.WaitAsync(ct).ConfigureAwait(false);
                }
                await AcquireSlotInOrderAsync(true, ct).ConfigureAwait(false);
            }
            finally { _heavyOrder.Release(); }
            return;
        }
        await AcquireSlotInOrderAsync(false, ct).ConfigureAwait(false);
    }

    private async Task AcquireSlotInOrderAsync(bool exclusive, CancellationToken ct)
    {
        await _order.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    bool ok = _running < _maxParallel && !(exclusive && _heavyRunning);
                    if (ok) { _running++; if (exclusive) _heavyRunning = true; return; }
                    wait = _changed.Task;
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        finally { _order.Release(); }
    }

    private void ReleaseJobSlot(Job job)
    {
        if (!job.SlotHeld) return;
        job.SlotHeld = false;
        ReleaseSlot(job.Spec.Exclusive);
    }

    private void ReleaseSlot(bool exclusive)
    {
        lock (_gate) { _running--; if (exclusive) _heavyRunning = false; }
        Signal();
    }

    private async Task RunJobAsync(Job job, Task acquire)
    {
        var spec = job.Spec;
        bool exclusive = spec.Exclusive;
        bool acquireAwaited = false;
        try
        {
            job.OpenLog();
            acquireAwaited = true;
            try { await acquire.ConfigureAwait(false); job.SlotHeld = true; }
            catch (OperationCanceledException)
            {
                job.Log("Cancelled before start.");
                job.StartUtc = job.EndUtc = DateTime.UtcNow;
                job.StatusReason = JobOutcomeClassifier.CancelledReason;
                job.State = JobState.Cancelled;
                return;
            }

            try { await ExecuteAsync(job).ConfigureAwait(false); }
            finally { ReleaseJobSlot(job); }
        }
        catch (Exception ex)
        {
            job.Log("Internal error: " + ex.Message);
            job.EndUtc ??= DateTime.UtcNow;
            job.StatusReason = "Internal error: " + ex.Message;
            job.State = JobState.Failed;
        }
        finally
        {
            // The job failed before it waited for its queue position (e.g. its log could not be opened): the slot it is still
            // queued for is handed back as soon as it is granted, or the queue would lose that slot for good.
            if (!acquireAwaited)
                _ = acquire.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) ReleaseSlot(exclusive); }, TaskScheduler.Default);
            if (spec.Finally != null)
            {
                try { spec.Finally(job); }
                catch (Exception ex) { job.Log("# Cleanup step failed: " + ex.Message); }
            }
            if (spec.Output != null && job.OwnsStaging)
            {
                OutputPublisher.RemoveEmptyStaging(spec.Output.StagingDir);
                lock (_activeStaging) _activeStaging.Remove(Path.GetFullPath(spec.Output.StagingDir));
            }
            // A PrepareOutsideSlot job that failed before its preparation step never set its start time.
            if (job.StartUtc == null && job.EndUtc != null) job.StartUtc = job.EndUtc;
            job.CloseLog();
            try { GetManifest(job.CaseDir).RecordAndWrite(job); } catch { }
            job.MarkRecorded();
        }
    }

    private ManifestWriter GetManifest(string caseDir)
    {
        lock (_lock)
        {
            if (!_manifests.TryGetValue(caseDir, out var m))
            {
                m = new ManifestWriter(caseDir) { SourcePath = _sourcePath, SourceRoots = _sourceRoots };
                _manifests[caseDir] = m;
            }
            return m;
        }
    }

    private async Task ExecuteAsync(Job job)
    {
        var spec = job.Spec;
        job.DebugLogging = LogLevel == ToolLogLevel.Debug;
        bool outsideSlot = spec.PrepareOutsideSlot && spec.PreProcess != null && !spec.Exclusive;
        // A job that waits in PreProcess (memory jobs behind the symbol check) starts its clock only once it really runs.
        if (!outsideSlot) { job.StartUtc = DateTime.UtcNow; job.State = JobState.Running; }

        var tool = spec.ExePath != null
            ? new InstalledTool(Path.GetFileName(spec.ExePath), spec.ExePath, BuildFlavor.Net9, spec.ExeVersion)
            : _locator.Resolve(spec.ExeName, PreferredFlavor);
        if (tool == null || !File.Exists(tool.Path))
        {
            job.Log(spec.ExePath != null
                ? $"ERROR: {spec.ExePath} was not found."
                : $"ERROR: {spec.ExeName} was not found under the tools root. Install or update the tools in Tools, or set the tools folder in Settings.");
            job.EndUtc = DateTime.UtcNow;
            job.StatusReason = JobOutcomeClassifier.Classify(0, false, false, Array.Empty<string>(), Array.Empty<string>(), toolMissing: true).Reason;
            job.State = JobState.Failed;
            return;
        }

        job.ExePath = tool.Path;
        job.Flavor = spec.ExePath != null ? null : tool.Flavor;
        job.ExeVersion = tool.Version;
        var toolDir = Path.GetDirectoryName(tool.Path) ?? "";
        var args = spec.Args.Select(a => a.Replace("{toolDir}", toolDir, StringComparison.Ordinal)).ToList();
        job.ResolvedArgs = args;
        job.CommandLine = BuildDisplay(tool.Path, args);
        job.Log(JobLogFormat.JobPrefix + job.Id, raise: false);
        if (job.StartUtc is { } started0) job.Log("# Started (UTC): " + started0.ToString("o"), raise: false);
        foreach (var hl in JobLogFormat.CommandHeaderLines(job.CommandLine)) job.Log(hl);

        try { Directory.CreateDirectory(spec.OutputDir); }
        catch (Exception ex)
        {
            job.Log("ERROR: cannot create output directory: " + ex.Message);
            job.EndUtc = DateTime.UtcNow;
            job.StatusReason = "Cannot create output directory: " + ex.Message;
            job.State = JobState.Failed;
            return;
        }

        // Snapshot the output folder so only files this run creates are renamed afterwards.
        OutputSnapshot? snapshot = null, staging = null;
        if (spec.Output != null)
        {
            // Layout v2: the tool writes into its own staging folder; the results are published under convention names afterwards.
            bool claimed;
            lock (_activeStaging) claimed = _activeStaging.Add(Path.GetFullPath(spec.Output.StagingDir));
            if (!claimed)
            {
                const string busy = "Another job is still writing to the same staging folder (a second run of the same triage plan?). Run this job again when that one has finished.";
                job.Log("ERROR: " + busy + " " + spec.Output.StagingDir);
                job.EndUtc = DateTime.UtcNow;
                job.StatusReason = busy;
                job.State = JobState.Failed;
                return;
            }
            job.OwnsStaging = true;
            try
            {
                OutputPublisher.PrepareStaging(spec.Output.StagingDir);
                staging = OutputSnapshot.Capture(spec.Output.StagingDir);
            }
            catch (Exception ex)
            {
                job.Log("ERROR: cannot create the staging folder: " + ex.Message);
                job.EndUtc = DateTime.UtcNow;
                job.StatusReason = "Cannot create the staging folder: " + ex.Message;
                job.State = JobState.Failed;
                return;
            }
        }
        else if (StripTimestampPrefix && spec.ExePath == null)
        {
            try { snapshot = OutputSnapshot.Capture(spec.OutputDir); }
            catch (Exception ex) { job.Log("# Output snapshot failed (files keep the tool's names): " + ex.Message); }
        }

        // Input facts (file times, size, hash) are recorded before anything touches the inputs.
        if (spec.Provenance is { Inputs.Count: > 0 } prov)
        {
            try
            {
                var (facts, note) = InputFacts.Collect(prov.Inputs, prov.HashInputs, prov.IsLiveVolume, prov.HashOnLiveVolume, job.Token);
                job.InputFacts = facts;
                job.InputFactsNote = note;
                job.Log($"# Recorded {facts.Count} input file fact(s){(note != null ? ": " + note : ".")}", raise: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { job.Log("# Input facts could not be recorded: " + ex.Message); }
        }

        // Preparation (e.g. a temporary read-only view of the evidence) runs before the process starts.
        if (spec.PreProcess != null)
        {
            try
            {
                if (outsideSlot)
                {
                    ReleaseJobSlot(job);
                    job.State = JobState.Queued;
                    job.StatusReason = WaitingReason;
                }
                await spec.PreProcess(new JobPostProcessContext(job, 0), job.Token).ConfigureAwait(false);
                if (outsideSlot)
                {
                    job.StatusReason = WaitingForSlotReason;
                    await AcquireAsync(false, job.Token).ConfigureAwait(false);
                    job.SlotHeld = true;
                    job.StatusReason = null;
                    job.StartUtc = DateTime.UtcNow;
                    job.State = JobState.Running;
                    job.Log("# Started (UTC): " + job.StartUtc.Value.ToString("o"), raise: false);
                }
            }
            catch (OperationCanceledException)
            {
                job.Log("Cancelled before the process started.");
                job.StartUtc ??= DateTime.UtcNow;
                job.EndUtc = DateTime.UtcNow;
                job.StatusReason = JobOutcomeClassifier.CancelledReason;
                job.State = JobState.Cancelled;
                return;
            }
            catch (Exception ex)
            {
                job.Log("ERROR: preparation failed: " + ex.Message);
                job.StartUtc ??= DateTime.UtcNow;
                job.EndUtc = DateTime.UtcNow;
                job.StatusReason = "Preparation failed: " + ex.Message;
                job.State = JobState.Failed;
                return;
            }
        }

        var timeout = spec.Timeout ?? Timeout;
        ProcessResult res;
        var lines = new List<string>();
        var errLines = new List<string>();
        var outLines = new List<string>();
        var progress = spec.ToolId == Memory.MemoryJobs.ToolId ? new Memory.VolatilityProgressThrottle() : null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var workDir = !string.IsNullOrEmpty(spec.WorkingDir) && Directory.Exists(spec.WorkingDir) ? spec.WorkingDir : Path.GetDirectoryName(tool.Path);
            if (job.DebugLogging)
            {
                foreach (var dl in ToolDebugLog.StartLines(tool.Path, args, workDir, Sysinternals.ProcessElevation.IsElevated(), ToolDebugLog.Inspect(tool.Path), spec.Environment, spec.StdoutPath))
                    job.Log(dl, raise: false);
                clock.Restart();
            }
            res = await ProcessRunner.RunAsync(tool.Path, args, workDir,
                (isErr, line) =>
                {
                    bool write = true;
                    if (progress != null)
                    {
                        if (Memory.VolatilityProgressThrottle.IsBlank(line)) return;
                        write = progress.ShouldLog(line);
                    }
                    if (write) lock (lines) { lines.Add(line); if (isErr) errLines.Add(line); else outLines.Add(line); }
                    job.Log(line, write: write);
                }, timeout, job.Token, spec.Environment, spec.StdoutPath).ConfigureAwait(false);
            if (job.DebugLogging)
            {
                string[] o, e;
                lock (lines) { o = outLines.ToArray(); e = errLines.ToArray(); }
                foreach (var dl in ToolDebugLog.EndLines(res.ExitCode, res.TimedOut, res.Cancelled, clock.Elapsed, o, e)) job.Log(dl, raise: false);
            }
            if ((res.Cancelled || res.TimedOut) && spec.CancelCommand is { Count: > 0 } cancelCmd)
                await RunCancelCommandAsync(job, cancelCmd).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            job.Log("ERROR: failed to start process: " + ex.Message);
            job.EndUtc = DateTime.UtcNow;
            job.StatusReason = "Failed to start process: " + ex.Message;
            job.State = JobState.Failed;
            return;
        }

        // Post-processing (e.g. Zeek log -> CSV conversion) runs before the outputs are classified and hashed.
        string? postError = null;
        if (spec.PostProcess != null && !res.Cancelled && !res.TimedOut)
        {
            try { await spec.PostProcess(new JobPostProcessContext(job, res.ExitCode), job.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { res = res with { Cancelled = true }; }
            catch (Exception ex)
            {
                postError = "Post-processing failed: " + ex.Message;
                job.Log("ERROR: " + postError);
            }
        }

        job.EndUtc = DateTime.UtcNow;
        job.ExitCode = res.ExitCode;
        job.Log($"# Exit code: {res.ExitCode}{(res.TimedOut ? " (timed out)" : res.Cancelled ? " (cancelled)" : "")}", raise: false);
        job.Log("# Finished (UTC): " + job.EndUtc.Value.ToString("o"), raise: false);
        string[] snap, errSnap;
        lock (lines) { snap = lines.ToArray(); errSnap = errLines.ToArray(); }
        if (staging != null)
        {
            // A cancelled / timed-out tool may have written incomplete files: they are still published, marked _PARTIAL.
            bool partial = res.Cancelled || res.TimedOut;
            if (partial && spec.ToolId == Memory.MemoryJobs.ToolId)
            {
                try
                {
                    var staged = Directory.EnumerateFiles(spec.Output!.StagingDir, "*.csv", SearchOption.AllDirectories).Where(staging.IsNewOrChanged).ToList();
                    Memory.MemoryJobs.DropEmptyPartialCsvs(staged, l => job.Log(l));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            try
            {
                var published = OutputPublisher.Publish(spec.ToolId, spec.Output!, spec.OutputDir, staging, l => job.Log(l), partial);
                job.PublishedOutputs = published;
                job.OutputRenames = published.Select(p => new OutputRename(p.OriginalPath, p.FinalPath)).ToList();
                job.PartialOutputs = partial && published.Count > 0;
            }
            catch (Exception ex) { job.Log("# Publishing the outputs failed (files stay in the staging folder): " + ex.Message); }
            job.StagingLeftovers = OutputPublisher.Leftovers(staging);
            if (job.StagingLeftovers.Count > 0)
            {
                job.SetDetail("output.leftInStaging", $"{job.StagingLeftovers.Count} file(s) in {spec.Output!.StagingDir}");
                job.Log($"# {job.StagingLeftovers.Count} output file(s) left in staging: {spec.Output!.StagingDir}");
            }
            if (job.PartialOutputs) job.SetDetail("output.partial", "true");
        }
        else if (snapshot != null)
        {
            try { job.OutputRenames = OutputRenamer.StripTimestampPrefixes(snapshot, OutputNaming.UserNamedFiles(args), spec.ToolId, l => job.Log(l)); }
            catch (Exception ex) { job.Log("# Output rename failed (files keep the tool's names): " + ex.Message); }
        }
        var startedAt = job.StartUtc ?? DateTime.UtcNow;
        var files = new List<string>();
        try
        {
            if (spec.Output != null)
            {
                files.AddRange(job.PublishedOutputs.Select(p => p.FinalPath).Where(f => !OutputNaming.IsEzForensicsSidecar(f)));
                files.AddRange(job.StagingLeftovers);
            }
            else if (Directory.Exists(spec.OutputDir))
                files.AddRange(Directory.EnumerateFiles(spec.OutputDir, "*", SearchOption.AllDirectories)
                    .Where(f => !OutputNaming.IsEzForensicsSidecar(f) && !IsJobBookkeepingFile(f, job.LogPath)
                                && File.GetLastWriteTimeUtc(f) >= startedAt.AddSeconds(-5)));
        }
        catch (Exception) { }
        var outcome = JobOutcomeClassifier.Classify(res.ExitCode, res.TimedOut, res.Cancelled, snap, files, timeout, errSnap);
        if (postError != null && outcome.State == JobState.Succeeded) outcome = new JobOutcome(JobState.Failed, postError);
        if (res.ExitCode == 0 && outcome.State == JobState.Failed)
            job.Log("ERROR: " + outcome.Reason);
        var reason = outcome.Reason ?? "";
        if (job.PartialOutputs) reason += $" {job.PublishedOutputs.Count} incomplete output file(s) were published with the _PARTIAL qualifier.";
        if (job.StagingLeftovers.Count > 0) reason += $" {job.StagingLeftovers.Count} output file(s) left in staging: {spec.Output!.StagingDir}";
        job.StatusReason = reason.Trim();
        job.State = outcome.State;
    }

    /// <summary>The job's own log, anything under a <c>logs</c> folder next to it, and run manifests are bookkeeping, not tool output.</summary>
    public static bool IsJobBookkeepingFile(string path, string? logPath)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("EzForensics_manifest_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrEmpty(logPath)) return false;
        if (string.Equals(path, logPath, StringComparison.OrdinalIgnoreCase)) return true;
        var logDir = Path.GetDirectoryName(logPath);
        if (string.IsNullOrEmpty(logDir)) return false;
        var prefix = logDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs the spec's cleanup command (e.g. <c>docker stop</c>) after a cancel or timeout; bounded, never throws.</summary>
    private static async Task RunCancelCommandAsync(Job job, IReadOnlyList<string> cmd)
    {
        try
        {
            job.Log("# Cleanup: " + BuildDisplay(cmd[0], cmd.Skip(1).ToList()));
            var r = await ProcessRunner.RunAsync(cmd[0], cmd.Skip(1).ToList(), null, (_, l) => job.Log(l),
                TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
            job.Log($"# Cleanup exit code: {r.ExitCode}", raise: false);
        }
        catch (Exception ex) { job.Log("# Cleanup failed: " + ex.Message); }
    }

    private static string BuildDisplay(string exe, IReadOnlyList<string> args)
    {
        static string Q(string s) => s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
        return string.Join(" ", new[] { Q(exe) }.Concat(args.Select(Q)));
    }
}
