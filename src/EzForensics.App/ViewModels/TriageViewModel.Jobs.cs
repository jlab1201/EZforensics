using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>
/// The Ingest jobs list: the planned rows first (live run order), then the jobs Ingest did not queue (Zeek, RITA, plaso, computer details...)
/// in queue order; one details pane, optional columns.
/// </summary>
public sealed partial class TriageViewModel
{
    /// <summary>Every job of the queue with the selection, log and details shown under the list.</summary>
    public JobListViewModel Jobs { get; private set; } = null!;
    public IngestColumnsViewModel Columns { get; private set; } = null!;

    /// <summary>What the grid shows: <see cref="Planned"/> rows in their live order, then the other jobs of the queue.</summary>
    public ObservableCollection<IJobListRow> JobRows { get; } = new();

    [ObservableProperty] private IJobListRow? _selectedRow;
    /// <summary>The source and artifacts section is open (it closes itself when a run starts).</summary>
    [ObservableProperty] private bool _isSourceExpanded = true;

    /// <summary>Raised so the view can scroll a row the user picked (ShowJob) into view.</summary>
    public event Action<IJobListRow>? RowScrollRequested;

    private readonly HashSet<Job> _ingestJobs = new();                    // every job Ingest queued (any run): never listed as another job
    private readonly Dictionary<Job, OtherJobItem> _others = new();
    private bool _syncPending;
    private DispatcherTimer? _jobsTimer;

    private void InitJobs()
    {
        Jobs = new JobListViewModel(ShowError);
        Columns = new IngestColumnsViewModel(S.Settings.IngestColumns);
        Planned.CollectionChanged += OnPlannedCollectionChanged;
        Jobs.Rows.CollectionChanged += (_, _) => ScheduleJobRowsSync();
        SyncJobRowsNow();
        _jobsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _jobsTimer.Tick += (_, _) =>
        {
            foreach (var r in JobRows.ToList()) if (r.HasRunningJob) r.TickDuration();
        };
        _jobsTimer.Start();
    }

    private void OnPlannedCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (PlannedJobItem p in e.NewItems) p.JobAttached = j => _ingestJobs.Add(j);
        ScheduleJobRowsSync();
    }

    /// <summary>Rebuilds the grid list once the current changes are done (a job is queued before Ingest attaches it to its row).</summary>
    private void ScheduleJobRowsSync()
    {
        if (_syncPending) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) { SyncJobRowsNow(); return; }
        _syncPending = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _syncPending = false; SyncJobRowsNow(); }));
    }

    private void SyncJobRowsNow()
    {
        try
        {
            var desired = new List<IJobListRow>(Planned.Count + Jobs.Rows.Count);
            desired.AddRange(Planned);
            var seen = new HashSet<Job>();
            foreach (var r in Jobs.Rows)
            {
                if (_ingestJobs.Contains(r.Job) || !seen.Add(r.Job)) continue;
                if (!_others.TryGetValue(r.Job, out var item)) _others[r.Job] = item = new OtherJobItem(r.Job);
                desired.Add(item);
            }
            for (int i = 0; i < desired.Count; i++)
            {
                if (i < JobRows.Count && ReferenceEquals(JobRows[i], desired[i])) continue;
                int j = -1;
                for (int k = i + 1; k < JobRows.Count; k++) if (ReferenceEquals(JobRows[k], desired[i])) { j = k; break; }
                if (j >= 0) JobRows.Move(j, i); else JobRows.Insert(i, desired[i]);
            }
            while (JobRows.Count > desired.Count) JobRows.RemoveAt(JobRows.Count - 1);
            foreach (var gone in _others.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _others[gone].Detach();
                _others.Remove(gone);
            }
        }
        catch (InvalidOperationException) { ScheduleJobRowsSync(); /* a list changed while reading it: try again */ }
    }

    partial void OnSelectedRowChanged(IJobListRow? value) => SyncDetails();

    /// <summary>The details pane follows the selected row's job.</summary>
    private void SyncDetails()
    {
        if (SelectedRow?.LatestJob is { } job) Jobs.ShowJob(job);
        else if (Jobs.Selected != null) Jobs.Selected = null;
    }

    /// <summary>Selects the row that shows the job (a planned row or another job) and loads its log.</summary>
    public void ShowJob(Job job)
    {
        Jobs.ShowJob(job);
        SyncJobRowsNow();
        IJobListRow? row = (IJobListRow?)Planned.FirstOrDefault(p => p.Jobs.Contains(job)) ?? JobRows.OfType<OtherJobItem>().FirstOrDefault(o => o.Job == job);
        if (row == null) return;
        SelectedRow = row;
        Jobs.ShowJob(job);
        RowScrollRequested?.Invoke(row);
    }

    /// <summary>The run has started: the source section folds away so the job list gets the room.</summary>
    private void OnRunStartedUi() => IsSourceExpanded = false;

    /// <summary>Right-click on a row: selects it and shows its job.</summary>
    public void SelectRowForMenu(IJobListRow row)
    {
        SelectedRow = row;
        if (row.LatestJob is { } job) Jobs.ShowJob(job);
    }

    // ------------------------------------------------------------------ optional columns (persisted)

    public void SaveIngestColumns()
    {
        S.Settings.IngestColumns = Columns.ToSettings();
        try { S.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
    }

    public void ShowAllIngestColumns()
    {
        foreach (var c in IngestColumnsViewModel.Optional) Columns.SetVisible(c, true);
        SaveIngestColumns();
    }

    public void ResetIngestColumns()
    {
        Columns.Load(new IngestJobColumns());
        SaveIngestColumns();
    }

    // ------------------------------------------------------------------ row menu (acts on the row's latest job)

    [RelayCommand]
    private void CancelRowJob(IJobListRow? row) => row?.LatestJob?.Cancel();

    [RelayCommand]
    private void RetryRowJob(IJobListRow? row)
    {
        if (row?.LatestJob is not { IsFinished: true } old) return;
        SelectRowForMenu(row);
        Jobs.RetryCommand.Execute(null);
        // a planned row shows the new job; it belongs to Ingest, so it is not listed again as another job
        if (row is PlannedJobItem p && Jobs.Selected?.Job is { } again && again != old) p.AttachJob(again);
    }

    [RelayCommand]
    private void OpenRowOutput(IJobListRow? row)
    {
        if (row?.LatestJob == null) return;
        SelectRowForMenu(row);
        Jobs.OpenOutputCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenRowLog(IJobListRow? row)
    {
        if (row?.LatestJob == null) return;
        SelectRowForMenu(row);
        Jobs.OpenLogFileCommand.Execute(null);
    }
}
