using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.Processes;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Post-run "Process tree data" jobs (no Ingest row): after a run has reported its outcome, one built-in correlator job
/// (<see cref="ProcessCorrelator.Spec"/>) is queued in the background for each host of the run (<see cref="IngestSteps.ProcessHosts"/>) that has a process
/// source and whose output is not up to date. They show on Jobs, are not counted in the run's message and Stop cancels them while queued / running.
/// </summary>
public sealed partial class TriageViewModel
{
    private void QueueProcessTreeData(string caseDir, IReadOnlyList<Job> runJobs)
    {
        var planned = _runPlanned;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(caseDir) || planned.Count == 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var specs = new List<JobSpec>();
                foreach (var host in IngestSteps.ProcessHosts(caseDir, planned))
                {
                    if (!ProcessCorrelator.HasProcessSource(host) || ProcessCorrelator.IsUpToDate(host)) continue;
                    specs.Add(ProcessCorrelator.Spec(exe, host));
                }
                if (specs.Count == 0) return;
                AppServices.OnUi(() =>
                {
                    foreach (var spec in specs)
                    {
                        var job = S.Queue.Enqueue(spec, caseDir);
                        _processJobs.Add(job);
                        if (_stopRequested) job.Cancel();
                    }
                    AppLog.Info($"Ingest: {specs.Count} background process tree data job(s) queued.");
                });
            }
            catch (Exception ex) { AppLog.Error("Could not queue the process tree data jobs", ex); }
        });
    }
}
