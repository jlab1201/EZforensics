using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EzForensics.Core.Tools;

namespace EzForensics.Core.Settings;

public enum FlavorPreference { Auto, Net9, Net4 }
public enum ProxyMode { System, None, Manual }
public enum AppTheme { Dark, Light }
public enum EvtxFilterMode { All, Selected }

public sealed class AppSettings : INotifyPropertyChanged
{
    private string _toolsRoot = AppPaths.DefaultToolsRoot;
    private FlavorPreference _preferredFlavor = FlavorPreference.Auto;
    private ProxyMode _proxyMode = ProxyMode.System;
    private string? _proxyUrl;
    private int _maxParallel = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
    private int _jobTimeoutMinutes = 120;
    private string _caseRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "EZ Forensics Cases");
    private string _dateFormat = "";
    private string _recmdBatchFile = "";
    private AppTheme _theme = AppTheme.Dark;
    private int _netVersionToDownload = 9;
    private bool _navPinned;
    private double _cellViewerWidth = 760;
    private double _cellViewerHeight = 520;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ToolsRoot { get => _toolsRoot; set => Set(ref _toolsRoot, value); }
    public FlavorPreference PreferredFlavor { get => _preferredFlavor; set => Set(ref _preferredFlavor, value); }
    public ProxyMode ProxyMode { get => _proxyMode; set => Set(ref _proxyMode, value); }
    public string? ProxyUrl { get => _proxyUrl; set => Set(ref _proxyUrl, value); }
    public int MaxParallel { get => _maxParallel; set => Set(ref _maxParallel, Math.Max(1, value)); }
    public int JobTimeoutMinutes { get => _jobTimeoutMinutes; set => Set(ref _jobTimeoutMinutes, value); }
    public string CaseRoot { get => _caseRoot; set => Set(ref _caseRoot, value); }
    /// <summary>Empty means the tool default.</summary>
    public string DateFormat { get => _dateFormat; set => Set(ref _dateFormat, value ?? ""); }
    /// <summary>RECmd batch file for {batchFile}; empty means the built-in BatchExamplesDFIRBatch.reb.</summary>
    public string RecmdBatchFile { get => _recmdBatchFile; set => Set(ref _recmdBatchFile, value ?? ""); }
    public AppTheme Theme { get => _theme; set => Set(ref _theme, value); }
    public int NetVersionToDownload { get => _netVersionToDownload; set => Set(ref _netVersionToDownload, value); }
    /// <summary>Left navigation pinned open (false, the default and first-run state = collapsed icon rail that expands as an overlay).</summary>
    public bool NavPinned { get => _navPinned; set => Set(ref _navPinned, value); }
    private string? _lastPage;
    /// <summary>Page key of the page the app was on when it closed (null = Ingest). Not part of the Options edit copy.</summary>
    public string? LastPage { get => _lastPage; set => Set(ref _lastPage, value); }
    /// <summary>Last size of the Results cell-value window.</summary>
    public double CellViewerWidth { get => _cellViewerWidth; set => Set(ref _cellViewerWidth, value); }
    public double CellViewerHeight { get => _cellViewerHeight; set => Set(ref _cellViewerHeight, value); }

    private string? _plasoPath;
    /// <summary>plaso override: a folder with log2timeline/psort (or a venv), or a python.exe that has plaso installed.</summary>
    public string? PlasoPath { get => _plasoPath; set => Set(ref _plasoPath, value); }

    private double _uiZoom = Settings.UiZoom.Default;
    private string _uiFontFamily = UiFonts.DefaultUiFamily;
    private double _uiFontSize = UiFonts.DefaultUiSize;
    private string _monoFontFamily = UiFonts.DefaultMonoFamily;
    private double _monoFontSize = UiFonts.DefaultMonoSize;
    /// <summary>App-wide UI zoom (1.0 = 100 %), clamped to 0.5–2.5.</summary>
    public double UiZoom { get => _uiZoom; set => Set(ref _uiZoom, Settings.UiZoom.Clamp(value)); }
    /// <summary>UI font family (a WPF fallback list such as "Segoe UI Variable Text, Segoe UI" is allowed).</summary>
    public string UiFontFamily { get => _uiFontFamily; set => Set(ref _uiFontFamily, UiFonts.Family(value, UiFonts.DefaultUiFamily)); }
    /// <summary>Base UI font size in device-independent pixels.</summary>
    public double UiFontSize { get => _uiFontSize; set => Set(ref _uiFontSize, UiFonts.ClampSize(value, UiFonts.DefaultUiSize)); }
    /// <summary>Monospace font for logs, command previews, the Results grid and the cell viewer.</summary>
    public string MonoFontFamily { get => _monoFontFamily; set => Set(ref _monoFontFamily, UiFonts.Family(value, UiFonts.DefaultMonoFamily)); }
    public double MonoFontSize { get => _monoFontSize; set => Set(ref _monoFontSize, UiFonts.ClampSize(value, UiFonts.DefaultMonoSize)); }

    /// <summary>Flavor to hand to IToolLocator.Resolve (null = automatic).</summary>
    [JsonIgnore]
    public BuildFlavor? PreferredBuildFlavor => PreferredFlavor switch
    {
        FlavorPreference.Net9 => BuildFlavor.Net9,
        FlavorPreference.Net4 => BuildFlavor.Net4,
        _ => null,
    };

    private bool _keepExtractedEvidence = true;
    /// <summary>Keep the extracted copy of archived evidence after the run (false = offer to delete it).</summary>
    public bool KeepExtractedEvidence { get => _keepExtractedEvidence; set => Set(ref _keepExtractedEvidence, value); }

    private bool _stripTimestampPrefix = true;
    /// <summary>Remove the EZ run-time prefix (20261007123456_) from tool output names (originals kept in the manifest).</summary>
    public bool StripTimestampPrefix { get => _stripTimestampPrefix; set => Set(ref _stripTimestampPrefix, value); }

    private Execution.ToolLogLevel _toolLogLevel = Execution.ToolLogLevel.Normal;
    /// <summary>Tool logging: Debug adds the full command line, working directory, elevation, tool version/signature, exit code, duration and output tails to every job log.</summary>
    public Execution.ToolLogLevel ToolLogLevel { get => _toolLogLevel; set => Set(ref _toolLogLevel, value); }

    private bool _showAnalyzeHints = true;
    /// <summary>Analyze hints (reliability banner and column markers). False hides them for good (Options > Display).</summary>
    public bool ShowAnalyzeHints { get => _showAnalyzeHints; set => Set(ref _showAnalyzeHints, value); }

    /// <summary>Hints the examiner dismissed (case folder + hint text); a hint with other text shows again.</summary>
    public List<string> DismissedHints { get; set; } = new();

    private ResultsTreeColumns _resultsTreeColumns = new();
    /// <summary>Results case tree: visible optional columns and column widths.</summary>
    public ResultsTreeColumns ResultsTreeColumns { get => _resultsTreeColumns; set => Set(ref _resultsTreeColumns, value ?? new ResultsTreeColumns()); }

    private IngestJobColumns _ingestColumns = new();
    /// <summary>Ingest page: visible optional columns of the Jobs list.</summary>
    public IngestJobColumns IngestColumns { get => _ingestColumns; set => Set(ref _ingestColumns, value ?? new IngestJobColumns()); }

    private string _plasoParsers = "";
    private Plaso.PlasoOutputFormat _plasoFormat = Plaso.PlasoOutputFormat.Dynamic;
    private string _plasoWorkers = "";
    private bool _plasoTimezoneUtc = true;
    private bool _plasoVss;
    private bool _plasoUnattended = true;
    private DateTime? _plasoFromDate;
    private DateTime? _plasoToDate;
    /// <summary>plaso timeline options (Tools > Timeline): <c>--parsers</c> preset or filter expression; empty = plaso auto-selects.</summary>
    public string PlasoParsers { get => _plasoParsers; set => Set(ref _plasoParsers, value ?? ""); }
    /// <summary>psort output format.</summary>
    public Plaso.PlasoOutputFormat PlasoFormat { get => _plasoFormat; set => Set(ref _plasoFormat, value); }
    /// <summary><c>--workers</c>; empty (or not a positive number) = CPUs minus one.</summary>
    public string PlasoWorkers { get => _plasoWorkers; set => Set(ref _plasoWorkers, value ?? ""); }
    /// <summary><c>--timezone UTC</c> for timestamps stored without a time zone.</summary>
    public bool PlasoTimezoneUtc { get => _plasoTimezoneUtc; set => Set(ref _plasoTimezoneUtc, value); }
    /// <summary>Process Volume Shadow Copies (<c>--vss_stores all</c>).</summary>
    public bool PlasoVss { get => _plasoVss; set => Set(ref _plasoVss, value); }
    /// <summary><c>--unattended</c>: never prompt, fail instead.</summary>
    public bool PlasoUnattended { get => _plasoUnattended; set => Set(ref _plasoUnattended, value); }
    /// <summary>psort event filter range (UTC, inclusive, whole days); null = unbounded.</summary>
    public DateTime? PlasoFromDate { get => _plasoFromDate; set => Set(ref _plasoFromDate, value?.Date); }
    public DateTime? PlasoToDate { get => _plasoToDate; set => Set(ref _plasoToDate, value?.Date); }

    private string? _zeekPath;
    /// <summary>Zeek override: a zeek.exe (or its folder). Null = automatic (zeek.exe on PATH, Docker zeek/zeek:lts, WSL).</summary>
    public string? ZeekPath { get => _zeekPath; set => Set(ref _zeekPath, value); }

    private string? _volatilityPath;
    private bool _volatilityOffline;
    /// <summary>Volatility 3 override: a vol.exe (or the folder / venv that holds it). Null = automatic (EZ Forensics' own venv, then PATH).</summary>
    public string? VolatilityPath { get => _volatilityPath; set => Set(ref _volatilityPath, value); }
    /// <summary>Volatility runs with --offline: symbols come only from the local symbol directory (never fetched from the internet).</summary>
    public bool VolatilityOffline { get => _volatilityOffline; set => Set(ref _volatilityOffline, value); }

    private List<string>? _volatilityPlugins;
    /// <summary>Plugin ids (memory plugins catalog, Windows and Linux) Ingest runs on every memory image (Tools). Null = the
    /// catalog defaults; <c>windows.info</c> always runs. See <see cref="Memory.MemoryPlugins.SelectedFor"/>.</summary>
    public List<string>? VolatilityPlugins { get => _volatilityPlugins; set => Set(ref _volatilityPlugins, value); }

    private List<EzForensics.Core.Results.FlagDefinition> _flags = EzForensics.Core.Results.FlagCatalog.Defaults();
    private string _flagLastId = EzForensics.Core.Results.FlagCatalog.DefaultId;
    private bool _flagRowTint = true;
    private bool _escapeFormulasOnCopy = true;
    /// <summary>Results flags: the flag list in display order (menus, legend, filters). Never empty; ids are stable, labels and colours editable.</summary>
    public List<EzForensics.Core.Results.FlagDefinition> Flags { get => _flags; set => Set(ref _flags, EzForensics.Core.Results.FlagCatalog.Normalize(value)); }
    /// <summary>Results flags: id of the flag used by a click on the flag cell, Space and "Flag selected" (the last one picked).</summary>
    public string FlagLastId { get => _flagLastId; set => Set(ref _flagLastId, string.IsNullOrWhiteSpace(value) ? EzForensics.Core.Results.FlagCatalog.DefaultId : value.Trim()); }
    /// <summary>Settings written before flags had ids stored the last colour name here; read once and mapped to a flag id.</summary>
    [JsonPropertyName("flagLastColor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyFlagLastColor { get => null; set { if (EzForensics.Core.Results.FlagCatalog.MigrateLegacy(value) is { } id) FlagLastId = id; } }
    /// <summary>Results flags: tint flagged rows lightly in their flag colour.</summary>
    public bool FlagRowTint { get => _flagRowTint; set => Set(ref _flagRowTint, value); }
    /// <summary>
    /// Analyze copy (clipboard rows): cells starting with <c>= + - @</c> get a leading <c>'</c> so pasting into Excel never runs them as
    /// formulas (CSV exports are never changed). Default on.
    /// </summary>
    public bool EscapeFormulasOnCopy { get => _escapeFormulasOnCopy; set => Set(ref _escapeFormulasOnCopy, value); }
    /// <summary>Results: the Computer details panel is expanded.</summary>

    private Dictionary<string, ColumnLayout> _columnLayouts = new();
    /// <summary>Analyze grid: hidden columns and widths per CSV header signature (see <see cref="ColumnLayoutStore"/>; at most 200 entries).</summary>
    public Dictionary<string, ColumnLayout> ColumnLayouts { get => _columnLayouts; set => Set(ref _columnLayouts, value ?? new Dictionary<string, ColumnLayout>()); }

    private bool _thirdPartyLicensesAccepted;
    private DateTime? _thirdPartyLicensesAcceptedUtc;
    private int? _ezDistroWslVersion;
    /// <summary>
    /// The user ticked "I accept the license terms of the third-party tools this setup installs" in the setup wizard (Setup.ThirdPartyLicenses).
    /// </summary>
    public bool ThirdPartyLicensesAccepted { get => _thirdPartyLicensesAccepted; set => Set(ref _thirdPartyLicensesAccepted, value); }
    /// <summary>When the third-party licences were accepted (UTC).</summary>
    public DateTime? ThirdPartyLicensesAcceptedUtc { get => _thirdPartyLicensesAcceptedUtc; set => Set(ref _thirdPartyLicensesAcceptedUtc, value); }
    /// <summary>WSL version (1 or 2) of the EZ Forensics distro when setup created it; 1 = WSL 2 was not usable (e.g. a VM without nested virtualization).</summary>
    public int? EzDistroWslVersion { get => _ezDistroWslVersion; set => Set(ref _ezDistroWslVersion, value); }

    private bool _setupWizardSeen;
    private bool _setupWizardDontShow;
    private SetupResumeState? _setupResume;
    private string? _updateFeedUrl;
    /// <summary>The first-run setup wizard was finished once: it never opens by itself again (the menu opens it). A cancelled first setup leaves it false.</summary>
    public bool SetupWizardSeen { get => _setupWizardSeen; set => Set(ref _setupWizardSeen, value); }
    /// <summary>Legacy "Don't show setup at startup" (no longer in the UI; still honoured: the wizard does not open by itself).</summary>
    public bool SetupWizardDontShow { get => _setupWizardDontShow; set => Set(ref _setupWizardDontShow, value); }
    /// <summary>A setup that waits for a Windows restart (enable WSL): the wizard reopens on its Install page at the next start and finishes it.</summary>
    public SetupResumeState? SetupResume { get => _setupResume; set => Set(ref _setupResume, value); }
    /// <summary>The project's GitHub repository: where the app looks for updates unless <see cref="UpdateFeedUrl"/> overrides it.</summary>
    public const string DefaultUpdateFeedUrl = "https://github.com/jlab1201/EZforensics";
    /// <summary>Update feed override for the installed app: a GitHub repository URL (https://github.com/owner/repo), a Velopack feed URL or a local folder. Empty = <see cref="DefaultUpdateFeedUrl"/>.</summary>
    public string? UpdateFeedUrl { get => _updateFeedUrl; set => Set(ref _updateFeedUrl, value); }
    /// <summary>The feed actually used: the override when set, otherwise <see cref="DefaultUpdateFeedUrl"/>.</summary>
    [JsonIgnore]
    public string EffectiveUpdateFeedUrl => string.IsNullOrWhiteSpace(_updateFeedUrl) ? DefaultUpdateFeedUrl : _updateFeedUrl.Trim();

    private Dictionary<string, string> _hotKeys = new();
    /// <summary>User shortcut overrides by action id (see <see cref="HotKeys"/>). Missing = default gesture, empty string = unassigned.</summary>
    public Dictionary<string, string> HotKeys { get => _hotKeys; set => Set(ref _hotKeys, value ?? new Dictionary<string, string>()); }

    private Dictionary<string, string> _toolSourcePaths = new();
    /// <summary>Tools page: the last source folder / file picked per rule id (the Source row of the Command tile).</summary>
    public Dictionary<string, string> ToolSourcePaths { get => _toolSourcePaths; set => Set(ref _toolSourcePaths, value ?? new Dictionary<string, string>()); }

    private Dictionary<string, string> _toolExtraArgs = new();
    /// <summary>
    /// "Additional arguments" of the app-built commands (plaso, Zeek, RITA, Volatility), by command id (<see cref="Commands.ExtraArgsStore"/>):
    /// used by the Tools page and by Ingest.
    /// </summary>
    public Dictionary<string, string> ToolExtraArgs { get => _toolExtraArgs; set => Set(ref _toolExtraArgs, value ?? new Dictionary<string, string>()); }

    private EvtxFilterMode _evtxFilterMode = EvtxFilterMode.All;
    /// <summary>EvtxECmd event filter: All = every event is parsed (default), Selected = only the ticked event IDs (--inc). The legacy EvtxUseRecommended key is ignored.</summary>
    [JsonPropertyName("evtxFilterMode"), JsonConverter(typeof(JsonStringEnumConverter))]
    public EvtxFilterMode EvtxFilterMode { get => _evtxFilterMode; set => Set(ref _evtxFilterMode, value); }

    private bool _ritaEnabled = true;
    private List<string> _ritaInternalSubnets = new(Rita.RitaOptions.DefaultInternalSubnets);
    private bool _ritaOnlineThreatIntel;
    /// <summary>Ingest: run RITA after Zeek (only when RITA is installed; one analysis per host folder, _Case for captures with no identified host).</summary>
    public bool RitaEnabled { get => _ritaEnabled; set => Set(ref _ritaEnabled, value); }
    /// <summary>RITA <c>filtering.internal_subnets</c> (CIDRs). Default RFC 1918 + fd00::/8.</summary>
    public List<string> RitaInternalSubnets { get => _ritaInternalSubnets; set => Set(ref _ritaInternalSubnets, value ?? new List<string>(Rita.RitaOptions.DefaultInternalSubnets)); }
    /// <summary>RITA online threat intel feeds (abuse.ch Feodo Tracker). Off by default: analysis stays offline.</summary>
    public bool RitaOnlineThreatIntel { get => _ritaOnlineThreatIntel; set => Set(ref _ritaOnlineThreatIntel, value); }

    private List<Triage.IngestSelection> _ingestSelections = Triage.IngestSelections.Normalize(null);
    /// <summary>Ingest "Custom selection" sets (Preferences > Ingest selections). Always starts with the built-in Default (seeded when missing).</summary>
    public List<Triage.IngestSelection> IngestSelections { get => _ingestSelections; set => Set(ref _ingestSelections, Triage.IngestSelections.Normalize(value)); }

    private string _examinerName = "";
    /// <summary>Name recorded with edits and approvals on the Review page; empty = the Windows user name.</summary>
    public string ExaminerName { get => _examinerName; set => Set(ref _examinerName, value?.Trim() ?? ""); }

    private string _aiProvider = "ollama";
    private string _aiModel = "";
    private string _aiBaseUrl = "";
    /// <summary>Assistant model provider: "ollama" (local, default), "anthropic", "openai" or "openrouter". The API key is not here (see ApiKeyStore).</summary>
    public string AiProvider { get => _aiProvider; set => Set(ref _aiProvider, string.IsNullOrWhiteSpace(value) ? "ollama" : value.Trim().ToLowerInvariant()); }
    /// <summary>Selected model id; empty = not chosen yet.</summary>
    public string AiModel { get => _aiModel; set => Set(ref _aiModel, value?.Trim() ?? ""); }
    private Dictionary<string, string> _aiModelByProvider = new();
    /// <summary>Last model chosen per provider, so switching providers back restores it (the current provider's lives in <see cref="AiModel"/>).</summary>
    public Dictionary<string, string> AiModelByProvider { get => _aiModelByProvider; set => Set(ref _aiModelByProvider, value ?? new()); }
    /// <summary>Base URL override; empty = the provider default.</summary>
    public string AiBaseUrl { get => _aiBaseUrl; set => Set(ref _aiBaseUrl, value?.Trim() ?? ""); }

    // Old files may still carry assistantOpenAnalyze / assistantOpenExplore; they are ignored (Spectre is one app-wide chat now).
    private string _assistantMode = "";
    private bool _assistantExpanded;
    private double _assistantPopupWidth = 380, _assistantPopupHeight = 560;
    /// <summary>Where Spectre shows when open: "popup" (default), "docked" or "window". A file without the setting follows the old
    /// <see cref="AssistantFloating"/> flag (false = docked).</summary>
    public string AssistantMode
    {
        get => _assistantMode is "popup" or "docked" or "window" ? _assistantMode : _assistantFloating ? "popup" : "docked";
        set => Set(ref _assistantMode, value is "popup" or "docked" or "window" ? value : "popup");
    }
    /// <summary>Spectre is open (true) or collapsed to the launcher icon (false).</summary>
    public bool AssistantExpanded { get => _assistantExpanded; set => Set(ref _assistantExpanded, value); }
    public double AssistantPopupWidth { get => _assistantPopupWidth; set => Set(ref _assistantPopupWidth, Math.Max(300, value)); }
    public double AssistantPopupHeight { get => _assistantPopupHeight; set => Set(ref _assistantPopupHeight, Math.Max(300, value)); }

    private bool _assistantAutoMode;
    private bool _assistantFloating = true;
    private double _assistantDockWidth = 380, _assistantWindowLeft, _assistantWindowTop, _assistantWindowWidth, _assistantWindowHeight;
    /// <summary>Legacy: false = docked. Superseded by <see cref="AssistantMode"/>.</summary>
    public bool AssistantFloating { get => _assistantFloating; set => Set(ref _assistantFloating, value); }
    /// <summary>Width of the docked Spectre panel (dragged with the splitter); at least 300.</summary>
    public double AssistantDockWidth { get => _assistantDockWidth; set => Set(ref _assistantDockWidth, Math.Max(300, value)); }
    /// <summary>Width of the docked Findings evidence panel (dragged with the splitter); 0 = never set (45% of the page).</summary>
    public double EvidencePanelWidth { get => _evidencePanelWidth; set => Set(ref _evidencePanelWidth, value <= 0 ? 0 : Math.Max(360, value)); }
    private double _evidencePanelWidth;
    /// <summary>Floating Spectre window position and size; width 0 = never placed (the app picks a default).</summary>
    public double AssistantWindowLeft { get => _assistantWindowLeft; set => Set(ref _assistantWindowLeft, value); }
    public double AssistantWindowTop { get => _assistantWindowTop; set => Set(ref _assistantWindowTop, value); }
    public double AssistantWindowWidth { get => _assistantWindowWidth; set => Set(ref _assistantWindowWidth, value); }
    public double AssistantWindowHeight { get => _assistantWindowHeight; set => Set(ref _assistantWindowHeight, value); }
    /// <summary>Auto-mode: Spectre applies its filter, flag and open-file proposals itself (with Undo) instead of waiting for Apply. Off by default.</summary>
    public bool AssistantAutoMode { get => _assistantAutoMode; set => Set(ref _assistantAutoMode, value); }

    private bool _runSpectreAfterIngest = true;
    /// <summary>Queue a "Spectre analysis" job (hand-off + phases 1-5) when an Ingest run finishes (not after Stop). On by default.</summary>
    public bool RunSpectreAfterIngest { get => _runSpectreAfterIngest; set => Set(ref _runSpectreAfterIngest, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public JsonSettingsStore() : this(DefaultPath) { }
    public JsonSettingsStore(string path) { Path = path; }

    /// <summary><c>%LOCALAPPDATA%\EzForensicsData\settings.json</c>; <c>EZFORENSICS_SETTINGS</c> (or the old <c>EZHUB_SETTINGS</c>) overrides it.</summary>
    public static string DefaultPath => AppPaths.SettingsPath;

    public string Path { get; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
                if (EzForensics.Core.Results.FlagCatalog.RemoveRetiredSpectreEvidence(loaded))
                {
                    try { Save(loaded); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>Saved setup selection, so a setup interrupted by a restart continues where it stopped.</summary>
public sealed class SetupResumeState
{
    public List<string> Items { get; set; } = new();
    // "ZeekRuntime" (Wsl / Docker) was removed: setup installs Network in WSL only. Old settings files that still have it load
    // fine (System.Text.Json ignores unknown properties).
    public DateTime SavedUtc { get; set; }
    /// <summary>The interrupted setup is the first one (opened by itself at the first start): Cancel after the restart reverts it.</summary>
    public bool FirstSetup { get; set; }
    /// <summary>What the first setup installed before the restart (removed again when the resumed setup is cancelled).</summary>
    public List<Setup.RevertTarget> Installed { get; set; } = new();
}
