using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Common;
using EzForensics.Core.Execution;

namespace EzForensics.App.ViewModels;

/// <summary>Grid row wrapper adding a live duration.</summary>
public sealed partial class JobRow : ObservableObject
{
    public JobRow(Job job)
    {
        Job = job;
        // a job already finished when the page opened: its end time orders it among the finished rows
        if (job.IsFinished) FinishedStamp = job.EndUtc?.Ticks ?? 0;
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Job.State)) FinishedStamp = job.IsFinished ? FinishedStamp ?? RunOrder.NextStamp() : null;
            if (e.PropertyName is nameof(Job.StartUtc) or nameof(Job.EndUtc) or nameof(Job.State)) Tick();
        };
    }

    public Job Job { get; }

    /// <summary>When the job finished (null while queued / running), for the live order of the Jobs grid.</summary>
    public long? FinishedStamp { get; private set; }

    public RunPhase Phase => Job.State switch
    {
        JobState.Running => RunPhase.Running,
        JobState.Queued => RunPhase.Queued,
        _ => RunPhase.Finished,
    };

    public string Duration => FormatDuration(Job);

    /// <summary>"m:ss" (or "h:mm:ss") since the job started, up to its end; "" before it started.</summary>
    public static string FormatDuration(Job job)
    {
        if (job.StartUtc is not DateTime s) return "";
        var d = (job.EndUtc ?? DateTime.UtcNow) - s;
        return d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
    }

    public void Tick() => OnPropertyChanged(nameof(Duration));
}

/// <summary>
/// The job list with its selection, details and log: Ingest hosts it (one Jobs list). Errors go to
/// <c>onError</c> (the hosting page's banner).
/// </summary>
public sealed partial class JobListViewModel : ObservableObject
{
    private readonly Action<string, Exception> _onError;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _logCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsFill))]
    private JobRow? _selected;
    /// <summary>The full command line (all lines of a multi-line script); "" when unknown.</summary>
    [ObservableProperty] private string _commandText = "";
    /// <summary>
    /// Command and Log expand and collapse together; closed until a job is picked, and each new pick opens them again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsFill))]
    private bool _isDetailsExpanded;

    /// <summary>The details row fills the remaining height only while a job is selected and the details are expanded.</summary>
    public bool DetailsFill => Selected != null && IsDetailsExpanded;

    public JobListViewModel(Action<string, Exception> onError)
    {
        _onError = onError;
        foreach (var j in S.Queue.Jobs.Where(j => !j.Hidden)) { Rows.Add(new JobRow(j)); j.PropertyChanged += OnJobChanged; }
        Reorder();
        S.Queue.Jobs.CollectionChanged += OnJobsChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            foreach (var r in Rows) if (r.Job.State == JobState.Running) r.Tick();
            AddMissingRows();
        };
        _timer.Start();
    }

    /// <summary>
    /// Safety net: every job of the queue is listed. A job enqueued from a background thread (plaso's second step, a retry) is added to
    /// the queue's list through a posted action; whatever such a hand-off missed is added here, so no executed job is ever absent.
    /// </summary>
    private void AddMissingRows()
    {
        try
        {
            var missing = S.Queue.Jobs.Where(j => !j.Hidden && Rows.All(r => r.Job != j)).ToList();
            if (missing.Count == 0) return;
            foreach (var j in missing) { Rows.Add(new JobRow(j)); j.PropertyChanged += OnJobChanged; }
            ScheduleReorder();
        }
        catch (InvalidOperationException) { /* the list changed while enumerating: the next tick retries */ }
    }

    /// <summary>
    /// The live run order (<see cref="RunOrder"/>, the same as the Ingest planned jobs): running, queued (in queue order), then finished in
    /// the order they finished, the newest at the bottom. Rows are moved as jobs change state; the list never scrolls by itself.
    /// </summary>
    public ObservableCollection<JobRow> Rows { get; } = new();
    public LogBuffer Log { get; } = new();

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) { Rows.Clear(); return; }
        if (e.NewItems != null)
            foreach (Job j in e.NewItems)
            {
                if (j.Hidden) continue;
                if (Rows.Any(r => r.Job == j)) continue;   // already shown by ShowJob
                Rows.Add(new JobRow(j));
                j.PropertyChanged += OnJobChanged;
            }
        ScheduleReorder();
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Job j) return;
        if (Selected?.Job == j && e.PropertyName is nameof(Job.CommandLine) or nameof(Job.State) or nameof(Job.ExitCode) or nameof(Job.StatusReason) or nameof(Job.EndUtc))
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) RefreshDetail(); else dispatcher.BeginInvoke(new Action(RefreshDetail));
        }
        if (e.PropertyName != nameof(Job.State)) return;
        if (Selected?.Job == j) NotifyCommands();
        ScheduleReorder();
    }

    private bool _reorderPending;

    /// <summary>Re-applies the order once the current state changes are done (jobs change state from the queue's thread too).</summary>
    private void ScheduleReorder()
    {
        if (_reorderPending) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) { Reorder(); return; }
        _reorderPending = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _reorderPending = false;
            Reorder();
        }));
    }

    private void Reorder()
    {
        try { RunOrder.Reorder(Rows, r => r.Phase, r => r.Job.QueueSequence, r => r.FinishedStamp); }
        catch (Exception ex) { AppLog.Error("Ordering the jobs failed", ex); }
    }

    private static AppServices S => AppServices.Current;

    private void Guard(Action action, string context)
    {
        try { action(); }
        catch (Exception ex) { _onError(context, ex); }
    }

    private string? _commandFromLog;

    private void RefreshDetail()
    {
        var job = Selected?.Job;
        CommandText = job == null ? "" : job.CommandLine ?? _commandFromLog ?? "";
    }

    /// <summary>The advice Spectre sees for a failed job: the likely next step, as the old hint card showed it.</summary>
    private string FailureAdvice()
    {
        var job = Selected?.Job;
        if (job is not { State: JobState.Failed or JobState.TimedOut }) return "";
        var tail = Log.Text;
        if (tail.Length > 4000) tail = tail[^4000..];
        return "Next step: " + JobFailureAdvice.Advise(job.Spec.ToolId, job.ExitCode, job.StatusReason ?? "", tail);
    }

    [RelayCommand]
    private void CopyCommandText() => CopyToClipboard(CommandText);

    [RelayCommand]
    private void CopyLog() => CopyToClipboard(Log.Text);

    private static void CopyToClipboard(string text)
    {
        try { System.Windows.Clipboard.SetDataObject(text, true); }
        catch (Exception ex) { AppLog.Error("Could not copy to the clipboard", ex); }
    }

    partial void OnSelectedChanged(JobRow? value)
    {
        _commandFromLog = null;
        if (value != null) IsDetailsExpanded = true;
        RefreshDetail();
        NotifyCommands();
        _ = LoadLogAsync(value?.Job);
    }

    /// <summary>Selects the row for the job and loads its log.</summary>
    public void ShowJob(Job job)
    {
        var row = Rows.FirstOrDefault(r => r.Job == job);
        if (row == null) { row = new JobRow(job); Rows.Add(row); job.PropertyChanged += OnJobChanged; Reorder(); }
        IsDetailsExpanded = true;
        if (Selected == row) _ = LoadLogAsync(job);
        else Selected = row;
    }

    private void NotifyCommands()
    {
        CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadLogAsync(Job? job)
    {
        _logCts?.Cancel();
        var cts = _logCts = new CancellationTokenSource();
        if (job == null) { Log.Clear(); return; }
        if (job.IsFinished && job.LogPath != null && File.Exists(job.LogPath))
        {
            try
            {
                var text = await Task.Run(() =>
                {
                    using var fs = new FileStream(job.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var r = new StreamReader(fs);
                    return r.ReadToEnd();
                }, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    _commandFromLog = JobLogFormat.Split(text).Command;
                    Log.SetText(text);
                    RefreshDetail();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.SetText($"Could not read {job.LogPath}: {ex.Message}");
            }
        }
        else
        {
            Log.Attach(job, S.Output);
        }
    }

    private bool CanCancel() => Selected != null && !Selected.Job.IsFinished;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => Selected?.Job.Cancel();

    [RelayCommand]
    private void CancelAll() => S.Queue.CancelAll();

    [RelayCommand]
    private void OpenOutput() => Guard(() => ShellOps.OpenFolder(Selected?.Job.Spec.OutputDir), "Could not open the output folder");

    [RelayCommand]
    private void OpenLogFile() => Guard(() => ShellOps.OpenFile(Selected?.Job.LogPath), "Could not open the log file");

    private bool CanRetry() => Selected != null && Selected.Job.IsFinished;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry()
    {
        Guard(() =>
        {
            var job = Selected!.Job;
            var again = S.Queue.Enqueue(job.Spec, job.CaseDir);
            ShowJob(again);
        }, "Could not retry the job");
    }
}
