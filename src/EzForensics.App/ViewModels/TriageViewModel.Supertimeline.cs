using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Plaso;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// The "Supertimeline (plaso): &lt;Host&gt;" planned rows, one per host (<see cref="IngestSteps.Supertimelines"/>): the last steps of a run
/// (log2timeline + psort on the host's evidence, as jobs, into <c>&lt;case&gt;\&lt;Host&gt;\Timeline</c>), started once every other job of
/// the run has finished, one plaso run at a time. Shown while plaso is available, not ticked by default (<see cref="IngestDefaults"/>); the
/// plaso options are the Tools > Timeline ones (<see cref="PlasoOptionsViewModel"/>, saved in AppSettings).
/// </summary>
public sealed partial class TriageViewModel
{
    /// <summary>A super timeline of the run is waiting for its manifest records or building (the extracted copy is still in use).</summary>
    [ObservableProperty] private bool _isBuildingTimeline;

    public bool PlasoAvailable => S.Plaso?.Selected != null;

    private void InitSupertimeline()
    {
        S.PlasoChanged += RefreshSupertimelineRow;
        S.EnsurePlasoProbed();
    }

    partial void OnIsBuildingTimelineChanged(bool value)
    {
        DeleteExtractedCopyCommand.NotifyCanExecuteChanged();
        RefreshRunGate();
    }

    /// <summary>plaso found or removed after the scan: adds the timeline rows (unticked) at the end of the plan, or removes them while none runs.</summary>
    private void RefreshSupertimelineRow()
    {
        if (Planned.Count == 0 || RefuseWhileRunning("super timeline re-plan")) return;
        var rows = Planned.Where(p => p.IsSupertimeline).ToList();
        if (PlasoAvailable == (rows.Count > 0)) return;
        var plan = Planned.OrderBy(p => p.Sequence).ToList();
        if (rows.Count > 0)
        {
            if (rows.Any(r => r.IsWaiting || r.IsBusy)) return;
            foreach (var row in rows)
            {
                row.PropertyChanged -= OnItemChanged;
                Planned.Remove(row);
                plan.Remove(row);
            }
        }
        else
        {
            var sources = StepSources();
            var caseDir = CaseFolder.Trim();
            var tools = plan.Where(p => !IngestSteps.IsStep(p.Planned)).Select(p => p.Planned).ToList();
            if (sources.Count == 0 || caseDir.Length == 0 || tools.Count == 0) return;
            var added = IngestSteps.Supertimelines(caseDir, tools, sources);
            if (added.Count == 0) return;
            foreach (var p in added)
            {
                var item = new PlannedJobItem(p);
                // plaso found after a restored session: the row gets its saved tick (one per host)
                if (_restoredTicks != null && _restoredTicks.TryGetValue(item.Key, out var on)) item.IsSelected = on;
                item.PropertyChanged += OnItemChanged;
                Planned.Add(item);
                plan.Add(item);
            }
        }
        AfterPlanChanged(plan);
        RefreshAvailability();
    }

    /// <summary>
    /// The run's one completion message: the other jobs' counts (kept by CheckRunFinished) plus the super timelines (one job each), as
    /// "All N jobs succeeded. See Jobs..." with a Jobs button; the timelines' details are in their Result and in the log.
    /// </summary>
    private void ReportTimeline(IReadOnlyList<SupertimelineOutcome> outcomes, int rowCount, string detail, InfoSeverity severity)
    {
        var (total, failed) = _runCounts;
        total += rowCount;
        failed += Math.Max(rowCount - outcomes.Count(o => o.State == SupertimelineState.Built), 0);
        AppLog.Info("Ingest super timelines: " + detail);
        var text = RunOutcome(total, failed, _runSkipped);
        _runCounts = (0, 0);
        ShowNextSteps = !_stopRequested;
        ReportRun(text.Text, text.Severity);
    }

    /// <summary>Builds the super timeline of every row, one plaso run after the other; reports them in one message at the end.</summary>
    private async Task BuildSupertimelinesAsync(IReadOnlyList<PlannedJobItem> rows, string caseDir)
    {
        var outcomes = new List<SupertimelineOutcome>();
        var severity = InfoSeverity.Success;
        try
        {
            foreach (var row in rows)
            {
                if (_stopRequested) { row.IsWaiting = false; continue; }
                try { outcomes.Add(await BuildSupertimelineAsync(row, caseDir)); }
                catch (Exception ex)
                {
                    AppLog.Error("plaso super timeline failed", ex);
                    if (row.Jobs.Count == 0) ListNotStarted(row, caseDir, HostOf(row), "Not started: " + ex.Message);
                    outcomes.Add(new SupertimelineOutcome(HostOf(row), SupertimelineState.Failed, Error: $"{ex.Message} (details in {AppLog.LogPath})"));
                    severity = InfoSeverity.Error;
                }
                finally
                {
                    row.IsBusy = false;
                    row.IsWaiting = false;
                }
            }
            var (text, ok) = SupertimelineSummary.Describe(outcomes);
            ReportTimeline(outcomes, rows.Count, text, ok ? InfoSeverity.Success : severity == InfoSeverity.Error ? severity : InfoSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error("plaso super timelines failed", ex);
            ReportTimeline(outcomes, rows.Count, $"The plaso super timelines failed: {ex.Message} (details in {AppLog.LogPath})", InfoSeverity.Error);
        }
        finally
        {
            foreach (var row in rows) { row.IsBusy = false; row.IsWaiting = false; }
            IsBuildingTimeline = false;
        }
    }

    private static string HostOf(PlannedJobItem row) => IngestSteps.HostDisplay(IngestSteps.SupertimelineHost(row.Planned));

    /// <summary>Builds the plaso super timeline of <paramref name="row"/>'s sources into its host's Timeline folder; the jobs show on the row.</summary>
    private void ListNotStarted(PlannedJobItem row, string caseDir, string host, string reason)
    {
        try
        {
            var job = PlasoTimelineRunner.EnqueueNotStarted(S.Queue, caseDir, row.Planned.Spec.OutputDir, host, reason);
            AppServices.OnUi(() => { row.AttachJob(job); _timelineJobs.Add(job); });
        }
        catch (Exception ex) { AppLog.Error("Could not list the super timeline that did not start on the Jobs page", ex); }
    }

    private async Task<SupertimelineOutcome> BuildSupertimelineAsync(PlannedJobItem row, string caseDir)
    {
        var host = HostOf(row);
        var install = S.Plaso?.Selected;
        var sources = IngestSteps.Sources(row.Planned).ToList();
        var missing = sources.FirstOrDefault(p => !Directory.Exists(p) && !File.Exists(p));
        if (install == null || sources.Count == 0 || missing != null)
        {
            row.NotQueuedReason = install == null ? "Not started: plaso is not installed (set it up in Tools > Timeline)."
                : sources.Count == 0 ? "Not started: no evidence folder for plaso." : $"Not started: the plaso source does not exist: {missing}";
            AppLog.Warn($"Ingest super timeline ({host}) was not started: {row.NotQueuedReason}");
            ListNotStarted(row, caseDir, host, row.NotQueuedReason);
            row.NotQueuedReason = null;   // the failed job now carries the state and the reason
            return new SupertimelineOutcome(host, SupertimelineState.NotStarted, Error: "not started");
        }
        var o = new PlasoRunOptions
        {
            Source = sources[0], AdditionalSources = sources.Skip(1).ToList(), CaseDir = caseDir,
            OutputDir = row.Planned.Spec.OutputDir, Provenance = row.Planned.Spec.Provenance,
        };
        PlasoOptionsViewModel.Apply(o);
        row.IsBusy = true;
        row.IsWaiting = false;
        var r = await PlasoTimelineRunner.RunAsync(S.Queue, install, o, onJob: j => AppServices.OnUi(() =>
        {
            row.AttachJob(j);
            _timelineJobs.Add(j);
            j.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EzForensics.Core.Execution.Job.State)) RefreshRunGate(); };
            if (_stopRequested) j.Cancel();
        }));
        return r.Succeeded ? new SupertimelineOutcome(host, SupertimelineState.Built, r.Events, r.OutputCsv)
            : r.Cancelled ? new SupertimelineOutcome(host, SupertimelineState.Cancelled)
            : new SupertimelineOutcome(host, SupertimelineState.Failed, Error: r.Error);
    }
}
