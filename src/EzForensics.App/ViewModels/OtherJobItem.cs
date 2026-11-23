using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Common;
using EzForensics.Core.Execution;

namespace EzForensics.App.ViewModels;

/// <summary>A job that Ingest did not queue (Zeek, RITA, plaso, process tree data, computer details...) as a row of the Ingest jobs list, below the planned rows.</summary>
public sealed class OtherJobItem : ObservableObject, IJobListRow
{
    private readonly Job _job;

    public OtherJobItem(Job job)
    {
        _job = job;
        job.PropertyChanged += OnJobChanged;
    }

    public Job Job => _job;
    public string Tool => _job.Spec.ToolId;
    public string Label => _job.Spec.Label;
    public string MatchedPath => "";
    public string Category => _job.Spec.Category;
    public bool RequiresAdmin => false;
    public string Status => JobResultText.Text(_job.State, _job.ExitCode);
    public InfoSeverity StatusSeverity => _job.State switch
    {
        JobState.Succeeded => InfoSeverity.Success,
        JobState.Failed or JobState.TimedOut => InfoSeverity.Error,
        JobState.Cancelled or JobState.Skipped => InfoSeverity.Warning,
        _ => InfoSeverity.Info,
    };
    public string StatusToolTip => (string.IsNullOrEmpty(_job.StatusReason) ? "" : _job.StatusReason + Environment.NewLine) + "Click to show this job's log below";
    public bool ShowProgress => true;
    public double ProgressValue => _job.IsFinished ? 1 : 0;
    public bool ProgressIndeterminate => _job.State == JobState.Running;
    public string Duration => JobRow.FormatDuration(_job);
    public string Result => _job.StatusReason ?? "";
    public string OutputDir => _job.Spec.OutputDir ?? "";
    public Job? LatestJob => _job;
    public bool CanCancelJob => !_job.IsFinished;
    public bool CanRetryJob => _job.IsFinished;
    public bool HasJob => true;
    public bool ShowStatusBadge => false;
    public double RowOpacity => 1.0;
    public string? RowToolTip => null;
    public bool IsSelected { get => false; set { } }
    public bool CanToggle => false;
    public bool ShowTick => false;
    public bool HasRunningJob => _job.State == JobState.Running;
    public void TickDuration() => OnPropertyChanged(nameof(Duration));

    /// <summary>Stops following the job (the row left the list).</summary>
    public void Detach() => _job.PropertyChanged -= OnJobChanged;

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Job.State) or nameof(Job.ExitCode) or nameof(Job.StatusReason) or nameof(Job.StartUtc) or nameof(Job.EndUtc))
            AppServices.OnUi(Raise);
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusSeverity));
        OnPropertyChanged(nameof(StatusToolTip));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(ProgressIndeterminate));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(CanCancelJob));
        OnPropertyChanged(nameof(CanRetryJob));
        OnPropertyChanged(nameof(HasRunningJob));
    }
}
