using System.ComponentModel;
using EzForensics.Core.Common;
using EzForensics.Core.Execution;

namespace EzForensics.App.ViewModels;

/// <summary>A row of the Ingest jobs list: a planned job (with a tick) or a job started elsewhere (<see cref="OtherJobItem"/>).</summary>
public interface IJobListRow : INotifyPropertyChanged
{
    string Tool { get; }
    string Label { get; }
    string MatchedPath { get; }
    string Category { get; }
    bool RequiresAdmin { get; }
    string Status { get; }
    InfoSeverity StatusSeverity { get; }
    string StatusToolTip { get; }
    bool ShowProgress { get; }
    double ProgressValue { get; }
    bool ProgressIndeterminate { get; }
    string Duration { get; }
    string Result { get; }
    string OutputDir { get; }
    Job? LatestJob { get; }
    bool CanCancelJob { get; }
    bool CanRetryJob { get; }
    bool HasJob { get; }
    bool ShowStatusBadge { get; }
    double RowOpacity { get; }
    string? RowToolTip { get; }
    bool IsSelected { get; set; }
    bool CanToggle { get; }
    bool ShowTick { get; }
    bool HasRunningJob { get; }
    void TickDuration();
}
