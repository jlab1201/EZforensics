using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Common;
using EzForensics.Core.EventLogs;
using EzForensics.Core.Execution;
using EzForensics.Core.Network;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

public sealed partial class TriageViewModel : PageViewModel
{
    private const string EvtxRuleId = "evtx";
    private CancellationTokenSource? _scanCts;
    private SourceLayout? _layout;
    private List<DetectedArtifact>? _found;
    /// <summary>Every system (host) found under the source; all are planned (a host picker comes later).</summary>
    private IReadOnlyList<DiscoveredSystem> _systems = Array.Empty<DiscoveredSystem>();
    private bool _caseFolderEdited;
    private PcapScanResult? _pcaps;
    private string? _pcapRoot;
    private bool _settingCaseFolder;

    [ObservableProperty] private string? _source;
    [ObservableProperty] private string _caseName = "case";
    [ObservableProperty] private string _caseFolder = "";
    [ObservableProperty] private bool _isScanning;
    /// <summary>A scan result is shown (the Detected artifacts cards are visible).</summary>
    [ObservableProperty] private bool _hasScan;
    [ObservableProperty] private bool _showAdminWarning;
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private int _pcapCount;
    /// <summary>Result of the last Run selected click, shown next to the button (the banner may be scrolled out of view).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RunHint), nameof(HasRunHint))] private string _runStatus = "";
    [ObservableProperty] private InfoSeverity _runStatusSeverity = InfoSeverity.Info;
    /// <summary>The last run skipped jobs because tools are missing: show Open setup next to the button.</summary>
    [ObservableProperty] private bool _showRunOpenSetup;
    /// <summary>The run was stopped and left cancelled jobs: the hint offers "Resume cancelled jobs".</summary>
    [ObservableProperty] private bool _canResumeCancelled;
    /// <summary>A run finished without being stopped: the first (green) hint points to Analyze, the second (blue, amber on failure) has Jobs.</summary>
    [ObservableProperty] private bool _showNextSteps;
    public string NextStepsText => "Ingest finished: Open the Analyze page to review the results.";

    /// <summary>Raised when the run hint below the planned jobs (with the Administrator choice) must be scrolled into view.</summary>
    public event Action? AdminWarningRequested;

    private string? _runCaseDir;
    /// <summary>Every job queued by the current run (computer details, tool jobs, RITA); the super timelines start once all have finished.</summary>
    private readonly List<Job> _runJobs = new();
    /// <summary>The current run's end was handled (or no run is going on).</summary>
    private bool _runFinished = true;
    /// <summary>Stop was clicked during the current run: nothing new starts (no super timelines) and the cancelled jobs can be resumed.</summary>
    private bool _stopRequested;
    /// <summary>The plaso jobs of the super timelines (started after the run's jobs); Stop cancels them too.</summary>
    private readonly List<Job> _timelineJobs = new();
    /// <summary>The "Process tree data" correlator jobs queued after the run onto its row (not counted in the run message); Stop cancels them too.</summary>
    private readonly List<Job> _processJobs = new();
    /// <summary>The plan's rows not left out by a card when the current run started (hosts for the post-run process tree data).</summary>
    private List<PlannedJob> _runPlanned = new();
    /// <summary>Planned row of every run job (Resume re-queues a cancelled job onto its row).</summary>
    private readonly Dictionary<Job, PlannedJobItem> _jobRows = new();
    private bool _runComputerDetails;
    /// <summary>The super timeline rows ticked in the current run (one per host; started after every other job, one at a time).</summary>
    private List<PlannedJobItem> _runTimelines = new();
    /// <summary>The filesystem MACB timeline rows ticked in the current run (one per host; built after every other job and before the super timelines, one at a time).</summary>
    private List<PlannedJobItem> _runMacb = new();

    public TriageViewModel()
    {
        UpdateCaseFolder();
        S.ToolsRescanned += UpdateFirstRun;
        S.ZeekChanged += OnZeekChanged;
        InitRita();
        InitPersistence();
        InitMemory();
        InitSupertimeline();
        InitAvailability();
        InitJobs();
    }

    // ------------------------------------------------------------------ packet captures (Zeek)

    /// <summary>Planned packet captures (checked, not under an unchecked system): at least one.</summary>
    public bool HasPcaps => PcapCount > 0;
    public bool ZeekReady => S.Zeek?.Selected != null;


    private void OnZeekChanged()
    {
        OnPropertyChanged(nameof(ZeekReady));
        RefreshAvailability();
    }

    partial void OnPcapCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPcaps));
        OnZeekChanged();
    }


    /// <summary>
    /// Computer details (first row), the EZ jobs for the detected artifacts, persistence, one Zeek job per capture (+ RITA), memory, then the
    /// plaso super timelines, one per host (last rows, when plaso is available).
    /// </summary>
    private List<PlannedJob> BuildPlan(string caseDir)
    {
        var plan = new List<PlannedJob>();
        // Every hit carries its own system root and host (multi-host sources: <case>\<Host>\<Category>\...), with the host labels edited
        // on the page. Every detected artifact is planned: an unchecked card item only excludes its rows (ApplyExclusions).
        var found = AllFound();
        if (found.Count > 0)
        {
            var problems = new List<string>();
            var rules = S.IngestRules(problems);
            foreach (var pr in problems) AppLog.Warn("Custom Ingest command ignored: " + pr);
            plan.AddRange(TriagePlanner.PlanJobs(found, rules, caseDir));
        }
        plan.AddRange(PlanPersistence(caseDir));
        var pcaps = _pcaps?.Files ?? (IReadOnlyList<PcapFile>)Array.Empty<PcapFile>();
        if (pcaps.Count > 0)
        {
            var evidence = _pcapEvidence;
            var zeekPlan = NetworkTriage.Plan(pcaps, caseDir, S.Zeek?.Selected, _pcapRoot, LabelledSystems(),
                p => evidence.TryGetValue(p, out var e) ? e : null, CaseHosts(caseDir), IngestZeekOptions());
            plan.AddRange(zeekPlan);
            plan.AddRange(PlanRita(zeekPlan, caseDir));
        }
        plan.AddRange(PlanMemory(caseDir));
        return IngestSteps.Compose(plan, caseDir, StepSources(), PlasoAvailable);
    }

    /// <summary>Host evidence of the scanned captures that are not under a system root (read off the UI thread when a scan is applied).</summary>
    private IReadOnlyDictionary<string, PcapEvidence> _pcapEvidence = new Dictionary<string, PcapEvidence>();
    private string? _caseHostsDir;
    private IReadOnlyList<EzForensics.Core.SystemInfo.KnownHost> _caseHosts = Array.Empty<EzForensics.Core.SystemInfo.KnownHost>();

    /// <summary>Hosts of an earlier ingest of the case (their Computer details IPs), cached per case folder; refreshed on every scan.</summary>
    private IReadOnlyList<EzForensics.Core.SystemInfo.KnownHost> CaseHosts(string caseDir)
    {
        if (!string.Equals(_caseHostsDir, caseDir, StringComparison.OrdinalIgnoreCase))
        {
            try { _caseHosts = EzForensics.Core.SystemInfo.ComputerDetailsStore.KnownHosts(caseDir); }
            catch (Exception ex) { AppLog.Error("Reading the case's known hosts failed", ex); _caseHosts = Array.Empty<EzForensics.Core.SystemInfo.KnownHost>(); }
            _caseHostsDir = caseDir;
        }
        return _caseHosts;
    }

    /// <summary>
    /// Folders the computer details and the super timeline read: this ingest's extracted archive roots, else the source folder; none for a
    /// single evidence file.
    /// </summary>
    private IReadOnlyList<string> StepSources()
    {
        if (_effectiveRoots is { Count: > 0 } roots) return roots.ToList();
        if (_summaryIsFile || string.IsNullOrEmpty(_summarySource) || !Directory.Exists(_summarySource)) return Array.Empty<string>();
        return new[] { _summarySource };
    }

    public override string Title => PageNames.Ingest;
    public ObservableCollection<PlannedJobItem> Planned { get; } = new();
    public bool IsAdmin => Elevation.IsAdministrator;
    public bool HasPlan => Planned.Count > 0;

    public override void OnNavigatedTo() => UpdateFirstRun();

    partial void OnSourceChanged(string? value)
    {
        // Explorer's "Copy as path" (and some pastes) add quotes: keep the bare path.
        if (NormalizeSourcePath(value) is var clean && clean != value) { Source = clean; return; }
        OnSourceChangedForArchives(value);
    }

    /// <summary>A pasted path without its surrounding quotes ("C:\x y" → C:\x y); anything else is unchanged (typing is never altered).</summary>
    internal static string? NormalizeSourcePath(string? value)
    {
        var t = value?.Trim();
        return t is { Length: >= 2 } && t[0] == '"' && t[^1] == '"' ? t[1..^1].Trim() : value;
    }

    /// <summary>A file or folder dropped on the page (or pasted from Explorer) becomes the source.</summary>
    public void SetSourceFromShell(string path) => Source = NormalizeSourcePath(path)?.Trim();

    partial void OnCaseNameChanged(string value)
    {
        if (!_caseFolderEdited) UpdateCaseFolder();
    }

    partial void OnCaseFolderChanged(string value)
    {
        if (!_settingCaseFolder) _caseFolderEdited = true;
        RefreshRunGate();
    }

    private void UpdateCaseFolder()
    {
        _settingCaseFolder = true;
        CaseFolder = Path.Combine(S.Settings.CaseRoot, $"{AppServices.Stamp()}_{AppServices.SafeName(CaseName)}");
        _settingCaseFolder = false;
    }

    /// <summary>
    /// A run is going on (<see cref="RunActive"/>): everything that changes the plan is locked (card and row checkboxes, Defaults, All,
    /// Clear, Browse, Scan) so no row's progress is lost. Unlocks when the run has finished; the results stay until the next Run or Scan.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEditPlan), nameof(PagefileEditable))] private bool _isRunLocked;

    /// <summary>The plan can be changed (no run going on).</summary>
    public bool CanEditPlan => !IsRunLocked;

    partial void OnIsRunLockedChanged(bool value)
    {
        foreach (var p in Planned) p.IsLocked = value;
        ApplySelectionCommand.NotifyCanExecuteChanged();
        BrowseSourceCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        ExtractArchivesCommand.NotifyCanExecuteChanged();
        // re-evaluates the cards' checkboxes (and, once unlocked, the rows' tools that were left alone during the run)
        RefreshAvailability();
    }

    /// <summary>Browse…: one dialog for a folder (drive, KAPE output, mounted image) or a single evidence file (capture, memory image, archive).</summary>
    [RelayCommand(CanExecute = nameof(CanEditPlan))]
    private void BrowseSource()
    {
        var p = Dialogs.PickFileOrFolder("Select an evidence file, or open a folder and click Open to use the folder", Dialogs.EvidenceFileFilter, Source);
        if (p != null) Source = p;
    }

    [RelayCommand]
    private void BrowseCaseFolder()
    {
        var d = Dialogs.PickFolder("Select the case folder", CaseFolder);
        if (d != null) CaseFolder = d;
    }

    [RelayCommand(CanExecute = nameof(CanEditPlan))]
    private async Task ScanAsync()
    {
        if (RefuseWhileRunning("scan")) return;
        var src = Source?.Trim();
        if (string.IsNullOrEmpty(src)) { ShowMessage("Choose a source first: Browse…, paste a path or drop a file or folder here.", InfoSeverity.Warning); return; }
        bool isFile = File.Exists(src);
        if (!isFile && !Directory.Exists(src)) { ShowMessage($"Source does not exist: {src}", InfoSeverity.Warning); return; }
        // An archive (zip / 7z / rar / tar / gz) or a virtual disk as the source: extracted into the case folder, then scanned.
        bool isArchive = isFile && IsArchiveFile(src);

        _scanCts?.Cancel();
        _restoredTicks = null;   // a new scan: the ticks of a restored session no longer apply
        var cts = _scanCts = new CancellationTokenSource();
        IsScanning = true;
        Banner = null;
        if (isArchive) KeepArchiveCaseFolder(src);
        else if (!_caseFolderEdited) UpdateCaseFolder();
        try
        {
            if (S.Zeek == null) { try { await S.RefreshZeekAsync(); } catch (Exception ex) { AppLog.Error("Zeek discovery failed", ex); } }
            if (!S.VolatilityProbed) { try { await S.RefreshVolatilityAsync(); } catch (Exception ex) { AppLog.Error("Volatility discovery failed", ex); } }

            IReadOnlyList<string>? roots = null;
            IReadOnlyDictionary<string, EzForensics.Core.Archives.ArchiveOrigin>? origins = null;
            if (isArchive)
            {
                if (!EnsureCaseFolderForArchive()) return;
                EzForensics.Core.Archives.ArchiveIngestResult? extracted;
                try { extracted = await IngestArchiveAsync(src, cts); }
                catch (EzForensics.Core.Archives.ArchiveIngestException ex) { ShowMessage(ex.Message, InfoSeverity.Error); return; }
                if (extracted == null) return;
                origins = ApplyArchiveResults(src, new[] { extracted });
                roots = _effectiveRoots;
                SetArchiveScanning();
                isFile = false;
            }
            // A plain scan (folder or non-archive file) never reads an earlier extracted copy: the manifest's source path and the computer
            // details must not point there (Extract and scan reuses the extraction when it is asked for again).
            else ResetArchiveState();
            var caseDir = CaseFolder;

            var (scan, pcaps, evidence, truncated, containers) = await Task.Run(() =>
            {
                TriageScanResult s;
                PcapScanResult pc;
                if (roots != null)
                {
                    s = ArtifactScanner.ScanSources(roots, ct: cts.Token, origins: origins);
                    pc = MergePcaps(roots.Select(r => PcapScanner.Scan(r, null, cts.Token)));
                }
                else
                {
                    // A single file (e.g. a .pcap) has no system root: only the capture scan applies.
                    s = isFile
                        ? TriageScanResult.ForFile(src, "Single evidence file: only packet captures and memory images are processed (pick a folder for Windows artifacts).")
                        : ArtifactScanner.ScanSource(src, ct: cts.Token);
                    pc = PcapScanner.Scan(src, null, cts.Token);
                }
                var (ev, tr) = isFile ? (new List<string>(), false) : FindEvidenceArchives(s.Systems, cts.Token);
                var cont = isFile || roots != null ? new List<string>() : FindContainerArchives(src, s.Systems);
                return (s, pc, ev, tr, cont);
            }, cts.Token);
            if (pcaps.Files.Count > 0) await EnsureRitaProbedAsync();
            var pcapRoot = roots != null ? EffectiveSource : isFile ? Path.GetDirectoryName(src) : src;
            await ApplyScanAsync(scan, pcaps, caseDir, src, isFile, pcapRoot);
            SetEvidenceArchives(evidence, truncated);
            SetContainerArchives(containers);
        }
        catch (OperationCanceledException) { if (isArchive) ShowMessage("Scan cancelled.", InfoSeverity.Warning); }
        catch (Exception ex) { ShowError("Scan failed", ex); }
        finally
        {
            IsArchiveBusy = false;
            if (_scanCts == cts) IsScanning = false;
        }
    }

    /// <summary>Shows a scan result on the page (systems, captures, memory images, plan). <paramref name="src"/> is the source as displayed.</summary>
    private async Task ApplyScanAsync(TriageScanResult scan, PcapScanResult pcaps, string caseDir, string src, bool isFile, string? pcapRoot)
    {
        if (RunActive())
        {
            ShowMessage("A run started while scanning: the scan result was not applied. Scan again once the run has finished.", InfoSeverity.Warning);
            return;
        }
        // which host each capture was collected on: evidence inside the captures (at most ~5 s each), off the UI thread
        var evidence = pcaps.Files.Count == 0 ? new Dictionary<string, PcapEvidence>()
            : await Task.Run(() => NetworkTriage.ReadEvidence(pcaps.Files, scan.Systems));
        if (RunActive())
        {
            ShowMessage("A run started while scanning: the scan result was not applied. Scan again once the run has finished.", InfoSeverity.Warning);
            return;
        }
        _pcapEvidence = evidence;
        _caseHostsDir = null;
        var layout = scan.Layout;
        var found = scan.Artifacts;
        _layout = layout;
        _found = found;
        _systems = scan.Systems;
        _pcaps = pcaps;
        _pcapRoot = pcapRoot;
        _summarySource = src;
        _summaryIsFile = isFile;
        RunStatus = "";
        ShowRunOpenSetup = false;
        CanResumeCancelled = false;
        ShowNextSteps = false;
        ShowAdminWarning = false;
        SetSystems(scan.Systems);
        SetPcaps(pcaps);
        SetMemoryImages(scan.MemoryImages, _pcapRoot);
        await UpdatePersistenceRootsAsync(layout, isFile);
        // Archived evidence: computer details and plaso read the extracted copy, i.e. exactly this ingest's extracted roots (one log2timeline
        // run each), never their common parent (<case>\.ezstaging\extracted may hold copies of other archives).
        var plan = BuildPlan(caseDir);

        PcapCount = IncludedPcapCount();
        HasScan = true;
        SetPlan(plan);
        OnZeekChanged();
        RaiseRita();
        // Banner only for an issue: Zeek / Volatility not set up are shown on their Detected artifacts card.
        if (plan.Count == 0 && scan.MemoryImages.Count == 0)
            ShowMessage(isFile ? "This file is not a packet capture (pcap / pcapng), a memory image or an archive; pick a folder for other evidence."
                               : EffectiveSource != null ? "No supported artifacts, packet captures or memory images were found in the extracted archive."
                               : "No supported artifacts, packet captures or memory images were found at this source.", InfoSeverity.Warning);
        else if (EffectiveSource != null && Archives.Any(a => a.HasProblems))
            ShowMessage("Archive extracted and scanned, with warnings: expand the archived evidence line.", InfoSeverity.Warning);
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>Replaces the plan (new rows use their default tick). Computer details stays the first row and the super timelines the last.</summary>
    private void SetPlan(IReadOnlyList<PlannedJob> plan) => SetPlan(plan.Select(p => (p, p.SelectedByDefault)).ToList());

    /// <summary>
    /// Replaces the plan with these rows and their wanted ticks (ordered as <see cref="IngestSteps.Order"/>); rows left out by a card are
    /// excluded (their tick comes back when the card item is ticked again). Refused while a run is going on: its rows' progress must survive.
    /// </summary>
    private void SetPlan(IReadOnlyList<(PlannedJob Planned, bool Selected)> rows)
    {
        if (RefuseWhileRunning("re-plan")) return;
        rows = WithProcessTree(rows);
        foreach (var p in Planned) { p.PropertyChanged -= OnItemChanged; p.ResetRun(); }
        Planned.Clear();
        foreach (var (p, selected) in IngestSteps.Order(rows, r => r.Planned))
        {
            var item = new PlannedJobItem(p) { IsSelected = selected };
            item.PropertyChanged += OnItemChanged;
            Planned.Add(item);
        }
        AfterPlanChanged();
        RefreshAvailability();
        OnPropertyChanged(nameof(SelectAllState));
    }

    /// <summary>
    /// After rows were added or removed: plan sequence (from <paramref name="planOrder"/>, the rows in plan order; default the list as it
    /// is), card exclusions and the summary.
    /// </summary>
    private void AfterPlanChanged(IReadOnlyList<PlannedJobItem>? planOrder = null)
    {
        var order = planOrder ?? Planned;
        for (int i = 0; i < order.Count; i++) { order[i].Sequence = i; order[i].IsLocked = IsRunLocked; }
        OnPropertyChanged(nameof(HasPlan));
        ApplyExclusions();
        ReorderNow();
        UpdateSystemArtifacts();
    }

    /// <summary>The Disk card's "Artifacts:" line of every system: the groups of its planned rows (excluded ones too), in plan order.</summary>
    private void UpdateSystemArtifacts()
    {
        var plan = Planned.OrderBy(p => p.Sequence).Select(p => p.Planned).ToList();
        foreach (var s in Systems) s.ArtifactGroups = ArtifactDetails.ArtifactGroups(plan, s.RootPath);
    }

    /// <summary>True (and logged) while a run is going on: the plan must not change then.</summary>
    private bool RefuseWhileRunning(string what)
    {
        if (!RunActive()) return false;
        AppLog.Info($"Ingest: {what} skipped, a run is going on.");
        return true;
    }

    /// <summary>What the Detected artifacts cards leave out.</summary>
    private IngestCardSelection CardSelection() => new(Choices(),
        MemoryImages.Where(m => !m.IsIncluded).Select(m => m.Path).ToList(),
        Pcaps.Where(p => !p.IsIncluded).Select(p => p.Path).ToList());

    /// <summary>Marks the rows left out by an unchecked system, memory image or capture (greyed and unticked, never removed).</summary>
    private void ApplyExclusions()
    {
        var rows = Planned.ToList();
        var reasons = IngestExclusion.Reasons(rows.Select(r => r.Planned).ToList(), CardSelection());
        for (int i = 0; i < rows.Count; i++) rows[i].ExcludedReason = reasons[i];
        PcapCount = IncludedPcapCount();
        UpdateSelectionSummary();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlannedJobItem.LatestJob) && sender == SelectedRow && SelectedRow?.LatestJob is { } lj && Jobs.Selected?.Job != lj) Jobs.ShowJob(lj);
        if (e.PropertyName == nameof(PlannedJobItem.IsSelected)) UpdateSelectionSummary();
        else if (e.PropertyName is nameof(PlannedJobItem.ExcludedReason) or nameof(PlannedJobItem.UnavailableReason)) OnPropertyChanged(nameof(SelectAllState));
        if (e.PropertyName is nameof(PlannedJobItem.IsSelected) or nameof(PlannedJobItem.Status) or nameof(PlannedJobItem.ExcludedReason)
            or nameof(PlannedJobItem.UnavailableReason))
            ScheduleReorder();
    }

    private bool _reorderPending;

    /// <summary>Re-applies the live order once the current changes are done (several rows usually change together).</summary>
    private void ScheduleReorder()
    {
        if (_reorderPending) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) { ReorderNow(); return; }
        _reorderPending = true;
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _reorderPending = false;
            ReorderNow();
        }));
    }

    /// <summary>
    /// The live order of the planned jobs (<see cref="RunOrder"/>): running, queued (ticked rows before a run), finished in the order they
    /// finished, then unticked, excluded or unavailable rows in plan order. Rows are moved, never re-created, so their state survives.
    /// </summary>
    private void ReorderNow()
    {
        try { RunOrder.Reorder(Planned, p => p.Phase, p => p.Sequence, p => p.FinishedStamp); }
        catch (Exception ex) { AppLog.Error("Ordering the planned jobs failed", ex); }
    }

    private void UpdateSelectionSummary()
    {
        int sel = Planned.Count(p => p.IsSelected);
        SelectionSummary = $"{sel} of {Planned.Count} selected";
        OnPropertyChanged(nameof(SelectAllState));
        RefreshRunGate();
    }

    /// <summary>One entry of the "Custom selection" drop-down: a saved selection, or (<see cref="IsManage"/>) the link to Preferences.</summary>
    public sealed record SelectionMenuItem(string Label, bool IsManage = false);

    /// <summary>Entries of the "Custom selection" drop-down (the saved selections, then "Manage…"); rebuilt each time the menu opens.</summary>
    public ObservableCollection<SelectionMenuItem> SelectionMenu { get; } = new();

    /// <summary>Raised by "Manage…": the shell opens Preferences at the Ingest selections section.</summary>
    public event Action? ManageSelectionsRequested;

    public void RefreshSelectionMenu()
    {
        SelectionMenu.Clear();
        foreach (var s in IngestSelections.Normalize(S.Settings.IngestSelections)) SelectionMenu.Add(new SelectionMenuItem(s.Name));
        SelectionMenu.Add(new SelectionMenuItem("Manage…", IsManage: true));
    }

    /// <summary>Ticks exactly the selectable rows whose rule id is in the picked selection and unticks the others.</summary>
    [RelayCommand(CanExecute = nameof(CanEditPlan))]
    private void ApplySelection(SelectionMenuItem? item)
    {
        if (item == null) return;
        if (item.IsManage) { ManageSelectionsRequested?.Invoke(); return; }
        ApplySelectionSet(IngestSelections.Find(S.Settings.IngestSelections, item.Label));
    }

    /// <summary>Same semantics as the former Defaults button: available rows get the tick, excluded rows keep it for when they come back.</summary>
    public void ApplySelectionSet(IngestSelection selection)
    {
        foreach (var p in Planned.Where(p => p.IsAvailable)) p.SetWanted(IngestSelections.Includes(selection, p.Planned.Artifact.RuleId));
        UpdateSelectionSummary();
    }

    /// <summary>Header checkbox of the first column: true = every selectable row ticked, false = none, null = some (selectable = available and not excluded).</summary>
    public bool? SelectAllState
    {
        get
        {
            var rows = Planned.Where(p => p.IsAvailable && !p.IsExcluded).ToList();
            if (rows.Count == 0) return false;
            int on = rows.Count(p => p.IsSelected);
            return on == 0 ? false : on == rows.Count ? true : null;
        }
    }

    /// <summary>Header checkbox click: ticks every selectable row, or clears them when all are already ticked.</summary>
    public void ToggleSelectAll()
    {
        if (!CanEditPlan) return;
        bool on = SelectAllState != true;
        foreach (var p in Planned.Where(p => p.IsAvailable && !p.IsExcluded)) p.IsSelected = on;
        OnPropertyChanged(nameof(SelectAllState));
    }

    private bool CanRunOrStop() => IsStopMode || CanRunSelected();
    private bool CanRunSelected() => RunBlockedReason == null;

    /// <summary>Jobs queued by this Ingest run are still Queued or Running: the Run button shows Stop (and stays enabled although the plan is locked).</summary>
    public bool IsStopMode => _runJobs.Any(j => j.State is JobState.Queued or JobState.Running)
        || _timelineJobs.Any(j => !j.IsFinished) || (IsBuildingTimeline && !_stopRequested) || (ProcessTreeActive && !_stopRequested);
    public string RunButtonText => IsStopMode ? "Stop" : "Run selected";
    public string RunButtonGlyph => IsStopMode ? "" : "";

    /// <summary>Cancels only this run's jobs (running and queued); other pages' jobs are untouched.</summary>
    private void StopRun()
    {
        _stopRequested = true;
        _macbCts?.Cancel();
        var jobs = _runJobs.Concat(_timelineJobs).Concat(_processJobs).Where(j => !j.IsFinished).ToList();
        AppLog.Info($"Ingest: Stop clicked ({jobs.Count} jobs).");
        // Job.Cancel -> the queue's token -> ProcessRunner kills the whole process tree of a running job; queued jobs end as Cancelled.
        foreach (var j in jobs) j.Cancel();
        OnProcessTreeStopped(jobs);
        RefreshRunGate();
    }

    /// <summary>"Resume cancelled jobs": queues this run's cancelled jobs again (same specs, same rows); Run selected becomes Stop meanwhile.</summary>
    [RelayCommand]
    private void ResumeCancelled()
    {
        try
        {
            var cancelled = _runJobs.Where(j => j.State is JobState.Cancelled or JobState.Skipped).ToList();
            if (_runCaseDir == null) { CanResumeCancelled = false; return; }
            _stopRequested = false;
            var (processResumed, processWaits) = ResumeProcessTree();
            if (cancelled.Count == 0)
            {
                CanResumeCancelled = false;
                if (!processResumed) return;
                // only the Process tree data step was stopped: its jobs run again (or are queued now when none had been queued)
                ClearRunStatus("resuming the process tree data.");
                if (processWaits) QueueProcessTreeData(_runCaseDir);
                RefreshRunGate();
                return;
            }
            AppLog.Info($"Ingest: resuming {cancelled.Count} cancelled jobs.");
            _runFinished = false;
            CanResumeCancelled = false;
            ClearRunStatus($"resuming {cancelled.Count} cancelled jobs.");
            foreach (var old in cancelled)
            {
                _jobRows.TryGetValue(old, out var row);
                old.PropertyChanged -= OnRunJobChanged;
                _runJobs.Remove(old);
                _jobRows.Remove(old);
                var job = S.Queue.Enqueue(old.Spec, old.CaseDir);
                if (row != null) { row.ReplaceJob(old, job); _jobRows[job] = row; }
                job.PropertyChanged += OnRunJobChanged;
                _runJobs.Add(job);
            }
            foreach (var t in _runMacb.Concat(_runTimelines)) t.IsWaiting = true;
            RefreshRunGate();
            CheckRunFinished();
        }
        catch (Exception ex) { ReportRunError(ex); }
    }

    [RelayCommand] private void OpenAnalyzePage() => S.Navigate<ResultsViewModel>();

    /// <summary>Why Run selected is disabled (tooltip and the line next to the button), or null when it can run.</summary>
    public string? RunBlockedReason => TriageRunCheck.BlockedReason(new RunGate(
        Planned.Count, Planned.Count(p => p.IsSelected), HasSystemErrors ? SystemErrorText : null,
        RunActive(), IsMemoryPlanning, string.IsNullOrWhiteSpace(CaseFolder)));

    /// <summary>
    /// The disabled reason when it is an issue the user must fix (a system name, an empty case folder); the other reasons (nothing ticked,
    /// a run in progress, memory planning) only show in the button's tooltip.
    /// </summary>
    private string? RunIssue => Planned.Count > 0 && (HasSystemErrors || string.IsNullOrWhiteSpace(CaseFolder)) ? RunBlockedReason : null;

    /// <summary>Line next to Run selected: a blocking issue, else the last run's problem or its completion (the page's one run message).</summary>
    public string RunHint => RunIssue ?? RunStatus;
    public bool HasRunHint => !string.IsNullOrEmpty(RunHint) && RunHint != _dismissedRunHint;
    /// <summary>The run hint text the user dismissed; the hint stays hidden until its text changes.</summary>
    private string? _dismissedRunHint;

    /// <summary>Dismiss on the run hint (not offered while the Administrator choice is shown).</summary>
    [RelayCommand]
    private void DismissRunHint()
    {
        _dismissedRunHint = RunHint;
        OnPropertyChanged(nameof(HasRunHint));
    }

    /// <summary>Dismiss on the green next-steps hint; the next finished run shows it again.</summary>
    [RelayCommand]
    private void DismissNextSteps() => ShowNextSteps = false;
    public InfoSeverity RunHintSeverity => RunIssue != null ? InfoSeverity.Warning : RunStatusSeverity;
    public string RunButtonToolTip => IsStopMode ? "Cancel the running and queued jobs of this ingest run" : RunBlockedReason ?? "Queue the ticked jobs (output goes to the case folder)";

    partial void OnRunStatusSeverityChanged(InfoSeverity value) => OnPropertyChanged(nameof(RunHintSeverity));

    /// <summary>Re-evaluates Run selected (enabled state, tooltip, hint line).</summary>
    private void RefreshRunGate()
    {
        var wasLocked = IsRunLocked;
        IsRunLocked = RunActive();
        if (IsRunLocked && !wasLocked) OnRunStartedUi();
        RunSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsStopMode));
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(RunButtonGlyph));
        OnPropertyChanged(nameof(RunBlockedReason));
        OnPropertyChanged(nameof(RunHint));
        OnPropertyChanged(nameof(HasRunHint));
        OnPropertyChanged(nameof(RunHintSeverity));
        OnPropertyChanged(nameof(RunButtonToolTip));
    }

    /// <summary>
    /// Shows a run problem or the run's completion next to the Run button (always in view; not also in the banner, so each message appears
    /// once) and in the app log.
    /// </summary>
    private void ReportRun(string message, InfoSeverity severity, bool offerSetup = false)
    {
        RunStatus = message;
        RunStatusSeverity = severity;
        ShowRunOpenSetup = offerSetup;
        if (severity is InfoSeverity.Warning or InfoSeverity.Error) AppLog.Warn("Ingest run: " + message);
        else AppLog.Info("Ingest run: " + message);
    }

    private void ReportRunError(Exception ex)
    {
        AppLog.Error("Could not start the ingest run", ex);
        ReportRun($"Could not start the ingest run: {ex.Message} (details in {AppLog.LogPath})", InfoSeverity.Error);
    }

    /// <summary>No run message (a cancelled or a cleanly started run is not reported; only problems and the completion are).</summary>
    private void ClearRunStatus(string logLine)
    {
        RunStatus = "";
        RunStatusSeverity = InfoSeverity.Info;
        ShowRunOpenSetup = false;
        CanResumeCancelled = false;
        ShowNextSteps = false;
        AppLog.Info("Ingest run: " + logLine);
    }

    /// <summary>
    /// A triage run of this case is still queued / running: a second run would share its staging folders (and publish into the same
    /// category folders while the first is still writing), so Run selected waits until it has finished.
    /// </summary>
    private bool CaseHasActiveTriageJobs()
    {
        var caseDir = CaseFolder?.Trim();
        if (string.IsNullOrEmpty(caseDir)) return false;
        return S.Queue.Jobs.Any(j => !j.IsFinished && j.Spec.Output != null && SameDir(j.CaseDir, caseDir));
    }

    /// <summary>A run is still going on: jobs of this case queued / running, a super timeline waiting / building, or the process tree data waiting / running.</summary>
    private bool RunActive() => CaseHasActiveTriageJobs() || !_runFinished || IsBuildingTimeline || ProcessTreeActive;

    /// <summary>Same folder (full paths, trailing separators and case ignored).</summary>
    internal static bool SameDir(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary><paramref name="path"/> is <paramref name="dir"/> or inside it (C:\case2 is not inside C:\case).</summary>
    internal static bool IsUnder(string? path, string? dir)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            var p = Path.GetFullPath(path).TrimEnd('\\', '/');
            var d = Path.GetFullPath(dir).TrimEnd('\\', '/');
            return string.Equals(p, d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunOrStop))]
    private void RunSelected()
    {
        try
        {
            if (IsStopMode) { StopRun(); return; }
            int sel = Planned.Count(p => p.IsSelected), admin = Planned.Count(p => p.IsSelected && p.RequiresAdmin);
            AppLog.Info($"Ingest: Run selected clicked ({sel} of {Planned.Count} selected, {admin} need Administrator, elevated={IsAdmin}, case={CaseFolder}).");
            if (!IsAdmin && admin > 0)
            {
                // One hint below the planned jobs carries the choice (its buttons show while ShowAdminWarning); it is scrolled into view.
                ShowAdminWarning = true;
                ReportRun("Some selected jobs need Administrator.", InfoSeverity.Warning);
                AdminWarningRequested?.Invoke();
                return;
            }
            StartRun();
        }
        catch (Exception ex) { ReportRunError(ex); }
    }

    [RelayCommand]
    private void RunWithoutAdminJobs()
    {
        ShowAdminWarning = false;
        foreach (var p in Planned.Where(p => p.RequiresAdmin)) p.IsSelected = false;
        if (CanRunSelected()) StartRun();
        else ReportRun("Nothing was queued without the Administrator jobs: " + RunBlockedReason, InfoSeverity.Warning);
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        // the page (source, case, scan result, ticks) is saved first: the elevated instance shows it again without scanning
        if (!SaveSessionAndRestartElevated())
            ReportRun("Restart as Administrator was cancelled: nothing was queued. Choose Skip admin jobs, or try again.", InfoSeverity.Warning);
    }

    [RelayCommand]
    private void DismissAdminWarning()
    {
        ShowAdminWarning = false;
        ClearRunStatus("cancelled at the Administrator choice; nothing was queued.");
    }

    private void StartRun()
    {
        try { StartRunCore(); }
        catch (Exception ex) { ReportRunError(ex); }
    }

    /// <summary>Queues the selected jobs. Every outcome (queued, partly skipped, nothing queued) is reported by <see cref="ReportRun"/>.</summary>
    private void StartRunCore()
    {
        var caseDir = CaseFolder.Trim();
        if (string.IsNullOrEmpty(caseDir)) { ReportRun("Nothing was queued: the case folder is empty. Choose a case folder, then Run selected.", InfoSeverity.Warning); return; }

        var skips = new RunSkips { MemoryNotSetUp = PrepareMemoryRun() };
        // Re-plan if the case folder changed after the scan, keeping the user's ticks.
        var selected = Planned.Where(p => p.IsSelected).OrderBy(p => p.Sequence).ToList();   // run sequence = plan order (the list is in the live order)
        if (_found != null && _layout != null && selected.Count > 0 &&
            selected.Any(s => !IsUnder(s.Planned.Spec.OutputDir, caseDir)))
        {
            var keys = new HashSet<string>(Planned.Where(p => p.WantedSelected).Select(s => s.Key));
            SetPlan(BuildPlan(caseDir).Select(p => (p, keys.Contains(PlannedJobItem.KeyOf(p)))).ToList());
            selected = Planned.Where(p => p.IsSelected).OrderBy(p => p.Sequence).ToList();
        }

        foreach (var p in Planned) p.ResetRun();
        var computerDetails = selected.FirstOrDefault(p => p.IsComputerDetails);
        var timelines = selected.Where(p => p.IsSupertimeline).ToList();
        var macb = selected.Where(p => p.IsMacbTimeline).ToList();
        var processTree = selected.FirstOrDefault(p => p.IsProcessTree);

        // Resolve every selected row first (Zeek / memory placeholders), then check the tools, before anything is queued.
        var zeek = S.Zeek?.Selected;
        var toQueue = new List<(PlannedJobItem Item, JobSpec Spec)>();
        foreach (var p in selected)
        {
            var spec = p.Planned.Spec;
            // Computer details is queued first and the super timelines after every other job (below); RITA rows wait for this run's Zeek
            // jobs: queued after them (EnqueueRita below). Process tree data has no tool of its own: its jobs are built after the run.
            if (IngestSteps.IsStep(p.Planned) || NetworkTriage.IsRita(p.Planned)) continue;
            if (p.Planned.Artifact.RuleId == NetworkTriage.RuleId)
            {
                // Always use the Zeek found now (it may have been installed after the scan).
                if (zeek == null) { skips.ZeekNotSetUp++; p.NotQueuedReason = "Not queued: Zeek is not set up (Tools)."; continue; }
                try { spec = NetworkTriage.Resolve(p.Planned, zeek, IngestZeekOptions()); }
                catch (ArgumentException ex) { skips.ZeekErrors.Add($"{Path.GetFileName(p.MatchedPath)}: {ex.Message}"); p.NotQueuedReason = "Not queued: " + ex.Message; continue; }
            }
            // Memory rows planned before Volatility was set up are placeholders (PrepareMemoryRun re-planned them when it is set up now).
            if (p.Planned.Artifact.RuleId == MemoryTriage.RuleId && !CanRunMemoryRow(p.Planned))
            {
                p.NotQueuedReason = "Not queued: Volatility 3 is not set up (Tools).";
                continue;
            }
            // A memory image or capture alone in an archive has no discovered system: record its archive by path.
            spec = EzForensics.Core.Archives.ArchiveProvenance.Attach(spec, p.Planned.Artifact.MatchedPath, _archiveOrigins);
            toQueue.Add((p, spec));
        }

        // Missing tools (fresh install where the EZ tools were not downloaded yet, tools folder moved, ...): those jobs would only fail
        // at start, so they are not queued and the page names what is missing (Open setup). The locator is rescanned once first, in
        // case the tools were installed after the last scan (setup wizard, restart).
        var preferred = S.Queue.PreferredFlavor;
        if (toQueue.Any(t => TriageRunCheck.ToolMissing(t.Spec, S.Locator, preferred) != null))
        {
            try { S.Locator.Rescan(S.Settings.ToolsRoot); }
            catch (Exception ex) { AppLog.Error("Tools rescan before the ingest run failed", ex); }
        }
        var missing = toQueue.Where(t => TriageRunCheck.ToolMissing(t.Spec, S.Locator, preferred) != null).ToList();
        if (missing.Count > 0)
        {
            skips.MissingToolJobs = missing.Count;
            skips.MissingTools.AddRange(TriageRunCheck.MissingTools(missing.Select(m => m.Spec), S.Locator, preferred).Select(n => Path.GetFileName(n)));
            AppLog.Warn($"Ingest: {missing.Count} selected job(s) not queued, tools not found (tools folder {S.Settings.ToolsRoot}): {string.Join(", ", skips.MissingTools)}");
            foreach (var m in missing)
                m.Item.NotQueuedReason = "Not queued: tool not found (" + Path.GetFileName(TriageRunCheck.ToolMissing(m.Spec, S.Locator, preferred)) + "). Open setup to download it.";
            toQueue = toQueue.Except(missing).ToList();
        }

        if (toQueue.Count == 0 && computerDetails == null && timelines.Count == 0 && processTree == null)
        {
            if (selected.Any(p => NetworkTriage.IsRita(p.Planned)))
                skips.RitaNotes.Add("the RITA jobs read this run's Zeek output, and no Zeek job could be queued.");
            ReportRun(TriageRunCheck.Describe(0, skips, caseDir), InfoSeverity.Warning, offerSetup: skips.MissingToolJobs > 0);
            RefreshRunGate();
            return;
        }

        // EvtxECmd: the selected event IDs (--inc), and what to do with event log output already in the case.
        var evtxRows = toQueue.Select((t, i) => (t, i)).Where(x => x.t.Item.Planned.Artifact.RuleId == EvtxRuleId).ToList();
        if (evtxRows.Count > 0)
        {
            var existing = evtxRows.SelectMany(x => EvtxOutputPolicy.FindExisting(x.t.Spec.OutputDir)).ToList();
            var choice = existing.Count == 0 ? EvtxRerunChoice.CreateNew : EvtxRerunPrompt.Ask(existing, evtxRows.Count);
            if (choice == EvtxRerunChoice.Cancel)
            {
                foreach (var p in Planned) p.ResetRun();
                ClearRunStatus("cancelled at the event log prompt; nothing was queued.");
                RefreshRunGate();
                return;
            }
            foreach (var (t, i) in evtxRows)
            {
                var sp = t.Spec;
                toQueue[i] = (t.Item, EvtxOutputPolicy.Apply(sp, choice));
            }
        }

        Directory.CreateDirectory(caseDir);
        // Manifest: sourcePath = what the user picked (folder, drive, archive...); archived evidence also records sourceRoots = this
        // ingest's extracted roots (the jobs and computer details read those, never their common parent).
        var userSource = Source?.Trim();
        S.Queue.SetSource(string.IsNullOrEmpty(userSource) ? null : userSource, _effectiveRoots);
        foreach (var j in _runJobs) j.PropertyChanged -= OnRunJobChanged;
        _runJobs.Clear();
        _jobRows.Clear();
        _timelineJobs.Clear();
        foreach (var j in _processJobs) j.PropertyChanged -= OnProcessJobChanged;
        _processJobs.Clear();
        _runPlanned = Planned.Where(p => !p.IsExcluded).OrderBy(p => p.Sequence).Select(p => p.Planned).ToList();
        _runProcessTree = processTree;
        _stopRequested = false;
        _runFinished = true;   // no end-of-run handling while the jobs are being queued (_runCaseDir is set below)
        _runCaseDir = null;
        _runComputerDetails = computerDetails != null;
        _runTimelines = timelines;
        _runMacb = macb;

        // Computer details first, before every other job of the run (no other job reads its output, so the rest is queued right away).
        if (computerDetails != null) EnqueueComputerDetails(computerDetails, caseDir);
        var zeekRun = new List<(PlannedJob Planned, Job Job)>();
        foreach (var (item, spec) in toQueue)
        {
            var job = S.Queue.Enqueue(spec with { Args = S.ExpandAppVariables(spec.Args, toolId: spec.ToolId).ToList() }, caseDir);
            TrackRunJob(item, job);
            if (item.Planned.Artifact.RuleId == NetworkTriage.RuleId) zeekRun.Add((item.Planned, job));
        }
        skips.RitaNotes.AddRange(EnqueueRita(selected.Where(p => NetworkTriage.IsRita(p.Planned)).ToList(), zeekRun, caseDir));
        // The super timelines are the last steps: they start when every job above has finished (OnRunJobChanged → CheckRunFinished).
        // Only skipped jobs are reported at the start (the rows show the progress; the completion is reported once, at the end).
        foreach (var t in macb.Concat(timelines)) t.IsWaiting = true;
        if (processTree != null) processTree.IsWaiting = true;
        if (skips.Any) ReportRun(TriageRunCheck.Describe(_runJobs.Count, skips, caseDir), InfoSeverity.Warning, offerSetup: skips.MissingToolJobs > 0);
        else ClearRunStatus(TriageRunCheck.Describe(_runJobs.Count, skips, caseDir));
        _runSkipped = skips.Any;
        S.LastCaseDir = caseDir;
        _runCaseDir = caseDir;
        _runFinished = false;
        RefreshRunGate();
        CheckRunFinished();
    }

    /// <summary>Queues the computer details job(s) of the run: one per Windows system of the source, read from the offline registry.</summary>
    private void EnqueueComputerDetails(PlannedJobItem row, string caseDir)
    {
        try
        {
            var jobs = ComputerDetailsJob.Enqueue(S, IngestSteps.Sources(row.Planned), caseDir, Systems.Count > 0 ? ActiveSystems() : null);
            foreach (var job in jobs) TrackRunJob(row, job);
            if (jobs.Count == 0) row.NotQueuedReason = "Not queued: no Windows system with registry hives in the source.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not queue the computer details", ex);
            row.NotQueuedReason = "Not queued: " + ex.Message;
        }
    }

    /// <summary>A job of the current run: shown on its planned row (Progress, Result) and counted for the end of the run.</summary>
    private void TrackRunJob(PlannedJobItem row, Job job)
    {
        row.AttachJob(job);
        job.PropertyChanged += OnRunJobChanged;
        _runJobs.Add(job);
        _jobRows[job] = row;
    }

    /// <summary>Result link of a planned row: selects its job in place (the details pane shows its log).</summary>
    [RelayCommand]
    private void OpenRowJob(IJobListRow? row)
    {
        if (row?.LatestJob is { } job) ShowJob(job);
    }

    private void OnRunJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Job.State))
        {
            CheckRunFinished();
            RefreshRunGate();
            DeleteExtractedCopyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Once every job of the run has finished: reports the outcome, then the post-run steps (memory-only details, super timelines).</summary>
    private void CheckRunFinished()
    {
        if (_runFinished || _runCaseDir == null || _runJobs.Any(j => !j.IsFinished)) return;
        _runFinished = true;
        int total = _runJobs.Count, failed = _runJobs.Count(j => j.State is not JobState.Succeeded);
        var timelines = _runTimelines;
        var macb = _runMacb;
        var outcome = RunOutcome(total, failed, _runSkipped);
        if (_stopRequested)
        {
            int cancelled = _runJobs.Count(j => j.State is JobState.Cancelled or JobState.Skipped);
            if (cancelled > 0)
            {
                // Stopped: nothing new starts (no super timelines, no cleanup of the extracted copy: a resume still needs it).
                ReportRun($"{total - cancelled} completed, {cancelled} cancelled.", InfoSeverity.Warning);
                CanResumeCancelled = true;
                foreach (var t in macb.Concat(timelines)) t.IsWaiting = false;
                if (_runProcessTree is { IsWaiting: true } pt) EndProcessTreeStopped(pt);
                _ = AfterRunAsync(_runCaseDir, _runJobs.ToList(), _runComputerDetails, Array.Empty<PlannedJobItem>(), stopped: true);
                RefreshRunGate();
                return;
            }
            _stopRequested = false;   // everything had finished before the Stop took effect
        }
        // One completion message: with MACB or super timelines it is reported when the last one is done (BuildMacbTimelinesAsync /
        // BuildSupertimelinesAsync); the rows show the timelines' progress meanwhile.
        if (timelines.Count > 0 || macb.Count > 0) _runCounts = (total, failed);
        else if (outcome.Text.Length > 0) { ReportRun(outcome.Text, outcome.Severity); ShowNextSteps = true; }
        _ = AfterRunAsync(_runCaseDir, _runJobs.ToList(), _runComputerDetails, timelines);
        RefreshRunGate();
    }

    /// <summary>Jobs of the run before the super timelines: "All N jobs succeeded. See Jobs…" (empty when no job was queued) and its severity.</summary>
    private (int Total, int Failed) _runCounts;
    /// <summary>Some selected jobs of the current run were not queued (reported when it was started).</summary>
    private bool _runSkipped;

    internal static (string Text, InfoSeverity Severity) RunOutcome(int total, int failed, bool skipped)
    {
        if (total == 0) return ("", skipped ? InfoSeverity.Warning : InfoSeverity.Success);
        var text = failed == 0 ? $"All {total} jobs succeeded. Select a row for its log."
                               : $"{failed} of {total} jobs did not succeed. Select a row for its log.";
        return (text, failed == 0 && !skipped ? InfoSeverity.Info : InfoSeverity.Warning);
    }

    /// <summary>
    /// Steps after the run's last job. A job's State turns final before its run-manifest record is written, so this first waits for every
    /// run job's <see cref="Job.Recorded"/>: the memory-only computer details read the windows.info details (image format, SHA-256) from
    /// the manifest on disk, and the timeline build reads the manifest too. The super timelines (one per host, built one after the other) are
    /// the last step of the run.
    /// </summary>
    private async Task AfterRunAsync(string caseDir, IReadOnlyList<Job> jobs, bool computerDetails, IReadOnlyList<PlannedJobItem> timelines, bool stopped = false)
    {
        // The super timeline rows wait through this too: keep Run selected blocked until the last build has finished.
        var macb = stopped ? new List<PlannedJobItem>() : _runMacb;
        if (timelines.Count > 0 || macb.Count > 0) IsBuildingTimeline = true;
        try { await Task.WhenAll(jobs.Select(j => j.Recorded)).WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { AppLog.Warn("Run manifest records were not all written within 60 s; continuing with the post-run steps."); }
        try
        {
            if (computerDetails) WriteMemoryOnlyComputerDetails(caseDir, jobs);
            // Started before the extracted-copy cleanup is scheduled: the cleanup waits for the build (it reads the extracted copy).
            // The MACB timelines go first and hand over to the super timelines when they are done.
            if (macb.Count > 0) _ = BuildMacbTimelinesAsync(macb, timelines, caseDir);
            else if (timelines.Count > 0) _ = BuildSupertimelinesAsync(timelines, caseDir);
            if (!stopped) QueueProcessTreeData(caseDir);
            if (!stopped) ScheduleExtractedCleanup();
            if (!stopped) _ = QueueSpectreAfterIngestAsync(caseDir);
        }
        catch (Exception ex)
        {
            if (timelines.Count > 0 || macb.Count > 0) { foreach (var t in macb.Concat(timelines)) t.IsWaiting = false; IsBuildingTimeline = false; }
            if (_runProcessTree is { IsWaiting: true } pt) EndProcessTreeWait(pt, "Not queued: " + ex.Message);
            ShowError("Post-run steps failed", ex);
        }
    }

    /// <summary>
    /// Options → AI "Run Spectre after Ingest": once the run's super timelines and process tree data are done, queues the Spectre analysis
    /// job (it shows in the Jobs list). Skipped after Stop, when the option is off, or when the engine is not installed.
    /// </summary>
    private async Task QueueSpectreAfterIngestAsync(string caseDir)
    {
        try
        {
            if (S.Settings.RunSpectreAfterIngest != true) return;
            while ((IsBuildingTimeline || ProcessTreeActive) && !_stopRequested) await Task.Delay(1000);
            if (_stopRequested) return;
            var (job, reason) = await SpectreAnalysisQueue.EnqueueAsync(S.Queue, caseDir, CancellationToken.None);
            if (job == null) AppLog.Info("Spectre analysis after Ingest not queued: " + reason);
        }
        catch (Exception ex) { AppLog.Warn("Could not queue the Spectre analysis after Ingest: " + ex.Message); }
    }
}
