using System.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Common;
using EzForensics.Core.Execution;
using EzForensics.Core.Processes;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// The "Process tree data" row (<see cref="IngestSteps.ProcessTree"/>, the last row of a plan with a process source). Ticked, it waits for the
/// run's other jobs; then one built-in correlator job (<see cref="ProcessCorrelator.Spec"/>) is queued in the background for each host of the plan
/// (<see cref="IngestSteps.ProcessHosts"/>, rows left out by a card not counted) that has a process source and whose output is not up to date. The
/// jobs are attached to the row (status, duration, result, log), are not counted in the run's message, and Stop cancels them while queued / running
/// ("Resume cancelled jobs" queues them again). No host to correlate: the row ends Skipped with the reason. Ticked alone, it correlates the
/// existing output of the case.
/// </summary>
public sealed partial class TriageViewModel
{
    private const string ProcessTreeStoppedReason = "Not queued: the run was stopped.";
    /// <summary>The Process tree data row ticked in the current run (null = not ticked).</summary>
    private PlannedJobItem? _runProcessTree;

    /// <summary>The row is waiting for the run or its correlator jobs are queued / running (the plan stays locked, Run selected shows Stop).</summary>
    private bool ProcessTreeActive => _runProcessTree is { IsWaiting: true } || _processJobs.Any(j => !j.IsFinished);

    /// <summary><paramref name="rows"/> with the Process tree data row added (last; a restored session's tick, else ticked) or removed, as the plan's process sources say.</summary>
    private List<(PlannedJob Planned, bool Selected)> WithProcessTree(IReadOnlyList<(PlannedJob Planned, bool Selected)> rows)
    {
        var list = rows.ToList();
        var row = IngestSteps.ProcessTree(CaseFolder.Trim(), list.Where(r => !IngestSteps.IsStep(r.Planned)).Select(r => r.Planned).ToList());
        int at = list.FindIndex(r => IngestSteps.IsProcessTree(r.Planned));
        list.RemoveAll(r => IngestSteps.IsProcessTree(r.Planned));
        if (row == null) return list;
        bool on = at >= 0 ? rows[at].Selected
            : _restoredTicks != null && _restoredTicks.TryGetValue(PlannedJobItem.KeyOf(row), out var was) ? was : row.SelectedByDefault;
        list.Add((row, on));
        return list;
    }

    /// <summary>
    /// After the run's other jobs (AfterRunAsync): queues the correlator jobs of the waiting Process tree data row onto it. Every outcome ends the
    /// row's wait: jobs attached, Skipped with the reason (up to date, no process source), or stopped (resumable).
    /// </summary>
    private void QueueProcessTreeData(string caseDir)
    {
        var row = _runProcessTree;
        if (row is not { IsWaiting: true }) return;
        if (_stopRequested) { EndProcessTreeStopped(row); return; }
        var planned = _runPlanned;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(caseDir))
        {
            EndProcessTreeWait(row, "Not queued: the EZ Forensics executable or the case folder is unknown.");
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var specs = new List<JobSpec>();
                int upToDate = 0;
                foreach (var host in IngestSteps.ProcessHosts(caseDir, planned))
                {
                    if (!ProcessCorrelator.HasProcessSource(host)) continue;
                    if (ProcessCorrelator.IsUpToDate(host)) { upToDate++; continue; }
                    specs.Add(ProcessCorrelator.Spec(exe, host));
                }
                AppServices.OnUi(() =>
                {
                    if (row != _runProcessTree || !row.IsWaiting) return;
                    if (_stopRequested) { EndProcessTreeStopped(row); return; }
                    if (specs.Count == 0)
                    {
                        EndProcessTreeWait(row, upToDate > 0 ? "Up to date: the process tree data of every host is current."
                                                             : "Not queued: no host has process lists or event log output.");
                        return;
                    }
                    foreach (var spec in specs) TrackProcessJob(row, S.Queue.Enqueue(spec, caseDir));
                    row.IsWaiting = false;
                    RefreshRunGate();
                    AppLog.Info($"Ingest: {specs.Count} process tree data job(s) queued ({upToDate} host(s) up to date).");
                });
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not queue the process tree data jobs", ex);
                AppServices.OnUi(() => { if (row == _runProcessTree && row.IsWaiting) EndProcessTreeWait(row, "Not queued: " + ex.Message); });
            }
        });
    }

    private void TrackProcessJob(PlannedJobItem row, Job job)
    {
        row.AttachJob(job);
        job.PropertyChanged += OnProcessJobChanged;
        _processJobs.Add(job);
    }

    private void OnProcessJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Job.State)) AppServices.OnUi(RefreshRunGate);
    }

    /// <summary>The row stops waiting with <paramref name="reason"/> (shown as Skipped).</summary>
    private void EndProcessTreeWait(PlannedJobItem row, string reason)
    {
        row.IsWaiting = false;
        row.NotQueuedReason = reason;
        RefreshRunGate();
    }

    /// <summary>Stop before the correlator jobs were queued: nothing is queued, "Resume cancelled jobs" queues them later.</summary>
    private void EndProcessTreeStopped(PlannedJobItem row)
    {
        EndProcessTreeWait(row, ProcessTreeStoppedReason);
        if (_runFinished && !CanResumeCancelled)
        {
            ReportRun("Stopped: the process tree data was not built.", InfoSeverity.Warning);
            CanResumeCancelled = true;
        }
    }

    /// <summary>Stop after the run's other jobs: the correlator step was stopped (its row's jobs cancelled), so it can be resumed.</summary>
    private void OnProcessTreeStopped(IReadOnlyCollection<Job> cancelled)
    {
        if (!_runFinished || _runProcessTree == null || !cancelled.Any(_processJobs.Contains)) return;
        ReportRun("Stopped: the process tree data jobs were cancelled.", InfoSeverity.Warning);
        CanResumeCancelled = true;
    }

    /// <summary>
    /// "Resume cancelled jobs" for the Process tree data row: its cancelled jobs are queued again (same specs); a row stopped before its jobs were
    /// queued waits again. Returns whether there was anything to resume and whether the row now waits (queue it when no run job is resumed).
    /// </summary>
    private (bool Any, bool Waits) ResumeProcessTree()
    {
        var row = _runProcessTree;
        if (row == null) return (false, false);
        bool any = false;
        foreach (var old in row.Jobs.Where(j => j.State is JobState.Cancelled or JobState.Skipped).ToList())
        {
            old.PropertyChanged -= OnProcessJobChanged;
            _processJobs.Remove(old);
            var job = S.Queue.Enqueue(old.Spec, old.CaseDir);
            row.ReplaceJob(old, job);
            job.PropertyChanged += OnProcessJobChanged;
            _processJobs.Add(job);
            any = true;
        }
        if (row.NotQueuedReason == ProcessTreeStoppedReason)
        {
            row.NotQueuedReason = null;
            row.IsWaiting = true;
            return (true, true);
        }
        return (any, false);
    }
}
