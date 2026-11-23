using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Common;
using EzForensics.Core.Execution;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// One row of the Ingest planned jobs: the planned job, its tick and, once Run selected queued it, its job(s) for the Progress and Result
/// columns (a computer details row has one job per system, the super timeline row log2timeline then psort, Process tree data one per host).
/// </summary>
public sealed partial class PlannedJobItem : ObservableObject, IJobListRow
{
    private readonly List<Job> _jobs = new();

    [ObservableProperty] private bool _isSelected;
    /// <summary>Ticked in a run that has not started this row yet (the super timeline waits for every other job).</summary>
    [ObservableProperty] private bool _isWaiting;
    /// <summary>Started work that has no unfinished job right now (the super timeline between log2timeline and psort).</summary>
    [ObservableProperty] private bool _isBusy;
    /// <summary>Ticked but not queued in the last run (tool missing, Zeek not set up...): the reason, shown as Skipped.</summary>
    [ObservableProperty] private string? _notQueuedReason;
    /// <summary>Why the row cannot run (its tool is not installed or has an issue), or null. The row is greyed out and unticked meanwhile.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(CanToggle), nameof(RowOpacity), nameof(RowToolTip))] private string? _unavailableReason;
    /// <summary>
    /// Why the row is left out by a Detected artifacts card ("Disk not selected"...), or null. The row stays in the plan, greyed, unticked
    /// and disabled; its tick before the exclusion comes back when the card item is ticked again.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsExcluded), nameof(CanToggle), nameof(RowOpacity), nameof(RowToolTip))] private string? _excludedReason;
    /// <summary>A run is going on: the tick cannot change (the row's progress must survive).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanToggle))] private bool _isLocked;
    private bool _unticked;   // unticked because it became unavailable: ticked again (default) when it is available again
    private bool _tickBeforeExclusion;
    private long? _finishedStamp;

    public PlannedJobItem(PlannedJob p)
    {
        Planned = p;
        _isSelected = p.SelectedByDefault;
    }

    public PlannedJob Planned { get; set; }
    public string Tool => Planned.Spec.ToolId;
    public string Label => Planned.Spec.Label;
    public string MatchedPath => Planned.Artifact.MatchedPath;
    public string Category => Planned.Spec.Category;
    public bool RequiresAdmin => Planned.RequiresAdmin;
    public int MatchCount => Planned.Artifact.MatchCount;
    public string Key => KeyOf(Planned);
    /// <summary>The row key of a planned job (ticks survive a re-plan by it).</summary>
    public static string KeyOf(PlannedJob p) => p.Artifact.RuleId + "|" + p.Artifact.MatchedPath + "|" + p.Spec.Label;
    /// <summary>The row has a tick (jobs started elsewhere, listed below the planned rows, have none).</summary>
    public bool ShowTick => true;
    /// <summary>Raised when a job is attached (the page remembers every job Ingest queued, so it does not list it again as another job).</summary>
    public Action<Job>? JobAttached { get; set; }
    public bool IsAvailable => UnavailableReason == null;
    public bool IsExcluded => ExcludedReason != null;
    /// <summary>The row checkbox is enabled: available, not excluded by a card and no run going on.</summary>
    public bool CanToggle => IsAvailable && !IsExcluded && !IsLocked;
    public double RowOpacity => IsAvailable && !IsExcluded ? 1.0 : 0.5;
    public string? RowToolTip => ExcludedReason ?? UnavailableReason;

    /// <summary>The tick the user wants: the current one, or for an excluded row the one it gets back when its card item is ticked again.</summary>
    public bool WantedSelected => IsExcluded ? _tickBeforeExclusion : IsSelected;

    /// <summary>Sets the wanted tick (Defaults / All / Clear): ticks the row, or for an excluded row the tick it gets back later.</summary>
    public void SetWanted(bool on)
    {
        if (IsExcluded) _tickBeforeExclusion = on && IsAvailable;
        else if (IsAvailable || !on) IsSelected = on;
    }

    /// <summary>Position in the plan (the queued / unselected order of <see cref="RunOrder"/>).</summary>
    public long Sequence { get; set; }

    /// <summary>Where the row stands for the live order: running, queued (or ticked before a run), finished, or not part of the run.</summary>
    public RunPhase Phase
    {
        get
        {
            if (IsRunning) return RunPhase.Running;
            if (IsWaiting || _jobs.Any(j => !j.IsFinished)) return RunPhase.Queued;
            if (NotQueuedReason != null || _jobs.Count > 0) return RunPhase.Finished;
            return IsSelected ? RunPhase.Queued : RunPhase.Inactive;
        }
    }

    /// <summary>When the row finished (its last job, or not queued), for the order of the finished rows.</summary>
    public long? FinishedStamp => _finishedStamp;
    public bool IsComputerDetails => IngestSteps.IsComputerDetails(Planned);
    public bool IsSupertimeline => IngestSteps.IsSupertimeline(Planned);
    public bool IsMacbTimeline => IngestSteps.IsMacbTimeline(Planned);
    public bool IsProcessTree => IngestSteps.IsProcessTree(Planned);

    /// <summary>The jobs queued for this row in the last run.</summary>
    public IReadOnlyList<Job> Jobs => _jobs;

    /// <summary>The job the row shows and opens (Result link): the running one, else queued, else the first that did not succeed, else the last.</summary>
    public Job? Job
    {
        get
        {
            var i = JobResultText.RepresentativeIndex(_jobs.Select(j => j.State).ToList());
            return i < 0 ? null : _jobs[i];
        }
    }

    /// <summary>The job the row's columns, details and context menu act on (same as <see cref="Job"/>).</summary>
    public Job? LatestJob => Job;
    /// <summary>Cancel applies while the latest job is queued or running; Retry once it has finished.</summary>
    public bool CanCancelJob => LatestJob is { IsFinished: false };
    public bool CanRetryJob => LatestJob is { IsFinished: true };
    /// <summary>Duration column (live while the job runs; <see cref="TickDuration"/> refreshes it).</summary>
    public string Duration => LatestJob is { } j ? JobRow.FormatDuration(j) : "";
    public string Result => LatestJob?.StatusReason ?? NotQueuedReason ?? "";
    public string OutputDir => LatestJob?.Spec.OutputDir ?? Planned.Spec.OutputDir ?? "";
    /// <summary>A job of the row is running: its Duration changes every second.</summary>
    public bool HasRunningJob => _jobs.Any(j => j.State == JobState.Running);
    public void TickDuration() => OnPropertyChanged(nameof(Duration));

    public bool HasJob => _jobs.Count > 0;
    /// <summary>The not-clickable Status badge (no job, but a status such as Waiting or Skipped).</summary>
    public bool ShowStatusBadge => !HasJob && Status.Length > 0;
    private bool IsRunning => IsBusy || _jobs.Any(j => j.State == JobState.Running);
    private bool IsDone => !IsBusy && !IsWaiting && _jobs.Count > 0 && _jobs.All(j => j.IsFinished);

    /// <summary>Progress column: hidden before the run (and for a row that was not queued).</summary>
    public bool ShowProgress => _jobs.Count > 0 || IsWaiting || IsBusy;
    /// <summary>No job reports a fraction: indeterminate while running.</summary>
    public bool ProgressIndeterminate => IsRunning;
    public double ProgressValue => IsDone ? 1 : 0;

    /// <summary>Status column text: Done / Failed (exit 2) / Cancelled / Skipped / Running / Queued / Waiting; empty before the run.</summary>
    public string Status =>
        NotQueuedReason != null ? JobResultText.Text(JobState.Skipped, null)
        : Job is { } j ? (IsBusy || IsWaiting) && j.IsFinished ? JobResultText.Text(JobState.Running, null) : JobResultText.Text(j.State, j.ExitCode)
        : IsBusy ? JobResultText.Text(JobState.Running, null)
        : IsWaiting ? "Waiting"
        : "";

    /// <summary>Colour of the Status badge: Success (done), Error (failed / timed out), Warning (cancelled / skipped / not queued), Info (queued /
    /// running / waiting).</summary>
    public InfoSeverity StatusSeverity =>
        NotQueuedReason != null ? InfoSeverity.Warning
        : IsBusy || IsWaiting ? InfoSeverity.Info
        : Job is { } j ? j.State switch
        {
            JobState.Succeeded => InfoSeverity.Success,
            JobState.Failed or JobState.TimedOut => InfoSeverity.Error,
            JobState.Cancelled or JobState.Skipped => InfoSeverity.Warning,
            _ => InfoSeverity.Info,
        }
        : InfoSeverity.Info;

    public string StatusToolTip =>
        NotQueuedReason ?? (Job is { } j ? (string.IsNullOrEmpty(j.StatusReason) ? "" : j.StatusReason + Environment.NewLine) + "Click to show this job's log below"
        : IsWaiting ? "Starts when the other jobs of the run have finished" : "");

    public void AttachJob(Job job)
    {
        _jobs.Add(job);
        job.PropertyChanged += OnJobChanged;
        JobAttached?.Invoke(job);
        RaiseRun();
    }

    /// <summary>Resume: the cancelled job is replaced by the job re-queued from its spec (the row then shows the new one).</summary>
    public void ReplaceJob(Job old, Job replacement)
    {
        old.PropertyChanged -= OnJobChanged;
        _jobs.Remove(old);
        AttachJob(replacement);
    }

    /// <summary>Forgets the previous run's jobs (a new run starts).</summary>
    public void ResetRun()
    {
        foreach (var j in _jobs) j.PropertyChanged -= OnJobChanged;
        _jobs.Clear();
        IsWaiting = false;
        IsBusy = false;
        NotQueuedReason = null;
        RaiseRun();
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Core.Execution.Job.State) or nameof(Core.Execution.Job.ExitCode) or nameof(Core.Execution.Job.StatusReason) or nameof(Core.Execution.Job.StartUtc) or nameof(Core.Execution.Job.EndUtc))
            AppServices.OnUi(RaiseRun);
    }

    partial void OnUnavailableReasonChanged(string? value)
    {
        if (value != null && WantedSelected)
        {
            _unticked = true;
            if (IsExcluded) _tickBeforeExclusion = false; else IsSelected = false;
        }
        else if (value == null && _unticked)
        {
            _unticked = false;
            if (IsExcluded) _tickBeforeExclusion = Planned.SelectedByDefault; else IsSelected = Planned.SelectedByDefault;
        }
    }

    partial void OnExcludedReasonChanged(string? oldValue, string? newValue)
    {
        if (oldValue == null && newValue != null) { _tickBeforeExclusion = IsSelected; IsSelected = false; }
        else if (oldValue != null && newValue == null) IsSelected = _tickBeforeExclusion;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && (UnavailableReason != null || ExcludedReason != null)) IsSelected = false;   // a greyed-out row cannot be ticked
    }

    partial void OnIsWaitingChanged(bool value) => RaiseRun();
    partial void OnIsBusyChanged(bool value) => RaiseRun();
    partial void OnNotQueuedReasonChanged(string? value) => RaiseRun();

    private void RaiseRun()
    {
        if (Phase == RunPhase.Finished) _finishedStamp ??= RunOrder.NextStamp();
        else _finishedStamp = null;
        OnPropertyChanged(nameof(Job));
        OnPropertyChanged(nameof(LatestJob));
        OnPropertyChanged(nameof(CanCancelJob));
        OnPropertyChanged(nameof(CanRetryJob));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(OutputDir));
        OnPropertyChanged(nameof(HasJob));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusSeverity));
        OnPropertyChanged(nameof(ShowStatusBadge));
        OnPropertyChanged(nameof(StatusToolTip));
    }
}
