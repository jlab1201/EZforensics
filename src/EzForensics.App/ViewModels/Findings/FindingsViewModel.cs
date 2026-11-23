using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Assistant;
using EzForensics.Core.Execution;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>
/// The Findings page: the case as a story in time. Spectre's analysis runs as a queued job (after Ingest, or "Analyse case" / "Update"
/// here); the page shows a status line, the timeline, the key events and the finding editor, and the report in a flyout.
/// Split like <c>ResultsViewModel</c>: .Moments (timeline, key events), .Review (list, editor, approvals, suggestions), .Report.
/// Every engine call is awaited off the UI thread by the client, and failures go to the banner.
/// </summary>
public sealed partial class FindingsViewModel : PageViewModel
{
    /// <summary>The analysis phase the Spectre analysis job goes through (the report, phase 6, is made from the Report flyout).</summary>
    public const int AnalysisPhase = SpectreAnalysisJob.DefaultThroughPhase;

    public override string Title => PageNames.Findings;
    public override SpectrePage AssistantPage => SpectrePage.Findings;
    public override bool ShowsAssistant => true;

    private readonly Func<string?> _caseDir;
    private readonly Func<CancellationToken, Task<bool>> _isAvailable;
    private readonly Func<JobQueue?> _queue;
    private readonly Func<string, Task<(Job? Job, string? Reason)>> _enqueue;
    private SpectreFindingsClient? _client;
    private string? _openedDir;
    private JobQueue? _hookedQueue;
    private Job? _watchedJob;

    [ObservableProperty] private bool _hasCase;
    [ObservableProperty] private string _caseLabel = "";

    // ---- engine and analysis state (the status line)
    [ObservableProperty] private bool _engineChecked;
    [ObservableProperty] private bool _engineAvailable;
    [ObservableProperty] private string _engineText = "Checking Spectre…";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCaseId))] private string? _caseId;
    [ObservableProperty] private int _lastPhase;
    [ObservableProperty] private DateTime? _analysedUtc;
    [ObservableProperty] private bool _evidenceChanged;
    [ObservableProperty] private string _evidenceChangedText = "";
    /// <summary>The "files changed" hint was dismissed for this case (shows again when another case opens).</summary>
    [ObservableProperty] private bool _evidenceHintDismissed;
    [ObservableProperty] private Job? _runningJob;
    [ObservableProperty] private string _runResult = "";

    public bool HasCaseId => !string.IsNullOrEmpty(CaseId);
    public string? CaseDir => _caseDir();
    /// <summary>Spectre has finished an analysis of this case (phase 3 or later): there is something to import.</summary>
    public bool HasEngineResults => HasCaseId && LastPhase >= 3;
    /// <summary>The case analysis has items: the page shows them, whether or not Spectre has run.</summary>
    public bool HasAnalysis => _all.Count > 0;
    public bool IsAnalysing => RunningJob != null;
    public bool ShowAnalyse => EngineAvailable && HasCase && !IsAnalysing;
    /// <summary>"Run Spectre" until Spectre has results for the case, then "Re-run Spectre".</summary>
    public string AnalyseButtonText => HasEngineResults ? "Re-run Spectre" : "Run Spectre";
    public bool ShowEvidenceHint => HasEngineResults && EvidenceChanged && !IsAnalysing && !EvidenceHintDismissed;

    [RelayCommand]
    private void DismissEvidenceHint()
    {
        EvidenceHintDismissed = true;
        if (EzForensics.App.Services.AppServices.Current is not { } app || EvidenceChangedText.Length == 0) return;
        var list = app.Settings.DismissedHints;
        if (!list.Contains(HintKey)) list.Add(HintKey);
        if (list.Count > 200) list.RemoveRange(0, list.Count - 200);
        try { app.SaveSettings(); } catch (Exception ex) { AppLog.Warn("Could not save the dismissed hint: " + ex.Message); }
    }

    private string HintKey => "findings.evidence|" + (_openedDir ?? "") + "|" + EvidenceChangedText;
    partial void OnEvidenceHintDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowEvidenceHint));
    public bool ShowSetup => EngineChecked && !EngineAvailable;

    /// <summary>What is happening (checking, engine missing, running, queued, not analysed); empty for an analysed case.</summary>
    public string StatusLine
    {
        get
        {
            if (!HasCase) return "";
            if (!EngineChecked) return "Checking Spectre…";
            if (!EngineAvailable) return "";   // the "Spectre: not set up" chip in the header says it
            if (RunningJob is { } job) return RunningText(job);
            return ""; // the empty state and the overview card say the rest
        }
    }

    internal static string RunningText(Job job) => job.State == JobState.Queued
        ? "Spectre analysis is waiting for other jobs to finish…"
        : "Spectre is analysing the case…" + (job.StartUtc is { } s ? $" (started {s.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC)" : "");

    /// <summary>The status line of an analysed case. The date is left out when it is today (UTC).</summary>
    internal static string AnalysedText(DateTime? analysedUtc, int findings, int moments, int needReview, DateTime nowUtc)
    {
        var parts = new List<string>();
        if (analysedUtc is { } t)
            parts.Add("Analysed " + (t.Date == nowUtc.Date ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) + " UTC");
        else parts.Add("Analysed");
        parts.Add(Plural(findings, "finding"));
        parts.Add(Plural(moments, "key event"));
        parts.Add($"{needReview} need{(needReview == 1 ? "s" : "")} review");
        return string.Join(" · ", parts);
    }

    private static string Plural(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    /// <summary>Name recorded with edits and approvals: Options → Examiner name, else the Windows user.</summary>
    public string Examiner => AppServices.Current?.Settings.ExaminerName is { } n && !string.IsNullOrWhiteSpace(n) ? n.Trim() : Environment.UserName;

    /// <param name="client">Tests pass one on a fake caller; null = the shared Spectre connection.</param>
    /// <param name="isAvailable">Tests replace the engine check.</param>
    /// <param name="queue">The Jobs queue the analysis job runs in; null = the app's.</param>
    /// <param name="enqueue">Queues the analysis for a case folder; null = <see cref="SpectreAnalysisQueue.EnqueueAsync"/>.</param>
    public FindingsViewModel(Func<string?> caseDir, SpectreFindingsClient? client = null, Func<CancellationToken, Task<bool>>? isAvailable = null,
        Func<JobQueue?>? queue = null, Func<string, Task<(Job? Job, string? Reason)>>? enqueue = null)
    {
        _caseDir = caseDir;
        _client = client;
        _isAvailable = isAvailable ?? (ct => AssistantSpectre.IsAvailableAsync(caseDir(), ct));
        _queue = queue ?? (() => AppServices.Current?.Queue);
        _enqueue = enqueue ?? (dir => _queue() is { } q ? SpectreAnalysisQueue.EnqueueAsync(q, dir, CancellationToken.None)
                                                      : Task.FromResult<(Job?, string?)>((null, "The Jobs queue is not available.")));
        Moments.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasMoments)); OnPropertyChanged(nameof(CanPresent)); PresentCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ShowNoMoments)); StatusChanged(); };
    }

    private SpectreFindingsClient Client => _client ??= AssistantSpectre.CreateFindingsClient(_caseDir);

    public override void OnNavigatedTo()
    {
        var dir = CaseDir;
        var has = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        HasCase = has;
        CaseLabel = has ? Path.GetFileName(dir!.TrimEnd('\\', '/')) : "";
        HookQueue();
        if (!has) return;
        if (!string.Equals(dir, _openedDir, StringComparison.OrdinalIgnoreCase) || LinkChangedSinceLoad(dir!))
            _ = GuardAsync(() => OpenCaseAsync(dir!), "Could not open the Review page");
        else { _ = CheckEvidenceAsync(dir!); _ = GuardAsync(() => ReloadStoreAsync(dir!), "Could not read the case analysis"); }
    }

    /// <summary>Load demo case from this page: the shell does not navigate to the page already on screen, so open the new case here.</summary>
    protected override void OnDemoCaseLoaded() => OnNavigatedTo();

    private DateTime? _loadedLinkStamp;

    private static DateTime? LinkStamp(string dir)
    {
        try
        {
            var p = Path.Combine(dir, AssistantConsent.FolderName, SpectreCaseLink.FileName);
            return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The case link was rewritten (an analysis finished while the page was not shown): reload on the next visit.</summary>
    private bool LinkChangedSinceLoad(string dir) => LinkStamp(dir) != _loadedLinkStamp;

    /// <summary>Opens the case: the page shows the case analysis store at once; then it checks the engine and imports Spectre's results when there are newer ones.</summary>
    internal async Task OpenCaseAsync(string dir)
    {
        _openedDir = dir;
        HasCase = Directory.Exists(dir);
        if (HasCase) CaseLabel = Path.GetFileName(dir.TrimEnd('\\', '/'));
        ResetCase();
        EngineChecked = false; EngineText = "Checking Spectre…";
        RefreshRunningJob();
        if (HasCase)
        {
            await LoadHostInfoAsync();
            if (!string.Equals(dir, _openedDir, StringComparison.OrdinalIgnoreCase)) return;
            OpenStore(dir);
            await RefreshSyncBadgeAsync(dir);
        }
        var ok = await _isAvailable(CancellationToken.None);
        if (!string.Equals(dir, _openedDir, StringComparison.OrdinalIgnoreCase)) return;   // the case changed meanwhile
        EngineAvailable = ok; EngineChecked = true;
        if (!ok)
        {
            EngineText = "Spectre is not set up yet. Install it from the Tools page.";
            return;
        }
        try { var st = await Client.StatusAsync(CancellationToken.None); EngineText = st.Version != null ? $"Spectre {st.Version} is ready." : "Spectre is ready."; }
        catch (Exception ex) when (ex is SpectreToolException or IOException or TimeoutException) { EngineText = "Spectre is running."; AppLog.Warn("spectre_status: " + ex.Message); }
        await LoadLinkAndDataAsync(dir);
    }

    private async Task LoadLinkAndDataAsync(string dir)
    {
        _loadedLinkStamp = LinkStamp(dir);
        var link = SpectreCaseLink.Load(dir);
        if (link != null) { CaseId = link.EngineCaseId; LastPhase = link.LastPhase ?? 0; }
        AnalysedUtc = LastPhase >= 3 ? _loadedLinkStamp : null;   // the link is rewritten when a run finishes
        StatusChanged();
        if (HasEngineResults)
        {
            await ImportFromEngineAsync(dir);
            _ = CheckEvidenceAsync(dir);
            await RefreshSyncBadgeAsync(dir);
        }
    }

    /// <summary>Has the case's evidence changed since it was handed to Spectre? Shows a dismissable hint when it has.</summary>
    private async Task CheckEvidenceAsync(string dir)
    {
        if (!HasEngineResults) return;
        try
        {
            var check = await Task.Run(() => SpectreCaseLink.EvidenceChanged(dir));
            if (!string.Equals(dir, _openedDir, StringComparison.OrdinalIgnoreCase)) return;
            EvidenceChanged = check.Known && check.Changed;
            EvidenceChangedText = EvidenceChanged ? EvidenceText(check) : "";
            // a hint dismissed before stays hidden; new or other changed files give a new text, so it shows again
            EvidenceHintDismissed = EvidenceChanged && EzForensics.App.Services.AppServices.Current?.Settings.DismissedHints.Contains(HintKey) == true;
        }
        catch (Exception ex) { AppLog.Warn("Evidence check failed: " + ex.Message); }
    }

    internal static string EvidenceText(EvidenceCheck c)
    {
        var parts = new List<string>();
        if (c.Added.Count > 0) parts.Add($"{c.Added.Count} new");
        if (c.Modified.Count > 0) parts.Add($"{c.Modified.Count} changed");
        if (c.Removed.Count > 0) parts.Add($"{c.Removed.Count} removed");
        return "The case files changed since the analysis (" + string.Join(", ", parts) + ").";
    }

    private void ResetCase()
    {
        _store = null; _adapted = new(); _enginePlain = new(); _engineStale = false; _flags = Array.Empty<FlagCsv>(); SyncChangeCount = 0; CanUseMyAnalysis = false;
        CaseId = null; LastPhase = 0; AnalysedUtc = null; RunResult = ""; EvidenceChanged = false; EvidenceChangedText = ""; EvidenceHintDismissed = false;
        ResetReview();
        HostFilter = null; BucketFilter = null;   // another case opens on All computers
        ResetMoments();
        ResetReport();
        IsReportOpen = false;
        SelectedTab = FindingsTab.Overview;
        if (_walkthrough != null) _walkthrough.IsOpen = false;
        _evidencePanel?.Close();
        _detailEvidence?.Close();
    }

    [RelayCommand]
    private void OpenSpectreSetup() => S.OpenToolsConfiguration(ToolsSection.Spectre);

    /// <summary>The "Spectre: not set up" chip: checks again first (it may have been set up since), then opens the set-up page if it still is not.</summary>
    [RelayCommand]
    private async Task SpectreChipAsync()
    {
        await RecheckEngineAsync();
        if (ShowSetup) OpenSpectreSetup();
    }

    [RelayCommand]
    private async Task RecheckEngineAsync()
    {
        if (CaseDir is { } dir && Directory.Exists(dir)) await GuardAsync(() => OpenCaseAsync(dir), "Could not check Spectre");
    }

    // ------------------------------------------------------------ the analysis job

    private bool CanAnalyse() => EngineAvailable && HasCase && !IsAnalysing;

    /// <summary>"Analyse case" and "Update": queues the Spectre analysis job (hand-off, then phases 1-5); it runs in the queue but is not listed in the Jobs list.</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyse))]
    private Task AnalyseAsync() => GuardAsync(async () =>
    {
        if (CaseDir is not { } dir) return;
        RunResult = "";
        var (job, reason) = await _enqueue(dir);
        if (job == null) { ShowMessage("Spectre analysis not queued: " + (reason ?? "unknown reason"), InfoSeverity.Warning); return; }
        Watch(job);
    }, "Could not queue the Spectre analysis");

    /// <summary>Cancels the queued or running analysis job (it is not in the Jobs list).</summary>
    [RelayCommand]
    private void CancelAnalysis() => (RunningJob ?? _watchedJob)?.Cancel();

    private void HookQueue()
    {
        var q = _queue();
        if (q == null || ReferenceEquals(q, _hookedQueue)) return;
        if (_hookedQueue != null) _hookedQueue.Jobs.CollectionChanged -= OnQueueChanged;
        _hookedQueue = q;
        q.Jobs.CollectionChanged += OnQueueChanged;
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => Post(RefreshRunningJob);

    /// <summary>Follows the case's queued or running analysis job (also one queued by Ingest).</summary>
    private void RefreshRunningJob()
    {
        HookQueue();
        if (_hookedQueue is not { } q) return;
        var active = q.Jobs.LastOrDefault(j => SpectreAnalysisJob.Is(j) && !j.IsFinished && SpectreAnalysisQueue.SameDir(j.CaseDir, CaseDir));
        if (active != null && !ReferenceEquals(active, _watchedJob)) Watch(active);
        else if (active == null && RunningJob != null && RunningJob.IsFinished) RunningJob = null;
    }

    private void Watch(Job job)
    {
        if (_watchedJob != null) _watchedJob.PropertyChanged -= OnWatchedJobChanged;
        _watchedJob = job;
        job.PropertyChanged += OnWatchedJobChanged;
        RunningJob = job.IsFinished ? null : job;
        if (job.IsFinished) OnJobFinished(job);
    }

    private void OnWatchedJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Job job) return;
        if (e.PropertyName == nameof(Job.State)) Post(() =>
        {
            if (!ReferenceEquals(job, _watchedJob)) return;
            if (job.IsFinished) { RunningJob = null; OnJobFinished(job); }
            else StatusChanged();
        });
        else if (e.PropertyName == nameof(Job.StartUtc)) Post(StatusChanged);
    }

    private void OnJobFinished(Job job)
    {
        if (job.State == JobState.Succeeded)
        {
            RunResult = "";
            if (CaseDir is { } dir && SpectreAnalysisQueue.SameDir(job.CaseDir, dir))
                _ = GuardAsync(() => ReloadAfterRunAsync(dir), "Could not load the analysis");
            return;
        }
        RunResult = job.State switch
        {
            JobState.Cancelled => "The Spectre analysis was cancelled.",
            _ => "The Spectre analysis did not finish" + (string.IsNullOrWhiteSpace(job.StatusReason) ? "." : ": " + job.StatusReason),
        };
        ShowMessage(RunResult, InfoSeverity.Warning);
        StatusChanged();
    }

    /// <summary>After a run: re-reads the link (new case id, phase) and the findings and timeline, keeping the selection.</summary>
    internal async Task ReloadAfterRunAsync(string dir)
    {
        if (!string.Equals(dir, _openedDir, StringComparison.OrdinalIgnoreCase)) return;   // another case is open; it loads on its next visit
        EvidenceChanged = false; EvidenceChangedText = "";
        await LoadLinkAndDataAsync(dir);
    }

    /// <summary>Runs on the UI thread (job events come from the queue's threads); inline when there is no dispatcher (tests).</summary>
    private static void Post(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess() || !d.Thread.IsAlive || d.HasShutdownStarted) a(); else d.BeginInvoke(a);
    }

    partial void OnEngineAvailableChanged(bool value) => StatusChanged();
    partial void OnEngineCheckedChanged(bool value) => StatusChanged();
    partial void OnEngineTextChanged(string value) => StatusChanged();
    partial void OnHasCaseChanged(bool value) => StatusChanged();
    partial void OnLastPhaseChanged(int value) => StatusChanged();
    partial void OnAnalysedUtcChanged(DateTime? value) => StatusChanged();
    partial void OnRunningJobChanged(Job? value) => StatusChanged();
    partial void OnEvidenceChangedChanged(bool value) => StatusChanged();

    private void StatusChanged()
    {
        foreach (var n in new[] { nameof(StatusLine), nameof(HasAnalysis), nameof(IsAnalysing), nameof(ShowAnalyse), nameof(ShowEvidenceHint), nameof(ShowSetup),
                                  nameof(PendingApprovals), nameof(ReportButtonText), nameof(AnalyseButtonText), nameof(ShowEmptyState), nameof(HasEngineResults), nameof(CanPresent) })
            OnPropertyChanged(n);
        AnalyseCommand.NotifyCanExecuteChanged();
        PresentCommand.NotifyCanExecuteChanged();
        StoryChanged();
    }
}
