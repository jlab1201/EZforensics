using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Windows;
using EzForensics.Core.Catalog;
using EzForensics.Core.Execution;
using EzForensics.Core.Plaso;
using EzForensics.Core.Settings;
using EzForensics.Core.Tools;
using EzForensics.Core.Triage;

namespace EzForensics.App.Services;

/// <summary>Composition root: the single instances shared by every page (composed manually in App.xaml.cs).</summary>
public sealed class AppServices
{

    public AppServices(JsonSettingsStore store, AppSettings settings)
    {
        Store = store;
        Settings = settings;
        Locator = new ToolLocator();
        Catalog = ToolCatalog.LoadEmbedded();
        Output = new JobOutputHub();
        LocalAi = new LocalAiService(CreateHttpClient, settings, SaveSettings);
        // Must be constructed on the UI thread: Jobs and job events marshal to this SynchronizationContext.
        Queue = new JobQueue(Locator, settings.MaxParallel, TimeSpan.FromMinutes(Math.Max(1, settings.JobTimeoutMinutes)))
        {
            PreferredFlavor = settings.PreferredBuildFlavor,
            StripTimestampPrefix = settings.StripTimestampPrefix,
            LogLevel = settings.ToolLogLevel,
            EvtxCustomMapsDir = EzForensics.Core.EventLogs.EvtxCustomMaps.DefaultFolder,
        };
        Queue.Jobs.CollectionChanged += OnJobsChanged;
        // Install / update processes run on a private queue: never listed in the Jobs list on Ingest (only case work is).
        Maintenance = new MaintenanceRunner(new JobQueue(Locator, 2, TimeSpan.FromMinutes(Math.Max(1, settings.JobTimeoutMinutes)))
        {
            PreferredFlavor = settings.PreferredBuildFlavor,
            StripTimestampPrefix = false,
            LogLevel = settings.ToolLogLevel,
            EvtxCustomMapsDir = EzForensics.Core.EventLogs.EvtxCustomMaps.DefaultFolder,
        }, Path.Combine(AppPaths.LogsDir, "maintenance"));
        IngestDefaults.UseDefaultSelection(IngestSelections.Find(settings.IngestSelections, IngestSelections.DefaultName).RuleIds);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.IngestSelections))
                IngestDefaults.UseDefaultSelection(IngestSelections.Find(settings.IngestSelections, IngestSelections.DefaultName).RuleIds);
            if (e.PropertyName == nameof(AppSettings.PreferredFlavor)) Queue.PreferredFlavor = Maintenance.Queue.PreferredFlavor = settings.PreferredBuildFlavor;
            if (e.PropertyName == nameof(AppSettings.StripTimestampPrefix)) Queue.StripTimestampPrefix = settings.StripTimestampPrefix;
            if (e.PropertyName == nameof(AppSettings.ToolLogLevel)) Queue.LogLevel = Maintenance.Queue.LogLevel = settings.ToolLogLevel;
        };
    }

    public static AppServices Current { get; set; } = null!;

    /// <summary>Analyze > Actions > Hide hints (this session): in memory only, a new app session shows hints again.</summary>
    public bool HideHintsThisSession { get; set; }

    public JsonSettingsStore Store { get; }
    public AppSettings Settings { get; }
    public ToolLocator Locator { get; }
    public JobQueue Queue { get; }
    /// <summary>
    /// Private runner for install / update processes (setup wizard, background resume; later the Tools page): its own JobQueue that the
    /// Ingest never lists. Output goes to the caller's log callback and to job logs under the given log folder.
    /// </summary>
    public MaintenanceRunner Maintenance { get; }
    public JobOutputHub Output { get; }
    /// <summary>"Use local AI" (Ollama + model): one run at a time for Options, Tools and the Spectre chat card.</summary>
    public LocalAiService LocalAi { get; }

    /// <summary>True while a job whose tool id starts with one of <paramref name="toolIdPrefixes"/> is queued or running, on the analysis queue or on
    /// the private maintenance queue (so an install started by setup is seen too).</summary>
    public bool HasActiveJob(params string[] toolIdPrefixes) =>
        Queue.Jobs.Concat(Maintenance.Queue.Jobs).Any(j => !j.IsFinished && toolIdPrefixes.Any(p => j.Spec.ToolId.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    public IReadOnlyList<ToolDefinition> Catalog { get; }

    /// <summary>Case folder of the most recent triage run (Results defaults to it).</summary>
    public string? LastCaseDir { get; set; }

    /// <summary>Raised on the UI thread after every rescan of the tools root.</summary>
    public event Action? ToolsRescanned;

    /// <summary>Raised when a page asks the shell to switch to another page (by VM type).</summary>
    public event Action<Type>? NavigationRequested;

    /// <summary>Raised when new results are ready to analyze (a case's jobs settled); the shell shows a badge on Analyze.</summary>
    public event Action? ResultsReady;
    public void RaiseResultsReady() => OnUi(() => ResultsReady?.Invoke());

    public bool? HasNet9 => RuntimeProbe.Cached;

    public Task<bool> ProbeNet9Async() => RuntimeProbe.HasNet9Async();

    /// <summary>Raised to show a specific job (selected, log loaded) on Ingest.</summary>
    public event Action<Job>? JobRequested;

    /// <summary>Raised to open Ingest (its jobs list).</summary>
    public event Action? JobsRequested;

    public void ShowJobs() => JobsRequested?.Invoke();

    /// <summary>Raised after Navigate to Learn when the Inbox tab should be on screen.</summary>
    public event Action? LearnInboxRequested;
    public void ShowLearnInbox()
    {
        Navigate<ViewModels.Learn.LearnViewModel>();
        LearnInboxRequested?.Invoke();
    }

    public void ShowJob(Job job) => JobRequested?.Invoke(job);

    /// <summary>Raised to open Options and scroll to the AI card.</summary>
    public event Action? OptionsAiRequested;
    public void ShowOptionsAi() => OptionsAiRequested?.Invoke();

    public void Navigate<TViewModel>() => NavigationRequested?.Invoke(typeof(TViewModel));
    public void Navigate(Type viewModel) => NavigationRequested?.Invoke(viewModel);

    /// <summary>Raised after <see cref="OpenToolsConfiguration"/> switched to the Tools page: it opens that tool's category and entry.</summary>
    public event Action<ToolsSection>? ToolsSectionRequested;

    /// <summary>Opens the Tools page on one tool (its category page with the tool selected): setup, licence, defaults.</summary>
    public void OpenToolsConfiguration(ToolsSection section)
    {
        Navigate<ViewModels.ToolsViewModel>();
        ToolsSectionRequested?.Invoke(section);
    }

    /// <summary>Raised after an Explore tool was installed or removed on the Tools page.</summary>
    public event Action? ExploreToolsChanged;
    public void RaiseExploreToolsChanged() => ExploreToolsChanged?.Invoke();

    /// <summary>Set after the Ingest memory defaults change (Tools page): the plans pick up the new plugin set.</summary>
    public event Action? MemoryPluginsChanged;
    public void RaiseMemoryPluginsChanged() => OnUi(() => MemoryPluginsChanged?.Invoke());

    /// <summary>Analyze focus mode (F11): the shell hides its navigation and Analyze hides the case tree. Raised on the UI thread when it changes.</summary>
    public event Action<bool>? FocusModeChanged;
    public bool IsFocusMode { get; private set; }

    public void SetFocusMode(bool on)
    {
        if (IsFocusMode == on) return;
        IsFocusMode = on;
        FocusModeChanged?.Invoke(on);
    }

    /// <summary>Raised when a case folder becomes the open case everywhere (Analyze's tree, Review, Spectre), e.g. after Load demo case.</summary>
    public event Action<string>? CaseOpenRequested;

    /// <summary>Makes <paramref name="caseDir"/> the open case: the last case, and Analyze's case folder (so Review and Spectre follow it).</summary>
    public void OpenCase(string caseDir)
    {
        LastCaseDir = caseDir;
        CaseOpenRequested?.Invoke(caseDir);
    }

    /// <summary>
    /// Load demo case: copies the synthetic demo shipped with the app to a new folder under the case root, builds its process tree data and
    /// opens it as the case. Returns the new case folder.
    /// </summary>
    public async Task<string> LoadDemoCaseAsync()
    {
        var r = await EzForensics.Core.Case.DemoCase.CreateAsync(Settings.CaseRoot);
        foreach (var w in r.Warnings) AppLog.Warn("Demo case: " + w);
        OpenCase(r.CaseDir);
        return r.CaseDir;
    }

    /// <summary>Raised to open a CSV (case folder, file) on the Analyze page.</summary>
    public event Action<string, string>? OpenCsvRequested;

    /// <summary>Switches to Analyze and opens <paramref name="csvPath"/> with <paramref name="caseDir"/> as the tree root.</summary>
    public void OpenInResults(string caseDir, string csvPath)
    {
        LastCaseDir = caseDir;
        OpenCsvRequested?.Invoke(caseDir, csvPath);
    }

    /// <summary>Raised to open a CSV on Analyze at one record: (case folder, file, record id from a Spectre finding).</summary>
    public event Action<string, string, string>? OpenRecordRequested;

    /// <summary>Switches to Analyze, opens <paramref name="csvPath"/> and selects the row whose record-id column equals <paramref name="recordId"/>.</summary>
    public void OpenRecordInResults(string caseDir, string csvPath, string recordId)
    {
        if (OpenRecordRequested == null) { OpenInResults(caseDir, csvPath); return; }
        LastCaseDir = caseDir;
        OpenRecordRequested.Invoke(caseDir, csvPath, recordId);
    }

    /// <summary>Raised to show the records behind a finding on Analyze: (case folder, files with their rows, finding title).</summary>
    public event Action<string, IReadOnlyList<Core.Spectre.EvidenceTarget>, string>? ShowEvidenceRequested;

    /// <summary>
    /// Switches to Analyze, opens the first file of <paramref name="targets"/>, flags the rows behind the finding, filters to them and selects the first;
    /// a banner on Analyze says so and offers the other files. Without a listener (tests, no Analyze page) it just opens the first file.
    /// </summary>
    public void ShowEvidenceInResults(string caseDir, IReadOnlyList<Core.Spectre.EvidenceTarget> targets, string title)
    {
        if (targets.Count == 0) return;
        if (ShowEvidenceRequested == null) { OpenInResults(caseDir, targets[0].Csv); return; }
        LastCaseDir = caseDir;
        ShowEvidenceRequested.Invoke(caseDir, targets, title);
    }

    public async Task RescanAsync()
    {
        var root = Settings.ToolsRoot;
        await Task.Run(() => Locator.Rescan(root));
        ToolsRescanned?.Invoke();
    }

    /// <summary>True when at least one exe from the catalog exists under the tools root.</summary>
    public bool HasAnyEzTools
    {
        get
        {
            var names = new HashSet<string>(Catalog.Select(c => c.Exe), StringComparer.OrdinalIgnoreCase);
            return Locator.All.Any(t => names.Contains(t.ExeName));
        }
    }

    public bool IsInstalled(string exe)
    {
        var name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";
        return Locator.All.Any(t => t.ExeName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolve without blocking the UI on the runtime probe (falls back to a plain match until it finishes).</summary>
    public InstalledTool? ResolveNonBlocking(string exe)
    {
        if (HasNet9 != null) return Locator.Resolve(exe, Settings.PreferredBuildFlavor);
        var name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";
        var want = Settings.PreferredBuildFlavor ?? BuildFlavor.Net9;
        var m = Locator.All.Where(t => t.ExeName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return m.FirstOrDefault(t => t.Flavor == want) ?? m.FirstOrDefault();
    }

    public void SaveSettings() => Store.Save(Settings);

    // ---- EvtxECmd event selection (Core/EventLogs): shared by the Tools page card and the Ingest run
    public EzForensics.Core.EventLogs.EvtxSelectionStore EvtxStore { get; } = new();
    private EzForensics.Core.EventLogs.EvtxSelection? _evtx;
    public EzForensics.Core.EventLogs.EvtxSelection Evtx => _evtx ??= EvtxStore.Load();
    /// <summary>Raised after the selection or the "use recommended" switch changed.</summary>
    public event Action? EvtxChanged;
    public void SaveEvtx() { try { EvtxStore.Save(Evtx); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Could not save the event selection: " + ex.Message); } EvtxChanged?.Invoke(); }
    /// <summary>
    /// Expands the app-managed command variables: <c>{eventIds}</c> (event selection), <c>{dateFormat}</c> (Settings; blank drops <c>--dt</c>)
    /// and <c>{batchFile}</c> (RECmd batch setting, else the built-in file whose <c>{toolDir}</c> the job queue expands). <paramref name="quiet"/> skips log warnings.
    /// </summary>
    public IReadOnlyList<string> ExpandAppVariables(IReadOnlyList<string> args, bool quiet = false, string? toolId = null)
    {
        var selected = Settings.EvtxFilterMode == EvtxFilterMode.Selected;
        IReadOnlyList<string> r;
        if (args.Any(a => a.Contains("{eventIds}", StringComparison.Ordinal)))
            r = EzForensics.Core.EventLogs.EvtxArgs.ExpandEventIds(args, selected, Evtx);
        else if (selected && string.Equals(toolId, "EvtxECmd", StringComparison.OrdinalIgnoreCase))
            r = EzForensics.Core.EventLogs.EvtxArgs.Apply(args, true, Evtx);
        else r = args;
        r = CommandVariables.Expand(r, "dateFormat", Settings.DateFormat?.Trim(), "--dt");
        if (r.Any(a => a.Contains("{batchFile}", StringComparison.Ordinal)))
            r = CommandVariables.Expand(r, "batchFile", CommandVariables.ResolveBatchFile(Settings.RecmdBatchFile, quiet ? null : AppLog.Warn), "--bn");
        return r;
    }

    /// <summary>The --dt warning for a {dateFormat} value Analyze cannot parse, or null.</summary>
    public string? DateFormatWarning => string.IsNullOrWhiteSpace(Settings.DateFormat) || EzForensics.Core.Results.CsvValueParser.IsAnalyzeDateFormat(Settings.DateFormat.Trim())
        ? null : EzForensics.Core.Results.CsvValueParser.DateFormatWarning;

    // ---- Custom Ingest commands (Core/Triage): per-rule command overrides, applied to the embedded rules when Ingest plans a run
    private EzForensics.Core.Triage.CustomCommandStore? _ingestCommands;
    public EzForensics.Core.Triage.CustomCommandStore IngestCommands
    {
        get
        {
            if (_ingestCommands != null) return _ingestCommands;
            var st = new EzForensics.Core.Triage.CustomCommandStore();
            try { st.Load(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { AppLog.Warn("Could not read the custom Ingest commands: " + ex.Message); }
            return _ingestCommands = st;
        }
    }
    /// <summary>The embedded rules with the user's valid custom commands swapped in; invalid ones are listed in <paramref name="problems"/>.</summary>
    public IReadOnlyList<EzForensics.Core.Triage.TriageRule> IngestRules(ICollection<string>? problems = null)
        => EzForensics.Core.Triage.CustomCommand.Apply(EzForensics.Core.Triage.TriageRules.LoadEmbedded(), IngestCommands, problems);
    public event Action? IngestCommandsChanged;
    public void SaveIngestCommands() { try { IngestCommands.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Could not save the custom Ingest commands: " + ex.Message); } IngestCommandsChanged?.Invoke(); }

    public HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        switch (Settings.ProxyMode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Manual when !string.IsNullOrWhiteSpace(Settings.ProxyUrl):
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(Settings.ProxyUrl) { UseDefaultCredentials = true };
                break;
            default:
                handler.UseProxy = true;
                handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                break;
        }
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EzForensics/" + (typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0.1"));
        return http;
    }

    public static string Stamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss");

    public static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "case" : cleaned;
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;
        foreach (Job j in e.NewItems) Output.Attach(j);
    }

    public static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    // ------------------------------------------------------------------ plaso (super timeline engine)

    private Task<PlasoDiscovery>? _plasoProbe;

    /// <summary>Last plaso discovery result (null until the first probe finished).</summary>
    public PlasoDiscovery? Plaso { get; private set; }

    /// <summary>True while a discovery is running.</summary>
    public bool IsProbingPlaso => _plasoProbe is { IsCompleted: false };

    /// <summary>Raised on the UI thread when a plaso discovery starts or finishes.</summary>
    public event Action? PlasoChanged;

    /// <summary>Starts the first discovery if none ran yet (non-blocking).</summary>
    public void EnsurePlasoProbed()
    {
        if (_plasoProbe == null) _ = RefreshPlasoAsync();
    }

    // ------------------------------------------------------------------ Zeek (packet capture -> logs)

    private Task<EzForensics.Core.Zeek.ZeekDiscovery>? _zeekProbe;

    /// <summary>Last Zeek discovery result (null until the first probe finished).</summary>
    public EzForensics.Core.Zeek.ZeekDiscovery? Zeek { get; private set; }

    public bool IsProbingZeek => _zeekProbe is { IsCompleted: false };

    /// <summary>Raised on the UI thread when a Zeek discovery starts or finishes.</summary>
    public event Action? ZeekChanged;

    public void EnsureZeekProbed()
    {
        if (_zeekProbe == null) _ = RefreshZeekAsync();
    }

    /// <summary>Re-runs Zeek discovery (configured zeek.exe, PATH, Docker, WSL) off the UI thread.</summary>
    public Task<EzForensics.Core.Zeek.ZeekDiscovery> RefreshZeekAsync()
    {
        if (_zeekProbe is { IsCompleted: false } running) return running;
        var path = Settings.ZeekPath;
        var t = _zeekProbe = Task.Run(() => { var d = new EzForensics.Core.Zeek.ZeekLocator().Discover(path); Zeek = d; return d; });
        ZeekChanged?.Invoke();
        _ = t.ContinueWith(r =>
        {
            if (!r.IsCompletedSuccessfully) AppLog.Error("Zeek discovery failed", r.Exception);
            OnUi(() => ZeekChanged?.Invoke());
        }, TaskScheduler.Default);
        return t;
    }

    // ------------------------------------------------------------------ RITA (beacons / long connections / DNS tunneling over Zeek logs)

    private Task<EzForensics.Core.Rita.RitaDiscovery>? _ritaProbe;

    /// <summary>Last RITA discovery (null until the first probe finished).</summary>
    public EzForensics.Core.Rita.RitaDiscovery? Rita { get; private set; }

    public bool IsProbingRita => _ritaProbe is { IsCompleted: false };

    /// <summary>Raised on the UI thread when a RITA discovery starts or finishes, or its settings (enabled, internal subnets, threat intel) change.</summary>
    public event Action? RitaChanged;

    public void EnsureRitaProbed()
    {
        if (_ritaProbe == null) _ = RefreshRitaAsync();
    }

    public void RaiseRitaChanged() => OnUi(() => RitaChanged?.Invoke());

    /// <summary>Re-runs RITA discovery (one short command in the EZ Forensics distro; starts it when stopped) off the UI thread.</summary>
    public Task<EzForensics.Core.Rita.RitaDiscovery> RefreshRitaAsync()
    {
        if (_ritaProbe is { IsCompleted: false } running) return running;
        var t = _ritaProbe = Task.Run(() => { var d = EzForensics.Core.Rita.RitaLocator.Discover(); Rita = d; return d; });
        RitaChanged?.Invoke();
        _ = t.ContinueWith(r =>
        {
            if (!r.IsCompletedSuccessfully) AppLog.Error("RITA discovery failed", r.Exception);
            OnUi(() => RitaChanged?.Invoke());
        }, TaskScheduler.Default);
        return t;
    }

    // ------------------------------------------------------------------ Volatility 3 (memory images)

    private Task<EzForensics.Core.Memory.VolatilityInstall?>? _volatilityProbe;

    /// <summary>Last Volatility 3 discovery (null while not found or before the first probe finished; see <see cref="VolatilityProbed"/>).</summary>
    public EzForensics.Core.Memory.VolatilityInstall? Volatility { get; private set; }

    /// <summary>True once a discovery has finished (Volatility null then means "not installed").</summary>
    public bool VolatilityProbed { get; private set; }

    public bool IsProbingVolatility => _volatilityProbe is { IsCompleted: false };

    /// <summary>Raised on the UI thread when a Volatility discovery starts or finishes, or its settings (offline mode) change.</summary>
    public event Action? VolatilityChanged;

    public void EnsureVolatilityProbed()
    {
        if (_volatilityProbe == null) _ = RefreshVolatilityAsync();
    }

    public void RaiseVolatilityChanged() => OnUi(() => VolatilityChanged?.Invoke());

    /// <summary>Re-runs Volatility discovery (configured path, EZ Forensics' venv, PATH; each answers the version probe) off the UI thread.</summary>
    public Task<EzForensics.Core.Memory.VolatilityInstall?> RefreshVolatilityAsync()
    {
        if (_volatilityProbe is { IsCompleted: false } running) return running;
        var path = Settings.VolatilityPath;
        var t = _volatilityProbe = Task.Run(() =>
        {
            var d = new EzForensics.Core.Memory.VolatilityLocator().Discover(path);
            Volatility = d;
            VolatilityProbed = true;
            return d;
        });
        VolatilityChanged?.Invoke();
        _ = t.ContinueWith(r =>
        {
            if (!r.IsCompletedSuccessfully) { AppLog.Error("Volatility discovery failed", r.Exception); VolatilityProbed = true; }
            OnUi(() => VolatilityChanged?.Invoke());
        }, TaskScheduler.Default);
        return t;
    }

    /// <summary>Re-runs discovery (configured path, managed venv, PATH, Docker) off the UI thread.</summary>
    public Task<PlasoDiscovery> RefreshPlasoAsync()
    {
        if (_plasoProbe is { IsCompleted: false } running) return running;
        var path = Settings.PlasoPath;
        var t = _plasoProbe = Task.Run(() => { var d = new PlasoLocator().Discover(path); Plaso = d; return d; });
        PlasoChanged?.Invoke();
        _ = t.ContinueWith(r =>
        {
            if (!r.IsCompletedSuccessfully) AppLog.Error("plaso discovery failed", r.Exception);
            OnUi(() => PlasoChanged?.Invoke());
        }, TaskScheduler.Default);
        return t;
    }
}
