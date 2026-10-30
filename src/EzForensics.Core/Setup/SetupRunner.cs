namespace EzForensics.Core.Setup;

/// <summary>Outcome of one step, returned by an <see cref="ISetupExecutor"/>.</summary>
public sealed record SetupStepResult(SetupStepState State, string Message)
{
    public static SetupStepResult Ok(string message) => new(SetupStepState.Done, message);
    public static SetupStepResult AlreadyDone(string message) => new(SetupStepState.AlreadyInstalled, message);
    public static SetupStepResult Fail(string reason) => new(SetupStepState.Failed, reason);
    public static SetupStepResult NeedsAction(string what) => new(SetupStepState.ActionNeeded, what);
    public static SetupStepResult RestartRequired(string message) => new(SetupStepState.WaitingForRestart, message);
}

/// <summary>What a step can do while it runs: write log lines and report progress.</summary>
public sealed class SetupStepContext
{
    internal SetupStepContext(SetupStep step, SetupPlan plan) { Step = step; Plan = plan; }
    public SetupStep Step { get; }
    public SetupPlan Plan { get; }
    public void Log(string line) => Step.Log(line);

    /// <summary>A running step's bar never reads full: only the runner sets 1, once the step is Done.</summary>
    public const double MaxRunningProgress = 0.99;

    /// <summary>The step reported a real fraction (its bar shows it; otherwise the runner publishes its estimate).</summary>
    internal bool Reported { get; private set; }

    /// <summary>0..1 (capped at <see cref="MaxRunningProgress"/> while running), or null for indeterminate.</summary>
    public void Report(double? fraction)
    {
        if (Step.State != SetupStepState.Running) return; // a late Progress<T> callback after the step finished
        if (fraction.HasValue) Reported = true;
        Step.Progress = fraction is { } f ? Math.Clamp(f, 0, MaxRunningProgress) : null;
    }
}

/// <summary>Runs one kind of step. <see cref="DefaultSetupExecutor"/> is the real one; tests use fakes.</summary>
public interface ISetupExecutor
{
    Task<SetupStepResult> ExecuteAsync(SetupStepContext context, CancellationToken ct);
}

/// <summary>Overall progress of a run (for an overall progress bar).</summary>
public sealed record SetupRunProgress(int StepsFinished, int StepsTotal, string? CurrentStep, double Fraction);

/// <summary>
/// How far the running step is, for a smooth overall bar. A step that reports fractions (downloads) moves with them; between two
/// reports, and for steps that never report (pip, apt, wsl --import), the estimate creeps toward <see cref="CreepCap"/> of the
/// remaining share as log lines arrive and as time passes (relative to the step's estimated duration). Never goes backwards and
/// never reaches 1 by itself (the step's end does that).
/// </summary>
public sealed class StepProgressEstimate
{
    /// <summary>The creep never passes this fraction of the step.</summary>
    public const double CreepCap = 0.9;
    /// <summary>Each log line closes this share of the remaining gap to <see cref="CreepCap"/>.</summary>
    public const double LineShare = 0.03;

    private readonly double _estimatedSeconds;
    private double _reported, _lineCreep, _timeCreep, _last;
    private TimeSpan _reportedAt;

    public StepProgressEstimate(int estimatedSeconds) => _estimatedSeconds = Math.Max(10, estimatedSeconds);

    /// <summary>A fraction reported by the step (null = indeterminate, ignored).</summary>
    public void Report(double? fraction, TimeSpan elapsed)
    {
        if (fraction is not { } f) return;
        _reported = Math.Max(_reported, Math.Clamp(f, 0, 1));
        _reportedAt = elapsed;
        _lineCreep = _timeCreep = 0; // the creep starts again from the reported point
    }

    public void LogLine() => _lineCreep += (1 - _lineCreep) * LineShare;

    /// <summary>Time since the step started (time creep: 63% of the gap after the step's estimated duration).</summary>
    public void Elapsed(TimeSpan elapsed) =>
        _timeCreep = 1 - Math.Exp(-Math.Max(0, (elapsed - _reportedAt).TotalSeconds) / _estimatedSeconds);

    public double Value
    {
        get
        {
            var v = _reported >= CreepCap ? _reported : _reported + (CreepCap - _reported) * Math.Max(_lineCreep, _timeCreep);
            _last = Math.Max(_last, Math.Min(1, v));
            return _last;
        }
    }
}

/// <summary>
/// Executes a <see cref="SetupPlan"/> in order. A step runs only when every step it depends on succeeded; otherwise it is
/// skipped with a plain reason (or waits for the restart a dependency needs). One failure does not stop unrelated steps.
/// Run again with the same plan to retry: steps that already succeeded are kept.
/// </summary>
public sealed class SetupRunner
{
    private readonly ISetupExecutor _executor;

    public SetupRunner(ISetupExecutor executor) => _executor = executor;

    public bool IsRunning { get; private set; }

    /// <summary>Share of the overall bar: the step's estimated duration (from the catalog sizes), at least 5 s; built-in steps 1.</summary>
    public static double Weight(SetupStep s) => s.Kind == SetupStepKind.BuiltIn ? 1 : Math.Max(5, s.EstimatedSeconds);

    // live state for Tick() (the host calls it about once a second so steps without output still move the bar)
    private Action? _tick;

    /// <summary>Re-reports the overall progress with the running step's time-based estimate. Call on the runner's context.</summary>
    public void Tick() => _tick?.Invoke();

    public async Task RunAsync(SetupPlan plan, IProgress<SetupRunProgress>? progress = null, CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Setup is already running.");
        IsRunning = true;
        var ctx = SynchronizationContext.Current;
        try
        {
            foreach (var s in plan.Steps)
            {
                s.Context = ctx;
                if (!s.Succeeded && s.Kind != SetupStepKind.BuiltIn) s.ResetForRetry();
            }
            var byId = plan.Steps.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            var work = plan.Steps.Where(s => !s.Succeeded).ToList();
            int total = plan.Steps.Count, done = plan.Steps.Count(s => s.Succeeded);
            double totalWeight = plan.Steps.Sum(Weight), doneWeight = plan.Steps.Where(s => s.Succeeded).Sum(Weight);
            void Report(string? current, double inStep, double currentWeight = 0) => progress?.Report(new SetupRunProgress(done, total, current,
                totalWeight <= 0 ? 1 : Math.Min(1, (doneWeight + currentWeight * inStep) / totalWeight)));
            void Finished(SetupStep s) { done++; doneWeight += Weight(s); Report(null, 0); }
            Report(null, 0);

            foreach (var step in work)
            {
                if (ct.IsCancellationRequested)
                {
                    step.State = SetupStepState.Cancelled;
                    step.Message = "Not started (setup was cancelled).";
                    continue;
                }
                var deps = step.DependsOn.Select(d => byId.TryGetValue(d, out var x) ? x : null).Where(x => x != null).Select(x => x!).ToList();
                var waiting = deps.FirstOrDefault(d => d.State == SetupStepState.WaitingForRestart);
                var blocked = deps.FirstOrDefault(d => !d.Succeeded);
                if (waiting != null)
                {
                    step.State = SetupStepState.WaitingForRestart;
                    step.Message = $"Continues after Windows restarts ({waiting.Title}).";
                    Finished(step);
                    continue;
                }
                if (blocked != null)
                {
                    step.State = SetupStepState.Skipped;
                    step.Message = $"Skipped: it needs \"{blocked.Title}\", which did not finish.";
                    Finished(step);
                    continue;
                }

                step.State = SetupStepState.Running;
                step.Progress = null;
                step.Log($"> {step.Title}");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var estimate = new StepProgressEstimate(step.EstimatedSeconds);
                var context = new SetupStepContext(step, plan);
                double weight = Weight(step);
                double? published = null;
                void ReportStep()
                {
                    var v = estimate.Value;
                    Report(step.Title, v, weight);
                    // A step without a real fraction (pip, apt, wsl --install) shows the estimate: it creeps, never reads full (cap 0.9).
                    if (context.Reported || step.State != SetupStepState.Running) return;
                    published = Math.Min(v, StepProgressEstimate.CreepCap);
                    step.Progress = published;
                }
                void OnStepProgress(object? _, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName != nameof(SetupStep.Progress) || !context.Reported && step.Progress == published) return;
                    estimate.Report(step.Progress, sw.Elapsed);
                    ReportStep();
                }
                void OnStepLog(SetupStep _, string __)
                {
                    estimate.LogLine();
                    estimate.Elapsed(sw.Elapsed);
                    ReportStep();
                }
                step.PropertyChanged += OnStepProgress;
                step.LogLine += OnStepLog;
                _tick = () => { estimate.Elapsed(sw.Elapsed); ReportStep(); };
                Report(step.Title, estimate.Value, weight); // the step bar starts indeterminate, the estimate follows on the first line / tick
                SetupStepResult r;
                try
                {
                    r = await _executor.ExecuteAsync(context, ct).ConfigureAwait(true);
                }
                // Only the user's cancel: an HttpClient timeout is also a TaskCanceledException and is a failure (Explain says "timed out").
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    r = new SetupStepResult(SetupStepState.Cancelled, "Cancelled.");
                }
                catch (Exception ex)
                {
                    step.Log("ERROR: " + ex);
                    r = SetupStepResult.Fail(Explain(ex));
                }
                finally
                {
                    step.PropertyChanged -= OnStepProgress;
                    step.LogLine -= OnStepLog;
                    _tick = null;
                }
                if (r.State == SetupStepState.Failed && ct.IsCancellationRequested) r = new SetupStepResult(SetupStepState.Cancelled, "Cancelled.");
                step.Message = r.Message;
                step.State = r.State; // first: a running step's bar never reads full
                step.Progress = r.State is SetupStepState.Done or SetupStepState.AlreadyInstalled ? 1 : step.Progress;
                step.Log($"{step.StateLabel}: {r.Message}");
                Finished(step);
            }
        }
        finally
        {
            IsRunning = false;
            _tick = null;
        }
    }

    /// <summary>Plain-language reason for common failures.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        HttpRequestException h when h.StatusCode is { } c => $"The download failed (HTTP {(int)c}). Check the internet connection and the proxy in Settings, then click Retry.",
        HttpRequestException => "Could not reach the download server. Check the internet connection and the proxy in Settings, then click Retry.",
        TaskCanceledException => "The download timed out. Check the internet connection and the proxy in Settings, then click Retry.",
        UnauthorizedAccessException u => "Access denied: " + u.Message + " Close programs that use these files, then click Retry.",
        IOException io when Tools.ToolUpdater.IsNetworkIo(io) => "The connection to the download server dropped (" + io.Message.TrimEnd('.') +
                                                                  "). Completed downloads are kept: click Retry to continue with the rest.",
        IOException io when io.Message.Contains("disk", StringComparison.OrdinalIgnoreCase) => "Not enough disk space: " + io.Message,
        IOException io => "A file could not be written: " + io.Message + " Close programs that use these files, then click Retry.",
        System.IO.InvalidDataException d => d.Message,
        _ => ex.Message,
    };
}
