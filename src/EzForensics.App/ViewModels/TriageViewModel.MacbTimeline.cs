using EzForensics.App.Services;
using EzForensics.Core.Timeline;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// The "Filesystem timeline (MACB): &lt;Host&gt;" planned rows, one per host with an $MFT row (<see cref="IngestSteps.MacbTimelines"/>): built
/// after every other job of the run has finished (an MFTECmd bodyfile job per $MFT, converted into
/// <c>&lt;case&gt;\&lt;Host&gt;\Timeline\Timeline_MACB_*.csv</c> by <see cref="MacbTimelineRunner"/>), one host after the other, then the
/// super timelines start. Ticked by default (<see cref="IngestDefaults"/>); their counts join the run's one completion message.
/// </summary>
public sealed partial class TriageViewModel
{
    /// <summary>Cancelled by Stop: ends a bodyfile conversion that is not a job.</summary>
    private CancellationTokenSource? _macbCts;

    /// <summary>Builds the MACB timeline of every row, one after the other, then hands over to the super timelines (<paramref name="timelines"/>) or reports the run.</summary>
    private async Task BuildMacbTimelinesAsync(IReadOnlyList<PlannedJobItem> rows, IReadOnlyList<PlannedJobItem> timelines, string caseDir)
    {
        var outcomes = new List<MacbTimelineOutcome>();
        var cts = _macbCts = new CancellationTokenSource();
        if (_stopRequested) cts.Cancel();
        try
        {
            foreach (var row in rows)
            {
                if (_stopRequested) { row.IsWaiting = false; continue; }
                try { outcomes.Add(await BuildMacbTimelineAsync(row, caseDir, cts.Token)); }
                catch (Exception ex)
                {
                    AppLog.Error("MACB timeline failed", ex);
                    if (row.Jobs.Count == 0) ListMacbNotStarted(row, caseDir, HostOf(row), "Not started: " + ex.Message);
                    outcomes.Add(new MacbTimelineOutcome(HostOf(row), MacbTimelineState.Failed, Error: $"{ex.Message} (details in {AppLog.LogPath})"));
                }
                finally
                {
                    row.IsBusy = false;
                    row.IsWaiting = false;
                }
            }
        }
        catch (Exception ex) { AppLog.Error("MACB timelines failed", ex); }
        finally
        {
            _macbCts = null;
            foreach (var row in rows) { row.IsBusy = false; row.IsWaiting = false; }
        }
        // The rows count as one job each in the run's completion message (a stopped or failed row did not succeed).
        var built = outcomes.Count(o => o.State == MacbTimelineState.Built);
        var (total, failed) = _runCounts;
        _runCounts = (total + rows.Count, failed + Math.Max(rows.Count - built, 0));
        var (text, _) = MacbTimelineSummary.Describe(outcomes);
        AppLog.Info("Ingest MACB timelines: " + text);
        if (timelines.Count > 0)
        {
            _ = BuildSupertimelinesAsync(timelines, caseDir);   // IsBuildingTimeline stays set until the last one is done
            return;
        }
        try
        {
            var (t, f) = _runCounts;
            var outcome = RunOutcome(t, f, _runSkipped);
            _runCounts = (0, 0);
            ShowNextSteps = !_stopRequested;
            ReportRun(outcome.Text, outcome.Severity);
        }
        finally { IsBuildingTimeline = false; }
    }

    /// <summary>The $MFT of a live volume or a volume root has no containing folder to check; every other path must exist.</summary>
    private static bool MftExists(string path) =>
        File.Exists(path) || Path.GetDirectoryName(path) is not { } dir || string.Equals(dir.TrimEnd('\\', '/'), Path.GetPathRoot(path)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Lists a MACB timeline that did not start on the Jobs page (a Failed job holding the reason) and on its row.</summary>
    private void ListMacbNotStarted(PlannedJobItem row, string caseDir, string host, string reason)
    {
        try
        {
            var job = MacbTimelineRunner.EnqueueNotStarted(S.Queue, caseDir, row.Planned.Spec.OutputDir, host, reason);
            AppServices.OnUi(() => { row.AttachJob(job); _timelineJobs.Add(job); });
        }
        catch (Exception ex) { AppLog.Error("Could not list the MACB timeline that did not start on the Jobs page", ex); }
    }

    private async Task<MacbTimelineOutcome> BuildMacbTimelineAsync(PlannedJobItem row, string caseDir, CancellationToken ct)
    {
        var host = HostOf(row);
        var mfts = IngestSteps.Sources(row.Planned).ToList();
        var timelineDir = row.Planned.Spec.OutputDir;
        var drive = MacbTimelineRunner.DriveFor(row.Planned.Artifact.System?.RootPath ?? row.Planned.Artifact.SystemRoot);
        var missing = mfts.FirstOrDefault(p => !MftExists(p));
        string? reason = mfts.Count == 0 ? "Not started: no $MFT file for the filesystem timeline."
            : missing != null ? $"Not started: the $MFT file does not exist: {missing}" : null;
        if (reason == null)
        {
            var probe = MacbTimelineRunner.MftBodySpec(mfts[0], timelineDir, "probe", 0, 1, drive, null);
            if (TriageRunCheck.ToolMissing(probe, S.Locator, S.Queue.PreferredFlavor) is { } tool)
                reason = $"Not started: {Path.GetFileName(tool)} is not installed (Open setup, or Tools).";
        }
        if (reason != null)
        {
            AppLog.Warn($"Ingest MACB timeline ({host}) was not started: {reason}");
            ListMacbNotStarted(row, caseDir, host, reason);
            return new MacbTimelineOutcome(host, MacbTimelineState.NotStarted, Error: reason);
        }
        row.IsBusy = true;
        row.IsWaiting = false;
        var r = await MacbTimelineRunner.RunAsync(S.Queue, caseDir, timelineDir, mfts, drive, row.Planned.Spec.Provenance, onJob: j => AppServices.OnUi(() =>
        {
            row.AttachJob(j);
            _timelineJobs.Add(j);
            j.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EzForensics.Core.Execution.Job.State)) RefreshRunGate(); };
            if (_stopRequested) j.Cancel();
        }), ct: ct);
        return r.Succeeded ? new MacbTimelineOutcome(host, MacbTimelineState.Built, r.Rows, r.OutputCsv)
            : r.Cancelled ? new MacbTimelineOutcome(host, MacbTimelineState.Cancelled)
            : new MacbTimelineOutcome(host, MacbTimelineState.Failed, Error: r.Error);
    }
}
